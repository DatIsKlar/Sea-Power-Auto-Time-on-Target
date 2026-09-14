using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The waypoint route a non-kinematic round flies against a surface target, and the point on it
    /// the round steers at.
    ///
    /// A cruise missile does not fly a climb command to an altitude. Each leg of its route is an
    /// Immediate (or Linear) waypoint, and the missile steers at a look-ahead point that
    /// <c>Waypoint.CalculatePointAtTime</c> (Waypoint.cs:423) places by lerping from the missile
    /// toward the TARGET by <c>flatV * T / distance-to-waypoint</c>. The lead therefore grows without
    /// limit as a boundary nears, and the round flattens before every boundary. Measured on six
    /// rounds flown with GuardTrack, the lead matches this to about 1%.
    ///
    /// Everything here is a pure function of values resolved at setup, so it runs in the lab and on
    /// the estimator threads. <c>WaypointSim</c> covers the same game code a second way, by driving
    /// the game's own Waypoint objects through reflection for the fallback tier; see its header for
    /// why the two stay separate.
    /// </summary>
    internal static partial class FlightTime
    {
        internal enum WaypointLegMode : byte { Immediate, Linear, Kappa }

        /// <summary>One leg of the route, resolved at setup from the ammunition.</summary>
        internal readonly struct WaypointLeg
        {
            /// <summary>Flat distance to target at which the leg completes.</summary>
            internal readonly float EndDist;
            /// <summary>Resolved end height, Unity units.</summary>
            internal readonly float Alt;
            internal readonly float ClimbDeg;
            internal readonly float DescentDeg;
            /// <summary>The inverse of the game's AllowExceedingAngleLimits.</summary>
            internal readonly bool ClampAngles;
            internal readonly WaypointLegMode Mode;
            internal readonly bool SkipNext;

            internal WaypointLeg(float endDist, float alt, float climbDeg, float descentDeg,
                                 bool clampAngles, WaypointLegMode mode, bool skipNext)
            {
                EndDist = endDist;
                Alt = alt;
                ClimbDeg = climbDeg;
                DescentDeg = descentDeg;
                ClampAngles = clampAngles;
                Mode = mode;
                SkipNext = skipNext;
            }

            /// <summary>Waypoint.UpdateAndGetActiveWaypoint (Waypoint.cs:687-694): a completed leg
            /// that asks to skip the next one hands it its own settings, keeping the next leg's
            /// boundary, and clears the flag on the copy.</summary>
            internal WaypointLeg WithSettingsOf(in WaypointLeg s)
                => new WaypointLeg(EndDist, s.Alt, s.ClimbDeg, s.DescentDeg, s.ClampAngles, s.Mode,
                                   false);

            public override string ToString()
                => System.FormattableString.Invariant(
                    $"{EndDist:0.###}/{Mode}/{Alt:0.###}/{ClimbDeg:0.#}/{DescentDeg:0.#}/{(ClampAngles ? 1 : 0)}{(SkipNext ? 1 : 0)}");
        }

        /// <summary>
        /// Missile.CreateWaypointConfigs (Missile.cs:2145-2425) for a non-kinematic round against a
        /// surface target, followed by FastForwardWaypoints (Missile.cs:2809). Takes the raw
        /// ammunition values, in Unity units and degrees, so the lab can rebuild the route from a
        /// capture. Returns null when there is no route to fly.
        ///
        /// A terrain follower's heights are AboveTerrain. Over open sea that is the same value as an
        /// absolute height plus the wave height (TerrainUtils.cs:1590-1607), about 0.01u, so the
        /// route is shared; flight over land is not modelled.
        ///
        /// A group leader's final-flight and terminal legs fly GroupLeaderAlt (Missile.cs:2245,
        /// :2310). Pass a negative <paramref name="groupLeaderAlt"/> for a round that does not lead.
        /// Every round of an ammunition with GroupSize above 1 joins or creates a group
        /// (Missile.cs:2129, :3244), so a single round leads a group of one, and in a salvo the
        /// followers form on the leader, whose flight is the one the salvo keeps time to.
        /// </summary>
        internal static WaypointLeg[] BuildNonKinLegs(
            float launchFlat, float targetAlt,
            float maxLoftAlt, float maxLoftAngle, bool loftToSkim,
            float seaSkimStart, float seaSkimAlt, float seaSkimAngle,
            float finalFlightDist, float finalFlightAlt, float finalFlightAngle,
            float terminalApproachDist, float terminalAltRaw, bool terminalLoft,
            float terminalDiveDist, bool popUpEnabled, float popUpDist, bool terrainFollow,
            float groupLeaderAlt)
        {
            bool leader = groupLeaderAlt >= 0f;
            var legs = new System.Collections.Generic.List<WaypointLeg>(6);
            float d = 0f;
            float targetFloor = Mathf.Max(targetAlt, WaypointMinHeightU);

            // Built in the game's order, nearest the target first, and reversed at the end.
            if (terminalDiveDist > 0f && terminalDiveDist < terminalApproachDist && !terminalLoft)
            {
                // Linear to the target's height. AllowExceedingAngleLimits keeps its default, true.
                legs.Add(new WaypointLeg(0f, targetFloor, 0f, 0f, false,
                                         WaypointLegMode.Linear, false));
                d = terminalDiveDist;
            }
            if (popUpDist > 0f && popUpEnabled)
            {
                if (legs.Count > 0) legs.RemoveAt(0);
                legs.Add(new WaypointLeg(0f, targetFloor, 0f, 0f, false,
                                         WaypointLegMode.Kappa, false));
                d = popUpDist;
            }
            if (terminalApproachDist > 0f && !terminalLoft)
            {
                float alt = terminalAltRaw;
                if (alt < 0f && legs.Count != 0)
                    alt = finalFlightDist > 0f ? finalFlightAlt : seaSkimAlt;
                // A negative height left over is OnLineToTarget, which flies Linear to the target,
                // but only for an Absolute height: a terrain follower's AboveTerrain height is
                // floored at the surface instead (Missile.cs:2235-2239).
                bool onLine = alt < 0f && !terrainFollow;
                // The leader's height replaces the value AFTER the on-line test (Missile.cs:2245).
                if (leader) alt = groupLeaderAlt;
                legs.Add(new WaypointLeg(d, onLine ? targetFloor : Mathf.Max(alt, WaypointMinHeightU),
                                         finalFlightAngle, finalFlightAngle, false,
                                         onLine ? WaypointLegMode.Linear : WaypointLegMode.Immediate,
                                         false));
                d = terminalLoft ? 0.5f : terminalApproachDist;
            }
            if (finalFlightDist > 0f)
            {
                legs.Add(new WaypointLeg(d, leader ? groupLeaderAlt : finalFlightAlt,
                                         finalFlightAngle, finalFlightAngle,
                                         true, WaypointLegMode.Immediate, false));
                d = finalFlightDist;
            }
            if (seaSkimStart > 0f)
            {
                legs.Add(new WaypointLeg(d, seaSkimAlt, seaSkimAngle, seaSkimAngle,
                                         true, WaypointLegMode.Immediate, false));
                d = seaSkimStart;
            }
            if (maxLoftAlt > 0f)
            {
                // Non-kinematic and not an air target: Immediate, steering to LoftHeight, which is
                // the declared ceiling with that same ceiling as its minimum (Missile.cs:2351-2419,
                // Waypoint.GetDesiredHeight).
                legs.Add(new WaypointLeg(d, maxLoftAlt, maxLoftAngle, finalFlightAngle,
                                         true, WaypointLegMode.Immediate, !loftToSkim));
            }
            if (legs.Count == 0) return null;
            legs.Reverse();

            // FastForwardWaypoints at creation: drop leading legs the round is already inside.
            // Their settings are never copied here, because the launch point is not outside them.
            while (legs.Count > 1 && launchFlat <= legs[0].EndDist) legs.RemoveAt(0);
            return legs.ToArray();
        }

        /// <summary>The floor from each leg's MinHeight, AboveSeaLevel 0.</summary>
        private const float WaypointMinHeightU = 0.001f;

        /// <summary>
        /// Waypoint.CalculateFuturePointRecursive and CalculatePointAtTime (Waypoint.cs:393-463).
        /// Returns the look-ahead point as (flat distance to target, height).
        ///
        /// When the look-ahead runs past the active leg's boundary the game continues into the next
        /// leg from the boundary, at the height the missile would reach there, using that leg's OWN
        /// settings: the skip-copy happens only when a leg completes. That is why a loft round dips
        /// briefly toward sea-skimming height just before its first boundary.
        ///
        /// <paramref name="popUpHeight"/> is the terminal pop-up's height when a look-ahead that reaches
        /// the pop-up leg should follow its KappaLoft curve (0: take the leg's end height), with the
        /// target's height and the pop-up angle that curve needs.
        /// </summary>
        internal static Vector2 WaypointPointAtTime(WaypointLeg[] legs, int activeIdx,
                                                     in WaypointLeg active, float legProgress,
                                                     float legStartAlt, float lookTime,
                                                     float flatDist, float posY,
                                                     float flatSpeed, float verticalSpeed,
                                                     float popUpTargetAlt, float popUpHeight,
                                                     float popUpAngle)
        {
            WaypointLeg leg = active;
            int k = activeIdx;
            float fromS = flatDist, fromY = posY, lastAlt = legStartAlt, pct = legProgress;
            float remaining = Mathf.Max(flatDist - active.EndDist, 0f);
            float look = lookTime;
            while (true)
            {
                float timeToEnd = flatSpeed > WaypointDistanceEpsilonU
                    ? remaining / flatSpeed : float.MaxValue;
                if (look <= timeToEnd || k >= legs.Length - 1)
                {
                    float run = flatSpeed * Mathf.Min(look, timeToEnd);
                    if (run > remaining) run = remaining;
                    float fraction = remaining > WaypointDistanceEpsilonU ? run / remaining : 1f;
                    float progress = Mathf.Min(pct + (1f - pct) * fraction, 1f);
                    float h = leg.Mode == WaypointLegMode.Linear
                        ? lastAlt + progress * (leg.Alt - lastAlt)
                        : leg.Alt;
                    float lead = fromS * fraction;
                    bool clamp = leg.ClampAngles;
                    float climbDeg = leg.ClimbDeg, descentDeg = leg.DescentDeg;
                    // A look-ahead that reaches the pop-up leg takes that leg's own KappaLoft height and
                    // climb limit, as Waypoint.CalculateFuturePointRecursive does, instead of its end height.
                    if (popUpHeight > 0f && leg.Mode == WaypointLegMode.Kappa && k != activeIdx)
                    {
                        h = TerminalPopUpAltitude(progress, lastAlt, popUpTargetAlt, popUpHeight,
                                                  popUpAngle, Mathf.Max(fromS, WaypointDistanceEpsilonU));
                        clamp = true;
                        climbDeg = descentDeg = popUpAngle;
                    }
                    if (clamp && lead > WaypointDistanceEpsilonU &&
                        progress < WaypointClampProgressLimit)
                    {
                        float rise = h - fromY;
                        if (rise > 0f && climbDeg > WaypointAngleEpsilonDeg)
                            h = Mathf.Min(h, fromY + lead * Mathf.Tan(climbDeg * Mathf.Deg2Rad));
                        else if (rise < 0f && descentDeg > WaypointAngleEpsilonDeg)
                            h = Mathf.Max(h, fromY - lead * Mathf.Tan(descentDeg * Mathf.Deg2Rad));
                    }
                    return new Vector2(fromS - lead, Mathf.Max(h, WaypointMinHeightU));
                }

                // Into the next leg. The boundary height is the MISSILE's height projected by this
                // level's time-to-boundary, not an accumulated one (Waypoint.cs:412).
                look -= timeToEnd;
                float boundaryY = posY + timeToEnd * verticalSpeed;
                fromS = leg.EndDist;
                k++;
                leg = legs[k];
                remaining = Mathf.Max(fromS - leg.EndDist, 0f);
                fromY = boundaryY;
                lastAlt = boundaryY;
                pct = 0f;
            }
        }

        private const float WaypointAngleEpsilonDeg = 0.001f;

        /// <summary>CalculatePointAtTime drops the angle clamp once the look-ahead point is this far
        /// through its waypoint (Waypoint.cs:444).</summary>
        private const float WaypointClampProgressLimit = 0.95f;

        /// <summary>A completed leg hands on its settings only past this progress
        /// (Waypoint.cs:687).</summary>
        private const float LegSkipCopyProgress = 0.01f;

        /// <summary>Unity's Vector3 equality threshold on the squared difference.</summary>
        private const float UnityVectorEqualitySqr = 9.99999944E-11f;
    }
}
