using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Runs the integration loop on worker threads so a burst of refreshes does not land on the
    /// frame that asks for them. Setup on main thread, workers run the pure loop, only main thread
    /// writes the cache. Solve is deterministic over its input. See docs/ARCHITECTURE.md "Async solver design".
    /// </summary>
    internal static partial class FlightTime
    {
        private sealed class SolveRequest
        {
            internal TofKey Key;
            internal SolveInput Input;
            internal AmmunitionParameters Ap;
            // Held so a declined result can fall back on the main thread, where the fallback tiers
            // (which need ObjectBase) are legal to call.
            internal ObjectBase Unit;
            internal string AmmoId;
            internal ObjectBase Target;
            internal float Result;
            // D5 and D6 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md. CapturedSim is the sim time
            // the input snapshot was built on the main thread; a result's freshness currently starts
            // when it is PUBLISHED, which is a later and more flattering moment. Diagnostic only:
            // nothing gates on either field yet.
            internal float CapturedSim;
            internal float CapturedReal;
        }

        private static readonly ConcurrentQueue<SolveRequest> _pending = new ConcurrentQueue<SolveRequest>();
        private static readonly ConcurrentQueue<SolveRequest> _done = new ConcurrentQueue<SolveRequest>();
        // Main-thread only: stops a burst queueing the same key every frame while it is in flight.
        private static readonly HashSet<TofKey> _inFlight = new HashSet<TofKey>();
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static Thread[] _workers;

        /// <summary>
        /// Verification mode: solve every queued request a SECOND time on the main thread and compare.
        ///
        /// Solve is deterministic over its input, so the two answers must agree exactly. Any
        /// difference means the snapshot did not carry some value correctly, which is the only way
        /// this refactor can be wrong. Checking it in the mod rather than by comparing runs matters
        /// because usable accuracy samples are scarce: a mission's ships run out of missiles after
        /// one salvo, and most rounds are excluded as seeker switches, so a run yields one to three
        /// comparisons. This yields one per queued simulation, several hundred per mission.
        ///
        /// Doubles the simulation work while on, so it is a correctness run, never a timing run.
        /// </summary>
        internal static bool VerifySolve;
        internal static long VerifyChecked;
        internal static long VerifyMismatched;

        internal static int QueueDepth => _pending.Count;
        internal static int InFlight => _inFlight.Count;
        internal static long AsyncCompleted;
        internal static long AsyncDeclined;

        // D5. A worker that throws never enqueues its request, so its key stays in _inFlight and
        // every later RequestRefresh for that key reports "already queued" and keeps handing out the
        // previous value. These three say whether that can happen in play: a fault count that is not
        // a shutdown abort, and the age of the oldest key still in flight.
        internal static long WorkerFaults;
        internal static long WorkerAborts;
        private static readonly Dictionary<TofKey, float> _inFlightSince = new Dictionary<TofKey, float>();
        // Written by DrainCompleted, read by the census line. Real seconds, main thread only.
        internal static float LastPublishAgeReal;
        internal static float MaxPublishAgeReal;

        /// <summary>Worker count. 0 disables the pipeline and everything runs synchronously.</summary>
        internal static int WorkerCount { get; private set; }

        internal static void StartWorkers(int count)
        {
            if (_workers != null) return;
            if (count <= 0)
            {
                // Say so explicitly. Silence here would be indistinguishable from the pool having
                // started, and the whole point of a run is knowing which mode produced the numbers.
                Bootstrap.Log.LogInfo(
                    "[AutoTOT] estimator: 0 solve workers, running SYNCHRONOUSLY on the main thread " +
                    "(EstimatorThreads = 0)");
                return;
            }
            WorkerCount = count;
            _workers = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                var th = new Thread(WorkerLoop)
                {
                    // Background so a stuck worker can never keep the game process alive, and below
                    // normal so the render and job threads always win a contended core. On the weak
                    // CPUs this exists for, losing the race is the correct outcome: the estimate
                    // arrives a frame later instead of stealing a frame.
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                    Name = "AutoTOT.Solve" + i,
                };
                _workers[i] = th;
                th.Start();
            }
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] estimator: ASYNC, {count} solve worker(s) started " +
                $"({SystemInfo.processorCount} logical cores)");
        }

        // Runs until the process exits. The threads are IsBackground, so that is the only shutdown
        // there has ever been: nothing calls for a pool stop, and stopping one mid-mission would
        // strand queued work (see the note on EstimatorThreads in Bootstrap.LoadConfig).
        private static void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    _signal.Wait();
                    while (_pending.TryDequeue(out SolveRequest r))
                    {
                        IntegratedPhases ph = default;
                        // Setup was timed on the main thread when the input was built. This stamps
                        // the loop start on THIS thread, so LoopDone measures the worker's own work.
                        ModelStats.LoopStarting();
                        r.Result = Solve(in r.Input, r.Ap, ref ph);
                        _done.Enqueue(r);
                    }
                }
                catch (ThreadAbortException)
                {
                    // Process shutdown. Counted rather than logged: the six of these in the tester
                    // logs are all immediately after the final "coordinator state reset", and a key
                    // poisoned at shutdown costs nothing. Kept separate from WorkerFaults so a real
                    // mid-mission fault cannot hide among them. D5.
                    Interlocked.Increment(ref WorkerAborts);
                }
                catch (Exception e)
                {
                    // A worker must never die: the queue would fill and every refresh would stall.
                    // The one log call in the mod that runs off the main thread; see ModLog's note.
                    Interlocked.Increment(ref WorkerFaults);
                    ModLog.Warn("solve worker", e);
                }
            }
        }

        /// <summary>
        /// Queue a refresh for a value that already has a usable previous estimate. Returns false if
        /// the work could not be queued, in which case the caller keeps using its cached value.
        /// Main thread only.
        /// </summary>
        internal static bool RequestRefresh(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (_workers == null) return false;
            if (!TryKey(unit, ammoId, target, out AmmunitionParameters ap, out TofKey key)) return false;
            if (_inFlight.Contains(key)) return true;   // already queued; not a failure

            if (!TryBuildSolveInput(unit, ap, target, out SolveInput input, out _, emitDiag: false))
                return false;                            // integrator declines; caller goes synchronous
            ModelStats.SetupDone();                      // setup ran here, on the main thread

            _inFlight.Add(key);
            _inFlightSince[key] = Time.realtimeSinceStartup;
            _pending.Enqueue(new SolveRequest
            {
                Key = key, Input = input, Ap = ap,
                Unit = unit, AmmoId = ammoId, Target = target,
                CapturedSim = GameClock.SimNow(),
                CapturedReal = Time.realtimeSinceStartup,
            });
            _signal.Release();
            return true;
        }

        /// <summary>
        /// Publish finished results into the cache. Main thread only, called once at the top of the
        /// tick, so the cache keeps a single writer.
        /// </summary>
        internal static void DrainCompleted()
        {
            while (_done.TryDequeue(out SolveRequest r))
            {
                // Not in flight means a mission reset dropped this request while a worker was still
                // running it. Its ids belong to the previous mission, so the answer is discarded.
                if (!_inFlight.Remove(r.Key)) continue;
                _inFlightSince.Remove(r.Key);
                AsyncCompleted++;
                // D6: how old the geometry behind this answer is by the time anything can read it.
                LastPublishAgeReal = Time.realtimeSinceStartup - r.CapturedReal;
                if (LastPublishAgeReal > MaxPublishAgeReal) MaxPublishAgeReal = LastPublishAgeReal;

                if (VerifySolve) VerifyAgainstMainThread(r);

                // Same plausibility bound the synchronous path applies. Without it a worker can
                // publish an impossible flight time under a live key, and every caller for the next
                // half second is handed it as fact.
                bool implausible = r.Unit != null && !r.Unit.IsDestroyed &&
                                   r.Target != null && !r.Target.IsDestroyed &&
                                   IsImplausible(r.Result, r.Unit, r.Ap, r.Target);
                if (r.Result > MinValidSeconds && !implausible)
                {
                    _cache.Set(r.Key, r.Result);
                    ModelStats.TierUsed(ModelStats.Tier.Integrator);
                    continue;
                }
                if (implausible)
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] estimate-rejected {r.AmmoId} (worker): {r.Result:0.00}s against " +
                        $"a straight-line floor of " +
                        $"{StraightLineFloorSeconds(r.Unit, r.Ap, r.Target):0.0}s. Not cached.");

                // The loop declined. The remaining tiers need ObjectBase, so they can only run here.
                // Rare on the beta branch, where the integrator answers every call.
                AsyncDeclined++;
                ModelStats.Stalled();
                if (r.Unit == null || r.Unit.IsDestroyed || r.Target == null || r.Target.IsDestroyed)
                    continue;
                float v = KinematicRaw(r.Unit, r.Ap, r.Target);
                _cache.Set(r.Key, v);
            }
        }

        /// <summary>
        /// Drop everything queued or in flight. Called on mission end: the object ids a request
        /// carries belong to the finished mission, and Unity reuses instance ids, so a result landing
        /// after a restart could publish one mission's flight time under another mission's key.
        /// Results still in workers are discarded on arrival because their keys are no longer in
        /// flight. Main thread only.
        /// </summary>
        internal static void ResetQueues()
        {
            while (_pending.TryDequeue(out _)) { }
            while (_done.TryDequeue(out _)) { }
            _inFlight.Clear();
            _inFlightSince.Clear();
            LastPublishAgeReal = 0f;
            MaxPublishAgeReal = 0f;
            AsyncCompleted = 0;
            AsyncDeclined = 0;
            VerifyChecked = 0;
            VerifyMismatched = 0;
        }

        /// <summary>
        /// D5 and D6 of the beta-release audit, as one line on a sim-time cadence.
        ///
        /// The two questions it answers. Can a key be stranded in flight, which would leave every
        /// later caller on a stale value forever? `oldestInFlight` is the answer: it should stay
        /// within a tick or two of the solve time and fall back to 0. And how old is the geometry
        /// behind a published async result by the time anything reads it? `publishAge` is that,
        /// measured from the main-thread capture rather than from the publish.
        ///
        /// Main thread only. Silent when the pool is off or nothing has run.
        /// </summary>
        internal static void LogAsyncCensus()
        {
            if (_workers == null) return;
            if (AsyncCompleted == 0 && _inFlight.Count == 0 && WorkerFaults == 0) return;

            float now = Time.realtimeSinceStartup;
            float oldest = 0f;
            foreach (KeyValuePair<TofKey, float> kv in _inFlightSince)
            {
                float age = now - kv.Value;
                if (age > oldest) oldest = age;
            }

            string faults = WorkerFaults > 0
                ? $", workerFaults {WorkerFaults} (NOT shutdown aborts, a key may be stranded)"
                : ", workerFaults 0";
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] estimator-census: inFlight {_inFlight.Count}, queued {_pending.Count}, " +
                $"completed {AsyncCompleted}, declined {AsyncDeclined}, oldestInFlight {oldest:0.00}s, " +
                $"publishAge last {LastPublishAgeReal:0.00}s max {MaxPublishAgeReal:0.00}s, " +
                $"aborts {WorkerAborts}{faults}. D5 and D6 of the beta-release audit.");
        }

        /// <summary>
        /// Re-solve a completed request here on the main thread and compare with what the worker
        /// returned. Exact equality is required: the same input through the same pure code must give
        /// the same float, so this is not a tolerance check.
        /// </summary>
        private static void VerifyAgainstMainThread(SolveRequest r)
        {
            IntegratedPhases ph = default;
            float mine = Solve(in r.Input, r.Ap, ref ph);
            VerifyChecked++;
            if (mine.Equals(r.Result)) return;   // bitwise, so NaN == NaN counts as agreement
            VerifyMismatched++;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] solve-verify MISMATCH {r.AmmoId}: worker {r.Result:F4}s, " +
                $"main {mine:F4}s, delta {(r.Result - mine):F4}s. The snapshot is not carrying " +
                $"every value the loop reads.");
        }

        /// <summary>
        /// Cache-only read, for callers that must not block. Main thread only.
        ///
        /// <para>A cached DECLINE must look like a miss. <see cref="Kinematic"/> caches the tier
        /// chain's answer whatever it is, including the -1 that means "the model declined", and the
        /// async decline path caches its re-run the same way. <see cref="Estimate"/> intercepts
        /// those and substitutes the straight-line tier; this method cannot, because it must not
        /// run a tier. Returning the -1 raw would hand it to callers that treat the result as a
        /// flight time: the anchor scan would compute a negative <c>needed</c> and silently never
        /// let that item win the anchor, and the release gate would put -1 into
        /// <c>LastFlightEst</c> and release the shot on this tick. Reporting a miss instead makes
        /// both callers fall through to <see cref="Estimate"/>, which resolves the decline
        /// properly. The next tick does not recover on its own: -1 fails the
        /// <c>LastFlightEst &gt;= 0</c> refresh branch, so the release path re-solves rather than
        /// reusing the bad value.</para>
        /// </summary>
        internal static bool TryCached(ObjectBase unit, string ammoId, ObjectBase target, out float value)
        {
            value = 0f;
            if (!TryKey(unit, ammoId, target, out _, out TofKey key)) return false;
            if (!_cache.TryGet(key, out float cached) || cached <= MinValidSeconds) return false;
            value = cached;
            return true;
        }
    }
}
