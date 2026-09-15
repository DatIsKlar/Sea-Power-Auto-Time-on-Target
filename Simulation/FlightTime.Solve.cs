using System;
﻿using UnityEngine;
using SeaPower;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        /// <summary>
        /// Everything the step loop needs, as plain values. The loop reads no Unity API and no mutable game
        /// state: every <c>transform</c> read, and the one <c>AmmunitionParameters</c> field the game writes
        /// at runtime (<c>_finalFlightPhaseAltUnity</c>, read in <c>ResolveStageProfile</c>), happens in
        /// setup on the main thread, so <see cref="Solve"/> is a pure function of its input and can run off
        /// the main thread. Fields the loop mutates are carried as their initial values and unpacked into locals.
        /// </summary>
        internal readonly struct SolveInput
        {
            internal Vector2[] AltNodes { get; init; }
            internal string AmmoLabel { get; init; }
            internal float BoostClimbDeg { get; init; }
            /// <summary>Deceleration in knots per second, a rate so the step size stays a parameter of the
            /// loop.</summary>
            internal float DecelRate { get; init; }
            /// <summary>Seeker range that opens and closes the drag brake window, in Unity units. Negative:
            /// no window can exist. Zero: not known (an initializer that omits it, or an older capture),
            /// which falls back to the altitude gate; the safe reading is the default one.</summary>
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
            /// <see cref="FlightTime.TargetMapVelocity(SeaPower.ObjectBase, out float)"/> rounding and the
            /// evasive boost: the velocity the round believes in, which the step loop leads and dead-reckons
            /// the switch point with (FlightTime.Lead.cs).</summary>
            internal Vector3 TargetVelReported { get; init; }
            /// <summary>What the distance-keyed stage and waypoint switches are measured to. The
            /// default, <see cref="SwitchTarget.ShipCentre"/>, is the older law.</summary>
            internal SwitchTarget StageSwitchTarget { get; init; }
            /// <summary>Slant range inside which a dead-reckoning round's seeker locks, once its
            /// terminal phase has started. 0 = it never locks.</summary>
            internal float SeekerLockRangeU { get; init; }
            /// <summary><c>_maxLoftAltUnity</c> at setup, for the lead's average speed. Captured rather
            /// than read in the loop: the game rewrites it at launch for two loft modes
            /// (Missile.cs:1792-1800).</summary>
            internal float LeadLoftAltU { get; init; }
            /// <summary><c>_finalFlightPhaseMaxAngle</c>, the descent angle the lead reads.</summary>
            internal float LeadFinalAngleDeg { get; init; }
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
            /// <summary>Seconds of flight before MaintainHeight will enter TerminalApproach: the
            /// ammunition's TerminalDelay, or 0.8 of the time to fly its minimum launch range
            /// (Missile.cs:487-491 with :52).</summary>
            internal float TerminalDelaySec { get; init; }
            /// <summary>The target's hull, which ends the flight where the round enters it; null (an air
            /// or submerged target) keeps the 3 u stop.</summary>
            internal HullBox Hull { get; init; }
            /// <summary>Why the hull has its size, for the capture: "identified", "size" and the contact's
            /// size class, "noise", "no track", "size unknown", or "none" without a hull.</summary>
            internal string HullSource { get; init; }
            internal bool TrackDiag { get; init; }
            /// <summary>The rate every ordinary turn is budgeted at: the ammunition's
            /// <c>MaxTurnRate</c>, plus the banking roll addend where that applies.</summary>
            internal float TurnRate { get; init; }
            /// <summary>Turn-rate floor for the ToBearing window only, the ammunition's <c>LaunchTurnRate</c>
            /// unclamped; 0 (the default, all but three shipped ammunition) applies no floor. See
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
        /// Per-round thrust multiplier for a replay. The game rolls one per missile (Missile.cs:62); the
        /// estimator cannot know it and assumes 1.0, but a replay scored against one recorded flight can set
        /// that flight's value. Offline only.
        /// </summary>
        internal static float OfflineMotorScale = 1f;

        /// <summary>Offline counterfactual for tools/lab: the game's motor clock is
        /// <c>GameTime.missionElapsedTime</c>, which advances once per rendered frame, so every tick of
        /// a frame reads the clock as the frame began. When set, a tick before the last entry reads the
        /// latest entry at or before it (recorded frame starts, seconds since launch); a later tick
        /// reads its own time less <see cref="OfflineThrustClockLagSec"/>, floored at zero.</summary>
        internal static float[] OfflineThrustFrameClock;
        internal static float OfflineThrustClockLagSec;

        internal static float OfflineThrustClock(float t)
        {
            float[] c = OfflineThrustFrameClock;
            if (c != null && c.Length > 0 && t < c[c.Length - 1] - 1e-4f)
            {
                float clock = 0f;
                for (int i = 0; i < c.Length && c[i] <= t + 1e-4f; i++) clock = c[i];
                return clock;
            }
            return Mathf.Max(t - OfflineThrustClockLagSec, 0f);
        }

        /// <summary>
        /// A round's recorded physics-step timeline, so a replay flies the steps the round flew instead of
        /// their average. The game adapts its step between 60 Hz and 30 Hz on frame rate; the outcome writer
        /// records every change as "t:step". Times are flight seconds since launch, ascending, each step
        /// holding until the next; the first entry, recorded a few tenths of a second after launch, is taken
        /// to hold from t = 0.
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

            /// <summary>Time-weighted mean step over a flight of the given length, for reporting.</summary>
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

        /// <summary>Set for one replay and cleared afterwards, like <see cref="OfflineMotorScale"/>; null
        /// flies a constant step.</summary>
        internal static OfflineStepSchedule OfflineSteps;

        /// <summary>Counterfactual Q: the arrival time is interpolated inside the step (the hull's slab entry
        /// fraction or the 3 u crossing) instead of taking the start of the first step inside.</summary>
        internal static bool OfflineArrivalInterp;

        /// <summary>Counterfactual R: each step moves by the velocity the game realises when it adds the
        /// increment to a float32 position once per physics tick (GameMath.TranslateRoundedVelocity at
        /// <see cref="OfflineGameStep"/>), accumulated in double so the model does not round on its own grid.</summary>
        internal static bool OfflineGameRounding;

        /// <summary>Counterfactual K: while a kinematic round's motor burns, thrust and drag are integrated in
        /// <see cref="OfflineGameStep"/> substeps inside each step, and the step moves at their mean speed.</summary>
        internal static bool OfflineMotorSubsteps;

        /// <summary>Offline counterfactual: substep only the step in which the motor burns out, where
        /// MissileSimulator.CalculateThrustOverTime returns remainingTime * timeWindow instead of
        /// remainingTime times the sustainer acceleration, so a 0.1 s window loses up to six times what the
        /// game's 1/60 s tick loses.</summary>
        internal static bool OfflineBurnoutSubsteps;


        /// <summary>The game physics step counterfactuals R and K use; the lab sets it per replay.</summary>
        internal static float OfflineGameStep = 1f / 60f;

        /// <summary>Flat distance from the ship's centre at which the last offline replay ended, -1 when
        /// it did not arrive.</summary>
        internal static float OfflineArrivalFlatU = -1f;

        /// <summary>The round's height above the ship's centre on the first step inside 3 u, where the
        /// older arrival test ends a flight; NaN when it never got there.</summary>
        internal static float OfflineHeightAtCloseU = float.NaN;

        /// <summary>Offline diagnostic for tools/lab `pathcmp`: when set, each step of the next replay
        /// appends its time, flat distance to the target's centre, height above it, speed and pitch.</summary>
        internal static System.Collections.Generic.List<(float T, float Flat, float Height, float Kn, float Pitch)> OfflinePath;

        /// <summary>Offline diagnostic for tools/lab `stepcmp --raw`: when set, each step of the next replay
        /// appends the flat offset to the predicted target at the step's start, the horizontal direction it
        /// flies, and the direction it wanted.</summary>
        internal static System.Collections.Generic.List<(float T, float Dx, float Dz, float DirX, float DirZ, float WantX, float WantZ)> OfflineGeom;

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
            phases.FinalFlightStartU = -1f;
            return Solve(in i, ap, ref phases, dt);
        }
