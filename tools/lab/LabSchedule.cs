using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTOT.Lab
{
    /// <summary>
    /// Each round's launch time from one ship's launchers of one weapon system, simulated from the game's own
    /// gate order. L2 of docs/plans/open/model-and-launcher-audit.md. Pure: no Unity or game state, so a build
    /// can move it into Support/ unchanged and the scorer and the mod then share one implementation.
    ///
    /// The game rules it follows, beta decompile:
    ///   - ObjectBase.HandleEngageTasks (ObjectBase.cs:3718-4222) walks the ship's engage tasks in order and
    ///     hands each one to the first usable launcher, one shot per hand-over, removing the task when its last
    ///     shot is taken (:4212-4221). A launcher already executing a task is not usable
    ///     (WeaponSelector.isWeaponSystemUsable).
    ///   - The exception (:3894): a task with fewer than two shots left is not handed over while the first-choice
    ///     launcher, one with associated sensors, is executing against the same target. The last round of an
    ///     order then waits for that launcher. <see cref="Spec.LastRoundWaitsForFirstLauncher"/>.
    ///   - A single-shot launcher ends its task at the launch (WeaponSystemLauncher.cs:870-872), so every round
    ///     is a fresh hand-over, and the launcher cycle is paid again: OnRailWarmup (:689-701) and the hatch
    ///     (:725-729).
    ///   - Then FirerateDelay against that launcher's own last launch (:742-746) and SharedLaunchDelay against
    ///     the ship's one timer for the system name (:747-751), which every launch resets (:845-848).
    ///
    /// Not modelled: salvo launchers (SalvoFireAmount above 1), launcher order by firing arc
    /// (WeaponSelector.GetWeaponSystemsForAmmunition), and a launcher that is empty or out of arc for the
    /// whole order. The fixed fire-to-first-round lead is the order's own StartupLead, taken as given, so this
    /// tests cadence and queueing and nothing else.
    /// </summary>
    internal static class LaunchSchedule
    {
        internal sealed class Spec
        {
            /// <summary>Launchers serving the ammunition.</summary>
            internal int Launchers = 1;
            /// <summary>The ship's shared launch interval for the system name, 0 when it declares none.</summary>
            internal double SharedInterval;
            /// <summary>60 / FireRate, gated per launcher.</summary>
            internal double FireInterval;
            /// <summary>OnRailWarmup plus the hatch animation, paid by every round.</summary>
            internal double Cycle;
            /// <summary>Time after a launch before the launcher can take the next round. 0 is the game's rule; a
            /// reading, never a fitted term.</summary>
            internal double RoundOverhead;
            /// <summary>ObjectBase.cs:3894: an order's last round is not handed to another launcher while the first
            /// launcher is executing against the same target. The game applies it only to a first-choice launcher with
            /// associated sensors; the scorer sets it per run.</summary>
            internal bool LastRoundWaitsForFirstLauncher;
        }

        internal struct Order
        {
            internal double RegisterSim;
            /// <summary>The order's fire-to-first-round lead as the mod set it, which includes one Cycle.</summary>
            internal double StartupLead;
            internal int Rounds;
            /// <summary>Orders with equal ids are aimed at the same target.</summary>
            internal int TargetId;
        }

        /// <summary>Simulation step. Finer than the game's 60 Hz tick, so the step adds nothing measurable.</summary>
        internal const double Step = 1.0 / 240.0;

        private const double Horizon = 7200.0;

        /// <summary>Launch times per order, in the order launched. An order the horizon cuts short has fewer.</summary>
        internal static List<double>[] Simulate(Spec s, IReadOnlyList<Order> orders)
        {
            int n = orders.Count;
            var launches = new List<double>[n];
            for (int j = 0; j < n; j++) launches[j] = new List<double>();
            if (n == 0) return launches;

            int launcherCount = Math.Max(1, s.Launchers);
            var freeAt = new double[launcherCount];
            var lastLaunch = new double[launcherCount];
            var heldOrder = new int[launcherCount];
            var heldReady = new double[launcherCount];
            for (int l = 0; l < launcherCount; l++)
            {
                freeAt[l] = double.NegativeInfinity;
                lastLaunch[l] = double.NegativeInfinity;
                heldOrder[l] = -1;
            }

            int[] remaining = new int[n];
            int unlaunched = 0;
            for (int j = 0; j < n; j++)
            {
                remaining[j] = Math.Max(0, orders[j].Rounds);
                unlaunched += remaining[j];
            }
            int[] taskOrder = Enumerable.Range(0, n).OrderBy(j => orders[j].RegisterSim).ThenBy(j => j).ToArray();
            double start = orders[taskOrder[0]].RegisterSim;
            double shipLast = double.NegativeInfinity;

            for (long step = 0; unlaunched > 0; step++)
            {
                double t = start + step * Step;
                if (t > start + Horizon) break;

                // Launch: every launcher holding a round whose gates have all passed, in launcher order.
                for (int l = 0; l < launcherCount; l++)
                {
                    int j = heldOrder[l];
                    if (j < 0 || t < heldReady[l] || t - lastLaunch[l] < s.FireInterval) continue;
                    if (s.SharedInterval > 0 && t - shipLast < s.SharedInterval) continue;
                    launches[j].Add(t);
                    lastLaunch[l] = t;
                    shipLast = t;
                    heldOrder[l] = -1;
                    freeAt[l] = t + s.RoundOverhead;
                    unlaunched--;
                }

                // Hand-over: each task, oldest first, takes the first free launcher, one round per step.
                foreach (int j in taskOrder)
                {
                    if (remaining[j] == 0 || orders[j].RegisterSim > t) continue;
                    if (s.LastRoundWaitsForFirstLauncher && remaining[j] == 1 && launcherCount > 1 &&
                        heldOrder[0] >= 0 && orders[heldOrder[0]].TargetId == orders[j].TargetId)
                        continue;
                    for (int l = 0; l < launcherCount; l++)
                    {
                        if (heldOrder[l] >= 0 || freeAt[l] > t) continue;
                        heldOrder[l] = j;
                        // No round leaves before the order's own lead; later rounds pay the cycle from the hand-over.
                        double gate = Math.Max(t, orders[j].RegisterSim + Math.Max(0.0, orders[j].StartupLead - s.Cycle));
                        heldReady[l] = gate + s.Cycle;
                        remaining[j]--;
                        break;
                    }
                }
            }
            return launches;
        }
    }

    internal static partial class Program
    {
        private sealed class SchedDispatch
        {
            internal string Ammo, Ship, Target, System;
            internal double SharedInterval;
            internal int Launchers, Usable;
        }

        private sealed class SchedStartup
        {
            internal double Cycle, Hatch;
        }

        private sealed class SchedOrder
        {
            internal string Run, Ammo, Target;
            internal double Register, StartupLead;
            internal int MaxGroupSize;
            internal double Compression;
            internal readonly List<double> Launches = new List<double>();
            internal readonly List<double> Ests = new List<double>();
            internal SchedDispatch Dispatch;
            internal double HeadLead;
            internal bool HeadPooled;

            internal int Wave1 => MaxGroupSize > 1 ? Math.Min(Launches.Count, MaxGroupSize) : Launches.Count;
            internal bool Grouped => MaxGroupSize > 1 && Wave1 > 1;
            internal double Est => Ests.Count > 0 ? Ests.Average() : 0.0;
        }

        /// <summary>One scored unit: an order fired alone on its ship's system in the run, or the batch of orders that
        /// shared it. A batch is scored as a whole because the scheduler centres a shared launch clock's rounds as one
        /// ripple, so a single order of a pool is deliberately off centre.
        ///
        /// Head and Sched are centre errors measured from each order's release, so they carry any error in the
        /// order's StartupLead. HeadRel and SchedRel measure the same centres from the unit's own first launch, which
        /// removes the first round's lead and leaves the cadence and queueing alone.</summary>
        private struct SchedScore
        {
            internal double Head, Sched, HeadRel, SchedRel, First;
            internal int Orders, Rounds;
        }

        private static readonly Regex DispatchLine = new Regex(
            @"\] dispatch (\S+) from (.+?) -> (.+?): declared onRailWarmup \S+, sharedLaunchInterval (\S+), " +
            @"predicted startupLead [\d.]+s \| (\d+) launcher\(s\), (\d+) usable(.*)$", RegexOptions.Compiled);

        private static readonly Regex LauncherEntry = new Regex(@"#-?\d+\[([^\]]+)\]", RegexOptions.Compiled);

        private static readonly Regex StartupLogLine = new Regex(
            @"\] launcher-startup (\S+) from (.+?): total [\d.]+s = .*?launcherCycle ([\d.]+)s.*?\| onRailWarmup \S+, hatchCycle ([\d.]+)s",
            RegexOptions.Compiled);

        /// <summary>
        /// L1/L2 scorer of docs/plans/open/model-and-launcher-audit.md: every archived live-test run's recorded launch
        /// times against two launcher models, both given each order's own StartupLead.
        ///
        /// HEAD: the scheduler as it stands, reimplemented from LauncherFacts.Compute (the ShotInterval floors),
        /// PrepareIntent (half span, or the full span for a grouped salvo) and PoolSharedLaunchClock. It predicts an
        /// order's mean launch offset from its release as StartupLead + ReleaseLead, or the last offset for a group.
        /// SCHED: <see cref="LaunchSchedule.Simulate"/> over every order the ship fired on that system in the run.
        ///
        /// The error is recorded minus predicted launch offset from the order's release, averaged over the unit's
        /// rounds (the last round of a grouped salvo). That average is what the arrivals are centred on, so it is the
        /// part of a schedule error that moves the time on target.
        ///
        /// Recorded side: each round capture's LaunchTime and RegisteredSim, joined to its order's `dispatch` line
        /// (launchers, shared interval, system name) and `launcher-startup` line (cycle, hatch). FireRate and salvo
        /// settings come from the game's weapons.ini by system name.
        ///
        /// Options: --overhead s (a reading of per-round dispatch time, never a fitted term); --last-round-waits (the
        /// ObjectBase.cs:3894 rule for every system); --last-round-waits-for a,b (only for those system names);
        /// --orders (a row per order).
        /// </summary>
        private static int ScheduleCommand(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab schedule <runs-dir> [--overhead s] [--last-round-waits] " +
                                        "[--last-round-waits-for sys,sys] [--orders]");
                return 2;
            }
            string root = args[0];
            double overhead = 0.0;
            bool printOrders = false, lastRoundAll = false;
            var lastRoundSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--overhead" && i + 1 < args.Length) overhead = double.Parse(args[++i], Inv);
                else if (args[i] == "--orders") printOrders = true;
                else if (args[i] == "--last-round-waits") lastRoundAll = true;
                else if (args[i] == "--last-round-waits-for" && i + 1 < args.Length)
                    foreach (string sys in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)) lastRoundSystems.Add(sys.Trim());
            }

            Dictionary<string, Dictionary<string, string>> weapons = ReadWeaponsIni();
            var coverage = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Count(string what, int by = 1) => coverage[what] = (coverage.TryGetValue(what, out int c) ? c : 0) + by;

            var rows = new List<string>();
            var csv = new StringBuilder(
                "run,ship,system,ammo,target,rounds,grouped,pooled,batchOrders,compression,launchers,usable,shared,fireInterval," +
                "cycle,headInterval,recFirst,recCentre,headCentre,schedCentre,errHead,errSched,errHeadRel,errSchedRel," +
                "recSpacing,schedSpacing,maxRoundErrSched\n");
            var singles = new SortedDictionary<string, List<SchedScore>>(StringComparer.Ordinal);
            var batches = new SortedDictionary<string, List<SchedScore>>(StringComparer.Ordinal);

            foreach (string run in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string runName = Path.GetFileName(run);
                string log = Path.Combine(run, "LogOutput.log");
                string solve = Path.Combine(run, "solve");
                string[] results = Directory.Exists(solve) ? Directory.GetFiles(solve, "*.result") : Array.Empty<string>();
                if (!File.Exists(log) || results.Length == 0) { Count("runs without captures"); continue; }
                Count("runs with captures");

                var dispatches = new List<SchedDispatch>();
                var startups = new Dictionary<string, SchedStartup>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(log))
                {
                    Match m = DispatchLine.Match(line);
                    if (m.Success)
                    {
                        Match sys = LauncherEntry.Match(m.Groups[7].Value);
                        dispatches.Add(new SchedDispatch
                        {
                            Ammo = m.Groups[1].Value, Ship = m.Groups[2].Value, Target = m.Groups[3].Value.Trim(),
                            SharedInterval = m.Groups[4].Value == "none" ? 0.0 : double.Parse(m.Groups[4].Value.TrimEnd('s'), Inv),
                            Launchers = int.Parse(m.Groups[5].Value, Inv), Usable = int.Parse(m.Groups[6].Value, Inv),
                            System = sys.Success ? sys.Groups[1].Value : null,
                        });
                        continue;
                    }
                    m = StartupLogLine.Match(line);
                    if (m.Success)
                        startups[m.Groups[1].Value + "|" + m.Groups[2].Value] = new SchedStartup
                        {
                            Cycle = double.Parse(m.Groups[3].Value, Inv), Hatch = double.Parse(m.Groups[4].Value, Inv),
                        };
                }

                var orders = new Dictionary<string, SchedOrder>(StringComparer.Ordinal);
                foreach (string file in results.OrderBy(f => f, StringComparer.Ordinal))
                {
                    Count("round captures");
                    Dictionary<string, string> r = ReadDump(file);
                    if (!r.TryGetValue("RegisteredSim", out string reg) || !r.TryGetValue("LaunchTime", out string launch) ||
                        !double.TryParse(reg, NumberStyles.Float, Inv, out double register) || register < 0)
                    {
                        Count("round captures without a registration stamp (before 2026-09-12)");
                        continue;
                    }
                    string ammo = r.TryGetValue("Label", out string label) ? label : "?";
                    string target = r.TryGetValue("Target", out string tg) ? tg.Trim() : "?";
                    string key = ammo + "|" + target + "|" + register.ToString("0.000", Inv);
                    if (!orders.TryGetValue(key, out SchedOrder o))
                    {
                        o = new SchedOrder
                        {
                            Run = runName, Ammo = ammo, Target = target, Register = register,
                            StartupLead = r.TryGetValue("StartupLead", out string sl) ? double.Parse(sl, Inv) : 0.0,
                            MaxGroupSize = r.TryGetValue("MaxGroupSize", out string mg) ? int.Parse(mg, Inv) : 0,
                            Compression = r.TryGetValue("MaxCompression", out string mc) ? double.Parse(mc, Inv) : 0.0,
                        };
                        orders[key] = o;
                    }
                    o.Launches.Add(double.Parse(launch, Inv));
                    if (r.TryGetValue("EstAtLaunch", out string est) && double.TryParse(est, NumberStyles.Float, Inv, out double e) && e > 0)
                        o.Ests.Add(e);
                }

                // An order's dispatch line is written when it is registered, so the k-th registration of an ammunition at a
                // target is its k-th dispatch line.
                var matched = new List<SchedOrder>();
                foreach (IGrouping<string, SchedOrder> g in orders.Values.GroupBy(o => o.Ammo + "|" + o.Target))
                {
                    List<SchedOrder> caps = g.OrderBy(o => o.Register).ToList();
                    List<SchedDispatch> lines = dispatches.Where(d => d.Ammo == caps[0].Ammo && d.Target == caps[0].Target).ToList();
                    if (lines.Count != caps.Count) { Count("orders whose dispatch lines do not pair one to one", caps.Count); continue; }
                    for (int k = 0; k < caps.Count; k++) caps[k].Dispatch = lines[k];
                    matched.AddRange(caps);
                }

                foreach (IGrouping<string, SchedOrder> batch in matched.GroupBy(o => o.Dispatch.Ship + "|" + o.Dispatch.System))
                {
                    List<SchedOrder> list = batch.OrderBy(o => o.Register).ToList();
                    SchedDispatch d = list[0].Dispatch;
                    if (d.System == null) { Count("orders with no launcher named", list.Count); continue; }
                    if (!startups.TryGetValue(list[0].Ammo + "|" + d.Ship, out SchedStartup st))
                    {
                        Count("orders with no launcher-startup line", list.Count);
                        continue;
                    }
                    weapons.TryGetValue(d.System, out Dictionary<string, string> sec);
                    int fireRate = IniInt(sec, "firerate", 60);
                    int salvoAmount = IniInt(sec, "salvofireamount", 1);
                    double salvoTime = IniDouble(sec, "salvofiretime", 0.0);
                    if (sec == null) Count("orders whose system has no weapons.ini section (defaults used)", list.Count);
                    if (salvoAmount > 1) { Count("orders on salvo launchers (not modelled)", list.Count); continue; }

                    var spec = new LaunchSchedule.Spec
                    {
                        Launchers = d.Launchers,
                        SharedInterval = d.SharedInterval,
                        FireInterval = fireRate > 0 ? 60.0 / fireRate : 0.0,
                        Cycle = st.Cycle,
                        RoundOverhead = overhead,
                        LastRoundWaitsForFirstLauncher = lastRoundAll || lastRoundSystems.Contains(d.System),
                    };
                    var targetIds = new Dictionary<string, int>(StringComparer.Ordinal);
                    List<double>[] sim = LaunchSchedule.Simulate(spec, list.Select(o => new LaunchSchedule.Order
                    {
                        RegisterSim = o.Register, StartupLead = o.StartupLead, Rounds = o.Launches.Count,
                        TargetId = targetIds.TryGetValue(o.Target, out int id) ? id : targetIds[o.Target] = targetIds.Count,
                    }).ToList());

                    double headInterval = HeadShotInterval(fireRate, salvoAmount, salvoTime, d.SharedInterval, st.Hatch, st.Cycle, d.Launchers);
                    foreach (SchedOrder o in list)
                        o.HeadLead = o.Grouped ? (o.Wave1 - 1) * headInterval : (o.Wave1 - 1) / 2.0 * headInterval;
                    if (d.SharedInterval > 0 && list.Count >= 2 && !list.Any(o => o.Grouped)) HeadPool(list, headInterval);

                    // Each model's first launch of the unit, and the recorded one, for the cadence-only error.
                    double recFirstAbs = list.SelectMany(o => o.Launches).Min();
                    double headFirstAbs = list.Min(o => o.Register + o.StartupLead);
                    double schedFirstAbs = sim.Any(x => x.Count > 0) ? sim.Where(x => x.Count > 0).SelectMany(x => x).Min() : double.NaN;

                    string family = StripUnitId(d.Ship) + " " + list[0].Ammo;
                    double batchHead = 0.0, batchSched = 0.0, batchHeadRel = 0.0, batchSchedRel = 0.0, batchFirst = 0.0;
                    int batchRounds = 0, batchOrders = 0;
                    for (int k = 0; k < list.Count; k++)
                    {
                        SchedOrder o = list[k];
                        List<double> rec = o.Launches.OrderBy(x => x).Select(x => x - o.Register).ToList();
                        List<double> pred = sim[k].OrderBy(x => x).Select(x => x - o.Register).ToList();
                        if (pred.Count != rec.Count) { Count("orders the simulation did not finish"); continue; }
                        Count("orders scored");
                        Count("rounds scored", o.Launches.Count);

                        double recCentre = o.Grouped ? rec[rec.Count - 1] : rec.Average();
                        double headCentre = o.StartupLead + o.HeadLead;
                        double schedCentre = o.Grouped ? pred[pred.Count - 1] : pred.Average();
                        double recSpacing = rec.Count > 1 ? (rec[rec.Count - 1] - rec[0]) / (rec.Count - 1) : double.NaN;
                        double schedSpacing = pred.Count > 1 ? (pred[pred.Count - 1] - pred[0]) / (pred.Count - 1) : double.NaN;
                        double maxRound = rec.Zip(pred, (a, b) => Math.Abs(a - b)).Max();
                        double errHead = recCentre - headCentre, errSched = recCentre - schedCentre;
                        double recRel = o.Register + recCentre - recFirstAbs;
                        double errHeadRel = recRel - (o.Register + headCentre - headFirstAbs);
                        double errSchedRel = recRel - (o.Register + schedCentre - schedFirstAbs);
                        double first = rec[0] - o.StartupLead;

                        // A grouped salvo counts once (its last round); independent rounds each count.
                        int weight = o.Grouped ? 1 : rec.Count;
                        batchHead += errHead * weight;
                        batchSched += errSched * weight;
                        batchHeadRel += errHeadRel * weight;
                        batchSchedRel += errSchedRel * weight;
                        batchFirst += first;
                        batchRounds += weight;
                        batchOrders++;

                        csv.Append(string.Join(",", SchedCsv(o.Run), SchedCsv(d.Ship), SchedCsv(d.System), o.Ammo, SchedCsv(o.Target),
                            o.Launches.Count.ToString(Inv), o.Grouped ? "1" : "0", o.HeadPooled ? "1" : "0", list.Count.ToString(Inv),
                            N(o.Compression), d.Launchers.ToString(Inv), d.Usable.ToString(Inv), N(d.SharedInterval), N(spec.FireInterval),
                            N(st.Cycle), N(headInterval), N(first), N(recCentre), N(headCentre), N(schedCentre), N(errHead), N(errSched),
                            N(errHeadRel), N(errSchedRel), N(recSpacing), N(schedSpacing), N(maxRound))).Append('\n');
                        rows.Add($"{o.Run,-44} {StripUnitId(d.Ship),-26} {o.Ammo,-15} n{o.Launches.Count,-3}" +
                                 $"{(o.Grouped ? "G" : o.HeadPooled ? "P" : " ")} B{list.Count} L{d.Launchers}/{d.Usable} " +
                                 $"S{d.SharedInterval,4:0.0} F{spec.FireInterval,5:0.0} C{st.Cycle,4:0.0} I{headInterval,5:0.0} | " +
                                 $"first {first,6:+0.00;-0.00} centre {recCentre,7:0.00} head {errHead,6:+0.00;-0.00} " +
                                 $"sched {errSched,6:+0.00;-0.00} rel {errHeadRel,6:+0.00;-0.00}/{errSchedRel,6:+0.00;-0.00} " +
                                 $"spacing {recSpacing,5:0.00}/{schedSpacing,5:0.00} maxRound {maxRound,5:0.00}");
                    }
                    if (batchOrders == 0) continue;
                    var score = new SchedScore
                    {
                        Head = batchHead / batchRounds, Sched = batchSched / batchRounds,
                        HeadRel = batchHeadRel / batchRounds, SchedRel = batchSchedRel / batchRounds,
                        First = batchFirst / batchOrders, Orders = batchOrders, Rounds = batchRounds,
                    };
                    SortedDictionary<string, List<SchedScore>> table = batchOrders >= 2 ? batches : singles;
                    if (!table.TryGetValue(family, out List<SchedScore> fl)) table[family] = fl = new List<SchedScore>();
                    fl.Add(score);
                }
            }

            if (printOrders) foreach (string row in rows) Console.WriteLine(row);

            Console.WriteLine();
            Console.WriteLine("coverage");
            foreach (KeyValuePair<string, int> c in coverage) Console.WriteLine($"  {c.Value,5}  {c.Key}");
            Console.WriteLine();
            Console.WriteLine($"centre error, recorded minus predicted launch offset (s); overhead {overhead:0.00}s; last round waits: " +
                              (lastRoundAll ? "every system" : lastRoundSystems.Count > 0 ? string.Join(",", lastRoundSystems) : "off") +
                              "; release = measured from each order's release, first launch = from the unit's own first launch " +
                              "(cadence only); first = first round minus StartupLead, mean per order");
            PrintScoreTable("orders fired alone on their ship's system (one row per order)", singles);
            PrintScoreTable("orders sharing a ship's system in one run (one row per batch, rounds weighted)", batches);

            string csvPath = Environment.GetEnvironmentVariable("AUTOTOT_SCHEDULE_CSV");
            if (!string.IsNullOrEmpty(csvPath)) File.WriteAllText(csvPath, csv.ToString());
            return 0;
        }

        private static void PrintScoreTable(string title, SortedDictionary<string, List<SchedScore>> table)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine($"{"family",-44} {"units",5} {"rounds",6} | {"from release: head",18} {"sched",11} | " +
                              $"{"from first launch: head",23} {"sched",11} | {"first mean",10}");
            Console.WriteLine($"{"",-44} {"",5} {"",6} | {"mean|e| max",18} {"mean|e| max",11} | {"mean|e| max",23} {"mean|e| max",11} |");
            var all = new List<SchedScore>();
            foreach (KeyValuePair<string, List<SchedScore>> f in table)
            {
                all.AddRange(f.Value);
                Console.WriteLine(ScoreRow(f.Key, f.Value));
            }
            Console.WriteLine(ScoreRow("ALL", all));
            Console.WriteLine(ScoreRow("ALL, units of 2+ rounds", all.Where(x => x.Rounds > 1).ToList()));
        }

        private static string ScoreRow(string name, List<SchedScore> v)
        {
            if (v.Count == 0) return $"{name,-44} {0,5}";
            string Pair(Func<SchedScore, double> f) =>
                $"{v.Average(x => Math.Abs(f(x))),5:0.00} {v.Max(x => Math.Abs(f(x))),5:0.00}";
            return $"{name,-44} {v.Count,5} {v.Sum(x => x.Rounds),6} | {Pair(x => x.Head),18} {Pair(x => x.Sched),11} | " +
                   $"{Pair(x => x.HeadRel),23} {Pair(x => x.SchedRel),11} | {v.Average(x => x.First),10:+0.00;-0.00}";
        }

        /// <summary>LauncherFactsSource.Compute's ShotInterval, term for term (Support/LauncherFacts.cs:359-418). The hatch
        /// floor there also needs more than one container per launcher, which no log line records, so it is applied
        /// whenever the hatch is longer than the interval.</summary>
        private static double HeadShotInterval(int fireRate, int salvoAmount, double salvoTime, double shared,
                                               double hatch, double cycle, int launchers)
        {
            const double MaxPlausible = 60.0;
            double interval = salvoAmount > 1 ? salvoTime : fireRate > 0 ? 60.0 / fireRate : 0.0;
            if (shared > interval) interval = shared;
            if (salvoAmount <= 1 && hatch > interval && hatch < MaxPlausible) interval = hatch;
            if (salvoAmount <= 1)
            {
                double perRound = cycle / Math.Max(1, launchers);
                if (perRound > interval && perRound < MaxPlausible) interval = perRound;
            }
            return interval;
        }

        /// <summary>Coordinator.PoolSharedLaunchClock and ApplyClockPool (Core/Coordinator.cs:1210-1319), with each
        /// order's commit flight estimate taken as its rounds' mean EstAtLaunch.</summary>
        private static void HeadPool(List<SchedOrder> orders, double interval)
        {
            if (interval <= 0) return;
            List<SchedOrder> sorted = orders.OrderByDescending(o => o.Est).ToList();
            for (int start = 0; start < sorted.Count; )
            {
                double longest = sorted[start].Est;
                int rounds = sorted[start].Wave1;
                int end = start + 1;
                while (end < sorted.Count && longest - sorted[end].Est < rounds * interval)
                {
                    rounds += sorted[end].Wave1;
                    end++;
                }
                if (end - start >= 2)
                {
                    double weighted = 0.0;
                    for (int j = start; j < end; j++) weighted += sorted[j].Wave1 * (longest - sorted[j].Est);
                    double lead = Math.Max(0.0, (rounds - 1) * 0.5 * interval - weighted / rounds);
                    for (int j = start; j < end; j++) { sorted[j].HeadLead = lead; sorted[j].HeadPooled = true; }
                }
                start = end;
            }
        }

        /// <summary>weapons.ini sections from the base game, then any Workshop item's for a section the base lacks. Keys
        /// are lower case. The game reads them by system name (ObjectBaseLoader.cs:2801).</summary>
        private static Dictionary<string, Dictionary<string, string>> ReadWeaponsIni()
        {
            string game = Environment.GetEnvironmentVariable("AUTOTOT_GAME_DIR");
            if (string.IsNullOrEmpty(game)) game = "/NEW-DRIVE/SteamLibrary/steamapps/common/Sea Power";
            var files = new List<string> { Path.Combine(game, "Sea Power_Data/StreamingAssets/original/systems/weapons.ini") };
            string workshop = Path.GetFullPath(Path.Combine(game, "../../workshop/content/1286220"));
            if (Directory.Exists(workshop))
                files.AddRange(Directory.GetDirectories(workshop).OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => Path.Combine(x, "systems/weapons.ini")).Where(File.Exists));

            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files.Where(File.Exists))
            {
                var seenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> current = null;
                foreach (string raw in File.ReadLines(file, Encoding.Latin1))
                {
                    string line = raw;
                    int comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line.Substring(0, comment);
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        string name = close > 1 ? line.Substring(1, close - 1).Trim() : "";
                        if (sections.ContainsKey(name) && !seenHere.Contains(name)) { current = null; continue; }
                        seenHere.Add(name);
                        current = sections.TryGetValue(name, out var existing) ? existing
                            : sections[name] = new Dictionary<string, string>(StringComparer.Ordinal);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (current == null || eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    if (!current.ContainsKey(k)) current[k] = line.Substring(eq + 1).Trim();
                }
            }
            return sections;
        }

        private static int IniInt(Dictionary<string, string> sec, string key, int fallback) =>
            sec != null && sec.TryGetValue(key, out string v) && int.TryParse(v, NumberStyles.Integer, Inv, out int i) ? i : fallback;

        private static double IniDouble(Dictionary<string, string> sec, string key, double fallback) =>
            sec != null && sec.TryGetValue(key, out string v) && double.TryParse(v, NumberStyles.Float, Inv, out double d) ? d : fallback;

        private static string StripUnitId(string ship)
        {
            int close = ship.IndexOf("] ", StringComparison.Ordinal);
            return ship.StartsWith("[", StringComparison.Ordinal) && close > 0 ? ship.Substring(close + 2) : ship;
        }

        private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", Inv);

        private static string SchedCsv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
