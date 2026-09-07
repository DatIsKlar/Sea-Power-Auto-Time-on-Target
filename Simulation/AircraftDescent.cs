using System;
using System.Text;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// How long an aircraft needs to reach a commanded altitude, by stepping the game's own pitch
    /// logic. Written for DESCENT, which is the direction a launch envelope needs: an aircraft above
    /// its weapon's launch band must come down before it can fire.
    ///
    /// The whole vertical answer is <c>FixedWingFlightPhysics.GetDesiredPitch()</c>, which is a
    /// LIMIT times a PROXIMITY TAPER:
    ///
    ///   taper = clamp(err / thresholdAltitude, -1, 1)          // err = commanded - current
    ///   limit = (descend) min(|_maxOutOfCombatDescentPitch|, maxDivePitchDeg)     // a parameter
    ///           (climb)   min(GetMaxClimbPitch(...), _maxOutOfCombatClimbPitch, 89)  // energy
    ///   pitch -> limit * taper, rate-limited by _maxTurnRates.x
    ///   verticalRate = TAS * sin(pitch)
    ///
    /// then tightened near the target by a G-limited pull-out term built from _maxG / _minG. Note
    /// the asymmetry: CLIMB is energy-limited (thrust, drag, air density) while DESCENT is merely
    /// parameter-limited, so descent is both the easier half and the one that matters here. Climb
    /// is not implemented; it needs GetMaxClimbPitch, whose _dragPower is private.
    ///
    /// TAS is derived from MACH, not held in knots. Aircraft hold a commanded Mach, so true airspeed
    /// changes with altitude purely because the speed of sound does: a measured descent held Mach
    /// 0.964 to 0.957 from 64k to 20k ft while TAS rose from 552 to 586 kn.
    ///
    /// ACCURACY: mean absolute error 5.2% over four measured descents on two airframes, 64k to 3k ft
    /// (submarine ascent, for comparison, is 1.4%). The residual is consistently positive, so this
    /// runs slightly slow. Do not tune it out; the remaining terms are known and unmodelled, chiefly
    /// that the game's pitch controller is a proportional loop rather than the rate limit used here.
    ///
    /// Pure arithmetic apart from the atmosphere lookups, matching <see cref="SubmarineAscent"/>.
    /// </summary>
    internal static class AircraftDescent
    {
        private const float StepSim = 0.05f;
        private const int MaxSteps = 20000;         // ~17 min of sim; a real descent is under a minute
        private const float PullOutGuardMetres = 5f;               // game's own |err| guard
        private const float MinPitchRateDeg = 0.1f;                // game's own turnSpeed.x guard

        internal struct State
        {
            public float AltitudeU;        // current, unity units, positive up
            // Two DIFFERENT altitudes, and conflating them was worth 14s on a 40s estimate.
            // GateAltU is the launch ceiling: a THRESHOLD to cross, after which the weapon can fire.
            // CommandAltU is what the aircraft is actually flying to, and it is the only thing
            // GetDesiredPitch tapers against. For an anti-ship strike the hardpoint explicitly does
            // NOT clamp the commanded altitude to the ceiling (WeaponSystemHardpoint.cs:1147, the
            // EngageSurfaceContact exclusion), so the aircraft descends through the ceiling at full
            // pitch instead of levelling onto it. Tapering against the ceiling invented a long
            // asymptotic settle that never happens.
            public float GateAltU;         // launch ceiling, the altitude to get below
            public float CommandAltU;      // the aircraft's own commanded altitude, drives the taper
            public float Mach;             // held constant; TAS follows the atmosphere
            public float PitchDeg;         // positive = climb, matching GetDesiredPitch
            public float PitchRateDeg;     // _maxTurnRates.x
            public float DescentLimitDeg;  // min(|_maxOutOfCombatDescentPitch|, maxDivePitchDeg), positive
            public float ThresholdAltU;    // _outOfCombatThresholdAltitude
            public float MinG, MaxG;
        }

        /// <summary>
        /// Seconds to reach <see cref="State.TargetAltU"/>. False when the seed is unusable or the
        /// aircraft makes no progress. Only descent is modelled: a climb request returns false rather
        /// than a wrong answer.
        /// </summary>
        internal static bool TryEstimate(State s, out float seconds) => TryEstimate(s, out seconds, null);

        /// <summary>
        /// As above, additionally writing a sampled profile into <paramref name="trace"/> for the
        /// envelope-sim diagnostic. Pass null on any path that runs per frame.
        /// </summary>
        internal static bool TryEstimate(State s, out float seconds, StringBuilder trace)
        {
            seconds = 0f;
            if (!GameMath.IsFinite(s.AltitudeU) || !GameMath.IsFinite(s.GateAltU) || !GameMath.IsFinite(s.CommandAltU) || s.Mach <= 0f ||
                s.PitchRateDeg <= 0f || s.DescentLimitDeg <= 0f || s.ThresholdAltU <= 0f) return false;
            if (s.GateAltU >= s.AltitudeU) return false;     // already inside the band
            // Commanded no lower than the ceiling: the aircraft is not descending far enough to get
            // under the gate on its own, so it never arrives. Same answer, and same meaning, as the
            // submarine parked above its launch depth.
            if (s.CommandAltU >= s.GateAltU) return false;

            float alt = s.AltitudeU, pitch = s.PitchDeg, t = 0f, nextSample = 0f;

            for (int i = 0; i < MaxSteps; i++)
            {
                // Arrival is crossing the GATE, not reaching the commanded altitude.
                if (alt <= s.GateAltU) { seconds = t; return true; }
                float err = s.CommandAltU - alt;               // negative while descending

                float taper = Mathf.Clamp(err / s.ThresholdAltU, -1f, 1f);
                float tasU = TrueAirspeedU(s.Mach, alt);
                float vsp = (float)Math.Sin(pitch * Math.PI / 180.0) * tasU;

                // G-limited pull-out: as the time left to the target approaches the time needed to
                // arrest the descent, the taper is overridden so the aircraft levels off early, and
                // past that it commands a climb. This is what stops it flying through the altitude.
                if (Math.Abs(vsp) > 1e-6f)
                {
                    float n6 = vsp * GameUnits.MetersPerUnity / 9.8f;                    // s to arrest at 1g
                    float n7 = (n6 < 0f) ? (s.MaxG - 1f) : (s.MinG - 1f);     // available g margin
                    float n8 = (Math.Abs(n7) > 0.001f) ? Math.Abs(n6 / n7) : float.PositiveInfinity;
                    if (s.PitchRateDeg > MinPitchRateDeg) n8 += Math.Abs(pitch) / s.PitchRateDeg;
                    float n9 = err / vsp;                                     // s to the target
                    if (n9 > 0.001f && Math.Abs(err) * GameUnits.MetersPerUnity > PullOutGuardMetres)
                    {
                        float b = Math.Sign(err) * Mathf.Clamp(2f - n8 / n9, -1f, 1f);
                        taper = (err < 0f) ? Math.Max(taper, b) : Math.Min(taper, b);
                    }
                }

                float want = s.DescentLimitDeg * taper;   // limit is positive; the taper carries the sign
                float step = s.PitchRateDeg * StepSim;
                pitch += Mathf.Clamp(want - pitch, -step, step);

                float tasNow = TrueAirspeedU(s.Mach, alt);
                float rateU = (float)Math.Sin(pitch * Math.PI / 180.0) * tasNow;
                alt += rateU * StepSim;
                t += StepSim;

                if (trace != null && t >= nextSample)
                {
                    nextSample += TelemetryCadence.SampleIntervalSim;
                    trace.Append($" t+{t:0}s {alt * GameUnits.UnityToFeet:0}ft p{pitch:0.0} " +
                                 $"r{rateU * GameUnits.UnityToFeet:0.00}ft/s tas {tasNow * GameUnits.UnityToFeet * 0.592484f:0}kn;");
                }
            }
            return false;
        }

        /// <summary>True airspeed in unity units per second, from the held Mach and the atmosphere.</summary>
        private static float TrueAirspeedU(float mach, float altU)
            => mach * Atmosphere.SpeedOfSound(altU * GameUnits.MetersPerUnity) / GameUnits.MetersPerUnity;
    }
}