#endif

        // Upstream corrections, on in the mod. Plain statics so the offline lab can turn each off for an A/B;
        // nothing in game writes them, so the worker threads only read them.

        /// <summary>A steering look-ahead that reaches a terminal pop-up leg takes that leg's own KappaLoft
        /// height and climb limit, as Waypoint.CalculateFuturePointRecursive does, instead of its end height.</summary>
        internal static bool PopUpLookAhead = true;

        /// <summary>A cruise missile's active pop-up leg holds its steering point to TerminalPopUpAngle below
        /// 0.95 progress: that waypoint does not allow exceeding its angle limits.</summary>
        internal static bool PopUpAngleClamp = true;

        /// <summary>TerminalVerticalTurnRate applies for the whole TerminalApproach stage, not only inside a
        /// pop-up leg.</summary>
        internal static bool TerminalTurnWholeStage = true;

        /// <summary>A kinematic loft round steers by the live KappaLoft curve (Waypoint.GetOrComputeLoftParams
        /// and BallisticHeightAt, started where the round is when its initial phase ends), unclamped,
        /// instead of the sampled altitude nodes.</summary>
        internal static bool LiveKappaLoft = true;

        /// <summary>Unity's Vector3.RotateTowards returns the current direction unchanged when its dot with
        /// the target direction exceeds 1 - 1e-5 (0.256 degrees), and WeaponBase.setCourseTowardsPosition
        /// turns a kinematic surface-target round with it, so the nose holds between steps of that size and
        /// the motor-off stall drag sees no pitch rate on the held ticks. Not in the terminal split, which
        /// turns angles with MoveTowards, and not for an air target, which the mover rotates by
        /// Quaternion.AngleAxis.</summary>
        internal static bool RotateDeadBand = true;

        /// <summary>A kinematic round's altitude legs all allow exceeding their angle limits, so the game
        /// holds the leg before the terminal one until the terminal distance and then flies the terminal
        /// leg's Linear line: the terminal phase starts at TermDist, and each step steers at the 1.5 s point
        /// on the line from where the round entered it to the terminal height.</summary>
        internal static bool KinematicTerminalLine = true;

        /// <summary>The flight time returned is a double sum of the steps taken. The loop's clock <c>t</c> is
        /// float32, and adding the same step rounds every sum the same way within a power of two (0.05 s
        /// short by 400 s at 1/60 s). Motion uses the step itself, so only the reported time carried the
        /// error; comparisons inside the loop keep <c>t</c>.</summary>
        internal static bool DoubleClock = true;

        /// <summary>The drag brake (CalculateDrag handed the round's own height) holds over the seeker window
        /// the game has: the missile's CurrentTarget is null from seeker activation, tested on a vector
        /// flattened to the missile's height, until detection, a 3-D range, both against SeekerActiveRange,
        /// and only for an ammunition without TargetMemory whose mid-course correction allows launcher
        /// guidance (SolveInput.SeekerBrakeRangeU, negative when it can never open). A capture without the
        /// range keeps the older altitude and pitch gate.</summary>
        internal static bool SeekerWindowBrake = true;

        /// <summary>A kinematic terminal-linear round whose 1.5 s look-ahead crosses the terminal boundary steers at
        /// the point that look-ahead reaches on the terminal Linear leg, from the boundary at the height its vertical
        /// speed projects there (Waypoint.CalculateFuturePointRecursive), so it starts down about 1.5 s before
        /// TermDist as the recorded rounds do.</summary>
        internal static bool KinematicLineLookAhead = true;

        internal static bool RotateHolds(bool nonKin, float targetAltU, bool terminalStage,
                                         float terminalVerticalRate, float stepTurnRate,
                                         Vector3 forward, Vector3 wanted) =>
            RotateDeadBand && !nonKin && Mathf.Abs(targetAltU) < 1f &&
            !(terminalStage && terminalVerticalRate > 0f && Mathf.Abs(terminalVerticalRate - stepTurnRate) > 1f) &&
            Vector3.Dot(forward, wanted) > 1f - 1e-5f;

        /// <summary>Waypoint.BallisticHeightAt, non-ballistic, term for term.</summary>
        private static float LoftCurve(float percent, float startHeight, float hPeak, float endHeight,
                                       float midpointPercent, float startSlopeNormalized)
        {
            float mid = Mathf.Max(midpointPercent, 1e-5f);
            if (percent < midpointPercent)
            {
                float u = percent / mid;
                float slope = MathF.Max(1f, startSlopeNormalized);
                return startHeight + (hPeak - startHeight) *
                       (u * u * (3f - 2f * u) + slope * u * (1f - u) * (1f - u));
            }
            float v = (percent - midpointPercent) / Mathf.Max(1f - midpointPercent, 1e-5f);
            float v2 = v * v, v3 = v2 * v;
            return (2f * v3 - 3f * v2 + 1f) * hPeak + (-2f * v3 + 3f * v2) * endHeight +
                   (v3 - v2) * 3f * (endHeight - hPeak);
        }

        /// <summary>
        /// A surface target's hull as a box centred on the ship and aligned to its heading, so a flight
        /// ends where the round enters the hull, which is where the game's collision cast
        /// (WeaponBase.CheckCollision) ends it, instead of 3 u from the ship's centre. Its size is what
        /// the shooter's side knows; see TargetHull. Immutable, so one instance can ride in a SolveInput
        /// to a worker thread.
        /// </summary>
        internal sealed class HullBox
        {
            internal readonly Vector3 Axis;
            internal readonly float HalfLengthU, HalfBeamU, HalfHeightU;

            internal HullBox(Vector3 axis, float lengthU, float beamU, float heightU)
            {
                Axis = axis;
                HalfLengthU = lengthU * 0.5f;
                HalfBeamU = beamU * 0.5f;
                HalfHeightU = heightU * 0.5f;
            }

            /// <summary>Whether the round's last step, from <paramref name="a"/> to <paramref name="b"/>
            /// (each its position minus the ship's centre at that step), passed through the hull. A swept
            /// test, because a hypersonic round covers more than the hull's height in one step and a
            /// point test lets it pass straight through.</summary>
            internal bool SegmentHits(Vector3 a, Vector3 b)
            {
                float t0 = 0f, t1 = 1f;
                return Slab(a.x * Axis.x + a.z * Axis.z, b.x * Axis.x + b.z * Axis.z, HalfLengthU, ref t0, ref t1) &&
                       Slab(a.x * Axis.z - a.z * Axis.x, b.x * Axis.z - b.z * Axis.x, HalfBeamU, ref t0, ref t1) &&
                       Slab(a.y, b.y, HalfHeightU, ref t0, ref t1);
            }
#if AUTOTOT_OFFLINE

            /// <summary>Where along the segment from <paramref name="a"/> to <paramref name="b"/> it enters the
            /// hull, 0 to 1; 1 when it does not.</summary>
            internal float SegmentEntry(Vector3 a, Vector3 b)
            {
                float t0 = 0f, t1 = 1f;
                bool hit = Slab(a.x * Axis.x + a.z * Axis.z, b.x * Axis.x + b.z * Axis.z, HalfLengthU, ref t0, ref t1) &&
                           Slab(a.x * Axis.z - a.z * Axis.x, b.x * Axis.z - b.z * Axis.x, HalfBeamU, ref t0, ref t1) &&
                           Slab(a.y, b.y, HalfHeightU, ref t0, ref t1);
                return hit ? t0 : 1f;
            }
#endif

            private static bool Slab(float p0, float p1, float half, ref float t0, ref float t1)
            {
                float d = p1 - p0;
                if (Mathf.Abs(d) < 1e-9f) return Mathf.Abs(p0) <= half;
                float ta = (-half - p0) / d, tb = (half - p0) / d;
                if (ta > tb) { float swap = ta; ta = tb; tb = swap; }
                t0 = Mathf.Max(t0, ta);
                t1 = Mathf.Min(t1, tb);
                return t0 <= t1;
            }
        }

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
        /// The integration loop, as a pure function of <paramref name="i"/>. <paramref name="ap"/> is passed
        /// because the game's thrust helpers take it, but every field reached through it is written once at
        /// ini load (LiftFactor and MinVelocity here; _acceleration, _accelerationTime,
        /// _sustainerBurnAcceleration, _sustainerBurnTime, _maxVelocityInKnots and Kinematics inside
        /// CalculateThrustOverTime), so concurrent reads are safe.
        /// </summary>
        private static float Solve(in SolveInput i, AmmunitionParameters ap,
                                   ref IntegratedPhases phases, float dt = IntegrationStepSim)
        {
            const float KU = GameUnits.KnotsToUnityPerSecond;
            // Argument arrays for the MethodInfo fallback, which runs only when the delegate fast path failed
            // to bind; allocated on first use.
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
            // What the stage and waypoint switches are measured to; see FlightTime.Lead.cs.
            SwitchTarget switchTarget          = i.StageSwitchTarget;
            Vector3     knownVel               = i.TargetVelReported;
            float       seekerLockRange        = i.SeekerLockRangeU;
            float       leadLoftAlt            = i.LeadLoftAltU;
            float       leadFinalAngle         = i.LeadFinalAngleDeg;
            // The game's _targetInterceptPosition starts at the launch aim point (Missile.cs:63) and
            // keeps its last value on a tick the lead has no solution.
            Vector3     intercept              = i.TargetPos;
            float       travelledFlat          = 0f;
            bool        terminalEntered        = false;
            bool        terminalLatched        = false;
            bool        seekerLocked           = false;
            bool        terminalLinear         = i.TerminalLinear;
            float       termAlt                = i.TermAlt;
            float       termDist               = i.TermDist;
            float       termVelKn              = i.TermVelKn;
            bool        terminalPopUpEnabled   = i.TerminalPopUpEnabled;
            float       terminalPopUpDist      = i.TerminalPopUpDist;
            float       terminalDelaySec       = i.TerminalDelaySec;
            HullBox     hull                   = i.Hull;
            float       seekerBrakeRangeU      = i.SeekerBrakeRangeU;
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
            // legStartFlat and legStartAlt are the game's _lastWaypointPosition, which follows the missile
            // until the initial phase ends (Missile.cs:2469-2480). Air-launched rounds fly the route too.
            // `nonKin` and `!isTerminalLoft` are defensive: setup, and the lab's rebuild, only make a route
            // for such a round.
            WaypointLeg[] legs                 = WaypointLegSteering && nonKin &&
                                                 !isTerminalLoft ? i.Legs : null;
            int         legIdx                 = 0;
            WaypointLeg legEff                 = legs != null ? legs[0] : default;
            float       legStartFlat           = -1f;
            float       legStartAlt            = i.LaunchAltU;
            float       legProgress            = 0f;
            bool        legCashedIn            = false;

            // Launched inside the loft/sea-skimming boundary, FastForwardWaypoints removes the leading Kappa
            // waypoint; the remaining kinematic altitude waypoint is Immediate with
            // AllowExceedingAngleLimits=true, so it aims straight at its fixed-height look-ahead point
            // instead of clamping to MaxLoftAngle (effectively a 90-degree climb for a VLS round far below
            // it). The finite turn rate still owns the nose-over and its overshoot.
            bool immediateAltitudeWaypoint = ImmediateAltitudeWaypointOwns(
                nonKin, lofting, loftEntryDist, flatDistTotal);
            float altitudeClimbCommandDeg = immediateAltitudeWaypoint
                ? BoostClimbDeg
                : boostClimbDeg;

            // The receding test below is a closest-approach guard: it ends the flight when the round stops
            // closing, which is how an overflight terminates. It must not fire before the round has
            // meaningfully closed. A fixed rail flies its own bearing first (an aircraft hardpoint points
            // wherever the nose does, a non-training canister can sit 90 degrees off the line of sight), so
            // the range can grow from the first step, and the target's own motion can shorten it for a step
            // or two. So the latch needs the range to fall below its start by more than such a wobble,
            // compared from the first real step (`havePrev`). Overflight clears the margin by kilometres; a
            // round that can never catch its target runs to maxFlight and another tier answers.
            bool hasClosed = false;
            float prevSlant = float.MaxValue;
            bool havePrev = false;
            float startFlat = -1f;
            float closeMargin = 0f;
            // Counted rather than derived from t/dt, which is only the step count while the step is
            // constant. See the offline step schedule below.
            int steps = 0;
            // The pop-up leg a look-ahead may reach, as WaypointPointAtTime takes it (height 0: none).
            float popUpLookHeight = PopUpLookAhead && terminalPopUpEnabled && terminalPopUpDist > 0f
                ? terminalPopUpHeight : 0f;
            // Where the live KappaLoft waypoint starts, fixed when the initial phase ends, and where the
            // kinematic terminal line starts, fixed when the round enters it.
            bool kappaStartSet = false;
            float kappaStartY = 0f, kappaStartFlat = 0f;
            float lineStartY = 0f, lineStartFlat = 0f;
            // The round's position relative to the ship's centre on the previous step, for the hull's swept test.
            Vector3 prevRel = Vector3.zero;
            bool havePrevRel = false;
            double clock = t;
#if AUTOTOT_OFFLINE
            double offlinePosX = pos.x, offlinePosY = pos.y, offlinePosZ = pos.z;
            OfflineStepSchedule sched = OfflineSteps;
            int schedIdx = 0;
#endif

            while (t < maxFlight)
            {
#if AUTOTOT_OFFLINE
                // Fly the steps this round actually flew. Guarded, since the shipped mod always integrates at
                // a constant step and must not pay for the test.
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
#if AUTOTOT_OFFLINE
                OfflinePath?.Add((t, flatDist, pos.y - predTgt.y, velKnots, prevPitch));
#endif

                if (startFlat < 0f)
                {
                    startFlat = flatDist;
                    // Proportional, with an absolute floor: a 20 km shot is gated on the floor and a
                    // 700 km one on the fraction, so neither a short shot nor a long one can arm the
                    // latch on noise. The floor is the same distance the arrival test uses.
                    closeMargin = Mathf.Max(CloseEnoughDistU, startFlat * RecedeCloseFraction);
                }

                // Flat range alone is the wrong closest-approach test for a diving round: a terminal waypoint
                // with AllowExceedingAngleLimits left true and PitchMode Immediate commands a round straight
                // down, and the game flies it with x and z frozen while a target under way pushes flat range
                // back up. Requiring slant range to recede too cannot strand the solve: the guard waits only
                // while the round trades altitude, and once it levels out slant recedes exactly when flat does.
                float slantDy = pos.y - predTgt.y;
                float slantDist = Mathf.Sqrt(flatDist * flatDist + slantDy * slantDy);
                // Arrival: the step that passes through the target's hull, or 3 u from the centre of a
                // target without one.
                Vector3 rel = pos - predTgt;
                bool closeEnough = hull != null
                    ? hull.SegmentHits(havePrevRel ? prevRel : rel, rel)
                    : flatDist < CloseEnoughDistU;
#if AUTOTOT_OFFLINE
                float offlineEntryBack = 0f;
                if (OfflineArrivalInterp && closeEnough && havePrevRel)
                {
                    float entry = hull != null ? hull.SegmentEntry(prevRel, rel)
                        : prevFlat > flatDist && prevFlat < float.MaxValue
                            ? Mathf.Clamp01((prevFlat - CloseEnoughDistU) / (prevFlat - flatDist)) : 1f;
                    offlineEntryBack = (1f - entry) * dt;
                }
#endif
                prevRel = rel;
                havePrevRel = true;
#if AUTOTOT_OFFLINE
                if (float.IsNaN(OfflineHeightAtCloseU) && flatDist < CloseEnoughDistU)
                    OfflineHeightAtCloseU = rel.y;
#endif
                if ((flatDist > prevFlat && slantDist > prevSlant && t > dt && hasClosed) || closeEnough)
                {
#if AUTOTOT_OFFLINE
                    OfflineArrivalFlatU = flatDist;
#endif
                    ModelStats.LoopDone(steps);
                    if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
#if AUTOTOT_OFFLINE
                    return DoubleClock ? (float)(clock - offlineEntryBack) : t - offlineEntryBack;
#else
                    return DoubleClock ? (float)clock : t;
#endif
                }
                if (havePrev && flatDist < prevFlat && flatDist <= startFlat - closeMargin)
                    hasClosed = true;
                prevFlat = flatDist;
                prevSlant = slantDist;
                havePrev = true;

                // The distance the game's switches read: flat range to its intercept point, the known
                // target led by the velocity the target reports (Missile.cs:374-376, :2427-2468). The
                // arrival test above, steering, the ToBearing cone and drag keep the predicted ship.
                float switchFlat = flatDist;
                if (switchTarget != SwitchTarget.ShipCentre)
                {
                    // A dead-reckoning round's seeker is switched on by the terminal branch and locks
                    // on its first echo; from then on the round knows where the ship really is.
                    if (switchTarget == SwitchTarget.DeadReckoned && !seekerLocked && terminalEntered &&
                        seekerLockRange > 0f && slantDist <= seekerLockRange)
                        seekerLocked = true;
                    // Dead reckoning restarts from the true position at ToBearing entry, where the game
                    // resets _lostGuidanceTime (Missile.cs:252).
                    Vector3 known = switchTarget == SwitchTarget.DeadReckoned && !seekerLocked
                        ? targetPos + targetVel * Mathf.Min(t, initialPhaseDur) +
                          knownVel * Mathf.Max(t - initialPhaseDur, 0f)
                        : predTgt;
                    if (knownVel.sqrMagnitude >= UnityVectorEqualitySqr)
                    {
                        float leadTime = LeadInterceptTime(pos, velKnots, travelledFlat, known, knownVel,
                            maxVelKn, leadLoftAlt, leadFinalAngle, prevPitch, dragFactor,
                            ap.LiftFactor, ap.MinVelocity);
                        if (leadTime > 0f) intercept = known + knownVel * leadTime;
                    }
                    else intercept = known;
                    float leadDx = intercept.x - pos.x, leadDz = intercept.z - pos.z;
                    switchFlat = Mathf.Sqrt(leadDx * leadDx + leadDz * leadDz);
                    // TerminalApproach is a state the game never leaves (MaintainHeight.takeAction
                    // changes state into it), not a distance. The lock above can move the known
                    // position back past the threshold, 2.3 u on a ship that realises less than its
                    // reported speed, and a distance test alone would read that as leaving the
                    // terminal phase and fly the gap at cruise speed.
                    if (termDist > 0f && switchFlat <= termDist) terminalLatched = true;
                }

                Vector3 horizDir = flatDist > 1e-4f
                    ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;
                Vector3 horizDirTarget = horizDir;   // where the round wants to point
                // Pitch, yaw and roll share one rotation budget, so independent axes would spend it twice: 90 deg of
                // pitch with 90 deg of heading error is 120 deg of quaternion travel, and an on-bearing shot reduces
                // to MoveTowards. The attitude persists across steps, or the budget spent on roll would be refunded
                // every step. Not gated on `nonKin`: Legacy takes one Quaternion.RotateTowards over the whole rotation
                // (setCourseTowardsPositionLegacy); Full takes one Vector3.RotateTowards covering yaw and pitch for a
                // surface target with the default TerminalVerticalTurnRate (the else of the |terminalRate - rate| > 1
                // split) or one 3-D cone for an air target, levelling roll separately. Only the legacy path folds
                // roll into the same quaternion (the banking addend in setup).
                bool coupledTurn = launchHeading.sqrMagnitude > 0.5f && t >= initialPhaseDur;

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
                if (turnDerateThresholdKn > 0f && velKnots > turnDerateThresholdKn)
                    stepTurnRate *= turnDerateThresholdKn / velKnots;
                bool toBearingOverride = inToBearing && toBearingTurnRate > stepTurnRate;
                if (toBearingOverride)
                    stepTurnRate = toBearingTurnRate;
                // The roll addend reaches pitch only where the mover spends one combined rotation. With the
                // override in force the rate differs from MaxTurnRate, so the legacy mover takes its
                // three-rotation branch and pitch turns at the override rate (plan_yj-18a pitches over at its
                // LaunchTurnRate, 50 deg/s).
                if (!(ToBearingOverrideOwnsPitch && toBearingOverride))
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
                        flatDistTotal, switchFlat);
                    float descentGeomDist = (pos.y - termAlt)
                                          / Mathf.Tan(Mathf.Max(descentOnsetDeg, MinDescentOnsetDeg) * Mathf.Deg2Rad);
                    // A kinematic round's legs allow exceeding their angle limits, so it holds its height
                    // to the terminal distance instead of starting down at a geometric onset.
                    float diveStart = KinematicTerminalLine && terminalLinear
                        ? termDist : Mathf.Max(termDist, descentGeomDist);
                    if (!terminalKappaActive && diveStart > 0f &&
                        (switchFlat <= diveStart || terminalLatched))
                    { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                    else if (!terminalKappaActive && finalDist > 0f && switchFlat <= finalDist)
                    {
                        phase = 1;
                        stageAlt = CruiseOrFinalFlightAlt(switchFlat, finalFlightDist,
                                                          finalFlightAlt, cruiseAlt);
                        // The altitude schedule ends here, the speed may not. An ammunition that
                        // requires a target to proceed stays in MaintainLoftAlt, and so at loft
                        // speed, until its seeker activates, which is further in than this
                        // boundary. It still descends on cue, which is why only stageTgt is held.
                        stageTgt = (loftSpeedHoldDist > 0f && switchFlat > loftSpeedHoldDist)
                                 ? loftVelKn : maxVelKn;
                    }
                    // A round enters the loft only if launched outside the sea-skimming boundary. Coming out
                    // of ToBearing the stage is not MaintainLoftAlt, so the `_loftToSkim || stage !=
                    // MaintainLoftAlt` clause at Missile.cs:641 is true for every ammunition and the
                    // sea-skimming branch wins; `num` is then 2 and the loft branch (`num <= 1`) is
                    // unreachable for the rest of the flight. `lofting` alone is a property of the ammunition
                    // and says the round can loft, not that it will. Tested on the launch range, since entry
                    // is decided once, at ToBearing.
                    else if (loftPath)
                    {
                        stageTgt = loftVelKn;
                        // The live kinematic path flies a KappaLoft waypoint whose ceiling TuneWaypoints
                        // already replaced; setup stores the game's sampled Kappa curve in altNodes.
                        // LoftProfileAltU remains for older captures and where no sample exists (a leg too
                        // short to sample, or a failed bind or sample, which setup warns about). Cruise
                        // missiles hold the configured ceiling.
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
                        // A kinematic Kappa waypoint whose tuned loft height is at the launch floor stays on
                        // the target-level path. The ammunition's defaulted cruise ceiling must not be reused
                        // here: it can alias MaxLoftAlt. Explicit final and sea-skimming stages were handled
                        // above and keep their altitude.
                        stageAlt = ResolveUnstagedAltitude(
                            nonKin, lofting, targetAlt0, switchFlat, finalFlightDist,
                            finalFlightAlt, cruiseAlt);
                        stageTgt = maxVelKn;
                    }

                    float altErr = stageAlt - pos.y;
                    float targetPitch = 0f;
                    float diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg;

                    // The game's ToBearing test: the angle between the missile's attitude and the line to its
                    // aim point, against a 5 deg cone with a 10.0 s cap; inToBearing is resolved at the top
                    // of the step, since its turn rate reaches the heading slew too. Launch and ToBearing
                    // both command _maxVelocityInKnots (Missile.cs:3142).
                    if (t < initialPhaseDur || inToBearing)
                        stageTgt = maxVelKn;

                    // Bang-bang altitude control while transiting, proportional once holding, latched on first
                    // arrival. The real round holds full climb and then noses over at _maxTurnRateDegrees, so the
                    // overshoot is the turn rate, not a defect. Arrival is the altitude error changing sign: a band
                    // test cannot work, since cruise moves 3-5.5 u per 0.1 s step against a 1 u AltToleranceU.
                    bool holdingAlt = false;
                    if (phase != altLatchPhase)
                    { altLatchPhase = phase; altLatched = false; prevAltErr = float.NaN; }
                    if (!altLatched && !float.IsNaN(prevAltErr) &&
                        ((prevAltErr > 0f) != (altErr > 0f)))
                        altLatched = true;
                    prevAltErr = altErr;
                    holdingAlt = altLatched;

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

                    // Missile.SetMissileCourse's pre-apex guard, approximated with unstretched look-ahead
                    // points, for rounds without a waypoint route (an air target, or a capture older than
                    // rich-flight-v3); the route block below runs the exact guard on the real points.
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
                        // Legs complete against the same intercept point as the stages
                        // (Missile.cs:2475), and the look-ahead is measured along the line to it.
                        float legFlat = switchFlat;
                        if (t < initialPhaseDur || legStartFlat < 0f)
                        {
                            // Until guidance starts the game resets _lastWaypointPosition to the
                            // missile every tick, so the first leg is measured from here.
                            legStartFlat = legFlat;
                            legStartAlt = pos.y;
                        }
                        else
                        {
                            // A group leader cashes its group in on entering TerminalApproach and rebuilds
                            // its route (Missile.cs:858-862, MissileGroup.cs:199-201): CreateWaypoints builds
                            // every leg from the launch point and ends with FastForwardWaypoints
                            // (Missile.cs:2769-2835), dropping each leg the round is already inside. So only
                            // the final leg survives, started from the round's position, unless another leg's
                            // boundary lies inside the entry distance (no groupable ammunition declares a
                            // terminal pop-up). The entry distance is TermDist, the seeker search past the
                            // terminal boundary.
                            if (i.GroupLeader && !legCashedIn && legFlat <= termDist &&
                                legIdx < legs.Length - 1)
                            {
                                legCashedIn = true;
                                legIdx = legs.Length - 1;
                                legEff = legs[legIdx];
                                legStartFlat = legFlat;
                                legStartAlt = pos.y;
                                legProgress = 0f;
                            }
                            // Waypoint.UpdateAndGetActiveWaypoint: complete every leg passed.
                            while (legIdx < legs.Length - 1 && legFlat <= legEff.EndDist)
                            {
                                bool copy = legProgress > LegSkipCopyProgress && legEff.SkipNext;
                                WaypointLeg done = legEff;
                                legIdx++;
                                legEff = copy ? legs[legIdx].WithSettingsOf(in done) : legs[legIdx];
                                legStartFlat = legFlat;
                                legStartAlt = pos.y;
                                legProgress = 0f;
                            }
                            // Waypoint.UpdateWaypoint's progress: geometric, caught up by at most
                            // 2.5x this tick's flat motion (Waypoint.cs:372-385).
                            float travelled = Mathf.Max(legStartFlat - legFlat, 0f);
                            float legTotal = Mathf.Max(travelled +
                                Mathf.Max(legFlat - legEff.EndDist, 0f), WaypointDistanceEpsilonU);
                            legProgress = Mathf.Max(legProgress, Mathf.Min(travelled / legTotal,
                                legProgress + WaypointProgressCatchupMultiplier * legFlatSpeed * dt / legTotal));

                            // A Kappa leg is the terminal popup, flown by its own block below.
                            if (legEff.Mode != WaypointLegMode.Kappa)
                            {
                                float legVerticalSpeed = velKnots * KU * Mathf.Sin(prevPitch * Mathf.Deg2Rad);
                                Vector2 desired = WaypointPointAtTime(legs, legIdx, in legEff,
                                    legProgress, legStartAlt, WaypointSteeringLookaheadSim, legFlat,
                                    pos.y, legFlatSpeed, legVerticalSpeed,
                                    targetAlt0, popUpLookHeight, terminalPopUpAngle);
                                Vector2 future = WaypointPointAtTime(legs, legIdx, in legEff,
                                    legProgress, legStartAlt, WaypointFutureLookaheadSim, legFlat,
                                    pos.y, legFlatSpeed, legVerticalSpeed,
                                    targetAlt0, popUpLookHeight, terminalPopUpAngle);
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
                                    Mathf.Max(legFlat - desired.x, WaypointDistanceEpsilonU)) *
                                    Mathf.Rad2Deg;
                            }
                        }
                    }

                    if (KinematicLineLookAhead && KinematicTerminalLine && terminalLinear && !nonKin && phase == 1 &&
                        !terminalKappaActive && termDist > 0f && switchFlat > termDist)
                    {
                        // CalculateFuturePointRecursive: the time to the boundary at the flat speed; past it, the next
                        // leg from the boundary at the height the round's vertical speed projects there.
                        float ylFlatV = velKnots * KU * Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad));
                        float ylRemaining = switchFlat - termDist;
                        float ylTimeToEnd = ylFlatV > 1e-3f ? ylRemaining / ylFlatV : float.MaxValue;
                        if (WaypointSteeringLookaheadSim > ylTimeToEnd)
                        {
                            float ylVy = velKnots * KU * Mathf.Sin(prevPitch * Mathf.Deg2Rad);
                            float ylBoundaryY = pos.y + ylTimeToEnd * ylVy;
                            float ylRun = Mathf.Min(ylFlatV * (WaypointSteeringLookaheadSim - ylTimeToEnd), termDist);
                            float ylAlt = ylBoundaryY + ylRun / Mathf.Max(termDist, WaypointDistanceEpsilonU) *
                                          (termAlt - ylBoundaryY);
                            targetPitch = Mathf.Atan2(ylAlt - pos.y,
                                Mathf.Max(ylRemaining + ylRun, WaypointDistanceEpsilonU)) * Mathf.Rad2Deg;
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
                            lineStartY = pos.y;
                            lineStartFlat = Mathf.Max(flatDist, WaypointDistanceEpsilonU);
                        }
                        if (KinematicTerminalLine)
                        {
                            // Waypoint.CalculatePointAtTime on a Linear leg: the point 1.5 s ahead, at
                            // the height the line from the leg's start to its end has there.
                            float lineLook = Mathf.Clamp(velKnots * KU * Mathf.Abs(Mathf.Cos(prevPitch * Mathf.Deg2Rad)) *
                                                         WaypointSteeringLookaheadSim,
                                                         WaypointDistanceEpsilonU, Mathf.Max(flatDist, WaypointDistanceEpsilonU));
                            float lineProgress = Mathf.Clamp01(1f - (flatDist - lineLook) / lineStartFlat);
                            float lineAlt = lineStartY + lineProgress * (termAlt - lineStartY);
                            targetPitch = Mathf.Atan2(lineAlt - pos.y, lineLook) * Mathf.Rad2Deg;
                        }
                        else
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
                        if (LiveKappaLoft)
                        {
                            // The waypoint's start point is where the missile is when its initial phase ends
                            // (_lastWaypointPosition follows it until then); its end point lies on the line to
                            // the target, the leg's distance short of it, at the target's height.
                            if (!kappaStartSet && t >= initialPhaseDur)
                            {
                                kappaStartSet = true;
                                kappaStartY = pos.y;
                                kappaStartFlat = flatDist;
                            }
                            float kStartY = kappaStartSet ? kappaStartY : pos.y;
                            float kStartFlat = kappaStartSet ? kappaStartFlat : flatDist;
                            float kBoundary = kappaEndFlat > 0f ? Mathf.Max(flatDistTotal - kappaEndFlat, 0f) : 0f;
                            float kMagnitude = Mathf.Max(kStartFlat - kBoundary, 1f);
                            float kEndY = targetAlt0;
                            float invC = 1f / Mathf.Max(loftClimbTan, 1e-6f), invD = 1f / Mathf.Max(loftDescentTan, 1e-6f);
                            float kGeo = (kMagnitude + kStartY * invC + kEndY * invD) / (invC + invD);
                            float kPeak = Mathf.Max(Mathf.Max(Mathf.Min(loftAlt, kGeo), kStartY), kEndY);
                            float kClimbRun = (kPeak - kStartY) * invC, kDescentRun = (kPeak - kEndY) * invD;
                            float kMidA = MathF.Min(0.95f, kClimbRun / kMagnitude);
                            float kMidB = MathF.Min(0.95f, kClimbRun / MathF.Max(0.1f, kClimbRun + kDescentRun));
                            float kSpan = kEndY - kStartY;
                            float kMid = loftAlt > kEndY ? kMidA
                                : Mathf.Lerp(kMidB, kMidA, kSpan > 0.001f ? Mathf.Clamp01((loftAlt - kStartY) / kSpan) : 1f);
                            float kClimbDeg = Mathf.Atan(1f / invC) * Mathf.Rad2Deg;
                            float kStartAngle = Mathf.Max(0f, Mathf.Min(launchPitch, kClimbDeg));
                            float kSlope = kStartAngle > 0.001f && kClimbDeg > 0.001f
                                ? Mathf.Clamp01(MathF.Tan(kStartAngle * Mathf.Deg2Rad) /
                                                MathF.Tan(Mathf.Clamp(kClimbDeg, 0.001f, 89f) * Mathf.Deg2Rad))
                                : 0f;
                            float kLead = flatSpeed * WaypointSteeringLookaheadSim;
                            if (kLead > flatDist) kLead = flatDist;
                            float kPct = Mathf.Clamp01(kappaProgress);
                            float kProg = Mathf.Min(kPct + (1f - kPct) * kLead / Mathf.Max(flatDist, WaypointDistanceEpsilonU), 1f);
                            float kD = kProg - kPct;
                            float kH0 = LoftCurve(kPct, kStartY, kPeak, kEndY, kMid, kSlope);
                            float kRun = Mathf.Max(kD * kMagnitude, 0.001f);
                            float kChord = kH0 + kRun * Mathf.Tan(Mathf.Atan2(
                                LoftCurve(kPct + kD, kStartY, kPeak, kEndY, kMid, kSlope) - kH0, kD * kMagnitude));
                            float kFloorRun = Mathf.Clamp(flatSpeed * WaypointFutureLookaheadSim, kRun,
                                                          Mathf.Max((1f - kPct) * kMagnitude, 0.001f));
                            float kFloorH = LoftCurve(Mathf.Min(kPct + kFloorRun / kMagnitude, 1f),
                                                             kStartY, kPeak, kEndY, kMid, kSlope);
                            float kFloorAngle = Mathf.Min(Mathf.Atan2(pos.y - kFloorH, kFloorRun), MathF.PI * 89f / 180f);
                            float kCommand = Mathf.Max(kChord, pos.y - kRun * Mathf.Tan(kFloorAngle));
                            targetPitch = Mathf.Clamp(Mathf.Atan2(kCommand - pos.y,
                                Mathf.Max(kLead, WaypointDistanceEpsilonU)) * Mathf.Rad2Deg, -89f, 89f);
                        }
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
                        switchFlat <= terminalPopUpDist;
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
                        // A cruise missile's pop-up waypoint does not allow exceeding its angle limits, so
                        // the steering point is held to TerminalPopUpAngle until 0.95 progress.
                        float popUpLimit = PopUpAngleClamp && nonKin &&
                                           terminalPopUpProgress < WaypointClampProgressLimit
                            ? Mathf.Clamp(terminalPopUpAngle, 0.01f, 89f) : 89f;
                        targetPitch = Mathf.Clamp(Mathf.Atan2(
                            Mathf.Max(tangentAlt, floorAlt) - pos.y, look) * Mathf.Rad2Deg,
                            -popUpLimit, popUpLimit);
                    }

                    if (launchPitch >= 0f && t < initialPhaseDur)
                    {
                        // Missile.Launch rotates a nose-high round down at a fixed 6 deg/s when this
                        // ammunition flag is set: a controller law, not an empirical offset (a 3 s launch
                        // leaves RGM-109B at 72 degrees).
                        targetPitch = slightBallisticLaunch
                            ? Mathf.Max(10f, launchPitch - 6f * (t + dt))
                            : launchPitch;
                    }

                    // Both movers take the pitch rate from TerminalVerticalTurnRate for the whole
                    // TerminalApproach stage, not only inside a pop-up leg. MaintainHeight.takeAction
                    // refuses TerminalApproach until the flight is older than the terminal delay. The
                    // legacy mover tests the terminal rate against the raw MaxTurnRate, the kinematic mover
                    // against the derated rate.
                    bool terminalStage = termDist > 0f && t >= terminalDelaySec &&
                                         (switchFlat <= termDist || terminalLatched);
                    bool splitTerminalTurn = TerminalTurnWholeStage
                        ? terminalStage && terminalVerticalRate > 0f &&
                          Mathf.Abs(terminalVerticalRate - (nonKin ? turnRateBase : stepTurnRate)) > 1f
                        : terminalPopUpActiveThisStep && terminalVerticalRate > 0f &&
                          Mathf.Abs(terminalVerticalRate - stepTurnRate) > 1f;
                    // The ballistic launch rotation acts directly on pitch. When ordinary guidance resumes,
                    // SetMissileCourse restores that pitch at MaxTurnRate, and the separate banking/roll
                    // budget must not snap the nose back to the climb command. The game reads the flag only
                    // in Launch and GoToSurface (Missile.cs:163, :246), so this ends the first time the nose
                    // reaches its command; left open, it would re-arm on every later pull-up.
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
                        if (!RotateHolds(nonKin, targetPos.y, terminalStage, terminalVerticalRate,
                                         stepTurnRate, att * Vector3.forward, tgt * Vector3.forward))
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
                        pitchDeg = !splitTerminalTurn && !splitLaunchRecovery && t >= initialPhaseDur &&
                                   RotateHolds(nonKin, targetPos.y, terminalStage,
                                       terminalVerticalRate, stepTurnRate,
                                       GameMath.QEuler(-prevPitch, YawOf(horizDir), 0f) * Vector3.forward,
                                       GameMath.QEuler(-targetPitch, YawOf(horizDirTarget), 0f) * Vector3.forward)
                            ? prevPitch
                            : Mathf.MoveTowards(prevPitch, targetPitch, pitchTurnRate * dt);
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
#if AUTOTOT_OFFLINE
                float thrustClock = OfflineThrustClock(t);
                if (_thrustFn != null) thrust = _thrustFn(ap, isAir, thrustClock, dt);
                else
                {
                    if (thrustArgs == null) thrustArgs = new object[4];
                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = thrustClock; thrustArgs[3] = dt;
                    thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                }
