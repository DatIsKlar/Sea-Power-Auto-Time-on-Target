using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        private const float IntegrationStepSim = 0.1f;
        private const float AltToleranceU = 0.5f;
        // The game's OWN ini defaults: MaxLoftAngle 30 (AmmunitionParameters.cs:1633),
        // SeaSkimmingMaxDescentAngle 30 (:1662), FinalFlightPhaseMaxAngle 30 (:1683).
        private const float DefaultClimbDeg = 30f;
        private const float DefaultDescentDeg = 30f;
        private const float BoostClimbDeg = 90f;
        private const float DefaultTurnRateDeg = 5f;   // MaxTurnRate default (AmmunitionParameters.cs:1732)
        private const float MinDescentOnsetDeg = 5f;
        private const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        // Deliberately NOT a round number: a 15s sampler aliased a 15.0s limit cycle and reported a
        // steady altitude while the missile swung 1115 <-> 1260u. 7 is prime relative to the
        // plausible cycle periods here.
        private const float TelemetrySampleIntervalSim = 7f;
        // The launch phase lasts up to ~19s and creates the residual fixed offset, so sample it
        // densely; 7s would leave one point inside it.
        private const float LaunchBurstWindowSim = 20f;
        private const float LaunchBurstIntervalSim = 1f;
        // Inner tier for the nose-over itself. A vertically-launched sea-skimmer reverses its whole
        // launch attitude inside ~2s, and at 1s cadence the same yj-18a data reads as either 19 or
        // 33.7 deg/s depending on the averaging window. 0.25s gives ~20 samples instead of two.
        private const float NoseOverWindowSim = 5f;
        private const float NoseOverIntervalSim = 0.25f;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        private const float LookaheadMultiplier = 20f;
        private const float MinLookaheadU = 50f;

        // ---- Isolation gates. Each mirrors one piece of the live mover's behaviour, kept separate
        // so a single rebuild can A/B them independently. Every one below is validated and on.
        // What each models: docs/model/03-trajectory.md. The evidence that established it, the
        // alternatives that were falsified and the measurements behind them:
        // docs/plans/reference/integrator-rnd.md.

        // Bang-bang altitude control while TRANSITING, proportional once HOLDING, latched on first
        // arrival. The real round holds full climb and only then noses over at _maxTurnRateDegrees,
        // so the overshoot is the turn rate, not a defect.
        private const bool LatchedProportionalHold = true;

        // TerminalApproach needs the seeker to hold an echo for SearchForTargetsTime, not just the
        // distance, and the clock resets on every tick without one (Missile.cs:584-593), so the
        // missile closes at CRUISE speed past its nominal _terminalApproachDist.
        private const bool SearchTimeTerminalOnset = true;

        // ---- Launch-phase fidelity. The game runs Launch -> ToBearing before any cruise/loft
        // command, and both the commanded speed and the attitude the round leaves with differ
        // there. Those first seconds become a fixed offset carried for the rest of the flight.

        // ToBearing's exit condition, which decides WHEN the launch stage ends. It selects the
        // commanded speed below; it does NOT command an attitude (reading it as one was falsified).
        private const float ToBearingConeDeg = 5f;      // Missile.cs:343
        private const float ToBearingMaxSeconds = 10f;  // Missile.cs:343

        // Launch/ToBearing command _maxVelocityInKnots; loft speed waits for MaintainLoftAlt
        // (Missile.cs:3142-3145).
        private const bool LaunchStageSpeed = true;

        // Launch elevation from the launcher's container transform, NOT `_fixVerticalLaunchAngle`,
        // which reads 35 deg for every launcher in the game because that is the ini default and its
        // gating bool also defaults true (ObjectBaseLoader.cs:2688-2690).
        private const bool LauncherTransformLaunchAngle = true;

        // A launcher that cannot train fires along its OWN bearing, so an off-bearing shot flies its
        // initial phase the wrong way and then turns. railAz, not bearingErr, predicts that turn.
        private const bool FixedRailLaunchHeading = true;

        // A vertical rail has no bearing of its own, so the round leaves carrying the SHIP's yaw.
        private const bool VerticalLaunchInheritsShipHeading = true;

        // Pitch, yaw and roll share ONE Quaternion.RotateTowards budget (WeaponBase.cs:1769-1771),
        // so modelling the axes independently spends it twice: 90 deg of pitch with 90 deg of
        // heading error is 120 deg of quaternion travel, not 90. Reached only for
        // Kinematics == None (:1599). An on-bearing shot is unaffected, the step reducing to
        // MoveTowards. Requires the attitude to PERSIST across steps, or the budget spent on roll is
        // refunded every step and the gate goes inert.
        private const bool CoupledPitchYawRateLimit = true;

        // Non-kinematic + SupportsBanking gets a SECOND rotation call per tick: performToTargetRoll
        // at a hardcoded 60 deg/s on top of the normal RotateTowards (WeaponBase.cs:1773-1776,
        // :1789-1792). It assigns local euler to WORLD rotation, which is gimbal-degenerate near
        // vertical, so after a VLS launch that budget lands on PITCH. The two sum.
        private const bool BankingAddsRollBudgetToPitch = true;
        private const float BankingRollRateDeg = 60f;   // WeaponBase.cs:1792, hardcoded

        // The 90 deg boost climb is scoped to isHighBallisticLofter, not to kinematic ammo at large:
        // AllowExceedingAngleLimits (Missile.cs:2197) permits exceeding the commanded angle but does
        // not make the command 90. A round lofting inside the atmosphere flies its own MaxLoftAngle.

        // `launch-rail` reports pure LAUNCHER GEOMETRY, independent of any missile flying, so it is
        // emitted from the planning path rather than behind emitDiag. Keyed by (unit, ammo) and
        // re-emitted only when the rail MOVES: a trainable mount's slew is captured, a fixed rail
        // logs once and goes quiet.
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLogged =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLoggedAz =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, int> _railLogCount =
            new System.Collections.Generic.Dictionary<string, int>();
        private const float RailRelogDeltaDeg = 2f;
        private const int RailMaxLogsPerKey = 24;   // a deliberate heading sweep needs headroom
        /// <summary>
        /// Where the round actually points as it leaves the ship: the launch elevation read from
        /// the launcher transform, whether that launcher is a fixed rail, and the rail itself so
        /// the step loop can fly its bearing. Also emits the one-off `launch-rail` diagnostic.
        /// </summary>
        private readonly struct LaunchGeometry
        {
            internal readonly float Pitch, PitchIni;
            internal readonly Transform Rail;
            internal readonly bool FixedRail;
            internal readonly string RailAzText;
            internal LaunchGeometry(float pitch, float pitchIni, Transform rail, bool fixedRail, string railAzText)
            {
                Pitch = pitch; PitchIni = pitchIni; Rail = rail;
                FixedRail = fixedRail; RailAzText = railAzText;
            }
        }

        private static LaunchGeometry ResolveLaunchGeometry(
            ObjectBase unit, AmmunitionParameters ap, Vector3 launchPos, Vector3 targetPos)
        {
            float launchPitch = -1f;
            float launchPitchIni = -1f;
            Transform rail = null;
            bool fixedRail = false;
            string railAzTxt = "n/a";
            try
            {
                var launchers = unit.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
                if (launchers != null && launchers.Count > 0)
                {
                    // Fixed mounts for one ammo can point in completely different directions (two
                    // MK141s, Port and Starboard, 180 deg apart), so `launchers[0]` would read the
                    // wrong bearing half the time. The round comes off whichever launcher bears:
                    // pick the one whose horizontal rail direction is closest to the target.
                    int pick = 0;
                    if (launchers.Count > 1)
                    {
                        Vector3 toTgtH = targetPos - launchPos; toTgtH.y = 0f;
                        float best = float.MaxValue;
                        for (int li = 0; li < launchers.Count; li++)
                        {
                            var lw = launchers[li]?._vwp;
                            Transform lt = null;
                            var lc = launchers[li]?._containers;
                            if (lc != null && lc.Count > 0 && lc[0]?._gunObject != null)
                                lt = lc[0]._gunObject.transform;
                            else if (lw != null && lw._containerBaseObject != null)
                                lt = lw._containerBaseObject.transform;
                            if (lt == null || toTgtH.sqrMagnitude < 1e-6f) continue;
                            Vector3 lf = lt.forward; lf.y = 0f;
                            if (lf.sqrMagnitude < 1e-4f) continue;   // vertical: no bearing
                            float off = Vector3.Angle(lf, toTgtH);
                            if (off < best) { best = off; pick = li; }
                        }
                    }
                    var vwp = launchers[pick]?._vwp;
                    if (vwp != null && vwp._fixVerticalLaunchAngleForLauncher)
                        launchPitchIni = vwp._fixVerticalLaunchAngle + vwp._additionalFixVerticalLaunchAngle;
                    launchPitch = launchPitchIni;

                    var ws = launchers[pick];
                    // The object the game actually elevates: gunObject per container, or the
                    // shared container base when they are joined (WeaponSystem.alignToTarget
                    // :1379-1381). gunObj is populated either way, so prefer it.
                    Transform railGun = (ws._containers != null && ws._containers.Count > 0
                                         && ws._containers[0]?._gunObject != null)
                        ? ws._containers[0]._gunObject.transform : null;
                    Transform railBase = (vwp != null && vwp._containerBaseObject != null)
                        ? vwp._containerBaseObject.transform : null;
                    Transform railMount = (vwp != null && vwp._mountObject != null)
                        ? vwp._mountObject.transform : null;
                    rail = railGun ?? railBase;

                    float railDeg = rail != null
                        ? Mathf.Asin(Mathf.Clamp(rail.forward.y, -1f, 1f)) * Mathf.Rad2Deg
                        : float.NaN;

                    // A launcher that cannot move fires along the rail as built. One that can
                    // move aims first, so its CURRENT transform is wherever it is parked and
                    // must never be read as a launch attitude -- compute the game's own aim
                    // instead (alignToTarget:1360-1377; RotateWeaponToAngle:1644 confirms the
                    // angle passed there becomes the gun's local elevation).
                    fixedRail = vwp != null
                             && !vwp._isMountRotatable && !vwp._areContainersRotatable;
                    float predictedPitch = -1f;
                    if (vwp != null)
                    {
                        if (fixedRail && !float.IsNaN(railDeg))
                        {
                            predictedPitch = Mathf.Clamp(railDeg, 0f, 90f);
                        }
                        else
                        {
                            // Trainable: elevation to the target unless the launcher fixes the
                            // angle, less the mount's own pitch, clamped to the elevation arc.
                            Vector3 toTgt = targetPos - launchPos;
                            float tgtElev = toTgt.sqrMagnitude > 1e-6f
                                ? Mathf.Asin(Mathf.Clamp(toTgt.normalized.y, -1f, 1f)) * Mathf.Rad2Deg
                                : 0f;
                            float mountPitch = railMount != null
                                ? Mathf.Asin(Mathf.Clamp(railMount.forward.y, -1f, 1f)) * Mathf.Rad2Deg
                                : 0f;
                            float bas = vwp._fixVerticalLaunchAngleForLauncher
                                ? vwp._fixVerticalLaunchAngle : tgtElev;
                            float e = bas + vwp._additionalFixVerticalLaunchAngle - mountPitch;
                            if (vwp._elevationArc.y > vwp._elevationArc.x)
                                e = Mathf.Clamp(e, vwp._elevationArc.x, vwp._elevationArc.y);
                            predictedPitch = Mathf.Clamp(e, 0f, 90f);
                        }
                    }

                    // Horizontal analogue of railDeg: rail bearing vs bearing to target. `bearingErr`
                    // cannot see this, measuring the SHIP's heading instead. Meaningless when the
                    // rail is near-vertical, which is every VLS shot.
                    float railAzDeg = float.NaN;
                    if (rail != null)
                    {
                        Vector3 rf = rail.forward; rf.y = 0f;
                        Vector3 tt = targetPos - launchPos; tt.y = 0f;
                        if (rf.sqrMagnitude < 1e-4f) railAzTxt = "vertical";
                        else if (tt.sqrMagnitude > 1e-6f)
                        {
                            railAzDeg = Vector3.Angle(rf, tt);
                            railAzTxt = railAzDeg.ToString("0.0") + "°";
                        }
                    }

                    // Re-emitted whenever the rail moves, so the formula above is measured rather
                    // than assumed.
                    string railKey = unit.GetInstanceID() + "/" + (ap._ammunitionFileName ?? "?");
                    if (Coordinator.VerboseLog && vwp != null)
                    {
                        try
                        {
                            _railLogCount.TryGetValue(railKey, out int n);
                            bool firstSeen = !_railLogged.TryGetValue(railKey, out float prev);
                            bool elevMoved = !float.IsNaN(railDeg)
                                          && Mathf.Abs(railDeg - prev) > RailRelogDeltaDeg;
                            // A fixed box launcher never changes ELEVATION, so without an azimuth
                            // test a launch-bearing sweep would be uninstrumentable.
                            bool azMoved = !float.IsNaN(railAzDeg)
                                        && (!_railLoggedAz.TryGetValue(railKey, out float prevAz)
                                            || Mathf.Abs(railAzDeg - prevAz) > RailRelogDeltaDeg);
                            if ((firstSeen || elevMoved || azMoved) && n < RailMaxLogsPerKey)
                            {
                                _railLogged[railKey] = railDeg;
                                if (!float.IsNaN(railAzDeg)) _railLoggedAz[railKey] = railAzDeg;
                                _railLogCount[railKey] = n + 1;
                                string E(Transform tr) => tr == null ? "n/a"
                                    : (Mathf.Asin(Mathf.Clamp(tr.forward.y, -1f, 1f)) * Mathf.Rad2Deg)
                                        .ToString("0.0") + "°";
                                Bootstrap.Log.LogInfo(
                                    $"[AutoTOT] launch-rail {ap._ammunitionFileName}: " +
                                    $"gunObj {E(railGun)}, containerBase {E(railBase)}, mount {E(railMount)}, " +
                                    $"fixedRail {fixedRail}, predicted {predictedPitch:0.0}°, " +
                                $"railAz {railAzTxt}, " +
                                    $"containersRotatable {vwp._areContainersRotatable}, " +
                                    $"joined {vwp._areContainersJoinedTogether}, " +
                                    $"mountRotatable {vwp._isMountRotatable}, " +
                                    $"elevArc {vwp._elevationArc.x:0.0}/{vwp._elevationArc.y:0.0}, " +
                                    $"iniPitch {launchPitchIni:0.0}°");
                            }
                        }
                        catch { }
                    }

                    if (LauncherTransformLaunchAngle && predictedPitch >= 0f)
                        launchPitch = predictedPitch;
                }
            }
            catch { launchPitch = launchPitchIni; }
            return new LaunchGeometry(launchPitch, launchPitchIni, rail, fixedRail, railAzTxt);
        }

        /// <summary>
        /// Where the final and terminal flight phases begin, and the altitudes and descent angles
        /// they command. Depends only on the ammunition's own parameters.
        /// </summary>
        private readonly struct StageProfile
        {
            internal readonly float FinalDist, FinalAlt, TermDist, TermAlt, DescentDeg, DescentOnsetDeg;
            internal StageProfile(float finalDist, float finalAlt, float termDist, float termAlt,
                                  float descentDeg, float descentOnsetDeg)
            {
                FinalDist = finalDist; FinalAlt = finalAlt; TermDist = termDist;
                TermAlt = termAlt; DescentDeg = descentDeg; DescentOnsetDeg = descentOnsetDeg;
            }
        }

        private static StageProfile ResolveStageProfile(AmmunitionParameters ap, float maxVelKn)
        {
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
            if (SearchTimeTerminalOnset && ap._searchForTargetsTime > 0f)
            {
                // Distance the missile still covers at cruise while the seeker searches.
                termDist = Mathf.Max(
                    termDist - maxVelKn * GameUnits.KnotsToUnityPerSecond * ap._searchForTargetsTime, 0f);
            }
            float termAlt = ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt;
            float descentDeg = ap._finalFlightPhaseMaxAngle > 0.01f ? ap._finalFlightPhaseMaxAngle
                             : (ap._seaSkimmingMaxDescentAngle > 0.01f ? ap._seaSkimmingMaxDescentAngle : DefaultDescentDeg);
            float descentOnsetDeg = Mathf.Max(descentDeg,
                Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));
            return new StageProfile(finalDist, finalAlt, termDist, termAlt, descentDeg, descentOnsetDeg);
        }

        /// <summary>
        /// How far off-bearing a shot is at launch: the horizontal angle between the shooter's
        /// heading and the bearing to the target. Diagnostic only, and a CORRELATE, not the game's
        /// own quantity, which measures against the missile's forward vector (carrying launch pitch
        /// too) rather than the ship's heading in the horizontal plane.
        /// </summary>
        /// <returns>Degrees, or -1 when unavailable.</returns>
        private static float LaunchBearingErrDeg(ObjectBase unit, Vector3 launchPos, Vector3 targetPos)
        {
            try
            {
                Vector3 shipFwd = unit.transform.forward; shipFwd.y = 0f;
                Vector3 toTarget = targetPos - launchPos; toTarget.y = 0f;
                if (shipFwd.sqrMagnitude > 1e-6f && toTarget.sqrMagnitude > 1e-6f)
                    return Vector3.Angle(shipFwd, toTarget);
            }
            catch { }
            return -1f;
        }

        /// <summary>Compass yaw of a flattened heading vector, in Unity's euler-y convention.</summary>
        private static float YawOf(Vector3 h) => Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg;

        internal static void ClearRailLog()
        { _railLogged.Clear(); _railLoggedAz.Clear(); _railLogCount.Clear(); }
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

        internal struct IntegratedPhases
        {
            public bool Valid;
            public bool Lofting;
            public float LoftAltTarget;
            public float PeakAltU;
            public float ClimbTime, CruiseTime, DescentTime;
            public float VStart;
            public float VClimbExit;
            public float VCruiseExit;
            public float VTerm;
            public float FinalDistU;
            public float TermDistU;
            // Flat distance at which the model entered the dive: max(termDist, descentGeomDist),
            // so unlike the raw TermDistU it carries the geometric ramp that governs high lofters.
            // -1 = never reached phase 2. Compare against `stage-obs`.
            public float DiveStartU;
            public float DescentOnsetDeg;
        }

        internal static float IntegratedEndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
            => IntegratedEndTimeCore(unit, ap, target, out _, emitDiag: false);

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
            if (!TryBuildSolveInput(unit, ap, target, out SolveInput input, out phases, emitDiag))
                return -1f;
            ModelStats.SetupDone();
            return Solve(in input, ap, ref phases);
        }

        /// <summary>
        /// The setup half: everything that reads Unity state or game state the game mutates, which is
        /// therefore main-thread only. Produces the immutable input the step loop runs on, so the
        /// same setup feeds both the synchronous path and the worker pool. False means the integrator
        /// declines and the caller should fall through to the next tier.
        /// </summary>
        internal static bool TryBuildSolveInput(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out SolveInput input, out IntegratedPhases phases, bool emitDiag)
        {
            input = default;
            phases = default;
            ModelStats.SimStarted();
            try
            {
                EnsureSimLookup();
                if (!_simIsBeta || _thrustMethod == null) return false;
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                if (!nonKin && _dragMethod == null) return false;

                const float ZeroDensityAltU = 1f / 0.00163f;
                Vector3 launchPos = unit.transform.position;
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool isAir = unit.IsAirUnit;

                float tvMag = targetVel.magnitude;
                if (tvMag > 0f && ap.AssumeEvasiveTarget(target))
                {
                    Vector3 flee = targetPos - launchPos; flee.y = 0f;
                    if (flee.sqrMagnitude > 1e-8f)
                    {
                        targetVel += flee.normalized * (tvMag * EvasiveBoostFraction);
                        targetVel = targetVel.normalized * Mathf.Min(targetVel.magnitude, tvMag);
                    }
                }

                float dragFactor = ap.GetDragFactor(isAir);
                float startVelKnots = Mathf.Max(unit._velocityInKnots, 0f);
                float maxFlight = ap._maxFlightTime > 0f ? ap._maxFlightTime : MaxFlightTimeFallback;
                float targetAlt0 = Mathf.Max(targetPos.y, 0f);

                float loftAlt = -1f;
                if (_loftCapMethod != null)
                {
                    float cap = (float)_loftCapMethod.Invoke(null,
                        new object[] { ap, Mathf.Max(launchPos.y, 0f), targetAlt0 });
                    float floor = Mathf.Max(Mathf.Max(launchPos.y, 0f), targetAlt0);
                    if (cap > floor + AltToleranceU)
                        loftAlt = cap;
                }
                bool lofting = loftAlt > Mathf.Max(launchPos.y, 0f) + AltToleranceU;

                bool isTerminalLoft = ap._terminalLoft;
                bool isHighBallisticLofter = !nonKin && lofting && loftAlt > ZeroDensityAltU;

                float climbDeg = ap._maxLoftAngle > AltToleranceU ? ap._maxLoftAngle : DefaultClimbDeg;
                float boostClimbDeg = isHighBallisticLofter ? BoostClimbDeg : climbDeg;
                float turnRate = ap._maxTurnRateDegrees > VelocityEpsilonKn ? ap._maxTurnRateDegrees : DefaultTurnRateDeg;
                // See BankingAddsRollBudgetToPitch above: the roll call is a second, independent
                // per-tick budget, so the two can sum onto pitch.
                if (BankingAddsRollBudgetToPitch && nonKin && ap._supportsBanking)
                    turnRate += BankingRollRateDeg;

                LaunchGeometry geom = ResolveLaunchGeometry(unit, ap, launchPos, targetPos);
                float launchPitch = geom.Pitch;
                float launchPitchIni = geom.PitchIni;
                Transform rail = geom.Rail;
                bool fixedRail = geom.FixedRail;
                string railAzTxt = geom.RailAzText;
                float initialPhaseDur = Mathf.Max(ap._initialFlightPhaseDuration, 0f);

                float launchBearingErrDeg = LaunchBearingErrDeg(unit, launchPos, targetPos);

                float maxVelKn = Mathf.Max(ap._maxVelocityInKnots, 1f);
                float loftVelKn = ap._maxLoftVelocityInKnots > 0f ? ap._maxLoftVelocityInKnots : maxVelKn;
                float termVelKn = ap._terminalVelocityInKnots > 0f ? ap._terminalVelocityInKnots : maxVelKn;
                float decelPerStep = ap._deceleration * GravityKnPerMs * IntegrationStepSim;

                StageProfile stage = ResolveStageProfile(ap, maxVelKn);
                float finalDist = stage.FinalDist;
                float finalAlt = stage.FinalAlt;
                float termDist = stage.TermDist;
                float termAlt = stage.TermAlt;
                float descentDeg = stage.DescentDeg;
                float descentOnsetDeg = stage.DescentOnsetDeg;

                phases.Valid = true;
                phases.Lofting = lofting;
                phases.LoftAltTarget = lofting ? loftAlt : 0f;
                phases.VStart = startVelKnots;
                phases.PeakAltU = launchPos.y;
                phases.FinalDistU = finalDist;
                phases.TermDistU = termDist;
                phases.DiveStartU = -1f;
                phases.DescentOnsetDeg = descentOnsetDeg;

                Vector3 pos = launchPos;
                float velKnots = startVelKnots;
                float t = 0f;
                float prevPitch = launchPitch >= 0f ? launchPitch : 0f;
                // Launch heading for a fixed launcher: the horizontal analogue of prevPitch. From
                // the rail's own bearing where it has one, else the ship's yaw for a vertical
                // cell. Zero when there is nothing to model (trainable mount, or no rail
                // resolved), in which case horizDir keeps aiming straight at the target.
                Vector3 launchHeading = Vector3.zero;
                if (FixedRailLaunchHeading && fixedRail && rail != null)
                {
                    Vector3 rfwd = rail.forward; rfwd.y = 0f;
                    if (rfwd.sqrMagnitude > 1e-4f) launchHeading = rfwd.normalized;
                    else if (VerticalLaunchInheritsShipHeading)
                    {
                        // Vertical rail: no bearing of its own, so inherit the ship's yaw
                        // (see VerticalLaunchInheritsShipHeading). Same vector the bearingErr
                        // diagnostic reads above; the per-step block below is reused unchanged.
                        Vector3 sfwd = unit.transform.forward; sfwd.y = 0f;
                        if (sfwd.sqrMagnitude > 1e-4f) launchHeading = sfwd.normalized;
                    }
                }
                // Attitude carried ACROSS steps for the coupled turn: rebuilding it each step would
                // refund the budget spent on roll and silently restore the independent-limit rate.
                // Re-seeded (roll zero) on any step that does not take the coupled branch.
                Quaternion att = Quaternion.Euler(-prevPitch, YawOf(launchHeading), 0f);
                float prevFlat = float.MaxValue;
                bool tlGliding = false;
                // Arrival latch: has the missile reached the current phase's target altitude
                // yet? Before arrival it is transiting (bang-bang, so the finite-turn-rate nose-over
                // still overshoots); after, it is holding (proportional, so it cannot limit-cycle).
                bool altLatched = false;
                int altLatchPhase = -1;
                float prevAltErr = float.NaN;

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
                float nextSample = NoseOverIntervalSim;
                bool trackDiag = Coordinator.VerboseLog && emitDiag;
                string ammoLabel = ap._ammunitionFileName ?? "?";
                if (trackDiag)
                    Bootstrap.Log.LogInfo($"[AutoTOT] sim-launch {ammoLabel}: launchPitch " +
                        $"{(launchPitch >= 0f ? launchPitch.ToString("0.0") + "°" : "n/a (heading)")}" +
                        $", initPhase {initialPhaseDur:0.0}s, turnRate {turnRate:0.0}/s, loftAlt {loftAlt:0}u" +
                        $", descentDeg {descentDeg:0.0}°, onsetDeg {descentOnsetDeg:0.0}°" +
                        $", bearingErr {(launchBearingErrDeg >= 0f ? launchBearingErrDeg.ToString("0.0") + "°" : "n/a")}" +
                        // Launch range, so each shot self-records its geometry. Two ranges per shot
                        // cannot falsify a fixed-offset-plus-drift decomposition; three can.
                        $", range {flatDistTotal:0}u ({flatDistTotal * GameUnits.MetersPerUnity / 1000f:0.0}km)" +
                        // Both values, so the ini default and the rail's real orientation can be
                        // compared directly on one line.
                        $", iniPitch {(launchPitchIni >= 0f ? launchPitchIni.ToString("0.0") + "°" : "n/a")}" +
                        // Sampled on the fired-shot path: the planning-path launch-rail reading goes
                        // stale because the ship keeps turning between planning and launch.
                        $", railAz {railAzTxt}");

                // The loop runs as a pure function of this snapshot. Building it here keeps every
                // Unity read and every mutable-game-state read on the main thread, which is what
                // makes the loop safe to run elsewhere. See FlightTime.Solve.cs.
                input = new SolveInput(
                    altNodes,
                    ammoLabel,
                    boostClimbDeg,
                    decelPerStep,
                    descentDeg,
                    descentOnsetDeg,
                    dragFactor,
                    finalAlt,
                    finalDist,
                    flatDistTotal,
                    initialPhaseDur,
                    isAir,
                    isHighBallisticLofter,
                    isTerminalLoft,
                    launchPitch,
                    loftAlt,
                    loftVelKn,
                    lofting,
                    maxFlight,
                    maxVelKn,
                    nonKin,
                    targetAlt0,
                    targetPos,
                    targetVel,
                    termAlt,
                    termDist,
                    termVelKn,
                    trackDiag,
                    turnRate,
                    altLatchPhase,
                    altLatched,
                    att,
                    launchHeading,
                    nextSample,
                    pos,
                    prevAltErr,
                    prevFlat,
                    prevPitch,
                    t,
                    tlGliding,
                    velKnots);

                return true;
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] integrated flight-time setup failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
