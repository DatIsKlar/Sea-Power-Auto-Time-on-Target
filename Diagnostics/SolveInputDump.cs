using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Writes one solver input to disk as plain text, so the same solve can be replayed outside the
    /// game.
    ///
    /// <see cref="FlightTime.SolveInput"/> is a pure value: the step loop reads no Unity API and no
    /// mutable game state, so a solve is fully determined by this struct plus the eight ammunition
    /// fields the loop and the game's thrust helper read. Capturing those is therefore enough to
    /// reproduce an estimate exactly, which is what makes an offline step-size sweep possible
    /// without re-flying anything.
    ///
    /// One file per round, under BepInEx/AutoTOT-solve/. Written only when TraceFlightModel is on,
    /// the same gate as the sim-track trace, and never on the normal path.
    /// </summary>
    internal static class SolveInputDump
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Monotonic, so two rounds of one ammunition launched on the same tick still get
        // separate entries.
        private static int _seq;

        internal static string Directory => Path.Combine(Paths.BepInExRootPath, "AutoTOT-solve");

        /// <summary>Writes one input and returns its key, or null if it could not be written.</summary>
        internal static string Write(string label, in FlightTime.SolveInput i, AmmunitionParameters ap)
        {
            try
            {
                // Keyed by label, wall clock and mission time. Label alone meant a second sortie
                // silently overwrote the first. Mission time alone is not enough either: firing one
                // round early in each mission, which is the cleanest way to gather data, reproduces
                // nearly the same mission time every run, so the corpus would hold one entry per
                // ammunition instead of one per shot. The wall clock is what actually separates two
                // sorties; mission time stays in the name because it is what the log lines show.
                // Label, wall clock, mission time and a sequence number. The label alone repeats
                // whenever the same ammunition is fired twice, and mission time alone repeats across
                // missions, since firing early in each one is the cleanest way to gather data. Both
                // ambiguities pair one round's outcome with another round's input, which is worse
                // than losing the round.
                string key = Sanitize(label) + "__" +
                             DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + "__t" +
                             GameClock.SimNow().ToString("0.0", Inv) + "__" +
                             System.Threading.Interlocked.Increment(ref _seq).ToString(Inv);
                System.IO.Directory.CreateDirectory(Directory);

                var b = new StringBuilder();
                b.AppendLine("# AutoTOT solver input. Replay with tools/lab.");
                b.AppendLine(F("Label", label));
                b.AppendLine(F("Key", key));
                b.AppendLine(F("SolvedAtSim", GameClock.SimNow()));

                // Ammunition. Two fields the step loop reads directly, and six more that
                // MissileSimulator.CalculateThrustOverTime reads out of the same object.
                b.AppendLine(F("Ammo.MinVelocity", ap.MinVelocity));
                b.AppendLine(F("Ammo.LiftFactor", ap.LiftFactor));
                b.AppendLine(F("Ammo.Acceleration", ap._acceleration));
                b.AppendLine(F("Ammo.AccelerationTime", ap._accelerationTime));
                b.AppendLine(F("Ammo.SustainerBurnAcceleration", ap._sustainerBurnAcceleration));
                b.AppendLine(F("Ammo.SustainerBurnTime", ap._sustainerBurnTime));
                b.AppendLine(F("Ammo.MaxVelocityInKnots", ap._maxVelocityInKnots));
                b.AppendLine(F("Ammo.Kinematics", ap.Kinematics.ToString()));

                b.AppendLine(F("AmmoLabel", i.AmmoLabel));
                b.AppendLine(F("BoostClimbDeg", i.BoostClimbDeg));
                b.AppendLine(F("DecelRate", i.DecelRate));
                b.AppendLine(F("DescentDeg", i.DescentDeg));
                b.AppendLine(F("DescentOnsetDeg", i.DescentOnsetDeg));
                b.AppendLine(F("DragFactor", i.DragFactor));
                b.AppendLine(F("CruiseAlt", i.CruiseAlt));
                b.AppendLine(F("FinalFlightAlt", i.FinalFlightAlt));
                b.AppendLine(F("FinalFlightDist", i.FinalFlightDist));
                b.AppendLine(F("FinalDist", i.FinalDist));
                b.AppendLine(F("FlatDistTotal", i.FlatDistTotal));
                b.AppendLine(F("InitialPhaseDur", i.InitialPhaseDur));
                b.AppendLine(F("IsAir", i.IsAir));
                b.AppendLine(F("IsHighBallisticLofter", i.IsHighBallisticLofter));
                b.AppendLine(F("IsTerminalLoft", i.IsTerminalLoft));
                b.AppendLine(F("LaunchPitch", i.LaunchPitch));
                b.AppendLine(F("LoftAlt", i.LoftAlt));
                b.AppendLine(F("LoftEntryDist", i.LoftEntryDist));
                b.AppendLine(F("LoftSpeedHoldDist", i.LoftSpeedHoldDist));
                b.AppendLine(F("LoftVelKn", i.LoftVelKn));
                b.AppendLine(F("Lofting", i.Lofting));
                b.AppendLine(F("MaxFlight", i.MaxFlight));
                b.AppendLine(F("MaxVelKn", i.MaxVelKn));
                b.AppendLine(F("NonKin", i.NonKin));
                b.AppendLine(F("TargetAlt0", i.TargetAlt0));
                b.AppendLine(F("TargetPos", i.TargetPos));
                b.AppendLine(F("TargetVel", i.TargetVel));
                b.AppendLine(F("TermAlt", i.TermAlt));
                b.AppendLine(F("TermDist", i.TermDist));
                b.AppendLine(F("TermVelKn", i.TermVelKn));
                b.AppendLine(F("TurnRate", i.TurnRate));
                b.AppendLine(F("ToBearingTurnRate", i.ToBearingTurnRate));
                b.AppendLine(F("TurnRateBase", i.TurnRateBase));
                b.AppendLine(F("TurnDerateThresholdKn", i.TurnDerateThresholdKn));
                b.AppendLine(F("AltLatchPhase", i.AltLatchPhase));
                b.AppendLine(F("AltLatched", i.AltLatched));
                b.AppendLine(F("Att", i.Att));
                b.AppendLine(F("LaunchHeading", i.LaunchHeading));
                b.AppendLine(F("NextSample", i.NextSample));
                b.AppendLine(F("Pos", i.Pos));
                b.AppendLine(F("PrevAltErr", i.PrevAltErr));
                b.AppendLine(F("PrevFlat", i.PrevFlat));
                b.AppendLine(F("PrevPitch", i.PrevPitch));
                b.AppendLine(F("T", i.T));
                b.AppendLine(F("TlGliding", i.TlGliding));
                b.AppendLine(F("VelKnots", i.VelKnots));
                b.AppendLine(F("AltNodes", i.AltNodes));

                string file = Path.Combine(Directory, key + ".solveinput");
                File.WriteAllText(file, b.ToString());
                Bootstrap.Log.LogInfo($"[AutoTOT] solve-dump {label}: wrote {file}");
                return key;
            }
            catch (Exception e)
            {
                ModLog.Warn("solve-input dump failed", e);
                return null;
            }
        }

        /// <summary>
        /// Records what actually happened to a round, beside the input it was solved from. An input
        /// with no outcome is not a test case; this is the half that makes the pair scoreable.
        /// </summary>
        internal static void WriteResult(string key, string ammoLabel, string targetName,
                                         float actualFlight, bool arrived, float finalRangeM,
                                         bool retargeted, float motorPerformance,
                                         float minCompression, float maxCompression,
                                         float minFlownStep, float maxFlownStep,
                                         float estAtLaunch, float launchTime,
                                         bool inGroup, bool groupLeader, int maxGroupSize,
                                         float peakSpeedKn, float peakAltU, float lastSpeedKn)
        {
            // A round that finished with no input to pair against is a hole in the corpus, and the
            // only way to notice is to say so. Silence here cost six of seven rounds once.
            if (string.IsNullOrEmpty(key))
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] solve-result {ammoLabel}: NOT RECORDED, no solver input was dumped " +
                    $"for this round (actual {actualFlight:0.0}s).");
                return;
            }
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var b = new StringBuilder();
                b.AppendLine("# AutoTOT flight outcome, paired with the .solveinput of the same name.");
                b.AppendLine(F("Key", key));
                b.AppendLine(F("Label", ammoLabel));
                b.AppendLine(F("Target", targetName));
                b.AppendLine(F("ActualFlight", actualFlight));
                b.AppendLine(F("Arrived", arrived));
                b.AppendLine(F("FinalRangeM", finalRangeM));
                b.AppendLine(F("Retargeted", retargeted));
                // The game rolls this per round (Missile.cs:62) and multiplies thrust by it. The
                // estimator cannot know it in advance and correctly assumes 1.0, but a replay can
                // use the recorded value to remove the +/-2% that otherwise swamps kinematic rounds.
                b.AppendLine(F("MotorPerformance", motorPerformance));
                // Above 10x the game enlarges its own physics step, so the round really does fly a
                // different trajectory and the actual time is not comparable.
                b.AppendLine(F("MinCompression", minCompression));
                b.AppendLine(F("MaxCompression", maxCompression));
                // What the round was actually integrated with. A replay at this step is comparing
                // like with like, whatever compression produced it.
                b.AppendLine(F("MinFlownStep", minFlownStep));
                b.AppendLine(F("MaxFlownStep", maxFlownStep));
                b.AppendLine(F("EstAtLaunch", estAtLaunch));
                // Launch and impact, so a salvo can be reconstructed afterwards. Rounds of one
                // group arrive TOGETHER, so a shared impact time is what identifies them, and the
                // launch time is what says which of them waited and for how long.
                b.AppendLine(F("LaunchTime", launchTime));
                b.AppendLine(F("ImpactTime", launchTime + actualFlight));
                // What the round actually did, against what the model says it should have. Without
                // these an entry says only that the estimate was wrong, never where: a flight that
                // is slow because it never reached its top speed and one that is slow because it
                // flew a longer arc look identical from the flight time alone.
                b.AppendLine(F("PeakSpeedKn", peakSpeedKn));
                b.AppendLine(F("PeakAltU", peakAltU));
                b.AppendLine(F("TerminalSpeedKn", lastSpeedKn));
                b.AppendLine(F("InGroup", inGroup));
                b.AppendLine(F("GroupLeader", groupLeader));
                b.AppendLine(F("MaxGroupSize", maxGroupSize));
                // Provenance. A corpus outlives the build that produced it, so an entry has to say
                // what it was captured against or a later disagreement is unattributable.
                b.AppendLine(F("GameVersion", Application.version));
                b.AppendLine(F("GameBranch", FlightTime.SimIsBeta ? "beta" : "public"));
                b.AppendLine(F("ModVersion", Bootstrap.Version));
                b.AppendLine(F("CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)));
                // One input per round now, so this can no longer be ambiguous. Still written, so a
                // corpus mixing old and new captures reads uniformly.
                b.AppendLine(F("Claims", 1));

                string file = Path.Combine(Directory, key + ".result");
                File.WriteAllText(file, b.ToString());
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] solve-result {ammoLabel}: actual {actualFlight:0.0}s, " +
                    $"motor {motorPerformance:0.000}, compression {minCompression:0}-{maxCompression:0}x, " +
                    $"step {minFlownStep:0.0000}-{maxFlownStep:0.0000}s " +
                    $"-> {Path.GetFileName(file)}");
            }
            catch (Exception e)
            {
                ModLog.Warn("solve-result write failed", e);
            }
        }

        // "R" round-trips the float exactly, so a replay starts from the same bits the game solved
        // with rather than from a printed approximation.
        private static string F(string k, float v) => k + " = " + v.ToString("R", Inv);
        private static string F(string k, int v) => k + " = " + v.ToString(Inv);
        private static string F(string k, bool v) => k + " = " + (v ? "true" : "false");
        private static string F(string k, string v) => k + " = " + (v ?? string.Empty);

        private static string F(string k, Vector3 v)
            => k + " = " + v.x.ToString("R", Inv) + "," + v.y.ToString("R", Inv) + "," +
               v.z.ToString("R", Inv);

        private static string F(string k, Quaternion q)
            => k + " = " + q.x.ToString("R", Inv) + "," + q.y.ToString("R", Inv) + "," +
               q.z.ToString("R", Inv) + "," + q.w.ToString("R", Inv);

        private static string F(string k, Vector2[] nodes)
        {
            if (nodes == null || nodes.Length == 0) return k + " = ";
            var b = new StringBuilder(k).Append(" = ");
            for (int n = 0; n < nodes.Length; n++)
            {
                if (n > 0) b.Append(';');
                b.Append(nodes[n].x.ToString("R", Inv)).Append(',')
                 .Append(nodes[n].y.ToString("R", Inv));
            }
            return b.ToString();
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var b = new StringBuilder(s.Length);
            foreach (char c in s) b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return b.ToString();
        }
    }
}