#else
                if (_thrustFn != null) thrust = _thrustFn(ap, isAir, t, dt);
                else
                {
                    if (thrustArgs == null) thrustArgs = new object[4];
                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                    thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                }
#endif
#if AUTOTOT_OFFLINE
                thrust *= OfflineMotorScale;
#endif
                bool motorBurning = thrust > 0f;

                float dragThisStep = 0f;
#if AUTOTOT_OFFLINE
                float offlineStepKn = -1f;
#endif
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
                    // The brake holds over the seeker window (SeekerWindowBrake): flat range inside the
                    // seeker range, slant range still outside it.
                    bool inVacuumDive = SeekerWindowBrake && seekerBrakeRangeU != 0f
                        ? seekerBrakeRangeU > 0f && flatDist < seekerBrakeRangeU && slantDist > seekerBrakeRangeU
                        : phase == 2 && pos.y > ZeroDensityAltU && pitchDeg < VacuumDivePitchThreshold;
                    float dragTargetAlt = inVacuumDive ? pos.y : predTgt.y;
#if AUTOTOT_OFFLINE
                    // M4: the step in which the motor burns out, found with the game's own thrust function,
                    // which returns exactly 0 once both burns are over (a partial-kinematics sustain phase
                    // returns a negative value).
                    bool offlineBurnoutStep = OfflineBurnoutSubsteps && motorBurning && _thrustFn != null &&
                                              OfflineGameStep > 0f &&
                                              _thrustFn(ap, isAir, OfflineThrustClock(t + dt), OfflineGameStep) == 0f;
                    int offlineSub = (OfflineMotorSubsteps && motorBurning || offlineBurnoutStep) &&
                                     _dragFn != null && _thrustFn != null && OfflineGameStep > 0f
                        ? Mathf.Max(1, Mathf.RoundToInt(dt / OfflineGameStep)) : 1;
                    if (offlineSub > 1)
                    {
                        velKnots -= thrust;
                        float sdt = dt / offlineSub, sumKn = 0f;
                        for (int k = 0; k < offlineSub; k++)
                        {
                            float subThrust = _thrustFn(ap, isAir, OfflineThrustClock(t + k * sdt), sdt) * OfflineMotorScale;
                            velKnots += subThrust;
                            velKnots -= _dragFn(pos.y, velKnots * KU, sdt, -pitchDeg, dragFactor, subThrust > 0f,
                                                dragTargetAlt, ap.LiftFactor, ap.MinVelocity, -pitchRate);
                            sumKn += velKnots;
                        }
                        offlineStepKn = sumKn / offlineSub;
                    }
                    else if (_dragFn != null)
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
#else
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
#endif
                    velKnots -= dragThisStep;
                    // No stage-speed clamp here: kinematic ammo is thrust minus drag, uncapped.
                    // The live mover adds thrust unconditionally for Kinematics != None
                    // (Missile.cs:3151); the stage target is consumed only by the other branch.
                }

                if (velKnots < MinSpeedKn) { ModelStats.LoopDone(steps); return -1f; }

                float pr = pitchDeg * Mathf.Deg2Rad;
                Vector3 dir = horizDir * Mathf.Cos(pr) + Vector3.up * Mathf.Sin(pr);
