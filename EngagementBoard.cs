using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Per-target engagement state for the planner's live overview, and the snapshot builder
    /// (<see cref="CollectSalvos"/>) that the HUD renders.
    ///
    /// This consolidates what used to be five separate per-target dictionaries (fired-at, impact
    /// time, impact spread, wave count, wave gap) into ONE row per target, so the rows can no
    /// longer drift out of sync and there is exactly one prune path
    /// (<see cref="CollectSalvos"/> prunes fired targets that went idle past their grace window;
    /// never-fired rows of dropped orders are removed via <see cref="Drop"/>).
    /// </summary>
    internal static class EngagementBoard
    {
        private const float EngageGrace = 8f; // sim seconds a fired target stays listed after going idle

        /// <summary>One target's engagement state. Rows only ever exist for coordinated targets.</summary>
        private sealed class Engagement
        {
            public float FiredAtSim = -1f;  // GameTime.time of last release at this target; -1 = held only
            public float ImpactSim = -1f;   // scheduled (or anchor-tracked live) impact time; -1 = none
            public float ImpactSpread;      // ± arrival spread (s); independent salvos only, 0 for grouped
            public int Waves = 1;           // reload-separated waves
            public float WaveGap;           // sim seconds between successive wave impacts
        }

        private static readonly Dictionary<ObjectBase, Engagement> _byTarget =
            new Dictionary<ObjectBase, Engagement>();

        // ---- Snapshot scratch (reused every CollectSalvos call to avoid per-frame allocation) ----
        private static readonly Dictionary<ObjectBase, SalvoLine> _salvoMap =
            new Dictionary<ObjectBase, SalvoLine>();
        private static readonly List<ObjectBase> _pruneScratch = new List<ObjectBase>();

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

        /// <summary>Called when a batch is scheduled: sets the shared impact + arrival-shape figures.</summary>
        internal static void RecordScheduled(ObjectBase target, float impactSim, float impactSpread, int waves, float waveGap)
        {
            Engagement e = GetOrCreate(target);
            e.ImpactSim = impactSim;
            e.ImpactSpread = impactSpread;
            e.Waves = waves;
            e.WaveGap = waveGap;
        }

        /// <summary>Called by observation anchoring while the anchor ripple rewrites the shared impact.</summary>
        internal static void UpdateImpact(ObjectBase target, float impactSim)
            => GetOrCreate(target).ImpactSim = impactSim;

        /// <summary>Called when a held shot is actually released at the target.</summary>
        internal static void MarkFired(ObjectBase target)
            => GetOrCreate(target).FiredAtSim = GameTime.time;

        internal static bool HasFired(ObjectBase target)
            => _byTarget.TryGetValue(target, out Engagement e) && e.FiredAtSim >= 0f;

        /// <summary>
        /// The target's last predicted (anchor-finalized) impact time, for residual logging.
        /// Returns false if the target has no coordinated row or no impact was ever set.
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
        /// Removes a never-fired target's row (held order dropped before release). Fired targets
        /// are owned by the grace-window prune in <see cref="CollectSalvos"/> instead.
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
        /// Snapshot of what we're currently coordinating, grouped by target: shots still held for
        /// timing (<see cref="SalvoLine.Queued"/>) and friendly missiles already in flight at that
        /// target (<see cref="SalvoLine.InFlight"/>). Reuses <paramref name="outList"/> to avoid
        /// per-frame allocation. Also prunes fired targets that went idle past their grace window.
        /// </summary>
        internal static void CollectSalvos(List<SalvoLine> outList)
        {
            outList.Clear();
            _salvoMap.Clear();
            float now = GameTime.time;

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

            // In-flight rounds count only for targets we actually coordinated (fired) — otherwise
            // any friendly missile at that contact would inflate the overview.
            LaunchDiagnostics.ForEachInFlight((w, t) =>
            {
                if (!HasFired(t)) return;
                _salvoMap.TryGetValue(t, out SalvoLine ln);
                ln.Target = t;
                ln.InFlight += 1;
                _salvoMap[t] = ln;
            });

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
                }
                else
                {
                    ln.ImpactSim = -1f;
                    ln.ImpactSpread = 0f;
                    ln.Waves = 1;
                    ln.WaveGap = 0f;
                }
                outList.Add(ln);
            }

            // Prune fired targets that are idle and past their grace window (or gone). Never-fired
            // rows stay — they belong to held orders and are dropped via Drop() or Clear().
            _pruneScratch.Clear();
            foreach (KeyValuePair<ObjectBase, Engagement> kv in _byTarget)
            {
                ObjectBase t = kv.Key;
                Engagement e = kv.Value;
                if (e.FiredAtSim < 0f) continue;
                bool active = _salvoMap.TryGetValue(t, out SalvoLine ln) && (ln.Queued > 0 || ln.InFlight > 0);
                bool inGrace = (now - e.FiredAtSim) < EngageGrace;
                if (t == null || t.IsDestroyed || (!active && !inGrace))
                    _pruneScratch.Add(t);
            }
            for (int i = 0; i < _pruneScratch.Count; i++) _byTarget.Remove(_pruneScratch[i]);
        }
    }
}
