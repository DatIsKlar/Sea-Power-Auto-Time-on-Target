using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The point the game measures a round's distance-keyed stage switches to.
    ///
    /// <c>MaintainHeight.takeAction</c> tests the terminal, final-flight and sea-skimming boundaries and the
    /// seeker switch-on against the flat distance to <c>_targetInterceptPosition</c> (Missile.cs:374-376),
    /// and <c>Waypoint.UpdateAndGetActiveWaypoint</c> completes legs against the same point. That point is
    /// the target the missile believes in, led by the velocity it believes
    /// (<c>CalculateInterceptAimPoint</c>, Missile.cs:2427-2468); a round without launcher guidance
    /// dead-reckons it from ToBearing entry at the reported velocity, so a ship whose float32 rounding moves
    /// it slower than it reports is overtaken and the switch comes early.
    ///
    /// Every input is known at the point of fire. The game's random terms (INS drift, aim deviation, the
    /// seeker's estimate once locked) are left out because a planner cannot know them. Plain arithmetic
    /// except the drag call, which uses the step loop's binding so the lab and the game run the same code.
    /// </summary>
    internal static partial class FlightTime
    {
        /// <summary>Which target position the step loop measures stage and waypoint switches to.</summary>
        internal enum SwitchTarget : byte
        {
            /// <summary>
            /// Flat range to the predicted ship; the struct default, so a capture that omits the field
            /// replays as before. Setup also picks it for an air target (semi-active homers measure to the
            /// target itself), a <c>ForceDirectPoint</c> round (Missile.cs:2441-2445), and when the game's
            /// drag method did not bind.
            /// </summary>
            ShipCentre = 0,
            /// <summary>A launcher-guided round. <c>OnUpdateEveryFrame</c> refreshes <c>_lostGuidanceTime</c>
            /// while guidance holds (Missile.cs:1942-2027), so <c>CalcEstimatedTargetPosition</c> reads the
            /// true position; the lead still uses the reported velocity.</summary>
            Tracked = 1,
            /// <summary>Every other round: the target at ToBearing entry, advanced at the reported
            /// velocity (Missile.cs:2729-2731), until the seeker locks and the known position becomes
            /// the seeker's estimate of the real ship (Missile.cs:2699-2709).</summary>
            DeadReckoned = 2,
        }

        /// <summary>The game's own knots-to-units literal in these three methods.</summary>
        private const float LeadKnotsToUnity = 0.0076554087f;

        /// <summary>Setup's choice of <see cref="SwitchTarget"/>, as a pure function so the lab derives
        /// an older capture's value by the same rule.</summary>
        internal static SwitchTarget ResolveSwitchTarget(bool airTarget, bool forceDirectPoint,
                                                         bool launcherGuided, bool dragBound)
        {
            if (airTarget || forceDirectPoint || !dragBound) return SwitchTarget.ShipCentre;
            return launcherGuided ? SwitchTarget.Tracked : SwitchTarget.DeadReckoned;
        }

        /// <summary>
        /// The range inside which a dead-reckoning round's seeker can lock, or 0 when it never can
        /// (no seeker). Lock needs an echo, which is a slant-range test against the seeker's own range
        /// (<c>ActiveRadarSeeker.isInRange</c>); for a round without launcher guidance the seeker is
        /// switched on only when the round takes the terminal branch (Missile.cs:569), so the step
        /// loop also requires the terminal phase to have started.
        /// </summary>
        internal static float ResolveSeekerLockRange(bool unguided, float activeRange, float passiveRange)
        {
            if (unguided) return 0f;
            float range = Mathf.Max(activeRange, passiveRange);
            return range > 0f ? range : 0f;
        }

        /// <summary>
        /// Missile.CalculateLeadInterceptPoint (Missile.cs:2603-2636): the time after which the known
        /// target, moving at <paramref name="knownVel"/>, meets the round. Returns -1 when either
        /// quadratic has no positive root; the game then leaves <c>_targetInterceptPosition</c> where
        /// it was, and so does the caller.
        /// </summary>
        /// <param name="pitchDeg">The round's flight-path pitch, climb positive. Only the altitude term
        /// reads it, and only above 1 u over the target.</param>
        internal static float LeadInterceptTime(Vector3 pos, float speedKn, float travelledFlatU,
                                                Vector3 knownTgt, Vector3 knownVel, float maxVelKn,
                                                float loftAltU, float finalAngleDeg, float pitchDeg,
                                                float dragFactor, float liftFactor, float stallKn)
        {
            Vector3 toTarget = knownTgt - pos;
            float distance = toTarget.magnitude;
            float speedU = speedKn * LeadKnotsToUnity;
            float speed = speedU;
            // Early in the flight the round is assumed to reach cruise speed.
            if (travelledFlatU * 4f < distance)
                speed = Mathf.Max(speed, maxVelKn * LeadKnotsToUnity / 1.3f);

            float t = SolveInterceptQuadratic(speed, toTarget, knownVel);
            if (t <= 0f) return -1f;
            float avg = DragCorrectedAvgSpeed(speed, speedKn, t, loftAltU, finalAngleDeg, dragFactor,
                                              pos.y, knownTgt.y, liftFactor, stallKn);
            t = SolveInterceptQuadratic(avg, toTarget, knownVel);
            if (t <= 0f) return -1f;
            t = Mathf.Min(t, 2f * distance / Mathf.Max(avg, 0.001f));

            float above = pos.y - knownTgt.y;
            if (above > 1f && finalAngleDeg > 0.001f)
            {
                // The game takes the dive angle off the velocity vector, asin(-vy / |v|); the model's
                // velocity lies along its pitch, so that is -pitch.
                float diveDeg = -pitchDeg;
                float f = Mathf.Clamp(finalAngleDeg - Mathf.Max(diveDeg, 0f), 0f, finalAngleDeg) *
                          (Mathf.PI / 180f) * 0.5f;
                t += above * Mathf.Tan(f) / avg;
            }
            return t;
        }

#if AUTOTOT_OFFLINE
        /// <summary>Offline entry for tools/lab's leadcheck, which needs the drag binding bound first,
        /// exactly as SolveOffline does. Exists only in the AUTOTOT_OFFLINE build.</summary>
        internal static float LeadInterceptTimeOffline(Vector3 pos, float speedKn, float travelledFlatU,
                                                       Vector3 knownTgt, Vector3 knownVel, float maxVelKn,
                                                       float loftAltU, float finalAngleDeg, float pitchDeg,
                                                       float dragFactor, float liftFactor, float stallKn)
        {
            EnsureSimLookup();
            return LeadInterceptTime(pos, speedKn, travelledFlatU, knownTgt, knownVel, maxVelKn, loftAltU,
                                     finalAngleDeg, pitchDeg, dragFactor, liftFactor, stallKn);
        }
#endif

        /// <summary>Missile.SolveInterceptQuadratic (Missile.cs:3546-3586), term for term.</summary>
        internal static float SolveInterceptQuadratic(float missileSpeed, Vector3 toTarget,
                                                      Vector3 targetVelocity)
        {
            float speedSq = missileSpeed * missileSpeed;
            float a = targetVelocity.sqrMagnitude - speedSq;
            float b = 2f * Vector3.Dot(toTarget, targetVelocity);
            float c = toTarget.sqrMagnitude;
            if (Mathf.Abs(a) < 0.0001f)
            {
                if (Mathf.Abs(b) < 0.0001f) return -1f;
                float linear = (0f - c) / b;
                return linear > 0f ? linear : -1f;
            }
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return -1f;
            float root = Mathf.Sqrt(disc);
            float t1 = (0f - b + root) / (2f * a);
            float t2 = (0f - b - root) / (2f * a);
            if (t1 > 0f && t2 > 0f) return Mathf.Min(t1, t2);
            if (t1 > 0f) return t1;
            if (t2 > 0f) return t2;
            return -1f;
        }

        /// <summary>
        /// Missile.DragCorrectedAvgSpeed (Missile.cs:2638-2654): the mean of the round's speed now and
        /// its speed after coasting for the intercept time under <c>CalculateDrag</c> taken once, at
        /// the middle of the descent. For a non-kinematic round the drag factor is -1, so the "drag"
        /// is negative and the average sits above the current speed.
        /// </summary>
        private static float DragCorrectedAvgSpeed(float speedU, float speedKn, float interceptTime,
                                                   float loftAltU, float finalAngleDeg,
                                                   float dragFactor, float altitude,
                                                   float targetAltitude, float liftFactor,
                                                   float stallKn)
        {
            float top = Mathf.Max(altitude, loftAltU > 0f ? loftAltU : altitude);
            float drop = top - targetAltitude;
            float descentShare = 0.5f;
            if (drop > 1f && finalAngleDeg > 0.001f && interceptTime > 0.001f && loftAltU > 0f)
            {
                float descentRun = drop / Mathf.Tan(finalAngleDeg * (Mathf.PI / 180f));
                float flightRun = speedU * interceptTime;
                descentShare = Mathf.Clamp01(descentRun / flightRun);
            }
            float midAltitude = top - descentShare * drop * 0.5f;
            float drag = LeadDrag(midAltitude, speedU, dragFactor, targetAltitude, liftFactor, stallKn);
            float endKn = Mathf.Max(speedKn - drag * interceptTime, 0f);
            return (speedU + endKn * LeadKnotsToUnity) * 0.5f;
        }

        /// <summary>MissileSimulator.CalculateDrag for one second at zero pitch with the motor off, the
        /// call DragCorrectedAvgSpeed makes. Through the step loop's binding; setup does not select a
        /// lead target unless one of the two bound.</summary>
        private static float LeadDrag(float altitude, float velocityU, float dragFactor,
                                      float targetAltitude, float liftFactor, float stallKn)
        {
            if (_dragFn != null)
                return _dragFn(altitude, velocityU, 1f, 0f, dragFactor, false, targetAltitude,
                               liftFactor, stallKn, 0f);
            return (float)_dragMethod.Invoke(null, new object[]
            {
                altitude, velocityU, 1f, 0f, dragFactor, false, targetAltitude, liftFactor, stallKn, 0f
            });
        }
    }
}
