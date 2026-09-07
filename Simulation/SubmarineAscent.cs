using System;
using System.Text;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// How long a submarine needs to reach the depth its weapon can launch from, by stepping the
    /// game's own vertical model. Two mechanisms (ballast and dive planes) are mutually exclusive;
    /// the interlock between them changes the answer several-fold. Pure arithmetic, no Unity access.
    /// See docs/model/07-diagnostics.md §7.6 for equations, measurement evidence, and limits.
    /// </summary>
    internal static class SubmarineAscent
    {
        private const float StepSim = 0.1f;             // matches FlightTime's IntegrationStepSim
        private const int MaxSteps = 12000;             // 20 min of sim at StepSim; a real ascent is ~1 min

        // Interlock thresholds, from applyDepth. Named rather than inlined because the whole model
        // turns on them: above BOTH, ballast is zeroed and the boat is on its planes alone.
        private const float InterlockNormSpeed = 0.1f;
        private const float InterlockPitchDeg = 5f;

        // No-progress bail. A boat parked at a depth it will not leave (the launcher raises it only
        // to the weapon's own ceiling, which can still be above the depth a guidance radar mast
        // needs) never arrives, and the honest answer is "never", not a large number.
        private const float MinProgressUnity = 1e-5f;
        private const int NoProgressSteps = 50;         // 5s of sim making no headway

        /// <summary>Seed for one ascent. Plain data: no Unity types, so the loop is testable.</summary>
        internal struct State
        {
            public float DepthU;          // current, unity units, positive down
            public float TargetDepthU;    // where the launcher is taking it, same convention
            public float PitchDeg;        // current hull pitch, positive nose-down
            public float BallastRateU;    // _currentDepthChangeUsingTanks, unity/s, positive down
            public float SpeedKn;         // held constant for the ascent
            public float MaxSpeedKn;      // _maxForwardVelocitySubmergedInKnots
            public float MaxPitchDeg;     // _maxPitchAngle
            public float PitchRateDeg;    // _pitchChangeRate, deg/s
            public float BallastAccelU;   // _ballastTankChangeRate, unity/s^2
            public float MaxBallastU;     // _maxDepthChangeUsingTanks, unity/s
        }

        /// <summary>
        /// Seconds to reach <see cref="State.TargetDepthU"/>. False when the boat makes no headway
        /// (already the answer for a depth-deadlocked order) or the seed is unusable.
        /// <paramref name="trace"/> receives a sampled profile for the asc-sim diagnostic when a
        /// builder is supplied; pass null on the hot path.
        /// </summary>
        internal static bool TryEstimate(State s, out float seconds, StringBuilder trace)
        {
            seconds = 0f;
            if (!GameMath.IsFinite(s.DepthU) || !GameMath.IsFinite(s.TargetDepthU) || s.MaxSpeedKn <= 0f ||
                s.PitchRateDeg <= 0f || !GameMath.IsFinite(s.SpeedKn)) return false;

            float toGo = s.DepthU - s.TargetDepthU;
            if (toGo <= 0f) { seconds = 0f; return true; }   // already shallow enough

            float depth = s.DepthU, pitch = s.PitchDeg, ballast = s.BallastRateU;
            float normSpd = Math.Abs(s.SpeedKn) / s.MaxSpeedKn;
            float speedU = s.SpeedKn * GameUnits.KnotsToUnityPerSecond;
            float t = 0f, nextSample = 0f;
            int stalled = 0;

            for (int i = 0; i < MaxSteps; i++)
            {
                float err = s.TargetDepthU - depth;      // negative while ascending

                // Ballast: target rate IS the depth error, clamped, then ramped toward.
                float ballTgt = Mathf.Clamp(err, -s.MaxBallastU, s.MaxBallastU);
                if (ballast < ballTgt) ballast = Math.Min(ballast + s.BallastAccelU * StepSim, ballTgt);
                else if (ballast > ballTgt) ballast = Math.Max(ballast - s.BallastAccelU * StepSim, ballTgt);

                // Dive planes: pitch authority scales with speed AND collapses near the surface.
                float depthFac = Mathf.Clamp01(2f * depth);
                float pitchLimit = s.MaxPitchDeg * depthFac;
                float tgtPitch = normSpd * Mathf.Clamp(s.MaxPitchDeg * err, -pitchLimit, pitchLimit);
                if (pitch < tgtPitch) pitch = Math.Min(pitch + s.PitchRateDeg * StepSim, tgtPitch);
                else if (pitch > tgtPitch) pitch = Math.Max(pitch - s.PitchRateDeg * StepSim, tgtPitch);

                // The interlock: under way and pitched, the boat flies its planes and blows no tanks.
                //
                // This ZEROES THE STATE, not just this step's contribution, exactly as the game does
                // (`_currentDepthChangeUsingTanks = 0f`). The distinction is worth a comment because
                // getting it wrong is invisible in steady state and costs ~10% on the total: with the
                // state left charging in the background, ballast is released fully wound up at its
                // 5 ft/s cap the instant pitch drops below the threshold near the target, and the
                // boat finishes far too fast. It must ramp again from zero at _ballastTankChangeRate,
                // which on a deep run is a minute of the approach. Measured against 13 real
                // manoeuvres this single behaviour moved mean error from 4.5% to 1.4%.
                if (normSpd > InterlockNormSpeed && Math.Abs(pitch) > InterlockPitchDeg) ballast = 0f;

                float rate = ballast + speedU * (float)Math.Sin(pitch * Math.PI / 180.0);
                float step = rate * StepSim;
                depth += step;
                t += StepSim;

                if (trace != null && t >= nextSample)
                {
                    nextSample += TelemetryCadence.SampleIntervalSim;
                    trace.Append($" t+{t:0}s {depth * GameUnits.UnityToFeet:0}ft p{pitch:0.0} " +
                                 $"r{-rate * GameUnits.UnityToFeet:0.00}ft/s{(ballast == 0f ? " planes" : " tanks")};");
                }

                if (depth <= s.TargetDepthU) { seconds = t; return true; }

                // Ascending means depth decreasing, so progress is a negative step.
                if (step > -MinProgressUnity) { if (++stalled >= NoProgressSteps) return false; }
                else stalled = 0;
            }
            return false;   // ran out of steps: treat as unreachable rather than guessing
        }

    }
}
