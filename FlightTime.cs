using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Flight-time estimation from a shooter to a target.
    ///
    /// Primary path: the game's own kinematic shot simulator
    /// (<c>AmmunitionParameters.MaxRangePrecise</c> → <c>Missile.SimulateShotLinear</c>), which
    /// integrates boost, loft arc, drag, and velocity bleed — exact for any missile, stock or
    /// modded, with no per-type tuning. A straight-line max-speed estimate is used ONLY when the
    /// simulator declines (target out of range).
    ///
    /// All results are single-missile (lone) flight times. Grouped-missile forming behaviour is
    /// NOT modelled here — it is handled as release lead in <c>Coordinator.PrepareIntent</c> and
    /// as observation anchoring in <c>Coordinator.UpdateAnchorTracking</c>, because a group's
    /// convergent impact depends on the salvo size and the launcher's realized cadence, neither
    /// of which belongs in a per-(shooter, ammo, target) estimate.
    ///
    /// The kinematic sim is ~100+ integration steps and the planner UI asks for every weapon
    /// row's ETA on every OnGUI pass, so results are cached briefly (see <see cref="TtlCache"/>)
    /// per shooter/ammo/target — repeated calls collapse to one sim per refresh window.
    /// </summary>
    internal static class FlightTime
    {
        private const float CacheTtlSeconds = 0.5f;   // real seconds

        /// <summary>
        /// Below this an estimate counts as "unavailable" (kinematic sim declined / degenerate
        /// geometry). Callers treat such estimates as unknown, never as "arrives instantly".
        /// </summary>
        internal const float MinValidSeconds = 0.01f;

        /// <summary>Straight-line fallback guard: slower than this and the ammo can't fly.</summary>
        private const float MinSpeedMs = 0.1f;

        private struct TofKey : IEquatable<TofKey>
        {
            public int UnitId;
            public string AmmoFile;
            public int TargetId;

            public bool Equals(TofKey o) =>
                UnitId == o.UnitId && TargetId == o.TargetId && AmmoFile == o.AmmoFile;
            public override bool Equals(object obj) => obj is TofKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked { return ((UnitId * 397) ^ TargetId) * 397 ^ (AmmoFile?.GetHashCode() ?? 0); }
            }
        }

        private static readonly TtlCache<TofKey, float> _cache = new TtlCache<TofKey, float>(CacheTtlSeconds);

        private static bool _lastCallWasHit;
        internal static bool WasLastCallCacheHit => _lastCallWasHit;

        /// <summary>
        /// Best available flight-time estimate (seconds) from <paramref name="unit"/> to
        /// <paramref name="target"/> with <paramref name="ammoId"/>: kinematic sim if it
        /// answers, straight-line max-speed otherwise. Returns 0 when nothing can be estimated
        /// (null unit/target/ammo, or no kinematics and no speed), which callers must treat as
        /// "unknown" — never as "arrives instantly".
        /// </summary>
        internal static float Estimate(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (unit == null || target == null) return 0f;

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            AmmunitionParameters ap = ammo?._ap;
            if (ap == null) return 0f;

            float kinematic = Kinematic(unit, ap, target);
            if (kinematic > MinValidSeconds) return kinematic;

            _lastCallWasHit = false;
            // Fallback only if the simulator declined (out of range / no kinematics): straight
            // line at max speed, better than holding a launch forever.
            float speedMs = ap._maxVelocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= MinSpeedMs) return 0f;
            return GameUnits.MetersBetween(unit, target) / speedMs;
        }

        /// <summary>
        /// Flight time (s) from the game's own kinematic shot simulator, or -1 if unavailable
        /// (reflection miss, sim declined, or threw). Cached for
        /// <see cref="CacheTtlSeconds"/> real seconds per shooter/ammo/target.
        /// Sets <see cref="WasLastCallCacheHit"/> to indicate whether the result came from cache.
        /// </summary>
        internal static float Kinematic(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            TofKey key = new TofKey
            {
                UnitId = unit.GetInstanceID(),
                AmmoFile = ap._ammunitionFileName,
                TargetId = target.GetInstanceID(),
            };
            if (_cache.TryGet(key, out float hit))
            {
                _lastCallWasHit = true;
                return hit;
            }

            _lastCallWasHit = false;
            float value = KinematicRaw(unit, ap, target);
            _cache.Set(key, value);
            return value;
        }

        internal static void ClearCache() { _cache.Clear(); _profileCache.Clear(); }

        internal static long TofHits => _cache.HitCount;
        internal static long TofMisses => _cache.MissCount;
        internal static long ProfileHits => _profileCache.HitCount;
        internal static long ProfileMisses => _profileCache.MissCount;
        internal static int TofCacheSize => _cache.Count;
        internal static int ProfileCacheSize => _profileCache.Count;
        internal static long TofEvictionsTtl => _cache.EvictionsTtl;
        internal static long TofEvictionsCapacity => _cache.EvictionsCapacity;
        internal static long ProfileEvictionsTtl => _profileCache.EvictionsTtl;
        internal static long ProfileEvictionsCapacity => _profileCache.EvictionsCapacity;
        internal static void ResetStats() { _cache.ResetStats(); _profileCache.ResetStats(); }

        // ---- Grouped-salvo forming delay (group-drag correction), range-aware tau_form model ----
        //
        // A grouped salvo's leader throttles to 0.6x its stage speed (MissileGroup.AdjustMembersVelocities
        // -40% clamp) to let the ripple form, so the GROUP arrives later than the solo estimate. The
        // delay is COMPUTED per shot from the game's OWN speed-vs-time shot profile — never a stored or
        // fitted per-type number:
        //
        //     P(t)     = cumulative along-track distance = integral of speed(t)      (sim profile)
        //     tauForm  = time when P(t) reaches 2.5 * P(span)   (group forms; 2.5 = 1/0.4 closing ratio,
        //                                                         leader 0.6v vs straggler 1.0v)
        //     delay    = max(0, 0.4*tauForm - span)             (0.4 = leader throttle fraction)
        //
        // WHY it is range-aware WITHOUT any range/type constant: the delay is nonzero only when the
        // missile DECELERATES between the launch window and the forming point. On a flat (constant-
        // speed) profile tauForm = 2.5*span exactly, so delay = 0.4*2.5*span - span = 0. A lofting
        // missile builds distance fast during launch, then at SHORT range it has descended to slow
        // final-flight by the time it covers 2.5*P(span) => tauForm stretches => positive delay; at
        // LONG range 2.5*P(span) is still out in fast loft => tauForm ~= 2.5*span => delay ~= 0.
        //
        // The 0.4 / 2.5 constants come from the game's MissileGroup clamp (same physics for every
        // grouped missile, stock or modded). Everything else is read live: the profile from
        // SimulateShotLinear for THIS missile+geometry, and `span` from the observed launch ripple.
        // Non-grouped ammo (_maxGroupSize <= 1) returns 0.
        //
        // Validated in-game (2026-08-27) against measured true group lateness across 3 ranges:
        //   214km span73 -> computed 13.9s (measured ~15.5s);  330km -> 0s (~-1.7s);  400km -> 0s (~-1.8s)
        // and earlier span points (214km ×10 span34 -> ~+3s; ×20 span73 -> ~+16s). See the group-tau
        // log line in Coordinator and memory: autotot-grouped-flight-underestimate.

        private struct SpeedProfile { public float[] Times; public float[] Speeds; public Vector3[] Positions; }
        private static readonly TtlCache<TofKey, SpeedProfile> _profileCache =
            new TtlCache<TofKey, SpeedProfile>(CacheTtlSeconds);

        private static MethodInfo _simulateShotMethod;
        private static bool _simulateLookedUp;
        private static bool _simIsBeta; // resolved method is MissileSimulator.EstimateShot (beta branch)

        /// <summary>
        /// Extra arrival delay (seconds) for a GROUPED salvo of this ammo whose launcher ripples the
        /// rounds over <paramref name="launchSpan"/> seconds. 0 for a non-grouped/degenerate case or
        /// when the sim profile is unavailable. Uses the game's own speed-vs-time shot profile.
        /// </summary>
        internal static float GroupFormingDelay(ObjectBase unit, string ammoId, ObjectBase target, float launchSpan)
        {
            return GroupFormingTauDiag(unit, ammoId, target, launchSpan, out _, out _, out float delay)
                ? delay : 0f;
        }

        /// <summary>
        /// Diagnostic: the game's OWN modeled shot trajectory for this (shooter, ammo, target), so the
        /// sim's predicted path can be compared against the missile's ACTUAL flown path to explain the
        /// sim-vs-actual flight-time gap. Returns, from the same <see cref="SimulateShotLinear"/> profile
        /// used for grouping (cached): the sim intercept time, the modeled peak altitude (max
        /// <c>Positions[i].y</c> — same Unity-y space as a live <c>transform.position.y</c>), and the
        /// speed (kn) at launch, mid-flight, and intercept. False if the profile is unavailable.
        /// </summary>
        internal static bool TryTrajectoryDiag(ObjectBase unit, string ammoId, ObjectBase target,
            out float simInterceptTime, out float simPeakAltU, out float vLaunch, out float vMid, out float vTerm)
        {
            simInterceptTime = -1f; simPeakAltU = 0f; vLaunch = 0f; vMid = 0f; vTerm = 0f;
            if (unit == null || target == null) return false;
            AmmunitionParameters ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;

            SpeedProfile prof = GetSpeedProfile(unit, ap, target);
            float[] t = prof.Times, v = prof.Speeds;
            if (t == null || v == null || v.Length < 2) return false;

            simInterceptTime = t[t.Length - 1];
            vLaunch = v[0];
            vMid = v[v.Length / 2];
            vTerm = v[v.Length - 1];
            if (prof.Positions != null)
            {
                float peak = float.NegativeInfinity;
                for (int i = 0; i < prof.Positions.Length; i++)
                    if (prof.Positions[i].y > peak) peak = prof.Positions[i].y;
                simPeakAltU = peak;
            }
            return true;
        }

        /// <summary>
        /// Core of the range-aware τ_form group-drag model (drives <see cref="GroupFormingDelay"/> and
        /// the `group-tau` diagnostic log). Computes, from the game's own shot speed/time profile:
        ///   pSpan     = cumulative along-track distance covered in the first <paramref name="span"/> s
        ///   tauForm   = time at which cumulative distance reaches 2.5·pSpan (group forms; 2.5 = 1/0.4
        ///               from the leader-0.6v vs straggler-1.0v closing of MissileGroup's −40% clamp)
        ///   candidate = max(0, 0.4·tauForm − span)   (0.4 = leader throttle fraction) — the delay
        /// Distances are in knot·seconds (the KNOTS_TO_UNITY factor cancels in the 2.5 ratio).
        /// Returns false if the profile is unavailable / degenerate. See GroupFormingDelay header.
        /// </summary>
        internal static bool GroupFormingTauDiag(ObjectBase unit, string ammoId, ObjectBase target,
            float span, out float pSpan, out float tauForm, out float candidateDelay)
        {
            pSpan = 0f; tauForm = 0f; candidateDelay = 0f;
            if (unit == null || target == null || span <= 0f) return false;
            AmmunitionParameters ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null || ap._maxGroupSize <= 1) return false;

            SpeedProfile prof = GetSpeedProfile(unit, ap, target);
            float[] t = prof.Times, v = prof.Speeds;
            if (t == null || v == null || t.Length < 2) return false;

            float total = t[t.Length - 1];
            if (total <= 0f) return false;
            float spanClamped = Mathf.Min(span, total);

            // Cumulative distance (knot·seconds) at t = spanClamped.
            pSpan = CumulativeDistance(t, v, spanClamped);
            if (pSpan <= 0f) return false;

            // Walk the profile until cumulative distance reaches 2.5·pSpan; interpolate the time.
            float targetDist = 2.5f * pSpan;
            float cum = 0f;
            tauForm = total; // cap: never reaches 2.5·pSpan within the modelled flight
            for (int i = 1; i < t.Length; i++)
            {
                float dt = t[i] - t[i - 1];
                if (dt <= 0f) continue;
                float seg = 0.5f * (v[i - 1] + v[i]) * dt;
                if (cum + seg >= targetDist)
                {
                    float need = targetDist - cum;
                    tauForm = t[i - 1] + dt * (seg > 0f ? Mathf.Clamp01(need / seg) : 0f);
                    break;
                }
                cum += seg;
            }

            candidateDelay = Mathf.Max(0f, 0.4f * tauForm - span);
            return true;
        }

        // Cumulative trapezoidal distance (knot·seconds) from t=0 to t=tEnd over the profile.
        private static float CumulativeDistance(float[] t, float[] v, float tEnd)
        {
            float cum = 0f;
            for (int i = 1; i < t.Length; i++)
            {
                float a = t[i - 1], b = t[i];
                if (a >= tEnd) break;
                if (b <= a) continue;
                float hi = Mathf.Min(b, tEnd);
                float vhi = Mathf.Lerp(v[i - 1], v[i], (hi - a) / (b - a));
                cum += 0.5f * (v[i - 1] + vhi) * (hi - a);
            }
            return cum;
        }

        // The solo missile's speed-vs-time profile from the game's own shot sim. Cached like
        // Kinematic (the sim is 100+ steps and PredictAnchorImpact calls this every tick while the
        // anchor ripple is live).
        private static SpeedProfile GetSpeedProfile(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            TofKey key = new TofKey { UnitId = unit.GetInstanceID(), AmmoFile = ap._ammunitionFileName, TargetId = target.GetInstanceID() };
            if (_profileCache.TryGet(key, out SpeedProfile hit)) return hit;

            SpeedProfile prof = ComputeSpeedProfile(unit, ap, target);
            _profileCache.Set(key, prof);
            return prof;
        }

        private static SpeedProfile ComputeSpeedProfile(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            try
            {
                if (!_simulateLookedUp)
                {
                    _simulateLookedUp = true;
                    // Stable branch: Missile.SimulateShotLinear (step-by-step sim).
                    _simulateShotMethod = typeof(Missile).GetMethod("SimulateShotLinear", new Type[]
                    {
                        typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                        typeof(bool), typeof(float), typeof(float), typeof(float),
                        typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(float), typeof(bool)
                    });
                    _simIsBeta = false;
                    if (_simulateShotMethod == null)
                    {
                        // Beta branch: the sim moved to MissileSimulator.EstimateShot with a different
                        // signature (drops stepsPerMile, reorders loft/evasive, adds arrivalMargin;
                        // Chebyshev algorithm). Resolve the type by name — no compile-time reference —
                        // exactly like the KinematicRangeResult version-drift absorption. Same out-lists.
                        Type ms = typeof(Missile).Assembly.GetType("SeaPower.MissileSimulator");
                        _simulateShotMethod = ms?.GetMethod("EstimateShot", new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                            typeof(bool), typeof(float), typeof(float), typeof(float), typeof(bool),
                            typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(bool)
                        });
                        _simIsBeta = _simulateShotMethod != null;
                    }
                }
                if (_simulateShotMethod == null) return default;

                var speeds = new List<float>();
                var times = new List<float>();
                var traj = new List<Vector3>();
                Vector3 launchPos = unit.transform.position;
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool evasive = ap.AssumeEvasiveTarget(target);
                // stepsPerMile 2f matches AmmunitionParameters.MaxRangePrecise's own call (stable only).
                object[] args = _simIsBeta
                    ? new object[]
                    {
                        ap, launchPos, unit._velocityInKnots, targetVel, targetPos, unit.IsAirUnit,
                        -1f, -1f, -1f, evasive, traj, speeds, times, true
                    }
                    : new object[]
                    {
                        ap, launchPos, unit._velocityInKnots, targetVel, targetPos, unit.IsAirUnit,
                        -1f, 2f, -1f, traj, speeds, times, -1f, evasive
                    };
                _simulateShotMethod.Invoke(null, args);
                if (speeds.Count < 2 || times.Count != speeds.Count) return default;
                return new SpeedProfile
                {
                    Times = times.ToArray(),
                    Speeds = speeds.ToArray(),
                    Positions = traj.Count == speeds.Count ? traj.ToArray() : null,
                };
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog) Bootstrap.Log.LogWarning($"[AutoTOT] speed-profile sim failed: {e.GetType().Name}: {e.Message}");
                return default;
            }
        }

        // Reflection handles resolved once (the method/field don't change at runtime) instead of
        // on every uncached compute. _interceptTimeField is bound lazily off the first return
        // object (the return type is not exposed publicly by the game assembly).
        private static MethodInfo _maxRangePreciseMethod;
        private static bool _maxRangeLookedUp;
        private static FieldInfo _interceptTimeField;

        private static float KinematicRaw(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            try
            {
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool evasive = ap.AssumeEvasiveTarget(target);
                // iterations = 0: single-pass estimate. The precise iterative version (the game
                // uses 8) is ~8-9x the sim work and only nudged fast kinematic missiles by ~3s,
                // while doing nothing for low-kinematics cruise missiles — their real routing adds
                // distance the linear sim cannot capture either way (see README, Known limitations).
                if (!_maxRangeLookedUp)
                {
                    _maxRangeLookedUp = true;
                    _maxRangePreciseMethod = typeof(AmmunitionParameters).GetMethod("MaxRangePrecise", new Type[] { typeof(ObjectBase), typeof(Vector3), typeof(Vector3), typeof(int), typeof(bool) });
                }
                if (_maxRangePreciseMethod == null) return -1f;
                object krObj = _maxRangePreciseMethod.Invoke(ap, new object[] { unit, targetPos, targetVel, 0, evasive });
                if (krObj == null) return -1f;
                if (_interceptTimeField == null)
                    _interceptTimeField = krObj.GetType().GetField("InterceptTime");
                if (_interceptTimeField == null) return -1f;
                return (float)_interceptTimeField.GetValue(krObj);
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog) Bootstrap.Log.LogWarning($"[AutoTOT] kinematic flight-time failed: {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }
    }
}
