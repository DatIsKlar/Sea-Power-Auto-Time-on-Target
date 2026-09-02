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
        // Straight-line fallback results (kinematic sim unavailable) get their own cache so a
        // per-frame caller doesn't recompute and re-register a profiling "miss" every frame.
        private static readonly TtlCache<TofKey, float> _fallbackCache = new TtlCache<TofKey, float>(CacheTtlSeconds);

        private static bool _lastCallWasHit;
        internal static bool WasLastCallCacheHit => _lastCallWasHit;

        internal static float Estimate(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (unit == null || target == null) return 0f;

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            AmmunitionParameters ap = ammo?._ap;
            if (ap == null) return 0f;

            float kinematic = Kinematic(unit, ap, target);
            if (kinematic > MinValidSeconds) return kinematic;

            TofKey key = new TofKey
            {
                UnitId = unit.GetInstanceID(),
                AmmoFile = ap._ammunitionFileName,
                TargetId = target.GetInstanceID(),
            };
            if (_fallbackCache.TryGet(key, out float cachedFallback))
            {
                _lastCallWasHit = true;
                return cachedFallback;
            }

            _lastCallWasHit = false;
            float speedMs = ap._maxVelocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= MinSpeedMs) return 0f;
            float fallback = GameUnits.MetersBetween(unit, target) / speedMs;
            _fallbackCache.Set(key, fallback);
            return fallback;
        }

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

        internal static void ClearCache() { _cache.Clear(); _fallbackCache.Clear(); _profileCache.Clear(); }

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

        private struct SpeedProfile { public float[] Times; public float[] Speeds; public Vector3[] Positions; }
        private static readonly TtlCache<TofKey, SpeedProfile> _profileCache =
            new TtlCache<TofKey, SpeedProfile>(CacheTtlSeconds);

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

        private static MethodInfo _maxRangePreciseMethod;
        private static bool _maxRangeLookedUp;
        private static FieldInfo _interceptTimeField;

        private static float KinematicRaw(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            float integrated = IntegratedEndTime(unit, ap, target);
            if (integrated > MinValidSeconds) return integrated;

            if (WaypointSim.Ready && WaypointSim.FullReady)
            {
                float wp = WaypointSim.EndTime(unit, ap, target);
                if (wp > MinValidSeconds) return wp;
            }
            return MaxRangePreciseEndTime(unit, ap, target);
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
                if (Coordinator.VerboseLog) Bootstrap.Log.LogWarning($"[AutoTOT] kinematic flight-time failed: {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }
    }
}
