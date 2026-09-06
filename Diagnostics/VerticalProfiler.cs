using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Data capture for modelling how a platform changes depth or altitude. Off by default;
    /// a research tool, not a runtime diagnostic. Samples every submarine and aircraft whenever
    /// commanded altitude differs from actual. See docs/model/07-diagnostics.md §7.7 for design.
    /// </summary>
    internal static class VerticalProfiler
    {
        internal static bool Enabled;

        private const float SampleIntervalSim = 1f;
        private const float SettleFeet = 3f;      // commanded vs actual, below this it has arrived
        private const int SettleSamples = 3;      // consecutive settled samples before closing a run
        private const float UnityToFeet = 220.47266f;

        // Opening a run and closing one are different questions, so they use different thresholds.
        // Aircraft hold altitude to within a few feet and never sit exactly on the commanded value,
        // so a 3 ft opening test turned station-keeping jitter into 321 junk blocks out of 330 in
        // the first session. A run has to be worth measuring before it is worth opening.
        private const float OpenFeetAir = 50f;
        private const float OpenFeetSub = 5f;

        // Position snaps read as impossible speeds: the game teleports a unit on spawn and on
        // altitude correction (`transform.position = ...DesiredAltitude`), and the first session
        // logged peaks of 1372 and 1612 ft/s, above the airframes' own declared maxima. Samples
        // beyond a plausible ceiling are dropped from the rate statistics and COUNTED, so a
        // snap-heavy run is visibly suspect rather than quietly biased.
        private const float SnapMarginFactor = 1.5f;
        private const float SubMaxPlausibleFtPerSec = 25f;

        private sealed class Track
        {
            public int Run;
            public bool Active;
            public float StartSim, StartFt, LastSim, LastFt;
            public float PeakRate;
            public int Settled;
            public bool CardLogged;
            public float TargetFt;    // the commanded value this run is for; a change ends the run
            public int Snaps;
            public float MaxCompression;   // highest time compression seen during the run
        }

        private static readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private static float _nextScanSim = float.NegativeInfinity;
        private static int _runCounter;

        internal static void Reset() { _tracks.Clear(); _runCounter = 0; _nextScanSim = float.NegativeInfinity; }

        internal static void Tick(float simNow)
        {
            if (!Enabled) return;
            if (simNow - _nextScanSim < 0f) return;
            _nextScanSim = simNow + SampleIntervalSim;

            var mgr = Singleton<ObjectsManager>.Instance;
            List<ObjectBase> units = mgr?._listOfAllUnits;
            if (units == null) return;

            for (int i = 0; i < units.Count; i++)
            {
                ObjectBase u = units[i];
                if (u == null || u.IsDestroyed) continue;
                // Helicopter derives from ObjectBase, NOT Aircraft, so it needs naming explicitly
                // or it is silently skipped. Included because dipping and torpedo runs are vertical
                // behaviour too, and the card labels the type so the traces stay separable.
                bool isSub = u is Submarine;
                if (!isSub && !(u is Aircraft) && !(u is Helicopter)) continue;
                Sample(u, isSub, simNow);
            }
        }

        private static void Sample(ObjectBase u, bool isSub, float simNow)
        {
            // Depth is positive-down for boats, altitude positive-up for aircraft. Keep each in the
            // sign the platform is normally discussed in so the log reads naturally.
            float nowFt = (isSub ? -u.transform.position.y : u.transform.position.y) * UnityToFeet;
            float cmdFt = (isSub ? -u.DesiredAltitude.Value : u.DesiredAltitude.Value) * UnityToFeet;

            int id = u.GetInstanceID();
            if (!_tracks.TryGetValue(id, out Track t)) { t = new Track(); _tracks[id] = t; }

            bool moving = Mathf.Abs(cmdFt - nowFt) > SettleFeet;

            // A commanded change mid-run makes two manoeuvres look like one. In the first session a
            // surface-then-dive pair was logged as a single 50->300 ft run and read as a 44% model
            // error that did not exist. Close the run and let the next sample open a fresh one.
            if (t.Active && Mathf.Abs(cmdFt - t.TargetFt) > SettleFeet)
            {
                Close(u, t, isSub, simNow, nowFt, "target changed");
                return;
            }

            if (!t.Active)
            {
                float openFt = isSub ? OpenFeetSub : OpenFeetAir;
                if (Mathf.Abs(cmdFt - nowFt) <= openFt) return;
                if (!t.CardLogged) { t.CardLogged = true; LogCard(u, isSub); }
                t.Active = true;
                t.Run = ++_runCounter;
                t.StartSim = simNow; t.StartFt = nowFt;
                t.LastSim = simNow; t.LastFt = nowFt;
                t.PeakRate = 0f; t.Settled = 0; t.Snaps = 0; t.TargetFt = cmdFt;
                t.MaxCompression = 0f;
            }

            float dt = Mathf.Max(0.001f, simNow - t.LastSim);
            float rate = (nowFt - t.LastFt) / dt;                 // + = deeper / higher
            // Physics fidelity depends on time compression: Time.timeScale is pinned at
            // GameTime._physicsTimeScaleCap (10), so above 10x sim time outruns the physics steps and
            // the submarine code takes explicit approximation branches. A run is only comparable with
            // another at the same compression, so record the worst seen rather than trusting memory.
            if (GameTime.TimeCompression > t.MaxCompression) t.MaxCompression = GameTime.TimeCompression;
            bool snap = Mathf.Abs(rate) > PlausibleRateFtPerSec(u, isSub);
            if (snap) t.Snaps++;
            else if (Mathf.Abs(rate) > Mathf.Abs(t.PeakRate)) t.PeakRate = rate;
            t.LastSim = simNow; t.LastFt = nowFt;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] vprof #{t.Run} {LaunchDiagnostics.SafeName(u)}: t+{simNow - t.StartSim:0.0}s " +
                $"{(isSub ? "depth" : "alt")} {nowFt:0}ft -> {cmdFt:0}ft, rate {rate:+0.00;-0.00}ft/s, " +
                $"pitch {PitchDeg(u):0.0}, spd {u._velocityInKnots:0.0}kn" +
                (snap ? " SNAP" : "") + Extra(u, isSub));

            if (!moving && ++t.Settled >= SettleSamples) Close(u, t, isSub, simNow, nowFt, "arrived");
            else if (moving) t.Settled = 0;
        }

        /// <summary>
        /// Ends a run. Reports direction and endpoints as well as distance: without them a climb and
        /// a descent are indistinguishable in the summary, and they are governed differently.
        /// </summary>
        private static void Close(ObjectBase u, Track t, bool isSub, float simNow, float nowFt, string why)
        {
            t.Active = false;
            float span = simNow - t.StartSim;
            float moved = nowFt - t.StartFt;
            // Sign conventions differ by platform: depth is positive DOWN, altitude positive UP.
            // Applying one to the other inverts the label, which it did on the first aircraft run.
            string dir = isSub ? (moved > 0f ? "dive" : "ascend")
                               : (moved > 0f ? "climb" : "descend");
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] vprof #{t.Run} {LaunchDiagnostics.SafeName(u)}: DONE {dir} " +
                $"{t.StartFt:0}ft -> {nowFt:0}ft ({Mathf.Abs(moved):0}ft) in {span:0.0}s, " +
                $"mean {(span > 0f ? Mathf.Abs(moved) / span : 0f):0.00}ft/s, " +
                $"peak {Mathf.Abs(t.PeakRate):0.00}ft/s, {why}, " +
                $"maxCompression {t.MaxCompression:0.#}x" +
                (t.MaxCompression > GameTime._physicsTimeScaleCap ? " APPROXIMATED" : "") +
                (t.Snaps > 0 ? $", {t.Snaps} SNAP sample(s) excluded" : ""));
        }

        /// <summary>Ceiling above which a sample is a position snap rather than motion.</summary>
        private static float PlausibleRateFtPerSec(ObjectBase u, bool isSub)
        {
            if (isSub) return SubMaxPlausibleFtPerSec;
            if (u is Aircraft air && air.Ap != null && air.Ap._maxClimbRate > 0f)
                return air.Ap._maxClimbRate * 0.0148809375f * UnityToFeet * SnapMarginFactor;
            return float.MaxValue;
        }

        /// <summary>Hull pitch, positive nose-down, matching the sign the submarine model uses.</summary>
        private static float PitchDeg(ObjectBase u)
        {
            if (u is Submarine sub) return sub.getPitch();
            float x = u.transform.localEulerAngles.x;
            return (x > 180f) ? x - 360f : x;
        }

        private static string Extra(ObjectBase u, bool isSub)
        {
            if (isSub && u is Submarine sub)
                return $", tanks {sub._currentDepthChangeUsingTanks * UnityToFeet:+0.00;-0.00}ft/s";
            if (!isSub && u is Aircraft air && air.Ap != null)
            {
                // The game's atmosphere and drag helpers take metres and m/s. InducedDrag is
                // 2W^2/(rho*v^2*pi*b^2*0.75), which is only dimensionally consistent in SI, so the
                // knots conversion below is required rather than cosmetic.
                const float FeetToMetres = 0.3048f, KnotsToMetresPerSec = 0.514444f;
                float altM = u.transform.position.y * UnityToFeet * FeetToMetres;
                float rho = Atmosphere.Density(altM) / Atmosphere.Density(0f);
                string lapse = TryUnary(_thrustLapse, air.Ap, altM, out float tl) ? $", lapse {tl:0.000}" : "";
                string ind = TryBinary(_inducedDrag, air.Ap, u._velocityInKnots * KnotsToMetresPerSec, altM, out float dr)
                             ? $", inducedDrag {dr:0.0}" : "";
                // An aircraft holds a commanded MACH, so true airspeed in knots falls with
                // altitude purely because the speed of sound does, with no change of throttle.
                // Logging Mach alongside knots is what separates that from a real deceleration:
                // measured climbs held Mach to within 3% while TAS fell 4-7%.
                float sos = Atmosphere.SpeedOfSound(altM) / KnotsToMetresPerSec;   // knots
                float mach = (sos > 0f) ? u._velocityInKnots / sos : 0f;
                string cmd = "";
                ISpeedCommand sc = u.SpeedCommand?.Value;
                // CommandSpeedInKnots reads NaN on these airframes, so only Mach is reported: it is
                // the quantity actually held anyway, and TAS follows from it and the atmosphere.
                if (sc != null) cmd = $", cmdMach {sc.SpeedInMach:0.00}";
                // The game's OWN energy solution for the climb angle. Logging it beside the observed
                // pitch turns the model check into a direct comparison with nothing fitted: on a
                // climb the two should agree. Descent is NOT this equation (see the card), it is
                // capped by the descent-pitch parameters, which is why descents run about twice
                // the climb rate.
                // desiredPitch is the game's COMPLETE answer (GetDesiredPitch): the applicable pitch
                // limit multiplied by a proximity factor that tapers the angle as the aircraft nears
                // its commanded altitude. maxClimbPitch is only the energy sub-term inside it.
                // Logging both shows whether a discrepancy is the energy model or the taper.
                string pitches = "";
                if (TryDesiredPitch(air, out float dp)) pitches += $", desiredPitch {dp:0.0}";
                if (TryMaxClimbPitch(air, out float mcp)) pitches += $", maxClimbPitch {mcp:0.0}";
                return $", mach {mach:0.000}, sos {sos:0}kn{cmd}{pitches}, mass {air.Ap.Mass:0}, " +
                       $"rho {rho:0.000}{lapse}{ind}";
            }
            return "";
        }

        // ThrustLapse and InducedDrag exist ONLY on the beta branch; Atmosphere.Density and
        // SpeedOfSound are on both. Reached by cached reflection so the public build still runs and
        // simply omits those two columns, the same rule the guidance-channel members follow.
        private static bool _energyResolved;
        private static MethodInfo _thrustLapse, _inducedDrag;
        private static bool _climbPitchResolved;
        private static MethodInfo _maxClimbPitch;
        private static int _maxClimbPitchArgs;
        private static bool _desiredPitchResolved;
        private static MethodInfo _desiredPitch;

        /// <summary>
        /// The game's own commanded pitch, <c>GetDesiredPitch()</c>: the applicable limit (energy for
        /// a climb, a parameter for a descent) times a proximity taper. Public and argument-free on
        /// both branches, so this is the whole vertical answer with nothing reconstructed.
        /// </summary>
        private static bool TryDesiredPitch(Aircraft air, out float pitch)
        {
            pitch = 0f;
            object mc = air.Motioncontroller;
            if (mc == null) return false;
            if (!_desiredPitchResolved)
            {
                _desiredPitchResolved = true;
                _desiredPitch = mc.GetType().GetMethod("GetDesiredPitch",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            if (_desiredPitch == null) return false;
            try { pitch = (float)_desiredPitch.Invoke(mc, new object[0]); return true; }
            catch { return false; }
        }

        /// <summary>
        /// The game's computed maximum climb angle. Beta declares
        /// <c>GetMaxClimbPitch(float speedTradeSecs, bool applyClimbRateCap)</c> and the public branch
        /// declares <c>GetMaxClimbPitch()</c>, so both arities are resolved and whichever exists is
        /// used. Lives on FixedWingFlightPhysics, reached through the public Motioncontroller.
        /// </summary>
        private static bool TryMaxClimbPitch(Aircraft air, out float pitch)
        {
            pitch = 0f;
            object mc = air.Motioncontroller;
            if (mc == null) return false;
            if (!_climbPitchResolved)
            {
                _climbPitchResolved = true;
                const BindingFlags I = BindingFlags.Public | BindingFlags.Instance;
                Type t = mc.GetType();
                _maxClimbPitch = t.GetMethod("GetMaxClimbPitch", I, null, new[] { typeof(float), typeof(bool) }, null);
                _maxClimbPitchArgs = 2;
                if (_maxClimbPitch == null)
                {
                    _maxClimbPitch = t.GetMethod("GetMaxClimbPitch", I, null, Type.EmptyTypes, null);
                    _maxClimbPitchArgs = 0;
                }
            }
            if (_maxClimbPitch == null) return false;
            try
            {
                // The real call site is GetMaxClimbPitch(flag ? 30f : 180f, !flag) with
                // flag = _avoidingTerrain || _inCombat (FixedWingFlightPhysics.cs:774). Passing 0
                // for speedTradeSecs suppresses the speed-trade term entirely and reported an angle
                // 4-5 deg shallower than the aircraft actually flew. These are the out-of-combat
                // arguments, which is what a profiling run is.
                object[] args = _maxClimbPitchArgs == 2 ? new object[] { 180f, true } : new object[0];
                pitch = (float)_maxClimbPitch.Invoke(mc, args);
                return true;
            }
            catch { return false; }
        }

        private static void ResolveEnergy()
        {
            _energyResolved = true;
            const BindingFlags I = BindingFlags.Public | BindingFlags.Instance;
            Type t = typeof(AircraftParameters);
            _thrustLapse = t.GetMethod("ThrustLapse", I, null, new[] { typeof(float) }, null);
            _inducedDrag = t.GetMethod("InducedDrag", I, null, new[] { typeof(float), typeof(float) }, null);
        }

        private static bool TryUnary(MethodInfo m, object target, float a, out float result)
        {
            result = 0f;
            if (!_energyResolved) ResolveEnergy();
            if (m == null) return false;
            try { result = (float)m.Invoke(target, new object[] { a }); return true; }
            catch { return false; }
        }

        private static bool TryBinary(MethodInfo m, object target, float a, float b, out float result)
        {
            result = 0f;
            if (!_energyResolved) ResolveEnergy();
            if (m == null) return false;
            try { result = (float)m.Invoke(target, new object[] { a, b }); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Static parameters, once per unit per mission. Saves digging the INI, and records exactly
        /// the constants any candidate model has to be expressed in.
        /// </summary>
        private static void LogCard(ObjectBase u, bool isSub)
        {
            if (isSub && u is Submarine sub && sub.SP != null)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] vprof-card {LaunchDiagnostics.SafeName(u)} SUB: " +
                    $"periscope {sub.SP._periscopeDepth * UnityToFeet:0}ft, " +
                    $"maxTanks {sub.SP._maxDepthChangeUsingTanks * UnityToFeet:0.00}ft/s, " +
                    $"ballastAccel {sub.SP._ballastTankChangeRate * UnityToFeet:0.0000}ft/s2, " +
                    $"maxPitch {sub.SP._maxPitchAngle:0.0}deg, pitchRate {sub.SP._pitchChangeRate:0.0}deg/s, " +
                    $"maxSpdSubmerged {sub.SP._maxForwardVelocitySubmergedInKnots:0.0}kn");
                return;
            }
            if (!isSub && u is Aircraft air && air.Ap != null)
            {
                AircraftParameters ap = air.Ap;
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] vprof-card {LaunchDiagnostics.SafeName(u)} AIR: " +
                    $"maxClimbRate {ap._maxClimbRate:0.00} ({ap._maxClimbRate * 0.0148809375f * UnityToFeet:0.0}ft/s), " +
                    $"engines {ap._engineCount}, maxWetThrust {ap._maxWetThrust:0}, mass {ap.Mass:0}, " +
                    $"presetAlts [{PresetFeet(ap)}], wingSpan {ap._wingSpan:0.0}, " +
                    // Descent is governed by these, NOT by the climb energy equation
                    // (FixedWingFlightPhysics.cs:760), which is why descents run far faster.
                    $"climbPitch {ap._maxOutOfCombatClimbPitch:0.0}/{ap._maxCombatClimbPitch:0.0} (cruise/combat), " +
                    $"thresholdAlt {ap._outOfCombatThresholdAltitude * UnityToFeet:0}/{ap._inCombatThresholdAltitude * UnityToFeet:0}ft, " +
                    $"G {ap._minG:0.0}..{ap._maxG:0.0}, " +
                    $"descentPitch {ap._maxOutOfCombatDescentPitch:0.0}/{ap._maxCombatDescentPitch:0.0} (cruise/combat), " +
                    $"physicsApproximation {GameTime.IsPhysicsApproximationEnabled()}");
                return;
            }
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] vprof-card {LaunchDiagnostics.SafeName(u)} HELO: " +
                $"physicsApproximation {GameTime.IsPhysicsApproximationEnabled()}");
        }

        private static string PresetFeet(AircraftParameters ap)
        {
            float[] p = ap._presetAltitudes;
            if (p == null || p.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append($"{p[i] * UnityToFeet:0}");
            }
            return sb.ToString();
        }
    }
}
