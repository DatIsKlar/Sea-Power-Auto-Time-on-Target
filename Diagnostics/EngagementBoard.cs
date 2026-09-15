using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Per-target engagement state for the planner's live overview, and the snapshot builder
    /// (<see cref="CollectSalvos"/>) the HUD renders. One row per target, pruned by <see cref="Prune"/> (idle
    /// fired targets past their grace) and <see cref="Drop"/> (never-fired rows of dropped orders).
    /// </summary>
    internal static class EngagementBoard
    {
        private const float EngageGrace = 8f; // sim seconds a fired target stays listed after going idle

        /// <summary>One target's engagement state. Rows only ever exist for coordinated targets.</summary>
        private sealed class Engagement
        {
            public float FiredAtSim = -1f;  // sim time of last release at this target; -1 = held only
            public float ImpactSim = -1f;   // scheduled (or anchor-tracked live) impact time; -1 = none
            public float ImpactSpread;      // ± arrival spread (s); independent salvos only, 0 for grouped
            public int Waves = 1;           // reload-separated waves
            public float WaveGap;           // sim seconds between successive wave impacts
            public int StrikeId;            // rows sharing an id were scheduled as ONE strike; 0 = none
        }

        private static readonly Dictionary<ObjectBase, Engagement> _byTarget =
            new Dictionary<ObjectBase, Engagement>();

        // Snapshot scratch, reused every CollectSalvos call.
        private static readonly Dictionary<ObjectBase, SalvoLine> _salvoMap =
            new Dictionary<ObjectBase, SalvoLine>();
        private static readonly List<ObjectBase> _pruneScratch = new List<ObjectBase>();
        // Cached so the per-frame CollectSalvos does not allocate a closure.
        private static readonly System.Action<WeaponBase, ObjectBase> _countInFlight = CountInFlightAt;

        /// <summary>One row of the HUD's ENGAGEMENTS overview.</summary>
        internal struct SalvoLine
        {
            public ObjectBase Target;
            public int Queued;           // shots still held for timing
            public int InFlight;         // friendly missiles already in flight at this target
            public float ImpactSim;      // synced impact time (-1 if unknown)
            public float ImpactSpread;   // ± arrival spread (s)
            public int Waves;            // reload-separated waves (1 = single wave)
            public float WaveGap;        // sim seconds between successive wave impacts
            public int AnchorLaunched;   // observation anchoring: launches observed so far
            public int AnchorTotal;      // >0 while a batch anchor's ripple is being tracked
            public int StrikeId;         // rows sharing an id land on one coordinated impact; 0 = none
        }

        private static void CountInFlightAt(WeaponBase w, ObjectBase t)
        {
            // In-flight rounds count only for targets AutoTOT fired at; otherwise any friendly missile would count.
            if (!HasFired(t)) return;
            _salvoMap.TryGetValue(t, out SalvoLine ln);
            ln.Target = t;
            ln.InFlight += 1;
            _salvoMap[t] = ln;
        }

        private static Engagement GetOrCreate(ObjectBase target)
        {
            if (!_byTarget.TryGetValue(target, out Engagement e))
            {
                e = new Engagement();
                _byTarget[target] = e;
            }
            return e;
        }

        /// <summary>Called when a batch is scheduled: sets the shared impact and arrival-shape figures.</summary>
        internal static void RecordScheduled(ObjectBase target, float impactSim, float impactSpread,
                                             int waves, float waveGap, int strikeId)
        {
            Engagement e = GetOrCreate(target);
            e.ImpactSim = impactSim;
            e.ImpactSpread = impactSpread;
            e.Waves = waves;
            e.WaveGap = waveGap;
            e.StrikeId = strikeId;
        }

        /// <summary>Called by observation anchoring while the anchor ripple rewrites the shared impact.</summary>
        internal static void UpdateImpact(ObjectBase target, float impactSim)
            => GetOrCreate(target).ImpactSim = impactSim;

        /// <summary>Called when a held shot is actually released at the target.</summary>
        internal static void MarkFired(ObjectBase target)
            => GetOrCreate(target).FiredAtSim = GameClock.SimNow();

        internal static bool HasFired(ObjectBase target)
            => _byTarget.TryGetValue(target, out Engagement e) && e.FiredAtSim >= 0f;

        /// <summary>
        /// True if AutoTOT is coordinating this target (a row exists), so a missile at it is one AutoTOT
        /// fired rather than a defensive SAM. Scopes per-missile diagnostics to AutoTOT's shots.
        /// </summary>
        internal static bool IsCoordinated(ObjectBase target)
            => target != null && _byTarget.ContainsKey(target);

        /// <summary>
        /// The target's last predicted impact time, for residual logging; false if the target has no row or
        /// no impact was set.
        /// </summary>
        internal static bool TryGetPredictedImpact(ObjectBase target, out float impactSim)
        {
            impactSim = -1f;
            if (target == null || !_byTarget.TryGetValue(target, out Engagement e) || e.ImpactSim < 0f)
                return false;
            impactSim = e.ImpactSim;
            return true;
        }

        /// <summary>
        /// Remove a never-fired target's row, left by a held order dropped before release. Fired rows are
        /// left to <see cref="Prune"/>.
        /// </summary>
        internal static void Drop(ObjectBase target)
        {
            if (target != null && !HasFired(target)) _byTarget.Remove(target);
        }

        internal static void Clear()
        {
            _byTarget.Clear();
            _salvoMap.Clear();
            _pruneScratch.Clear();
        }

        /// <summary>
        /// Snapshot of what is being coordinated, by target: shots still held
        /// (<see cref="SalvoLine.Queued"/>) and friendly missiles in flight
        /// (<see cref="SalvoLine.InFlight"/>). Reuses <paramref name="outList"/>.
        /// </summary>
        internal static void CollectSalvos(List<SalvoLine> outList)
        {
            outList.Clear();
            _salvoMap.Clear();

            // Held (scheduled) shots and anchor-ripple progress.
            foreach (Coordinator.Scheduled s in Coordinator.ScheduledItems)
            {
                ObjectBase t = s.Item.Target;
                if (t == null || t.IsDestroyed) continue;
                _salvoMap.TryGetValue(t, out SalvoLine ln);
                ln.Target = t;
                if (!s.Fired) ln.Queued += Mathf.Max(1, s.Item.Shots);
                if (s.IsAnchor && !s.RippleDone)
                {
                    ln.AnchorLaunched = s.LaunchTimes.Count;
                    ln.AnchorTotal = Mathf.Max(1, s.AnchorShots);
                }
                _salvoMap[t] = ln;
            }

            LaunchDiagnostics.ForEachInFlight(_countInFlight);

            // Merge the board's impact/spread/wave figures into each line.
            foreach (KeyValuePair<ObjectBase, SalvoLine> kv in _salvoMap)
            {
                SalvoLine ln = kv.Value;
                if (_byTarget.TryGetValue(kv.Key, out Engagement e))
                {
                    ln.ImpactSim = e.ImpactSim;
                    ln.ImpactSpread = e.ImpactSpread;
                    ln.Waves = e.Waves;
                    ln.WaveGap = e.WaveGap;
                    ln.StrikeId = e.StrikeId;
                }
                else
                {
                    ln.ImpactSim = -1f;
                    ln.ImpactSpread = 0f;
                    ln.Waves = 1;
                    ln.WaveGap = 0f;
                    ln.StrikeId = 0;
                }
                outList.Add(ln);
            }

            // Pruning runs on the coordinator tick (Prune), so hiding the panel cannot stop it.
        }

        /// <summary>
        /// Drop every row whose target is gone, and fired rows idle past their grace window. Never-fired rows
        /// of live targets belong to held orders and go through <see cref="Drop"/> or <see cref="Clear"/>.
        /// Called from the coordinator tick rather than the draw, so a hidden panel still prunes; activity is
        /// recomputed rather than read from the display scratch for the same reason.
        /// </summary>
        internal static void Prune(float now)
        {
            if (_byTarget.Count == 0) return;

            _pruneScratch.Clear();
            foreach (KeyValuePair<ObjectBase, Engagement> kv in _byTarget)
            {
                ObjectBase t = kv.Key;
                Engagement e = kv.Value;
                // Before the never-fired filter: a dead target's held orders are dropped by the release loop,
                // and its row would otherwise outlive them.
                if (t == null || t.IsDestroyed) { _pruneScratch.Add(t); continue; }
                if (e.FiredAtSim < 0f) continue;
                // Active includes an order that fired but whose rounds have not left yet, or a slow launcher
                // (a rising submarine) would lose its row and its round would be treated as uncoordinated.
                bool active = IsStillWorking(t) || LaunchDiagnostics.HasPendingLaunch(t);
                bool inGrace = (now - e.FiredAtSim) < EngageGrace;
                if (!active && !inGrace) _pruneScratch.Add(t);
            }
            for (int i = 0; i < _pruneScratch.Count; i++) _byTarget.Remove(_pruneScratch[i]);
        }

        /// <summary>Held orders or friendly rounds still flying at this target.</summary>
        private static bool IsStillWorking(ObjectBase target)
        {
            foreach (Coordinator.Scheduled s in Coordinator.ScheduledItems)
                if (s.Item != null && ReferenceEquals(s.Item.Target, target)) return true;
            return LaunchDiagnostics.HasInFlightAt(target);
        }
    }
}
