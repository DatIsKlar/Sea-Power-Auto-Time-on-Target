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
        /// Hard ceiling on the integration loop, for ammunition that declares MaxFlightTime as float.MaxValue
        /// (most). The loop's other exit is the closest-approach test, and a round that never closes or
        /// recedes would spin: past t = 2^24 * dt a float t stops advancing. Four hours is well clear of
        /// anything physical; the longest-ranged ammunition needs about three at cruise speed.
        /// </summary>
        internal const float SolveLoopCeilingSim = 4f * 3600f;

        /// <summary>
        /// The target velocity every estimator reads: the game's <c>_velocityVecInUnity</c>, with a surface
        /// ship's float32 rounding applied (<see cref="GameMath.TranslateRoundedVelocity"/>). That field is
        /// what <c>Vessel.applyMainThrust</c> hands <c>Translate</c> each tick, not what the transform
        /// realises: a slow ship away from the map origin moves at roughly 0.87 to 1.15 of its speed. Only a
        /// <c>Vessel</c> is rounded; a submarine's thrust path does not Translate, and an aircraft's per-tick
        /// move is large enough that rounding is negligible.
        ///
        /// <paramref name="step"/> is the physics step the rounding used
        /// (<see cref="PredictedPhysicsStep"/>), 0 when none was applied. Main thread only.
        /// </summary>
        internal static Vector3 TargetMapVelocity(ObjectBase target, out float step)
        {
            Vector3 reported = target._velocityVecInUnity;
            step = 0f;
            if (!(target is Vessel)) return reported;
            step = PredictedPhysicsStep();
            return GameMath.TranslateRoundedVelocity(target.transform.position, reported, step);
        }

        /// <inheritdoc cref="TargetMapVelocity(ObjectBase, out float)"/>
        internal static Vector3 TargetMapVelocity(ObjectBase target) => TargetMapVelocity(target, out _);

        /// <summary>The two values <c>GameTime.SetAdaptiveFixedDeltaTime</c> gives
        /// <c>Time.fixedDeltaTime</c>: 1/30 s while the frame rate is under 30, 1/60 s once it is over 40.</summary>
        internal const float BaseStep60Hz = 0.0166667f, BaseStep30Hz = 0.0333333f;

        /// <summary>Time constant of the step history, and the history needed before it is trusted
        /// over the 1/60 s default. See <see cref="PredictedPhysicsStep"/>.</summary>
        private const float StepHistoryTauSim = 60f, StepHistoryMinSim = 20f;

        private static float _stepSim30, _stepSim60, _stepHistorySim, _stepLastSim = -1f;

        /// <summary>
        /// Feeds <see cref="PredictedPhysicsStep"/>: sim seconds spent at each base step, decayed with a
        /// one minute time constant. Called once per coordinator tick, whether or not a strike is
        /// pending. Main thread only.
        /// </summary>
        internal static void ObservePhysicsStep(float simNow)
        {
            if (_stepLastSim < 0f || simNow < _stepLastSim) { _stepLastSim = simNow; return; }
            float dt = simNow - _stepLastSim;
            _stepLastSim = simNow;
            if (dt <= 0f) return;
            float decay = Mathf.Exp(-dt / StepHistoryTauSim);
            _stepSim30 *= decay;
            _stepSim60 *= decay;
            if (Time.fixedDeltaTime > (BaseStep60Hz + BaseStep30Hz) * 0.5f) _stepSim30 += dt;
            else _stepSim60 += dt;
            _stepHistorySim += dt;
        }

        /// <summary>Forget the step history, on leaving a mission.</summary>
        internal static void ResetPhysicsStep()
        {
            _stepSim30 = _stepSim60 = _stepHistorySim = 0f;
            _stepLastSim = -1f;
        }

        /// <summary>
        /// The physics step a ship will move at over a flight, for
        /// <see cref="TargetMapVelocity(ObjectBase, out float)"/>. Not the live step: the game drops to 1/30
        /// s whenever the frame rate dips under 30 (every mission load, any busy moment), and the rounding
        /// differs completely between the two steps. So the base step is the one held for most of the last
        /// minute of sim time, 1/60 s until 20 s have been seen. The compression scaling above the physics
        /// cap (<c>Time.fixedDeltaTime * TimeCompression / cap</c>) comes from the live values, since
        /// compression is a setting.
        /// </summary>
        internal static float PredictedPhysicsStep()
        {
            float baseStep = _stepHistorySim >= StepHistoryMinSim && _stepSim30 > _stepSim60
                           ? BaseStep30Hz : BaseStep60Hz;
            float live = Time.fixedDeltaTime;
            float scale = live > 0f ? GameTime.fixedDeltaTime / live : 1f;
            return baseStep * Mathf.Max(scale, 1f);
        }

        /// <summary>
        /// The evasive-target speed boost the game applies before simulating a shot: push the target's
        /// velocity away from the shooter by <see cref="EvasiveBoostFraction"/> of its own speed, then
        /// renormalize, so it turns without adding speed. No-op for a stationary target or ammo that does not
        /// assume evasion. Main thread only (reads <c>ap.AssumeEvasiveTarget</c>).
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

        // Every order of a strike holds an entry at once, keyed by shooter, ammunition and target, so the
        // room is sized well past any strike.
        private const int EstimateCacheCapacity = 4096;
        private static readonly TtlCache<TofKey, float> _cache =
            new TtlCache<TofKey, float>(CacheTtlSeconds, EstimateCacheCapacity);
        // Straight-line fallback results (kinematic sim unavailable) get their own cache so a
        // per-frame caller doesn't recompute and re-register a profiling "miss" every frame.
        private static readonly TtlCache<TofKey, float> _fallbackCache =
            new TtlCache<TofKey, float>(CacheTtlSeconds, EstimateCacheCapacity);

        // Display-only memo in front of Estimate: the panel asks for an ETA per visible row per frame, and at
        // the 0.5 s firing TTL some of those would run a full integration every frame to draw a label. It
        // returns exactly what Estimate returns, up to DisplayCacheTtlSeconds stale (invisible in text
        // rounded to the second); firing decisions never read it.
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

            // The kinematic tier ran and declined, so this call paid for a full integration. Leave
            // _lastCallWasHit false (Kinematic set it) so the profiler does not count that integration as a
            // cache hit; see CoordinatorProfiler.CountCachedHit.
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
        /// One rule produces every timing correction for a moving shooter: <b>pair a launch time with a
        /// flight estimate valid at that launch time</b>. <see cref="Estimate"/> reads the shooter's current
        /// <c>transform.position</c>; for an aircraft closing at 540 kn that answer walks earlier by the
        /// distance flown, and a strike's shared impact with it. The rail heading is carried because the
        /// platform turns before the prediction re-runs; pitch is read from the live rail, since an elevation
        /// in the platform's own frame does not drift.
        /// </summary>
        internal readonly struct LaunchState
        {
            internal readonly bool    Valid;
            internal readonly Vector3 PosU;         // shooter position at launch, Unity units
            internal readonly float   VelKnots;     // shooter speed at launch
            internal readonly Vector3 HeadingFlat;  // rail bearing at launch, flat; zero = unknown
            /// <summary>
            /// Where the target was when this shot was taken; zero means its live position, the normal case.
            /// Set only by the retargeting diagnostic, which back-projects the position of the ship a round
            /// actually hit from where it was at impact; the live position would bias the answer long by the
            /// ship's whole travel.
            /// </summary>
            internal readonly Vector3 TargetPosU;

            internal LaunchState(Vector3 posU, float velKnots, Vector3 headingFlat)
                : this(posU, velKnots, headingFlat, Vector3.zero) { }

            internal LaunchState(Vector3 posU, float velKnots, Vector3 headingFlat, Vector3 targetPosU)
            {
                Valid = true;
                PosU = posU;
                VelKnots = Mathf.Max(velKnots, 0f);
                HeadingFlat = headingFlat;
                TargetPosU = targetPosU;
            }
        }

        /// <summary>
        /// <see cref="Estimate"/> for a round that has already launched: the same integrator, run from the
        /// recorded launch state. Uncached: the answer is a constant of one launch, which the caller
        /// memoises, and the shared cache is keyed on the live shooter. The lower tiers take no origin
        /// override, so this declines (-1) rather than answer from a cruder model; the caller's live
        /// estimate, a real integration from the wrong point, beats a straight-line floor from the right one.
        /// The plausibility floor is measured from the recorded position.
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
            NoteSyncWrite(key);
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
                Vector3 targetVel = TargetMapVelocity(target);
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
        private static bool _interceptTimeLookedUp;   // the result type is fixed per session, so is a miss

        /// <summary>
        /// Fraction of the straight-line, top-speed flight time below which a result is broken rather than
        /// slow: a real shot is never faster than range over max speed, so this bound comes from the physics,
        /// not tuning.
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
        /// <see cref="MinValidSeconds"/> (0.01 s) only means "the model declined", so an absurdly short
        /// answer would otherwise reach the coordinator as a flight time and steal the anchor role or set the
        /// shared impact in the past.
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
                Vector3 targetVel = TargetMapVelocity(target);
                bool evasive = ap.AssumeEvasiveTarget(target);
                if (!_maxRangeLookedUp)
                {
                    _maxRangeLookedUp = true;
                    _maxRangePreciseMethod = typeof(AmmunitionParameters).GetMethod("MaxRangePrecise", new Type[] { typeof(ObjectBase), typeof(Vector3), typeof(Vector3), typeof(int), typeof(bool) });
                }
                if (_maxRangePreciseMethod == null) return -1f;
                object krObj = _maxRangePreciseMethod.Invoke(ap, new object[] { unit, targetPos, targetVel, 0, evasive });
                if (krObj == null) return -1f;
                if (!_interceptTimeLookedUp)
                {
                    _interceptTimeLookedUp = true;
                    _interceptTimeField = krObj.GetType().GetField("InterceptTime");
                }
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