#if AUTOTOT_OFFLINE
                OfflineGeom?.Add((t, dx, dz, horizDir.x, horizDir.z, horizDirTarget.x, horizDirTarget.z));
                float offlineMoveKn = offlineStepKn >= 0f ? offlineStepKn : velKnots;
                if (OfflineGameRounding && OfflineGameStep > 0f)
                {
                    Vector3 realised = GameMath.TranslateRoundedVelocity(pos, offlineMoveKn * KU * dir, OfflineGameStep);
                    offlinePosX += (double)realised.x * dt;
                    offlinePosY += (double)realised.y * dt;
                    offlinePosZ += (double)realised.z * dt;
                    pos = new Vector3((float)offlinePosX, (float)offlinePosY, (float)offlinePosZ);
                }
                else
                    pos += offlineMoveKn * KU * dt * dir;
#else
                pos += velKnots * KU * dt * dir;
#endif
                // _traveledDistance: the flat part of each tick's motion (Missile.cs:2114-2118).
                travelledFlat += velKnots * KU * dt * Mathf.Abs(Mathf.Cos(pr));

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
                if (phase == 2) terminalEntered = true;
                if (phases.FinalFlightStartU < 0f && finalFlightDist > 0f && switchFlat <= finalFlightDist)
                    phases.FinalFlightStartU = flatDist;
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
                        $"dist {flatDist * GameUnits.MetersPerUnity / 1000f:0.0}km slant {slantKm:0.0}km" +
                        (switchTarget != SwitchTarget.ShipCentre
                            ? $" switchOff {(switchFlat - flatDist) * GameUnits.MetersPerUnity:0}m" : ""));
                    nextSample += (t < NoseOverWindowSim) ? NoseOverIntervalSim
                                : (t < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
                                : TelemetrySampleIntervalSim;
                }

                prevPitch = pitchDeg;
                t += dt;
                clock += dt;
            }
            ModelStats.LoopDone(steps);
            return -1f;
        }
    }
}
