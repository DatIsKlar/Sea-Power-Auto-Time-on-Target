using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoTOT.Lab
{
    /// <summary>
    /// The readouts command: the game's two tactical-map flight times against the flight each round actually
    /// flew, beside the mod's own estimate from the same frame. Diagnostic D1 of the Flight Time Display plan
    /// (FlightTimeDisplay/docs/plans/open/flight-time-display.md).
    /// </summary>
    internal static partial class Program
    {
        private static int Readouts(string[] args)
        {
            bool perRound = args.Contains("--rounds");
            string dir = CorpusArg(args.Where(a => a != "--rounds").ToArray(), 0);

            var rows = new List<(string Label, string Key, string Run, float Launch, float Impact, bool Grouped,
                                 float Actual, float Ours, float Tooltip, float AttackLine)>();
            var skipped = new List<string>();
            int withoutFields = 0;

            foreach (string resultFile in Directory.GetFiles(dir, "*.result").OrderBy(f => f))
            {
                Dictionary<string, string> r = ReadDump(resultFile);
                if (!r.ContainsKey("GameTooltipEst")) { withoutFields++; continue; }
                string label = r.GetValueOrDefault("Label", Path.GetFileNameWithoutExtension(resultFile));

                // The score command's exclusions that do not depend on a replay: each makes a round unfair to
                // score rather than merely inaccurate.
                string maneuver = TargetManeuverReason(r);
                string reason =
                    !B(r, "Arrived")   ? $"no arrival ({F(r, "FinalRangeM"):F0} m out)"
                  : B(r, "Retargeted") ? "seeker switched target, flight is to a different ship"
                  : I(r, "Claims") > 1 ? "several rounds shared one solver input"
                  : maneuver;
                if (reason != null) { skipped.Add($"{label}: {reason}"); continue; }

                float actual = ActualEnd(r);
                rows.Add((label, r.GetValueOrDefault("Key", ""), RunOf(r), F(r, "LaunchTime"),
                          F(r, "LaunchTime") + actual, B(r, "InGroup"), actual, F(r, "EstAtLaunch"),
                          F(r, "GameTooltipEst"), F(r, "GameAttackLineEst")));
            }

            // A missile group arrives together, so each member's flight includes its wait for the formation and
            // only the last round fired is comparable with a single-round readout. Clustered as the score
            // command does: label, run and a shared impact time. A group of one flies alone and is kept.
            foreach (var salvo in rows.Where(q => q.Grouped)
                                      .GroupBy(q => (q.Label, q.Run, Key: (int)Math.Round(q.Impact / 2.0)))
                                      .ToList())
            {
                if (salvo.Count() == 1) continue;
                var last = salvo.OrderBy(q => q.Launch).Last();
                foreach (var member in salvo.Where(q => q.Key != last.Key))
                {
                    rows.Remove(member);
                    skipped.Add($"{member.Label}: grouped, not the last round of its salvo");
                }
            }

            if (withoutFields > 0)
                Console.WriteLine($"{withoutFields} result(s) predate the readout fields and are not read.");
            if (rows.Count == 0)
            {
                Console.WriteLine("No scorable round carries GameTooltipEst.");
                return 1;
            }

            // gap = actual flight - estimate, the corpus's sign: positive means the estimate was short.
            string Cell(IEnumerable<float> gaps)
            {
                List<float> g = gaps.ToList();
                if (g.Count == 0) return $"{"-",7}{"-",7}{"-",7}{0,4}";
                return $"{g.Average(),7:+0.0;-0.0}{g.Average(Math.Abs),7:0.0}{g.Max(Math.Abs),7:0.0}{g.Count,4}";
            }
            static bool Shown(float v) => v > 0f;

            string block = $"{"bias",7}{"|mean|",7}{"max",7}{"n",4}";
            Console.WriteLine($"{"",-30}   {"ours (EstAtLaunch)",-25}   {"game tooltip",-25}   {"game attack line",-25}");
            Console.WriteLine($"{"family",-26}{"n",4}   {block}   {block}   {block}");
            var families = rows.GroupBy(q => q.Label).OrderBy(gr => gr.Key).ToList();
            int famTooltip = 0, famAttack = 0, tooltipWorse = 0, attackWorse = 0, oursWorseTooltip = 0, oursWorseAttack = 0;
            foreach (var fam in families)
            {
                var ours = fam.Where(q => Shown(q.Ours)).Select(q => q.Actual - q.Ours).ToList();
                var tip = fam.Where(q => Shown(q.Tooltip)).Select(q => q.Actual - q.Tooltip).ToList();
                var line = fam.Where(q => Shown(q.AttackLine)).Select(q => q.Actual - q.AttackLine).ToList();
                Console.WriteLine($"{Trim(fam.Key, 25),-26}{fam.Count(),4}   {Cell(ours)}   {Cell(tip)}   {Cell(line)}");

                if (ours.Count == 0) continue;
                float oursMean = ours.Average(Math.Abs);
                if (tip.Count > 0)
                {
                    famTooltip++;
                    if (tip.Average(Math.Abs) > oursMean) tooltipWorse++;
                    if (oursMean > tip.Average(Math.Abs) + 0.5f) oursWorseTooltip++;
                }
                if (line.Count > 0)
                {
                    famAttack++;
                    if (line.Average(Math.Abs) > oursMean) attackWorse++;
                    if (oursMean > line.Average(Math.Abs) + 0.5f) oursWorseAttack++;
                }
            }

            Console.WriteLine($"\n{"all",-26}{rows.Count,4}   " +
                $"{Cell(rows.Where(q => Shown(q.Ours)).Select(q => q.Actual - q.Ours))}   " +
                $"{Cell(rows.Where(q => Shown(q.Tooltip)).Select(q => q.Actual - q.Tooltip))}   " +
                $"{Cell(rows.Where(q => Shown(q.AttackLine)).Select(q => q.Actual - q.AttackLine))}");

            var both = rows.Where(q => Shown(q.Tooltip) && Shown(q.AttackLine)).ToList();
            if (both.Count > 0)
                Console.WriteLine($"\nThe two game readouts on the same shot ({both.Count} rounds): mean |tooltip - attack line| " +
                                  $"{both.Average(q => Math.Abs(q.Tooltip - q.AttackLine)):0.0} s, max " +
                                  $"{both.Max(q => Math.Abs(q.Tooltip - q.AttackLine)):0.0} s.");

            // The decision rule written into the plan before any run.
            string Verdict(string name, int fams, int worse, int oursWorse)
            {
                bool replace = fams > 0 && worse * 3 >= fams * 2 && oursWorse == 0;
                return $"{name}: game worse than ours on {worse} of {fams} families, ours worse by more than 0.5 s on " +
                       $"{oursWorse}. {(replace ? "REPLACE" : "KEEP the game's number")}.";
            }
            Console.WriteLine("\nDecision rule (replace when the game is worse on at least two thirds of the families " +
                              "and ours is never worse by more than 0.5 s):");
            Console.WriteLine("  " + Verdict("tooltip", famTooltip, tooltipWorse, oursWorseTooltip));
            Console.WriteLine("  " + Verdict("attack line", famAttack, attackWorse, oursWorseAttack));

            if (perRound)
            {
                Console.WriteLine($"\n{"round",-60}{"actual",8}{"ours",8}{"tooltip",9}{"attack",8}");
                foreach (var q in rows.OrderBy(q => q.Label).ThenBy(q => q.Key))
                    Console.WriteLine($"{Trim(q.Key, 59),-60}{q.Actual,8:0.0}{q.Ours,8:0.0}{q.Tooltip,9:0.0}{q.AttackLine,8:0.0}");
            }

            Console.WriteLine($"\n{rows.Count} round(s) scored, {skipped.Count} excluded.");
            foreach (string sk in skipped) Console.WriteLine("  excluded  " + sk);
            return 0;
        }
    }
}
