using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Port of the PUBLIC-branch unified flight simulator <c>Missile.SimulateShotLinear</c> onto the
    /// BETA branch, via reflection. The public game had ONE model for every missile: a waypoint
    /// flight-plan (loft / cruise / terminal) flown with PN guidance + drag — no per-class special
    /// cases. Beta removed <c>SimulateShotLinear</c> and its private glue
    /// (<c>CreateSimulationWaypoints</c>/<c>UpdateSimulationWaypointContexts</c>/
    /// <c>SimulationWaypointState</c>) but KEPT the machinery it stands on, all public:
    /// <c>Missile.CreateWaypointConfigs</c>, <c>Waypoint</c> + nested <c>Settings</c>/<c>Context</c>/
    /// <c>Status</c>/<c>DistanceToTargetWaypoint</c>, <c>Waypoint.UpdateAndGetActiveWaypoint</c>,
    /// <c>ComputePN</c>, <c>CalculateDrag</c>, <c>CalculateThrustOverTime</c>. We reconstruct the thin
    /// glue and re-drive the loop.
    ///
    /// The loft waypoint carries <c>PitchMode = KappaLoft</c> and
    /// <c>AllowExceedingAngleLimits = (Kinematics != 0)</c> — the game's OWN mechanism for a kinematic
    /// missile to legitimately overshoot <c>MaxLoftAlt</c>. If a faithful port reproduces yj-20's
    /// ~1425u apex natively, it retires the hand-built region model + vacuum brake + -40 deg heuristic
    /// + TerminalLoft glide in <see cref="FlightTime"/> (see docs/plans/WAYPOINT-SIM-PORT.md).
    ///
    /// PHASE 1 (this file so far): the SPIKE — <see cref="TryLogWaypointCommand"/> builds the waypoint
    /// plan and logs the commanded loft ALTITUDE vs the real track, to answer the make-or-break
    /// question before the full loop is ported. Everything is reflection + fail-soft: a missing handle
    /// just disables the spike (logged once), never throws into the estimator.
    ///
    /// Reflection is used for the game METHODS/CTORS (matching FlightTime.EnsureSimLookup's style and
    /// preserving the one-DLL-runs-on-both-branches guarantee). Plain <c>AmmunitionParameters</c>
    /// fields are read with direct typed access (same as FlightTime).
    /// </summary>
    internal static class WaypointSim
    {
        internal const float KU = 0.0076554087f;  // knots -> Unity units/s (game constant)
        private const float Rad2DegF = 57.29578f;

        private static bool _lookedUp;
        internal static bool Ready { get; private set; }

        // Types (resolved by full name off the game assembly).
        private static Type _tWpCreateCtx;   // Missile+WaypointCreationContext (public class)
        private static Type _tWpConfig;      // Missile+WaypointConfig (public struct)
        private static Type _tWaypoint;      // SeaPower.Waypoint (public class)
        private static Type _tWpSettings;    // Waypoint+Settings (public class)
        private static Type _tWpContext;     // Waypoint+Context (public struct)
        private static Type _tWpStatus;      // Waypoint+Status (public struct)
        private static Type _tDistWp;        // Waypoint+DistanceToTargetWaypoint (public class : IWaypoint)
        private static Type _tGeoPos;        // SeaPower.GeoPosition
        private static Type _tListWaypoint;  // List<Waypoint>

        // Methods / ctors.
        private static MethodInfo _mCreateConfigs;   // Missile.CreateWaypointConfigs(ctx) -> List<WaypointConfig>
        private static MethodInfo _mUpdateActive;    // Waypoint.UpdateAndGetActiveWaypoint(ref list, ref last, target, dt)
        private static MethodInfo _mUpdateContext;   // Waypoint.UpdateContext(Context)
        private static MethodInfo _mGeoToUnity;      // GeoPosition.ToUnity() -> Vector3
        private static ConstructorInfo _ctorGeoFromVec;  // GeoPosition(Vector3)
        private static ConstructorInfo _ctorDistWp;      // DistanceToTargetWaypoint(float, bool)
        private static ConstructorInfo _ctorWaypoint;    // Waypoint(IWaypoint, Settings, Context)

        // Fields — WaypointCreationContext.
        private static FieldInfo _fCtxAp, _fCtxIsAir, _fCtxTerrain, _fCtxGroupLeader,
            _fCtxStartAngle, _fCtxPopUpDisabled, _fCtxLaunchAlt, _fCtxLoftOverride;
        // Fields — WaypointConfig.
        private static FieldInfo _fCfgDist, _fCfgSettings;
        // Fields — Waypoint.
        private static FieldInfo _fWpStatus, _fWpSettings;
        // Fields — Status.
        private static FieldInfo _fStDesiredPos, _fStDone;
        // Fields — Settings.
        private static FieldInfo _fSetTargetSpeed, _fSetLoftHeight, _fSetPitchMode, _fSetEndHeight, _fSetSkipNext;
        // Fields — HeightSettings (EndHeight / LoftHeight element).
        private static FieldInfo _fHsAltitude, _fHsHeightType;
        // Fields — Context.
        private static FieldInfo _fCoGeo, _fCoUnity, _fCoVel, _fCoFlatVel, _fCoStall, _fCoMotor, _fCoLastWp;

        // --- Phase 2/3 (full loop port). Resolved best-effort; a miss leaves FullReady false so
        // EndTime no-ops (spike/Ready unaffected). Wired as the middle fallback tier in
        // FlightTime.KinematicRaw (integrator -> WaypointSim.EndTime -> legacy EstimateShot); the
        // grounded integrator stays primary. All on MissileSimulator except the two seed methods.
        internal static bool FullReady { get; private set; }
        private static MethodInfo _mComputePN;    // MissileSimulator.ComputePN(Vector3,Vector3,Vector3,float)->Vector3
        private static MethodInfo _mThrust;       // MissileSimulator.CalculateThrustOverTime(ap,bool,float,float)->float
        private static MethodInfo _mDrag;         // MissileSimulator.CalculateDrag (10-arg)
        private static MethodInfo _mAccelTimes;   // MissileSimulator.GetAccelerationTimes(ap,bool)->(float,float)
        private static MethodInfo _mAnalytical;   // Missile.CalculateMissileInterceptTimeAnalytical(...)->(float,bool,Vector3)
        private static MethodInfo _mSimple;       // Missile.CalculateMissileInterceptTimeSimple(...)->float

        /// <summary>
        /// Resolve every reflection handle once. Called from <see cref="FlightTime.EnsureSimLookup"/>
        /// after it confirms the beta branch, with the game assembly. Fail-soft: any miss leaves
        /// <see cref="Ready"/> false and the spike/port simply no-ops.
        /// </summary>
        internal static void EnsureLookup(Assembly gameAsm)
        {
            if (_lookedUp) return;
            _lookedUp = true;
            try
            {
                _tWpCreateCtx = gameAsm.GetType("SeaPower.Missile+WaypointCreationContext");
                _tWpConfig = gameAsm.GetType("SeaPower.Missile+WaypointConfig");
                _tWaypoint = gameAsm.GetType("SeaPower.Waypoint");
                _tWpSettings = gameAsm.GetType("SeaPower.Waypoint+Settings");
                _tWpContext = gameAsm.GetType("SeaPower.Waypoint+Context");
                _tWpStatus = gameAsm.GetType("SeaPower.Waypoint+Status");
                _tDistWp = gameAsm.GetType("SeaPower.Waypoint+DistanceToTargetWaypoint");
                _tGeoPos = gameAsm.GetType("SeaPower.GeoPosition");
                if (_tWpCreateCtx == null || _tWpConfig == null || _tWaypoint == null ||
                    _tWpSettings == null || _tWpContext == null || _tWpStatus == null ||
                    _tDistWp == null || _tGeoPos == null)
                { LogInitFail("type"); return; }

                _tListWaypoint = typeof(List<>).MakeGenericType(_tWaypoint);

                const BindingFlags PI = BindingFlags.Public | BindingFlags.Instance;
                const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;

                _mCreateConfigs = typeof(Missile).GetMethod("CreateWaypointConfigs", PS, null,
                    new[] { _tWpCreateCtx }, null);
                // UpdateAndGetActiveWaypoint has a 3-arg and a 4-arg overload; we want the 4-arg
                // (…, float simulationDt). Its first two params are by-ref, so match by name+count.
                _mUpdateActive = FindStaticByNameCount(_tWaypoint, "UpdateAndGetActiveWaypoint", 4);
                _mUpdateContext = _tWaypoint.GetMethod("UpdateContext", PI, null, new[] { _tWpContext }, null);
                _mGeoToUnity = _tGeoPos.GetMethod("ToUnity", PI, null, Type.EmptyTypes, null);
                _ctorGeoFromVec = _tGeoPos.GetConstructor(new[] { typeof(Vector3) });
                _ctorDistWp = _tDistWp.GetConstructor(new[] { typeof(float), typeof(bool) });
                _ctorWaypoint = FindWaypointCtor();

                if (_mCreateConfigs == null || _mUpdateActive == null || _mUpdateContext == null ||
                    _mGeoToUnity == null || _ctorGeoFromVec == null || _ctorDistWp == null ||
                    _ctorWaypoint == null)
                { LogInitFail("method/ctor"); return; }

                _fCtxAp = _tWpCreateCtx.GetField("Ap", PI);
                _fCtxIsAir = _tWpCreateCtx.GetField("IsAirTarget", PI);
                _fCtxTerrain = _tWpCreateCtx.GetField("TerrainFollow", PI);
                _fCtxGroupLeader = _tWpCreateCtx.GetField("IsGroupLeader", PI);
                _fCtxStartAngle = _tWpCreateCtx.GetField("StartAngle", PI);
                _fCtxPopUpDisabled = _tWpCreateCtx.GetField("SelectivePopUpDisabled", PI);
                _fCtxLaunchAlt = _tWpCreateCtx.GetField("LaunchAltUnity", PI);
                _fCtxLoftOverride = _tWpCreateCtx.GetField("LoftAltUnityOverride", PI);

                _fCfgDist = _tWpConfig.GetField("DistanceToTarget", PI);
                _fCfgSettings = _tWpConfig.GetField("Settings", PI);

                _fWpStatus = _tWaypoint.GetField("WpStatus", PI);
                _fWpSettings = _tWaypoint.GetField("WpSettings", PI);

                _fStDesiredPos = _tWpStatus.GetField("DesiredPosition", PI);
                _fStDone = _tWpStatus.GetField("Done", PI);

                _fSetTargetSpeed = _tWpSettings.GetField("TargetSpeedKnots", PI);
                _fSetLoftHeight = _tWpSettings.GetField("LoftHeight", PI);
                _fSetPitchMode = _tWpSettings.GetField("PitchMode", PI);
                _fSetEndHeight = _tWpSettings.GetField("EndHeight", PI);
                _fSetSkipNext = _tWpSettings.GetField("SkipNextWaypointOnComplete", PI);
                Type tHs = gameAsm.GetType("SeaPower.Waypoint+HeightSettings");
                if (tHs != null)
                {
                    _fHsAltitude = tHs.GetField("Altitude", PI);
                    _fHsHeightType = tHs.GetField("HeightType", PI);
                }

                _fCoGeo = _tWpContext.GetField("GeoPosition", PI);
                _fCoUnity = _tWpContext.GetField("UnityPosition", PI);
                _fCoVel = _tWpContext.GetField("VelocityVector", PI);
                _fCoFlatVel = _tWpContext.GetField("FlatVelocity", PI);
                _fCoStall = _tWpContext.GetField("StallSpeedKnots", PI);
                _fCoMotor = _tWpContext.GetField("MotorBurning", PI);
                _fCoLastWp = _tWpContext.GetField("LastWaypoint", PI);

                if (_fCtxAp == null || _fCfgDist == null || _fCfgSettings == null ||
                    _fWpStatus == null || _fWpSettings == null || _fStDesiredPos == null ||
                    _fSetTargetSpeed == null || _fCoGeo == null)
                { LogInitFail("field"); return; }

                Ready = true;

                // Phase 2 full-loop handles (additive; independent of the spike surface above).
                Type ms = gameAsm.GetType("SeaPower.MissileSimulator");
                if (ms != null)
                {
                    _mComputePN = ms.GetMethod("ComputePN", PS, null,
                        new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float) }, null);
                    _mThrust = ms.GetMethod("CalculateThrustOverTime", PS, null,
                        new[] { typeof(AmmunitionParameters), typeof(bool), typeof(float), typeof(float) }, null);
                    _mDrag = ms.GetMethod("CalculateDrag", PS, null, new[]
                    {
                        typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                        typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float)
                    }, null);
                    _mAccelTimes = ms.GetMethod("GetAccelerationTimes", PS, null,
                        new[] { typeof(AmmunitionParameters), typeof(bool) }, null);
                }
                _mAnalytical = typeof(Missile).GetMethod("CalculateMissileInterceptTimeAnalytical", PS, null,
                    new[] { typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3), typeof(float) }, null);
                _mSimple = typeof(Missile).GetMethod("CalculateMissileInterceptTimeSimple", PS, null,
                    new[] { typeof(AmmunitionParameters), typeof(Vector3), typeof(Vector3), typeof(Vector3) }, null);
                FullReady = _mComputePN != null && _mThrust != null && _mDrag != null &&
                            _mAccelTimes != null && _mAnalytical != null && _mSimple != null;

                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] wp-init: waypoint surface resolved (Ready=True, fullLoop={FullReady}; " +
                        $"pn {_mComputePN != null}, thrust {_mThrust != null}, drag {_mDrag != null}, " +
                        $"accel {_mAccelTimes != null}, analytical {_mAnalytical != null}, simple {_mSimple != null})");
            }
            catch (Exception e)
            {
                Ready = false;
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] wp-init: exception resolving waypoint surface: {e.Message}");
            }
        }

        private static void LogInitFail(string stage)
        {
            Ready = false;
            if (Coordinator.VerboseLog)
                Bootstrap.Log.LogWarning($"[AutoTOT] wp-init: FAILED at {stage} resolution — spike disabled");
        }

        private static MethodInfo FindStaticByNameCount(Type t, string name, int paramCount)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (MethodInfo m in t.GetMethods(F))
                if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            return null;
        }

        // Waypoint(IWaypoint wp, Settings settings, Context context) — pick the 3-arg ctor whose
        // 2nd/3rd params are Settings/Context (the other 3-arg ctor takes an ObjectBase 2nd param).
        private static ConstructorInfo FindWaypointCtor()
        {
            foreach (ConstructorInfo c in _tWaypoint.GetConstructors())
            {
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length == 3 && ps[1].ParameterType == _tWpSettings && ps[2].ParameterType == _tWpContext)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// SPIKE: build the waypoint flight-plan for this (shooter, ammo, target) and march a
        /// lightweight straight path, logging the commanded loft ALTITUDE (the height the active
        /// waypoint wants) every ~15s as a <c>wp-cmd</c> line. This is the make-or-break check:
        /// does the KappaLoft waypoint command yj-20's loft up toward ~1425u (overshoot), or cap at
        /// MaxLoftAlt (~1190u)? No physics/PN — only the waypoint-command half of the loop. VerboseLog.
        /// </summary>
        internal static void TryLogWaypointCommand(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            if (!Coordinator.VerboseLog) return;
            if (!Ready || unit == null || ap == null || target == null) return;
            try
            {
                Vector3 launchPos = unit.transform.position;
                Vector3 targetPos = target.transform.position;
                bool isAir = unit.IsAirUnit;
                float startVelKnots = Mathf.Max(unit._velocityInKnots, 0f);
                float cruiseKn = ap._maxVelocityInKnots > 1f ? ap._maxVelocityInKnots : 300f;
                float maxFlight = ap._maxFlightTime > 0f ? ap._maxFlightTime : 600f;
                float startAngleDeg = ap._maxLoftAngle > 0.5f ? ap._maxLoftAngle : 20f;
                string ammoLabel = ap._ammunitionFileName + "#" + unit.GetInstanceID();

                // Build the waypoint list (reconstructed CreateSimulationWaypoints).
                if (!TryBuildWaypoints(ap, launchPos, targetPos, startAngleDeg, isAir,
                        out IList waypoints, out object lastWp))
                {
                    Bootstrap.Log.LogWarning($"[AutoTOT] wp-cmd {ammoLabel}: build failed");
                    return;
                }

                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] wp-cmd {ammoLabel}: plan built, {waypoints.Count} waypoints, " +
                    $"startAngle {startAngleDeg:0.0}deg, cruise {cruiseKn:0}kn");

                Vector3 pos = launchPos;
                Vector3 horiz = new Vector3(targetPos.x - launchPos.x, 0f, targetPos.z - launchPos.z);
                if (horiz.sqrMagnitude < 1e-6f) return;
                Vector3 horizDir = horiz.normalized;
                float totalBurn = ap.TotalBurnTime;
                object geoTarget = _ctorGeoFromVec.Invoke(new object[] { targetPos });

                float nextLog = 0f;
                const float dt = 0.5f;
                for (float t = 0f; t < maxFlight; t += dt)
                {
                    // Straight flat march at cruise speed as a position stand-in (the spike observes
                    // the COMMANDED altitude, which depends on horizontal progress, not our own climb).
                    Vector3 velVec = horizDir * (cruiseKn * KU);
                    UpdateContexts(waypoints, pos, velVec, ap.MinVelocity, t < totalBurn);

                    object[] args = { waypoints, lastWp, geoTarget, dt };
                    _mUpdateActive.Invoke(null, args);
                    waypoints = (IList)args[0];
                    lastWp = args[1];
                    if (waypoints.Count == 0) break;

                    object active = waypoints[0];
                    float cmdAlt = ReadDesiredAltUnity(active);
                    if (t >= nextLog)
                    {
                        object settings = _fWpSettings.GetValue(active);
                        object pitchMode = settings != null ? _fSetPitchMode.GetValue(settings) : null;
                        float tgtSpd = settings != null ? (float)_fSetTargetSpeed.GetValue(settings) : 0f;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] wp-cmd {ammoLabel}: t+{t:0}s cmdAlt {cmdAlt:0.0}u alt {pos.y:0.0}u " +
                            $"flat {Flat(pos, targetPos):0}u pitch {pitchMode} tgtSpd {tgtSpd:0}kn wps {waypoints.Count}");
                        nextLog += 15f;
                    }

                    pos += horizDir * (cruiseKn * KU * dt);
                    if (Flat(pos, targetPos) < 3f) break;
                }
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] wp-cmd: exception {e.GetType().Name}: {e.Message}");
            }
        }

        // Reconstruct CreateSimulationWaypoints (public Missile.cs:2320-2351) via reflection.
        private static bool TryBuildWaypoints(AmmunitionParameters ap, Vector3 launchPos, Vector3 targetPos,
            float startAngleDeg, bool isAir, out IList waypoints, out object lastWp)
        {
            waypoints = null; lastWp = null;

            object ctx = Activator.CreateInstance(_tWpCreateCtx);
            _fCtxAp.SetValue(ctx, ap);
            _fCtxIsAir.SetValue(ctx, ap._targetType == Ammunition.Target.AAW);
            _fCtxTerrain.SetValue(ctx, ap._terrainFollowFlight);
            _fCtxGroupLeader.SetValue(ctx, false);
            _fCtxStartAngle.SetValue(ctx, startAngleDeg);
            _fCtxPopUpDisabled.SetValue(ctx, false);
            _fCtxLaunchAlt.SetValue(ctx, launchPos.y);
            _fCtxLoftOverride.SetValue(ctx, -1f);

            object configsObj = _mCreateConfigs.Invoke(null, new[] { ctx });
            if (!(configsObj is IList configs) || configs.Count == 0) return false;

            // Initial Waypoint.Context seeded at launch (boxed struct; set fields on the box).
            object launchGeo = _ctorGeoFromVec.Invoke(new object[] { launchPos });
            object initCtx = Activator.CreateInstance(_tWpContext);
            _fCoGeo.SetValue(initCtx, launchGeo);
            _fCoUnity.SetValue(initCtx, launchPos);
            _fCoVel.SetValue(initCtx, Vector3.zero);
            _fCoFlatVel.SetValue(initCtx, 0f);

            waypoints = (IList)Activator.CreateInstance(_tListWaypoint);
            foreach (object cfg in configs)
            {
                float dist = (float)_fCfgDist.GetValue(cfg);
                object settings = _fCfgSettings.GetValue(cfg);
                object iwp = _ctorDistWp.Invoke(new object[] { dist, true });
                object wp = _ctorWaypoint.Invoke(new[] { iwp, settings, initCtx });
                waypoints.Add(wp);
            }
            lastWp = launchGeo;
            return true;
        }

        // Reconstruct UpdateSimulationWaypointContexts (public Missile.cs:2353-2367).
        private static void UpdateContexts(IList waypoints, Vector3 pos, Vector3 vel, float stallKn, bool motorBurning)
        {
            object geo = _ctorGeoFromVec.Invoke(new object[] { pos });
            object ctx = Activator.CreateInstance(_tWpContext);
            _fCoGeo.SetValue(ctx, geo);
            _fCoUnity.SetValue(ctx, pos);
            _fCoVel.SetValue(ctx, vel);
            _fCoFlatVel.SetValue(ctx, vel.magnitude);
            _fCoStall.SetValue(ctx, stallKn);
            _fCoMotor.SetValue(ctx, motorBurning);
            for (int i = 0; i < waypoints.Count; i++)
                _mUpdateContext.Invoke(waypoints[i], new[] { ctx });
        }

        // active.WpStatus.DesiredPosition.ToUnity().y  (all boxed-struct field reads).
        private static float ReadDesiredAltUnity(object waypoint)
        {
            object status = _fWpStatus.GetValue(waypoint);
            object desired = _fStDesiredPos.GetValue(status);
            Vector3 u = (Vector3)_mGeoToUnity.Invoke(desired, null);
            return u.y;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // active.WpStatus.DesiredPosition.ToUnity()  (full Vector3).
        private static Vector3 ReadDesiredUnity(object waypoint)
        {
            object status = _fWpStatus.GetValue(waypoint);
            object desired = _fStDesiredPos.GetValue(status);
            return (Vector3)_mGeoToUnity.Invoke(desired, null);
        }

        // Mark waypoints[0].WpStatus.Done = true (boxed-struct write-back), for the loftTooHigh escape.
        private static void SetActiveDone(object waypoint)
        {
            object status = _fWpStatus.GetValue(waypoint);
            _fStDone.SetValue(status, true);
            _fWpStatus.SetValue(waypoint, status);
        }

        /// <summary>
        /// PHASE 2: the full ported flight sim (public <c>Missile.SimulateShotLinear</c>,
        /// Missile.cs:2371-2605). Flies the game's own waypoint plan with PN guidance + turn-rate
        /// limit + <c>CalculateDrag</c>, in Unity space. Returns intercept time in seconds, or -1 when
        /// non-beta / a handle is missing / the sim never closes / stalls (→ caller falls back).
        /// Variable names track the decompile (num13 = t, num4 = knots, vector2 = pos, vector4 = dir).
        /// </summary>
        internal static float EndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            if (!Ready || !FullReady || unit == null || ap == null || target == null) return -1f;
            try
            {
                Vector3 launchPosition = unit.transform.position;
                Vector3 targetPosition = target.transform.position;
                Vector3 targetVelocityVector = target._velocityVecInUnity;
                bool isAirUnit = unit.IsAirUnit;
                float startVelocityKnots = Mathf.Max(unit._velocityInKnots, 0f);

                // Evasive-target boost (public 2375-2383).
                float magnitude = targetVelocityVector.magnitude;
                if (magnitude > 0f && ap.AssumeEvasiveTarget(target))
                {
                    Vector3 v = targetPosition - launchPosition; v.y = 0f;
                    if (v.sqrMagnitude > 1e-8f)
                    {
                        targetVelocityVector += v.normalized * (magnitude * 0.8f);
                        targetVelocityVector = targetVelocityVector.normalized * Mathf.Min(targetVelocityVector.magnitude, magnitude);
                    }
                }

                float num = Flat(launchPosition, targetPosition);
                if (num < 0.001f) return 0f;
                float dragFactor = ap.GetDragFactor(isAirUnit);
                float num2 = ap._maxFlightTime > 0f ? ap._maxFlightTime : 600f;

                var at = (ValueTuple<float, float>)_mAccelTimes.Invoke(null, new object[] { ap, isAirUnit });
                float boostEnd = at.Item1 + Mathf.Max(0f, at.Item2);

                float num3;
                var atup = (ValueTuple<float, bool, Vector3>)_mAnalytical.Invoke(null,
                    new object[] { ap, launchPosition, startVelocityKnots, targetPosition, targetVelocityVector, dragFactor });
                num3 = atup.Item1;
                if (num3 < 0f)
                    num3 = (float)_mSimple.Invoke(null, new object[] { ap, launchPosition, targetPosition, targetVelocityVector });

                // Build the game's waypoint plan (aim = lead position), startAngle = _maxLoftAngle.
                float startAngleDeg = ap._maxLoftAngle;
                Vector3 aim = targetPosition + targetVelocityVector * num3;
                if (!TryBuildWaypoints(ap, launchPosition, aim, startAngleDeg, isAirUnit,
                        out IList waypoints, out object lastWp))
                    return -1f;

                Vector3 vector2 = launchPosition;
                float num4 = startVelocityKnots;
                float num5 = float.MaxValue;
                Vector3 vector3 = new Vector3(targetPosition.x - launchPosition.x, 0f, targetPosition.z - launchPosition.z);
                Vector3 vector4 = vector3.sqrMagnitude > 0.0001f ? vector3.normalized : Vector3.forward;
                float num6 = float.MaxValue;
                float num7 = 0f;
                float num8 = 0f;
                float num9 = ap._deceleration * 9.8f * 1.94384f;
                Vector3 vector6 = targetPosition;
                float num10 = Flat(vector2, vector6);
                float num11 = vector6.y - vector2.y;
                float num12 = Mathf.Sqrt(num10 * num10 + num11 * num11);
                Vector3 rhs = targetPosition - launchPosition; rhs.y = 0f;
                if (rhs.sqrMagnitude > 0.001f) rhs.Normalize();
                bool receding = Vector3.Dot(targetVelocityVector, rhs) < 0f;

                float navGain = ap._navigationGain;
                float turnRateRad = ap._maxTurnRateDegrees * (Mathf.PI / 180f);
                float launchRangeY = Mathf.Max(ap._launchRangesInUnity.y, 1f);
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                float minVel = ap.MinVelocity;
                float liftFactor = ap.LiftFactor;
                float nextLog = 0f;

                for (float num13 = 0f; num13 < num2; num13 += 0.5f)
                {
                    float num14 = num4 * KU;
                    if (num10 < num6) { num6 = num10; num7 = num13; }

                    // Re-estimate intercept time num3 (public 2446-2476).
                    if (num13 > num3 / 6f)
                    {
                        Vector3 lhs = vector6 - vector2;
                        float num15 = num14;
                        if (num13 < ap.TotalBurnTime)
                            num15 = Mathf.Max(num15, KU * (ap.TotalDeltaV / 2f + startVelocityKnots));
                        float num16 = num15 * num15 * 0.8f - targetVelocityVector.sqrMagnitude;
                        float num17 = -2f * Vector3.Dot(lhs, targetVelocityVector);
                        float num18 = -lhs.sqrMagnitude;
                        float num19 = num17 * num17 - 4f * num16 * num18;
                        if (Mathf.Abs(num16) > 0.001f && num19 >= 0f)
                        {
                            float num20 = (-num17 + Mathf.Sqrt(num19)) / (2f * num16);
                            if (num20 > 0f) num3 = num13 + num20;
                            else if (num14 > 0.001f) num3 = num13 + num12 / num14;
                        }
                        else
                        {
                            float num21 = targetVelocityVector.sqrMagnitude * 0.8f;
                            float num22 = num21 > 0.001f
                                ? Mathf.Max(0f, -Vector3.Dot(lhs, targetVelocityVector) / num21)
                                : (num14 > 0.001f ? num12 / num14 : 0f);
                            num3 = num13 + num22;
                        }
                    }

                    Vector3 vector7 = targetPosition + targetVelocityVector * num3;
                    float num23 = Mathf.Clamp01(1f - Mathf.Pow(Flat(vector2, vector7) / launchRangeY, 2f));
                    Vector3 position = Vector3.Lerp(vector7, vector6, 1f - num23);
                    Vector3 velocity = vector4 * num14;
                    bool flag2 = num13 < boostEnd;

                    // Waypoint update (public 2482-2483).
                    UpdateContexts(waypoints, vector2, velocity, minVel, flag2);
                    object geoPos = _ctorGeoFromVec.Invoke(new object[] { position });
                    object[] wargs = { waypoints, lastWp, geoPos, 0.5f };
                    _mUpdateActive.Invoke(null, wargs);
                    waypoints = (IList)wargs[0];
                    lastWp = wargs[1];

                    // Heading via PN, clamped to turn rate (public 2484-2503).
                    Vector3 vector8 = vector4 * num14;
                    Vector3 relPos = vector6 - vector2;
                    Vector3 relVel = targetVelocityVector - vector8;
                    Vector3 vector9 = (Vector3)_mComputePN.Invoke(null, new object[] { vector8, relPos, relVel, navGain });
                    float num24 = turnRateRad * num14;
                    if (vector9.sqrMagnitude > num24 * num24) vector9 = vector9.normalized * num24;
                    Vector3 vector10 = vector8 + vector9 * 0.5f;
                    Vector3 vector11 = new Vector3(vector10.x, 0f, vector10.z);
                    if (vector11.sqrMagnitude < 0.0001f) vector11 = new Vector3(vector4.x, 0f, vector4.z);
                    if (vector11.sqrMagnitude < 0.0001f) vector11 = new Vector3(relPos.x, 0f, relPos.z);
                    vector11.Normalize();

                    // Pitch toward the active waypoint's commanded position (public 2504-2507).
                    Vector3 a = waypoints.Count > 0 ? ReadDesiredUnity(waypoints[0]) : vector7;
                    float num25 = Mathf.Clamp(Mathf.Atan2(a.y - vector2.y, Flat(a, vector2)), -1.5707961f, 1.5707961f);
                    vector4 = new Vector3(vector11.x * Mathf.Cos(num25), Mathf.Sin(num25), vector11.z * Mathf.Cos(num25));
                    float num26 = -num25 * Rad2DegF;

                    // Speed update — verbatim public branch (2508-2527).
                    if (nonKin)
                    {
                        float tgtSpd = 0f;
                        if (waypoints.Count > 0)
                        {
                            object settings = _fWpSettings.GetValue(waypoints[0]);
                            if (settings != null) tgtSpd = (float)_fSetTargetSpeed.GetValue(settings);
                        }
                        float num28 = tgtSpd > 0f ? tgtSpd : ap._maxVelocityInKnots;
                        if (num4 < num28)
                        {
                            float thrust = (float)_mThrust.Invoke(null, new object[] { ap, isAirUnit, num13, 0.5f });
                            num4 = Mathf.Min(num4 + thrust, num28);
                        }
                        else if (num4 > num28)
                        {
                            num4 = Mathf.Max(num4 - num9 * 0.5f, num28);
                        }
                    }
                    else
                    {
                        float thrust = (float)_mThrust.Invoke(null, new object[] { ap, isAirUnit, num13, 0.5f });
                        float drag = (float)_mDrag.Invoke(null, new object[]
                        { vector2.y, num14, 0.5f, num26, dragFactor, flag2, vector6.y, liftFactor, minVel, 0f });
                        num4 = Mathf.Max(num4 + thrust - drag, 0f);
                    }
                    if (!flag2) num5 = Mathf.Min(num5, num4);

                    // Intercept detection (public 2532-2554) — return only the time.
                    float num32 = num4 * KU;
                    if (num12 > 0.0001f)
                    {
                        Vector3 rhs2 = (vector6 - vector2) / num12;
                        if (num8 * 0.5f * 2f >= num12) return num13;
                        num8 = Vector3.Dot(vector4 * num32 - targetVelocityVector, rhs2);
                    }

                    // Advance (public 2555-2572).
                    vector2 += vector4 * (num32 * 0.5f);
                    vector6 = targetPosition + targetVelocityVector * (num13 + 0.5f);
                    if (vector6.y < 0f)
                    {
                        Vector3 vector12 = targetVelocityVector * (num13 + 0.5f);
                        float m3 = vector12.magnitude;
                        float num34 = -targetPosition.y;
                        float num35 = m3 * m3 - num34 * num34;
                        Vector3 vector13 = new Vector3(vector12.x, 0f, vector12.z);
                        float m4 = vector13.magnitude;
                        vector6 = (num35 > 0f && m4 > 0.0001f)
                            ? targetPosition + vector13 / m4 * Mathf.Sqrt(num35) + Vector3.up * num34
                            : new Vector3(targetPosition.x, 0f, targetPosition.z);
                    }
                    num10 = Flat(vector2, vector6);
                    num11 = vector6.y - vector2.y;
                    num12 = Mathf.Sqrt(num10 * num10 + num11 * num11);

                    if (Coordinator.VerboseLog && num13 >= nextLog)
                    {
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] wp-track {ap._ammunitionFileName}#{unit.GetInstanceID()}: t+{num13:0}s " +
                            $"spd {num4:0}kn alt {vector2.y:0.0}u pitch {num26:0.0} cmdAlt {a.y:0.0}u " +
                            $"flat {num10:0}u wps {waypoints.Count}");
                        nextLog += 15f;
                    }

                    // loftTooHigh escape + stall (public 2577-2604).
                    if (num13 > 1f && !flag2)
                    {
                        if (num4 < minVel + 200f && waypoints.Count > 0)
                        {
                            object settings = _fWpSettings.GetValue(waypoints[0]);
                            object loft = settings != null ? _fSetLoftHeight.GetValue(settings) : null;
                            if (loft != null) { SetActiveDone(waypoints[0]); continue; }
                        }
                        if (num4 < minVel) break;
                    }
                    if (nonKin && Flat(vector2, launchPosition) > ap._launchRangesInUnity.y) break;
                }

                if (num7 < 0.2f) return -1f;   // never closed
                return num7;                   // closest-approach time
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] wp-track: EndTime exception {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }

        /// <summary>
        /// Stage boundaries extracted from the game's own <c>CreateWaypointConfigs</c> flight plan
        /// (Phase 4). Used to GROUND the region model's hand-derived boundaries — currently just the
        /// loft-end distance (`FinalDist`), the single value whose `_loftToSkim`-aware derivation has
        /// bitten us before; the rest stay hand-derived (they equal the same ini fields). `Valid` is
        /// false if the plan couldn't be built or has no loft stage → caller keeps its hand values.
        /// </summary>
        internal struct StageBoundaries
        {
            public bool Valid;
            public float FinalDist;   // loft-end / cruise-entry distance-to-target (Unity units); -1 = n/a
            public float LoftAlt;     // loft config LoftHeight.Altitude (Unity units); -1 = n/a
            public float LoftVel;     // loft config TargetSpeedKnots; -1 = n/a
        }

        /// <summary>
        /// Build the game's waypoint plan for this ammo and extract the loft-stage boundary from it.
        /// Also logs a `stage-src` dump of every config (the parity instrument) when VerboseLog is on.
        /// Returns false (→ caller falls back to hand-derivation) on any reflection miss / no plan.
        /// </summary>
        internal static bool TryStageBoundaries(AmmunitionParameters ap, float launchAltUnity,
            float targetAltUnity, out StageBoundaries sb)
        {
            sb = new StageBoundaries { FinalDist = -1f, LoftAlt = -1f, LoftVel = -1f };
            if (!Ready || _fSetEndHeight == null || _fHsAltitude == null || ap == null) return false;
            try
            {
                object ctx = Activator.CreateInstance(_tWpCreateCtx);
                _fCtxAp.SetValue(ctx, ap);
                _fCtxIsAir.SetValue(ctx, ap._targetType == Ammunition.Target.AAW);
                _fCtxTerrain.SetValue(ctx, ap._terrainFollowFlight);
                _fCtxGroupLeader.SetValue(ctx, false);
                _fCtxStartAngle.SetValue(ctx, ap._maxLoftAngle);
                _fCtxPopUpDisabled.SetValue(ctx, false);
                _fCtxLaunchAlt.SetValue(ctx, launchAltUnity);
                _fCtxLoftOverride.SetValue(ctx, -1f);

                object configsObj = _mCreateConfigs.Invoke(null, new[] { ctx });
                if (!(configsObj is IList configs) || configs.Count == 0) return false;

                // The list is launch->target order (generator Reverse()s before returning). The loft
                // config is the one carrying LoftHeight — its DistanceToTarget is where the loft ends
                // and the cruise stage begins (= our region model's finalDist). Prefer the highest
                // DistanceToTarget loft config if several carry a LoftHeight (pop-up also can).
                bool verbose = Coordinator.VerboseLog;
                var dump = verbose ? new System.Text.StringBuilder() : null;
                for (int i = 0; i < configs.Count; i++)
                {
                    object cfg = configs[i];
                    float dist = (float)_fCfgDist.GetValue(cfg);
                    object settings = _fCfgSettings.GetValue(cfg);
                    if (settings == null) continue;
                    float spd = (float)_fSetTargetSpeed.GetValue(settings);
                    object pitchMode = _fSetPitchMode.GetValue(settings);
                    object endH = _fSetEndHeight.GetValue(settings);
                    float endAlt = ResolveHeight(endH, targetAltUnity);
                    object loftH = _fSetLoftHeight.GetValue(settings);
                    bool hasLoft = loftH != null;
                    float loftAlt = hasLoft ? ResolveHeight(loftH, targetAltUnity) : -1f;

                    if (hasLoft && dist >= sb.FinalDist)
                    {
                        sb.FinalDist = dist;
                        sb.LoftAlt = loftAlt;
                        sb.LoftVel = spd;
                        sb.Valid = true;
                    }
                    dump?.Append($" [{i}] dtt {dist:0} spd {spd:0} endAlt {endAlt:0.0} " +
                                 $"pitch {pitchMode}{(hasLoft ? $" loftAlt {loftAlt:0.0}" : "")}");
                }

                if (verbose)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] stage-src {ap._ammunitionFileName}: {configs.Count} cfgs;{dump}" +
                        $"  => finalDist {sb.FinalDist:0}u loftAlt {sb.LoftAlt:0.0}u loftVel {sb.LoftVel:0}kn " +
                        $"(valid {sb.Valid})");
                return sb.Valid;
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] stage-src: exception {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        // Resolve a boxed HeightSettings to a Unity-units altitude. Handles the common HeightTypes;
        // returns -1 (ambiguous → caller keeps its own value) for OnLineToTarget / layer-relative.
        // HeightType enum: 0 Absolute, 1 RelativeToTarget, 2 PercentageOfTarget, 3 AboveTerrain,
        // 4 AboveSeaLevel, 5 AboveLayer, 6 PercentFromLastWaypoint, 7 OnLineToTarget.
        private static float ResolveHeight(object heightSettings, float targetAltUnity)
        {
            if (heightSettings == null || _fHsHeightType == null) return -1f;
            float alt = (float)_fHsAltitude.GetValue(heightSettings);
            int ht = (int)_fHsHeightType.GetValue(heightSettings);
            switch (ht)
            {
                case 0: // Absolute
                case 4: // AboveSeaLevel  (sea-level datum == absolute over water)
                case 3: // AboveTerrain   (terrain == 0 over water)
                    return alt;
                case 1: // RelativeToTarget
                    return targetAltUnity + alt;
                default: // OnLineToTarget / PercentageOfTarget / AboveLayer / PercentFromLastWaypoint
                    return -1f;
            }
        }
    }
}
