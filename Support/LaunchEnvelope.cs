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
                trace?.Append($" in band: {alt * GameUnits.UnityToFeet:0}ft is at or below " +
                              $"gate {hi * GameUnits.UnityToFeet:0}ft, no descent needed |");
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
                PitchDeg = -GameMath.PitchDeg(air.transform),            // euler is nose-down positive; the model is climb positive
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
            trace?.Append($" from {alt * GameUnits.UnityToFeet:0}ft to gate {hi * GameUnits.UnityToFeet:0}ft " +
                          $"(taper ref {cmdAlt * GameUnits.UnityToFeet:0}ft, " +
                          $"desired {air.DesiredAltitude.Value * GameUnits.UnityToFeet:0}ft, " +
                          $"band floor {lo * GameUnits.UnityToFeet:0}ft), " +
                          $"mach {mach:0.00}, pitch {st.PitchDeg:0.0}, limit {st.DescentLimitDeg:0.0}deg, " +
                          $"threshold {st.ThresholdAltU * GameUnits.UnityToFeet:0}ft, " +
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

            // D4 of docs/plans/open/anchor-liveness.md. The launcher sat in WaitingForRoll for the
            // whole of a 296s wait and roll was the one quantity nothing recorded, so a state named
            // after the aircraft's attitude could not be checked against that attitude.
            public float RollDeg;      // bank, positive = right wing down
            public float HeadingDeg;   // true heading
            public float BearingDeg;   // bearing to target, same reference as HeadingDeg
            public float OffBoreDeg;   // target relative to the nose, signed, -180..180
            public float RangeKm;      // slant range to target

            // D10 of docs/plans/open/anchor-liveness.md. The game's OWN pitch terms, read from
            // MotionController rather than derived from the transform, because these are the exact
            // values EngageSurfaceContact tests to decide whether the aircraft may fire. Logging
            // them beside our own PitchDeg is also what pins down the sign convention between the
            // two, which no run has established and which any pitch-aware fix depends on.
            public bool  HasMotion;      // false when the controller could not be read
            public float McPitchAngle;   // MotionController.PitchAngle
            public float McPitchRate;    // MotionController.PitchRate
            public float McPitchError;   // MotionController.PitchError
            public bool  McDirectPoint;  // MotionController._directPoint

            // D12 of docs/plans/open/anchor-liveness.md. The 2026-09-07f trace shows the aircraft
            // level at its commanded mach and then pitching up 38 degrees IMMEDIATELY AFTER its first
            // round, which is a manoeuvre the engagement triggers rather than a climb toward a cruise
            // altitude. The user reports that at 20,000ft it does none of this and fires both rounds
            // together. These two fields are what the game's own attack state machine drives, so they
            // are what says whether the climb is commanded and by what.
            public float DesiredAltFt;   // ObjectBase.DesiredAltitude, the commanded altitude
            public bool  InDiveAttack;   // Aircraft._inDiveAttackMode
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
            float sos = Atmosphere.SpeedOfSound(altU * GameUnits.MetersPerUnity);
            ISpeedCommand sc = air.SpeedCommand?.Value;

            s.AltFt = altU * GameUnits.UnityToFeet;
            s.GateFt = hi * GameUnits.UnityToFeet;
            s.TasKn = Mathf.Abs(air._velocityInKnots);
            s.Mach = (sos > 0f) ? s.TasKn * GameUnits.KnotsToMs / sos : 0f;
            s.CmdMach = (sc != null) ? sc.SpeedInMach : float.NaN;
            s.PitchDeg = -GameMath.PitchDeg(air.transform);
            s.AboveGate = altU > hi;
            s.RollDeg = RollDeg(air);
            s.HeadingDeg = air.transform.eulerAngles.y;
            // Guarded rather than trusted: Motioncontroller is swapped out at runtime
            // (SetFlightPhysicsModel) and is null on an aircraft that is not yet flying.
            try
            {
                MotionController mc = air.Motioncontroller;
                if (mc != null)
                {
                    s.HasMotion = true;
                    s.McPitchAngle = mc.PitchAngle;
                    s.McPitchRate = mc.PitchRate;
                    s.McPitchError = mc.PitchError;
                    s.McDirectPoint = mc._directPoint;
                }
            }
            catch { s.HasMotion = false; }
            try
            {
                s.DesiredAltFt = air.DesiredAltitude.Value * GameUnits.UnityToFeet;
                s.InDiveAttack = air._inDiveAttackMode;
            }
            catch { s.DesiredAltFt = float.NaN; }
            s.BearingDeg = float.NaN;
            s.OffBoreDeg = float.NaN;
            s.RangeKm = float.NaN;
            return true;
        }

        /// <summary>
        /// Fills the target-relative half of <see cref="AirSnapshot"/>, which the snapshot itself
        /// cannot do because it does not take a target. Separate so the existing single-argument
        /// callers keep working and pay nothing.
        /// </summary>
        internal static void AddTargetGeometry(ref AirSnapshot s, ObjectBase unit, ObjectBase target)
        {
            if (unit == null || target == null || target.IsDestroyed
                || unit.transform == null || target.transform == null) return;
            Vector3 to = target.transform.position - unit.transform.position;
            s.RangeKm = to.magnitude * GameUnits.MetersPerUnity / 1000f;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            if (flat.sqrMagnitude <= 1e-8f) return;
            s.BearingDeg = Quaternion.LookRotation(flat).eulerAngles.y;
            float rel = s.BearingDeg - s.HeadingDeg;
            while (rel > 180f) rel -= 360f;
            while (rel < -180f) rel += 360f;
            s.OffBoreDeg = rel;
        }

        /// <summary>Bank angle, signed, positive with the right wing down.</summary>
        private static float RollDeg(Aircraft air)
        {
            float z = air.transform.localEulerAngles.z;
            return (z > 180f) ? z - 360f : z;
        }

        /// <summary>Held Mach: the commanded value where there is one, else what it is doing now.</summary>
        private static float MachOf(Aircraft air)
        {
            ISpeedCommand sc = air.SpeedCommand?.Value;
            if (sc != null && sc.SpeedInMach > 0.01f) return sc.SpeedInMach;
            float sos = Atmosphere.SpeedOfSound(air.transform.position.y * GameUnits.MetersPerUnity);
            return (sos > 0f) ? Mathf.Abs(air._velocityInKnots) * GameUnits.KnotsToMs / sos : 0f;
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
