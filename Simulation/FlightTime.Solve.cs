using System;
﻿using UnityEngine;
using SeaPower;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        /// <summary>
        /// Everything the step loop needs, as plain values.
        ///
        /// The loop reads no Unity API and no mutable game state: every <c>transform</c> read, and
        /// the one <c>AmmunitionParameters</c> field the game writes at runtime
        /// (<c>_finalFlightPhaseAltUnity</c>, read in <c>ResolveStageProfile</c>), happens during
        /// setup on the main thread. Capturing that setup here makes <see cref="Solve"/> a pure
        /// function of its input, which is what lets it run off the main thread.
        ///
        /// Fields that the loop mutates are carried as their INITIAL values and unpacked into locals.
        /// </summary>
        internal readonly struct SolveInput
        {
            internal Vector2[] AltNodes { get; init; }
            internal string AmmoLabel { get; init; }
            internal float BoostClimbDeg { get; init; }
            /// <summary>Deceleration in knots per SECOND. Held as a rate rather than a
            /// per-step amount so the step size stays a parameter of the loop.</summary>
            internal float DecelRate { get; init; }
            /// <summary>Seeker range that opens and closes the drag brake window, in Unity units.
            /// Positive is that range; NEGATIVE means no window can exist for this ammunition; and
            /// ZERO means it is not known, which is what an object initializer that omits it gives
            /// and what a capture predating the field gives, and it falls back to the older
            /// altitude gate. The unknown case is the struct default deliberately, so the safe
            /// reading is the one you get by forgetting.</summary>
            internal float SeekerBrakeRangeU { get; init; }
            internal float DescentDeg { get; init; }
            internal float DescentOnsetDeg { get; init; }
            internal float DragFactor { get; init; }
            /// <summary>Altitude of the outer cruise leg, outside <see cref="FinalFlightDist"/>.</summary>
            internal float CruiseAlt { get; init; }
            /// <summary>Where the final-flight leg begins, and the altitude it commands. 0 dist = no
            /// such leg, and the switch is inert. See FlightTime.Integrator.ResolveStageProfile.</summary>
            internal float FinalFlightAlt { get; init; }
            internal float FinalFlightDist { get; init; }
            internal float FinalDist { get; init; }
            internal float FlatDistTotal { get; init; }
            internal float InitialPhaseDur { get; init; }
            /// <summary>The launch-state controller lowers a sufficiently nose-high launch at
            /// exactly six degrees per second while the initial phase is active.</summary>
            internal bool PerformsSlightBallisticInitialFlight { get; init; }
            internal bool IsAir { get; init; }
            internal bool IsHighBallisticLofter { get; init; }
            internal bool IsTerminalLoft { get; init; }
            internal float LaunchPitch { get; init; }
            /// <summary>The ammunition's declared loft ceiling before the live missile's
            /// range-dependent waypoint tuning.</summary>
            internal float ConfiguredLoftAlt { get; init; }
            /// <summary>The altitude selected by MissileSimulator.SearchOptimalLoftAltitude.
            /// This is the LoftHeight the live Kappa waypoint actually receives.</summary>
            internal float LoftAlt { get; init; }
            /// <summary>Launch range outside which the round enters the loft at all. 0 = no
            /// sea-skimming boundary, so it always lofts.</summary>
            internal float LoftEntryDist { get; init; }
            /// <summary>Flat distance inside which loft speed ends, when the ordinary stage boundary
            /// does not apply. 0 = no hold. See FlightTime.Integrator.ResolveLoftSpeedHoldDist.</summary>
            internal float LoftSpeedHoldDist { get; init; }
            internal float LoftVelKn { get; init; }
            /// <summary>Loft-profile inputs for a NON-cruise round: the two slopes that form the
            /// tent, whether the ammunition uses the ballistic variant, and the launch altitude the
            /// climb is measured from. Unused when <see cref="NonKin"/> is true, which is the game's
            /// own CruiseMissile test. See FlightTime.LoftProfileAltU.</summary>
            internal bool BallisticLoft { get; init; }
            internal float LoftClimbTan { get; init; }
            internal float LoftDescentTan { get; init; }
            internal float LaunchAltU { get; init; }
            internal bool Lofting { get; init; }
            internal float MaxFlight { get; init; }
            internal float MaxVelKn { get; init; }
            internal bool NonKin { get; init; }
            internal float TargetAlt0 { get; init; }
            internal Vector3 TargetPos { get; init; }
            internal Vector3 TargetVel { get; init; }
            /// <summary>The target's own <c>_velocityVecInUnity</c>, before
            /// <see cref="FlightTime.TargetMapVelocity(SeaPower.ObjectBase, out float)"/> rounded it and
            /// before the evasive boost. Written to the capture only; the step loop never reads it.</summary>
            internal Vector3 TargetVelReported { get; init; }
            /// <summary>The physics step <see cref="TargetVel"/> was rounded at, 0 when the target is
            /// not a surface ship and nothing was rounded. Written to the capture only.</summary>
            internal float TargetMoveStep { get; init; }
            /// <summary>True when the live kinematic terminal waypoint uses Linear pitch mode.
            /// Its endpoint is <see cref="TermAlt"/>.</summary>
            internal bool TerminalLinear { get; init; }
            internal float TermAlt { get; init; }
            internal float TermDist { get; init; }
            internal float TermVelKn { get; init; }
            /// <summary>Resolved at setup, including SelectivePopUp's target-type check.</summary>
            internal bool TerminalPopUpEnabled { get; init; }
            internal float TerminalPopUpDist { get; init; }
            internal float TerminalPopUpHeight { get; init; }
            internal float TerminalPopUpAngle { get; init; }
            internal float TerminalVerticalTurnRate { get; init; }
            internal bool TrackDiag { get; init; }
            /// <summary>The rate every ordinary turn is budgeted at: the ammunition's
            /// <c>MaxTurnRate</c>, plus the banking roll addend where that applies.</summary>
            internal float TurnRate { get; init; }
            /// <summary>Turn-rate FLOOR for the ToBearing window only, from the ammunition's
            /// <c>LaunchTurnRate</c>, unclamped. 0 when the ini leaves it at the default, which is
            /// all but three shipped ammunition, and 0 means the step loop applies no floor. See
            /// FlightTime.Integrator.ResolveToBearingTurnRate.</summary>
            internal float ToBearingTurnRate { get; init; }
            /// <summary>The <c>MaxTurnRate</c> part of <see cref="TurnRate"/>, without the banking
            /// roll addend. The G-derate applies to this part alone, because the mover derates
            /// before it spends the separate hardcoded roll budget.</summary>
            internal float TurnRateBase { get; init; }
            /// <summary>Speed above which the G-limit starts cutting the turn rate, precomputed so
            /// the step loop pays one compare. 0 = the ammunition can never reach its own limit, so
            /// the derate is off. See FlightTime.Integrator.ResolveTurnDerateThreshold.</summary>
            internal float TurnDerateThresholdKn { get; init; }
            internal int AltLatchPhase { get; init; }
            internal bool AltLatched { get; init; }
            internal Quaternion Att { get; init; }
            internal Vector3 LaunchHeading { get; init; }
            internal float NextSample { get; init; }
            internal Vector3 Pos { get; init; }
            internal float PrevAltErr { get; init; }
            internal float PrevFlat { get; init; }
            internal float PrevPitch { get; init; }
            internal float T { get; init; }
            internal bool TlGliding { get; init; }
            /// <summary>Flat distance from launch to the end of the Kappa waypoint. This can be
            /// shorter than the shot when a non-terminal-loft round has a terminal waypoint.</summary>
            internal float KappaEndFlat { get; init; }
            /// <summary>Waypoint.KappaLoft's monotonic internal progress. It catches up toward
            /// geometric progress at no more than 2.5x flat motion; see Waypoint.UpdateWaypoint.</summary>
            internal float KappaProgress { get; init; }
            internal float VelKnots { get; init; }
            /// <summary>The non-kinematic waypoint route, or null to fly the older altitude-command
            /// law. See FlightTime.Waypoints.cs.</summary>
            internal WaypointLeg[] Legs { get; init; }
            /// <summary>The round will lead its missile group, and cash the group in on entering
            /// TerminalApproach. See the route handling in Solve.</summary>
            internal bool GroupLeader { get; init; }
        }

