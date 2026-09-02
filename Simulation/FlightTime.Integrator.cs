using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        private const float IntegrationStepSim = 0.1f;
        private const float AltToleranceU = 0.5f;
        private const float DefaultClimbDeg = 20f;
        private const float DefaultDescentDeg = 20f;
        private const float BoostClimbDeg = 90f;
        private const float DefaultTurnRateDeg = 5f;
        private const float MinDescentOnsetDeg = 5f;
        private const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        private const float TelemetrySampleIntervalSim = 15f;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        private const float LookaheadMultiplier = 20f;
        private const float MinLookaheadU = 50f;
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
                float boostClimbDeg = (!nonKin && lofting) ? BoostClimbDeg : climbDeg;
                float turnRate = ap._maxTurnRateDegrees > VelocityEpsilonKn ? ap._maxTurnRateDegrees : DefaultTurnRateDeg;

                float launchPitch = -1f;
                try
                {
                    var launchers = unit.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
                    if (launchers != null && launchers.Count > 0)
                    {
                        var vwp = launchers[0]?._vwp;
                        if (vwp != null && vwp._fixVerticalLaunchAngleForLauncher)
                            launchPitch = vwp._fixVerticalLaunchAngle + vwp._additionalFixVerticalLaunchAngle;
                    }
                }
                catch { launchPitch = -1f; }
                float initialPhaseDur = Mathf.Max(ap._initialFlightPhaseDuration, 0f);

                float maxVelKn = Mathf.Max(ap._maxVelocityInKnots, 1f);
                float loftVelKn = ap._maxLoftVelocityInKnots > 0f ? ap._maxLoftVelocityInKnots : maxVelKn;
                float termVelKn = ap._terminalVelocityInKnots > 0f ? ap._terminalVelocityInKnots : maxVelKn;
                float decelPerStep = ap._deceleration * GravityKnPerMs * IntegrationStepSim;

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
                float termAlt = ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt;
                float descentDeg = ap._finalFlightPhaseMaxAngle > 0.01f ? ap._finalFlightPhaseMaxAngle
                                 : (ap._seaSkimmingMaxDescentAngle > 0.01f ? ap._seaSkimmingMaxDescentAngle : DefaultDescentDeg);
                float descentOnsetDeg = Mathf.Max(descentDeg,
                    Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));

                phases.Valid = true;
                phases.Lofting = lofting;
                phases.LoftAltTarget = lofting ? loftAlt : 0f;
                phases.VStart = startVelKnots;
                phases.PeakAltU = launchPos.y;
                phases.FinalDistU = finalDist;
                phases.TermDistU = termDist;

                Vector3 pos = launchPos;
                float velKnots = startVelKnots;
                float t = 0f;
                float prevPitch = launchPitch >= 0f ? launchPitch : 0f;
                float prevFlat = float.MaxValue;
                bool tlGliding = false;

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
                float nextSample = TelemetrySampleIntervalSim;
                bool trackDiag = Coordinator.VerboseLog && emitDiag;
                string ammoLabel = ap._ammunitionFileName ?? "?";
                if (trackDiag)
                    Bootstrap.Log.LogInfo($"[AutoTOT] sim-launch {ammoLabel}: launchPitch " +
                        $"{(launchPitch >= 0f ? launchPitch.ToString("0.0") + "°" : "n/a (heading)")}" +
                        $", initPhase {initialPhaseDur:0.0}s, turnRate {turnRate:0.0}/s, loftAlt {loftAlt:0}u" +
                        $", descentDeg {descentDeg:0.0}°, onsetDeg {descentOnsetDeg:0.0}°");

                while (t < maxFlight)
                {
                    Vector3 predTgt = targetPos + targetVel * t;
                    float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                    float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                    if ((flatDist > prevFlat && t > dt) || flatDist < CloseEnoughDistU)
                    {
                        if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
                        return t;
                    }
                    prevFlat = flatDist;

                    Vector3 horizDir = flatDist > 1e-4f
                        ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;

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
                        if (altErr > AltToleranceU) targetPitch = boostClimbDeg;
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

                        pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, turnRate * dt);
                    }
                    float pitchRate = (pitchDeg - prevPitch) / dt;

                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                    float thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
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
                        dragArgs[0] = pos.y; dragArgs[1] = velKnots * KU; dragArgs[2] = dt; dragArgs[3] = -pitchDeg;
                        dragArgs[4] = dragFactor; dragArgs[5] = motorBurning;
                        bool inVacuumDive = phase == 2 && pos.y > ZeroDensityAltU && pitchDeg < VacuumDivePitchThreshold;
                        dragArgs[6] = inVacuumDive ? pos.y : predTgt.y;
                        dragArgs[7] = ap.LiftFactor; dragArgs[8] = ap.MinVelocity; dragArgs[9] = -pitchRate;
                        dragThisStep = (float)_dragMethod.Invoke(null, dragArgs);
                        velKnots -= dragThisStep;
                        if (motorBurning && velKnots > stageTgt) velKnots = stageTgt;
                    }

                    if (velKnots < MinSpeedKn) return -1f;

                    float pr = pitchDeg * Mathf.Deg2Rad;
                    Vector3 dir = horizDir * Mathf.Cos(pr) + Vector3.up * Mathf.Sin(pr);
                    pos += velKnots * KU * dt * dir;

                    if (phase == 0) { phases.ClimbTime += dt; phases.VClimbExit = velKnots; }
                    else if (phase == 1) { phases.CruiseTime += dt; phases.VCruiseExit = velKnots; }
                    else { phases.DescentTime += dt; }
                    phases.VTerm = velKnots;
                    if (pos.y > phases.PeakAltU) phases.PeakAltU = pos.y;

                    if (trackDiag && t + dt >= nextSample)
                    {
                        float slantKm = (predTgt - pos).magnitude * GameUnits.MetersPerUnity / 1000f;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] sim-track {ammoLabel}: t+{t:0}s spd {velKnots:0}kn alt {pos.y:0.0} " +
                            $"pitch {pitchDeg:0} drag {dragThisStep / dt:0}kn/s phase {phase} " +
                            $"dist {flatDist * GameUnits.MetersPerUnity / 1000f:0.0}km slant {slantKm:0.0}km");
                        nextSample += TelemetrySampleIntervalSim;
                    }

                    prevPitch = pitchDeg;
                    t += dt;
                }
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
