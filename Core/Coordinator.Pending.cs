using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Coordinator (partial): the deferred commit. The firing frame does only the main-thread work
    /// (revalidation, the guidance clamp, PrepareIntent, one queue request per order). The flight
    /// estimates, several milliseconds each, are collected over the following frames; then the anchor is
    /// elected and the strike scheduled. Nothing launches at commit and the shared impact is computed when
    /// the commit completes, so the wait costs nothing.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// Main-thread solving per frame (ms) for orders the worker pool could not take. At least one solve
        /// always runs, so a commit completes even when one solve exceeds the budget.
        /// </summary>
        private const double PendingSolveBudgetMs = 2.0;

        /// <summary>
        /// Real seconds a commit may stay pending before its missing estimates are solved on the spot, so a
        /// stalled pool cannot leave a strike waiting. Logged when it fires.
        /// </summary>
        private const float PendingDeadlineReal = 5f;

        /// <summary>
        /// A commit whose orders are final and whose flight estimates are still being solved. It holds the
        /// batch's own <see cref="Intent"/> objects, so the estimates land on what will be scheduled.
        /// </summary>
        private sealed class PendingCommit
        {
            public List<Intent> Items;
            public bool Planner;    // planner fire: logs whatever the order count, always checks contention
            public bool IsStrike;   // a strike batch rather than an auto batch
            public float StartedReal;
            // When the queued snapshots were taken, which is the age of every estimate a worker answers with.
            public float StartedSim;
            public int Frames;      // ticks spent pending, for the completion line
        }

        // Commits waiting on their estimates, oldest first; normally empty.
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

        // Per-frame fallback budget, keyed on the frame so OnGUI and the tick share one allowance.
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
        /// Start the estimates for orders already prepared by <see cref="TryPrepareCommit"/> and
        /// <see cref="PrepareIntent"/>. Completes at once when every estimate is already in hand.
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

            // Queue every order before reading any answer, so the pool works on the whole set in parallel; a
            // strike's {unit, ammo, target} keys are all distinct and uncached.
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                it.CommitFlightEst = -1f;
                it.CommitEstSim = float.NegativeInfinity;
                it.CommitEstDone = false;
                if (FlightTime.RequestRefresh(it.Unit, it.AmmoId, it.Target))
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.FlightQueued);
            }

            // Added to the pending list only if it has to wait: completing from the list would leave the
            // entry behind and count its rounds twice.
            if (PumpPending(pc)) { CompletePending(pc); return; }
            _pending.Add(pc);
        }

        /// <summary>
        /// Advance every pending commit and schedule those whose estimates are all in. Runs once per tick,
        /// after <see cref="FlightTime.DrainCompleted"/> and before release.
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
                // Removed before scheduling, so the accounting never sees the rounds in both places.
                _pending.RemoveAt(i);
                CompletePending(pc);
            }
        }

        /// <summary>
        /// Resolve what one pending commit can without duplicating worker work: the cache, then waiting on a
        /// worker, then a budgeted solve here (<see cref="PendingSolveBudgetMs"/>). True when every order has
        /// an answer.
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
                    // A worker's snapshot was taken when the request was queued, so date the estimate to the
                    // commit, not to this frame.
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
        /// Solve one pending order here and charge the frame budget. A declined estimate (0) is recorded as
        /// an answer; anchor selection already handles it.
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
        /// Finish a commit past <see cref="PendingDeadlineReal"/> by solving everything missing, budget or
        /// not. Warned, since it means the worker pool stopped answering.
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

        /// <summary>Elect the anchor and schedule the strike; every estimate is already resolved.</summary>
        private static void CompletePending(PendingCommit pc)
        {
            List<Intent> items = pc.Items;
            float waitedMs = (Time.unscaledTime - pc.StartedReal) * 1000f;

            // Liveness only, not RevalidateCommit: its magazine pass counts pending orders, so it would trim
            // this commit against its own rounds.
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

            // Before the anchor election: a pooled lead changes the enroute time that PickAnchor, the release
            // gate and the board read.
            PoolSharedLaunchClock(items);
            Intent anchor = PickAnchor(items, out float maxEnroute);
            Schedule(items, GameClock.SimNow() + maxEnroute, anchor);

            if (pc.Planner || VerboseLog || items.Count > 1)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] {(pc.Planner ? "planner firing" : "coordinating")} {items.Count} " +
                    $"missile order(s) on {where}: longest enroute {maxEnroute:0.0}s, anchor " +
                    $"{anchor?.AmmoId} -> {UnitNaming.SafeName(anchor?.Target)}, impacts synced. " +
                    $"Estimates prepared in {waitedMs:0} ms over {pc.Frames} tick(s).");

            // As the immediate commit did: contention is reported for strikes and planner fires, not AUTO batches.
            if (pc.Planner || pc.IsStrike) WarnOnLauncherContention(items);
        }

        /// <summary>
        /// Drop orders whose shooter or target died while pending, so a dead shooter's estimate cannot win
        /// the anchor role.
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
        /// Rounds pending commits have spoken for on this ship, across every ammunition sharing
        /// <paramref name="ammoId"/>'s guiding sensor. Feeds the guidance-channel budget.
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
        /// Rounds of exactly this ammunition pending commits have spoken for on this ship. Feeds the magazine
        /// accounting.
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
