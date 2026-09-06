using System.Text;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// How long a shooter needs before it can fire at all, as opposed to how long its round then
    /// flies. A surface ship is ready where it stands; a submerged submarine must rise to the
    /// weapon's launch depth; an aircraft must descend into its launch envelope. The coordinator
    /// needs one number for all three, because that delay belongs in the enroute estimate that
    /// decides both anchor selection and follower release.
    ///
    /// Named for the question rather than the platform so the aircraft case has an obvious home.
    /// Submarines are the only implementation today: see
    /// <see cref="SubmarineFacts.EstimateLaunchDelay"/> and <see cref="SubmarineAscent"/>.
    ///
    /// The aircraft path is NOT written yet, deliberately. What is already known is that the game
    /// gates aircraft through the same engage-state machinery, setting
    /// <c>EngageState.LauncherTooHigh</c> from <c>_ap._minDepthUnity</c>
    /// (WeaponSystemLauncher.cs:425-431) beside the <c>LauncherTooLow</c> path submarines take, and
    /// that <c>AmmunitionParameters._launchAltitudesInUnity</c> carries MinLaunchAltitude and
    /// MaxLaunchAltitude. So the gate has the same shape and this seam is the right one. The
    /// descent dynamics still need their own read of the Aircraft motion code before anything is
    /// written here, exactly as the submarine model needed applyDepth read first.
    /// </summary>
    internal static class LaunchEnvelope
    {
        /// <summary>
        /// Seconds an aircraft needs to descend into its weapon's launch-altitude band. 0 when it is
        /// already inside the band, which is the usual case.
        ///
        /// Only the ABOVE-the-band case is modelled. An aircraft that is too LOW must climb, and
        /// climb is energy-limited through <c>GetMaxClimbPitch</c> whose <c>_dragPower</c> is private;
        /// that returns 0 here rather than a fabricated number, so a too-low aircraft is treated as
        /// ready and the coordinator's launch-state hold handles it as it would any other shooter
        /// that has not started firing.
        /// </summary>
        private static float AircraftDelay(Aircraft air, string ammoId, StringBuilder trace)
        {
            AmmunitionParameters ap = air.getAmmunitionByName(ammoId)?._ap;
            AircraftParameters p = air.Ap;
            if (ap == null || p == null) return 0f;

            // The launch band the hardpoint checks (WeaponSystemHardpoint.cs:748-749).
            float lo = ap._launchAltitudesInUnity.x, hi = ap._launchAltitudesInUnity.y;
            float alt = air.transform.position.y;
            if (hi <= lo || alt <= hi)
            {
                // Already able to fire. Say so, because a silent 0 here is indistinguishable from a
                // model that declined, and that cost a test run once.
                trace?.Append($" in band: {alt * AircraftDescent.FeetPerUnity:0}ft is at or below " +
                              $"gate {hi * AircraftDescent.FeetPerUnity:0}ft, no descent needed |");
                return 0f;
            }

            float mach = MachOf(air);
            if (mach <= 0f) return 0f;

            // What the aircraft is actually flying to. The hardpoint clamps this to the launch band
            // only OUTSIDE EngageSurfaceContact (WeaponSystemHardpoint.cs:1147), so on an anti-ship
            // strike it is the attack altitude, well below the ceiling, and that is what the pitch
            // taper is computed against.
            // ASSUMPTION, not read from source: an aircraft ordered onto a surface contact ends up
            // at or below the band's lower edge to attack, so the taper stays saturated through the
            // gate crossing. The exact attack altitude cannot be known here, because the commanded
            // altitude is still the cruise value at commit; the aircraft has not entered
            // EngageSurfaceContact yet. Reading DesiredAltitude alone made this return 0 every time,
            // since a cruising aircraft is commanded ABOVE the gate it is about to descend through.
            // Only the taper near the gate is sensitive to this, so the band floor is a safe proxy.
            float cmdAlt = Mathf.Min(air.DesiredAltitude.Value, lo);

            AircraftDescent.State st = new AircraftDescent.State
            {
                AltitudeU = alt,
                GateAltU = hi,
                CommandAltU = cmdAlt,
                Mach = mach,
                PitchDeg = -PitchDeg(air),            // euler is nose-down positive; the model is climb positive
                PitchRateDeg = p._maxTurnRates.x,
                // The COMBAT pair, deliberately, even though air._inCombat reads false right here.
                // GetDesiredPitch selects on _inCombat, and the aircraft is not in combat at commit:
                // the order being planned is what puts it there, and by the time it is descending to
                // release it flies the combat limit. Both measured airframes held exactly -60 deg,
                // matching _maxCombatDescentPitch, against the 40 deg out-of-combat value that was
                // seeded here before. Same trap as reading DesiredAltitude at commit and getting the
                // cruise altitude: a field read BEFORE the descent does not describe the state
                // DURING it.
                DescentLimitDeg = Mathf.Abs(p._maxCombatDescentPitch),
                ThresholdAltU = p._inCombatThresholdAltitude,
                MinG = p._minG,
                MaxG = p._maxG,
            };
            // Seed description, in the same shape the submarine writes, so envelope-sim reads the
            // same for both platforms. The in-combat flag is here because GetDesiredPitch selects
            // BOTH the pitch limit and the taper threshold on it, while this model always seeds the
            // out-of-combat pair; a run that logs inCombat True is telling us the seed is wrong.
            trace?.Append($" from {alt * AircraftDescent.FeetPerUnity:0}ft to gate {hi * AircraftDescent.FeetPerUnity:0}ft " +
                          $"(taper ref {cmdAlt * AircraftDescent.FeetPerUnity:0}ft, " +
                          $"desired {air.DesiredAltitude.Value * AircraftDescent.FeetPerUnity:0}ft, " +
                          $"band floor {lo * AircraftDescent.FeetPerUnity:0}ft), " +
                          $"mach {mach:0.00}, pitch {st.PitchDeg:0.0}, limit {st.DescentLimitDeg:0.0}deg, " +
                          $"threshold {st.ThresholdAltU * AircraftDescent.FeetPerUnity:0}ft, " +
                          $"inCombat {air._inCombat} (combat pair seeded regardless) |");
            return AircraftDescent.TryEstimate(st, out float seconds, trace) ? seconds : 0f;
        }

        /// <summary>
        /// Live vertical state of an aircraft shooter, for the envelope-track trace. Mirrors what
        /// <see cref="SubmarineFacts.Snapshot"/> does for a boat: the ground truth the prediction is
        /// scored against. Mach is carried alongside TAS because the model holds Mach constant and
        /// a dive that gains speed is the leading suspect for the residual.
        /// </summary>
        internal struct AirSnapshot
        {
            public float AltFt;        // current altitude
            public float GateFt;       // launch ceiling to get below
            public float Mach;         // instantaneous, NOT the commanded value
            public float CmdMach;      // commanded, so a throttle change is distinguishable
            public float TasKn;
            public float PitchDeg;     // positive = climb, matching GetDesiredPitch
            public bool AboveGate;     // still held out of its launch band
        }

        /// <summary>
        /// Reads <paramref name="unit"/> as an aircraft shooter. False for anything else, or when
        /// the ammo declares no usable launch band.
        /// </summary>
        internal static bool TrySnapshotAircraft(ObjectBase unit, string ammoId, out AirSnapshot s)
        {
            s = default;
            if (!(unit is Aircraft air) || air.IsDestroyed) return false;
            AmmunitionParameters ap = air.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;

            float lo = ap._launchAltitudesInUnity.x, hi = ap._launchAltitudesInUnity.y;
            if (hi <= lo) return false;

            float altU = air.transform.position.y;
            float sos = Atmosphere.SpeedOfSound(altU * AircraftDescent.UnityToMetres);
            ISpeedCommand sc = air.SpeedCommand?.Value;

            s.AltFt = altU * AircraftDescent.FeetPerUnity;
            s.GateFt = hi * AircraftDescent.FeetPerUnity;
            s.TasKn = Mathf.Abs(air._velocityInKnots);
            s.Mach = (sos > 0f) ? s.TasKn * 0.514444f / sos : 0f;
            s.CmdMach = (sc != null) ? sc.SpeedInMach : float.NaN;
            s.PitchDeg = -PitchDeg(air);
            s.AboveGate = altU > hi;
            return true;
        }

        /// <summary>Held Mach: the commanded value where there is one, else what it is doing now.</summary>
        private static float MachOf(Aircraft air)
        {
            ISpeedCommand sc = air.SpeedCommand?.Value;
            if (sc != null && sc.SpeedInMach > 0.01f) return sc.SpeedInMach;
            float sos = Atmosphere.SpeedOfSound(air.transform.position.y * AircraftDescent.UnityToMetres);
            return (sos > 0f) ? Mathf.Abs(air._velocityInKnots) * 0.514444f / sos : 0f;
        }

        private static float PitchDeg(Aircraft air)
        {
            float x = air.transform.localEulerAngles.x;
            return (x > 180f) ? x - 360f : x;
        }

        /// <summary>
        /// Seconds from the fire order until this shooter can put its first round away. 0 when it
        /// is ready now, which is the common case and the only EXACT answer: everything else is a
        /// model. Also 0 when the shooter can never get there, so callers must not read 0 as
        /// "ready" without checking the engage state; the no-launch hold in
        /// <see cref="Coordinator"/> is what terminates an order that will never fire.
        /// </summary>
        internal static float TimeToReady(ObjectBase unit, string ammoId)
            => TimeToReady(unit, ammoId, null);

        /// <summary>As above, writing a sampled profile into <paramref name="trace"/> for the
        /// asc-sim style diagnostic. Pass null on any path that runs per frame.</summary>
        internal static float TimeToReady(ObjectBase unit, string ammoId, StringBuilder trace)
        {
            if (unit == null || ammoId == null) return 0f;
            if (unit is Submarine) return SubmarineFacts.EstimateLaunchDelay(unit, ammoId, trace);
            if (unit is Aircraft air) return AircraftDelay(air, ammoId, trace);
            return 0f;   // surface ships are ready where they stand
        }
    }
}