#if AUTOTOT_OFFLINE
        /// <summary>
        /// Per-round thrust multiplier for a replay. The game rolls one of these per missile
        /// (Missile.cs:62) and multiplies thrust by it; the estimator cannot know it in advance and
        /// correctly assumes 1.0. A replay being scored against ONE recorded flight can set the
        /// value that flight actually used, which removes the +/-2% that otherwise dominates any
        /// single kinematic round. Offline only: the shipped mod has no such field.
        /// </summary>
        internal static float OfflineMotorScale = 1f;

        /// <summary>
        /// A round's recorded physics-step timeline, so a replay can fly the steps the round
        /// actually flew instead of one average of them.
        ///
        /// The game adapts its step between 60 Hz and 30 Hz on frame rate, and a round whose step
        /// moved mid-flight used to be unscoreable: no single step describes it. That is the single
        /// largest reason the corpus discards a round. The outcome writer records every change as
        /// "t:step", and this replays them.
        ///
        /// Times are flight seconds since launch, ascending, and each entry's step holds until the
        /// next one. The first recorded entry is the step at the first sighting a few tenths of a
        /// second after launch, and it is treated as holding from t = 0, which is the best estimate
        /// available for the frames before the tracker opened.
        /// </summary>
        internal sealed class OfflineStepSchedule
        {
            internal readonly float[] At;
            internal readonly float[] Dt;

            internal OfflineStepSchedule(float[] at, float[] dt)
            {
                if (at == null || dt == null || at.Length == 0 || at.Length != dt.Length)
                    throw new ArgumentException("step schedule needs equal, non-empty arrays");
                At = at; Dt = dt;
            }

            /// <summary>Time-weighted mean step over a flight of the given length, for reporting.
            /// A round that spent most of its flight at 30 Hz should not read as a 60 Hz round.
            /// </summary>
            internal float MeanStep(float flightSeconds)
            {
                if (flightSeconds <= 0f) return Dt[0];
                float acc = 0f;
                for (int k = 0; k < At.Length; k++)
                {
                    float from = k == 0 ? 0f : At[k];
                    float to = k + 1 < At.Length ? At[k + 1] : flightSeconds;
                    if (to > flightSeconds) to = flightSeconds;
                    if (to > from) acc += (to - from) * Dt[k];
                }
                return acc / flightSeconds;
            }
        }

        /// <summary>
        /// Set for one replay and cleared afterwards, exactly as <see cref="OfflineMotorScale"/> is.
        /// Null means fly a constant step, which is every shipped solve and most replays.
        /// </summary>
        internal static OfflineStepSchedule OfflineSteps;

        /// <summary>
        /// Offline entry point to the step loop, for the replay lab in tools/lab. Exists only in
        /// the AUTOTOT_OFFLINE build; the shipped mod has no such member.
        /// </summary>
        internal static float SolveOffline(in SolveInput i, AmmunitionParameters ap, float dt,
                                           out IntegratedPhases phases)
        {
            // The step loop calls the game's own thrust and drag helpers through reflection, so the
            // lookup that binds them has to have run. In game this happens during startup.
            EnsureSimLookup();
            phases = default;
            phases.DiveStartU = -1f;
            return Solve(in i, ap, ref phases, dt);
        }
