using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        private const float IntegrationStepSim = 0.1f;
        private const float AltToleranceU = 0.5f;
        // Fallbacks matching the game's OWN ini defaults, so an ammo that omits the key behaves
        // here exactly as it does in the game: MaxLoftAngle 30 (AmmunitionParameters.cs:1633),
        // SeaSkimmingMaxDescentAngle 30 (:1662), FinalFlightPhaseMaxAngle 30 (:1683). In practice
        // the loader always populates these, so these fire only for an ini that sets an explicit 0.
        private const float DefaultClimbDeg = 30f;
        private const float DefaultDescentDeg = 30f;
        private const float BoostClimbDeg = 90f;
        private const float DefaultTurnRateDeg = 5f;   // MaxTurnRate default (AmmunitionParameters.cs:1732)
        private const float MinDescentOnsetDeg = 5f;
        private const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        // Deliberately NOT a round number. A 15s sampler once aliased a limit cycle whose period
        // was exactly 15.0s, reporting a constant altitude while the missile was actually swinging
        // 1115 <-> 1260u -- a 10km oscillation that looked like a steady state and cost a full test
        // round. 7 is prime relative to the plausible cycle periods here.
        private const float TelemetrySampleIntervalSim = 7f;
        // The launch phase (initial flight phase + ToBearing) lasts up to ~19s and is where the
        // residual fixed offset is created, so sample it densely; 7s leaves one point inside it.
        private const float LaunchBurstWindowSim = 20f;
        private const float LaunchBurstIntervalSim = 1f;
        // Inner tier for the nose-over itself. A vertically-launched sea-skimmer reverses its whole
        // launch attitude inside ~2s, which at 1s cadence is TWO samples -- enough that the same
        // yj-18a data reads as either 19 deg/s (matching its MaxTurnRate) or 33.7 deg/s (2.2x it)
        // depending on the averaging window. 0.25s turns that into ~20 samples.
        private const float NoseOverWindowSim = 5f;
        private const float NoseOverIntervalSim = 0.25f;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        private const float LookaheadMultiplier = 20f;
        private const float MinLookaheadU = 50f;

        // ---- Isolation gates. Each mirrors one piece of the live mover's behaviour, kept separate
        // so a single rebuild can A/B them independently. Every one below is validated and on.
        // The evidence that established each, the alternatives that were falsified and the
        // measurements behind them live in docs/plans/reference/integrator-rnd.md.

        // Bang-bang altitude control is right while a missile is TRANSITING to an altitude: the real
        // round holds full climb to its commanded altitude and only then noses over at
        // _maxTurnRateDegrees, so the overshoot is a consequence of the turn rate, not a defect.
        // Proportional control is right only once it is already HOLDING. So latch on first arrival
        // at the phase's target altitude: bang-bang before, proportional after. Reuses AltToleranceU
        // as an arrival test rather than as a control deadband. Without the latch a lofter bleeds
        // cos(pitch) of its speed into a climb that goes nowhere, and the error grows with cruise
        // duration.
        private const bool LatchedProportionalHold = true;

        // The game does not enter TerminalApproach on distance alone: with SearchForTargetsTime > 0
        // the seeker must also hold an echo for that long first, and the clock resets on every tick
        // without one (Missile.cs:584-593). So the missile keeps closing at CRUISE speed past its
        // nominal _terminalApproachDist. Inert for ammo that leaves the field unset.
        private const bool SearchTimeTerminalOnset = true;

        // ---- Launch-phase fidelity ----
        // The model would otherwise jump straight from the rail into its cruise/loft commands, but
        // the game runs Launch -> ToBearing first, and both the commanded speed and the attitude the
        // round leaves with differ there. What happens in those first seconds becomes a fixed offset
        // carried unchanged for the rest of the flight.

        // ToBearing's exit condition, which decides WHEN the launch stage ends: the state runs until
        // the attitude is within a 5 deg cone of the aim point, or 10.0s elapse (Missile.cs:343). It
        // selects the commanded speed below. It does NOT command an attitude; reading it as one was
        // falsified.
        private const float ToBearingConeDeg = 5f;      // Missile.cs:343
        private const float ToBearingMaxSeconds = 10f;  // Missile.cs:343

        // Commanded speed during Launch/ToBearing is _maxVelocityInKnots; the loft speed applies
        // only once the stage is MaintainLoftAlt (Missile.cs:3142-3145). Inert for ammo whose loft
        // speed equals its max speed.
        private const bool LaunchStageSpeed = true;

        // The launch ELEVATION, read from the launcher rather than from `_fixVerticalLaunchAngle` --
        // that field reads 35 deg for every launcher in the game, being the ini default with its
        // gating bool also defaulting true (ObjectBaseLoader.cs:2688-2690). The direction the round
        // is actually pointing is the launcher's container transform,
        // `_containers[i]._gunObject.transform`: 90 deg for a VLS cell, 45 deg for an inclined tube.
        // Trainable mounts come for free, since WeaponSystemLauncher.alignToTarget:1264 elevates the
        // container when it can and returns immediately when it cannot, leaving a VLS cell as built.
        private const bool LauncherTransformLaunchAngle = true;

        // A launcher that cannot train fires along its OWN bearing, so an off-bearing shot spends its
        // initial flight phase flying the wrong way and then has to turn, closure the model does not
        // pay if `horizDir` snaps at the target from t=0. Inert wherever the rail is already
        // on-bearing or vertical. Note railAz and bearingErr are near-inverses on a Harpoon ship --
        // the MK141 boxes sit about 90 deg off the hull axis, so pointing the bow at the target puts
        // the rail abeam. railAz is the quantity that predicts the turn.
        private const bool FixedRailLaunchHeading = true;

        // A VERTICAL rail has no bearing of its own, so the branch above leaves launchHeading unset.
        // The round still leaves the cell carrying the SHIP's yaw and must turn onto the target
        // bearing before it can close. Reaches every vertical launch.
        private const bool VerticalLaunchInheritsShipHeading = true;

        // The game rate-limits pitch, yaw and roll with ONE Quaternion.RotateTowards sharing a single
        // budget (WeaponBase.cs:1769-1771), not one budget per axis, so modelling the axes
        // independently spends that budget twice. The caller passes turnRate = _maxTurnRateDegrees
        // (setCourseTowardsPosition:1583-1600); its G limiter never bites, MaxTurnG defaulting to 200
        // (AmmunitionParameters.cs:1733), and the three-budget branch needs TerminalApproach, so the
        // combined branch is the operative one. Reached only for Kinematics == None (:1599).
        //
        // Rotating from (pitch 90, yaw 90) to (pitch 0, yaw 0) is 120 deg of quaternion travel
        // against max(90, 90) = 90 deg under independent limiting, so at 10 deg/s the turn takes 12s
        // rather than 9s. With no yaw demand the quaternion angle equals |dPitch| exactly and
        // RotateTowards reduces to MoveTowards, so an on-bearing shot cannot be disturbed.
        private const bool CoupledPitchYawRateLimit = true;

        // A NON-KINEMATIC missile with SupportsBanking=True gets a SECOND rotation call every physics
        // tick: `setCourseTowardsPositionLegacy` runs its normal RotateTowards at MaxTurnRate, then
        // `performToTargetRoll` runs another at a hardcoded 60 deg/s (WeaponBase.cs:1773-1776 and
        // :1789-1792). That second call's target is `Euler(localEulerAngles.x, localEulerAngles.y,
        // num)`, local euler read back but assigned to WORLD rotation, which is gimbal-degenerate
        // near vertical, so after a VLS launch its budget lands largely on PITCH rather than roll.
        // The two budgets therefore sum: a banking round noses over at MaxTurnRate + 60 deg/s.
        private const bool BankingAddsRollBudgetToPitch = true;
        private const float BankingRollRateDeg = 60f;   // WeaponBase.cs:1792, hardcoded

        // The 90 deg boost climb applies ONLY above the air-density line, not to kinematic ammo
        // generally. A missile lofting above the atmosphere exceeds its commanded angle and flies
        // vertical; one lofting inside it flies its own MaxLoftAngle, whatever that angle is.
        // `AllowExceedingAngleLimits = (Kinematics != None)` (Missile.cs:2197) explains why
        // exceeding is permitted; it does not say the command is 90, which is why the boost climb is
        // scoped to isHighBallisticLofter rather than to kinematic ammo as a whole.

        // `launch-rail` reports pure LAUNCHER GEOMETRY -- no dependence on the missile flying, or on
        // firing at all -- so it is emitted from the planning path rather than behind emitDiag:
        // selecting a target dumps every launcher on the ship. Keyed by (unit, ammo) and re-emitted
        // only when the rail actually MOVES, so a trainable mount's slew from parked to its firing
        // elevation is captured (a few lines) while a fixed rail logs once and goes quiet.
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLogged =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLoggedAz =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, int> _railLogCount =
            new System.Collections.Generic.Dictionary<string, int>();
        private const float RailRelogDeltaDeg = 2f;
        private const int RailMaxLogsPerKey = 24;   // a deliberate heading sweep needs headroom
        /// <summary>Compass yaw of a flattened heading vector, in Unity's euler-y convention.</summary>
        /// <summary>
        /// How far off-bearing a shot is at launch: the horizontal angle between the shooter's
        /// heading and the bearing to the target. Diagnostic only, and a CORRELATE rather than the
        /// game's own quantity, which measures against the missile's forward vector (carrying launch
        /// pitch too) instead of the ship's heading in the horizontal plane. It is recorded so each
        /// shot carries its own launch geometry and the correlation against the ToBearing duration
        /// on the `stage-obs` line accumulates during ordinary play.
        /// </summary>
        /// <returns>Degrees, or -1 when unavailable.</returns>
        /// <summary>
        /// Where the final and terminal flight phases begin, and the altitudes and descent angles
        /// they command. Depends only on the ammunition's own parameters.
        /// </summary>
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
                    // A ship carries several launchers for one ammo, and for FIXED mounts they
                    // can point in completely different directions -- every Harpoon ship has two
                    // MK141s, Port and Starboard, 180 deg apart. `launchers[0]` is arbitrary, so
                    // taking it would read the wrong bearing half the time and apply a bogus
                    // ~180 deg turn. The round comes off whichever launcher bears, so pick the
                    // one whose horizontal rail direction is closest to the target.
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
                            // Trainable: elevation to the target unless the launcher uses a
                            // fixed angle, then the mount's own pitch removed and the result
                            // clamped to the elevation arc. This is the branch that governs a
                            // Standard missile fired at a surface ship.
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

                    // Horizontal analogue of railDeg: the angle between the rail's own bearing
                    // and the bearing to the target. A FIXED box launcher (neither mount nor
                    // containers rotatable) cannot train, so the missile leaves along the box's
                    // bearing and has to turn -- which `bearingErr` cannot see, because that
                    // measures the SHIP's heading. Meaningless when the rail is near-vertical
                    // (its horizontal component vanishes), which is every VLS shot.
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

                    // Re-emitted whenever the rail moves, so a trainable mount's slew from
                    // parked to firing elevation shows up next to `predicted` and the formula
                    // above is measured rather than assumed.
                    string railKey = unit.GetInstanceID() + "/" + (ap._ammunitionFileName ?? "?");
                    if (Coordinator.VerboseLog && vwp != null)
                    {
                        try
                        {
                            _railLogCount.TryGetValue(railKey, out int n);
                            bool firstSeen = !_railLogged.TryGetValue(railKey, out float prev);
                            bool elevMoved = !float.IsNaN(railDeg)
                                          && Mathf.Abs(railDeg - prev) > RailRelogDeltaDeg;
                            // A fixed box launcher never changes ELEVATION, so without this the
                            // line would never re-emit as the ship turns, and a launch-bearing sweep
                            // would be uninstrumentable.
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
            // Flat distance at which the model actually entered the terminal/dive phase. Unlike
            // TermDistU (the raw ini _terminalApproachDist) this is max(termDist, descentGeomDist),
            // so it reflects the altitude-dependent geometric ramp that governs high lofters.
            // -1 = the model never reached phase 2. Compare against the real TerminalApproach
            // transition reported by the `stage-obs` line.
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
            phases = default;
            ModelStats.SimStarted();
            try
            {
                EnsureSimLookup();
                if (!_simIsBeta || _thrustMethod == null) return -1f;
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                if (!nonKin && _dragMethod == null) return -1f;

                const float KU = GameUnits.KnotsToUnityPerSecond;
                const float dt = IntegrationStepSim;
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
                // Attitude carried ACROSS steps for the coupled turn. Rebuilding it from pitch and
                // heading every step refunds the budget spent on roll, because yawing a nose-up
                // airframe is mostly roll and the forward-vector readback throws that roll away, so
                // the turn finishes at the independent-limit rate instead of the coupled one.
                // Re-seeded (roll zero) on every step that does not take the coupled branch, so a
                // shot entering the gate part way through does not start from a stale attitude.
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
                        // cannot falsify a fixed-offset-plus-drift decomposition (any two points fit
                        // exactly); three can, and this makes that fit mechanical.
                        $", range {flatDistTotal:0}u ({flatDistTotal * GameUnits.MetersPerUnity / 1000f:0.0}km)" +
                        // Both values, so the ini default and the rail's real orientation can be
                        // compared directly on one line.
                        $", iniPitch {(launchPitchIni >= 0f ? launchPitchIni.ToString("0.0") + "°" : "n/a")}" +
                        // Sampled here, on the fired-shot path, because the planning-path launch-rail
                        // reading can be stale: the ship keeps turning between planning and launch.
                        // One shot logged railAz 177.6 at planning yet flew on-bearing.
                        $", railAz {railAzTxt}");

                // Step count is derived from t rather than counted, since t advances by exactly dt
                // per iteration. Every exit below must call ModelStats.LoopDone; there are four.
                ModelStats.SetupDone();
                while (t < maxFlight)
                {
                    Vector3 predTgt = targetPos + targetVel * t;
                    float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                    float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                    if ((flatDist > prevFlat && t > dt) || flatDist < CloseEnoughDistU)
                    {
                        ModelStats.LoopDone((int)(t / dt));
                        if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
                        return t;
                    }
                    prevFlat = flatDist;

                    Vector3 horizDir = flatDist > 1e-4f
                        ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;
                    Vector3 horizDirTarget = horizDir;   // where the round wants to point
                    bool coupledTurn = CoupledPitchYawRateLimit && nonKin
                                    && launchHeading.sqrMagnitude > 0.5f && t >= initialPhaseDur;

                    // Fixed-rail launch heading (see FixedRailLaunchHeading): fly the rail's bearing
                    // for the initial flight phase, then turn toward the target at MaxTurnRate --
                    // the horizontal mirror of the launchPitch hold further down. Approximation
                    // noted: the game rate-limits COMBINED pitch+yaw in one Quaternion.RotateTowards
                    // (WeaponBase.cs:1770), while this limits heading and pitch independently. For an
                    // abeam launch the turn is overwhelmingly yaw, so the error is small.
                    if (launchHeading.sqrMagnitude > 0.5f)
                    {
                        // Heading slews on its own only on the independent path. The coupled path
                        // turns heading and pitch together further down, out of one budget.
                        if (t >= initialPhaseDur && !coupledTurn)
                            launchHeading = Vector3.RotateTowards(
                                launchHeading, horizDir, turnRate * Mathf.Deg2Rad * dt, 0f).normalized;
                        horizDir = launchHeading;
                    }

                    float pitchDeg = 0f;
                    int phase = 1;
                    float stageTgt = maxVelKn;
                    {
                        float stageAlt;
                        float descentGeomDist = (pos.y - termAlt)
                                              / Mathf.Tan(Mathf.Max(descentOnsetDeg, MinDescentOnsetDeg) * Mathf.Deg2Rad);
                        float diveStart = Mathf.Max(termDist, descentGeomDist);
                        if (diveStart > 0f && flatDist <= diveStart)
                        { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                        else if (finalDist > 0f && flatDist <= finalDist)
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }
                        else if (lofting)
                        { stageTgt = loftVelKn; stageAlt = loftAlt; phase = 0; }
                        else
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }

                        float altErr = stageAlt - pos.y;
                        float targetPitch = 0f;
                        float diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg;

                        // The game's ToBearing test: angle between the missile's attitude and the
                        // line to its aim point, against a 5 deg cone, with a 10.0s cap
                        // (Missile.cs:343). The horizontal component is already aligned on every
                        // shot fired near its own bearing, so in practice this reduces to elevation.
                        float elevToTgtDeg = Mathf.Atan2(predTgt.y - pos.y,
                                                         Mathf.Max(flatDist, 1e-4f)) * Mathf.Rad2Deg;
                        bool inToBearing = launchPitch >= 0f
                                        && t >= initialPhaseDur
                                        && t < initialPhaseDur + ToBearingMaxSeconds
                                        && Mathf.Abs(prevPitch - elevToTgtDeg) >= ToBearingConeDeg;
                        // Launch and ToBearing both command _maxVelocityInKnots (Missile.cs:3142).
                        if (LaunchStageSpeed && (t < initialPhaseDur || inToBearing))
                            stageTgt = maxVelKn;

                        bool holdingAlt = false;
                        if (LatchedProportionalHold)
                        {
                            if (phase != altLatchPhase)
                            { altLatchPhase = phase; altLatched = false; prevAltErr = float.NaN; }
                            // Arrival = the altitude error CHANGES SIGN, i.e. the missile has crossed
                            // the stage altitude. A crossing cannot be stepped over; a position band
                            // can, and was -- at cruise this loop moves 3-5.5u of altitude per 0.1s
                            // step against the 1u-wide AltToleranceU band, so the earlier
                            // |altErr| <= tol test never once fired.
                            //
                            // Latching on the FIRST crossing does not flatten the loft overshoot:
                            // the overshoot comes from the turn-rate limit, not from the bang-bang
                            // command. At the crossing the missile is near +90 deg pitch and
                            // MoveTowards needs ~7.5s to swing it down, climbing the whole way.
                            if (!altLatched && !float.IsNaN(prevAltErr) &&
                                ((prevAltErr > 0f) != (altErr > 0f)))
                                altLatched = true;
                            prevAltErr = altErr;
                            holdingAlt = altLatched;
                        }

                        if (holdingAlt)
                        {
                            // Pitch that actually reaches stageAlt over a lookahead, clamped to the
                            // ammo's own climb/dive limits. Same lookahead the TerminalLoft node
                            // glide below uses.
                            float holdLook = Mathf.Max(velKnots * KU * dt * LookaheadMultiplier, MinLookaheadU);
                            targetPitch = Mathf.Clamp(Mathf.Atan2(altErr, holdLook) * Mathf.Rad2Deg,
                                                      -diveDeg, boostClimbDeg);
                        }
                        else if (altErr > AltToleranceU) targetPitch = boostClimbDeg;
                        else if (altErr < -AltToleranceU) targetPitch = -diveDeg;

                        if (isTerminalLoft && lofting)
                        {
                            if (altNodes != null)
                            {
                                float xNow = flatDistTotal - flatDist;
                                float look = Mathf.Max(velKnots * KU * dt * LookaheadMultiplier, MinLookaheadU);
                                float altAhead = InterpNodeAlt(altNodes, Mathf.Min(xNow + look, flatDistTotal));
                                float slopeDeg = Mathf.Atan2(pos.y - altAhead, look) * Mathf.Rad2Deg;
                                targetPitch = -Mathf.Clamp(slopeDeg, -boostClimbDeg, descentDeg);
                            }
                            else
                            {
                                if (!tlGliding && pos.y >= loftAlt - AltToleranceU) tlGliding = true;
                                if (tlGliding)
                                {
                                    float glideDeg = Mathf.Atan2(Mathf.Max(pos.y - targetAlt0, 0f),
                                        Mathf.Max(flatDist, 1f)) * Mathf.Rad2Deg;
                                    targetPitch = -Mathf.Min(glideDeg, descentDeg);
                                }
                            }
                        }

                        if (launchPitch >= 0f && t < initialPhaseDur)
                            targetPitch = launchPitch;

                        if (coupledTurn)
                        {
                            // One RotateTowards for pitch and yaw together, as the live mover does.
                            // Unity's euler x is nose-DOWN positive, so our climb-positive pitch is
                            // negated going in and read back off the forward vector coming out.
                            // The target carries no roll, so whatever roll the turn builds up
                            // bleeds back out on its own as the round settles on the bearing.
                            Quaternion tgt = Quaternion.Euler(-targetPitch, YawOf(horizDirTarget), 0f);
                            att = Quaternion.RotateTowards(att, tgt, turnRate * dt);
                            Vector3 fwd = att * Vector3.forward;
                            pitchDeg = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                            Vector3 fh = new Vector3(fwd.x, 0f, fwd.z);
                            if (fh.sqrMagnitude > 1e-6f)
                            {
                                launchHeading = fh.normalized;
                                horizDir = launchHeading;
                            }
                        }
                        else
                        {
                            pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, turnRate * dt);
                            // Not on the coupled path this step (initial phase, kinematic ammo, or
                            // no launch heading): keep the carried attitude in step with where the
                            // round actually points, roll zero.
                            att = Quaternion.Euler(-pitchDeg, YawOf(horizDir), 0f);
                        }
                    }
                    float pitchRate = (pitchDeg - prevPitch) / dt;

                    float thrust;
                    if (_thrustFn != null) thrust = _thrustFn(ap, isAir, t, dt);
                    else
                    {
                        thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                        thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                    }
                    bool motorBurning = thrust > 0f;

                    float dragThisStep = 0f;
                    if (nonKin)
                    {
                        if (velKnots > stageTgt)
                            velKnots -= Mathf.Min(decelPerStep, velKnots - stageTgt);
                        else if (velKnots < stageTgt - VelocityEpsilonKn)
                            velKnots += Mathf.Min(thrust, stageTgt - velKnots);
                    }
                    else
                    {
                        velKnots += thrust;
                        bool inVacuumDive = phase == 2 && pos.y > ZeroDensityAltU && pitchDeg < VacuumDivePitchThreshold;
                        float dragTargetAlt = inVacuumDive ? pos.y : predTgt.y;
                        if (_dragFn != null)
                            dragThisStep = _dragFn(pos.y, velKnots * KU, dt, -pitchDeg, dragFactor,
                                                   motorBurning, dragTargetAlt, ap.LiftFactor,
                                                   ap.MinVelocity, -pitchRate);
                        else
                        {
                            dragArgs[0] = pos.y; dragArgs[1] = velKnots * KU; dragArgs[2] = dt; dragArgs[3] = -pitchDeg;
                            dragArgs[4] = dragFactor; dragArgs[5] = motorBurning;
                            dragArgs[6] = dragTargetAlt;
                            dragArgs[7] = ap.LiftFactor; dragArgs[8] = ap.MinVelocity; dragArgs[9] = -pitchRate;
                            dragThisStep = (float)_dragMethod.Invoke(null, dragArgs);
                        }
                        velKnots -= dragThisStep;
                        // No stage-speed clamp here: kinematic ammo is thrust minus drag, uncapped.
                        // The live mover adds thrust unconditionally for Kinematics != None
                        // (Missile.cs:3151); the stage target is consumed only by the other branch.
                    }

                    if (velKnots < MinSpeedKn) { ModelStats.LoopDone((int)(t / dt)); return -1f; }

                    float pr = pitchDeg * Mathf.Deg2Rad;
                    Vector3 dir = horizDir * Mathf.Cos(pr) + Vector3.up * Mathf.Sin(pr);
                    pos += velKnots * KU * dt * dir;

                    if (phase == 0) { phases.ClimbTime += dt; phases.VClimbExit = velKnots; }
                    else if (phase == 1) { phases.CruiseTime += dt; phases.VCruiseExit = velKnots; }
                    else
                    {
                        phases.DescentTime += dt;
                        // First step in the dive: stamp where the model committed to descend, so the
                        // `stage-model` line can be read straight against the real TerminalApproach
                        // distance from `stage-obs`.
                        if (phases.DiveStartU < 0f) phases.DiveStartU = flatDist;
                    }
                    phases.VTerm = velKnots;
                    if (pos.y > phases.PeakAltU) phases.PeakAltU = pos.y;

                    if (trackDiag && t + dt >= nextSample)
                    {
                        float slantKm = (predTgt - pos).magnitude * GameUnits.MetersPerUnity / 1000f;
                        // hdgErr and roll: without them the trace shows only the projection the
                        // coupled turn writes to, so a turn that never slows down looks identical
                        // to one that does. hdgErr is signed, roll folded to +/-180.
                        float hdgErr = Mathf.DeltaAngle(YawOf(horizDir), YawOf(horizDirTarget));
                        float rollDeg = Mathf.DeltaAngle(0f, att.eulerAngles.z);
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] sim-track {ammoLabel}: t+{t:0.0}s spd {velKnots:0}kn alt {pos.y:0.0} " +
                            $"pitch {pitchDeg:0} hdgErr {hdgErr:0} roll {rollDeg:0} " +
                            $"drag {dragThisStep / dt:0}kn/s phase {phase} " +
                            $"dist {flatDist * GameUnits.MetersPerUnity / 1000f:0.0}km slant {slantKm:0.0}km");
                        nextSample += (t < NoseOverWindowSim) ? NoseOverIntervalSim
                                    : (t < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
                                    : TelemetrySampleIntervalSim;
                    }

                    prevPitch = pitchDeg;
                    t += dt;
                }
                ModelStats.LoopDone((int)(t / dt));
                return -1f;
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] integrated flight-time failed: {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }
    }
}
