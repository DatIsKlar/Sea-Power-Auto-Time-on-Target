using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        private const float IntegrationStepSim = 0.1f;
        // Altitude at which the game's air-density curve (1 - 0.00163*h)^4.256 reaches zero. Used by
        // the setup half to classify a high ballistic lofter and by the step loop to arm the vacuum
        // brake, so it is declared once for the whole partial class rather than in each.
        internal const float ZeroDensityAltU = 1f / 0.00163f;
        private const float AltToleranceU = 0.5f;
        // The game's OWN ini defaults, from AmmunitionParameters' ini reads: MaxLoftAngle 30,
        // SeaSkimmingMaxDescentAngle 30, FinalFlightPhaseMaxAngle 30. Cited by KEY rather than by
        // line: the decompile renumbers on every beta bump, and these four citations were already
        // pointing at the previous one. Grep the key in AmmunitionParameters.cs to find the read.
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
        private const float SetAngleEpsilonDeg = 0.01f;
        /// <summary>g in knots per second, for converting an ini deceleration in g into knots/s.
        /// Shared with WaypointSim, which computes the same decel from the same two factors.</summary>
        internal const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        /// <summary>Share of the launch range a round must take off the range before the
        /// closest-approach guard will believe it has closed. See the latch note in Solve.</summary>
        private const float RecedeCloseFraction = 0.002f;
        // Sampling cadence for the `sim-track` trace. Defined once in TelemetryCadence, because the
        // live `track` trace in LaunchDiagnostics has to sample at the same offsets for the two to
        // be comparable. Aliased here rather than called directly so the step loop, which reads
        // these, keeps its existing identifiers and generated code.
        private const float TelemetrySampleIntervalSim = TelemetryCadence.SampleIntervalSim;
        private const float LaunchBurstWindowSim = TelemetryCadence.LaunchBurstWindowSim;
        private const float LaunchBurstIntervalSim = TelemetryCadence.LaunchBurstIntervalSim;
        private const float NoseOverWindowSim = TelemetryCadence.NoseOverWindowSim;
        private const float NoseOverIntervalSim = TelemetryCadence.NoseOverIntervalSim;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        private const float LookaheadMultiplier = 20f;
        private const float MinLookaheadU = 50f;

        // Isolation gates. Each mirrors one piece of the live mover's behaviour, kept separate
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

        // Launch-phase fidelity. The game runs Launch -> ToBearing before any cruise/loft
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
        // gating bool also defaults true (ObjectBaseLoader, ini key "FixVerticalLaunchAngle").
        private const bool LauncherTransformLaunchAngle = true;

        // A launcher that cannot train fires along its OWN bearing, so an off-bearing shot flies its
        // initial phase the wrong way and then turns. railAz, not bearingErr, predicts that turn.
        private const bool FixedRailLaunchHeading = true;

        // A vertical rail has no bearing of its own, so the round leaves carrying the SHIP's yaw.
        private const bool VerticalLaunchInheritsShipHeading = true;

        // Pitch, yaw and roll share ONE rotation budget, so modelling the axes independently spends
        // it twice: 90 deg of pitch with 90 deg of heading error is 120 deg of quaternion travel,
        // not 90. An on-bearing shot is unaffected, the step reducing to MoveTowards. Requires the
        // attitude to PERSIST across steps, or the budget spent on roll is refunded every step and
        // the gate goes inert.
        //
        // Both mover paths spend one budget, which is why this is NOT scoped to Kinematics == None.
        // Legacy (Kinematics == None) takes one Quaternion.RotateTowards over the whole rotation
        // (WeaponBase.setCourseTowardsPositionLegacy, the else of its split); Kinematics == Full
        // takes one Vector3.RotateTowards over the forward vector for a surface target, or one
        // Quaternion.AngleAxis cone for an air target (setCourseTowardsPosition). The Full path
        // excludes roll from that budget and levels separately, so the mod's coupled path rotating
        // an attitude with roll zero is the matching shape. Only the per-axis split is different,
        // and it needs a TerminalVerticalTurnRate diverging from MaxTurnRate by more than 1 deg/s,
        // which 6 of 446 shipped ammunition declare.
        private const bool CoupledPitchYawRateLimit = true;

        // While in ToBearing the mover floors the turn rate at LaunchTurnRate when the ini sets it
        // above the ordinary rate (WeaponBase.setCourseTowardsPosition, the toBearingState branch).
        // Three shipped ammunition set it, and one of them is a mainstream strike round:
        // usn_rgm-84a at 40 deg/s against a MaxTurnRate of 15, wp_sa-n-6 at 40 against 20,
        // wp_sa-n-9 at 360 against 30. Left unmodelled the round turns onto its target too slowly,
        // flies further off-axis, and the estimate runs LATE. rgm-84c and rgm-84d leave the key at
        // its default, so the error shows on the A-variant alone and reads as scatter.
        private const bool LaunchTurnRateOverride = true;

        // The mover converts the commanded turn rate into a G-load at the round's CURRENT speed and
        // cuts the rate when it exceeds MaxTurnG (WeaponBase.setCourseTowardsPosition, ahead of the
        // Kinematics dispatch, so both paths derate). Unmodelled, the round turns harder than the
        // game lets it and the estimate runs early.
        //
        // Inert on every anti-ship missile in the game: none sets MaxTurnG, so all take the 200 g
        // default, and the fastest of them reaches 14.2 g. It is not inert generally. 82 shipped
        // ammunition set MaxTurnG, 25 being the most common value, and 57 missiles exceed their own
        // limit at max speed, down to 0.26x on usn_rim-2f. Every one is a SAM or an AAM, which the
        // coordinator will plan for: it filters to Ammunition.Type.Missile and DoesAmmoMatchTarget
        // and no further.
        private const bool TurnRateGDerate = true;

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
                        Vector3 toTgtH = GameMath.Flatten(targetPos - launchPos);
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
                            Vector3 lf = GameMath.Flatten(lt.forward);
                            if (lf.sqrMagnitude < GameMath.MinFlatSqrMagnitude) continue;   // vertical: no bearing
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

                    float railDeg = rail != null ? GameMath.ElevationDeg(rail.forward) : float.NaN;

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

                    // Horizontal analogue of railDeg: rail bearing vs bearing to target. `bearingErr`
                    // cannot see this, measuring the SHIP's heading instead. Meaningless when the
                    // rail is near-vertical, which is every VLS shot.
                    float railAzDeg = float.NaN;
                    if (rail != null)
                    {
                        Vector3 tt = GameMath.Flatten(targetPos - launchPos);
                        if (!GameMath.TryFlatDirection(rail.forward, out Vector3 rf)) railAzTxt = "vertical";
                        else if (tt.sqrMagnitude > 1e-6f)
                        {
                            railAzDeg = Vector3.Angle(rf, tt);
                            railAzTxt = railAzDeg.ToString("0.0") + "°";
                        }
                    }

                    // Re-emitted whenever the rail moves, so the formula above is measured rather
                    // than assumed.
                    string railKey = unit.GetInstanceID() + "/" + (ap._ammunitionFileName ?? "?");
                    if (Coordinator.TraceFlightModel && vwp != null)
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
                                    : GameMath.ElevationDeg(tr.forward).ToString("0.0") + "°";
                                Bootstrap.Log.LogInfo(
                                    // D8 of docs/plans/open/anchor-liveness.md: the sim stamp. This
                                    // trace is the best available proxy for a manoeuvring aircraft's
                                    // heading through a launch wait, and without a time it cannot be
                                    // lined up against envelope-track or engage-state.
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
            /// <summary>Altitude of the outer cruise leg, before the final-flight phase begins.</summary>
            internal readonly float CruiseAlt;
            /// <summary>Where the final-flight phase begins, and the altitude it commands. The game
            /// runs these as two stages with two altitudes; see the note in ResolveStageProfile.</summary>
            internal readonly float FinalFlightDist, FinalFlightAlt;
            /// <summary>Distance-to-target outside which the round enters the loft at all. 0 = the
            /// ammunition has no sea-skimming boundary, so it always lofts.</summary>
            internal readonly float LoftEntryDist;
            internal StageProfile(float finalDist, float finalAlt, float termDist, float termAlt,
                                  float descentDeg, float descentOnsetDeg, float cruiseAlt,
                                  float finalFlightDist, float finalFlightAlt, float loftEntryDist)
            {
                FinalDist = finalDist; FinalAlt = finalAlt; TermDist = termDist;
                TermAlt = termAlt; DescentDeg = descentDeg; DescentOnsetDeg = descentOnsetDeg;
                CruiseAlt = cruiseAlt; FinalFlightDist = finalFlightDist;
                FinalFlightAlt = finalFlightAlt; LoftEntryDist = loftEntryDist;
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
            // Two altitudes, not one. The game runs MaintainSeaSkimming at SeaSkimmingAlt and
            // MaintainFinalFlightAlt at FinalFlightPhaseAlt (Missile.cs:614-647), and switches
            // between them at FinalFlightPhaseDistToTarget. Collapsing them is close enough on a
            // long shot, where most of the cruise really is sea-skimming, and wrong on a shot that
            // starts inside the final-flight distance: an ss-n-3b at 91 km flew the whole way at
            // 1312 ft while the model held 13200 ft, worth -4.4s on a 199s flight.
            //
            // The game parses FinalFlightPhaseAlt with SeaSkimmingAlt as its default
            // (AmmunitionParameters, ini key "TerminalVelocity"), so for an ammunition that does not distinguish
            // the two these are equal and the switch below is inert. That is the common case.
            float cruiseAlt = ap._seaSkimmingAltUnity > 0f ? ap._seaSkimmingAltUnity : finalAlt;
            float finalFlightAlt = ap._finalFlightPhaseAltUnity > 0f
                                 ? ap._finalFlightPhaseAltUnity : cruiseAlt;
            // NOT clamped to the loft boundary, deliberately: the game tests this distance on its
            // own, and for a LoftToSkim=False round it is the point that ends the loft as well.
            float finalFlightDist = ap._finalFlightPhaseDistToTargetUnity;
            // The real loft-entry test. See the note on the phase selector in FlightTime.Solve.cs.
            float loftEntryDist = Mathf.Max(ap._seaSkimmingStartDistToTargetUnity, 0f);
            // finalAlt is left exactly as it was and now feeds ONLY this fallback. Twelve shipped
            // ammunition declare no TerminalAlt while giving different sea-skimming and
            // final-flight altitudes, so redefining it here would silently move their terminal leg.
            float termAlt = ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt;
            float descentDeg = ap._finalFlightPhaseMaxAngle > SetAngleEpsilonDeg ? ap._finalFlightPhaseMaxAngle
                             : (ap._seaSkimmingMaxDescentAngle > SetAngleEpsilonDeg ? ap._seaSkimmingMaxDescentAngle : DefaultDescentDeg);
            float descentOnsetDeg = Mathf.Max(descentDeg,
                Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));
            return new StageProfile(finalDist, finalAlt, termDist, termAlt, descentDeg, descentOnsetDeg,
                                    cruiseAlt, finalFlightDist, finalFlightAlt, loftEntryDist);
        }

        /// <summary>
        /// Distance-to-target at which a lofting missile stops flying at <c>MaxLoftVelocity</c>,
        /// for the ammunition that does not stop at the boundary <see cref="ResolveStageProfile"/>
        /// names. Returns 0 when the ordinary boundary applies.
        ///
        /// <para><b>Why this is not the stage-profile distance.</b> The game gives a missile
        /// <c>_maxLoftVelocityInKnots</c> only while its flight stage is <c>MaintainLoftAlt</c>
        /// (<c>Missile.cs:3143</c>), and BOTH distance-based exits from that stage sit behind one
        /// local flag (<c>Missile.cs:614-647</c>):</para>
        /// <code>
        /// else if (flag2 &amp;&amp; magnitude &lt; _finalFlightPhaseDistToTargetUnity &amp;&amp; flag &amp;&amp; num &lt;= 3) -> MaintainFinalFlightAlt
        /// else if (magnitude &lt; _seaSkimmingStartDistToTargetUnity &amp;&amp; flag &amp;&amp; num &lt;= 2 &amp;&amp; ...)  -> MaintainSeaSkimming
        /// else if (num &lt;= 1)                                                            -> MaintainLoftAlt
        /// </code>
        /// <para><c>Missile.cs:413</c> clears that flag for the whole flight when the ammunition
        /// declares <c>RequiresTargetToProceed</c>, and only seeker activation with a permitted
        /// target sets it back (<c>Missile.cs:426-433</c>). So neither declared distance takes
        /// effect until the seeker comes up, and the missile holds loft speed until then. Activation
        /// is <c>ShouldActivateMissileSeeker</c> (<c>BearingLaunchPrediction.cs:97-108</c>): inside
        /// the active range, or inside the passive range, or unconditionally for TV homing.</para>
        ///
        /// <para>The ALTITUDE schedule is unaffected and stays where <see cref="ResolveStageProfile"/>
        /// puts it. Telemetry on both affected rounds shows the descent to sea-skimming altitude
        /// happening at the declared distance while the speed does not change: an ss-n-3b at 316 km
        /// came down from 104 u to 59.9 u at 130 nmi and held 1150 kn for a further 148 km.</para>
        ///
        /// <para>Measured against the two ammunition this reaches, the predicted boundary lands on
        /// the observed stage change: ss-n-3b 1378 u predicted against 1381 u observed, ss-n-3
        /// 595 u against 595 u.</para>
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

        // The mover's own factors, pinned rather than taken from GameUnits: setCourseTowardsPosition
        // writes 0.514444f and 9.8f literally, and GameUnits.KnotsToMs carries one more digit. The
        // difference cannot matter at this scale, but a threshold derived from the game's arithmetic
        // should read as the game's arithmetic.
        private const float MoverKnotsToMs = 0.514444f;
        private const float MoverGravityMs2 = 9.8f;

        /// <summary>
        /// Speed, in knots, at which a turn at <paramref name="turnRateDeg"/> reaches the
        /// ammunition's own G-limit. Above it the mover cuts the rate in proportion; below it the
        /// rate stands. Returns 0 when no limit can apply, which turns the derate off.
        ///
        /// <para>The mover computes, every tick, from its CURRENT speed
        /// (<c>WeaponBase.setCourseTowardsPosition</c>, ahead of the Kinematics dispatch so both
        /// paths derate):</para>
        /// <code>
        /// v      = 0.514444 * knots            // m/s
        /// radius = v * (360 / rate) / (2*PI)   // m, the circle that rate traces at v
        /// g      = v^2 / (radius * 9.8)
        /// if (g &gt; MaxTurnG) rate *= MaxTurnG / g
        /// </code>
        /// <para>Substituting the radius collapses g to <c>v * rate * 2*PI / (360 * 9.8)</c>, which
        /// is linear in speed. So there is one speed where g equals the limit, and above it the
        /// surviving rate is <c>rate * threshold / knots</c>. Solving once here keeps the step loop
        /// to a compare and, on the far side of it, one divide.</para>
        ///
        /// <para>Inert for every anti-ship missile: none declares <c>MaxTurnG</c>, so all take the
        /// 200 g default, and the fastest reaches 14.2 g. It bites on SAMs and AAMs, 57 of which
        /// exceed their declared limit at their own max speed.</para>
        /// </summary>
        private static float ResolveTurnDerateThreshold(AmmunitionParameters ap, float turnRateDeg)
        {
            float maxTurnG = ap._maxTurnG;
            // A non-positive limit is an unset or malformed ini value, not an order to freeze the
            // round. One shipped ammunition writes `MaxTurnG=` with no number at all.
            if (maxTurnG <= 0f || turnRateDeg <= VelocityEpsilonKn) return 0f;
            float thresholdMs = maxTurnG * 360f * MoverGravityMs2
                              / (2f * Mathf.PI * turnRateDeg);
            return thresholdMs / MoverKnotsToMs;
        }

        /// <summary>
        /// The turn rate the ToBearing window is floored at. The mover floors the rate at
        /// <c>LaunchTurnRate</c> while <c>toBearingState</c> is set, and only when the ini value
        /// exceeds the rate already in hand (<c>WeaponBase.setCourseTowardsPosition</c>).
        /// <c>LaunchTurnRate</c> defaults to -1, so this returns 0 for all but three shipped
        /// ammunition.
        ///
        /// <para>The ini value is returned UNCLAMPED, and 0 means "no floor". The comparison
        /// against the ordinary rate belongs in the step loop, not here, because the mover applies
        /// the floor AFTER the G-derate has already cut the rate: an ammunition can sit below its
        /// launch rate at speed and above it when slow. Clamping here against the un-derated
        /// <c>MaxTurnRate</c> would both drop that case and, by returning the base rate as the
        /// "no override" answer, re-floor a derated step back to the un-derated rate — cancelling
        /// the G-derate through the whole ToBearing window on every round that leaves the key at
        /// its default, which is the entire population the derate exists for.</para>
        /// </summary>
        private static float ResolveToBearingTurnRate(AmmunitionParameters ap)
            => ap._toBearingTurnRateDegrees > 0f ? ap._toBearingTurnRateDegrees : 0f;

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
                Vector3 shipFwd = GameMath.Flatten(unit.transform.forward);
                Vector3 toTarget = GameMath.Flatten(targetPos - launchPos);
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
            => IntegratedEndTimeCore(unit, ap, target, out phases, emitDiag, default);

        private static float IntegratedEndTimeCore(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out IntegratedPhases phases, bool emitDiag, in LaunchState launch)
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
        /// </summary>
        internal static bool TryBuildSolveInput(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out SolveInput input, out IntegratedPhases phases, bool emitDiag)
            => TryBuildSolveInput(unit, ap, target, out input, out phases, emitDiag, default);

        /// <summary>
        /// As above, with an optional <see cref="LaunchState"/> override. When one is supplied the
        /// setup answers for the shot as it ACTUALLY left the rail (position, speed and rail
        /// bearing at that instant) rather than for a hypothetical shot from the platform's current
        /// position. Everything downstream of the setup is unchanged: the same step loop runs on the
        /// same immutable input, which is the point of this seam.
        /// </summary>
        internal static bool TryBuildSolveInput(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out SolveInput input, out IntegratedPhases phases, bool emitDiag,
            in LaunchState launch)
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

                Vector3 launchPos = launch.Valid ? launch.PosU : unit.transform.position;
                // The override is set only by the retargeting diagnostic; see LaunchState.TargetPosU.
                // Velocity still comes from the live target, which is right: a ship's course holds
                // far better over a flight than its position does.
                Vector3 targetPos = (launch.Valid && launch.TargetPosU != Vector3.zero)
                                  ? launch.TargetPosU : target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool isAir = unit.IsAirUnit;

                ApplyEvasiveBoost(ap, target, launchPos, targetPos, ref targetVel);

                float dragFactor = ap.GetDragFactor(isAir);
                float startVelKnots = Mathf.Max(launch.Valid ? launch.VelKnots
                                                             : unit._velocityInKnots, 0f);
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
                // turnRateBase is MaxTurnRate alone. It is carried separately because the two
                // corrections below apply to different parts: the G-derate cuts the base rate, and
                // the banking roll addend is a second budget the mover spends at a hardcoded rate
                // that no G-limit touches. Deriving the addend by subtraction in the step loop keeps
                // one number on the wire instead of two.
                float turnRateBase = ap._maxTurnRateDegrees > VelocityEpsilonKn ? ap._maxTurnRateDegrees : DefaultTurnRateDeg;
                float turnRate = turnRateBase;
                // See BankingAddsRollBudgetToPitch above: the roll call is a second, independent
                // per-tick budget, so the two can sum onto pitch.
                if (BankingAddsRollBudgetToPitch && nonKin && ap._supportsBanking)
                    turnRate += BankingRollRateDeg;
                float toBearingTurnRate = ResolveToBearingTurnRate(ap);
                float turnDerateThresholdKn = ResolveTurnDerateThreshold(ap, turnRateBase);

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
                    if (!GameMath.TryFlatDirection(rail.forward, out launchHeading)
                        && VerticalLaunchInheritsShipHeading)
                    {
                        // Vertical rail: no bearing of its own, so inherit the ship's yaw
                        // (see VerticalLaunchInheritsShipHeading). Same vector the bearingErr
                        // diagnostic reads above; the per-step block below is reused unchanged.
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
                float flatDistTotal = GameMath.FlatDistance(targetPos, launchPos);

                // The hold needs BOTH conditions, and the launch range is why it is resolved here
                // rather than beside the stage profile.
                //
                // `lofting`: an ammunition whose loft ceiling is at or below the launch altitude
                // never gets loft speed to hold.
                //
                // `flatDistTotal > finalDist`: the flag at Missile.cs:413 stops a round LEAVING
                // MaintainLoftAlt. It has no say in whether the round ever ENTERS it, which is
                // decided coming out of ToBearing and is not gated on the flag. A round launched
                // already inside the sea-skimming boundary goes ToBearing -> MaintainSeaSkimming
                // and flies the whole way at cruise speed. Observed on an ss-n-3b at 224 km:
                // `ToBearing -> MaintainSeaSkimming at t+15.4s, flat 3260u`, peak speed 900 kn
                // against a 1150 kn loft speed, and the model's own `climb 0.0s` agrees. Holding
                // loft speed there cost +59.1s on 2026-09-08, against -0.4s before the hold existed.
                float loftSpeedHoldDist = (lofting && flatDistTotal > finalDist)
                                        ? ResolveLoftSpeedHoldDist(ap) : 0f;
                if (isTerminalLoft && _altNodesMethod != null && flatDistTotal > 1f)
                {
                    try
                    {
                        object[] an = { ap, Mathf.Max(launchPos.y, 0f), targetAlt0, flatDistTotal, -1f, 0f };
                        var nodes = _altNodesMethod.Invoke(null, an)
                            as System.Collections.Generic.List<Vector2>;
                        if (nodes != null && nodes.Count >= 2) altNodes = nodes.ToArray();
                    }
                    catch { altNodes = null; }
                }
                float nextSample = NoseOverIntervalSim;
                bool trackDiag = Coordinator.TraceFlightModel && emitDiag;
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
                        // The loft-speed boundary, so a shot records whether the seeker-range hold
                        // applied and where. "none" means the stage profile's own boundary is in
                        // force, which is the case for all but two ammunition in the shipped set.
                        $", loftHold {(loftSpeedHoldDist > 0f ? loftSpeedHoldDist.ToString("0") + "u" : "none")}" +
                        // The two phase-1 altitudes and the boundary between them, plus the
                        // loft-entry distance, so a traced shot records which legs it should have
                        // flown without anyone having to re-read the ini.
                        $", loftEntry {(loftEntryDist > 0f ? loftEntryDist.ToString("0") + "u" : "none")}" +
                        $", cruiseAlt {cruiseAlt * GameUnits.MetersPerUnity / 0.3048f:0}ft" +
                        $", finalFlight {finalFlightDist:0}u @ {finalFlightAlt * GameUnits.MetersPerUnity / 0.3048f:0}ft" +
                        // The turn budget and its two corrections. "launchTurn none" and "gLimit
                        // none" are the answer for almost every round, which is the point: a shot
                        // that reads "none" for both is one where neither correction can have moved
                        // it, so a residual on that shot has some other cause. gLimit prints the
                        // speed at which the derate STARTS, so it can be read against the sim-track
                        // speeds on the same shot.
                        $", turn {turnRateBase:0.#}°/s" +
                        $", launchTurn {(toBearingTurnRate > 0f ? toBearingTurnRate.ToString("0.#") + "°/s" : "none")}" +
                        $", gLimit {(turnDerateThresholdKn > 0f ? turnDerateThresholdKn.ToString("0") + "kn" : "none")}" +
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
                    cruiseAlt,
                    finalFlightAlt,
                    finalFlightDist,
                    finalDist,
                    flatDistTotal,
                    initialPhaseDur,
                    isAir,
                    isHighBallisticLofter,
                    isTerminalLoft,
                    launchPitch,
                    loftAlt,
                    loftEntryDist,
                    loftSpeedHoldDist,
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
                    toBearingTurnRate,
                    turnRateBase,
                    turnDerateThresholdKn,
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
                ModLog.VerboseWarn("integrated flight-time setup failed", e);
                return false;
            }
        }
    }
}