#endif

        /// <summary>
        /// Which of the two cruise-leg altitudes applies at this distance. The game runs
        /// <c>MaintainSeaSkimming</c> and <c>MaintainFinalFlightAlt</c> as separate stages with
        /// separate altitudes and switches at <c>FinalFlightPhaseDistToTarget</c>
        /// (<c>Missile.cs:614-647</c>). Inert when the ammunition declares no final-flight distance,
        /// or declares the same altitude for both, which is the common case.
        /// </summary>
        internal static float CruiseOrFinalFlightAlt(float flatDist, float finalFlightDist,
                                                     float finalFlightAlt, float cruiseAlt)
            => (finalFlightDist > 0f && flatDist <= finalFlightDist) ? finalFlightAlt : cruiseAlt;

        /// <summary>The fallthrough altitude after explicit terminal/final and loft branches have
        /// declined the step. Kinematic ammunition whose tuned loft collapsed to the route floor
        /// follows the target-level path; cruise ammunition retains its configured stage.</summary>
        internal static float ResolveUnstagedAltitude(bool nonKin, bool lofting, float targetAlt,
                                                      float flatDist, float finalFlightDist,
                                                      float finalFlightAlt, float cruiseAlt)
            => !nonKin && !lofting
                ? targetAlt
                : CruiseOrFinalFlightAlt(flatDist, finalFlightDist, finalFlightAlt, cruiseAlt);

        /// <summary>Whether an unfinished Kappa waypoint still owns altitude selection ahead of
        /// its following terminal waypoint.</summary>
        internal static bool TerminalKappaOwnsAltitude(bool terminalLinear, bool nonKin,
                                                       bool loftPath, bool hasAltitudeNodes,
                                                       float kappaEndFlat, float flatDistTotal,
                                                       float flatDistRemaining)
            => terminalLinear && !nonKin && loftPath && hasAltitudeNodes && kappaEndFlat > 0f &&
               flatDistRemaining > Mathf.Max(flatDistTotal - kappaEndFlat, 0f);

        /// <summary>Whether launch starts inside the boundary that FastForwardWaypoints uses to
        /// discard the kinematic Kappa waypoint and expose the following Immediate waypoint.</summary>
        internal static bool ImmediateAltitudeWaypointOwns(bool nonKin, bool lofting,
                                                           float loftEntryDist,
                                                           float flatDistTotal)
            => !nonKin && lofting && loftEntryDist > 0f && flatDistTotal <= loftEntryDist;

        /// <summary>Waypoint.KappaLoft's configured non-ballistic curve for a terminal popup.</summary>
        private static float TerminalPopUpAltitude(float progress, float startAlt,
                                                    float targetAlt, float configuredHeight,
                                                    float angleDeg, float routeDist)
        {
            float slope = Mathf.Tan(Mathf.Clamp(angleDeg, 0.000001f, 89.9999f) *
                                    Mathf.Deg2Rad);
            float invSlope = 1f / slope;
            float heightLimit = targetAlt + configuredHeight;
            float geometricPeak = (routeDist + startAlt * invSlope +
                                   targetAlt * invSlope) / (2f * invSlope);
            float peak = Mathf.Max(Mathf.Min(heightLimit, geometricPeak), startAlt, targetAlt);
            float climbRun = (peak - startAlt) * invSlope;
            float midpoint = Mathf.Min(0.95f, climbRun /
                Mathf.Max(routeDist, WaypointDistanceEpsilonU));
            progress = Mathf.Clamp01(progress);

            if (progress < midpoint)
            {
                float u = progress / Mathf.Max(midpoint, 0.00001f);
                float smooth = u * u * (3f - 2f * u) + u * (1f - u) * (1f - u);
                return startAlt + (peak - startAlt) * smooth;
            }

            float v = (progress - midpoint) / Mathf.Max(1f - midpoint, 0.00001f);
            float v2 = v * v;
            float v3 = v2 * v;
            float endTangent = 3f * (targetAlt - peak);
            return (2f * v3 - 3f * v2 + 1f) * peak +
                   (-2f * v3 + 3f * v2) * targetAlt +
                   (v3 - v2) * endTangent;
        }

        /// <summary>
        /// The integration loop, as a pure function of <paramref name="i"/>.
        ///
        /// <paramref name="ap"/> is passed by reference rather than copied because the game's
        /// thrust helpers take it directly, but every field reached through it is written once at
        /// ini load: LiftFactor and MinVelocity here, and _acceleration, _accelerationTime,
        /// _sustainerBurnAcceleration, _sustainerBurnTime, _maxVelocityInKnots and Kinematics inside
        /// CalculateThrustOverTime. Concurrent reads of those are safe.
        /// </summary>
        private static float Solve(in SolveInput i, AmmunitionParameters ap,
                                   ref IntegratedPhases phases, float dt = IntegrationStepSim)
        {
            const float KU = GameUnits.KnotsToUnityPerSecond;
            // Argument arrays for the MethodInfo fallback, which only runs when the delegate fast
            // path failed to bind. Allocated on first use: beta always binds, and these were two
            // allocations on every solve for a branch it never took.
            object[] thrustArgs = null;
            object[] dragArgs = null;

            Vector2[]   altNodes               = i.AltNodes;
            string      ammoLabel              = i.AmmoLabel;
            float       boostClimbDeg          = i.BoostClimbDeg;
            float       decelRate              = i.DecelRate;
            float       decelPerStep           = decelRate * dt;
            float       descentDeg             = i.DescentDeg;
            float       descentOnsetDeg        = i.DescentOnsetDeg;
            float       dragFactor             = i.DragFactor;
            float       cruiseAlt              = i.CruiseAlt;
            float       finalFlightAlt         = i.FinalFlightAlt;
            float       finalFlightDist        = i.FinalFlightDist;
            float       finalDist              = i.FinalDist;
            float       flatDistTotal          = i.FlatDistTotal;
            float       initialPhaseDur        = i.InitialPhaseDur;
            bool        slightBallisticLaunch  = i.PerformsSlightBallisticInitialFlight;
            bool        isAir                  = i.IsAir;
            bool        isHighBallisticLofter  = i.IsHighBallisticLofter;
            bool        isTerminalLoft         = i.IsTerminalLoft;
            float       launchPitch            = i.LaunchPitch;
            float       loftAlt                = i.LoftAlt;
            float       loftEntryDist          = i.LoftEntryDist;
            float       loftSpeedHoldDist      = i.LoftSpeedHoldDist;
            float       loftVelKn              = i.LoftVelKn;
            bool        ballisticLoft          = i.BallisticLoft;
            float       loftClimbTan           = i.LoftClimbTan;
            float       loftDescentTan         = i.LoftDescentTan;
            float       launchAltU             = i.LaunchAltU;
            Vector3     launchPos              = i.Pos;
            bool        lofting                = i.Lofting;
            float       maxFlight              = i.MaxFlight;
            float       maxVelKn               = i.MaxVelKn;
            bool        nonKin                 = i.NonKin;
            float       targetAlt0             = i.TargetAlt0;
            Vector3     targetPos              = i.TargetPos;
            Vector3     targetVel              = i.TargetVel;
            bool        terminalLinear         = i.TerminalLinear;
            float       termAlt                = i.TermAlt;
            float       termDist               = i.TermDist;
            float       termVelKn              = i.TermVelKn;
            bool        terminalPopUpEnabled   = i.TerminalPopUpEnabled;
            float       terminalPopUpDist      = i.TerminalPopUpDist;
            float       terminalPopUpHeight    = i.TerminalPopUpHeight;
            float       terminalPopUpAngle     = i.TerminalPopUpAngle;
            float       terminalVerticalRate   = i.TerminalVerticalTurnRate;
            bool        trackDiag              = i.TrackDiag;
            float       turnRate               = i.TurnRate;
            float       toBearingTurnRate      = i.ToBearingTurnRate;
            float       turnRateBase           = i.TurnRateBase;
            float       turnDerateThresholdKn  = i.TurnDerateThresholdKn;
            // The mover derates MaxTurnRate for the G-limit and only then spends the separate
            // hardcoded roll budget, so the addend rides above the derate rather than through it.
                float       bankingRollAddend      = turnRate - turnRateBase;
            int         altLatchPhase          = i.AltLatchPhase;
            bool        altLatched             = i.AltLatched;
            Quaternion  att                    = i.Att;
            Vector3     launchHeading          = i.LaunchHeading;
            float       nextSample             = i.NextSample;
            Vector3     pos                    = i.Pos;
            float       prevAltErr             = i.PrevAltErr;
            float       prevFlat               = i.PrevFlat;
            float       prevPitch              = i.PrevPitch;
            float       t                      = i.T;
            bool        tlGliding              = i.TlGliding;
            float       kappaEndFlat           = i.KappaEndFlat;
            float       kappaProgress          = i.KappaProgress;
            float       velKnots               = i.VelKnots;
            bool        terminalLineStarted    = false;
            float       terminalLinePitch      = 0f;
            bool        terminalPopUpStarted   = false;
            float       terminalPopUpStartAlt  = 0f;
            float       terminalPopUpRouteDist = 0f;
            float       terminalPopUpProgress  = 0f;
            bool        launchRecoveryDone     = false;
            // The waypoint route. legEff is the active leg with any skip-copied settings applied;
            // legStartFlat and legStartAlt are the game's _lastWaypointPosition, which follows the
            // missile until the initial phase ends (Missile.cs:2469-2480).
            //
            // 2026-09-13: air-launched rounds fly the route too. The gate that held them out was
            // written when none had been flown with it; twenty have now, across four ranges, and
            // the route is worth +0.07 to +1.16 s against the older altitude law's 2.98 s mean. It
            // could not be lifted before the closest-approach guard above learned about diving
            // rounds, which is what made it look four times worse. The drop phase that gate's
            // comment worried about is 1.0 s for usn_agm-84d and does not show above this noise.
            // `nonKin` and `!isTerminalLoft` are defensive: setup, and the lab's rebuild, only make
            // a route for such a round.
            WaypointLeg[] legs                 = WaypointLegSteering && nonKin &&
                                                 !isTerminalLoft ? i.Legs : null;
            int         legIdx                 = 0;
            WaypointLeg legEff                 = legs != null ? legs[0] : default;
            float       legStartFlat           = -1f;
            float       legStartAlt            = i.LaunchAltU;
            float       legProgress            = 0f;
            bool        legCashedIn            = false;

            // When launch begins inside the loft/sea-skimming boundary, FastForwardWaypoints
            // removes the leading Kappa waypoint. The remaining kinematic altitude waypoint is
            // Immediate with AllowExceedingAngleLimits=true: it aims directly at its fixed-height
            // look-ahead point instead of clamping the command to MaxLoftAngle. For a VLS round
            // far below that height this is effectively a 90-degree climb. The finite turn rate
            // still owns the later nose-over and its resulting altitude overshoot.
            bool immediateAltitudeWaypoint = ImmediateAltitudeWaypointOwns(
                nonKin, lofting, loftEntryDist, flatDistTotal);
            float altitudeClimbCommandDeg = immediateAltitudeWaypoint
                ? BoostClimbDeg
                : boostClimbDeg;

            // The receding test below is a closest-approach guard: it ends the flight when the
            // round stops closing, which is how an overflight terminates. It must not fire
            // BEFORE the round has ever closed, because a fixed-rail launch deliberately flies
            // the rail's bearing first (see the launch-heading block a few lines down) and then
            // turns onto the target. With the rail more than ~90 degrees off, distance grows
            // from the first step, the guard fired at t = 2*dt, and the estimate came back as
            // exactly 0.2s for a real 700s shot. Ships never produced it because a trainable
            // launcher is already pointed at the target; an aircraft's hardpoint points wherever
            // the nose does, so any turn hit it. See
            // docs/plans/done/2026-09-07-offboresight-estimate-collapse.md.
            //
            // 2026-09-07: the latch as first written armed on the SEED step. `prevFlat` starts
            // at float.MaxValue, so the very first `flatDist < prevFlat` is true no matter which
            // way the round is pointing, and the guard was live again by t = 2*dt. The run that
            // day still produced 0.20s eleven times; only the plausibility floor caught them.
            // `havePrev` makes the first real step the first comparison.
            //
            // 2026-09-09: one step of closing is still not closing. A Chandler's Harpoon sits on a
            // canister that cannot train (`fixedRail True, containersRotatable False`), and with the
            // target 89 degrees off the rail the round flies almost square to the line of sight. The
            // TARGET's own motion then shortens the range for a step or two, which armed the latch,
            // and the next step receded and ended the flight: 2.00s returned for a shot whose
            // straight-line floor was 337.2s, eleven times across two runs. The comment above
            // predicted this only past 90 degrees, and believed ships immune because a trainable
            // launcher already points at the target; this launcher does not train.
            //
            // So the latch now needs the range to have fallen MEANINGFULLY below where it started,
            // by more than a wobble of that kind can produce. Overflight, which is what the guard is
            // for, clears the margin by kilometres long before the round arrives. A round that can
            // never catch its target now runs to maxFlight and is answered by another tier, which is
            // the honest outcome; before, it returned a fabricated 2 seconds.
            // See finding 14 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
            bool hasClosed = false;
            float prevSlant = float.MaxValue;
            bool havePrev = false;
            float startFlat = -1f;
            float closeMargin = 0f;
            // Counted rather than derived from t/dt, which is only the step count while the step is
            // constant. See the offline step schedule below.
            int steps = 0;
#if AUTOTOT_OFFLINE
            OfflineStepSchedule sched = OfflineSteps;
            int schedIdx = 0;
#endif

            while (t < maxFlight)
            {
#if AUTOTOT_OFFLINE
                // Fly the steps this round actually flew. Guarded rather than branch-free because
                // the shipped mod always integrates at a constant step and must not pay for a test
                // it can never take; the motor multiplier above is offline-only for the same reason.
                if (sched != null)
                {
                    while (schedIdx + 1 < sched.At.Length && t >= sched.At[schedIdx + 1]) schedIdx++;
                    if (dt != sched.Dt[schedIdx])
                    {
                        dt = sched.Dt[schedIdx];
                        decelPerStep = decelRate * dt;   // the one precomputed term that scales with dt
                    }
                }
#endif
                steps++;
                Vector3 predTgt = targetPos + targetVel * t;
                float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                if (startFlat < 0f)
                {
                    startFlat = flatDist;
                    // Proportional, with an absolute floor: a 20 km shot is gated on the floor and a
                    // 700 km one on the fraction, so neither a short shot nor a long one can arm the
                    // latch on noise. The floor is the same distance the arrival test uses.
                    closeMargin = Mathf.Max(CloseEnoughDistU, startFlat * RecedeCloseFraction);
                }

                // 2026-09-13: flat range alone is the wrong test for closest approach when the
                // round is diving. A terminal waypoint with AllowExceedingAngleLimits left at its
                // default true and PitchMode Immediate commands a round straight down, and the game
                // flies it: usn_agm-84d launched 10.8 km out from 20,000 ft holds -90 degrees for
                // eleven seconds with its x and z frozen. Flat range then stops shrinking, and a
                // target under way pushes it back up, so the guard fired at 7.1 s of a 56.8 s
                // flight. Requiring slant range to recede too cannot strand the solve: the guard is
                // only deferred while the round is trading altitude for nothing, and there is a
                // finite amount of that to trade, so once it levels out dy goes to zero and slant
                // recedes exactly when flat does.
                float slantDy = pos.y - predTgt.y;
                float slantDist = Mathf.Sqrt(flatDist * flatDist + slantDy * slantDy);
                if ((flatDist > prevFlat && slantDist > prevSlant && t > dt && hasClosed) ||
                    flatDist < CloseEnoughDistU)
                {
                    ModelStats.LoopDone(steps);
                    if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
                    return t;
                }
                if (havePrev && flatDist < prevFlat && flatDist <= startFlat - closeMargin)
                    hasClosed = true;
                prevFlat = flatDist;
                prevSlant = slantDist;
                havePrev = true;

                Vector3 horizDir = flatDist > 1e-4f
                    ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;
                Vector3 horizDirTarget = horizDir;   // where the round wants to point
                // Dropping `nonKin` here is deliberate. The gate was written believing the single
                // combined rotation budget was reached only on the legacy path, but the
                // Kinematics == Full path spends one budget too: a surface target with the default
                // TerminalVerticalTurnRate takes one Vector3.RotateTowards covering yaw and pitch
                // together (WeaponBase.setCourseTowardsPosition, the else of the
                // |terminalRate - rate| > 1 split), and an air target takes a single 3-D cone
                // rotation. Modelling the axes independently for kinematic ammo spent the budget
                // twice, exactly the error this gate removes for non-kinematic ammo. What stays
                // nonKin-only is BankingAddsRollBudgetToPitch: only the legacy path folds roll into
                // the same quaternion.
                bool coupledTurn = CoupledPitchYawRateLimit
                                && launchHeading.sqrMagnitude > 0.5f && t >= initialPhaseDur;

                // ToBearing, hoisted above the heading slew because the launch turn rate below has
                // to reach the slew, the coupled turn and the pitch step alike. The mover picks the
                // rate once per setCourseTowardsPosition call and spends it on whichever axes that
                // call touches, so the model must not pick a different rate per axis.
                //
                // The 3-D cone the game tests is reduced to elevation here (Missile.cs:343). The
                // horizontal component is already aligned on every shot fired near its own bearing,
                // and the coupled turn carries it where it is not.
                float elevToTgtDeg = Mathf.Atan2(predTgt.y - pos.y,
                                                 Mathf.Max(flatDist, 1e-4f)) * Mathf.Rad2Deg;
                bool inToBearing = launchPitch >= 0f
                                && t >= initialPhaseDur
                                && t < initialPhaseDur + ToBearingMaxSeconds
                                && Mathf.Abs(prevPitch - elevToTgtDeg) >= ToBearingConeDeg;

                // The rate this step is budgeted at. Two corrections ride on the base rate, in the
                // mover's own order: the G-limit derate first, then the ToBearing override, which
                // the mover applies as a floor AFTER derating (setCourseTowardsPosition reads
                // LaunchTurnRate only if it exceeds the already-derated rate). toBearingTurnRate
                // is the raw ini value, 0 when unset, so the compare below is the mover's own and
                // an unset key can never re-floor a derated step back up to MaxTurnRate.
                float stepTurnRate = turnRateBase;
                if (TurnRateGDerate && turnDerateThresholdKn > 0f && velKnots > turnDerateThresholdKn)
                    stepTurnRate *= turnDerateThresholdKn / velKnots;
                if (LaunchTurnRateOverride && inToBearing && toBearingTurnRate > stepTurnRate)
                    stepTurnRate = toBearingTurnRate;
                stepTurnRate += bankingRollAddend;

                // Fixed-rail launch heading: fly the rail's bearing for the initial flight
                // phase, then turn toward the target at MaxTurnRate. The horizontal mirror of
                // the launchPitch hold further down.
                if (launchHeading.sqrMagnitude > 0.5f)
                {
                    // Heading slews on its own only on the independent path. The coupled path
                    // turns heading and pitch together further down, out of one budget.
                    if (t >= initialPhaseDur && !coupledTurn)
                        launchHeading = Vector3.RotateTowards(
                            launchHeading, horizDir, stepTurnRate * Mathf.Deg2Rad * dt, 0f).normalized;
                    horizDir = launchHeading;
                }

                float pitchDeg = 0f;
                int phase = 1;
                float stageTgt = maxVelKn;
                {
                    float stageAlt;
                    float xNow = GameMath.FlatDistance(launchPos, pos);
                    bool loftPath = lofting &&
                        (loftEntryDist <= 0f || flatDistTotal > loftEntryDist);
                    // The live terminal waypoint cannot become active before the Kappa waypoint
                    // has completed. A low terminal endpoint must not feed descentGeomDist and
                    // truncate the valid Kappa curve early.
                    bool terminalKappaActive = TerminalKappaOwnsAltitude(
                        terminalLinear, nonKin, loftPath, altNodes != null, kappaEndFlat,
                        flatDistTotal, flatDist);
                    float descentGeomDist = (pos.y - termAlt)
                                          / Mathf.Tan(Mathf.Max(descentOnsetDeg, MinDescentOnsetDeg) * Mathf.Deg2Rad);
                    float diveStart = Mathf.Max(termDist, descentGeomDist);
                    if (!terminalKappaActive && diveStart > 0f && flatDist <= diveStart)
                    { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                    else if (!terminalKappaActive && finalDist > 0f && flatDist <= finalDist)
                    {
                        phase = 1;
                        stageAlt = CruiseOrFinalFlightAlt(flatDist, finalFlightDist,
                                                          finalFlightAlt, cruiseAlt);
                        // The altitude schedule ends here, the speed may not. An ammunition that
                        // requires a target to proceed stays in MaintainLoftAlt, and so at loft
                        // speed, until its seeker activates, which is further in than this
                        // boundary. It still descends on cue, which is why only stageTgt is held.
                        stageTgt = (loftSpeedHoldDist > 0f && flatDist > loftSpeedHoldDist)
                                 ? loftVelKn : maxVelKn;
                    }
                    // A round only enters the loft if it was LAUNCHED outside the sea-skimming
                    // boundary. Coming out of ToBearing the stage is not MaintainLoftAlt, so the
                    // `_loftToSkim || stage != MaintainLoftAlt` clause at Missile.cs:641 is true for
                    // every ammunition and the sea-skimming branch wins; once there, `num` is 2 and
                    // the loft branch (`num <= 1`) is unreachable for the rest of the flight.
                    // `lofting` alone is a property of the AMMUNITION (loftAlt > launchAlt) and is
                    // true at every range, so it says the round CAN loft, not that it WILL.
                    //
                    // Tested on the launch RANGE rather than the running distance: entry is decided
                    // once, at ToBearing, and cannot be re-entered later.
                    //
                    // Measured 2026-09-08 inside the band where the two disagree: ss-n-12 at 90.6nmi
                    // +11.6s and ss-n-19 at 91.5nmi -4.8s, the model climbing to 44300 and 50000ft
                    // while both rounds sea-skimmed. The signs differ because a long spurious loft
                    // repays its climb at loft speed and a short one does not.
                    else if (loftPath)
                    {
                        stageTgt = loftVelKn;
                        // The live kinematic path flies a KappaLoft waypoint whose ceiling has
                        // already been replaced by TuneWaypoints. Setup stores the game's sampled
                        // Kappa curve in altNodes. LoftProfileAltU remains for old captures which
                        // predate those resolved nodes, and in game only where the sample is not
                        // there: a leg too short to sample, or a failed bind or sample, which setup
                        // now warns about. Cruise missiles still hold the configured ceiling.
                        stageAlt = (!nonKin && altNodes != null)
                            ? InterpNodeAlt(altNodes, xNow)
                            : (!nonKin && loftClimbTan > 0f)
                                ? LoftProfileAltU(ballisticLoft, loftAlt, loftClimbTan,
                                                  loftDescentTan, launchAltU, targetAlt0,
                                                  flatDistTotal, xNow)
                                : loftAlt;
                        phase = 0;
                    }
                    else
                    {
                        phase = 1;
                        // A kinematic Kappa waypoint whose tuned loft height is at the launch
                        // floor stays on the target-level path. Do not reuse the ammunition's
                        // defaulted cruise ceiling here: that field can alias MaxLoftAlt when the
                        // ammunition does not define a separate cruise altitude.
                        // Explicit final/sea-skimming stages were handled by the branch above and
                        // deliberately keep their configured altitude.
                        stageAlt = ResolveUnstagedAltitude(
                            nonKin, lofting, targetAlt0, flatDist, finalFlightDist,
                            finalFlightAlt, cruiseAlt);
                        stageTgt = maxVelKn;
                    }

                    float altErr = stageAlt - pos.y;
                    float targetPitch = 0f;
                    float diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg;

                    // The game's ToBearing test: angle between the missile's attitude and the
                    // line to its aim point, against a 5 deg cone, with a 10.0s cap
                    // inToBearing is resolved at the top of the step, because the launch turn
                    // rate it selects has to reach the heading slew as well as the pitch step.
                    // Launch and ToBearing both command _maxVelocityInKnots (Missile.cs:3142).
                    if (LaunchStageSpeed && (t < initialPhaseDur || inToBearing))
                        stageTgt = maxVelKn;

                    bool holdingAlt = false;
                    if (LatchedProportionalHold)
                    {
                        if (phase != altLatchPhase)
                        { altLatchPhase = phase; altLatched = false; prevAltErr = float.NaN; }
                        // Arrival = the altitude error CHANGES SIGN. A band test cannot work:
                        // cruise moves 3-5.5u per 0.1s step against a 1u AltToleranceU.
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
                        float holdLook = Mathf.Max(velKnots * KU * LookaheadSim, MinLookaheadU);
                        targetPitch = Mathf.Clamp(Mathf.Atan2(altErr, holdLook) * Mathf.Rad2Deg,
                                                  -diveDeg, altitudeClimbCommandDeg);
                    }
                    else if (altErr > AltToleranceU) targetPitch = altitudeClimbCommandDeg;
                    else if (altErr < -AltToleranceU) targetPitch = -diveDeg;

                    // Missile.SetMissileCourse's pre-apex guard, approximated with unstretched
                    // look-ahead points. Only for rounds that fly without a waypoint route (air-
                    // launched, or replayed from a capture older than rich-flight-v3); the route
                    // block below runs the exact guard on the real points.
                    bool nonKappaImmediate = phase == 0 && nonKin;
                    if (legs == null && nonKappaImmediate && !holdingAlt && t >= initialPhaseDur)
                    {
                        float flatSpeedForGuard = velKnots * KU *
                            Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad));
                        float climbSlope = Mathf.Tan(Mathf.Clamp(boostClimbDeg,
                            0.01f, 89f) * Mathf.Deg2Rad);
                        float goalAlt = Mathf.Min(stageAlt, pos.y +
                            flatSpeedForGuard * WaypointSteeringLookaheadSim * climbSlope);
                        float futureAlt = Mathf.Min(stageAlt, pos.y +
                            flatSpeedForGuard * WaypointFutureLookaheadSim * climbSlope);
                        float betweenRun = flatSpeedForGuard *
                            (WaypointFutureLookaheadSim - WaypointSteeringLookaheadSim);
                        float pathPitch = Mathf.Atan2(futureAlt - goalAlt,
                            Mathf.Max(betweenRun, WaypointDistanceEpsilonU)) * Mathf.Rad2Deg;
                        float verticalSpeed = velKnots * KU *
                            Mathf.Sin(prevPitch * Mathf.Deg2Rad);
                        float timeToFutureAlt = (futureAlt - pos.y) / verticalSpeed;
                        float pitchDifference = Mathf.Abs(pathPitch - prevPitch);
                        // Missile.cs:2561-2575. The guard's budget is the raw ini rate, not the
                        // mover's: no G-derate and no banking roll addend, which would lift
                        // SS-N-19's 8 deg/s to 68. It is then scaled by pitch's share of the
                        // combined heading and pitch error.
                        float guardRate = inToBearing && toBearingTurnRate > 0f
                            ? toBearingTurnRate : turnRateBase;
                        float headingError = Vector3.Angle(horizDir, horizDirTarget);
                        float combinedError = Mathf.Sqrt(headingError * headingError +
                                                         pitchDifference * pitchDifference);
                        float verticalShare = combinedError > 0.001f
                            ? pitchDifference / combinedError : 1f;
                        float allocatedVerticalRate = guardRate * verticalShare;
                        float requiredRate = pitchDifference / timeToFutureAlt;
                        bool distinctLookAheadPoints = flatDist >
                            flatSpeedForGuard * WaypointSteeringLookaheadSim +
                            WaypointDistanceEpsilonU;
                        if (Mathf.Abs(requiredRate) > allocatedVerticalRate &&
                            timeToFutureAlt > 0.1f && distinctLookAheadPoints)
                            targetPitch = 0f;
                    }

                    // Fly the waypoint route: steer at the active leg's 1.5 s look-ahead point, or
                    // level off when Missile.SetMissileCourse's pre-apex guard (Missile.cs:2559-2581)
                    // says the round cannot shed its pitch before the 8 s point. Every term below is
                    // the game's; see FlightTime.Waypoints.cs for the point itself.
                    if (legs != null)
                    {
                        float legFlatSpeed = velKnots * KU * Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad));
                        if (t < initialPhaseDur || legStartFlat < 0f)
                        {
                            // Until guidance starts the game resets _lastWaypointPosition to the
                            // missile every tick, so the first leg is measured from here.
                            legStartFlat = flatDist;
                            legStartAlt = pos.y;
                        }
                        else
                        {
                            // A group leader cashes its group in on entering TerminalApproach and
                            // rebuilds its route (Missile.cs:858-862, MissileGroup.cs:199-200).
                            // GuardTrack shows the rebuilt route holding only the final leg,
                            // started from the round's position, on all six rounds flown with it;
                            // the code path that drops the leg before it is not identified. The
                            // entry distance is TermDist, the seeker search after the terminal
                            // boundary, which is where those six entered to within a tick.
                            if (i.GroupLeader && !legCashedIn && flatDist <= termDist &&
                                legIdx < legs.Length - 1)
                            {
                                legCashedIn = true;
                                legIdx = legs.Length - 1;
                                legEff = legs[legIdx];
                                legStartFlat = flatDist;
                                legStartAlt = pos.y;
                                legProgress = 0f;
                            }
                            // Waypoint.UpdateAndGetActiveWaypoint: complete every leg passed.
                            while (legIdx < legs.Length - 1 && flatDist <= legEff.EndDist)
                            {
                                bool copy = legProgress > LegSkipCopyProgress && legEff.SkipNext;
                                WaypointLeg done = legEff;
                                legIdx++;
                                legEff = copy ? legs[legIdx].WithSettingsOf(in done) : legs[legIdx];
                                legStartFlat = flatDist;
                                legStartAlt = pos.y;
                                legProgress = 0f;
                            }
                            // Waypoint.UpdateWaypoint's progress: geometric, caught up by at most
                            // 2.5x this tick's flat motion (Waypoint.cs:372-385).
                            float travelled = Mathf.Max(legStartFlat - flatDist, 0f);
                            float legTotal = Mathf.Max(travelled +
                                Mathf.Max(flatDist - legEff.EndDist, 0f), WaypointDistanceEpsilonU);
                            legProgress = Mathf.Max(legProgress, Mathf.Min(travelled / legTotal,
                                legProgress + WaypointProgressCatchupMultiplier * legFlatSpeed * dt / legTotal));

                            // A Kappa leg is the terminal popup, flown by its own block below.
                            if (legEff.Mode != WaypointLegMode.Kappa)
                            {
                                float legVerticalSpeed = velKnots * KU * Mathf.Sin(prevPitch * Mathf.Deg2Rad);
                                Vector2 desired = WaypointPointAtTime(legs, legIdx, in legEff,
                                    legProgress, legStartAlt, WaypointSteeringLookaheadSim, flatDist,
                                    pos.y, legFlatSpeed, legVerticalSpeed);
                                Vector2 future = WaypointPointAtTime(legs, legIdx, in legEff,
                                    legProgress, legStartAlt, WaypointFutureLookaheadSim, flatDist,
                                    pos.y, legFlatSpeed, legVerticalSpeed);
                                float desiredAlt = desired.y;

                                // The guard, term for term. rate is the raw ini value, or the
                                // ToBearing override in that state, scaled by pitch's share of
                                // the combined heading and pitch error.
                                float pathPitch = Mathf.Atan2(future.y - desired.y,
                                    Mathf.Max(desired.x - future.x, 0f)) * Mathf.Rad2Deg;
                                float pitchDifference = Mathf.Abs(pathPitch - prevPitch);
                                float guardRate = inToBearing && toBearingTurnRate > 0f
                                    ? toBearingTurnRate : turnRateBase;
                                float headingError = Vector3.Angle(horizDir, horizDirTarget);
                                float combinedError = Mathf.Sqrt(headingError * headingError +
                                                                 pitchDifference * pitchDifference);
                                float verticalShare = combinedError > 0.001f
                                    ? pitchDifference / combinedError : 1f;
                                float timeToFuture = (future.y - pos.y) / legVerticalSpeed;
                                float requiredRate = pitchDifference / timeToFuture;
                                // Unity's Vector3 != is true beyond a squared distance of 1e-10.
                                bool distinct = (future - desired).sqrMagnitude > UnityVectorEqualitySqr;
                                if (Mathf.Abs(requiredRate) > guardRate * verticalShare &&
                                    timeToFuture > 0.1f && distinct)
                                    desiredAlt = pos.y;

                                targetPitch = Mathf.Atan2(desiredAlt - pos.y,
                                    Mathf.Max(flatDist - desired.x, WaypointDistanceEpsilonU)) *
                                    Mathf.Rad2Deg;
                            }
                        }
                    }

                    // Kinematic terminal waypoints use Linear pitch and allow exceeding their
                    // configured altitude-angle constraints. Latch the line when the waypoint
                    // becomes active, exactly as the game fixes lastWaypoint at the transition.
                    if (terminalLinear && phase == 2)
                    {
                        if (!terminalLineStarted)
                        {
                            terminalLinePitch = Mathf.Atan2(termAlt - pos.y,
                                Mathf.Max(flatDist, WaypointDistanceEpsilonU)) * Mathf.Rad2Deg;
                            terminalLineStarted = true;
                        }
                        targetPitch = terminalLinePitch;
                    }

                    // Reproduce Waypoint.UpdateWaypoint/GetKappaLoftAltitude. A non-terminal
                    // round leaves this controller when its Kappa waypoint ends; TerminalLoft and
                    // a Kappa leg spanning the whole shot keep it through the terminal state.
                    bool kappaStillActive = phase == 0 || isTerminalLoft ||
                        kappaEndFlat >= flatDistTotal - WaypointDistanceEpsilonU;
                    if (!nonKin && lofting && altNodes != null && kappaStillActive)
                    {
                        float routeDist = Mathf.Max(kappaEndFlat > 0f ? kappaEndFlat
                            : xNow + flatDist, WaypointDistanceEpsilonU);
                        float flatSpeed = Mathf.Max(velKnots * KU *
                            Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad)), 0f);
                        // GeoPosition.GetDistance ignores height by default. For the Kappa
                        // waypoint's OnLineToTarget endpoint, UpdateWaypoint normalizes by flat
                        // launch-to-current plus flat current-to-target, even when the waypoint
                        // itself ends at an earlier distance-to-target boundary. Such a waypoint
                        // therefore completes while still partway through its normalized curve.
                        float progressTotal = Mathf.Max(xNow + flatDist, WaypointDistanceEpsilonU);
                        float geometric = Mathf.Clamp01(xNow / progressTotal);
                        float progressStep = WaypointProgressCatchupMultiplier * flatSpeed * dt /
                                             progressTotal;
                        kappaProgress = Mathf.Max(kappaProgress,
                            Mathf.Min(geometric, kappaProgress + progressStep));

                        float look = Mathf.Clamp(flatSpeed * WaypointSteeringLookaheadSim,
                                                 WaypointDistanceEpsilonU,
                                                 Mathf.Max(flatDist, WaypointDistanceEpsilonU));
                        float futureProgress = Mathf.Min(kappaProgress +
                            (1f - kappaProgress) * look /
                            Mathf.Max(flatDist, WaypointDistanceEpsilonU), 1f);
                        float curveNow = InterpNodeAlt(altNodes, routeDist * kappaProgress);
                        float curveFuture = InterpNodeAlt(altNodes, routeDist * futureProgress);
                        float curveRun = Mathf.Max((futureProgress - kappaProgress) * routeDist,
                                                   WaypointDistanceEpsilonU);
                        float tangentAlt = curveNow + look * (curveFuture - curveNow) / curveRun;

                        float correctionRun = Mathf.Clamp(flatSpeed * WaypointFutureLookaheadSim,
                                                          look,
                            Mathf.Max((1f - kappaProgress) * routeDist, look));
                        float correctionProgress = Mathf.Min(kappaProgress +
                            correctionRun / routeDist, 1f);
                        float correctionAlt = InterpNodeAlt(altNodes,
                            routeDist * correctionProgress);
                        float floorAlt = pos.y + look * (correctionAlt - pos.y) / correctionRun;
                        float commandAlt = Mathf.Max(tangentAlt, floorAlt);
                        targetPitch = Mathf.Clamp(
                            Mathf.Atan2(commandAlt - pos.y, look) * Mathf.Rad2Deg,
                            -descentDeg, altitudeClimbCommandDeg);
                    }
                    else if (isTerminalLoft && lofting)
                    {
                        if (!tlGliding && pos.y >= loftAlt - AltToleranceU) tlGliding = true;
                        if (tlGliding)
                        {
                            float glideDeg = Mathf.Atan2(Mathf.Max(pos.y - targetAlt0, 0f),
                                Mathf.Max(flatDist, 1f)) * Mathf.Rad2Deg;
                            targetPitch = -Mathf.Min(glideDeg, descentDeg);
                        }
                    }

                    bool terminalPopUpActiveThisStep = terminalPopUpEnabled &&
                        terminalPopUpDist > 0f && terminalPopUpHeight > 0f &&
                        flatDist <= terminalPopUpDist;
                    if (terminalPopUpActiveThisStep)
                    {
                        if (!terminalPopUpStarted)
                        {
                            terminalPopUpStarted = true;
                            terminalPopUpStartAlt = pos.y;
                            terminalPopUpRouteDist = Mathf.Max(flatDist,
                                WaypointDistanceEpsilonU);
                            terminalPopUpProgress = 0f;
                        }

                        float flatSpeed = Mathf.Max(velKnots * KU *
                            Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad)), 0f);
                        float travelled = Mathf.Max(terminalPopUpRouteDist - flatDist, 0f);
                        float progressTotal = Mathf.Max(travelled + flatDist,
                            WaypointDistanceEpsilonU);
                        float geometric = Mathf.Clamp01(travelled / progressTotal);
                        terminalPopUpProgress = Mathf.Max(terminalPopUpProgress,
                            Mathf.Min(geometric, terminalPopUpProgress +
                                WaypointProgressCatchupMultiplier * flatSpeed * dt / progressTotal));

                        float look = Mathf.Clamp(flatSpeed * WaypointSteeringLookaheadSim,
                            WaypointDistanceEpsilonU,
                            Mathf.Max(flatDist, WaypointDistanceEpsilonU));
                        float futureProgress = Mathf.Min(terminalPopUpProgress +
                            (1f - terminalPopUpProgress) * look /
                            Mathf.Max(flatDist, WaypointDistanceEpsilonU), 1f);
                        float curveNow = TerminalPopUpAltitude(terminalPopUpProgress,
                            terminalPopUpStartAlt, targetAlt0, terminalPopUpHeight,
                            terminalPopUpAngle, terminalPopUpRouteDist);
                        float curveFuture = TerminalPopUpAltitude(futureProgress,
                            terminalPopUpStartAlt, targetAlt0, terminalPopUpHeight,
                            terminalPopUpAngle, terminalPopUpRouteDist);
                        float curveRun = Mathf.Max(
                            (futureProgress - terminalPopUpProgress) * terminalPopUpRouteDist,
                            WaypointDistanceEpsilonU);
                        float tangentAlt = curveNow + look *
                            (curveFuture - curveNow) / curveRun;

                        float correctionRun = Mathf.Clamp(
                            flatSpeed * WaypointFutureLookaheadSim, look,
                            Mathf.Max((1f - terminalPopUpProgress) *
                                terminalPopUpRouteDist, look));
                        float correctionProgress = Mathf.Min(terminalPopUpProgress +
                            correctionRun / terminalPopUpRouteDist, 1f);
                        float correctionAlt = TerminalPopUpAltitude(correctionProgress,
                            terminalPopUpStartAlt, targetAlt0, terminalPopUpHeight,
                            terminalPopUpAngle, terminalPopUpRouteDist);
                        float floorAlt = pos.y + look *
                            (correctionAlt - pos.y) / correctionRun;
                        targetPitch = Mathf.Clamp(Mathf.Atan2(
                            Mathf.Max(tangentAlt, floorAlt) - pos.y, look) * Mathf.Rad2Deg,
                            -89f, 89f);
                    }

                    if (launchPitch >= 0f && t < initialPhaseDur)
                    {
                        // Missile.Launch rotates a nose-high round down at a fixed 6 deg/s when
                        // this ammunition flag is set. This is a controller law, not an empirical
                        // offset: RGM-109B's 3 s launch therefore leaves at 72 degrees, while an
                        // SS-N-19 launched at 45 degrees for 5 s leaves at 15 degrees.
                        targetPitch = slightBallisticLaunch
                            ? Mathf.Max(10f, launchPitch - 6f * (t + dt))
                            : launchPitch;
                    }

                    bool splitTerminalTurn = terminalPopUpActiveThisStep &&
                        terminalVerticalRate > 0f &&
                        Mathf.Abs(terminalVerticalRate - stepTurnRate) > 1f;
                    // The ballistic launch rotation is performed directly on pitch. When ordinary
                    // guidance resumes, SetMissileCourse restores that pitch at MaxTurnRate; the
                    // separate banking/roll budget must not snap the nose back to the climb command.
                    // The game reads the flag only in the Launch and GoToSurface states
                    // (Missile.cs:163, :246), so this ends the first time the nose reaches its
                    // command. Left open, it re-armed on every later pull-up, including dive
                    // pull-outs, and flew SS-N-3B's terminal level-off 5u below the sea.
                    bool splitLaunchRecovery = slightBallisticLaunch && !launchRecoveryDone &&
                        t >= initialPhaseDur && targetPitch > prevPitch + 0.01f &&
                        turnRateBase > 0f;
                    if (slightBallisticLaunch && t >= initialPhaseDur && !splitLaunchRecovery)
                        launchRecoveryDone = true;
                    if (coupledTurn && !splitTerminalTurn && !splitLaunchRecovery)
                    {
                        // One RotateTowards for pitch and yaw together, as the live mover does.
                        // Unity's euler x is nose-DOWN positive, so climb-positive pitch is
                        // negated going in and read back off the forward vector coming out.
                        Quaternion tgt = GameMath.QEuler(-targetPitch, YawOf(horizDirTarget), 0f);
                        att = GameMath.QRotateTowards(att, tgt, stepTurnRate * dt);
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
                        float pitchTurnRate = splitTerminalTurn ? terminalVerticalRate :
                            splitLaunchRecovery ? turnRateBase : stepTurnRate;
                        pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, pitchTurnRate * dt);
                        if ((splitTerminalTurn || splitLaunchRecovery) &&
                            launchHeading.sqrMagnitude > 0.5f)
                        {
                            Quaternion fromYaw = GameMath.QEuler(0f, YawOf(launchHeading), 0f);
                            Quaternion toYaw = GameMath.QEuler(0f, YawOf(horizDirTarget), 0f);
                            launchHeading = (GameMath.QRotateTowards(
                                fromYaw, toYaw, stepTurnRate * dt) * Vector3.forward).normalized;
                            horizDir = launchHeading;
                        }
                        // Not on the coupled path this step (initial phase, kinematic ammo, or
                        // no launch heading): keep the carried attitude in step with where the
                        // round actually points, roll zero.
                        att = GameMath.QEuler(-pitchDeg, YawOf(horizDir), 0f);
                    }
                }
                float pitchRate = (pitchDeg - prevPitch) / dt;

                float thrust;
                if (_thrustFn != null) thrust = _thrustFn(ap, isAir, t, dt);
                else
                {
                    if (thrustArgs == null) thrustArgs = new object[4];
                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                    thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                }
