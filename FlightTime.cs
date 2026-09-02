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

        /// <summary>
        /// Phase 4 A/B gate. false = region-model stage boundaries stay hand-derived (today's shipped
        /// behavior). true = the loft-end distance is grounded on the game's own CreateWaypointConfigs
        /// plan (via WaypointSim.TryStageBoundaries). The `stage-src` diagnostic logs both regardless,
        /// so parity can be confirmed before flipping. See docs/plans WAYPOINT-SIM-PORT Phase 4.
        /// </summary>
        internal const bool UseWaypointBoundaries = false;

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

        // Beta-only grounded step-integrator helpers (see IntegratedEndTime). Resolved lazily in
        // EnsureSimLookup off the same MissileSimulator type. All fail soft: a null handle just
        // disables the integrator so KinematicRaw falls through to the EstimateShot path.
        private static MethodInfo _thrustMethod;      // MissileSimulator.CalculateThrustOverTime
        private static MethodInfo _dragMethod;        // MissileSimulator.CalculateDrag (10-arg overload)
        private static MethodInfo _loftCapMethod;     // MissileSimulator.LoftCap
        private static MethodInfo _altNodesMethod;    // MissileSimulator.BuildAltitudeNodes (private, out param)
        private static MethodInfo _burnEndMethod;     // MissileSimulator.BurnEndTime(ap, isAir) — total motor burn
        private static MethodInfo _dragBreakdownMethod; // CalculateDrag 13-arg overload w/ out aero/induced/grav components

        // One-shot reflection resolution for both the shot-sim method (SimulateShotLinear vs EstimateShot)
        // and the beta-only BuildFlyout bypass. Called from both KinematicRaw and ComputeSpeedProfile.
        private static void EnsureSimLookup()
        {
            if (!_simulateLookedUp)
            {
                _simulateLookedUp = true;
                _simulateShotMethod = typeof(Missile).GetMethod("SimulateShotLinear", new Type[]
                {
                    typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                    typeof(bool), typeof(float), typeof(float), typeof(float),
                    typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(float), typeof(bool)
                });
                _simIsBeta = false;
                if (_simulateShotMethod == null)
                {
                    Type ms = typeof(Missile).Assembly.GetType("SeaPower.MissileSimulator");
                    _simulateShotMethod = ms?.GetMethod("EstimateShot", new Type[]
                    {
                        typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                        typeof(bool), typeof(float), typeof(float), typeof(float), typeof(bool),
                        typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(bool)
                    });
                    _simIsBeta = _simulateShotMethod != null;

                    if (_simIsBeta)
                    {
                        // Grounded-integrator helpers (beta only). Exact param types disambiguate
                        // the CalculateDrag overloads (the 10-arg public one at MissileSimulator.cs:1882).
                        _thrustMethod = ms.GetMethod("CalculateThrustOverTime", new Type[]
                        { typeof(AmmunitionParameters), typeof(bool), typeof(float), typeof(float) });
                        _dragMethod = ms.GetMethod("CalculateDrag", new Type[]
                        {
                            typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                            typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float)
                        });
                        _loftCapMethod = ms.GetMethod("LoftCap", new Type[]
                        { typeof(AmmunitionParameters), typeof(float), typeof(float) });

                        // The installed beta DLL can differ from our decompile snapshot, so exact-type
                        // resolution may miss. Fall back to a by-name match (no out/ref params), then
                        // log which handles resolved + the candidate signatures so we can confirm the
                        // real shape. See plan "in-game test 1".
                        if (_thrustMethod == null)
                            _thrustMethod = ResolveByName(ms, "CalculateThrustOverTime", 4);
                        if (_dragMethod == null)
                            _dragMethod = ResolveByName(ms, "CalculateDrag", 10);
                        if (_loftCapMethod == null)
                            _loftCapMethod = ResolveByName(ms, "LoftCap", 3);

                        // The game's real flown altitude schedule (x=flat dist from launch, y=alt).
                        // private static with an `out float` — ResolveByName skips by-ref, so bind it
                        // directly with an explicit byref type. Used to drive the integrator's altitude
                        // profile from the game's own nodes (correct descent-start/dive) instead of our
                        // hand-built region reconstruction; region model remains the fallback.
                        const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;
                        _altNodesMethod = ms.GetMethod("BuildAltitudeNodes", NPS, null, new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(float), typeof(float), typeof(float),
                            typeof(float), typeof(float).MakeByRefType()
                        }, null);

                        // Total motor burn time — grounds the boost-loft climb model (climb to loftAlt
                        // by burnout). Public static (ap, bool).
                        _burnEndMethod = ms.GetMethod("BurnEndTime", new Type[]
                        { typeof(AmmunitionParameters), typeof(bool) });
                        if (_burnEndMethod == null)
                            _burnEndMethod = ResolveByName(ms, "BurnEndTime", 2);

                        // Part H diagnostic: the component overload — same physics as the 10-arg
                        // version but returns aero/induced/gravity-along-path separately (three `out
                        // float`s), so we can attribute WHICH term brakes (or fails to brake) the
                        // live missile at each telemetry sample. Public static, 13 params.
                        _dragBreakdownMethod = ms.GetMethod("CalculateDrag", new Type[]
                        {
                            typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                            typeof(float), typeof(bool), typeof(float),
                            typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(),
                            typeof(float), typeof(float)
                        });

                        LogSimInit(ms);

                        // Resolve the waypoint-sim reflection surface (Phase 1 spike / future port).
                        // Off the same game assembly; fail-soft (a miss just disables the spike).
                        WaypointSim.EnsureLookup(typeof(Missile).Assembly);
                    }
                }
            }
        }

        // Linear-interp altitude from the game's BuildAltitudeNodes list at flat-distance-from-launch
        // x. Nodes are (x=flat dist, y=alt), ascending x from 0 (launch) to flatDistTotal (target).
        private static float InterpNodeAlt(Vector2[] nodes, float x)
        {
            if (x <= nodes[0].x) return nodes[0].y;
            int last = nodes.Length - 1;
            if (x >= nodes[last].x) return nodes[last].y;
            for (int i = 0; i < last; i++)
            {
                float x0 = nodes[i].x, x1 = nodes[i + 1].x;
                if (x >= x0 && x <= x1)
                {
                    float span = x1 - x0;
                    float f = span > 1e-4f ? (x - x0) / span : 0f;
                    return nodes[i].y + (nodes[i + 1].y - nodes[i].y) * f;
                }
            }
            return nodes[last].y;
        }

        // Best-effort resolve of a static method by name + parameter count, skipping any overload
        // with by-ref (out/ref) parameters and preferring the fewest params on a tie. Used only as a
        // fallback when exact-type GetMethod misses because the live assembly's signature drifted.
        private static MethodInfo ResolveByName(Type t, string name, int paramCount)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo best = null;
            foreach (MethodInfo m in t.GetMethods(F))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != paramCount) continue;
                bool byRef = false;
                foreach (ParameterInfo p in ps) if (p.ParameterType.IsByRef) { byRef = true; break; }
                if (byRef) continue;
                if (best == null) best = m;
            }
            return best;
        }

        // One-time verbose dump of integrator handle resolution — resolved name or NULL, plus the
        // real signatures the live assembly exposes for any missing handle. Lets us confirm the
        // installed beta's actual method shapes without another decompile pass.
        private static void LogSimInit(Type ms)
        {
            if (!Coordinator.VerboseLog) return;
            try
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] sim-init: beta {_simIsBeta}, thrust {(_thrustMethod != null)}, " +
                    $"drag {(_dragMethod != null)}, loftCap {(_loftCapMethod != null)}, " +
                    $"altNodes {(_altNodesMethod != null)}, burnEnd {(_burnEndMethod != null)}, " +
                    $"dragBreakdown {(_dragBreakdownMethod != null)}");
                foreach (var pair in new (string name, MethodInfo mi)[]
                {
                    ("CalculateThrustOverTime", _thrustMethod), ("CalculateDrag", _dragMethod),
                    ("LoftCap", _loftCapMethod),
                })
                {
                    if (pair.mi != null) continue;
                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    foreach (MethodInfo m in ms.GetMethods(F))
                    {
                        if (m.Name != pair.name) continue;
                        string sig = string.Join(", ", Array.ConvertAll(m.GetParameters(),
                            p => $"{p.ParameterType.Name} {p.Name}"));
                        Bootstrap.Log.LogInfo($"[AutoTOT] sim-init candidate {m.Name}({sig})");
                    }
                }
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] sim-init dump failed: {e.GetType().Name}: {e.Message}");
            }
        }

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
                EnsureSimLookup();
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

        // Beta-only: call BuildFlyout directly to get the raw accurate FlyoutProfile, then find
        // the intercept time using the raw FlatDistAt (Hermite interpolation) instead of the lossy
        // Chebyshev polynomial fit in EstimateShot. Replicates the same Gap/binary-search from
        // EstimateShot (lines 1293-1324) but against the un-smeared profile.



        // ---- Grounded step-integrator (beta primary estimator) ----
        //
        // The beta EstimateShot -> BuildFlyout path overestimates lofting-missile flight time by
        // ~30s (coarse node-based Chebyshev integration). The beta exposes no reusable forward
        // physics sim, but it DOES expose the exact per-tick helpers its live missile loop
        // (Missile.PerformMoveForward) uses: CalculateThrustOverTime and CalculateDrag. This
        // integrator forward-Euler steps a single missile using THOSE helpers (no invented
        // physics), modelling only the loft pitch(t) geometry ourselves — which is the one thing
        // the game hides in a non-reusable FlightStage state machine, and the actual source of the
        // BuildFlyout error. Because CalculateDrag folds gravity-along-flightpath (g·sin(-pitch))
        // into the speed decrement, speed bleed is driven by pitch(t), so getting the loft arc
        // roughly right is what closes the gap.
        //
        // Returns intercept time (s), or -1f if unavailable (not beta, non-kinematic ammo, helper
        // missing, stalled, or out of range) — callers fall through to the EstimateShot path.
        //
        // NOTE: motorPerformance is a live per-missile RNG factor unknowable pre-launch; we use the
        // nominal 1.0. The loft geometry (climb angle, nose-over trigger) is a first-cut 3-phase
        // model tuned against in-game telemetry, not a replica of the state machine.
        /// <summary>
        /// Per-phase breakdown of one <see cref="IntegratedEndTimeCore"/> run, for the verbose
        /// `int-phases` diagnostic line. Times in seconds, speeds in knots, altitudes in Unity units.
        /// <see cref="Valid"/> is false if integration never ran (not beta, non-kinematic, helper miss).
        /// </summary>
        internal struct IntegratedPhases
        {
            public bool Valid;
            public bool Lofting;
            public float LoftAltTarget;   // modeled loft altitude the climb aims for (Unity u)
            public float PeakAltU;        // highest altitude reached in the run (Unity u)
            public float ClimbTime, CruiseTime, DescentTime;  // time spent in each phase (s)
            public float VStart;          // speed at launch (kn)
            public float VClimbExit;      // speed at end of the climb phase (kn); 0 if none
            public float VCruiseExit;     // speed at end of the cruise phase (kn); 0 if none
            public float VTerm;           // speed at intercept / run end (kn)
            public float FinalDistU;      // remaining-dist boundary where final/sea-skim begins (Unity u)
            public float TermDistU;       // remaining-dist boundary where terminal begins (Unity u)
        }

        internal static float IntegratedEndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
            => IntegratedEndTimeCore(unit, ap, target, out _, emitDiag: false);

        /// <summary>
        /// Runs <see cref="IntegratedEndTimeCore"/> for (shooter, ammo, target) and returns the
        /// intercept time plus its per-phase telemetry. False if the integrator did not run (not
        /// beta, non-kinematic ammo, or a helper miss). Verbose-only diagnostic (`int-phases` line).
        /// </summary>
        internal static bool TryIntegratedPhaseDiag(ObjectBase unit, string ammoId, ObjectBase target,
            out float interceptTime, out IntegratedPhases phases)
        {
            interceptTime = -1f; phases = default;
            if (unit == null || target == null) return false;
            AmmunitionParameters ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;
            interceptTime = IntegratedEndTimeCore(unit, ap, target, out phases, emitDiag: true);
            return phases.Valid;
        }

        private static float IntegratedEndTimeCore(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out IntegratedPhases phases, bool emitDiag)
        {
            phases = default;
            try
            {
                EnsureSimLookup();
                if (!_simIsBeta || _thrustMethod == null) return -1f;
                // Two speed models (see PerformMoveForward, Missile.cs:2960-2984):
                //  - Full kinematics  -> thrust + CalculateDrag (needs _dragMethod).
                //  - Non-kinematic    -> seek a per-stage target speed, NO drag. This is the ss-n-19
                //    case (ini Guidance/ApplyKinematics unset -> Kinematics==None) and the actual
                //    fix target; the game models it with BuildFlyoutNonKinematic (MissileSimulator.cs:1104).
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                if (!nonKin && _dragMethod == null) return -1f;

                const float KU = 0.0076554087f;  // knots -> Unity units/s (game constant)
                const float dt = 0.1f;           // fixed step (game runs ~0.02s; 0.1 trades cost for a 1-shot calc)
                // Zero-density altitude (Unity units): the game's atmosphere is
                // Utils.CalculateAirDensity(alt) = (1 - 0.00163*alt)^4.256, which reaches ZERO at
                // 1/0.00163 ≈ 613.5u. Above this line there is no aero drag, and CalculateDrag's
                // induced-lift divisor floors at 0.001 → the ~800x terminal "vacuum brake". Derived
                // from the game coefficient (not a fitted literal) so it tracks any modded atmosphere.
                const float ZeroDensityAltU = 1f / 0.00163f;
                Vector3 launchPos = unit.transform.position;
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool isAir = unit.IsAirUnit;

                // Evasive-target velocity modification (mirror EstimateShot, MissileSimulator.cs:1222-1229).
                float tvMag = targetVel.magnitude;
                if (tvMag > 0f && ap.AssumeEvasiveTarget(target))
                {
                    Vector3 flee = targetPos - launchPos; flee.y = 0f;
                    if (flee.sqrMagnitude > 1e-8f)
                    {
                        targetVel += flee.normalized * (tvMag * 0.8f);
                        targetVel = targetVel.normalized * Mathf.Min(targetVel.magnitude, tvMag);
                    }
                }

                float dragFactor = ap.GetDragFactor(isAir);
                float startVelKnots = Mathf.Max(unit._velocityInKnots, 0f);
                float maxFlight = ap._maxFlightTime > 0f ? ap._maxFlightTime : 600f;
                float targetAlt0 = Mathf.Max(targetPos.y, 0f);

                // Loft altitude we climb to = the ammo's own loft profile, the LoftCap value
                // (_maxLoftAltUnity / target / launcher per _loftAltMode) — the altitude the LIVE
                // guidance actually climbs to, for BOTH kinematic and non-kinematic ammo. We do NOT
                // use SearchOptimalLoftAltitude: it's the game estimator's range-optimizer, not the
                // flown profile, and it returned ~1u for yj-20 (a Mach-10 lofter that really climbs
                // to ~1427u), collapsing the whole flight into sea-level drag. LoftCap matches reality
                // (validated on ss-n-19/ss-n-12 nonKin; yj-20 kinematic).
                float loftAlt = -1f;
                if (_loftCapMethod != null)
                {
                    float cap = (float)_loftCapMethod.Invoke(null,
                        new object[] { ap, Mathf.Max(launchPos.y, 0f), targetAlt0 });
                    float floor = Mathf.Max(Mathf.Max(launchPos.y, 0f), targetAlt0);
                    if (cap > floor + 0.5f)
                        loftAlt = cap;
                }
                bool lofting = loftAlt > Mathf.Max(launchPos.y, 0f) + 0.5f;

                // --- Missile-class taxonomy (A2). Computed ONCE; every downstream branch reads these
                // named locals instead of re-deriving ad-hoc conditions, so the taxonomy lives in one
                // place. All grounded in ini/derived values, no per-missile constants.
                //  - isNonKinematic (== nonKin): ApplyKinematics unset -> stage-speed seek, no drag
                //    (ss-n-19/ss-n-12/yj-18a). Kinematic ammo use thrust + CalculateDrag.
                //  - isTerminalLoft: ini TerminalLoft -> concave hold-then-descend; altitude driven by
                //    the game's BuildAltitudeNodes curve (hhq-9b).
                //  - isHighBallisticLofter: a kinematic missile whose loft tops ABOVE the zero-density
                //    line -> it zoom-climbs into vacuum and needs the steep dive pitch + the terminal
                //    vacuum brake (yj-20). Below the line there is drag the whole way, so no brake.
                bool isTerminalLoft = ap._terminalLoft;
                bool isHighBallisticLofter = !nonKin && lofting && loftAlt > ZeroDensityAltU;

                float climbDeg = ap._maxLoftAngle > 0.5f ? ap._maxLoftAngle : 20f;
                // Ascent climb angle. KINEMATIC lofters (ApplyKinematics=True: yj-20/hhq-9b) climb
                // NEAR-VERTICAL during boost, not at MaxLoftAngle: the game's loft waypoint sets
                // AllowExceedingAngleLimits = (Kinematics != 0) so its kappa loft legitimately exceeds
                // _maxLoftAngle, and GetMaxPitchAngle caps Launch at 90° (Missile.cs). The track vs
                // sim-track overlay proved the real yj-20 barely closes range while zoom-climbing to
                // ~80km (dist ~188km at t+60), but the region model at 60° closed ~104km too fast →
                // arrived early. nonKin ammo (ss-n-19/yj-18a) keeps MaxLoftAngle — ±3s, do not touch.
                float boostClimbDeg = (!nonKin && lofting) ? 90f : climbDeg;
                // Pitch slew rate for the finite nose-over that produces the loft overshoot (ini
                // MaxTurnRate; the game rate-limits pitch by this in the live mover, Missile.cs:2561).
                float turnRate = ap._maxTurnRateDegrees > 0.1f ? ap._maxTurnRateDegrees : 5f;

                // Launch elevation: the launcher fires at a FIXED vertical angle (VLS ≈ 90° = straight
                // up), NOT toward the target — read from the firing launcher's WeaponParameters
                // (_fixVerticalLaunchAngle, gated by _fixVerticalLaunchAngleForLauncher; same access as
                // LauncherFacts). The missile flies at this elevation during _initialFlightPhaseDuration,
                // then guidance pulls toward the loft profile — but the finite turn rate can't nose it
                // down instantly, so it keeps climbing steeply → the high apex + non-closing boost that
                // our old horizontal-launch model missed. All grounded (game code Missile.cs:1331/2469).
                // -1 => no fixed angle: fall back to the launch-heading elevation toward the target.
                float launchPitch = -1f;
                try
                {
                    var launchers = unit.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
                    if (launchers != null && launchers.Count > 0)
                    {
                        var vwp = launchers[0]?._vwp;
                        if (vwp != null && vwp._fixVerticalLaunchAngleForLauncher)
                            launchPitch = vwp._fixVerticalLaunchAngle + vwp._additionalFixVerticalLaunchAngle;
                    }
                }
                catch { launchPitch = -1f; }
                float initialPhaseDur = Mathf.Max(ap._initialFlightPhaseDuration, 0f);

                // NOTE: the game's BuildAltitudeNodes altitude schedule (dormant `_altNodesMethod`)
                // was tried twice (global + high-loft-scoped) and REVERTED — it flattens our vertical
                // boost (interpolated ramp shrinks altErr so boostClimbDeg never engages) and caps the
                // loft at MaxLoftAlt (yj-20 overshoots to ~1420u). We model the terminal descent
                // ourselves instead — see the geometric `diveStart` in the region block below (Part E).

                // Non-kinematic stage speeds (knots) + decel-per-step, mirroring TargetSpeedForX /
                // PerformMoveForward. loft cruise fast, plain cruise at max, terminal at term speed.
                float maxVelKn = Mathf.Max(ap._maxVelocityInKnots, 1f);
                float loftVelKn = ap._maxLoftVelocityInKnots > 0f ? ap._maxLoftVelocityInKnots : maxVelKn;
                float termVelKn = ap._terminalVelocityInKnots > 0f ? ap._terminalVelocityInKnots : maxVelKn;
                float decelPerStep = ap._deceleration * 9.8f * 1.94384f * dt; // knots shed per step

                // Non-kinematic stage geometry by REMAINING flat distance to target, mirroring the
                // game's own BuildAltitudeNodes / TargetSpeedForX: loft dash (high, loftVel) -> final /
                // sea-skim cruise (finalAlt, maxVel) -> terminal (termAlt, termVel). All from the ammo's
                // own ini params — no per-missile constants. Validated against ss-n-19's real telemetry.
                // Loft ends (descent to cruise begins) at the phase the missile actually transitions
                // to after loft: sea-skim if _loftToSkim, else the final-flight phase. Picking the
                // blind max() is wrong when _loftToSkim is false (the sea-skim distance, though
                // larger, is skipped) — that made ss-n-19 leave the 1525 dash ~37km too early.
                bool toSkim = ap._loftToSkim && ap._seaSkimmingStartDistToTargetUnity > 0f;
                float finalDist = toSkim
                    ? ap._seaSkimmingStartDistToTargetUnity
                    : (ap._finalFlightPhaseDistToTargetUnity > 0f
                        ? ap._finalFlightPhaseDistToTargetUnity
                        : Mathf.Max(ap._seaSkimmingStartDistToTargetUnity, ap._finalFlightPhaseDistToTargetUnity));
                float finalAlt = toSkim
                    ? Mathf.Max(ap._seaSkimmingAltUnity, 0f)
                    : (ap._finalFlightPhaseAltUnity > 0f ? ap._finalFlightPhaseAltUnity
                       : (ap._seaSkimmingAltUnity > 0f ? ap._seaSkimmingAltUnity : 0f));
                float termDist = ap._terminalApproachDist;
                float termAlt = ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt;
                float descentDeg = ap._finalFlightPhaseMaxAngle > 0.01f ? ap._finalFlightPhaseMaxAngle
                                 : (ap._seaSkimmingMaxDescentAngle > 0.01f ? ap._seaSkimmingMaxDescentAngle : 20f);
                // Dive-ONSET angle (Part F): the STEEPEST descent the missile can do, used ONLY for the
                // geometric dive-start below — NOT for the descent pitch (descentDeg above stays the
                // pitch, so no sea-skimmer's descent behavior changes). descentDeg prefers
                // _finalFlightPhaseMaxAngle, which DEFAULTS to 30 when the ini omits it
                // (AmmunitionParameters.cs:1683) and would mask a steeper _seaSkimmingMaxDescentAngle
                // (yj-20 has SeaSkim 45 but no FinalFlightPhase → descentDeg=30 → dive onset ~163km, at
                // apex). Taking the max of both real ini caps lets a high lofter hold cruise then dive at
                // its true steeper angle (yj-20 → 45 → onset ~79km, near the real ~58km). Both default
                // 30, so non-descent-configured ammo are unaffected; only matters where geomDist>termDist.
                float descentOnsetDeg = Mathf.Max(descentDeg,
                    Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));

                // Phase 4: ground the loft-end distance on the game's own CreateWaypointConfigs plan
                // (the authoritative _loftToSkim-aware boundary — the value our hand-derivation above
                // has gotten wrong before). The `stage-src` line inside logs generator-vs-hand for
                // parity. Only the loft-end is grounded for now; finalAlt/termDist/termAlt/speeds stay
                // hand-derived (they equal the same ini fields). emitDiag-scoped when the flag is off so
                // planning candidates don't spam. Falls back to the hand `finalDist` if invalid.
                if (WaypointSim.Ready && (emitDiag || UseWaypointBoundaries) &&
                    WaypointSim.TryStageBoundaries(ap, Mathf.Max(launchPos.y, 0f), targetAlt0, out var sb))
                {
                    if (UseWaypointBoundaries && sb.Valid && sb.FinalDist > 0f)
                        finalDist = sb.FinalDist;
                }

                // Per-phase telemetry, updated continuously so it's valid at every early return.
                phases.Valid = true;
                phases.Lofting = lofting;
                phases.LoftAltTarget = lofting ? loftAlt : 0f;
                phases.VStart = startVelKnots;
                phases.PeakAltU = launchPos.y;
                phases.FinalDistU = finalDist;
                phases.TermDistU = termDist;

                // --- Integration ---
                Vector3 pos = launchPos;
                float velKnots = startVelKnots;
                float t = 0f;
                float prevPitch = launchPitch >= 0f ? launchPitch : 0f; // start pointed at the launch elevation
                float prevFlat = float.MaxValue;
                bool tlGliding = false; // Part K latch: TerminalLoft ammo have reached apex and are gliding down

                // Part K2 — TerminalLoft altitude from the game's OWN BuildAltitudeNodes curve. hhq-9b's
                // real profile is CONCAVE (holds loft alt, then descends steeply) — a straight-line glide
                // can't match it, but the game computes the exact flown alt-vs-distance nodes. Scoped to
                // _terminalLoft so yj-20 (region model + vacuum brake, its BuildAltitudeNodes caps the loft
                // and kills its zoom-climb) is never touched. Fetch once; the loop follows the curve via a
                // short lookahead aim. Falls back to the geometric aim-at-target glide if the handle misses.
                Vector2[] altNodes = null;
                float flatDistTotal = new Vector2(targetPos.x - launchPos.x, targetPos.z - launchPos.z).magnitude;
                if (isTerminalLoft && _altNodesMethod != null && flatDistTotal > 1f)
                {
                    try
                    {
                        object[] an = { ap, Mathf.Max(launchPos.y, 0f), targetAlt0, flatDistTotal, -1f, 0f };
                        var lst = _altNodesMethod.Invoke(null, an)
                            as System.Collections.Generic.List<Vector2>;
                        if (lst != null && lst.Count >= 2) altNodes = lst.ToArray();
                    }
                    catch { altNodes = null; }
                }
                object[] thrustArgs = new object[4];
                object[] dragArgs = new object[10];
                float nextSample = 15f;   // sim-track sampling cadence (matches the real `track` line)
                bool trackDiag = Coordinator.VerboseLog && emitDiag;
                string ammoLabel = ap._ammunitionFileName ?? "?";
                if (trackDiag)
                    Bootstrap.Log.LogInfo($"[AutoTOT] sim-launch {ammoLabel}: launchPitch " +
                        $"{(launchPitch >= 0f ? launchPitch.ToString("0.0") + "°" : "n/a (heading)")}" +
                        $", initPhase {initialPhaseDur:0.0}s, turnRate {turnRate:0.0}/s, loftAlt {loftAlt:0}u" +
                        $", descentDeg {descentDeg:0.0}°, onsetDeg {descentOnsetDeg:0.0}°");

                while (t < maxFlight)
                {
                    Vector3 predTgt = targetPos + targetVel * t;
                    float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                    float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                    // Intercept = closest approach (flat distance stops decreasing) or very close.
                    if ((flatDist > prevFlat && t > dt) || flatDist < 3f)
                    {
                        // Reject an intercept the missile is too slow to make (EstimateShot:1326).
                        if (velKnots < ap.MinVelocity * 1.1f) return -1f;
                        return t;
                    }
                    prevFlat = flatDist;

                    Vector3 horizDir = flatDist > 1e-4f
                        ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;

                    // Stage geometry: pitch (deg) + phase (0 loft/climb, 1 final/cruise, 2 terminal),
                    // and the per-stage target speed. The stage SCHEDULE (loft -> final/skim ->
                    // terminal by remaining flat distance) is the guidance FSM, identical whether or
                    // not kinematics apply — so BOTH branches drive altitude/pitch from it. Only the
                    // speed update below differs (nonKin = stage-speed seek, no drag; kinematic =
                    // thrust+drag clamped to stageTgt). Validated on ss-n-19/ss-n-12 (nonKin).
                    float pitchDeg = 0f;
                    int phase = 1;
                    float stageTgt = maxVelKn;
                    {
                        // Region by remaining flat distance (mirror BuildAltitudeNodes / TargetSpeedForX).
                        float stageAlt;
                        // Terminal dive onset (Part E — OWN descent model). The region model used a FIXED
                        // ini distance (termDist = _terminalApproachDist), which is fine for low cruisers
                        // but forces a HIGH lofter (yj-20 ~1190u apex) to wait until ~46km then PLUNGE
                        // steeply — screaming through the dense sub-613u layer with no time to decelerate
                        // (kept ~6600 vs real ~3300kn → ~17s early). Grounded geometric law: the flat
                        // distance needed to lose the current altitude down to termAlt at the ini descent
                        // angle is (alt - termAlt)/tan(descentDeg). Begin the dive at the LATER of termDist
                        // and that geometric distance, so a high-apex missile descends GRADUALLY at
                        // descentDeg from far enough out to bleed speed in dense air. Pure trig from each
                        // ammo's OWN ini (termAlt, descentDeg) — no per-missile constant; Max() means it
                        // only changes behavior for genuinely high-apex ammo (low/sea-skim get
                        // geomDist < termDist → identical to today). Only affects descent ONSET, never the
                        // climb (boostClimbDeg still governs ascent via altErr>0), so it can't flatten the
                        // boost the way the alt-schedule did.
                        float descentGeomDist = (pos.y - termAlt)
                                              / Mathf.Tan(Mathf.Max(descentOnsetDeg, 5f) * Mathf.Deg2Rad);
                        float diveStart = Mathf.Max(termDist, descentGeomDist);
                        // The old `!ap._terminalLoft` guard skipped this for TerminalLoft ammo (hhq-9b),
                        // leaving them cruising at loft alt with no dive/decel — but their real telemetry
                        // DESCENDS in terminal (hhq-9b alt 226->16), so let them enter and dive toward termAlt.
                        if (diveStart > 0f && flatDist <= diveStart)
                        { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                        else if (finalDist > 0f && flatDist <= finalDist)
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }   // final / sea-skim cruise
                        else if (lofting)
                        { stageTgt = loftVelKn; stageAlt = loftAlt; phase = 0; }   // loft dash
                        else
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }

                        // Desired pitch: climb/descend toward the target altitude, at the stage angle.
                        float altErr = stageAlt - pos.y;
                        float targetPitch = 0f;
                        // Part I — descent PITCH for high lofters. descentDeg prefers _finalFlightPhaseMaxAngle
                        // (defaults to 30, masking a steeper _seaSkimmingMaxDescentAngle), so yj-20 dives at
                        // only 30° despite its ini SeaSkim 45. A high ballistic lofter that dives that shallow
                        // LINGERS above the 613u zero-density line, where Part H's own-altitude lift-drag
                        // inflates to ~205kn/s — so it over-brakes for far too long, stalls below MinVelocity,
                        // and the integrator falls back to legacy (the +29.3s regression). The real missile
                        // dives steep/fast (~53°), punches through 613u quickly, and the lift term recovers →
                        // stabilizes at ~3490kn. Fix: for genuine high lofters (loftAlt > the 613u density
                        // line), dive at descentOnsetDeg (the steeper Max(finalPhase, seaSkim) already used for
                        // the geometric onset) instead of descentDeg. Grounded (both are real ini caps, no
                        // fitted constant); scoped by loftAlt so low/sea-skim ammo keep descentDeg unchanged.
                        float diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg;
                        if (altErr > 0.5f) targetPitch = boostClimbDeg;   // near-vertical for kinematic lofters
                        else if (altErr < -0.5f) targetPitch = -diveDeg;

                        // Part K (scoped to _terminalLoft, e.g. hhq-9b): a TerminalLoft missile does NOT
                        // hold loft altitude then dive late — it GLIDES DOWN continuously from apex, pointed
                        // at the target, sinking into dense air where aero drag bleeds it (real hhq-9b
                        // 3844→1872kn; our flat-hold at 385u kept ~3200 in thin air → +7s early). The real
                        // descent angle steepens 11°→22° exactly as line-of-sight geometry predicts, so aim
                        // straight at the target: pitch = -atan((alt − termAlt)/flatDist), capped at the ini
                        // descentDeg. Purely geometric — no fitted constant. Latch once apex is reached so a
                        // sinking altitude doesn't read as "climb again". Gated on _terminalLoft: yj-20 (not
                        // TerminalLoft) and every other ammo keep the region model / vacuum brake untouched.
                        if (isTerminalLoft && lofting)
                        {
                            if (altNodes != null)
                            {
                                // Follow the game's BuildAltitudeNodes curve (Part K2). Aim at where the
                                // schedule says the missile should be a short lookahead ahead (flat dist
                                // from launch = flatDistTotal − flatDist): while the curve is still high the
                                // aim is level (holds loft), and it noses down exactly where the curve
                                // descends — reproducing the real concave hold-then-descend. Lookahead ≈ the
                                // flat distance covered in ~2s, floored so it's always a finite baseline.
                                float xNow = flatDistTotal - flatDist;
                                float look = Mathf.Max(velKnots * KU * dt * 20f, 50f);
                                float altAhead = InterpNodeAlt(altNodes, Mathf.Min(xNow + look, flatDistTotal));
                                float slopeDeg = Mathf.Atan2(pos.y - altAhead, look) * Mathf.Rad2Deg;
                                targetPitch = -Mathf.Clamp(slopeDeg, -boostClimbDeg, descentDeg);
                            }
                            else
                            {
                                // Fallback (handle miss): geometric aim at the target altitude (≈ sea level).
                                if (!tlGliding && pos.y >= loftAlt - 0.5f) tlGliding = true;
                                if (tlGliding)
                                {
                                    float glideDeg = Mathf.Atan2(Mathf.Max(pos.y - targetAlt0, 0f),
                                        Mathf.Max(flatDist, 1f)) * Mathf.Rad2Deg;
                                    targetPitch = -Mathf.Min(glideDeg, descentDeg);
                                }
                            }
                        }

                        // Initial flight phase: the missile flies straight along the launch elevation
                        // (no guidance) for _initialFlightPhaseDuration. For a fixed/VLS launch that's a
                        // steep/vertical climb — the source of the near-vertical boost. After it, guidance
                        // resumes and the finite turn-rate below eases the nose over toward the loft/dive.
                        if (launchPitch >= 0f && t < initialPhaseDur)
                            targetPitch = launchPitch;

                        // FINITE PITCH-RATE nose-over: the missile can't pivot instantly — it swings its
                        // nose at `_maxTurnRateDegrees` (ini MaxTurnRate). So when it reaches the loft
                        // ceiling and commands level/dive, it's STILL angled up while turning and keeps
                        // climbing → OVERSHOOTS MaxLoftAlt (yj-20: 1190u ceiling, MaxTurnRate 12°/s →
                        // ~5s nose-over at 6600kn → real 1420u apex). Snapping pitch instantly (the old
                        // behavior) never overshot → path too short → ~33s early. Grounded in the ini
                        // turn rate, physically correct for ALL ammo, no fitted constant. prevPitch is
                        // the running pitch (set at loop end).
                        pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, turnRate * dt);
                    }
                    float pitchRate = (pitchDeg - prevPitch) / dt;

                    // Thrust (game helper), motorPerformance = 1 — the accel magnitude for both models.
                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                    float thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                    bool motorBurning = thrust > 0f;

                    // Speed update — HYBRID by ammo kind (the altitude schedule above is shared).
                    // Non-kinematic ammo (ss-n-19/ss-n-12/yj-18a, cruise/sea-skimmers) seek the
                    // per-stage target speed with NO drag — the game's kinematic-less mover; passes ±3s.
                    // Kinematic ammo (ApplyKinematics=True: yj-20/hhq-9b) use the game's thrust+drag
                    // physics so they BURN OUT (CalculateThrustOverTime -> 0 past AccelerationTime+
                    // SustainerAccelerationTime) and then COAST, decelerating via CalculateDrag in the
                    // terminal dive — but CLAMPED to the commanded per-stage target so thrust can't push
                    // past the cap (the old un-clamped branch overshot 8817 vs the 6600 cap). Pure seek
                    // held MaxVelocity forever and missed the post-burnout terminal slowdown (yj-20
                    // 7043->3185, hhq-9b 4079->1268), landing ~20-40s early.
                    float dragThisStep = 0f;
                    if (nonKin)
                    {
                        if (velKnots > stageTgt)
                            velKnots -= Mathf.Min(decelPerStep, velKnots - stageTgt);
                        else if (velKnots < stageTgt - 0.001f)
                            velKnots += Mathf.Min(thrust, stageTgt - velKnots);
                    }
                    else
                    {
                        velKnots += thrust;
                        // Drag + gravity-along-path (game helper). velocity arg is Unity units/s; pitch deg.
                        // PITCH SIGN: the game's CalculateDrag uses positive pitch = DESCENDING (its
                        // gravity term 9.81*sin(-pitch) accelerates a dive), matching the live mover's
                        // _currentPitch = WrapAngle(localEulerAngles.x) and flight-path asin(-vy/speed)
                        // (Missile.cs:2047/2630/3175 — climbing → negative). OUR integrator uses the
                        // opposite (dir = up*sin(pitch), positive = climbing), so we must NEGATE pitch
                        // and pitchRate here or a vertical climb (+90) reads as a dive and gravity wrongly
                        // ADDS speed (over-sped the boost 6600 vs real 5105 → early nose-over → +22s early).
                        dragArgs[0] = pos.y; dragArgs[1] = velKnots * KU; dragArgs[2] = dt; dragArgs[3] = -pitchDeg;
                        dragArgs[4] = dragFactor; dragArgs[5] = motorBurning;
                        // targetAltitude arg drives CalculateDrag's induced-lift term num9 =
                        // sqrt(|cos p|)·dragFactor·liftFactor·9.81 / max(airDensity(targetAlt)/1.225, 0.001).
                        // The live mover feeds the TARGET's altitude while the seeker holds lock (dense →
                        // divisor 0.816 → tiny lift drag) and the missile's OWN altitude once the lock drops
                        // (Missile.cs:3170-3174). Step-1 drag-break attribution of the real yj-20 proved the
                        // lock drops only as a BRIEF transient at the steep nose-over (~t+105, own alt 708u,
                        // vacuum): num9 spikes to +164kn/s (dragFactor 2.4, pitch 59°), bleeding ~1810kn, then
                        // the lock RE-ACQUIRES and the missile is in dense air where num9 self-collapses.
                        // Part H's failure was feeding own-alt for ALL of phase 2 (kept inflating below 613u
                        // where real num10 recovers → cratered → stall → fallback). Part J removed it entirely
                        // (→ no terminal brake → +10.5 early). Step 2: feed own altitude — enabling num9 — ONLY
                        // in the terminal dive (phase 2) AND above the 613u zero-density line (Utils.
                        // CalculateAirDensity hits 0 at ~613.5u). That mirrors the real transient: the brake
                        // fires in the vacuum nose-over and cleanly shuts off as the missile drops into dense
                        // air (own-alt density recovers → divisor rises → num9 → ~0), exactly like the
                        // telemetry (ind 164@708u → ~0@208u). Grounded: 613u is the game's own density constant,
                        // dragFactor/liftFactor/pitch are per-ini; NO fitted constant, general to any high
                        // lofter. hhq-9b (loft 386u, never > 613) never triggers → unchanged. nonKin skips this
                        // branch. See plan Step 2 + STEP 1 RESULT.
                        // Step 3: the third condition is a steep nose-over (pitchDeg < -40°, our
                        // climb-positive convention) — the proxy for the seeker LOCK-DROP that makes the
                        // mover feed own-altitude. Step 2 (phase2 && >613 only) fired the brake at dive
                        // ONSET while still near-level (pitch −4°): cos≈1 → num9 MAXIMAL, sin≈0 → braked in
                        // place → runaway crater → fallback. The real lock drops only mid-nose-over at ~59°.
                        // A4/A4b (REVERTED): grounding this in the seeker cone geometry
                        // (Vector3.Angle(velocity, dirToTarget) > _seekerFOV/2 or _seekerGimbalFOV/2,
                        // SeekerBase.isInGimbalFOV) was tried TWICE and FALSIFIED by the real telemetry:
                        // the real yj-20 HELD lock at t+90 (near-level, ~39° look angle) and DROPPED it at
                        // t+105 (steep 59° dive, ~20° look angle) — it dropped when the look angle was
                        // SMALLER, so the real lock-drop tracks the steep terminal DIVE, not look-angle
                        // geometry (a look-angle gate on our smoothed/shallower pitch fires at the wrong
                        // moment — while near-level at apex). So `-40°` is kept as the faithful, general
                        // "steeply-diving" descriptor of the terminal lock-drop; it fires within phase 2 on
                        // our own dive pitch, is not a yj-20 fit (any high lofter noses over past 40°), and
                        // delivers yj-20 −5. See plan A4/A4b RESULT.
                        bool inVacuumDive = phase == 2 && pos.y > ZeroDensityAltU && pitchDeg < -40f;
                        dragArgs[6] = inVacuumDive ? pos.y : predTgt.y;
                        dragArgs[7] = ap.LiftFactor; dragArgs[8] = ap.MinVelocity; dragArgs[9] = -pitchRate;
                        dragThisStep = (float)_dragMethod.Invoke(null, dragArgs);
                        velKnots -= dragThisStep;
                        if (motorBurning && velKnots > stageTgt) velKnots = stageTgt; // cap THRUST only; post-burnout coasts (Part G)
                    }

                    if (velKnots < 1f) return -1f; // stalled

                    float pr = pitchDeg * Mathf.Deg2Rad;
                    Vector3 dir = horizDir * Mathf.Cos(pr) + Vector3.up * Mathf.Sin(pr);
                    pos += velKnots * KU * dt * dir;

                    // Phase accounting (after the step's speed/position update). Exit speeds overwrite
                    // each step, so they end holding the speed at the last step of that phase.
                    if (phase == 0) { phases.ClimbTime += dt; phases.VClimbExit = velKnots; }
                    else if (phase == 1) { phases.CruiseTime += dt; phases.VCruiseExit = velKnots; }
                    else { phases.DescentTime += dt; }
                    phases.VTerm = velKnots;
                    if (pos.y > phases.PeakAltU) phases.PeakAltU = pos.y;

                    // sim-track: the integrator's OWN predicted trajectory at the real `track` cadence
                    // (~15s), so we can overlay sim vs reality and see where the terminal decel fails.
                    // dragThisStep is per-step knots shed (0 for nonKin); ×(1/dt) => knots/s.
                    if (trackDiag && t + dt >= nextSample)
                    {
                        // `slant` = 3-D distance to the predicted target — directly comparable with the
                        // real `track` line (LaunchDiagnostics logs GameUnits.MetersBetween = 3-D). `dist`
                        // stays FLAT (horizontal) for the region/geometry reasoning. Without slant the
                        // overlay is apples-to-oranges (real slant grows with the ~80km climb).
                        float slantKm = (predTgt - pos).magnitude * GameUnits.MetersPerUnity / 1000f;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] sim-track {ammoLabel}: t+{t:0}s spd {velKnots:0}kn alt {pos.y:0.0} " +
                            $"pitch {pitchDeg:0} drag {dragThisStep / dt:0}kn/s phase {phase} " +
                            $"dist {flatDist * GameUnits.MetersPerUnity / 1000f:0.0}km slant {slantKm:0.0}km");
                        nextSample += 15f;
                    }

                    prevPitch = pitchDeg;
                    t += dt;
                }
                return -1f;
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] integrated flight-time failed: {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }

        /// <summary>
        /// Part H diagnostic: evaluates the game's OWN <c>CalculateDrag</c> component overload at the
        /// LIVE missile's current state and returns the per-term deceleration rates (kn/s, positive =
        /// slowing down) plus their total — exactly what <c>PerformMoveForward</c> subtracts from
        /// <c>_velocityInKnots</c> for kinematic ammo. Comparing <paramref name="totalKnPerSec"/> with
        /// the observed Δspeed/Δt from telemetry pins which term brakes the real missile (aero drag,
        /// lift-induced, or gravity-along-path) — or proves none does, i.e. the brake lives outside
        /// this helper. Inputs use the LIVE mover's conventions: pitch positive = DESCENDING (the
        /// game's <c>_currentPitch</c> = wrapped <c>localEulerAngles.x</c>), velocity in Unity u/s,
        /// dt = the game's fixedDeltaTime. False if any handle/state is unavailable.
        /// </summary>
        internal static bool TryDragBreakdown(WeaponBase w, ObjectBase target, float timeSinceLaunch,
            float pitchGameDeg, float pitchRateGameDegSec, float dt,
            out float totalKnPerSec, out float aeroKnPerSec, out float inducedKnPerSec, out float gravKnPerSec,
            out bool motorBurning, out bool lockHeld, out float targetAltUsed)
        {
            totalKnPerSec = aeroKnPerSec = inducedKnPerSec = gravKnPerSec = 0f;
            motorBurning = false;
            lockHeld = false;
            targetAltUsed = 0f;
            try
            {
                EnsureSimLookup();
                if (_dragBreakdownMethod == null || w == null || target == null || w._ap == null) return false;
                if (w.transform == null || target.transform == null || dt <= 0f) return false;
                AmmunitionParameters ap = w._ap;
                bool isAir = w._launchPlatform != null && w._launchPlatform.IsAirUnit;

                // motorBurning the way the mover computes it (thrust > 0): nominal burn window from
                // the game's own BurnEndTime (motorPerformance RNG is unknowable; nominal suffices).
                if (_burnEndMethod != null)
                    motorBurning = timeSinceLaunch < (float)_burnEndMethod.Invoke(null, new object[] { ap, isAir });

                // targetAltitude the way the MOVER computes it (Missile.cs:3170-3174): the live
                // missile's CurrentTarget altitude while the seeker holds lock, else the missile's
                // OWN altitude once the lock drops (CurrentTarget == null) — the switch that inflates
                // the lift term in the vacuum dive. Mirror it exactly so `induced` matches the real brake.
                ObjectBase liveTgt = null;
                try { liveTgt = w.CurrentTarget; } catch { liveTgt = null; }
                lockHeld = liveTgt != null && liveTgt.transform != null;
                targetAltUsed = lockHeld ? liveTgt.transform.position.y : w.transform.position.y;

                object[] args = new object[]
                {
                    w.transform.position.y,           // altitude (Unity u)
                    w._velocityInKnots * 0.0076554087f, // velocity (Unity u/s)
                    dt,                               // time step (s)
                    pitchGameDeg,                     // pitch, game convention (+ = descending)
                    ap.GetDragFactor(isAir),          // dragFactor (same call the mover makes)
                    ap.LiftFactor,
                    motorBurning,
                    targetAltUsed,                    // targetAltitude (mover's lock-held-or-own rule)
                    null, null, null,                 // out aero / induced / parallelG
                    ap.MinVelocity,                   // stallSpeedKnots
                    pitchRateGameDegSec,
                };
                float total = (float)_dragBreakdownMethod.Invoke(null, args);
                totalKnPerSec = total / dt;
                aeroKnPerSec = (float)args[8] / dt;
                inducedKnPerSec = (float)args[9] / dt;
                gravKnPerSec = (float)args[10] / dt;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static float KinematicRaw(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            // Beta primary: grounded step-integrator. Falls through (-1) to the game's own
            // MaxRangePrecise/EstimateShot path for non-kinematic ammo, out-of-range, or any
            // helper miss. On the public branch IntegratedEndTime returns -1 (not beta), so this
            // is always the MaxRangePrecise result there.
            float integrated = IntegratedEndTime(unit, ap, target);
            if (integrated > MinValidSeconds) return integrated;

            // Middle-tier fallback: the ported public waypoint sim (docs/plans/WAYPOINT-SIM-PORT.md).
            // The grounded integrator above stays primary (it owns all reference shots incl. the exotic
            // yj-20 loft-overshoot, which the game's own SimulateShotLinear does NOT model). But when
            // the integrator declines (out of envelope / helper miss), the waypoint sim flies the game's
            // own guidance and is ~±6s on lofters vs the legacy EstimateShot's ±33s — a far better net.
            if (WaypointSim.Ready && WaypointSim.FullReady)
            {
                float wp = WaypointSim.EndTime(unit, ap, target);
                if (wp > MinValidSeconds) return wp;
            }
            return MaxRangePreciseEndTime(unit, ap, target);
        }

        /// <summary>
        /// The game's own single-shot InterceptTime via <c>AmmunitionParameters.MaxRangePrecise</c>
        /// (→ <c>EstimateShot</c>/<c>SimulateShotLinear</c>). -1f if unavailable. Exposed so
        /// diagnostics can log it beside <see cref="IntegratedEndTime"/> for accuracy comparison.
        /// </summary>
        internal static float MaxRangePreciseEndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
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
