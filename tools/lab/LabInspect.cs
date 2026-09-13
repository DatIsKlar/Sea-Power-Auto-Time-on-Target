using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SeaPower;
using UnityEngine;

namespace AutoTOT.Lab
{
    /// <summary>
    /// Single-capture inspection: the trace and climb commands, and the bare step-size replay.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>
        /// Puts the round's recorded altitude against the model's, sample for sample.
        ///
        /// Peak altitude alone cannot separate the two ways a round ends up below its commanded
        /// altitude. If the recorded climb is still rising when it turns over, the round ran out of
        /// distance and the climb is rate-limited by something we do not model. If it flattens
        /// early and holds, it was commanded to a lower altitude than we think.
        /// </summary>
        /// <summary>
        /// How much the model's flight time moves with the per-round thrust roll, for one captured
        /// round. The game rolls <c>1.02 - rand/25</c> and multiplies thrust by it, and the scorer
        /// replays each round with the value it flew on the grounds that this removes the roll. It
        /// only removes it if the model's response matches the game's, and on `plan_yj-20` at 65 nm
        /// it does not: see docs/plans/open/yj-20-motor-sensitivity.md. Live rounds give the game's
        /// side of that comparison but carry sampling noise; this gives the model's side exactly.
        ///
        /// Reported as seconds per unit of motor and as a multiple of the round's own flight time,
        /// which is the form the two sides are compared in. The game's figure sits at 1.14 to 1.18
        /// on every family measured so far.
        /// </summary>
        private static int Motor(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab motor <solveinput-file> [scale ...]");
                return 2;
            }

