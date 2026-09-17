using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        // The production step. A 60 Hz step was compared offline over the recorded flights and did
        // not improve the estimates.
        private const float IntegrationStepSim = 0.1f;
        // Altitude at which the game's air-density curve (1 - 0.00163*h)^4.256 reaches zero. Used by
        // the setup half to classify a high ballistic lofter and by the step loop to arm the vacuum
        // brake, so it is declared once for the whole partial class rather than in each.
        internal const float ZeroDensityAltU = 1f / 0.00163f;
        private const float AltToleranceU = 0.5f;
        // The game's own ini defaults (AmmunitionParameters' ini reads): MaxLoftAngle 30,
        // SeaSkimmingMaxDescentAngle 30, FinalFlightPhaseMaxAngle 30. Cited by key rather than line, since
        // the decompile renumbers on every game update.
        private const float DefaultClimbDeg = 30f;
        private const float DefaultDescentDeg = 30f;
        private const float BoostClimbDeg = 90f;
        private const float DefaultTurnRateDeg = 5f;   // AmmunitionParameters, ini key "MaxTurnRate"
        private const float MinDescentOnsetDeg = 5f;
        // A launcher elevates between horizontal and straight up; anything outside that is a bad
        // read of the rail transform rather than a real aim point.
        private const float MaxLaunchElevationDeg = 90f;
        // An ini angle field left at its default reads as 0. This is the "was it actually set"
        // threshold, well under any angle a real round commands.
        internal const float SetAngleEpsilonDeg = 0.01f;
        /// <summary>
        /// SolveInput.SeekerBrakeRangeU for an ammunition that can never open the drag brake window.
        /// Negative, because zero is the struct default meaning "not known", so an initializer that omits the
        /// field falls back to the altitude gate.
        /// </summary>
        internal const float NoSeekerBrakeWindow = -1f;
        /// <summary>g in knots per second, for converting an ini deceleration in g into knots/s.</summary>
        internal const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        /// <summary>Share of the launch range a round must take off the range before the
        /// closest-approach guard will believe it has closed. See the latch note in Solve.</summary>
        private const float RecedeCloseFraction = 0.002f;
        // Sampling cadence for the `sim-track` trace, defined once in TelemetryCadence so the live `track`
        // trace samples at the same offsets. Aliased so the step loop keeps its identifiers and generated code.
        private const float TelemetrySampleIntervalSim = TelemetryCadence.SampleIntervalSim;
        private const float LaunchBurstWindowSim = TelemetryCadence.LaunchBurstWindowSim;
        private const float LaunchBurstIntervalSim = TelemetryCadence.LaunchBurstIntervalSim;
        private const float NoseOverWindowSim = TelemetryCadence.NoseOverWindowSim;
        private const float NoseOverIntervalSim = TelemetryCadence.NoseOverIntervalSim;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        // The altitude controller's horizon, in seconds, so changing the step changes only the numerical resolution.
        private const float LookaheadSim = 2f;
        private const float MinLookaheadU = 50f;
        // Waypoint laws shared by every waypoint type, Kappa and Immediate alike. UpdateWaypoint
        // advances a waypoint's progress by at most 2.5 times this tick's flat travel, and steers at
        // a point 1.5 s ahead (DesiredPosition) while the pre-apex guard looks 8 s ahead
        // (FuturePosition), Waypoint.cs:383-387. Named so they cannot be mistaken for per-weapon tuning.
        private const float WaypointProgressCatchupMultiplier = 2.5f;
        private const float WaypointSteeringLookaheadSim = 1.5f;
        private const float WaypointFutureLookaheadSim = 8f;
        private const float WaypointDistanceEpsilonU = 0.001f;
        internal const int SampledCurveMinimumNodeCount = 2;

        // Fly a non-kinematic round along its waypoint route (FlightTime.Waypoints.cs) instead of
        // commanding a climb or dive angle toward a stage altitude. Settable so the lab can score
        // the same build with it on and off.
        internal static bool WaypointLegSteering = true;
        // BuildAltitudeNodes only constructs a loft curve when its loft leg exceeds one game unit.
        private const float MinimumLoftLegDistanceU = 1f;

        // ToBearing's exit condition, which decides when the launch stage ends. It selects the commanded
        // speed below; it does not command an attitude.
        private const float ToBearingConeDeg = 5f;      // Missile.cs:343
        private const float ToBearingMaxSeconds = 10f;  // Missile.cs:343

        private const float BankingRollRateDeg = 60f;   // WeaponBase.cs:1792, hardcoded

        // Except while the ToBearing turn-rate override is in force: the rate then differs from MaxTurnRate, and
        // WeaponBase.setCourseTowardsPositionLegacy rotates roll, yaw and pitch in three calls, pitch at its own rate
        // (plan_yj-18a flies 50 deg/s through ToBearing, its LaunchTurnRate). Settable so the lab can score both.
        internal static bool ToBearingOverrideOwnsPitch = true;

        // The 90 deg boost climb is scoped to isHighBallisticLofter, not to kinematic ammo at large:
        // AllowExceedingAngleLimits (Missile.cs:2197) permits exceeding the commanded angle but does
        // not make the command 90. A round lofting inside the atmosphere flies its own MaxLoftAngle.

        // `launch-rail` reports launcher geometry independent of any missile, so it is emitted from the
        // planning path rather than behind emitDiag. Keyed by (unit, ammo) and re-emitted only when the rail moves.
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
            ObjectBase unit, AmmunitionParameters ap, WeaponSystem launcher, Vector3 launchPos, Vector3 targetPos)
        {
            float launchPitch = -1f;
            float launchPitchIni = -1f;
            Transform rail = null;
            bool fixedRail = false;
            string railAzTxt = "n/a";
            try
            {
                // The launcher is the caller's, picked once by ResolveLauncher, so the attitude and the
                // launch position describe the same one.
                if (launcher != null)
                {
                    var vwp = launcher._vwp;
                    if (vwp != null && vwp._fixVerticalLaunchAngleForLauncher)
                        launchPitchIni = vwp._fixVerticalLaunchAngle + vwp._additionalFixVerticalLaunchAngle;
                    launchPitch = launchPitchIni;

                    var ws = launcher;
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

                    float railDeg = rail != null ? GameMath.ElevationDeg(rail.forward) : float.NaN;

                    // A fixed launcher fires along the rail as built. A trainable one aims first, so its
                    // current transform is wherever it is parked and never a launch attitude; compute the
                    // game's own aim instead (alignToTarget:1360-1377; RotateWeaponToAngle:1644 makes that
                    // angle the gun's local elevation).
                    fixedRail = vwp != null
                             && !vwp._isMountRotatable && !vwp._areContainersRotatable;
                    float predictedPitch = -1f;
                    if (vwp != null)
                    {
                        if (fixedRail && !float.IsNaN(railDeg))
                        {
                            predictedPitch = Mathf.Clamp(railDeg, 0f, MaxLaunchElevationDeg);
                        }
                        else
                        {
                            // Trainable: elevation to the target unless the launcher fixes the
                            // angle, less the mount's own pitch, clamped to the elevation arc.
                            Vector3 toTgt = targetPos - launchPos;
                            float tgtElev = toTgt.sqrMagnitude > 1e-6f
                                ? GameMath.ElevationDeg(toTgt.normalized) : 0f;
                            float mountPitch = railMount != null
                                ? GameMath.ElevationDeg(railMount.forward) : 0f;
                            float baseElevDeg = vwp._fixVerticalLaunchAngleForLauncher
                                ? vwp._fixVerticalLaunchAngle : tgtElev;
                            float elevDeg = baseElevDeg + vwp._additionalFixVerticalLaunchAngle - mountPitch;
                            if (vwp._elevationArc.y > vwp._elevationArc.x)
                                elevDeg = Mathf.Clamp(elevDeg, vwp._elevationArc.x, vwp._elevationArc.y);
                            predictedPitch = Mathf.Clamp(elevDeg, 0f, MaxLaunchElevationDeg);
                        }
                    }

                    // Horizontal analogue of railDeg: rail bearing against bearing to target, which
                    // `bearingErr` (the ship's heading) cannot see. Meaningless for a near-vertical rail.
                    // Trace-only, so it is not formatted for every panel estimate.
                    float railAzDeg = float.NaN;
                    if (rail != null && Coordinator.TraceFlightModel)
                    {
                        Vector3 tt = GameMath.Flatten(targetPos - launchPos);
                        if (!GameMath.TryFlatDirection(rail.forward, out Vector3 rf)) railAzTxt = "vertical";
                        else if (tt.sqrMagnitude > 1e-6f)
                        {
                            railAzDeg = Vector3.Angle(rf, tt);
                            railAzTxt = railAzDeg.ToString("0.0") + "°";
                        }
                    }

                    // Re-emitted whenever the rail moves, so the formula above is measured rather than assumed.
                    if (Coordinator.TraceFlightModel && vwp != null)
                    {
                        string railKey = unit.GetInstanceID() + "/" + (ap._ammunitionFileName ?? "?");
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
                                    : GameMath.ElevationDeg(tr.forward).ToString("0.0") + "°";
                                Bootstrap.Log.LogInfo(
                                    // The sim stamp, so this trace (the best proxy for a manoeuvring
                                    // aircraft's heading through a launch wait) lines up against
                                    // envelope-track and engage-state.
                                    $"[AutoTOT] launch-rail {ap._ammunitionFileName} " +
                                    $"at sim {GameClock.SimNow():0.0}: " +
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

                    // Launch elevation from the launcher's container transform, not `_fixVerticalLaunchAngle`, which
                    // reads 35 deg for every launcher (the ini default, with its gating bool also defaulting true).
                    if (predictedPitch >= 0f)
                        launchPitch = predictedPitch;
                }
            }
            catch { launchPitch = launchPitchIni; }
            return new LaunchGeometry(launchPitch, launchPitchIni, rail, fixedRail, railAzTxt);
        }

        /// <summary>
        /// The launcher the engage task will take for this shot, or null when the unit has none for
        /// this ammunition. Read by both the launch position and the launch attitude.
        /// </summary>
        private static WeaponSystem ResolveLauncher(ObjectBase unit, AmmunitionParameters ap, Vector3 targetPos)
        {
            try
            {
                // Which launcher the round leaves from, asked the way the game asks. Fixed mounts for one
                // ammo can point 180 deg apart (two MK141s, port and starboard), and the round flies off its
                // own canister. WeaponSelector orders by firing arc, not rail bearing: preferred arc first in
                // declaration order, then firing arc, then nearest arc edge, then the rest; an arc can cover
                // a bearing its canisters point 120 deg away from. Element 0 is the launcher the engage task takes.
                var launchers = WeaponSelector.GetWeaponSystemsForAmmunition(
                    unit, targetPos, ap._ammunitionFileName, null);
                // Null or empty means every launcher was filtered out (all inoperable, offline, out
                // of ammo, or _engageOnlyInsideArc with the target outside). Fall back to the raw
                // list so a shot still gets an elevation rather than none.
                if (launchers == null || launchers.Count == 0)
                    launchers = unit.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
                return launchers != null && launchers.Count > 0 ? launchers[0] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The mean world position of every cell of <paramref name="launcher"/>. A round spawns as a child of
        /// its container's gun object at that container's attachment offset
        /// (<c>WeaponContainer.createAmmunitionObjectInstance</c>); the mean rather than the next cell sits
        /// within about 2 m of the spawn and needs no model of the firing order. False when the launcher has
        /// no cells, as an aircraft hardpoint does. Main thread only.
        /// </summary>
        internal static bool LauncherCellMean(WeaponSystem launcher, out Vector3 mean, out int cells)
        {
            mean = Vector3.zero;
            cells = 0;
            if (launcher?._containers == null) return false;
            try
            {
                Vector3 sum = Vector3.zero;
                foreach (WeaponContainer container in launcher._containers)
                {
                    if (container == null || container._gunObject == null || container._attachmentPositions == null)
                        continue;
                    Transform gun = container._gunObject.transform;
                    foreach (Vector3 attachment in container._attachmentPositions)
                    {
                        sum += gun.TransformPoint(attachment);
                        cells++;
                    }
                }
                if (cells == 0) return false;
                mean = sum / cells;
                return true;
            }
            catch
            {
                mean = Vector3.zero;
                cells = 0;
                return false;
            }
        }

        /// <summary>
        /// Where the final and terminal flight phases begin, and the altitudes and descent angles
        /// they command. Depends only on the ammunition's own parameters.
        /// </summary>
        private readonly struct StageProfile
        {
            internal readonly float FinalDist, TermDist, TermAlt, DescentDeg, DescentOnsetDeg;
            internal readonly bool TerminalLinear;
            /// <summary>Altitude of the outer cruise leg, before the final-flight phase begins.</summary>
            internal readonly float CruiseAlt;
            /// <summary>Where the final-flight phase begins, and the altitude it commands. The game
            /// runs these as two stages with two altitudes; see the note in ResolveStageProfile.</summary>
            internal readonly float FinalFlightDist, FinalFlightAlt;
            /// <summary>Distance-to-target outside which the round enters the loft at all. 0 = the
            /// ammunition has no sea-skimming boundary, so it always lofts.</summary>
            internal readonly float LoftEntryDist;
            internal StageProfile(float finalDist, float termDist, float termAlt,
                                  float descentDeg, float descentOnsetDeg, float cruiseAlt,
                                  float finalFlightDist, float finalFlightAlt, float loftEntryDist,
                                  bool terminalLinear)
            {
                FinalDist = finalDist; TermDist = termDist;
                TermAlt = termAlt; DescentDeg = descentDeg; DescentOnsetDeg = descentOnsetDeg;
                CruiseAlt = cruiseAlt; FinalFlightDist = finalFlightDist;
                FinalFlightAlt = finalFlightAlt; LoftEntryDist = loftEntryDist;
                TerminalLinear = terminalLinear;
            }
        }

        /// <summary>Resolve the live terminal waypoint's endpoint for the kinematic surface path.
        /// AAW endpoints are relative to the target; other surface endpoints are absolute, with an
        /// unset negative value represented by OnLineToTarget.</summary>
        internal static float ResolveKinematicTerminalEndAlt(AmmunitionParameters ap,
                                                              float targetAlt0)
        {
            return ap._targetType == Ammunition.Target.AAW
                ? targetAlt0 + ap._terminalAltUnity
                : (ap._terminalAltUnity < 0f ? targetAlt0 : ap._terminalAltUnity);
        }

        private static StageProfile ResolveStageProfile(AmmunitionParameters ap, float maxVelKn,
                                                        bool nonKin, bool isTerminalLoft,
                                                        bool isAir, float targetAlt0)
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
            // A TerminalLoft weapon has no terminal-approach leg: CreateWaypointConfigs guards that waypoint
            // with `ap._terminalApproachDist > 0f && !ap._terminalLoft` (Missile.cs:2228), and the same guard
            // rejects the terminal dive (:2158) and every runtime terminal branch (MissileSimulator.cs:601,
            // :745, :1231). Such a round flies the loft leg, which for kinematic ammunition is a KappaLoft
            // ending OnLineToTarget: a direct run at the target. Building the leg anyway would command a
            // short shot to climb toward cruise altitude for its whole flight.
            if (isTerminalLoft) termDist = 0f;
            // TerminalApproach needs the seeker to hold an echo for SearchForTargetsTime, not just the distance, and
            // the clock resets on every tick without one (Missile.cs:584-593), so the missile closes at cruise speed
            // past its nominal _terminalApproachDist.
            if (ap._searchForTargetsTime > 0f)
            {
                // Distance the missile still covers at cruise while the seeker searches.
                termDist = Mathf.Max(
                    termDist - maxVelKn * GameUnits.KnotsToUnityPerSecond * ap._searchForTargetsTime, 0f);
            }
            // Two altitudes: the game runs MaintainSeaSkimming at SeaSkimmingAlt and MaintainFinalFlightAlt
            // at FinalFlightPhaseAlt (Missile.cs:614-647), switching at FinalFlightPhaseDistToTarget.
            // Collapsing them fails a shot that starts inside the final-flight distance. FinalFlightPhaseAlt
            // defaults to SeaSkimmingAlt in the ini read, so for most ammunition the two are equal and the
            // switch is inert.
            float cruiseAlt = ap._seaSkimmingAltUnity > 0f ? ap._seaSkimmingAltUnity : finalAlt;
            float finalFlightAlt = ap._finalFlightPhaseAltUnity > 0f
                                 ? ap._finalFlightPhaseAltUnity : cruiseAlt;
            // NOT clamped to the loft boundary, deliberately: the game tests this distance on its
            // own, and for a LoftToSkim=False round it is the point that ends the loft as well.
            float finalFlightDist = ap._finalFlightPhaseDistToTargetUnity;
            // The real loft-entry test. See the note on the phase selector in FlightTime.Solve.cs.
            float loftEntryDist = Mathf.Max(ap._seaSkimmingStartDistToTargetUnity, 0f);
            // finalAlt feeds only this fallback: twelve shipped ammunition declare no TerminalAlt with
            // different sea-skimming and final-flight altitudes, so redefining it would move their terminal leg.
            bool terminalLinear = !nonKin && !isTerminalLoft && !isAir && termDist > 0f;
            float termAlt = terminalLinear
                ? ResolveKinematicTerminalEndAlt(ap, targetAlt0)
                : (ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt);
            // _finalFlightPhaseMaxAngle constrains the final-flight waypoint, which
            // Missile.CreateWaypointConfigs builds only when _finalFlightPhaseDistToTargetUnity is positive.
            // Without that leg the descent to cruise altitude rides the sea-skimming leg, constrained by
            // _seaSkimmingMaxDescentAngle.
            float seaSkimDescentDeg = ap._seaSkimmingMaxDescentAngle > SetAngleEpsilonDeg
                                    ? ap._seaSkimmingMaxDescentAngle : DefaultDescentDeg;
            float descentDeg = finalFlightDist > 0f && ap._finalFlightPhaseMaxAngle > SetAngleEpsilonDeg
                             ? ap._finalFlightPhaseMaxAngle
                             : seaSkimDescentDeg;
            float descentOnsetDeg = Mathf.Max(descentDeg,
                Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));
            return new StageProfile(finalDist, termDist, termAlt, descentDeg, descentOnsetDeg,
                                    cruiseAlt, finalFlightDist, finalFlightAlt, loftEntryDist,
                                    terminalLinear);
        }

        /// <summary>
        /// The seeker range that opens and closes the drag brake, in Unity units, or 0 when no such window
        /// can exist for this ammunition.
        ///
        /// <para>The live mover hands <c>CalculateDrag</c> the CurrentTarget's altitude when there is one and
        /// the round's own altitude when there is not (<c>Missile.cs:3170-3175</c>). That altitude's air
        /// density sits in a denominator floored at 0.001, so the choice is worth up to 800 times on the
        /// lift-induced term. <c>CurrentTarget</c> is null in exactly one case (<c>WeaponBase.cs:362</c>):
        /// the seeker is on, holds no echo, and the ammunition declares no <c>TargetMemory</c>.</para>
        ///
        /// <para>The window exists because the two tests use different distances. Activation
        /// (<c>ShouldActivateMissileSeeker</c>, from <c>MaintainHeight.takeAction</c>) uses a vector already
        /// flattened to the missile's altitude; detection (<c>ActiveRadarSeeker.isInRange</c>) is a 3-D
        /// magnitude. A round far above its target switches its seeker on long before it can see anything,
        /// and brakes in between; a sea skimmer crosses both at once and never brakes.</para>
        /// </summary>
        private static float ResolveSeekerBrakeRange(AmmunitionParameters ap)
        {
            // WeaponBase.cs:387. Target memory falls through to the intended target, so
            // CurrentTarget is never null and the game never brakes.
            if (ap._targetMemory) return NoSeekerBrakeWindow;
            // Missile.cs:382. Activation lives inside this branch; without it the seeker comes up elsewhere,
            // when the round is close enough to see and the window is already shut.
            if (!ap._midCourseCorrection.AllowsLauncherGuidance) return NoSeekerBrakeWindow;
            // WeaponBase.cs:374. An unguided seeker returns the intended target unconditionally.
            if (ap._guidanceType == AmmunitionParameters.GuidanceType.None) return NoSeekerBrakeWindow;
            float range = Mathf.Max(ap._seekerActiveRange, ap._seekerPassiveRange);
            return range > 0f ? range : NoSeekerBrakeWindow;
        }

        /// <summary>
        /// Distance-to-target at which a lofting missile stops flying at <c>MaxLoftVelocity</c>, for
        /// ammunition that does not stop at the boundary <see cref="ResolveStageProfile"/> names; 0 when the
        /// ordinary boundary applies.
        ///
        /// <para>The game gives a missile <c>_maxLoftVelocityInKnots</c> only in <c>MaintainLoftAlt</c>
        /// (<c>Missile.cs:3143</c>), and both distance-based exits from that stage sit behind one local flag
        /// (<c>Missile.cs:614-647</c>):</para>
        /// <code>
        /// else if (flag2 &amp;&amp; magnitude &lt; _finalFlightPhaseDistToTargetUnity &amp;&amp; flag &amp;&amp; num &lt;= 3) -> MaintainFinalFlightAlt
        /// else if (magnitude &lt; _seaSkimmingStartDistToTargetUnity &amp;&amp; flag &amp;&amp; num &lt;= 2 &amp;&amp; ...)  -> MaintainSeaSkimming
        /// else if (num &lt;= 1)                                                            -> MaintainLoftAlt
        /// </code>
        /// <para><c>Missile.cs:413</c> clears that flag for the whole flight when the ammunition declares
        /// <c>RequiresTargetToProceed</c>, and only seeker activation with a permitted target sets it back
        /// (<c>Missile.cs:426-433</c>), so the missile holds loft speed until the seeker comes up. Activation
        /// is <c>ShouldActivateMissileSeeker</c> (<c>BearingLaunchPrediction.cs:97-108</c>): inside the
        /// active or passive range, or unconditionally for TV homing. The altitude schedule is unaffected and
        /// stays where <see cref="ResolveStageProfile"/> puts it.</para>
        /// </summary>
        /// <returns>Flat distance to target in Unity units, or 0 when no hold applies.</returns>
        private static float ResolveLoftSpeedHoldDist(AmmunitionParameters ap)
        {
            // Every term is a gate the game applies before it can clear the flag. Missing any one
            // of them leaves flag true, the ordinary ladder runs, and the stage profile is right.
            if (!ap._requiresTargetToProceed) return 0f;
            // Missile.cs:382. The flag only exists inside this branch.
            if (!ap._midCourseCorrection.AllowsLauncherGuidance) return 0f;
            // Missile.cs:411. Unguided rounds never take the clearing branch.
            if (ap._guidanceType == AmmunitionParameters.GuidanceType.None) return 0f;
            // BearingLaunchPrediction.cs:103. A TV seeker reports active at any range, so the hold
            // is over before it starts.
            if (ap._guidanceType == AmmunitionParameters.GuidanceType.TVHoming) return 0f;
            // Either range activates the seeker, so the hold ends at whichever is reached first,
            // which is the larger distance.
            return Mathf.Max(Mathf.Max(ap._seekerActiveRange, ap._seekerPassiveRange), 0f);
        }

        // The mover's own literals (setCourseTowardsPosition writes 0.514444f and 9.8f), not GameUnits, so a
        // threshold derived from the game's arithmetic reads as the game's arithmetic.
        private const float MoverKnotsToMs = 0.514444f;
        private const float MoverGravityMs2 = 9.8f;

        /// <summary>
        /// Speed, in knots, at which a turn at <paramref name="turnRateDeg"/> reaches the ammunition's own
        /// G-limit. Above it the mover cuts the rate in proportion; below it the rate stands. 0 when no limit
        /// can apply, which turns the derate off.
        ///
        /// <para>The mover computes, every tick, from its current speed
        /// (<c>WeaponBase.setCourseTowardsPosition</c>, ahead of the Kinematics dispatch so both paths derate):</para>
        /// <code>
        /// v      = 0.514444 * knots            // m/s
        /// radius = v * (360 / rate) / (2*PI)   // m, the circle that rate traces at v
        /// g      = v^2 / (radius * 9.8)
        /// if (g &gt; MaxTurnG) rate *= MaxTurnG / g
        /// </code>
        /// <para>Substituting the radius makes g <c>v * rate * 2*PI / (360 * 9.8)</c>, linear in speed, so
        /// there is one speed where g equals the limit, and above it the surviving rate is <c>rate *
        /// threshold / knots</c>. Solved once here, the step loop needs a compare and, past it, one divide.</para>
        /// </summary>
        private static float ResolveTurnDerateThreshold(AmmunitionParameters ap, float turnRateDeg)
        {
            float maxTurnG = ap._maxTurnG;
            // A non-positive limit is an unset or malformed ini value (one ammunition writes `MaxTurnG=` with
            // no number), not an order to freeze the round.
            if (maxTurnG <= 0f || turnRateDeg <= VelocityEpsilonKn) return 0f;
            float thresholdMs = maxTurnG * 360f * MoverGravityMs2
                              / (2f * Mathf.PI * turnRateDeg);
            return thresholdMs / MoverKnotsToMs;
        }

        /// <summary>
        /// The turn rate the ToBearing window is floored at: the mover floors the rate at
        /// <c>LaunchTurnRate</c> while <c>toBearingState</c> is set, when the ini value exceeds the rate in
        /// hand (<c>WeaponBase.setCourseTowardsPosition</c>). <c>LaunchTurnRate</c> defaults to -1, so this
        /// returns 0 for all but three shipped ammunition.
        ///
        /// <para>Returned unclamped, 0 meaning no floor. The comparison belongs in the step loop because the
        /// mover applies the floor after the G-derate, so an ammunition can sit below its launch rate at
        /// speed and above it when slow. Clamping here against the un-derated <c>MaxTurnRate</c> would drop
        /// that case and re-floor every derated ToBearing step to the un-derated rate, cancelling the derate
        /// on exactly the rounds it exists for.</para>
        /// </summary>
        private static float ResolveToBearingTurnRate(AmmunitionParameters ap)
            => ap._toBearingTurnRateDegrees > 0f ? ap._toBearingTurnRateDegrees : 0f;

        /// <summary>
        /// How far off-bearing a shot is at launch: the horizontal angle between the shooter's heading and
        /// the bearing to the target. Diagnostic only, and a correlate: the game measures against the
        /// missile's forward vector, launch pitch included.
        /// </summary>
        /// <returns>Degrees, or -1 when unavailable.</returns>
        private static float LaunchBearingErrDeg(ObjectBase unit, Vector3 launchPos, Vector3 targetPos)
        {
            try
            {
                Vector3 shipFwd = GameMath.Flatten(unit.transform.forward);
                Vector3 toTarget = GameMath.Flatten(targetPos - launchPos);
                if (shipFwd.sqrMagnitude > 1e-6f && toTarget.sqrMagnitude > 1e-6f)
                    return Vector3.Angle(shipFwd, toTarget);
            }
            catch { }
            return -1f;
        }

        /// <summary>Resolve the target-dependent SelectivePopUp gate while the Unity objects are
        /// still available. The pure solver only receives the resulting trajectory choice.</summary>
        private static bool ResolveTerminalPopUpEnabled(AmmunitionParameters ap, ObjectBase target)
        {
            if (ap._terminalPopUpDist <= 0f) return false;
            if (!ap._selectivePopUp || target == null) return true;
            if (target is Vessel vessel) return vessel.VP != null && vessel.VP._displacement <= 420f;
            return target is Submarine;
        }

        /// <summary>Compass yaw of a flattened heading vector, in Unity's euler-y convention. Internal
        /// so the lab's scorer rebuilds a launch attitude with this function rather than a copy.</summary>
        internal static float YawOf(Vector3 h) => Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg;

        internal static void ClearRailLog()
        { _railLogged.Clear(); _railLoggedAz.Clear(); _railLogCount.Clear(); }
#if AUTOTOT_OFFLINE
        /// <summary>Offline entry for tools/lab `kappacheck`.</summary>
        internal static float InterpNodeAltOffline(Vector2[] nodes, float x) => InterpNodeAlt(nodes, x);
#endif

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
            // Flat distance to the ship at the model's final-flight switch, -1 when it never
            // switched. Read against the recorded MaintainFinalFlightAlt event, as DiveStartU is
            // against TerminalApproach.
            public float FinalFlightStartU;
            public float DescentOnsetDeg;
        }

        /// <summary>
        /// The traced integration behind `int-phases`, `sim-launch` and `sim-track`, from the round's
        /// recorded <paramref name="launch"/> state so the trace describes the rail it actually flew,
        /// the same input its at-launch estimate and its capture now use.
        /// </summary>
        internal static bool TryIntegratedPhaseDiag(ObjectBase unit, string ammoId, ObjectBase target,
            in LaunchState launch, out float interceptTime, out IntegratedPhases phases)
        {
            interceptTime = -1f; phases = default;
            if (unit == null || target == null) return false;
            AmmunitionParameters ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;
            interceptTime = IntegratedEndTimeCore(unit, ap, target, out phases, emitDiag: true, launch);
            return phases.Valid;
        }

        // Ammunition whose setup fell back from a game-supplied altitude input, warned once each. Main thread only.
        private static readonly System.Collections.Generic.HashSet<string> _setupFallbackWarned =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Say, once per ammunition and cause, that setup replaced a value the game supplies with the model's
        /// own; either replacement changes the altitude law a round is flown on, worth seconds on a lofting round.
        /// </summary>
        private static void WarnSetupFallback(AmmunitionParameters ap, string what, Exception e)
        {
            string ammo = ap?._ammunitionFileName ?? "?";
            if (!_setupFallbackWarned.Add(ammo + "/" + what)) return;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] estimator fallback {ammo}: {what} threw ({e.GetType().Name}: {e.Message}), " +
                $"so the round is flown on the model's own altitude law instead of the game's. " +
                $"Flight-time estimates for it may be several seconds off.");
        }

        /// <summary>
        /// Capture the solve input for one round that has just left the rail, from its recorded
        /// <paramref name="launch"/> state, and return the key it was filed under so the tracker can pair the
        /// round's outcome with it; null when the input cannot be built or the dump fails. Per round, not per
        /// solve: the estimator caches by shooter, ammunition and target, so a solve is not a round. The
        /// stored <c>LaunchHeading</c> is the rail the round flew, which differs from the live pick when a
        /// ship with two fixed launchers for one ammunition hands the round the other one.
        /// </summary>
        internal static string DumpInputFor(ObjectBase unit, string ammoId, ObjectBase target,
                                            in LaunchState launch)
        {
            try
            {
                if (!TryKey(unit, ammoId, target, out AmmunitionParameters ap, out _)) return null;
                // emitDiag false: sim-launch and sim-track are emitted by the estimate this round
                // already ran, and repeating them here would double every traced line.
                if (!TryBuildSolveInput(unit, ap, target, out SolveInput input, out _, false, launch))
                    return null;
                // The parity contract with tools/lab: this build's step-loop answer for exactly this input at
                // the production step, which the lab must reproduce. EstAtLaunch uses the same launch state,
                // so the two agree wherever the integrator ran; EstAtLaunch falls back to the live tiers
                // where it declines, and this value does not.
                IntegratedPhases parityPhases = default;
                parityPhases.DiveStartU = -1f;
                // Solve ends in ModelStats.LoopDone, which times against this thread's loop stamp.
                // Stamp it here, or the profiler charges the time since the last setup to the loop.
                ModelStats.LoopStarting();
                float inGameEstimate = Solve(in input, ap, ref parityPhases);
                return SolveInputDump.Write(input.AmmoLabel, in input, ap,
                                            inGameEstimate, IntegrationStepSim);
            }
            catch (Exception e)
            {
                ModLog.Warn("solve-input capture failed", e);
                return null;
            }
        }

        /// <summary>
        /// The altitude a non-cruise round is commanded to hold during the loft stages at a given distance
        /// flown (Missile.cs:3306 onwards). A cruise missile (Kinematics None, the game's CruiseMissile test)
        /// is commanded <c>_maxLoftAltUnity</c> and handled by the caller. Everything else gets a profile:
        /// <c>_maxLoftAlt</c> is a ceiling it may never reach. The apex is where the climb angle out of the
        /// launch point meets the descent angle into the target, clamped by that ceiling, so a short shot
        /// peaks well below it and flies in denser air.
        /// </summary>
        internal static float LoftProfileAltU(bool ballisticLoft, float ceilingU,
                                              float climbTan, float descentTan,
                                              float launchAltU, float targetAltU,
                                              float totalFlatU, float travelledFlatU)
        {
            if (totalFlatU <= 0f) return ceilingU;

            float apexCeiling = Mathf.Max(Mathf.Max(ceilingU, launchAltU), targetAltU);

            if (ballisticLoft)
            {
                // Split weighted by the square root of each side's height, then a quadratic ease
                // out of the launch point and into the target.
                float upLeg = Mathf.Sqrt(Mathf.Max(apexCeiling - launchAltU, 0f));
                float downLeg = Mathf.Sqrt(Mathf.Max(apexCeiling - targetAltU, 0f));
                float split = (upLeg + downLeg > 0.0001f)
                    ? Mathf.Clamp(upLeg / (upLeg + downLeg), 0f, 0.95f) : 0.5f;
                float apexAt = totalFlatU * Mathf.Max(split, 0.0001f);

                if (travelledFlatU < apexAt)
                {
                    float toGo = 1f - travelledFlatU / apexAt;
                    return apexCeiling - (apexCeiling - launchAltU) * toGo * toGo;
                }

                float past = Mathf.Clamp01((travelledFlatU - apexAt)
                                           / Mathf.Max(totalFlatU - apexAt, 0.0001f));
                return apexCeiling - (apexCeiling - targetAltU) * past * past;
            }

            // Each angle is widened if the ceiling could not be reached at that slope over the whole
            // shot, so the tent always spans the flight.
            float climb = Mathf.Clamp(Mathf.Max(climbTan, (apexCeiling - launchAltU) / totalFlatU),
                                      0.01f, 99f);
            float descent = Mathf.Clamp(Mathf.Max(descentTan, (apexCeiling - targetAltU) / totalFlatU),
                                        0.01f, 99f);
            float climbRun = 1f / climb;
            float descentRun = 1f / descent;

            // Where the two slopes cross, capped by the ceiling: this is the apex the round
            // actually flies to, and on a short shot it is far below _maxLoftAlt.
            float crossing = (totalFlatU + launchAltU * climbRun + targetAltU * descentRun)
                             / (climbRun + descentRun);
            float apex = Mathf.Min(ceilingU, crossing);
            apex = Mathf.Max(Mathf.Max(apex, launchAltU), targetAltU);

            float upRun = (apex - launchAltU) * climbRun;
            float downRun = (apex - targetAltU) * descentRun;
            float apexFraction = upRun / (upRun + downRun + 0.00001f);
            float apexDist = totalFlatU * apexFraction;

            if (travelledFlatU < apexDist)
                return Mathf.Lerp(launchAltU, apex,
                                  Mathf.Pow(travelledFlatU / apexDist, 0.5f));

            return Mathf.Lerp(apex, targetAltU,
                              Mathf.Pow((travelledFlatU - apexDist)
                                        / Mathf.Max(totalFlatU - apexDist, 0.0001f), 1.3f));
        }

        private static float IntegratedEndTimeCore(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out IntegratedPhases phases, bool emitDiag,
            in LaunchState launch = default)
        {
            if (!TryBuildSolveInput(unit, ap, target, out SolveInput input, out phases, emitDiag, launch))
                return -1f;
            ModelStats.SetupDone();
            return Solve(in input, ap, ref phases);
        }

        /// <summary>
        /// The setup half: everything that reads Unity state or game state the game mutates, which is
        /// therefore main-thread only. Produces the immutable input the step loop runs on, so the
        /// same setup feeds both the synchronous path and the worker pool. False means the integrator
        /// declines and the caller should fall through to the next tier.
        ///
        /// With a <see cref="LaunchState"/> the setup answers for the shot as it ACTUALLY left the
        /// rail (position, speed and rail bearing at that instant) rather than for a hypothetical
        /// shot from the platform's current position. Everything downstream is unchanged: the same
        /// step loop runs on the same immutable input, which is the point of this seam.
        /// </summary>
        internal static bool TryBuildSolveInput(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out SolveInput input, out IntegratedPhases phases, bool emitDiag,
            in LaunchState launch = default)
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

                // The override is set only by the retargeting diagnostic; see LaunchState.TargetPosU.
                // Velocity still comes from the live target, which is right: a ship's course holds
                // far better over a flight than its position does.
                Vector3 targetPos = (launch.Valid && launch.TargetPosU != Vector3.zero)
                                  ? launch.TargetPosU : target.transform.position;
                // The launcher the engage task will take, picked once for both the launch position
                // and the launch attitude below.
                WeaponSystem launcher = ResolveLauncher(unit, ap, targetPos);
                // A ship's round leaves from one of its launcher's cells, not the ship's centre (tens of
                // metres away on a VLS ship). A round in flight passes its own position in `launch`; a unit
                // without cells (an aircraft hardpoint) keeps its own.
                Vector3 launchPos = launch.Valid ? launch.PosU : unit.transform.position;
                int launchCells = 0;
                if (!launch.Valid && LauncherCellMean(launcher, out Vector3 cellMean, out launchCells))
                    launchPos = cellMean;
                // What the ship will cover, not what it reports; see TargetMapVelocity. The same
                // rounded value feeds the loft search below, so both halves of the setup agree.
                Vector3 targetVel = TargetMapVelocity(target, out float targetMoveStep);
                bool isAir = unit.IsAirUnit;

                ApplyEvasiveBoost(ap, target, launchPos, targetPos, ref targetVel);

                float dragFactor = ap.GetDragFactor(isAir);
                float startVelKnots = Mathf.Max(launch.Valid ? launch.VelKnots
                                                             : unit._velocityInKnots, 0f);
                float maxFlight = Mathf.Min(
                    ap._maxFlightTime > 0f ? ap._maxFlightTime : MaxFlightTimeFallback,
                    SolveLoopCeilingSim);
                float targetAlt0 = Mathf.Max(targetPos.y, 0f);
                float flatDistTotal = GameMath.FlatDistance(targetPos, launchPos);

                float configuredLoftAlt = -1f;
                if (_loftCapMethod != null)
                {
                    float cap = (float)_loftCapMethod.Invoke(null,
                        new object[] { ap, Mathf.Max(launchPos.y, 0f), targetAlt0 });
                    float floor = Mathf.Max(Mathf.Max(launchPos.y, 0f), targetAlt0);
                    if (cap > floor + AltToleranceU)
                        configuredLoftAlt = cap;
                }
                float loftAlt = configuredLoftAlt;
                // The live non-cruise path does not fly the configured ceiling: Missile.TuneWaypoints
                // replaces its KappaLoft waypoint's LoftHeight with this range-dependent choice before launch.
                if (!nonKin && configuredLoftAlt > 0f && _optimalLoftMethod != null)
                {
                    float altMin = Mathf.Max(Mathf.Max(launchPos.y, 0f), targetAlt0);
                    try
                    {
                        loftAlt = (float)_optimalLoftMethod.Invoke(null, new object[]
                        {
                            ap, launchPos, targetPos, targetVel, startVelKnots, isAir,
                            dragFactor, altMin, configuredLoftAlt
                        });
                        loftAlt = Mathf.Clamp(loftAlt, altMin, configuredLoftAlt);
                    }
                    catch (Exception e)
                    {
                        loftAlt = configuredLoftAlt;
                        WarnSetupFallback(ap, "the game's loft-altitude search", e);
                    }
                }
                bool lofting = loftAlt > Mathf.Max(launchPos.y, 0f) + AltToleranceU;

                bool isTerminalLoft = ap._terminalLoft;
                bool isHighBallisticLofter = !nonKin && lofting && loftAlt > ZeroDensityAltU;

                // The two slopes of the non-cruise loft tent, as tangents, clamped the way the
                // game clamps them before use.
                float loftClimbTan = Mathf.Tan(Mathf.Clamp(ap._maxLoftAngle, 0.01f, 89f) * Mathf.Deg2Rad);
                float loftDescentTan = Mathf.Tan(
                    Mathf.Clamp(ap._finalFlightPhaseMaxAngle, 0.01f, 89f) * Mathf.Deg2Rad);

                float climbDeg = ap._maxLoftAngle > AltToleranceU ? ap._maxLoftAngle : DefaultClimbDeg;
                float boostClimbDeg = isHighBallisticLofter ? BoostClimbDeg : climbDeg;
                // turnRateBase is MaxTurnRate alone, carried separately because the G-derate cuts the base
                // rate while the banking roll addend is a second budget at a hardcoded rate no G-limit
                // touches. The step loop derives the addend by subtraction, so one number travels instead of two.
                float turnRateBase = ap._maxTurnRateDegrees > VelocityEpsilonKn ? ap._maxTurnRateDegrees : DefaultTurnRateDeg;
                float turnRate = turnRateBase;
                // Non-kinematic + SupportsBanking gets a second rotation call per tick: RollTowardsLevel's banking
                // branch into performToTargetRoll, a hardcoded 60 deg/s RotateTowards on top of the normal one. It
                // assigns local euler to world rotation, gimbal-degenerate near vertical, so after a VLS launch that
                // budget lands on pitch and the two sum.
                if (nonKin && ap._supportsBanking)
                    turnRate += BankingRollRateDeg;
                float toBearingTurnRate = ResolveToBearingTurnRate(ap);
                float turnDerateThresholdKn = ResolveTurnDerateThreshold(ap, turnRateBase);

                LaunchGeometry geom = ResolveLaunchGeometry(unit, ap, launcher, launchPos, targetPos);
                float launchPitch = geom.Pitch;
                float launchPitchIni = geom.PitchIni;
                Transform rail = geom.Rail;
                bool fixedRail = geom.FixedRail;
                string railAzTxt = geom.RailAzText;
                float initialPhaseDur = Mathf.Max(ap._initialFlightPhaseDuration, 0f);

                // Printed by the sim-launch trace alone.
                float launchBearingErrDeg = Coordinator.TraceFlightModel
                    ? LaunchBearingErrDeg(unit, launchPos, targetPos) : -1f;

                float maxVelKn = Mathf.Max(ap._maxVelocityInKnots, 1f);
                float loftVelKn = ap._maxLoftVelocityInKnots > 0f ? ap._maxLoftVelocityInKnots : maxVelKn;
                float termVelKn = ap._terminalVelocityInKnots > 0f ? ap._terminalVelocityInKnots : maxVelKn;
                float decelRate = ap._deceleration * GravityKnPerMs;

                StageProfile stage = ResolveStageProfile(ap, maxVelKn, nonKin, isTerminalLoft,
                                                         isAir, targetAlt0);
                float finalDist = stage.FinalDist;
                float cruiseAlt = stage.CruiseAlt;
                float finalFlightDist = stage.FinalFlightDist;
                float finalFlightAlt = stage.FinalFlightAlt;
                float loftEntryDist = stage.LoftEntryDist;
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
                phases.FinalFlightStartU = -1f;
                phases.DescentOnsetDeg = descentOnsetDeg;

                Vector3 pos = launchPos;
                float velKnots = startVelKnots;
                float prevPitch = launchPitch >= 0f ? launchPitch : 0f;
                // Launch heading for a fixed launcher, the horizontal analogue of prevPitch: a launcher that cannot
                // train fires along its own bearing, so an off-bearing shot flies its initial phase the wrong way and
                // then turns. From the rail's bearing where it has one, else the ship's yaw for a vertical cell. Zero
                // when there is nothing to model (trainable mount, or no rail resolved), so horizDir keeps aiming
                // straight at the target.
                Vector3 launchHeading = Vector3.zero;
                if (fixedRail && rail != null)
                {
                    if (!GameMath.TryFlatDirection(rail.forward, out launchHeading))
                    {
                        // Vertical rail: no bearing of its own, so the round leaves carrying the ship's yaw. Same
                        // vector the bearingErr diagnostic reads above.
                        GameMath.TryFlatDirection(unit.transform.forward, out launchHeading);
                    }
                }
                // A recorded launch bearing wins over the live rail: the rail has since turned with
                // the platform, and the bearing this shot actually flew off is the one that decides
                // how far off-boresight it started. Only for a fixed rail, so a ship's trainable
                // mount (which aims at the target, heading zero) is untouched.
                if (launch.Valid && fixedRail && launch.HeadingFlat.sqrMagnitude > 1e-8f)
                    launchHeading = launch.HeadingFlat;
                // Attitude carried ACROSS steps for the coupled turn: rebuilding it each step would
                // refund the budget spent on roll and silently restore the independent-limit rate.
                // Re-seeded (roll zero) on any step that does not take the coupled branch.
                Quaternion att = GameMath.QEuler(-prevPitch, YawOf(launchHeading), 0f);

                Vector2[] altNodes = null;
                float kappaEndFlat = 0f;

                // The hold needs both conditions, and the launch range is why it is resolved here rather than
                // beside the stage profile. `lofting`: an ammunition whose loft ceiling is at or below the
                // launch altitude never gets loft speed. `flatDistTotal > finalDist`: the flag at
                // Missile.cs:413 stops a round leaving MaintainLoftAlt but not entering it, which is decided
                // out of ToBearing without the flag; a round launched inside the sea-skimming boundary goes
                // ToBearing -> MaintainSeaSkimming and flies the whole way at cruise speed.
                float loftSpeedHoldDist = (lofting && flatDistTotal > finalDist)
                                        ? ResolveLoftSpeedHoldDist(ap) : 0f;
                // Kinematic missiles are flown by Waypoint.KappaLoft whether TerminalLoft is set
                // or not. BuildAltitudeNodes is the game's sampled form of that same cubic curve;
                // the resolved override is the altitude TuneWaypoints selected for this shot.
                if (!nonKin && lofting && _altNodesMethod != null &&
                    flatDistTotal > MinimumLoftLegDistanceU)
                {
                    try
                    {
                        object[] an = { ap, Mathf.Max(launchPos.y, 0f), targetAlt0,
                                        flatDistTotal, loftAlt, 0f };
                        var nodes = _altNodesMethod.Invoke(null, an)
                            as System.Collections.Generic.List<Vector2>;
                        if (nodes != null && nodes.Count >= SampledCurveMinimumNodeCount)
                        {
                            altNodes = nodes.ToArray();
                            kappaEndFlat = (float)an[5];
                        }
                    }
                    catch (Exception e)
                    {
                        altNodes = null;
                        WarnSetupFallback(ap, "the game's altitude-node sample", e);
                    }
                }
                // What the stage switches are measured to. See FlightTime.Lead.cs.
                SwitchTarget switchTarget = ResolveSwitchTarget(target.IsAirUnit,
                    ap._forceDirectPoint, ap._midCourseCorrection.AllowsLauncherGuidance,
                    _dragFn != null || _dragMethod != null);
                bool trackDiag = Coordinator.TraceFlightModel && emitDiag;
                string ammoLabel = ap._ammunitionFileName ?? "?";
                if (trackDiag)
                    Bootstrap.Log.LogInfo($"[AutoTOT] sim-launch {ammoLabel}: launchPitch " +
                        $"{(launchPitch >= 0f ? launchPitch.ToString("0.0") + "°" : "n/a (heading)")}" +
                        $", initPhase {initialPhaseDur:0.0}s, turnRate {turnRate:0.0}/s, " +
                        $"loftAlt {loftAlt:0}u (configured {configuredLoftAlt:0}u)" +
                        $", descentDeg {descentDeg:0.0}°, onsetDeg {descentOnsetDeg:0.0}°" +
                        $", bearingErr {(launchBearingErrDeg >= 0f ? launchBearingErrDeg.ToString("0.0") + "°" : "n/a")}" +
                        // Launch range, so each shot records its geometry: two ranges per shot cannot
                        // separate a fixed offset from a drift, three can.
                        $", range {flatDistTotal:0}u ({flatDistTotal * GameUnits.MetersPerUnity / 1000f:0.0}km)" +
                        // Where the solve started: the launcher's cell mean, a round's own recorded
                        // position, or the unit's centre when the launcher has no cells.
                        $", launchFrom {(launch.Valid ? "launch state" : launchCells > 0 ? $"{launchCells} cells, {(launchPos - unit.transform.position).magnitude * GameUnits.MetersPerUnity:0.0}m from centre" : "unit centre")}" +
                        // Both values, so the ini default and the rail's real orientation can be
                        // compared directly on one line.
                        $", iniPitch {(launchPitchIni >= 0f ? launchPitchIni.ToString("0.0") + "°" : "n/a")}" +
                        // The loft-speed boundary, so a shot records whether and where the seeker-range hold
                        // applied; "none" means the stage profile's boundary, true for all but two shipped ammunition.
                        $", loftHold {(loftSpeedHoldDist > 0f ? loftSpeedHoldDist.ToString("0") + "u" : "none")}" +
                        // The two phase-1 altitudes, the boundary between them and the loft-entry distance,
                        // so a traced shot records which legs it should have flown.
                        $", loftEntry {(loftEntryDist > 0f ? loftEntryDist.ToString("0") + "u" : "none")}" +
                        $", cruiseAlt {cruiseAlt * GameUnits.MetersPerUnity / 0.3048f:0}ft" +
                        $", finalFlight {finalFlightDist:0}u @ {finalFlightAlt * GameUnits.MetersPerUnity / 0.3048f:0}ft" +
                        // The turn budget and its two corrections. "none" for both, the answer for almost
                        // every round, means neither correction can have moved the shot. gLimit prints the
                        // speed at which the derate starts, to read against the sim-track speeds.
                        $", turn {turnRateBase:0.#}°/s" +
                        $", launchTurn {(toBearingTurnRate > 0f ? toBearingTurnRate.ToString("0.#") + "°/s" : "none")}" +
                        $", gLimit {(turnDerateThresholdKn > 0f ? turnDerateThresholdKn.ToString("0") + "kn" : "none")}" +
                        // Sampled on the fired-shot path: the planning-path launch-rail reading goes
                        // stale because the ship keeps turning between planning and launch.
                        $", railAz {railAzTxt}" +
                        $", switchTo {switchTarget}");

                // The waypoint route the round will fly. Only for a surface target: an air target
                // gets RelativeToTarget heights and Linear legs, which are not ported.
                bool surfaceRoute = nonKin && !isTerminalLoft && !target.IsAirUnit &&
                                    ap._targetType != Ammunition.Target.AAW;
                // Every round of an ammunition with GroupSize above 1 joins or creates a group
                // (Missile.cs:2129, :3244); a single round leads its own.
                bool groupLeader = ap._maxGroupSize > 1 && !(target is LandUnit);
                // Resolved once and handed to both the route and the snapshot.
                bool popUpEnabled = ResolveTerminalPopUpEnabled(ap, target);
                float popUpDist = Mathf.Max(ap._terminalPopUpDist, 0f);
                WaypointLeg[] legs = surfaceRoute
                    ? BuildNonKinLegs(flatDistTotal, targetAlt0,
                        ap._maxLoftAltUnity, ap._maxLoftAngle, ap._loftToSkim,
                        ap._seaSkimmingStartDistToTargetUnity, ap._seaSkimmingAltUnity,
                        ap._seaSkimmingMaxDescentAngle,
                        ap._finalFlightPhaseDistToTargetUnity, ap._finalFlightPhaseAltUnity,
                        ap._finalFlightPhaseMaxAngle,
                        ap._terminalApproachDist, ap._terminalAltUnity, ap._terminalLoft,
                        ap._terminalDiveDistance,
                        popUpEnabled, popUpDist,
                        ap._terrainFollowFlight,
                        groupLeader ? ap._groupLeaderAltUnity : -1f)
                    : null;

                HullBox hull = TargetHull(unit, target, out string hullSource);

                // The loop runs as a pure function of this snapshot. Building it here keeps every
                // Unity read and every mutable-game-state read on the main thread, which is what
                // makes the loop safe to run elsewhere. See FlightTime.Solve.cs.
                input = new SolveInput
                {
                    AltNodes = altNodes,
                    AmmoLabel = ammoLabel,
                    BoostClimbDeg = boostClimbDeg,
                    DecelRate = decelRate,
                    DescentDeg = descentDeg,
                    DescentOnsetDeg = descentOnsetDeg,
                    DragFactor = dragFactor,
                    CruiseAlt = cruiseAlt,
                    FinalFlightAlt = finalFlightAlt,
                    FinalFlightDist = finalFlightDist,
                    FinalDist = finalDist,
                    FlatDistTotal = flatDistTotal,
                    InitialPhaseDur = initialPhaseDur,
                    PerformsSlightBallisticInitialFlight = ap._performsSlightBallisticInitialFlight,
                    IsAir = isAir,
                    IsHighBallisticLofter = isHighBallisticLofter,
                    IsTerminalLoft = isTerminalLoft,
                    LaunchPitch = launchPitch,
                    ConfiguredLoftAlt = configuredLoftAlt,
                    LoftAlt = loftAlt,
                    LoftEntryDist = loftEntryDist,
                    LoftSpeedHoldDist = loftSpeedHoldDist,
                    LoftVelKn = loftVelKn,
                    BallisticLoft = BallisticLoftOf(ap),
                    LoftClimbTan = loftClimbTan,
                    LoftDescentTan = loftDescentTan,
                    LaunchAltU = launchPos.y,
                    Lofting = lofting,
                    SeekerBrakeRangeU = ResolveSeekerBrakeRange(ap),
                    MaxFlight = maxFlight,
                    MaxVelKn = maxVelKn,
                    NonKin = nonKin,
                    TargetAlt0 = targetAlt0,
                    TargetPos = targetPos,
                    TargetVel = targetVel,
                    TargetVelReported = target._velocityVecInUnity,
                    StageSwitchTarget = switchTarget,
                    SeekerLockRangeU = ResolveSeekerLockRange(
                        ap._guidanceType == AmmunitionParameters.GuidanceType.None,
                        ap._seekerActiveRange, ap._seekerPassiveRange),
                    LeadLoftAltU = ap._maxLoftAltUnity,
                    LeadFinalAngleDeg = ap._finalFlightPhaseMaxAngle,
                    TargetMoveStep = targetMoveStep,
                    TerminalLinear = stage.TerminalLinear,
                    TermAlt = termAlt,
                    TermDist = termDist,
                    TermVelKn = termVelKn,
                    TerminalPopUpEnabled = popUpEnabled,
                    TerminalPopUpDist = popUpDist,
                    TerminalPopUpHeight = Mathf.Max(ap._terminalPopUpHeight, 0f),
                    TerminalPopUpAngle = Mathf.Clamp(ap._terminalPopUpAngle, 0.01f, 89f),
                    TerminalVerticalTurnRate = Mathf.Max(ap._terminalVerticalTurnRate, 0f),
                    TerminalDelaySec = ap._terminalDelay >= 0f ? ap._terminalDelay
                        : 0.8f * 3600f * Mathf.Max(ap._minLaunchRangeInMiles, 0f) / maxVelKn,
                    Hull = hull,
                    HullSource = hullSource,
                    TrackDiag = trackDiag,
                    TurnRate = turnRate,
                    ToBearingTurnRate = toBearingTurnRate,
                    TurnRateBase = turnRateBase,
                    TurnDerateThresholdKn = turnDerateThresholdKn,
                    Att = att,
                    LaunchHeading = launchHeading,
                    Pos = pos,
                    KappaEndFlat = kappaEndFlat,
                    VelKnots = velKnots,
                    Legs = legs,
                    GroupLeader = groupLeader
                };

                return true;
            }
            catch (Exception e)
            {
                ModLog.VerboseWarn("integrated flight-time setup failed", e);
                return false;
            }
        }

        /// <summary>
        /// The hull a round has to reach, sized by what the shooter's side knows about the target. Identified
        /// (the contact has a class): the target's own collider. Held by a sensor whose contact label shows a
        /// size class (every sensor but passive sonar and ESM, Vehicle.IncomingSignalInfo): the
        /// representative hull of that class. Otherwise (a noise contact, or none held now): the middle
        /// class. Not a surface ship: no hull, and the 3 u stop. The hull only ends the estimate's flight;
        /// neither its size nor its source reaches the player.
        /// </summary>
        private static HullBox TargetHull(ObjectBase unit, ObjectBase target, out string source)
        {
            source = "none";
            if (!(target is Vessel)) return null;
            Vehicle contact = null;
            try { contact = unit?._taskforce?.PlottingTable?.VehicleForObject(target); }
            catch { contact = null; }
            float headingDeg = target.getHeading();
            Vector3 hullM;   // length, height, beam
            if (contact != null && contact.Identified.Value && ColliderHull(target, headingDeg, out hullM))
            {
                source = "identified";
            }
            else
            {
                SensorSystem.SensorTypeSet sensors = contact != null
                    ? contact.DetectingSensors : SensorSystem.SensorTypeSet.Unknown;
                bool sizeShown = (sensors & ~(SensorSystem.SensorTypeSet.PassiveSonar |
                                              SensorSystem.SensorTypeSet.ESM)) != 0;
                string size = target._sensorData != null ? target._sensorData._size : null;
                if (sizeShown && ClassHull(size, out hullM))
                    source = "size " + size;
                else
                {
                    ClassHull(MiddleSizeClass, out hullM);
                    source = sizeShown ? "size unknown" : sensors != SensorSystem.SensorTypeSet.Unknown ? "noise" : "no track";
                }
            }
            float heading = headingDeg * Mathf.Deg2Rad;
            return new HullBox(new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading)),
                               hullM.x / GameUnits.MetersPerUnity, hullM.z / GameUnits.MetersPerUnity,
                               hullM.y / GameUnits.MetersPerUnity);
        }

        /// <summary>
        /// The target's first collider, the one the trace records as TargetBoundsM, measured in the ship's
        /// heading frame: length, height and beam in metres. The collider's own box when it has one (a
        /// BoxCollider's box, a MeshCollider's mesh bounds), its world-aligned bounds otherwise.
        /// </summary>
        private static bool ColliderHull(ObjectBase target, float headingDeg, out Vector3 hullM)
        {
            hullM = default;
            Collider c = target.GetComponentInChildren<Collider>();
            if (c == null) return false;
            Bounds box;
            Transform frame = c.transform;
            if (c is BoxCollider bc) box = new Bounds(bc.center, bc.size);
            else if (c is MeshCollider mc && mc.sharedMesh != null) box = mc.sharedMesh.bounds;
            else { box = c.bounds; frame = null; }
            Quaternion toShip = Quaternion.Euler(0f, -headingDeg, 0f);
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = new Vector3((k & 1) != 0 ? box.max.x : box.min.x,
                                             (k & 2) != 0 ? box.max.y : box.min.y,
                                             (k & 4) != 0 ? box.max.z : box.min.z);
                Vector3 p = toShip * (frame != null ? frame.TransformPoint(corner) : corner);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            Vector3 size = (hi - lo) * GameUnits.MetersPerUnity;
            if (size.x <= 0f || size.z <= 0f) return false;
            hullM = new Vector3(Mathf.Max(size.x, size.z), size.y, Mathf.Min(size.x, size.z));
            return true;
        }

        /// <summary>The class a contact without a shown size takes: the middle of the ship classes.</summary>
        private const string MiddleSizeClass = "Medium";

        /// <summary>A class hull's height over its beam. No ini declares a height; the two ship classes in
        /// the corpus give collider height over ini beam 0.51 (the tanker) and 0.94 (Yuniy Partizan).</summary>
        private const float ClassHullHeightPerBeam = 0.7f;

        /// <summary>
        /// The representative hull of a sensor size class (SensorData RCS, EnumRCSValues): the median
        /// Length and Beam of the vessel inis declaring that class, in metres (length, height, beam).
        /// VerySmall has one placeholder type and takes Small's, as do VeryTiny and Tiny, which no ship
        /// declares. False for anything else, such as a numeric RCS.
        /// </summary>
        private static bool ClassHull(string size, out Vector3 hullM)
        {
            float length, beam;
            switch (size)
            {
                case "VeryTiny": case "Tiny": case "VerySmall":
                case "Small":     length = 58.0f;  beam = 6.8f;  break;   // 26 types
                case "SemiSmall": length = 59.5f;  beam = 8.8f;  break;   // 51
                case "Meager":    length = 38.6f;  beam = 7.6f;  break;   // 41
                case "Medium":    length = 103.0f; beam = 11.8f; break;   // 33
                case "SemiLarge": length = 136.0f; beam = 14.3f; break;   // 38
                case "Large":     length = 172.0f; beam = 19.2f; break;   // 29
                case "VeryLarge": length = 250.0f; beam = 31.0f; break;   // 29
                default: hullM = default; return false;
            }
            hullM = new Vector3(length, ClassHullHeightPerBeam * beam, beam);
            return true;
        }
    }
}
