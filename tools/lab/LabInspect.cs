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

            // Model samples, as (t, alt), from the trace it just produced.
            var model = new List<(float T, float Alt)>();
            foreach (Match m in Regex.Matches(sw.ToString(), @"t\+([0-9.]+)s spd \d+kn alt ([0-9.]+)"))
                model.Add((float.Parse(m.Groups[1].Value, Inv), float.Parse(m.Groups[2].Value, Inv)));

            Console.WriteLine();
            Console.WriteLine($"{"t",8}{"realAlt",10}{"modelAlt",10}{"diff",9}{"realSpd",9}");
            Console.WriteLine(new string('-', 46));

            float ceiling = F(kv, "LoftAlt");
            float peakReal = 0f, peakModel = 0f;
            foreach (string sample in track.Split(';'))
            {
                string[] p = sample.Split(':');
                if (p.Length < 3) continue;
                float t = float.Parse(p[0], Inv), alt = float.Parse(p[1], Inv), spd = float.Parse(p[2], Inv);
                float ma = NearestAlt(model, t);
                peakReal = Math.Max(peakReal, alt);
                peakModel = Math.Max(peakModel, ma);
                Console.WriteLine($"{t,8:F0}{alt,10:F0}{ma,10:F0}{ma - alt,9:+0;-0}{spd,9:F0}");
            }

            Console.WriteLine(new string('-', 46));
            Console.WriteLine($"commanded ceiling {ceiling:F0}u, real peak {peakReal:F0}u, model peak {peakModel:F0}u");
            Console.WriteLine(r.GetValueOrDefault("StageChanges", ""));
            return 0;
        }

        private static float NearestAlt(List<(float T, float Alt)> model, float t)
        {
            float best = 0f, bestD = float.MaxValue;
            foreach (var m in model)
            {
                float d = Math.Abs(m.T - t);
                if (d < bestD) { bestD = d; best = m.Alt; }
            }
            return best;
        }

        /// <summary>
        /// Replays one round with the step trace on, printing the same sim-track lines the mod
        /// emits in game. This is how a disagreement gets localised to a phase.
        /// </summary>
        private static int Trace(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab trace <solveinput-file> [step]");
                return 2;
            }

            string file = args[0];
            float dt = args.Length > 1 ? float.Parse(args[1], Inv) : 0.1f;

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
