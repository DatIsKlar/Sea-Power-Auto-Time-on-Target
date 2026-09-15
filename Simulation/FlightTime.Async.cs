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
            // CapturedSim is the sim time
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

        /// <summary>
        /// Real seconds a key may sit in flight before <see cref="EvictIfStranded"/> takes it back.
        /// A solve is worst-case tens of milliseconds and the pool has never shown a backlog
        /// (`oldestInFlight 0.00s` across 26 runs), so this is three orders of magnitude above
        /// normal: it can only fire on a worker that is gone, never on one that is merely slow.
        /// </summary>
        private const float StrandedKeyTimeoutReal = 10f;

        /// <summary>Keys taken back from a worker that never returned. Reported by the census.</summary>
        internal static int AsyncStranded;

        // Sim time of the last SYNCHRONOUS cache write for a key that was in flight at the time. A
        // worker result captured at an EARLIER sim time describes older geometry than the value
        // already cached, and publishing it would overwrite the newer answer (chain-audit A1).
        // Sim time, not real time: within one frame, or while paused, the game has not moved, so
        // the worker's answer equals the synchronous one and publishing it only refreshes the
        // entry. Comparing real time discarded those too, and after a commit longer than the cache
        // TTL that left every order to be re-solved on the main thread on the next tick.
        // Recorded only while the key is in flight, so it holds at most the in-flight set. Main
        // thread only.
        private static readonly Dictionary<TofKey, float> _syncWrittenAt = new Dictionary<TofKey, float>();

        /// <summary>Worker results dropped because a synchronous solve had already answered the key.</summary>
        internal static long AsyncSuperseded;

        /// <summary>Called by the synchronous path after it writes the cache. Main thread only.</summary>
        private static void NoteSyncWrite(TofKey key)
        {
            if (_inFlight.Contains(key)) _syncWrittenAt[key] = GameClock.SimNow();
        }
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
            // A stranded key is taken back and reported as a FAILED refresh, so the caller runs
            // its synchronous fallback this tick and recovers immediately rather than waiting for
            // the re-queued solve. Later ticks go back to the pool as usual.
            if (_inFlight.Contains(key))
            {
                if (!EvictIfStranded(key)) return true;   // already queued; not a failure
                return false;                            // evicted: solve it here, now
            }

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
        /// A key whose worker never came back would otherwise stay in flight forever, and every
        /// later <see cref="RequestRefresh"/> for it returns "already queued" without queueing
        /// anything. The caller then reads its last estimate until the 0.5 s cache expires and has
        /// no way to recover, because its synchronous fallback runs only when the request FAILED.
        ///
        /// The way in is a worker exception thrown mid-dequeue: the catch in
        /// <see cref="WorkerLoop"/> is per batch, so requests already taken off the queue are never
        /// enqueued to `_done` and their keys are never removed.
        ///
        /// Evicting the key is enough on its own. The stranded request is not cancelled, and if its
        /// worker does eventually finish, <see cref="DrainCompleted"/> discards the result because
        /// the key is no longer in flight, which is the same path a mission reset already takes.
        ///
        /// Real time, not sim time: this is watching for a thread that stopped, and a paused or
        /// compressed game must not change how long that takes to notice. Main thread only.
        /// </summary>
        private static bool EvictIfStranded(TofKey key)
        {
            if (!_inFlightSince.TryGetValue(key, out float since)) return false;
            float age = Time.realtimeSinceStartup - since;
            if (age < StrandedKeyTimeoutReal) return false;

            _inFlight.Remove(key);
            _inFlightSince.Remove(key);
            AsyncStranded++;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] estimator: a solve key had been in flight {age:0.0}s with no result, " +
                $"so it was evicted and will be re-queued. " +
                $"workerFaults {WorkerFaults}, aborts {WorkerAborts}, stranded so far {AsyncStranded}.");
            return true;
        }

        /// <summary>
        /// True while a worker is already solving this key. A caller that must not block uses it to
        /// decide between waiting one more tick and solving the key itself: without it, a deferred
        /// commit would solve on the main thread every order whose worker had not finished
        /// yet, which is the cost it exists to avoid. Main thread only.
        /// </summary>
        internal static bool IsQueued(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (_workers == null) return false;
            if (!TryKey(unit, ammoId, target, out _, out TofKey key)) return false;
            if (!_inFlight.Contains(key)) return false;
            // Same eviction as RequestRefresh, for the same reason. Without it a deferred commit
            // waiting on a stranded key would wait for the whole mission, and this is the only
            // reader that can be asked about a key the release path has stopped refreshing.
            return !EvictIfStranded(key);
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

                // A synchronous solve answered this key after the game had moved on from the
                // moment this request was captured, so the cache holds fresher geometry than this.
                // Checked by stamp rather than by dropping the key from _inFlight at the
                // synchronous write: that would let the key be queued again while this request was
                // still running, and the older of the two results would then be the one published.
                bool superseded = _syncWrittenAt.TryGetValue(r.Key, out float syncAt) &&
                                  syncAt > r.CapturedSim;
                _syncWrittenAt.Remove(r.Key);
                if (superseded) { AsyncSuperseded++; continue; }

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

                // The loop declined, as it does for a shot it cannot close (a SAM planned beyond its
                // reach, for one; the Profiling tiers line counts them). The remaining tiers need
                // ObjectBase, so they can only run here.
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
            _syncWrittenAt.Clear();
            AsyncSuperseded = 0;
            AsyncStranded = 0;
            LastPublishAgeReal = 0f;
            MaxPublishAgeReal = 0f;
            AsyncCompleted = 0;
            AsyncDeclined = 0;
            VerifyChecked = 0;
            VerifyMismatched = 0;
        }

        /// <summary>
        /// Worker pool health and result freshness, as one line on a sim-time cadence.
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
                $"completed {AsyncCompleted}, declined {AsyncDeclined}, superseded {AsyncSuperseded}, " +
                $"stranded {AsyncStranded}, " +
                $"oldestInFlight {oldest:0.00}s, " +
                $"publishAge last {LastPublishAgeReal:0.00}s max {MaxPublishAgeReal:0.00}s, " +
                $"aborts {WorkerAborts}{faults}.");
        }

        /// <summary>
        /// Re-solve a completed request here on the main thread and compare with what the worker
        /// returned. Exact equality is required: the same input through the same pure code must give
        /// the same float, so this is not a tolerance check.
        /// </summary>
        private static void VerifyAgainstMainThread(SolveRequest r)
        {
            IntegratedPhases ph = default;
            ModelStats.LoopStarting();   // see DumpInputFor: Solve times against this stamp
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
            => TryCached(unit, ammoId, target, out value, out _);

        /// <summary>
        /// As above, and also how old the value is in REAL seconds. The cache is
        /// shared, so a hit can be an entry another caller stored up to the whole 0.5 s TTL ago,
        /// and a caller that stamps its own staleness bookkeeping from the hit records geometry of
        /// arbitrary age as computed this instant. At the game's 20x compression clamp that is up
        /// to 10 sim seconds of movement recorded as none.
        /// </summary>
        internal static bool TryCached(ObjectBase unit, string ammoId, ObjectBase target,
                                       out float value, out float ageReal)
        {
            value = 0f; ageReal = 0f;
            if (!TryKey(unit, ammoId, target, out _, out TofKey key)) return false;
            if (!_cache.TryGet(key, out float cached, out float age) || cached <= MinValidSeconds)
                return false;
            value = cached;
            ageReal = age;
            return true;
        }
    }
}
