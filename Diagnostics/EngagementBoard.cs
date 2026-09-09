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
    /// One row per target (fired-at, impact time, spread, wave count, wave gap) so the fields
    /// cannot drift out of sync, with exactly one prune path: <see cref="CollectSalvos"/> prunes
    /// fired targets idle past their grace window, and <see cref="Drop"/> removes never-fired rows
    /// left by dropped orders.
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

        // Snapshot scratch (reused every CollectSalvos call to avoid per-frame allocation)
        private static readonly Dictionary<ObjectBase, SalvoLine> _salvoMap =
            new Dictionary<ObjectBase, SalvoLine>();
        private static readonly List<ObjectBase> _pruneScratch = new List<ObjectBase>();
        // Cached so CollectSalvos (called per frame from the HUD) doesn't allocate a closure.
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
            // In-flight rounds count only for targets we actually coordinated (fired) ; otherwise
            // any friendly missile at that contact would inflate the overview.
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

        /// <summary>Called when a batch is scheduled: sets the shared impact + arrival-shape figures.</summary>
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
        /// D10 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md: how many rows the board is holding,
        /// how many have fired, and the age of the oldest scheduled impact. The only prune path is
        /// CollectSalvos, which the HUD calls while DRAWING, so the census is how a run says whether
        /// pruning stopped when the panel was hidden. Read-only; it prunes nothing itself, which is
        /// the point.
        /// </summary>
        internal static void Census(float simNow, out int rows, out int fired, out float oldestAgeSim)
        {
            rows = _byTarget.Count;
            fired = 0;
            oldestAgeSim = 0f;
            foreach (KeyValuePair<ObjectBase, Engagement> kv in _byTarget)
            {
                Engagement e = kv.Value;
                if (e.FiredAtSim >= 0f)
                {
                    fired++;
                    float age = simNow - e.FiredAtSim;
                    if (age > oldestAgeSim) oldestAgeSim = age;
                }
            }
        }

        /// <summary>True if AutoTOT is coordinating this target (a row exists) ; i.e. a missile at
        /// it is one we fired, not an auto-fired defensive SAM. Scopes verbose per-missile
        /// diagnostics to our own shots.</summary>
        internal static bool IsCoordinated(ObjectBase target)
            => target != null && _byTarget.ContainsKey(target);

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
        /// per-frame allocation.
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

            // Pruning is NOT done here any more. It runs on the coordinator tick, so that hiding
            // the panel cannot stop it, and doing it here as well would only pay the cost twice.
        }

        /// <summary>
        /// Drop fired targets that are idle and past their grace window, or gone. Never-fired rows
        /// stay: they belong to held orders and are dropped via <see cref="Drop"/> or
        /// <see cref="Clear"/>.
        ///
        /// Called from the coordinator's tick, not from the draw. It used to run only inside
        /// <see cref="CollectSalvos"/>, which the HUD calls while painting, so hiding the panel or
        /// turning the indicator off stopped the cleanup entirely: measured on 2026-09-09, a fired
        /// row survived 188 s with nothing in flight and nothing scheduled, against an 8 s grace,
        /// and only the mission reset cleared it. Finding 12 of
        /// docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
        ///
        /// Activity is recomputed here rather than read from the display scratch, for the same
        /// reason: the scratch is only filled when something draws.
        /// </summary>
        internal static void Prune(float now)
        {
            if (_byTarget.Count == 0) return;

            _pruneScratch.Clear();
            foreach (KeyValuePair<ObjectBase, Engagement> kv in _byTarget)
            {
                ObjectBase t = kv.Key;
                Engagement e = kv.Value;
                if (e.FiredAtSim < 0f) continue;
                // "Active" has to include an order that has fired but whose rounds have not left
                // the rail yet, or a slow launcher (a submarine climbing to launch depth) loses its
                // row mid-ascent and the round that follows is treated as one we never fired.
                bool active = IsStillWorking(t) || LaunchDiagnostics.HasPendingLaunch(t);
                bool inGrace = (now - e.FiredAtSim) < EngageGrace;
                if (t == null || t.IsDestroyed || (!active && !inGrace))
                    _pruneScratch.Add(t);
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
