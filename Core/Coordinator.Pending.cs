using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Coordinator (partial) ; the deferred commit. A commit's checks, shot counts and launcher
    /// facts are settled on the frame the player fires, but its flight estimates are not: each
    /// order needs its own solve, and a strike of several hundred orders needs several hundred of
    /// them at roughly 2.5 ms each on a fast CPU. Running those on the frame that asks for them
    /// froze a 522-order strike for 2.6 s (docs/plans/open/large-strike-commit-freeze.md).
    ///
    /// So the commit is split in two. The frame the player fires does the main-thread work only:
    /// revalidation, the guidance clamp, PrepareIntent, and one queue request per order. The orders
    /// then wait here until their estimates arrive, at which point anchor selection runs with no
    /// solving left to do and the strike is scheduled exactly as before.
    ///
    /// Nothing launches at commit, so the wait costs the player nothing: the shared impact time is
    /// computed from the clock at COMPLETION, and the release gate re-estimates continuously
    /// afterwards. What it buys is a game that keeps running while a large strike is prepared.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// Main-thread solving allowed per frame, in milliseconds, for orders the worker pool could
        /// not take. Deliberately small: this path exists so a commit still completes with the pool
        /// off or declining, not so it can race the pool. At least one solve always runs, so a
        /// single solve costing more than the whole budget cannot stall a commit forever.
        /// </summary>
        private const double PendingSolveBudgetMs = 2.0;

        /// <summary>
        /// How long a commit may stay pending, in real seconds, before the rest of its estimates
        /// are solved on the spot. The point is that a commit ALWAYS completes: a stranded solve
        /// key or a pool that has stopped answering must not leave a strike waiting forever. On the
        /// normal path this never fires, and when it does it says so.
        /// </summary>
        private const float PendingDeadlineReal = 5f;

        /// <summary>
        /// One commit whose orders are final and whose flight estimates are still being solved.
        /// The item list is the same list the batch held, with the same <see cref="Intent"/>
        /// objects, so nothing is copied and the estimates land on the objects that will be
        /// scheduled.
        /// </summary>
        private sealed class PendingCommit
        {
            public List<Intent> Items;
            public bool Planner;    // planner fire: logs whatever the order count, always checks contention
            public bool IsStrike;   // a strike batch rather than an auto batch
            public float StartedReal;
            public float StartedSim;  // when the queued snapshots were taken, which is the age of
                                      // every estimate a worker answers with
            public int Frames;      // ticks spent pending, for the completion line
        }

        // Commits waiting on their estimates, oldest first. Normally empty, and never more than a
        // handful: a commit clears in a frame or two unless it holds hundreds of orders or the pool is off.
        private static readonly List<PendingCommit> _pending = new List<PendingCommit>();

        /// <summary>Orders held in commits that are waiting on their flight estimates.</summary>
        internal static int PendingOrders
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pending.Count; i++) n += _pending[i].Items.Count;
                return n;
            }
        }

        /// <summary>True while any commit is waiting on its estimates.</summary>
        internal static bool CommitPending => _pending.Count > 0;

        // Per-frame budget for the main-thread fallback solves, keyed on the frame so a commit
        // started in OnGUI and the tick's own pump share one allowance rather than two.
        private static int _pendingBudgetFrame = -1;
        private static double _pendingSpentMs;

        private static bool PendingBudgetLeft()
        {
            if (Time.frameCount != _pendingBudgetFrame)
            {
                _pendingBudgetFrame = Time.frameCount;
                _pendingSpentMs = 0d;
            }
            return _pendingSpentMs < PendingSolveBudgetMs;
        }

        /// <summary>
        /// Take a prepared, revalidated, clamped set of orders and start their estimates. The
        /// caller has already run <see cref="TryPrepareCommit"/> and <see cref="PrepareIntent"/>,
        /// so the only thing left before the strike can be scheduled is the flight estimates.
        ///
        /// Completes immediately when every estimate is already in hand, which is what happens with
        /// the solve pool off and a small order count: the budget below covers them on the firing
        /// frame itself and the strike is scheduled without a tick of delay.
        /// </summary>
        private static void BeginCommit(List<Intent> items, bool planner, bool isStrike)
        {
            if (items == null || items.Count == 0) return;

            var pc = new PendingCommit
            {
                Items = items,
                Planner = planner,
                IsStrike = isStrike,
                StartedReal = Time.unscaledTime,
                StartedSim = GameClock.SimNow(),
            };

            // Queue every order before reading any answer. A multi-target strike commits
            // shooters x targets distinct cache keys at once with nothing cached (the key is
            // {unit, ammo, target}, so cross-target reuse is zero by construction). Queueing the
            // whole set first lets the pool work on all of it in parallel; reading as we go would
            // serialise the list behind whatever the first order needs. Setup is main-thread work,
            // about 5 us per order, and it is the only per-order cost this frame pays.
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                it.CommitFlightEst = -1f;
                it.CommitEstSim = float.NegativeInfinity;
                it.CommitEstDone = false;
                if (FlightTime.RequestRefresh(it.Unit, it.AmmoId, it.Target))
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.FlightQueued);
            }

            // Added to the pending list only if it has to wait. Completing straight out of the
            // list would leave the entry behind, and the accounting reads that list: the commit's
            // rounds would then be counted twice, once pending and once scheduled, for the rest of
            // the mission.
            if (PumpPending(pc)) { CompletePending(pc); return; }
            _pending.Add(pc);
        }

        /// <summary>
        /// Advance every pending commit and schedule the ones whose estimates are all in. Called
        /// once per tick, after <see cref="FlightTime.DrainCompleted"/> has published what the
        /// workers finished, and before the release stage, so a commit that completes on this tick
        /// is released on it too.
        /// </summary>
        private static void PumpPendingCommits()
        {
            if (_pending.Count == 0) return;

            float nowReal = Time.unscaledTime;
            for (int i = 0; i < _pending.Count; )
            {
                PendingCommit pc = _pending[i];
                pc.Frames++;
                bool ready = PumpPending(pc);
                if (!ready && nowReal - pc.StartedReal >= PendingDeadlineReal)
                {
                    ForceResolvePending(pc, nowReal - pc.StartedReal);
                    ready = true;
                }
                if (!ready) { i++; continue; }
                // Out of the pending list BEFORE it is scheduled, so the accounting never sees the
                // same rounds in both places.
                _pending.RemoveAt(i);
                CompletePending(pc);
            }
        }

        /// <summary>
        /// Resolve what can be resolved for one pending commit without solving anything the worker
        /// pool is already working on. Returns true when every order has an answer.
        ///
        /// The three routes an order takes, in order of preference: the cache, which is where a
        /// finished worker result lands; waiting, when a worker still has it; and a solve here,
        /// when nothing else will answer (the pool is off, or it declined the request, or its
        /// result was superseded and dropped). Only the third costs the frame anything, and it is
        /// capped by <see cref="PendingSolveBudgetMs"/>.
        /// </summary>
        private static bool PumpPending(PendingCommit pc)
        {
            List<Intent> items = pc.Items;
            bool ready = true;
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it == null || it.CommitEstDone) continue;

                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float cached))
                {
                    CoordinatorProfiler.CountCachedHit();
                    it.CommitFlightEst = cached;
                    // The snapshot behind a worker's answer was taken when the request was queued,
                    // which is the commit's own moment, not this frame's. Dating it now would tell
                    // the release gate the estimate is fresher than it is, and at 10x compression a
                    // third of a real second is three sim seconds.
                    it.CommitEstSim = pc.StartedSim;
                    it.CommitEstDone = true;
                    continue;
                }
                if (FlightTime.IsQueued(it.Unit, it.AmmoId, it.Target)) { ready = false; continue; }
                if (!PendingBudgetLeft()) { ready = false; continue; }

                SolvePendingNow(it);
            }
            return ready;
        }

        /// <summary>
        /// Solve one pending order on this thread and charge the frame's budget for it. The answer
        /// is recorded even when the estimator declined and returned 0: the commit is waiting for
        /// an ANSWER, and "this shot cannot be modelled" is one. Anchor selection already handles a
        /// zero, by never letting that order win the anchor role and saying so if none of them can.
        /// </summary>
        private static void SolvePendingNow(Intent it)
        {
            long t0 = Stopwatch.GetTimestamp();
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
            float est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
            CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
            _pendingSpentMs += (Stopwatch.GetTimestamp() - t0) * CoordinatorProfiler.MsPerTick;

            it.CommitFlightEst = est;
            it.CommitEstSim = GameClock.SimNow();   // solved here, so its geometry is this frame's
            it.CommitEstDone = true;
        }

        /// <summary>
        /// Finish a commit that has waited past <see cref="PendingDeadlineReal"/> by solving
        /// everything it is still missing, budget or no budget. This is the freeze the deferred
        /// commit exists to avoid, so it is a warning, not a quiet fallback: it means the worker
        /// pool did not answer for five seconds, which is a condition worth seeing in the log.
        /// </summary>
        private static void ForceResolvePending(PendingCommit pc, float waitedReal)
        {
            int left = 0;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < pc.Items.Count; i++)
            {
                Intent it = pc.Items[i];
                if (it == null || it.CommitEstDone) continue;
                left++;
                SolvePendingNow(it);
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * CoordinatorProfiler.MsPerTick;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] commit-deadline: {left} of {pc.Items.Count} order(s) still had no flight " +
                $"estimate after {waitedReal:0.0}s, so they were solved on this frame ({ms:0} ms) to " +
                $"get the strike away. The solve workers are not keeping up or are not answering.");
        }

        /// <summary>
        /// Elect the anchor and schedule the strike. Every order arrives here with its estimate
        /// already resolved, so this does no simulation work at all: the cost is one pass over the
        /// orders plus <see cref="Schedule"/>.
        /// </summary>
        private static void CompletePending(PendingCommit pc)
        {
            List<Intent> items = pc.Items;
            float waitedMs = (Time.unscaledTime - pc.StartedReal) * 1000f;

            // Liveness only. NOT RevalidateCommit: its magazine pass measures each order against
            // OutstandingRounds, which counts pending orders, so re-running it here would trim this
            // commit against its own rounds and shrink it to nothing.
            DropDeadPendingOrders(items);
            if (items.Count == 0)
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] every order in this strike was dropped while its flight estimates " +
                    "were being prepared: no shooter and target pair survived. Nothing was fired.");
                return;
            }

            _targetScratch.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                ObjectBase t = items[i].Target;
                if (t != null && !_targetScratch.Contains(t)) _targetScratch.Add(t);
            }
            string where = _targetScratch.Count == 1
                ? UnitNaming.SafeName(_targetScratch[0])
                : $"{_targetScratch.Count} targets";

            // Before the anchor election: a pooled lead changes the enroute time of every order in
            // the pool, and PickAnchor, the release gate and the board all read it as ReleaseLead.
            PoolSharedLaunchClock(items);
            Intent anchor = PickAnchor(items, out float maxEnroute);
            Schedule(items, GameClock.SimNow() + maxEnroute, anchor);

            if (pc.Planner || VerboseLog || items.Count > 1)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] {(pc.Planner ? "planner firing" : "coordinating")} {items.Count} " +
                    $"missile order(s) on {where}: longest enroute {maxEnroute:0.0}s, anchor " +
                    $"{anchor?.AmmoId} -> {UnitNaming.SafeName(anchor?.Target)}, impacts synced. " +
                    $"Estimates prepared in {waitedMs:0} ms over {pc.Frames} tick(s).");

            // Unchanged from the immediate commit: the auto path only reports contention for a
            // strike, where the player chose the target spread; the planner path always does.
            if (pc.Planner || pc.IsStrike) WarnOnLauncherContention(items);
        }

        /// <summary>
        /// Drop orders whose shooter or target died while the commit was pending. The window is
        /// normally a frame or two, but it is real time, and at high compression a few frames are
        /// several sim seconds. The release loop would drop these anyway; doing it here keeps them
        /// out of anchor selection, where a dead shooter's estimate could still win the anchor role
        /// and take the whole strike's timing with it.
        /// </summary>
        private static void DropDeadPendingOrders(List<Intent> items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                Intent it = items[i];
                bool gone = it == null || it.Unit == null || it.Unit.IsDestroyed
                                       || it.Target == null || it.Target.IsDestroyed;
                if (!gone) continue;
                items.RemoveAt(i);
                if (it == null) continue;
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order dropped before scheduling: {it.AmmoId} from " +
                    $"{UnitNaming.SafeName(it.Unit)} -> {UnitNaming.SafeName(it.Target)}, " +
                    $"{(it.Unit == null || it.Unit.IsDestroyed ? "the shooter" : "the target")} " +
                    $"went while the strike's flight estimates were being prepared. The rest of " +
                    $"the strike is unaffected.");
            }
        }

        /// <summary>
        /// Rounds a pending commit has spoken for on this ship, counted across every ammunition
        /// that shares <paramref name="ammoId"/>'s guiding sensor. Feeds the guidance-channel
        /// budget: a pending order is committed and unfired in every sense that matters to the
        /// channel count, and leaving it out let a second commit hand the same channels out twice.
        /// </summary>
        private static int PendingGuidedRounds(ObjectBase unit, string ammoId)
        {
            if (_pending.Count == 0) return 0;
            int n = 0;
            for (int p = 0; p < _pending.Count; p++)
            {
                List<Intent> items = _pending[p].Items;
                for (int i = 0; i < items.Count; i++)
                {
                    Intent it = items[i];
                    if (it == null || it.Unit != unit) continue;
                    if (LauncherFactsSource.SharesGuidanceSensor(unit, it.AmmoId, ammoId))
                        n += Mathf.Max(1, it.Shots);
                }
            }
            return n;
        }

        /// <summary>
        /// Rounds of exactly this ammunition a pending commit has spoken for on this ship. Feeds
        /// the magazine accounting, which matches the ammunition exactly because a magazine is not
        /// shared the way a guidance channel is.
        /// </summary>
        private static int PendingAmmoRounds(ObjectBase unit, string ammoId)
        {
            if (_pending.Count == 0) return 0;
            int n = 0;
            for (int p = 0; p < _pending.Count; p++)
            {
                List<Intent> items = _pending[p].Items;
                for (int i = 0; i < items.Count; i++)
                {
                    Intent it = items[i];
                    if (it == null || it.Unit != unit) continue;
                    if (string.Equals(it.AmmoId, ammoId, System.StringComparison.Ordinal))
                        n += Mathf.Max(1, it.Shots);
                }
            }
            return n;
        }

        /// <summary>Drop every pending commit. Mission reset only; the orders are never issued.</summary>
        private static void ResetPending()
        {
            if (_pending.Count == 0) return;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] {PendingOrders} order(s) awaiting flight estimates were discarded by " +
                $"the coordinator reset.");
            _pending.Clear();
            _pendingBudgetFrame = -1;
            _pendingSpentMs = 0d;
        }
    }
}
