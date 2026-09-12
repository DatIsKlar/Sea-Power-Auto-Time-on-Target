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
    /// Scoring: the score command against recorded flights, and parity against the in-game solve.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>
        /// Does the lab fly the same model the game ran? Each rich-flight-v3 input records the
        /// value the mod's own step loop returned for it (InGameEstimate) and the step it used.
        /// Replaying the identical input at that step, with the motor multiplier at the 1.0 the
        /// estimator assumes, must give the same number. A difference means the lab is not
        /// replaying the model the round was solved with, and every offline score is suspect.
        ///
        /// Only captures made by the build whose source the lab is compiled from can match.
        /// --launch-estimate falls back to EstAtLaunch for older captures. That value comes
        /// through the estimate cache and may be from an earlier solve at a different position,
        /// so a small difference there is not evidence of anything.
        /// </summary>
        private static int Parity(string[] args)
        {
            bool useLaunch = args.Contains("--launch-estimate");
            string[] rest = args.Where(a => a != "--launch-estimate").ToArray();
            string dir = CorpusArg(rest, 0);
            int match = 0, differ = 0, skipped = 0;
            Console.WriteLine($"{"round",-52}{"source",-14}{"game",10}{"lab",10}{"lab-game",11}");
            foreach (string file in Directory.GetFiles(dir, "*.solveinput").OrderBy(f => f))
            {
                Dictionary<string, string> kv = ReadDump(file);
                float expected, step;
                string source;
                if (kv.TryGetValue("InGameEstimate", out string ig) && ig != "NaN" && ig.Length > 0)
                {
                    expected = F(kv, "InGameEstimate");
                    step = F(kv, "InGameStep");
                    source = "in-game solve";
                }
                else if (useLaunch && File.Exists(Path.ChangeExtension(file, ".result")))
                {
                    expected = F(ReadDump(Path.ChangeExtension(file, ".result")), "EstAtLaunch");
                    step = 0.1f;
                    source = "EstAtLaunch";
                    if (expected <= 0f) { skipped++; continue; }
                }
                else { skipped++; continue; }

                AmmunitionParameters ap = BuildAmmo(kv);
                FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: false);
                FlightTime.OfflineMotorScale = 1f;
                float lab = FlightTime.SolveOffline(in input, ap, step, out _);
                float d = lab - expected;
                bool ok = Math.Abs(d) <= ParityToleranceSim;
                if (ok) match++; else differ++;
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-52}{source,-14}" +
                                  $"{expected,10:F3}{lab,10:F3}{d,+11:+0.000;-0.000}" +
                                  (ok ? "" : "  DIFFERS"));
            }
            Console.WriteLine($"\n{match} match, {differ} differ, {skipped} without a comparable value.");
            return differ == 0 ? 0 : 1;
        }

        /// <summary>The same code on the same input should agree to rounding.</summary>
        private const float ParityToleranceSim = 0.0005f;

        /// <summary>
        /// Scores the whole corpus: every input replayed at every step size, against the flight the
        /// round actually flew. This is the number a model change has to move.
        /// </summary>
        private static int Score(string[] files, float[] steps)
        {
            var rows = new List<(string Label, float Actual, float[] Est, float Flown, float FlownEst,
                                 bool Grouped, float Launch, float Impact)>();
            var skipped = new List<string>();

            foreach (string file in files)
            {
                string resultFile = Path.ChangeExtension(file, ".result");
                if (!File.Exists(resultFile))
                {
                    skipped.Add($"{Path.GetFileNameWithoutExtension(file)}: no outcome recorded");
                    continue;
                }

                Dictionary<string, string> r = ReadDump(resultFile);
                string label = r.GetValueOrDefault("Label", Path.GetFileNameWithoutExtension(file));

                // Exclusions. Each of these makes a round unfair to score rather than merely
                // inaccurate, so counting them would move the aggregate for a reason that has
                // nothing to do with the model.
                // Time compression is deliberately NOT an exclusion. Above 10x the game enlarges
                // its own physics step, but a round flown at a STEADY enlarged step is perfectly
                // reproducible: the lab replays it at that step. What cannot be reproduced is a
                // step that MOVED mid-flight, because then no single step describes the flight.
                // And since the mod is used at high compression, being right there is part of the
                // job rather than a case to discard.
                float stepLo = F(r, "MinFlownStep"), stepHi = F(r, "MaxFlownStep");
                bool stepSteady = stepLo > 0f && stepHi > 0f &&
                                  Math.Abs(stepHi - stepLo) <=
                                  SteadyStepRelativeTolerance * stepHi;
                string targetManeuver = TargetManeuverReason(r);

                string reason =
                    !B(r, "Arrived")   ? $"no arrival ({F(r, "FinalRangeM"):F0} m out)"
                  : B(r, "Retargeted") ? "seeker switched target, flight is to a different ship"
                  : I(r, "Claims") > 1 ? "several rounds shared one solver input"
                  // The solver freezes the target's velocity at launch, so a target that turned or
                  // changed speed afterwards was never predictable from that snapshot. Counting it
                  // as model error would teach the model to chase something it cannot see. 20
                  // degrees and 5 knots are loose enough to ignore station-keeping wobble.
                  : targetManeuver != null ? targetManeuver
                  : (stepHi > 0f && !stepSteady)
                        ? $"physics step moved in flight ({stepLo:F4}s to {stepHi:F4}s), " +
                          "so no single step describes it"
                  : null;
                if (reason != null) { skipped.Add($"{label}: {reason}"); continue; }

                Dictionary<string, string> kv = ReadDump(file);
                AmmunitionParameters ap = BuildAmmo(kv);
                FlightTime.SolveInput input = BuildInput(kv, ap);

                // Replay the motor roll this particular round flew, rather than the 1.0 the
                // estimator has to assume in advance.
                FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                    ? F(r, "MotorPerformance") : 1f;

                float[] est = steps.Select(dt => FlightTime.SolveOffline(in input, ap, dt, out _)).ToArray();

                // The step the round itself flew. Zero for a capture that predates the recording,
                // in which case the column is blank rather than guessed at.
                float flown = stepSteady ? stepHi : 0f;
                float flownEst = flown > 0f
                    ? FlightTime.SolveOffline(in input, ap, flown, out _) : -1f;
                FlightTime.OfflineMotorScale = 1f;

                // Grouped rounds are scored, but never mixed into the solo figure. The integrator
                // models a round flying alone; the formation effect is corrected for elsewhere, on
                // the release path, so averaging the two cohorts together would hide a real solo
                // regression behind grouped noise and invite double-correcting the model.
                // Launch time: recorded directly on newer captures, and recoverable from the key
                // ("...__t1975.7__3") on older ones, which is the solve stamp a tick before launch.
                float launch = F(r, "LaunchTime");
                if (launch <= 0f) launch = LaunchTimeFromKey(
                    r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(file)));

                rows.Add((label, F(r, "ActualFlight"), est, flown, flownEst, B(r, "InGroup"),
                          launch, launch + F(r, "ActualFlight")));
            }

            // A missile group arrives TOGETHER, so every member shares one impact time while their
            // launch times are spread across the salvo. Each round's own flight therefore includes
            // however long it loitered waiting for the rest, and its gap is that wait, not model
            // error: across one 20-round salvo the gap fell linearly from +72.9s on the first round
            // to +0.15s on the last, and the mean was half the launch span. Averaging that reports
            // the firing cadence dressed up as accuracy.
            //
            // So a salvo is scored on its LAST-fired round, the one that waited least, and the rest
            // are set aside as having flown a formation rather than a trajectory.
            int waited = 0;
            foreach (var salvo in rows.Where(r => r.Grouped)
                                      .GroupBy(r => (r.Label, Key: (int)Math.Round(r.Impact / 2.0)))
                                      .Where(g => g.Count() > 1))
            {
                float lastLaunch = salvo.Max(r => r.Launch);
                foreach (var r in salvo.Where(r => r.Launch < lastLaunch))
                {
                    rows.Remove(r);
                    waited++;
                }
                skipped.Add($"{salvo.Key.Label}: {salvo.Count() - 1} of {salvo.Count()} rounds waited " +
                            $"for the formation (impact ~{salvo.First().Impact:F0}s); " +
                            "scored on the last one fired");
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("nothing scoreable in the corpus.");
                foreach (string sk in skipped) Console.WriteLine("  excluded  " + sk);
                return 1;
            }

            Console.WriteLine();
            Console.Write($"{"round",-26}{"actual",9}");
            foreach (float dt in steps) Console.Write($"{"offGap@" + dt.ToString("0.####", Inv),12}");
            Console.Write($"{"flown step",12}{"offGap@step",12}");
            Console.WriteLine();
            Console.WriteLine(new string('-', 59 + 12 * steps.Length));

            // Match the production gap convention: actual - offline estimate. These values are
            // deliberately distinct from index.csv's actualMinusLaunchEstimate.
            foreach (var row in rows.OrderBy(r => r.Grouped).ThenBy(r => r.Label))
            {
                string name = row.Grouped ? Trim(row.Label, 22) + " [grp]" : Trim(row.Label, 25);
                Console.Write($"{name,-26}{row.Actual,8:F1}s");
                for (int k = 0; k < steps.Length; k++)
                    Console.Write(row.Est[k] > 0f ? $"{row.Actual - row.Est[k],12:+0.00;-0.00}" : $"{"failed",12}");
                Console.Write(row.Flown > 0f ? $"{row.Flown,12:F4}" : $"{"-",12}");
                Console.Write(row.FlownEst > 0f ? $"{row.Actual - row.FlownEst,12:+0.00;-0.00}" : $"{"-",12}");
                Console.WriteLine();
            }

            Console.WriteLine(new string('-', 59 + 12 * steps.Length));

            MeanRow("mean |offline gap| solo", rows.Where(r => !r.Grouped).ToList(), steps);
            var grouped = rows.Where(r => r.Grouped).ToList();
            if (grouped.Count > 0) MeanRow("mean |offline gap| grouped", grouped, steps);

            Console.WriteLine($"\n{rows.Count} round(s) scored, {skipped.Count} excluded.");
            foreach (string sk in skipped) Console.WriteLine("  excluded  " + sk);
            return 0;
        }

        /// <summary>Reject motion no launch-time snapshot could predict. Rich captures inspect the
        /// entire target path, so a target that turns away and later resumes its original course is
        /// still detected. Historical captures retain the launch/end fallback.</summary>
        private static string TargetManeuverReason(Dictionary<string, string> r)
        {
            float course0 = F(r, "TargetCourseAtLaunch");
            float speed0 = F(r, "TargetSpeedAtLaunch");
            if (speed0 <= 0f) return null;

            float maxTurn = Math.Abs(DeltaAngle(course0, F(r, "TargetCourseAtEnd")));
            float maxSpeedChange = Math.Abs(F(r, "TargetSpeedAtEnd") - speed0);
            string track = r.GetValueOrDefault("FlightTrack", "");
            if (track.Length > 0)
            {
                foreach (string sample in track.Split(';'))
                {
                    string[] p = sample.Split(':');
                    if (p.Length < 15) continue;
                    if (float.TryParse(p[13], NumberStyles.Float, Inv, out float course))
                        maxTurn = Math.Max(maxTurn, Math.Abs(DeltaAngle(course0, course)));
                    if (float.TryParse(p[14], NumberStyles.Float, Inv, out float speed))
                        maxSpeedChange = Math.Max(maxSpeedChange, Math.Abs(speed - speed0));
                }
            }

            if (maxTurn > 20f)
                return $"target turned {maxTurn:F0} deg after launch, which no launch-time estimate could predict";
            if (maxSpeedChange > 5f)
                return $"target changed speed by {maxSpeedChange:F0} kn after launch";
            return null;
        }

        private static void MeanRow(string label,
            List<(string Label, float Actual, float[] Est, float Flown, float FlownEst, bool Grouped,
                  float Launch, float Impact)> rows,
            float[] steps)
        {
            if (rows.Count == 0) return;
            Console.Write($"{label + " (" + rows.Count + ")",-26}{"",9}");
            for (int k = 0; k < steps.Length; k++)
            {
                var valid = rows.Where(r => r.Est[k] > 0f).ToList();
                Console.Write(valid.Count > 0
                    ? $"{valid.Average(r => Math.Abs(r.Est[k] - r.Actual)),12:F2}"
                    : $"{"-",12}");
            }
            var flownRows = rows.Where(r => r.FlownEst > 0f).ToList();
            Console.Write(flownRows.Count > 0
                ? $"{"",12}{flownRows.Average(r => Math.Abs(r.FlownEst - r.Actual)),12:F2}"
                : $"{"",12}{"-",12}");
            Console.WriteLine();
        }

        /// <summary>
        /// The mission time out of a corpus key, which ends "__t&lt;time&gt;__&lt;seq&gt;". Zero when
        /// the key predates that format.
        /// </summary>
        private static float LaunchTimeFromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0f;
            foreach (string part in key.Split(new[] { "__" }, StringSplitOptions.None))
                if (part.Length > 1 && part[0] == 't' &&
                    float.TryParse(part.Substring(1), System.Globalization.NumberStyles.Float, Inv,
                                   out float t))
                    return t;
            return 0f;
        }

        /// <summary>Signed smallest angle between two compass headings, in degrees.</summary>
        private static float DeltaAngle(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";
    }
}
