using System.Text;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// How long a shooter needs before it can fire at all, as opposed to how long its round then flies: a
    /// surface ship is ready where it stands, a submerged submarine must rise to launch depth
    /// (<see cref="SubmarineFacts.EstimateLaunchDelay"/>, <see cref="SubmarineAscent"/>), and an aircraft
    /// must descend into its launch band (<see cref="AircraftDelay"/>, <see cref="AircraftDescent"/>). The
    /// coordinator needs one number for all three, because the delay belongs in the enroute estimate behind
    /// anchor selection and follower release.
    ///
    /// The game runs both through the same engage-state machinery: <c>EngageState.LauncherTooHigh</c> from
    /// <c>_ap._minDepthUnity</c> beside the submarine's <c>LauncherTooLow</c>, with
    /// <c>AmmunitionParameters._launchAltitudesInUnity</c> as the band <c>WeaponSystemHardpoint</c> tests. An aircraft
    /// that is too low is not modelled; see <see cref="AircraftDelay"/>.
    /// </summary>
    internal static class LaunchEnvelope
    {
        /// <summary>
        /// How far apart the game considers a shooter and its target when testing launch range, in Unity
        /// units to compare straight against <c>_launchRangesInUnity</c>. It must match each weapon system:
        /// <c>WeaponSystemHardpoint</c> (aircraft) projects onto the horizontal plane,
        /// <c>WeaponSystemLauncher</c> (ships and submarines) uses the slant distance. They differ by about
        /// 0.4% at a 30 nm minimum and 30,000 ft, which matters exactly where a range test is read.
        /// </summary>
        internal static float LaunchDistanceUnity(ObjectBase unit, ObjectBase target)
        {
            if (unit == null || target == null) return 0f;
            Vector3 d = target.transform.position - unit.transform.position;
            if (unit.IsAirUnit) d.y = 0f;
            return d.magnitude;
        }

        /// <summary>
        /// The weapon's minimum launch range in Unity units, or 0 when it has none (290 of the game's 437
        /// ammunition files carry one, up to 100 nm for AS-4).
        /// </summary>
        internal static float MinRangeUnity(AmmunitionParameters ap)
            => ap == null ? 0f : Mathf.Max(0f, ap._launchRangesInUnity.x);

        /// <summary>
        /// True when the target is inside the weapon's minimum launch range, so the game will neither fire
        /// this order nor give it up.
        ///
        /// <para>Every platform refuses: the aircraft hardpoint sets <c>EngageState.TargetTooClose</c>
        /// (WeaponSystemHardpoint.cs:1217), as does a submarine below launch depth
        /// (WeaponSystemLauncher.cs:405) and a surface launcher against any target type
        /// (WeaponSystemLauncher.cs:495-501). The abort path (WeaponSystemLauncher.cs:535-542) is gated on
        /// <c>!IsPlayerObject</c>, so a player's order holds forever, and the
        /// launcher reaches <c>AligningLauncher</c> before the range test, so the ship turns to unmask first.</para>
        ///
        /// <para>So it is refused rather than warned about: the order cannot produce a round, makes the ship
        /// manoeuvre for nothing, and as anchor would time the whole strike against a launch that never comes.</para>
        /// </summary>
        internal static bool IsInsideMinimumRange(ObjectBase unit, AmmunitionParameters ap,
                                                  ObjectBase target, out float rangeNm, out float minNm)
        {
            rangeNm = 0f; minNm = 0f;
            if (unit == null || ap == null || target == null) return false;
            float minU = MinRangeUnity(ap);
            if (minU <= 0f) return false;
            float distU = LaunchDistanceUnity(unit, target);
            rangeNm = distU * GameUnits.UnityToNm;
            minNm = minU * GameUnits.UnityToNm;
            return distU <= minU;
        }

        /// <summary>Minimum-range test from an ammunition id, for callers that hold one.</summary>
        internal static bool IsInsideMinimumRange(ObjectBase unit, string ammoId, ObjectBase target,
                                                  out float rangeNm, out float minNm)
        {
            rangeNm = 0f; minNm = 0f;
            if (unit == null || ammoId == null) return false;
            AmmunitionParameters ap;
            try { ap = unit.getAmmunitionByName(ammoId)?._ap; } catch { return false; }
            return IsInsideMinimumRange(unit, ap, target, out rangeNm, out minNm);
        }

        /// <summary>
        /// Seconds an aircraft needs to descend into its weapon's launch-altitude band; 0 when already inside
        /// it, the usual case. Only above the band is modelled: a too-low aircraft must climb, which is
        /// energy-limited through <c>GetMaxClimbPitch</c> with a private <c>_dragPower</c>, so it returns 0
        /// and the coordinator's launch-state hold treats it like any shooter that has not started firing.
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
                // Already able to fire. Say so: a silent 0 is indistinguishable from a model that declined.
                trace?.Append($" in band: {alt * GameUnits.UnityToFeet:0}ft is at or below " +
                              $"gate {hi * GameUnits.UnityToFeet:0}ft, no descent needed |");
                return 0f;
            }

            float mach = MachOf(air);
            if (mach <= 0f) return 0f;

            // What the aircraft flies to. The hardpoint clamps this to the launch band only outside
            // EngageSurfaceContact (WeaponSystemHardpoint.cs:1147), so on an anti-ship strike it is the
            // attack altitude, which the pitch taper is computed against. Assumed, not read from source: an
            // aircraft ordered onto a surface contact ends at or below the band's lower edge, so the taper
            // stays saturated through the gate. At commit the commanded altitude is still the cruise value,
            // above the gate; only the taper near the gate is sensitive to this, so the band floor is a safe proxy.
            float cmdAlt = Mathf.Min(air.DesiredAltitude.Value, lo);

            AircraftDescent.State st = new AircraftDescent.State
            {
                AltitudeU = alt,
                GateAltU = hi,
                CommandAltU = cmdAlt,
                Mach = mach,
                PitchDeg = -GameMath.PitchDeg(air.transform),            // euler is nose-down positive; the model is climb positive
                PitchRateDeg = p._maxTurnRates.x,
                // The combat pair, even though air._inCombat reads false here: GetDesiredPitch selects on
                // _inCombat, and the order being planned is what puts the aircraft in combat, so it descends
                // at the combat limit. A field read at commit does not describe the descent.
                DescentLimitDeg = Mathf.Abs(p._maxCombatDescentPitch),
                ThresholdAltU = p._inCombatThresholdAltitude,
                MinG = p._minG,
                MaxG = p._maxG,
            };
            // Seed description in the submarine's shape, so envelope-sim reads alike for both. inCombat is
            // logged because GetDesiredPitch selects both the pitch limit and the taper threshold on it, and
            // the seed assumes combat.
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
        /// Live vertical state of an aircraft shooter for the envelope-track trace: the ground truth the
        /// prediction is scored against, as <see cref="SubmarineFacts.Snapshot"/> is for a boat. Mach is
        /// carried beside TAS because the model holds Mach constant.
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

            // Attitude and target geometry, so a launcher waiting in WaitingForRoll can be checked against the roll.
            public float RollDeg;      // bank, positive = right wing down
            public float HeadingDeg;   // true heading
            public float BearingDeg;   // bearing to target, same reference as HeadingDeg
            public float OffBoreDeg;   // target relative to the nose, signed, -180..180
            public float RangeKm;      // slant range to target

            // The game's own pitch terms from MotionController, the values EngageSurfaceContact tests before
            // the aircraft may fire; logged beside PitchDeg to pin down the sign convention between the two.
            public bool  HasMotion;      // false when the controller could not be read
            public float McPitchAngle;   // MotionController.PitchAngle
            public float McPitchRate;    // MotionController.PitchRate
            public float McPitchError;   // MotionController.PitchError
            public bool  McDirectPoint;  // MotionController._directPoint

            // What the game's attack state machine drives, to tell whether a climb after a release is
            // commanded and by what.
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
            // Guarded: MotionController is swapped at runtime (SetFlightPhysicsModel) and null on an aircraft
            // not yet flying.
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
        /// Fills the target-relative half of <see cref="AirSnapshot"/>, which the snapshot cannot, since it
        /// takes no target.
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
        /// Seconds from the fire order until this shooter can put its first round away: 0 when ready now, the
        /// common case and the only exact answer. Also 0 when it can never get there, so 0 is not "ready"
        /// without the engage state; the no-launch hold in <see cref="Coordinator"/> ends an order that will
        /// never fire.
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