#if AUTOTOT_OFFLINE
                thrust *= OfflineMotorScale;
#endif
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
                    // The altitude gate stands in for a window whose real terms are now measured
                    // and recorded in SolveInput.SeekerBrakeRangeU: the game feeds the round's own
                    // altitude only while CurrentTarget is null, between the seeker activating on
                    // FLAT range and detecting on SLANT range. Flying those terms instead was
                    // measured on 2026-09-13 and NOT adopted: with the descent onset corrected in
                    // the same change it moves plan_yj-20's short band from -4.63 to -0.16 s and its
                    // long band from +0.22 to +5.78 s, leaving the within-band spread at 2.8 to
                    // 3.3 s. A third, range-dependent term is missing and correcting these two
                    // without it only moves the bias around. See yj-20-motor-sensitivity.md.
                    bool inVacuumDive = phase == 2 && pos.y > ZeroDensityAltU &&
                                        pitchDeg < VacuumDivePitchThreshold;
                    float dragTargetAlt = inVacuumDive ? pos.y : predTgt.y;
                    if (_dragFn != null)
                        dragThisStep = _dragFn(pos.y, velKnots * KU, dt, -pitchDeg, dragFactor,
                                               motorBurning, dragTargetAlt, ap.LiftFactor,
                                               ap.MinVelocity, -pitchRate);
                    else
                    {
                        if (dragArgs == null) dragArgs = new object[10];
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

                if (velKnots < MinSpeedKn) { ModelStats.LoopDone(steps); return -1f; }

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
                    float rollDeg = Mathf.DeltaAngle(0f, GameMath.QEulerAngles(att).z);
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
            ModelStats.LoopDone(steps);
            return -1f;
        }
    }
}
