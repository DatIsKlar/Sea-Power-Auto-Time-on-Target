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
            internal readonly float       FinalAlt;
            internal readonly float       FinalDist;
            internal readonly float       FlatDistTotal;
            internal readonly float       InitialPhaseDur;
            internal readonly bool        IsAir;
            internal readonly bool        IsHighBallisticLofter;
            internal readonly bool        IsTerminalLoft;
            internal readonly float       LaunchPitch;
            internal readonly float       LoftAlt;
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
            internal readonly float       TurnRate;
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
                float finalAlt,
                float finalDist,
                float flatDistTotal,
                float initialPhaseDur,
                bool isAir,
                bool isHighBallisticLofter,
                bool isTerminalLoft,
                float launchPitch,
                float loftAlt,
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
                FinalAlt = finalAlt;
                FinalDist = finalDist;
                FlatDistTotal = flatDistTotal;
                InitialPhaseDur = initialPhaseDur;
                IsAir = isAir;
                IsHighBallisticLofter = isHighBallisticLofter;
                IsTerminalLoft = isTerminalLoft;
                LaunchPitch = launchPitch;
                LoftAlt = loftAlt;
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
            const float ZeroDensityAltU = 1f / 0.00163f;
            object[] thrustArgs = new object[4];
            object[] dragArgs = new object[10];

            Vector2[]   altNodes               = i.AltNodes;
            string      ammoLabel              = i.AmmoLabel;
            float       boostClimbDeg          = i.BoostClimbDeg;
            float       decelPerStep           = i.DecelPerStep;
            float       descentDeg             = i.DescentDeg;
            float       descentOnsetDeg        = i.DescentOnsetDeg;
            float       dragFactor             = i.DragFactor;
            float       finalAlt               = i.FinalAlt;
            float       finalDist              = i.FinalDist;
            float       flatDistTotal          = i.FlatDistTotal;
            float       initialPhaseDur        = i.InitialPhaseDur;
            bool        isAir                  = i.IsAir;
            bool        isHighBallisticLofter  = i.IsHighBallisticLofter;
            bool        isTerminalLoft         = i.IsTerminalLoft;
            float       launchPitch            = i.LaunchPitch;
            float       loftAlt                = i.LoftAlt;
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

                    // Fixed-rail launch heading: fly the rail's bearing for the initial flight
                    // phase, then turn toward the target at MaxTurnRate. The horizontal mirror of
                    // the launchPitch hold further down.
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
    }
}
