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
                catch (Exception e)
                {
                    // A worker must never die: the queue would fill and every refresh would stall.
                    // The one log call in the mod that runs off the main thread; see ModLog's note.
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
            _pending.Enqueue(new SolveRequest
            {
                Key = key, Input = input, Ap = ap,
                Unit = unit, AmmoId = ammoId, Target = target,
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
                AsyncCompleted++;

                if (VerifySolve) VerifyAgainstMainThread(r);

                if (r.Result > MinValidSeconds)
                {
                    _cache.Set(r.Key, r.Result);
                    ModelStats.TierUsed(ModelStats.Tier.Integrator);
                    continue;
                }

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
            AsyncCompleted = 0;
            AsyncDeclined = 0;
            VerifyChecked = 0;
            VerifyMismatched = 0;
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

        /// <summary>Cache-only read, for callers that must not block. Main thread only.</summary>
        internal static bool TryCached(ObjectBase unit, string ammoId, ObjectBase target, out float value)
        {
            value = 0f;
            if (!TryKey(unit, ammoId, target, out _, out TofKey key)) return false;
            return _cache.TryGet(key, out value);
        }
    }
}
