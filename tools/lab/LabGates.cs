using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SeaPower;

namespace AutoTOT.Lab
{
    internal static partial class Program
    {
        /// <summary>
        /// Counts, per family, how often the model's memoryless stage tests disagree with the game's latched states: M1 and
        /// M2 of docs/plans/open/model-and-launcher-audit.md.
        ///
        /// M1: steps on which the step's own ToBearing test reads true after the round has already left ToBearing, which the
        /// game never re-enters (Missile.cs:343, entered only from :188 and :253).
        /// M2: steps flown in the terminal stage before TerminalDelay has passed, which the game refuses (Missile.cs:485-538).
        ///
        /// Each capture is replayed once at the production step with its recorded motor roll. A family with no such steps
        /// has nothing for the counterfactual to change, which is the kill criterion for that family.
        ///
        ///   autotot-lab gates [corpus-dir]
        ///
        /// AUTOTOT_GATES_CSV=&lt;file&gt; writes key,label,reentrySteps,terminalBeforeDelaySteps per round.
        /// </summary>
        private static int GatesCommand(string[] args)
        {
            string dir = CorpusArg(args, 0);
            string[] files = Directory.GetFiles(dir, "*.solveinput").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var families = new SortedDictionary<string, List<(int reentry, int beforeDelay)>>(StringComparer.Ordinal);
            var csv = new StringBuilder("key,label,reentrySteps,terminalBeforeDelaySteps\n");
            int failed = 0;
            const float Step = 0.1f;

            foreach (string file in files)
            {
                string resultFile = Path.ChangeExtension(file, ".result");
                Dictionary<string, string> kv = ReadDump(file);
                Dictionary<string, string> r = File.Exists(resultFile) ? ReadDump(resultFile) : new Dictionary<string, string>();
                string label = kv.TryGetValue("Label", out string l) ? l : "?";
                string key = kv.TryGetValue("Key", out string k) ? k : Path.GetFileNameWithoutExtension(file);
                try
                {
                    AmmunitionParameters ap = BuildAmmo(kv);
                    FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: false);
                    FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f ? F(r, "MotorPerformance") : 1f;
                    FlightTime.SolveOffline(in input, ap, Step, out _);
                }
                catch (Exception)
                {
                    failed++;
                    continue;
                }
                finally
                {
                    FlightTime.OfflineMotorScale = 1f;
                }

                int reentry = FlightTime.OfflineToBearingReentrySteps;
                int beforeDelay = FlightTime.OfflinePhase2BeforeDelaySteps;
                if (!families.TryGetValue(label, out var list)) families[label] = list = new List<(int, int)>();
                list.Add((reentry, beforeDelay));
                csv.Append(key).Append(',').Append(label).Append(',').Append(reentry).Append(',').Append(beforeDelay).Append('\n');
            }

            Console.WriteLine($"{files.Length} captures, {failed} could not be replayed; step {Step:0.0}s; " +
                              $"latch {(FlightTime.OfflineToBearingLatch ? "on" : "off")}, " +
                              $"terminal delay gate {(FlightTime.OfflineTerminalDelayGate ? "on" : "off")}");
            Console.WriteLine($"{"family",-22} {"rounds",6} | {"M1 rounds",9} {"mean s",7} {"max s",6} | {"M2 rounds",9} {"mean s",7} {"max s",6}");
            string Cell(List<int> v) => v.Count == 0
                ? $"{0,9} {"",7} {"",6}"
                : $"{v.Count,9} {v.Average() * Step,7:0.0} {v.Max() * Step,6:0.0}";
            foreach (KeyValuePair<string, List<(int reentry, int beforeDelay)>> f in families)
            {
                List<int> m1 = f.Value.Where(x => x.reentry > 0).Select(x => x.reentry).ToList();
                List<int> m2 = f.Value.Where(x => x.beforeDelay > 0).Select(x => x.beforeDelay).ToList();
                Console.WriteLine($"{f.Key,-22} {f.Value.Count,6} | {Cell(m1)} | {Cell(m2)}");
            }

            string csvPath = System.Environment.GetEnvironmentVariable("AUTOTOT_GATES_CSV");
            if (!string.IsNullOrEmpty(csvPath)) File.WriteAllText(csvPath, csv.ToString());
            return 0;
        }
    }
}
