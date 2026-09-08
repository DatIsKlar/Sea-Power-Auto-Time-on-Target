using UnityEngine;
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
            internal readonly Vector2[]   AltNodes;
            internal readonly string      AmmoLabel;
            internal readonly float       BoostClimbDeg;
            internal readonly float       DecelPerStep;
            internal readonly float       DescentDeg;
            internal readonly float       DescentOnsetDeg;
            internal readonly float       DragFactor;
            /// <summary>Altitude of the outer cruise leg, outside <see cref="FinalFlightDist"/>.</summary>
            internal readonly float       CruiseAlt;
            /// <summary>Where the final-flight leg begins, and the altitude it commands. 0 dist = no
            /// such leg, and the switch is inert. See FlightTime.Integrator.ResolveStageProfile.</summary>
            internal readonly float       FinalFlightAlt;
            internal readonly float       FinalFlightDist;
            internal readonly float       FinalDist;
            internal readonly float       FlatDistTotal;
            internal readonly float       InitialPhaseDur;
            internal readonly bool        IsAir;
            internal readonly bool        IsHighBallisticLofter;
            internal readonly bool        IsTerminalLoft;
            internal readonly float       LaunchPitch;
            internal readonly float       LoftAlt;
            /// <summary>Launch range outside which the round enters the loft at all. 0 = no
            /// sea-skimming boundary, so it always lofts.</summary>
            internal readonly float       LoftEntryDist;
            /// <summary>Flat distance inside which loft speed ends, when the ordinary stage boundary
            /// does not apply. 0 = no hold. See FlightTime.Integrator.ResolveLoftSpeedHoldDist.</summary>
            internal readonly float       LoftSpeedHoldDist;
            internal readonly float       LoftVelKn;
            internal readonly bool        Lofting;
            internal readonly float       MaxFlight;
            internal readonly float       MaxVelKn;
            internal readonly bool        NonKin;
            internal readonly float       TargetAlt0;
            internal readonly Vector3     TargetPos;
            internal readonly Vector3     TargetVel;
            internal readonly float       TermAlt;
            internal readonly float       TermDist;
            internal readonly float       TermVelKn;
            internal readonly bool        TrackDiag;
            /// <summary>The rate every ordinary turn is budgeted at: the ammunition's
            /// <c>MaxTurnRate</c>, plus the banking roll addend where that applies.</summary>
            internal readonly float       TurnRate;
            /// <summary>Turn-rate FLOOR for the ToBearing window only, from the ammunition's
            /// <c>LaunchTurnRate</c>, unclamped. 0 when the ini leaves it at the default, which is
            /// all but three shipped ammunition, and 0 means the step loop applies no floor. See
            /// FlightTime.Integrator.ResolveToBearingTurnRate.</summary>
            internal readonly float       ToBearingTurnRate;
            /// <summary>The <c>MaxTurnRate</c> part of <see cref="TurnRate"/>, without the banking
            /// roll addend. The G-derate applies to this part alone, because the mover derates
            /// before it spends the separate hardcoded roll budget.</summary>
            internal readonly float       TurnRateBase;
            /// <summary>Speed above which the G-limit starts cutting the turn rate, precomputed so
            /// the step loop pays one compare. 0 = the ammunition can never reach its own limit, so
            /// the derate is off. See FlightTime.Integrator.ResolveTurnDerateThreshold.</summary>
            internal readonly float       TurnDerateThresholdKn;
            internal readonly int         AltLatchPhase;
            internal readonly bool        AltLatched;
            internal readonly Quaternion  Att;
            internal readonly Vector3     LaunchHeading;
            internal readonly float       NextSample;
            internal readonly Vector3     Pos;
            internal readonly float       PrevAltErr;
            internal readonly float       PrevFlat;
            internal readonly float       PrevPitch;
            internal readonly float       T;
            internal readonly bool        TlGliding;
            internal readonly float       VelKnots;

            internal SolveInput(
                Vector2[] altNodes,
                string ammoLabel,
                float boostClimbDeg,
                float decelPerStep,
                float descentDeg,
                float descentOnsetDeg,
                float dragFactor,
                float cruiseAlt,
                float finalFlightAlt,
                float finalFlightDist,
                float finalDist,
                float flatDistTotal,
                float initialPhaseDur,
                bool isAir,
                bool isHighBallisticLofter,
                bool isTerminalLoft,
                float launchPitch,
                float loftAlt,
                float loftEntryDist,
                float loftSpeedHoldDist,
                float loftVelKn,
                bool lofting,
                float maxFlight,
                float maxVelKn,
                bool nonKin,
                float targetAlt0,
                Vector3 targetPos,
                Vector3 targetVel,
                float termAlt,
                float termDist,
                float termVelKn,
                bool trackDiag,
                float turnRate,
                float toBearingTurnRate,
                float turnRateBase,
                float turnDerateThresholdKn,
                int altLatchPhase,
                bool altLatched,
                Quaternion att,
                Vector3 launchHeading,
                float nextSample,
                Vector3 pos,
                float prevAltErr,
                float prevFlat,
                float prevPitch,
                float t,
                bool tlGliding,
                float velKnots)
            {
                AltNodes = altNodes;
                AmmoLabel = ammoLabel;
                BoostClimbDeg = boostClimbDeg;
                DecelPerStep = decelPerStep;
                DescentDeg = descentDeg;
                DescentOnsetDeg = descentOnsetDeg;
                DragFactor = dragFactor;
                CruiseAlt = cruiseAlt;
                FinalFlightAlt = finalFlightAlt;
                FinalFlightDist = finalFlightDist;
                FinalDist = finalDist;
                FlatDistTotal = flatDistTotal;
                InitialPhaseDur = initialPhaseDur;
                IsAir = isAir;
                IsHighBallisticLofter = isHighBallisticLofter;
                IsTerminalLoft = isTerminalLoft;
                LaunchPitch = launchPitch;
                LoftAlt = loftAlt;
                LoftEntryDist = loftEntryDist;
                LoftSpeedHoldDist = loftSpeedHoldDist;
                LoftVelKn = loftVelKn;
                Lofting = lofting;
                MaxFlight = maxFlight;
                MaxVelKn = maxVelKn;
                NonKin = nonKin;
                TargetAlt0 = targetAlt0;
                TargetPos = targetPos;
                TargetVel = targetVel;
                TermAlt = termAlt;
                TermDist = termDist;
                TermVelKn = termVelKn;
                TrackDiag = trackDiag;
                TurnRate = turnRate;
                ToBearingTurnRate = toBearingTurnRate;
                TurnRateBase = turnRateBase;
                TurnDerateThresholdKn = turnDerateThresholdKn;
                AltLatchPhase = altLatchPhase;
                AltLatched = altLatched;
                Att = att;
                LaunchHeading = launchHeading;
                NextSample = nextSample;
                Pos = pos;
                PrevAltErr = prevAltErr;
                PrevFlat = prevFlat;
                PrevPitch = prevPitch;
                T = t;
                TlGliding = tlGliding;
                VelKnots = velKnots;
            }
        }

        /// <summary>
        /// Which of the two cruise-leg altitudes applies at this distance. The game runs
        /// <c>MaintainSeaSkimming</c> and <c>MaintainFinalFlightAlt</c> as separate stages with
        /// separate altitudes and switches at <c>FinalFlightPhaseDistToTarget</c>
        /// (<c>Missile.cs:614-647</c>). Inert when the ammunition declares no final-flight distance,
        /// or declares the same altitude for both, which is the common case.
        /// </summary>
        private static float CruiseOrFinalFlightAlt(float flatDist, float finalFlightDist,
                                                    float finalFlightAlt, float cruiseAlt)
            => (finalFlightDist > 0f && flatDist <= finalFlightDist) ? finalFlightAlt : cruiseAlt;

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
                                   ref IntegratedPhases phases)
        {
            const float KU = GameUnits.KnotsToUnityPerSecond;
            const float dt = IntegrationStepSim;
            object[] thrustArgs = new object[4];
            object[] dragArgs = new object[10];

            Vector2[]   altNodes               = i.AltNodes;
            string      ammoLabel              = i.AmmoLabel;
            float       boostClimbDeg          = i.BoostClimbDeg;
            float       decelPerStep           = i.DecelPerStep;
            float       descentDeg             = i.DescentDeg;
            float       descentOnsetDeg        = i.DescentOnsetDeg;
            float       dragFactor             = i.DragFactor;
            float       cruiseAlt              = i.CruiseAlt;
            float       finalFlightAlt         = i.FinalFlightAlt;
            float       finalFlightDist        = i.FinalFlightDist;
            float       finalDist              = i.FinalDist;
            float       flatDistTotal          = i.FlatDistTotal;
            float       initialPhaseDur        = i.InitialPhaseDur;
            bool        isAir                  = i.IsAir;
            bool        isHighBallisticLofter  = i.IsHighBallisticLofter;
            bool        isTerminalLoft         = i.IsTerminalLoft;
            float       launchPitch            = i.LaunchPitch;
            float       loftAlt                = i.LoftAlt;
            float       loftEntryDist          = i.LoftEntryDist;
            float       loftSpeedHoldDist      = i.LoftSpeedHoldDist;
            float       loftVelKn              = i.LoftVelKn;
            bool        lofting                = i.Lofting;
            float       maxFlight              = i.MaxFlight;
            float       maxVelKn               = i.MaxVelKn;
            bool        nonKin                 = i.NonKin;
            float       targetAlt0             = i.TargetAlt0;
            Vector3     targetPos              = i.TargetPos;
            Vector3     targetVel              = i.TargetVel;
            float       termAlt                = i.TermAlt;
            float       termDist               = i.TermDist;
            float       termVelKn              = i.TermVelKn;
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
            float       velKnots               = i.VelKnots;

            // The receding test below is a closest-approach guard: it ends the flight when the
            // round stops closing, which is how an overflight terminates. It must not fire
            // BEFORE the round has ever closed, because a fixed-rail launch deliberately flies
            // the rail's bearing first (see the launch-heading block a few lines down) and then
            // turns onto the target. With the rail more than ~90 degrees off, distance grows
            // from the first step, the guard fired at t = 2*dt, and the estimate came back as
            // exactly 0.2s for a real 700s shot. Ships never produced it because a trainable
            // launcher is already pointed at the target; an aircraft's hardpoint points wherever
            // the nose does, so any turn hit it. See
            // docs/plans/open/offboresight-estimate-collapse.md.
            //
            // 2026-09-07: the latch as first written armed on the SEED step. `prevFlat` starts
            // at float.MaxValue, so the very first `flatDist < prevFlat` is true no matter which
            // way the round is pointing, and the guard was live again by t = 2*dt. The run that
            // day still produced 0.20s eleven times; only the plausibility floor caught them.
            // `havePrev` makes the first real step the first comparison.
            bool hasClosed = false;
            bool havePrev = false;

            while (t < maxFlight)
            {
                Vector3 predTgt = targetPos + targetVel * t;
                float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                if ((flatDist > prevFlat && t > dt && hasClosed) || flatDist < CloseEnoughDistU)
                {
                    ModelStats.LoopDone((int)(t / dt));
                    if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
                    return t;
                }
                if (havePrev && flatDist < prevFlat) hasClosed = true;
                prevFlat = flatDist;
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
                    float descentGeomDist = (pos.y - termAlt)
                                          / Mathf.Tan(Mathf.Max(descentOnsetDeg, MinDescentOnsetDeg) * Mathf.Deg2Rad);
                    float diveStart = Mathf.Max(termDist, descentGeomDist);
                    if (diveStart > 0f && flatDist <= diveStart)
                    { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                    else if (finalDist > 0f && flatDist <= finalDist)
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
                    else if (lofting && (loftEntryDist <= 0f || flatDistTotal > loftEntryDist))
                    { stageTgt = loftVelKn; stageAlt = loftAlt; phase = 0; }
                    else
                    {
                        phase = 1;
                        stageAlt = CruiseOrFinalFlightAlt(flatDist, finalFlightDist,
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
                        // Unity's euler x is nose-DOWN positive, so climb-positive pitch is
                        // negated going in and read back off the forward vector coming out.
                        Quaternion tgt = Quaternion.Euler(-targetPitch, YawOf(horizDirTarget), 0f);
                        att = Quaternion.RotateTowards(att, tgt, stepTurnRate * dt);
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
                        pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, stepTurnRate * dt);
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
    }
}
