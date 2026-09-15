using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Ported waypoint-sim fallback (middle tier): reconstructs the game's waypoint flight plan via
    /// reflection and flies it with PN guidance and drag. <c>FlightTime.Waypoints.cs</c> re-implements the
    /// same waypoint laws as pure functions for the estimator threads and the lab; this tier drives the
    /// game's own objects, which ties it to the main thread. The two stay apart because merging them would
    /// move the flight model.
    ///
    /// Only where the newer simulator binds: <see cref="EnsureLookup"/> is called only from that branch of
    /// <c>FlightTime.EnsureSimLookup</c>, so on the public branch <see cref="Ready"/> stays false and
    /// <see cref="EndTime"/> no-ops. It serves shots the integrator declines.
    /// </summary>
    internal static class WaypointSim
    {
        private const float KU = GameUnits.KnotsToUnityPerSecond; // knots -> Unity units/s
        // Pinned copies of Mathf.Rad2Deg and Mathf.PI/2, not the Unity constants: they differ in the last
        // digit, and both are read inside the step loop, so swapping them could move a result.
        private const float Rad2DegF = 57.29578f;

        private const float SimTimestep = 0.5f;
        private const float SqrEpsilon = 0.0001f;
        private const float MinFlatDist = 0.001f;
        private const float QuadraticEpsilon = 0.001f;
        private const float HalfPi = 1.5707961f;
        private const float LoftHighSpeedMargin = 200f;
        private const float MinCloseApproachTime = 0.2f;
        private const float LoftCheckMinSimTime = 1f;

        // Start re-estimating the intercept once the shot is this far into its first estimate
        // (public Missile.cs:2446). Before that the seed from the analytical solver is the better number.
        private const float InterceptReestimateFraction = 6f;

        // The intercept solver's margin, at both places the game applies it: the quadratic's a-term (public
        // Missile.cs:2455) and the closing-rate fallback beside it (:2471). Equal to
        // FlightTime.EvasiveBoostFraction by coincidence (that one scales a target's speed for evasion), so
        // kept separate.
        private const float InterceptSpeedMarginFactor = 0.8f;

        // Cadence of the verbose wp-track line; not TelemetryCadence, which pairs the integrator's sim-track
        // with the live track. A fallback-tier shot is sampled coarsely.
        private const float WpTrackLogIntervalSim = 15f;

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

        // Fields: WaypointCreationContext.
        private static FieldInfo _fCtxAp, _fCtxIsAir, _fCtxTerrain, _fCtxGroupLeader,
            _fCtxStartAngle, _fCtxPopUpDisabled, _fCtxLaunchAlt, _fCtxLoftOverride;
        // Fields: WaypointConfig.
        private static FieldInfo _fCfgDist, _fCfgSettings;
        // Fields: Waypoint.
        private static FieldInfo _fWpStatus, _fWpSettings;
        // Fields: Status.
        private static FieldInfo _fStDesiredPos, _fStDone;
        // Fields: Settings.
        private static FieldInfo _fSetTargetSpeed, _fSetLoftHeight;
        // Fields: Context.
        private static FieldInfo _fCoGeo, _fCoUnity, _fCoVel, _fCoFlatVel, _fCoStall, _fCoMotor;

        // The full loop port (FullReady), resolved best-effort beside the waypoint surface (Ready); a miss
        // leaves FullReady false so EndTime no-ops. The middle tier of FlightTime.KinematicRaw, after the
        // integrator. All on MissileSimulator except the two seed methods.
        internal static bool FullReady { get; private set; }
        private static MethodInfo _mComputePN;    // MissileSimulator.ComputePN(Vector3,Vector3,Vector3,float)->Vector3
        private static MethodInfo _mThrust;       // MissileSimulator.CalculateThrustOverTime(ap,bool,float,float)->float
        private static MethodInfo _mDrag;         // MissileSimulator.CalculateDrag (10-arg)
        private static MethodInfo _mAccelTimes;   // MissileSimulator.GetAccelerationTimes(ap,bool)->(float,float)
        private static MethodInfo _mAnalytical;   // Missile.CalculateMissileInterceptTimeAnalytical(...)->(float,bool,Vector3)
        private static MethodInfo _mSimple;       // Missile.CalculateMissileInterceptTimeSimple(...)->float

        /// <summary>
        /// Resolve every reflection handle once, from <see cref="FlightTime.EnsureSimLookup"/> with the game assembly.
        /// Fail-soft: any miss leaves <see cref="Ready"/> false and the tier no-ops.
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
                // UpdateAndGetActiveWaypoint has 3- and 4-arg overloads; take the 4-arg one (…, float
                // simulationDt), matched by name and count since its first two params are by-ref.
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

                _fCoGeo = _tWpContext.GetField("GeoPosition", PI);
                _fCoUnity = _tWpContext.GetField("UnityPosition", PI);
                _fCoVel = _tWpContext.GetField("VelocityVector", PI);
                _fCoFlatVel = _tWpContext.GetField("FlatVelocity", PI);
                _fCoStall = _tWpContext.GetField("StallSpeedKnots", PI);
                _fCoMotor = _tWpContext.GetField("MotorBurning", PI);

                // All 22, not just the ones the step loop dereferences first: an unchecked null would fail
                // later as a NullReferenceException inside EndTime's catch, reading as "the tier declined".
                // The name is reported so a rename is findable.
                string missing = FirstMissingField();
                if (missing != null) { LogInitFail("field " + missing); return; }

                Ready = true;

                // Full-loop handles, independent of the waypoint surface above.
                Type ms = gameAsm.GetType(FlightTime.MissileSimulatorType);
                if (ms != null)
                {
                    _mComputePN = ms.GetMethod("ComputePN", PS, null,
                        new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float) }, null);
                    _mThrust = ms.GetMethod(FlightTime.ThrustMethodName, PS, null,
                        new[] { typeof(AmmunitionParameters), typeof(bool), typeof(float), typeof(float) }, null);
                    _mDrag = ms.GetMethod(FlightTime.DragMethodName, PS, null, new[]
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

                if (Coordinator.TraceFlightModel)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] wp-init: waypoint surface resolved (Ready=True, fullLoop={FullReady}; " +
                        $"pn {_mComputePN != null}, thrust {_mThrust != null}, drag {_mDrag != null}, " +
                        $"accel {_mAccelTimes != null}, analytical {_mAnalytical != null}, simple {_mSimple != null})");
            }
            catch (Exception e)
            {
                Ready = false;
                if (Coordinator.TraceFlightModel)
                    Bootstrap.Log.LogWarning($"[AutoTOT] wp-init: exception resolving waypoint surface: {e.Message}");
            }
        }

        /// <summary>
        /// Name of the first reflected field that did not resolve, or null when all 22 are present.
        /// Ordered as they are read above so the report points at the first thing that moved.
        /// </summary>
        private static string FirstMissingField()
        {
            if (_fCtxAp == null) return "WaypointCreationContext.Ap";
            if (_fCtxIsAir == null) return "WaypointCreationContext.IsAirTarget";
            if (_fCtxTerrain == null) return "WaypointCreationContext.TerrainFollow";
            if (_fCtxGroupLeader == null) return "WaypointCreationContext.IsGroupLeader";
            if (_fCtxStartAngle == null) return "WaypointCreationContext.StartAngle";
            if (_fCtxPopUpDisabled == null) return "WaypointCreationContext.SelectivePopUpDisabled";
            if (_fCtxLaunchAlt == null) return "WaypointCreationContext.LaunchAltUnity";
            if (_fCtxLoftOverride == null) return "WaypointCreationContext.LoftAltUnityOverride";
            if (_fCfgDist == null) return "WaypointConfig.DistanceToTarget";
            if (_fCfgSettings == null) return "WaypointConfig.Settings";
            if (_fWpStatus == null) return "Waypoint.WpStatus";
            if (_fWpSettings == null) return "Waypoint.WpSettings";
            if (_fStDesiredPos == null) return "Waypoint.Status.DesiredPosition";
            if (_fStDone == null) return "Waypoint.Status.Done";
            if (_fSetTargetSpeed == null) return "Waypoint.Settings.TargetSpeedKnots";
            if (_fSetLoftHeight == null) return "Waypoint.Settings.LoftHeight";
            if (_fCoGeo == null) return "Waypoint.Context.GeoPosition";
            if (_fCoUnity == null) return "Waypoint.Context.UnityPosition";
            if (_fCoVel == null) return "Waypoint.Context.VelocityVector";
            if (_fCoFlatVel == null) return "Waypoint.Context.FlatVelocity";
            if (_fCoStall == null) return "Waypoint.Context.StallSpeedKnots";
            if (_fCoMotor == null) return "Waypoint.Context.MotorBurning";
            return null;
        }

        private static void LogInitFail(string stage)
        {
            Ready = false;
            if (Coordinator.TraceFlightModel)
                Bootstrap.Log.LogWarning($"[AutoTOT] wp-init: FAILED at {stage} resolution, spike disabled");
        }

        private static MethodInfo FindStaticByNameCount(Type t, string name, int paramCount)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (MethodInfo m in t.GetMethods(F))
                if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            return null;
        }

        // Waypoint(IWaypoint wp, Settings settings, Context context): the 3-arg ctor whose 2nd and 3rd params
        // are Settings and Context (the other takes an ObjectBase second).
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

        // Reconstruct CreateSimulationWaypoints (public Missile.cs:2320-2351) via reflection.
        private static bool TryBuildWaypoints(AmmunitionParameters ap, Vector3 launchPos, Vector3 targetPos,
            float startAngleDeg, out IList waypoints, out object lastWp)
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
        /// The full ported flight sim (public <c>Missile.SimulateShotLinear</c>, Missile.cs:2371-2605): flies
        /// the game's own waypoint plan with PN guidance, a turn-rate limit and <c>CalculateDrag</c>, in
        /// Unity space. Returns the intercept time in seconds, or -1 when a handle is missing or the sim
        /// never closes or stalls (the caller falls back). Variable names track the decompile (simTime = t,
        /// velKnots = knots, missilePos = pos, direction = dir).
        /// </summary>
        /// <param name="emitDiag">
        /// Emit the per-15 s <c>wp-track</c> overlay. False by default: the frequent caller is the
        /// <see cref="FlightTime.KinematicRaw"/> fallback tier, reached whenever the integrator declines a
        /// shot, which must not trace. Only the per-shot instrument in <c>LaunchDiagnostics</c> passes true.
        /// </param>
        internal static float EndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target,
            bool emitDiag = false)
        {
            if (!Ready || !FullReady || unit == null || ap == null || target == null) return -1f;
            try
            {
                Vector3 launchPosition = unit.transform.position;
                Vector3 targetPosition = target.transform.position;
                Vector3 targetVelocityVector = FlightTime.TargetMapVelocity(target);
                bool isAirUnit = unit.IsAirUnit;
                float startVelocityKnots = Mathf.Max(unit._velocityInKnots, 0f);

                // Evasive-target boost (public 2375-2383), shared with the integrator's setup half.
                FlightTime.ApplyEvasiveBoost(ap, target, launchPosition, targetPosition,
                                             ref targetVelocityVector);

                float flatDist = GameMath.FlatDistance(launchPosition, targetPosition);
                if (flatDist < MinFlatDist) return 0f;
                float dragFactor = ap.GetDragFactor(isAirUnit);
                // Capped at the integrator's own ceiling: an unset MaxFlightTime loads as float.MaxValue, and
                // a 0.5 s step stops changing a float at 2^24 s.
                float maxFlightTime = Mathf.Min(ap._maxFlightTime > 0f ? ap._maxFlightTime : FlightTime.MaxFlightTimeFallback,
                                                FlightTime.SolveLoopCeilingSim);

                var accelTimes = (ValueTuple<float, float>)_mAccelTimes.Invoke(null, new object[] { ap, isAirUnit });
                float boostEnd = accelTimes.Item1 + Mathf.Max(0f, accelTimes.Item2);

                float interceptTimeEst;
                var analyticalResult = (ValueTuple<float, bool, Vector3>)_mAnalytical.Invoke(null,
                    new object[] { ap, launchPosition, startVelocityKnots, targetPosition, targetVelocityVector, dragFactor });
                interceptTimeEst = analyticalResult.Item1;
                if (interceptTimeEst < 0f)
                    interceptTimeEst = (float)_mSimple.Invoke(null, new object[] { ap, launchPosition, targetPosition, targetVelocityVector });

                // Build the game's waypoint plan (aim = lead position), startAngle = _maxLoftAngle.
                float startAngleDeg = ap._maxLoftAngle;
                Vector3 aim = targetPosition + targetVelocityVector * interceptTimeEst;
                if (!TryBuildWaypoints(ap, launchPosition, aim, startAngleDeg,
                        out IList waypoints, out object lastWp))
                    return -1f;

                Vector3 missilePos = launchPosition;
                float velKnots = startVelocityKnots;
                Vector3 initDirVec = new Vector3(targetPosition.x - launchPosition.x, 0f, targetPosition.z - launchPosition.z);
                Vector3 direction = initDirVec.sqrMagnitude > SqrEpsilon ? initDirVec.normalized : Vector3.forward;
                float closestApproachDist = float.MaxValue;
                float closestApproachTime = 0f;
                float closingSpeed = 0f;
                float decelPerStep = ap._deceleration * FlightTime.GravityKnPerMs;
                Vector3 predTargetPos = targetPosition;
                float flatDistToTarget = GameMath.FlatDistance(missilePos, predTargetPos);
                float altDelta = predTargetPos.y - missilePos.y;
                float slantRange = GameMath.SlantFromFlat(flatDistToTarget, altDelta);
                float navGain = ap._navigationGain;
                // Mathf.PI / 180f rather than Mathf.Deg2Rad, for the same reason Rad2DegF above is
                // pinned: this is the decompile's own expression and the two differ in the last bit.
                float turnRateRad = ap._maxTurnRateDegrees * (Mathf.PI / 180f);
                float launchRangeY = Mathf.Max(ap._launchRangesInUnity.y, 1f);
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                float minVel = ap.MinVelocity;
                float liftFactor = ap.LiftFactor;
                float nextLog = 0f;

                for (float simTime = 0f; simTime < maxFlightTime; simTime += SimTimestep)
                {
                    float speedUnity = velKnots * KU;
                    if (flatDistToTarget < closestApproachDist) { closestApproachDist = flatDistToTarget; closestApproachTime = simTime; }

                    // Re-estimate intercept time (public 2446-2476).
                    if (simTime > interceptTimeEst / InterceptReestimateFraction)
                    {
                        Vector3 targetOffset = predTargetPos - missilePos;
                        float estSpeedUnity = speedUnity;
                        if (simTime < ap.TotalBurnTime)
                            estSpeedUnity = Mathf.Max(estSpeedUnity, KU * (ap.TotalDeltaV / 2f + startVelocityKnots));
                        float quadA = estSpeedUnity * estSpeedUnity * InterceptSpeedMarginFactor - targetVelocityVector.sqrMagnitude;
                        float quadB = -2f * Vector3.Dot(targetOffset, targetVelocityVector);
                        float quadC = -targetOffset.sqrMagnitude;
                        float discriminant = quadB * quadB - 4f * quadA * quadC;
                        if (Mathf.Abs(quadA) > QuadraticEpsilon && discriminant >= 0f)
                        {
                            float timeToIntercept = (-quadB + Mathf.Sqrt(discriminant)) / (2f * quadA);
                            if (timeToIntercept > 0f) interceptTimeEst = simTime + timeToIntercept;
                            else if (speedUnity > QuadraticEpsilon) interceptTimeEst = simTime + slantRange / speedUnity;
                        }
                        else
                        {
                            float targetSpeedFactorSq = targetVelocityVector.sqrMagnitude * InterceptSpeedMarginFactor;
                            float fallbackInterceptTime = targetSpeedFactorSq > QuadraticEpsilon
                                ? Mathf.Max(0f, -Vector3.Dot(targetOffset, targetVelocityVector) / targetSpeedFactorSq)
                                : (speedUnity > QuadraticEpsilon ? slantRange / speedUnity : 0f);
                            interceptTimeEst = simTime + fallbackInterceptTime;
                        }
                    }

                    Vector3 leadPosition = targetPosition + targetVelocityVector * interceptTimeEst;
                    float leadBlendFactor = Mathf.Clamp01(1f - Mathf.Pow(GameMath.FlatDistance(missilePos, leadPosition) / launchRangeY, 2f));
                    Vector3 wpInputPos = Vector3.Lerp(leadPosition, predTargetPos, 1f - leadBlendFactor);
                    Vector3 wpVelocity = direction * speedUnity;
                    bool motorBurning = simTime < boostEnd;

                    // Waypoint update (public 2482-2483).
                    UpdateContexts(waypoints, missilePos, wpVelocity, minVel, motorBurning);
                    object geoPos = _ctorGeoFromVec.Invoke(new object[] { wpInputPos });
                    object[] wargs = { waypoints, lastWp, geoPos, SimTimestep };
                    _mUpdateActive.Invoke(null, wargs);
                    waypoints = (IList)wargs[0];
                    lastWp = wargs[1];

                    // Heading via PN, clamped to turn rate (public 2484-2503).
                    Vector3 velocityVec = direction * speedUnity;
                    Vector3 relPos = predTargetPos - missilePos;
                    Vector3 relVel = targetVelocityVector - velocityVec;
                    Vector3 pnCorrection = (Vector3)_mComputePN.Invoke(null, new object[] { velocityVec, relPos, relVel, navGain });
                    float maxHeadingChangeRad = turnRateRad * speedUnity;
                    if (pnCorrection.sqrMagnitude > maxHeadingChangeRad * maxHeadingChangeRad) pnCorrection = pnCorrection.normalized * maxHeadingChangeRad;
                    Vector3 halfStepHeading = velocityVec + pnCorrection * SimTimestep;
                    Vector3 horizHeading = new Vector3(halfStepHeading.x, 0f, halfStepHeading.z);
                    if (horizHeading.sqrMagnitude < SqrEpsilon) horizHeading = new Vector3(direction.x, 0f, direction.z);
                    if (horizHeading.sqrMagnitude < SqrEpsilon) horizHeading = new Vector3(relPos.x, 0f, relPos.z);
                    horizHeading.Normalize();

                    // Pitch toward the active waypoint's commanded position (public 2504-2507).
                    Vector3 waypointCmdPos = waypoints.Count > 0 ? ReadDesiredUnity(waypoints[0]) : leadPosition;
                    float pitchRadCmd = Mathf.Clamp(Mathf.Atan2(waypointCmdPos.y - missilePos.y, GameMath.FlatDistance(waypointCmdPos, missilePos)), -HalfPi, HalfPi);
                    direction = new Vector3(horizHeading.x * Mathf.Cos(pitchRadCmd), Mathf.Sin(pitchRadCmd), horizHeading.z * Mathf.Cos(pitchRadCmd));
                    float pitchDegCmd = -pitchRadCmd * Rad2DegF;

                    // Speed update, verbatim from the public branch (2508-2527).
                    if (nonKin)
                    {
                        float tgtSpd = 0f;
                        if (waypoints.Count > 0)
                        {
                            object settings = _fWpSettings.GetValue(waypoints[0]);
                            if (settings != null) tgtSpd = (float)_fSetTargetSpeed.GetValue(settings);
                        }
                        float targetSpeed = tgtSpd > 0f ? tgtSpd : ap._maxVelocityInKnots;
                        if (velKnots < targetSpeed)
                        {
                            float thrust = (float)_mThrust.Invoke(null, new object[] { ap, isAirUnit, simTime, SimTimestep });
                            velKnots = Mathf.Min(velKnots + thrust, targetSpeed);
                        }
                        else if (velKnots > targetSpeed)
                        {
                            velKnots = Mathf.Max(velKnots - decelPerStep * SimTimestep, targetSpeed);
                        }
                    }
                    else
                    {
                        float thrust = (float)_mThrust.Invoke(null, new object[] { ap, isAirUnit, simTime, SimTimestep });
                        float drag = (float)_mDrag.Invoke(null, new object[]
                        { missilePos.y, speedUnity, SimTimestep, pitchDegCmd, dragFactor, motorBurning, predTargetPos.y, liftFactor, minVel, 0f });
                        velKnots = Mathf.Max(velKnots + thrust - drag, 0f);
                    }
                    // Intercept detection (public 2532-2554); return only the time.
                    float speedUnityLoop = velKnots * KU;
                    if (slantRange > SqrEpsilon)
                    {
                        Vector3 losDirection = (predTargetPos - missilePos) / slantRange;
                        if (closingSpeed * SimTimestep * 2f >= slantRange) return simTime;
                        closingSpeed = Vector3.Dot(direction * speedUnityLoop - targetVelocityVector, losDirection);
                    }

                    // Advance (public 2555-2572).
                    missilePos += direction * (speedUnityLoop * SimTimestep);
                    predTargetPos = targetPosition + targetVelocityVector * (simTime + SimTimestep);
                    if (predTargetPos.y < 0f)
                    {
                        Vector3 targetDisplacement = targetVelocityVector * (simTime + SimTimestep);
                        float dispMag = targetDisplacement.magnitude;
                        float negTargetAlt = -targetPosition.y;
                        float slantSq = dispMag * dispMag - negTargetAlt * negTargetAlt;
                        Vector3 horizDisplacement = new Vector3(targetDisplacement.x, 0f, targetDisplacement.z);
                        float horizDispMag = horizDisplacement.magnitude;
                        predTargetPos = (slantSq > 0f && horizDispMag > SqrEpsilon)
                            ? targetPosition + horizDisplacement / horizDispMag * Mathf.Sqrt(slantSq) + Vector3.up * negTargetAlt
                            : new Vector3(targetPosition.x, 0f, targetPosition.z);
                    }
                    flatDistToTarget = GameMath.FlatDistance(missilePos, predTargetPos);
                    altDelta = predTargetPos.y - missilePos.y;
                    slantRange = GameMath.SlantFromFlat(flatDistToTarget, altDelta);

                    if (Coordinator.TraceFlightModel && emitDiag && simTime >= nextLog)
                    {
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] wp-track {ap._ammunitionFileName}#{unit.GetInstanceID()}: t+{simTime:0}s " +
                            $"spd {velKnots:0}kn alt {missilePos.y:0.0}u pitch {pitchDegCmd:0.0} cmdAlt {waypointCmdPos.y:0.0}u " +
                            $"flat {flatDistToTarget:0}u wps {waypoints.Count}");
                        nextLog += WpTrackLogIntervalSim;
                    }

                    // loftTooHigh escape + stall (public 2577-2604). The escape retires the loft waypoint and
                    // does not skip the range break below: the game reaches that break through `goto
                    // IL_0a08`, so the same iteration still evaluates it (public Missile.cs:2596-2604).
                    if (simTime > LoftCheckMinSimTime && !motorBurning)
                    {
                        bool stalled = velKnots < minVel;
                        // waypoints[0] is null-checked because the game checks it (public Missile.cs:2583); a
                        // null here would throw into EndTime's catch and turn a flyable shot into a silent -1.
                        if (velKnots < minVel + LoftHighSpeedMargin && waypoints.Count > 0
                            && waypoints[0] != null)
                        {
                            object settings = _fWpSettings.GetValue(waypoints[0]);
                            object loft = settings != null ? _fSetLoftHeight.GetValue(settings) : null;
                            // Retiring the waypoint clears the stall: the game sets Done and leaves
                            // the loop running, so the `break` below must not also fire.
                            if (loft != null) { SetActiveDone(waypoints[0]); stalled = false; }
                        }
                        if (stalled) break;
                    }
                    if (nonKin && GameMath.FlatDistance(missilePos, launchPosition) > ap._launchRangesInUnity.y) break;
                }

                if (closestApproachTime < MinCloseApproachTime) return -1f;   // never closed
                return closestApproachTime;                   // closest-approach time
            }
            catch (Exception e)
            {
                ModLog.VerboseWarn("wp-track: EndTime exception", e);
                return -1f;
            }
        }
    }
}