            string file = args[0];
            float[] scales = args.Skip(1).Select(a => float.Parse(a, Inv)).ToArray();
            if (scales.Length == 0) scales = new[] { 0.98f, 0.99f, 1.00f, 1.01f, 1.02f };

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);

            Console.WriteLine($"{"motor",8}{"estimate",12}");
            Console.WriteLine(new string('-', 20));
            var pts = new List<(float m, float est)>();
            foreach (float m in scales)
            {
                FlightTime.OfflineMotorScale = m;
                float est = FlightTime.SolveOffline(in input, ap, 0.1f, out _);
                FlightTime.OfflineMotorScale = 1f;
                pts.Add((m, est));
                Console.WriteLine($"{m,8:F4}{est,12:F3}");
            }

            // Least squares over whatever scales were asked for, so an uneven sweep still reports
            // one slope rather than a pair of endpoints that happen to bracket a kink.
            float mx = pts.Average(p => p.m), my = pts.Average(p => p.est);
            float den = pts.Sum(p => (p.m - mx) * (p.m - mx));
            float slope = den > 0f ? pts.Sum(p => (p.m - mx) * (p.est - my)) / den : 0f;
            Console.WriteLine(new string('-', 20));
            Console.WriteLine($"d(estimate)/d(motor) = {slope:F1} s per unit, " +
                              $"{Math.Abs(slope) / Math.Max(my, 0.001f):F2} x the flight");
            return 0;
        }

        private static int Climb(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab climb <solveinput-file>");
                return 2;
            }

            string file = args[0];
            string resultFile = Path.ChangeExtension(file, ".result");
            if (!File.Exists(resultFile))
            {
                Console.Error.WriteLine("no recorded outcome beside that input");
                return 1;
            }

            Dictionary<string, string> r = ReadDump(resultFile);
            string track = r.GetValueOrDefault("Track", "");
            if (track.Length == 0)
            {
                Console.Error.WriteLine(
                    "this round predates the recorded track; re-fly it to compare climbs");
                return 1;
            }

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: true);
            FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                ? F(r, "MotorPerformance") : 1f;

            var sw = new StringWriter();
            TextWriter prev = Console.Out;
            Console.SetOut(sw);
            Bootstrap.Log.Echo = true;
            try { FlightTime.SolveOffline(in input, ap, 0.1f, out _); }
            finally
            {
                // A throwing solve must not leave the rest of the run printing into the buffer.
                Bootstrap.Log.Echo = false;
                Console.SetOut(prev);
            }
            FlightTime.OfflineMotorScale = 1f;

            // Model samples from the trace it just produced. Speed as well as altitude: the trace
            // has always carried it and the column dropped it, which left the one question the
            // command cannot otherwise answer, whether a timing difference is a path difference or
            // a speed difference, needing a second tool.
            var model = new List<(float T, float Alt, float Spd)>();
            foreach (Match m in Regex.Matches(sw.ToString(), @"t\+([0-9.]+)s spd (\d+)kn alt ([0-9.]+)"))
                model.Add((float.Parse(m.Groups[1].Value, Inv),
                           float.Parse(m.Groups[3].Value, Inv),
                           float.Parse(m.Groups[2].Value, Inv)));

            Console.WriteLine();
            Console.WriteLine($"{"t",8}{"realAlt",10}{"modelAlt",10}{"diff",9}" +
                              $"{"realSpd",9}{"modelSpd",10}{"diff%",8}");
            Console.WriteLine(new string('-', 64));

            float ceiling = F(kv, "LoftAlt");
            float peakReal = 0f, peakModel = 0f;
            foreach (string sample in track.Split(';'))
            {
                string[] p = sample.Split(':');
                if (p.Length < 3) continue;
                float t = float.Parse(p[0], Inv), alt = float.Parse(p[1], Inv), spd = float.Parse(p[2], Inv);
                (float ma, float ms) = NearestSample(model, t);
                peakReal = Math.Max(peakReal, alt);
                peakModel = Math.Max(peakModel, ma);
                string spdDiff = spd > 1f ? $"{100f * (ms - spd) / spd:+0.0;-0.0}" : "";
                Console.WriteLine($"{t,8:F0}{alt,10:F0}{ma,10:F0}{ma - alt,9:+0;-0}" +
                                  $"{spd,9:F0}{ms,10:F0}{spdDiff,8}");
            }

            Console.WriteLine(new string('-', 64));
            Console.WriteLine($"commanded ceiling {ceiling:F0}u, real peak {peakReal:F0}u, model peak {peakModel:F0}u");
            Console.WriteLine(r.GetValueOrDefault("StageChanges", ""));
            return 0;
        }

        /// <summary>Model altitude and speed at the sample nearest this time. The recorded track
        /// and the model trace run on different cadences, so neither can be indexed by the other.
        /// </summary>
        private static (float Alt, float Spd) NearestSample(List<(float T, float Alt, float Spd)> model,
                                                            float t)
        {
            float bestAlt = 0f, bestSpd = 0f, bestD = float.MaxValue;
            foreach (var m in model)
            {
                float d = Math.Abs(m.T - t);
                if (d < bestD) { bestD = d; bestAlt = m.Alt; bestSpd = m.Spd; }
            }
            return (bestAlt, bestSpd);
        }

        /// <summary>
        /// Replays one round with the step trace on, printing the same sim-track lines the mod
        /// emits in game. This is how a disagreement gets localised to a phase.
        /// </summary>
        private static int Trace(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(
                    "usage: autotot-lab trace <solveinput-file> [step] [--motor <value>]");
                return 2;
            }

            // --motor replays the same input at a thrust multiplier of your choosing instead of the
            // one the round flew. Two traces of one input at two multipliers isolate the model's
            // response to thrust from every other difference between two rounds, which is what the
            // live salvo cannot do: its rounds differ in geometry as well as motor.
            float motorOverride = -1f;
            var rest = new List<string>();
            for (int k = 0; k < args.Length; k++)
            {
                if (args[k] == "--motor" && k + 1 < args.Length)
                { motorOverride = float.Parse(args[++k], Inv); continue; }
                rest.Add(args[k]);
            }

            string file = rest[0];
            float dt = rest.Count > 1 ? float.Parse(rest[1], Inv) : 0.1f;

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: true);
            Console.WriteLine("route " + (input.Legs == null ? "none (altitude-command law)"
                : string.Join(" ; ", input.Legs.Select(l => l.ToString()))));

            string resultFile = Path.ChangeExtension(file, ".result");
            if (File.Exists(resultFile))
            {
                Dictionary<string, string> r = ReadDump(resultFile);
                FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                    ? F(r, "MotorPerformance") : 1f;
                Console.WriteLine($"actual {F(r, "ActualFlight"):F1}s, motor {FlightTime.OfflineMotorScale:F3}");
            }
            if (motorOverride > 0f)
            {
                FlightTime.OfflineMotorScale = motorOverride;
                Console.WriteLine($"motor OVERRIDDEN to {motorOverride:F4}; the recorded outcome " +
                                  "above belongs to a different multiplier");
            }

            Bootstrap.Log.Echo = true;
            float est = FlightTime.SolveOffline(in input, ap, dt, out FlightTime.IntegratedPhases phases);
            Bootstrap.Log.Echo = false;
            FlightTime.OfflineMotorScale = 1f;

            Console.WriteLine($"est {est:F2}s at step {dt}, model peak {phases.PeakAltU:F1}u, " +
                              $"terminal {phases.VTerm:F0}kn");
            Console.WriteLine($"phases climb {phases.ClimbTime:F2}s, cruise {phases.CruiseTime:F2}s, " +
                              $"terminal {phases.DescentTime:F2}s; dive starts " +
                              $"{phases.DiveStartU:F1}u from target");
            return 0;
        }

        private static void Replay(string file, float[] steps)
        {
            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);

            Console.WriteLine();
            Console.WriteLine($"=== {kv.GetValueOrDefault("Label", Path.GetFileNameWithoutExtension(file))} ===");
            Console.WriteLine($"    range {input.FlatDistTotal:F0}u, {(input.NonKin ? "non-kinematic" : "kinematic")}, " +
                              $"max {input.MaxVelKn:F0}kn, launch pitch {input.LaunchPitch:F1} deg");
            if (input.ConfiguredLoftAlt > 0f)
                Console.WriteLine($"    loft configured {input.ConfiguredLoftAlt:F1}u, " +
                                  $"resolved {input.LoftAlt:F1}u, " +
                                  $"node peak {(input.AltNodes == null ? 0f : input.AltNodes.Max(n => n.y)):F1}u, " +
                                  $"Kappa end {input.KappaEndFlat:F1}u");
            Console.WriteLine();
            Console.WriteLine("      step        est      vs 0.1s     steps      solve");
            Console.WriteLine("    ------    -------    ---------   -------    -------");

            float baseline = float.NaN;
            foreach (float dt in steps)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                float est = FlightTime.SolveOffline(in input, ap, dt, out _);
                sw.Stop();

                if (float.IsNaN(baseline)) baseline = est;
                string delta = dt == steps[0]
                    ? "     -   "
                    : $"{est - baseline,+9:F3}";

                Console.WriteLine($"    {dt,6:F4}    {est,7:F2}s   {delta}   {(int)(est / dt),7}    {sw.Elapsed.TotalMilliseconds,6:F1}ms");
            }
        }
    }
}
