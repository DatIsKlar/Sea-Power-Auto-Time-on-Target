using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        private const float CacheTtlSeconds = 0.5f;
        internal const float MinValidSeconds = 0.01f;
        private const float MinSpeedMs = 0.1f;
        private const float GroupFormingDistMultiplier = 2.5f;
        private const float GroupFormingDelayFraction = 0.4f;

        // Shared by every flight-sim tier (integrator, waypoint port): the evasive-target speed
        // boost, as a fraction of the target's own speed, and the sim horizon used when the ammo
        // declares no _maxFlightTime.
        internal const float EvasiveBoostFraction = 0.8f;
        internal const float MaxFlightTimeFallback = 600f;

        /// <summary>
        /// The evasive-target speed boost the game applies before simulating a shot: push the
        /// target's velocity away from the shooter by <see cref="EvasiveBoostFraction"/> of its own
        /// speed, then renormalize so the boost changes direction without adding speed. No-op when
        /// the target is stationary or the ammo does not assume evasion.
        ///
        /// Main thread only: reads <c>ap.AssumeEvasiveTarget</c>, which touches game state. Both
        /// callers (the integrator's setup half and WaypointSim.EndTime) are on that path.
        /// </summary>
        internal static void ApplyEvasiveBoost(AmmunitionParameters ap, ObjectBase target,
            Vector3 launchPos, Vector3 targetPos, ref Vector3 targetVel)
        {
            float speed = targetVel.magnitude;
            if (speed <= 0f || !ap.AssumeEvasiveTarget(target)) return;
            Vector3 flee = targetPos - launchPos; flee.y = 0f;
            if (flee.sqrMagnitude <= 1e-8f) return;
            targetVel += flee.normalized * (speed * EvasiveBoostFraction);
            targetVel = targetVel.normalized * Mathf.Min(targetVel.magnitude, speed);
        }

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

        private static TofKey KeyFor(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
            => new TofKey
            {
                UnitId = unit.GetInstanceID(),
                AmmoFile = ap._ammunitionFileName,
                TargetId = target.GetInstanceID(),
            };

        /// <summary>
        /// Resolve a (unit, ammo id, target) triple to the ammo's parameters and its cache key.
        /// False when any part is missing, in which case the caller has nothing to estimate.
        /// </summary>
        private static bool TryKey(ObjectBase unit, string ammoId, ObjectBase target,
                                   out AmmunitionParameters ap, out TofKey key)
        {
            ap = null; key = default;
            if (unit == null || target == null) return false;
            ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;
            key = KeyFor(unit, ap, target);
            return true;
        }

        private static readonly TtlCache<TofKey, float> _cache = new TtlCache<TofKey, float>(CacheTtlSeconds);
        // Straight-line fallback results (kinematic sim unavailable) get their own cache so a
        // per-frame caller doesn't recompute and re-register a profiling "miss" every frame.
        private static readonly TtlCache<TofKey, float> _fallbackCache = new TtlCache<TofKey, float>(CacheTtlSeconds);

        // Display-only memo in front of Estimate. The panel asks for an ETA per visible row per
        // frame, ~90 calls, and at the 0.5s firing TTL a couple of those fell through to a full
        // 4300-step integration EVERY FRAME purely to draw a text label: 4.2ms/frame, 20% of a frame,
        // even with nothing in flight.
        //
        // This does not introduce a second estimator. It returns exactly what Estimate returns, just
        // recomputed less often, so a label can be up to DisplayCacheTtlSeconds stale -- about 1% of
        // a 500s flight, invisible in text rounded to the second. Firing decisions never read this
        // cache and are unaffected.
        private const float DisplayCacheTtlSeconds = 5f;
        private static readonly TtlCache<TofKey, float> _displayCache =
            new TtlCache<TofKey, float>(DisplayCacheTtlSeconds);

        /// <summary>
        /// Flight time for on-screen display. Same value as <see cref="Estimate"/>, memoised for
        /// several seconds. Never call this for a firing decision: use <see cref="Estimate"/>, which
        /// the commit and release paths share.
        /// </summary>
        internal static float EstimateForDisplay(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (!TryKey(unit, ammoId, target, out _, out TofKey key)) return 0f;
            if (_displayCache.TryGet(key, out float cached)) return cached;

            float value = Estimate(unit, ammoId, target);
            _displayCache.Set(key, value);
            return value;
        }

        private static bool _lastCallWasHit;
        internal static bool WasLastCallCacheHit => _lastCallWasHit;

        internal static float Estimate(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (!TryKey(unit, ammoId, target, out AmmunitionParameters ap, out TofKey key)) return 0f;

            float kinematic = Kinematic(unit, ap, target);
            if (kinematic > MinValidSeconds) return kinematic;

            // The kinematic tier ran and declined, so this call paid for a full integration whatever
            // the straight-line cache does next. Leave _lastCallWasHit false (Kinematic set it on
            // its way through): raising it here charged that integration to the profiler's cache-hit
            // bucket, which is the accounting error CoordinatorProfiler.CountCachedHit documents.
            if (_fallbackCache.TryGet(key, out float cachedFallback)) return cachedFallback;

            float speedMs = ap._maxVelocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= MinSpeedMs) return 0f;
            float fallback = GameUnits.MetersBetween(unit, target) / speedMs;
            _fallbackCache.Set(key, fallback);
            return fallback;
        }

        /// <summary>
        /// The state of a shooter at the instant a round left its rail: everything
        /// <see cref="TryBuildSolveInput"/> would otherwise read live from the platform.
        ///
        /// One rule produces every timing correction in
        /// docs/plans/open/aircraft-anchor-impact-slide.md: <b>pair a launch time with a flight
        /// estimate valid AT that launch time</b>. <see cref="Estimate"/> cannot express it, because
        /// it takes the shooter as an <see cref="ObjectBase"/> and reads <c>transform.position</c>,
        /// so it can only ever answer "from where the shooter is now". For a ship the two readings
        /// describe the same place; for an aircraft closing at 540kn the answer walks earlier by the
        /// distance flown, and the shared impact of a strike walks with it.
        ///
        /// The rail heading is carried because the launch bearing is the actual geometry the round
        /// flew (see docs/plans/open/offboresight-estimate-collapse.md), and by the time the
        /// prediction re-runs the aircraft has turned. Pitch is still read from the live rail: it is
        /// an elevation in the platform's own frame and does not drift as the platform manoeuvres.
        /// </summary>
        internal readonly struct LaunchState
        {
            internal readonly bool    Valid;
            internal readonly Vector3 PosU;         // shooter position at launch, Unity units
            internal readonly float   VelKnots;     // shooter speed at launch
            internal readonly Vector3 HeadingFlat;  // rail bearing at launch, flat; zero = unknown

            internal LaunchState(Vector3 posU, float velKnots, Vector3 headingFlat)
            {
                Valid = true;
                PosU = posU;
                VelKnots = Mathf.Max(velKnots, 0f);
                HeadingFlat = headingFlat;
            }
        }

        /// <summary>
        /// <see cref="Estimate"/> for a round that has ALREADY launched: the same integrator, run
        /// from the recorded launch state instead of from wherever the shooter is now.
        ///
        /// Deliberately uncached. The answer is a constant of one launch, so the caller memoises it
        /// against the launch observation and this runs once per round, where the shared TTL cache
        /// is keyed on the live shooter and would be wrong for this question.
        ///
        /// The lower tiers cannot take an origin override, so this DECLINES (-1) rather than
        /// answering from a cruder model. The caller then falls back to the live estimate, which is
        /// a real integration of the wrong launch point and beats a straight-line floor from the
        /// right one. The plausibility floor is applied from the recorded position, since that is
        /// where this shot actually started.
        /// </summary>
        internal static float EstimateFromLaunch(ObjectBase unit, string ammoId, ObjectBase target,
                                                 in LaunchState launch)
        {
            if (!launch.Valid) return Estimate(unit, ammoId, target);
            if (!TryKey(unit, ammoId, target, out AmmunitionParameters ap, out _)) return -1f;

            float integrated = IntegratedEndTimeCore(unit, ap, target, out _, emitDiag: false, launch);
            float floor = StraightLineFloorSeconds(launch.PosU, ap, target);
            if (integrated > MinValidSeconds
                && !(floor > 0f && integrated < floor * PlausibleFloorFraction))
            {
                ModelStats.TierUsed(ModelStats.Tier.Integrator);
                return integrated;
            }
            if (integrated > MinValidSeconds)
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] estimate-rejected {ap._ammunitionFileName}: launch-state integration " +
                    $"returned {integrated:0.00}s against a straight-line floor of {floor:0.0}s.");
            return -1f;
        }

        internal static float Kinematic(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            TofKey key = KeyFor(unit, ap, target);
            if (_cache.TryGet(key, out float cached))
            {
                _lastCallWasHit = true;
                return cached;
            }

            _lastCallWasHit = false;
            float value = KinematicRaw(unit, ap, target);
            _cache.Set(key, value);
            return value;
        }

        internal static void ClearCache()
        {
            _cache.Clear(); _fallbackCache.Clear(); _displayCache.Clear(); _profileCache.Clear();
            ResetQueues();
            ClearRailLog();
        }

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

        internal static float GroupFormingDelay(ObjectBase unit, string ammoId, ObjectBase target, float launchSpan)
        {
            return GroupFormingTauDiag(unit, ammoId, target, launchSpan, out _, out _, out float delay)
                ? delay : 0f;
        }

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

            pSpan = CumulativeDistance(t, v, spanClamped);
            if (pSpan <= 0f) return false;

            float targetDist = GroupFormingDistMultiplier * pSpan;
            float cum = 0f;
            tauForm = total;
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

            candidateDelay = Mathf.Max(0f, GroupFormingDelayFraction * tauForm - span);
            return true;
        }

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

        private struct SpeedProfile { public float[] Times; public float[] Speeds; }
        private static readonly TtlCache<TofKey, SpeedProfile> _profileCache =
            new TtlCache<TofKey, SpeedProfile>(CacheTtlSeconds);

        private static SpeedProfile GetSpeedProfile(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            TofKey key = KeyFor(unit, ap, target);
            if (_profileCache.TryGet(key, out SpeedProfile cached)) return cached;

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
                // `traj` is filled only because the game's sim signature requires the list; nothing
                // here reads the trajectory back, so it is not copied out.
                return new SpeedProfile
                {
                    Times = times.ToArray(),
                    Speeds = speeds.ToArray(),
                };
            }
            catch (Exception e)
            {
                ModLog.VerboseWarn("speed-profile sim failed", e);
                return default;
            }
        }

        private static MethodInfo _maxRangePreciseMethod;
        private static bool _maxRangeLookedUp;
        private static FieldInfo _interceptTimeField;

        /// <summary>
        /// Fraction of the straight-line, top-speed flight time below which a result is not
        /// slow-but-wrong, it is broken. A real shot is always SLOWER than range over max speed and
        /// never faster, so this is a bound the physics guarantees rather than a tuned number.
        /// </summary>
        private const float PlausibleFloorFraction = 0.5f;

        /// <summary>
        /// Straight-line time for this shot at the round's own top speed, or -1 when it cannot be
        /// computed. The lower bound no honest estimate can beat.
        /// </summary>
        internal static float StraightLineFloorSeconds(ObjectBase unit, AmmunitionParameters ap,
                                                       ObjectBase target)
        {
            if (unit == null || ap == null || target == null) return -1f;
            float speedMs = ap._maxVelocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= MinSpeedMs) return -1f;
            return GameUnits.MetersBetween(unit, target) / speedMs;
        }

        /// <summary>The same floor measured from a recorded launch position, for
        /// <see cref="EstimateFromLaunch"/>, where the shooter has since moved.</summary>
        internal static float StraightLineFloorSeconds(Vector3 originU, AmmunitionParameters ap,
                                                       ObjectBase target)
        {
            if (ap == null || target == null || target.transform == null) return -1f;
            float speedMs = ap._maxVelocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= MinSpeedMs) return -1f;
            return (target.transform.position - originU).magnitude * GameUnits.MetersPerUnity / speedMs;
        }

        /// <summary>
        /// True when a candidate estimate is physically impossible for this shot.
        ///
        /// <see cref="MinValidSeconds"/> is 0.01s and exists to mean "the model declined", not "the
        /// model answered absurdly". A 0.2s answer for a 195 km shot passed it, was cached, and
        /// reached the coordinator as a real flight time, where it either stole the anchor role or
        /// set the shared impact in the past. See
        /// docs/plans/open/offboresight-estimate-collapse.md.
        /// </summary>
        internal static bool IsImplausible(float seconds, ObjectBase unit, AmmunitionParameters ap,
                                           ObjectBase target)
        {
            float floor = StraightLineFloorSeconds(unit, ap, target);
            return floor > 0f && seconds < floor * PlausibleFloorFraction;
        }

        private static float KinematicRaw(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            float integrated = IntegratedEndTime(unit, ap, target);
            // An implausible answer is treated exactly like a decline, so the lower tiers get their
            // turn instead of the caller being handed a number that cannot be true.
            if (integrated > MinValidSeconds && IsImplausible(integrated, unit, ap, target))
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] estimate-rejected {ap._ammunitionFileName}: integrator returned " +
                    $"{integrated:0.00}s against a straight-line floor of " +
                    $"{StraightLineFloorSeconds(unit, ap, target):0.0}s. Falling through to the " +
                    $"remaining tiers.");
                integrated = -1f;
            }
            if (integrated > MinValidSeconds)
            {
                ModelStats.TierUsed(ModelStats.Tier.Integrator);
                return integrated;
            }
            // The integrator declined to answer, so this call also pays for whichever tier does.
            ModelStats.Stalled();

            if (WaypointSim.Ready && WaypointSim.FullReady)
            {
                float waypointEst = WaypointSim.EndTime(unit, ap, target);
                if (waypointEst > MinValidSeconds)
                {
                    ModelStats.TierUsed(ModelStats.Tier.Waypoint);
                    return waypointEst;
                }
            }
            float maxRangeEst = MaxRangePreciseEndTime(unit, ap, target);
            ModelStats.TierUsed(maxRangeEst > MinValidSeconds ? ModelStats.Tier.MaxRangePrecise
                                                              : ModelStats.Tier.Failed);
            return maxRangeEst;
        }

        internal static float MaxRangePreciseEndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            try
            {
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool evasive = ap.AssumeEvasiveTarget(target);
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
                ModLog.VerboseWarn("kinematic flight-time failed", e);
                return -1f;
            }
        }
    }
}
