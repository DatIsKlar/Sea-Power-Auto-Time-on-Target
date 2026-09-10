using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT.Lab
{
    /// <summary>
    /// Replays a solver input captured in game, at whatever integration step sizes are asked for.
    ///
    /// Every solve here uses the identical inputs, so a difference between two rows is caused by
    /// the step size and nothing else. That is what the in-game route could not give us: two
    /// flights are never fired from the same position at the same instant, and each round draws its
    /// own random motor performance, both of which move the answer by more than the effect being
    /// measured.
    ///
    /// Usage:
    ///   autotot-lab &lt;dump-dir-or-file&gt; [step ...]
    /// </summary>
    internal static class Program
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 0.1 is production. 0.0166667 and 0.0333333 are the game's own 60 Hz and 30 Hz physics
        // steps, which is the comparison the experiment is about.
        private static readonly float[] DefaultSteps = { 0.1f, 0.05f, 0.0333333f, 0.0166667f, 0.005f };

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(
                    "usage: autotot-lab [score] <dump-dir-or-file> [step ...]\n" +
                    "       autotot-lab import <capture-dir> [corpus-dir]\n" +
                    "       autotot-lab index [corpus-dir]");
                return 2;
            }

            if (args[0] == "import") return Import(args.Skip(1).ToArray());
            if (args[0] == "index") return Index(args.Skip(1).ToArray());

            bool score = args[0] == "score";
            if (score) args = args.Skip(1).ToArray();
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab score <corpus-dir> [step ...]");
                return 2;
            }

            string path = args[0] == "-" ? DefaultCorpus() : args[0];
            float[] steps = args.Length > 1
                ? args.Skip(1).Select(a => float.Parse(a, Inv)).ToArray()
                : DefaultSteps;

            string[] files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.solveinput").OrderBy(f => f).ToArray()
                : new[] { path };

            if (files.Length == 0)
            {
                Console.Error.WriteLine($"no .solveinput files under {path}");
                return 1;
            }

            if (score) return Score(files, steps);

            foreach (string file in files) Replay(file, steps);
            return 0;
        }

        /// <summary>
        /// Writes one row per stored round to index.csv, so the corpus can be sorted, filtered and
        /// plotted in a spreadsheet without opening any of it.
        ///
        /// Deliberately a derived file, not the store. The per-round text files remain the source of
        /// truth: a .solveinput is replay input rather than a table row, so putting the corpus in a
        /// real database would replace only the small half of it, and would cost the diffs and
        /// hand-editability that make a checked-in corpus pleasant to live with. The index is
        /// regenerated, never edited.
        /// </summary>
        private static int Index(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : DefaultCorpus();
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"corpus not found: {dir}");
                return 1;
            }

            var b = new System.Text.StringBuilder();
            b.AppendLine("key,ammo,target,rangeU,actualFlight,estAtLaunch,gap,arrived,finalRangeM," +
                         "retargeted,motor,minCompression,maxCompression,flownStep,inGroup," +
                         "groupLeader,maxGroupSize,gameVersion,gameBranch,modVersion,capturedAt");

            int n = 0;
            foreach (string result in Directory.GetFiles(dir, "*.result").OrderBy(f => f))
            {
                Dictionary<string, string> r = ReadDump(result);
                string inputFile = Path.ChangeExtension(result, ".solveinput");
                Dictionary<string, string> i = File.Exists(inputFile)
                    ? ReadDump(inputFile) : new Dictionary<string, string>();

                float actual = F(r, "ActualFlight"), est = F(r, "EstAtLaunch");
                b.Append(Csv(r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(result)))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("Label", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("Target", ""))).Append(',')
                 .Append(N(F(i, "FlatDistTotal"))).Append(',')
                 .Append(N(actual)).Append(',')
                 .Append(N(est)).Append(',')
                 .Append(est > 0f ? N(actual - est) : "").Append(',')
                 .Append(r.GetValueOrDefault("Arrived", "")).Append(',')
                 .Append(N(F(r, "FinalRangeM"))).Append(',')
                 .Append(r.GetValueOrDefault("Retargeted", "")).Append(',')
                 .Append(N(F(r, "MotorPerformance"))).Append(',')
                 .Append(N(F(r, "MinCompression"))).Append(',')
                 .Append(N(F(r, "MaxCompression"))).Append(',')
                 .Append(N(F(r, "MaxFlownStep"))).Append(',')
                 .Append(r.GetValueOrDefault("InGroup", "")).Append(',')
                 .Append(r.GetValueOrDefault("GroupLeader", "")).Append(',')
                 .Append(r.GetValueOrDefault("MaxGroupSize", "")).Append(',')
                 .Append(Csv(r.GetValueOrDefault("GameVersion", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("GameBranch", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("ModVersion", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("CapturedAt", "")));
                b.AppendLine();
                n++;
            }

            string file = Path.Combine(dir, "index.csv");
            File.WriteAllText(file, b.ToString());
            Console.WriteLine($"{n} round(s) -> {file}");
            return 0;
        }

        private static string N(float v) => v == 0f ? "" : v.ToString("0.###", Inv);

        private static string Csv(string v) =>
            v != null && (v.Contains(',') || v.Contains('"'))
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;

        /// <summary>
        /// Merges freshly captured rounds into the stored corpus.
        ///
        /// Copies only complete pairs, and never overwrites: a key already present is left alone
        /// rather than replaced, so the corpus only ever grows and a re-import cannot rewrite
        /// history. Keys carry a wall clock and a sequence number, so two captures collide only if
        /// they are genuinely the same round.
        /// </summary>
        private static int Import(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab import <capture-dir> [corpus-dir]");
                return 2;
            }

            string from = args[0];
            string to = args.Length > 1 ? args[1] : DefaultCorpus();

            if (!Directory.Exists(from))
            {
                Console.Error.WriteLine($"capture directory not found: {from}");
                return 1;
            }
            Directory.CreateDirectory(to);

            int added = 0, already = 0, incomplete = 0;
            foreach (string input in Directory.GetFiles(from, "*.solveinput").OrderBy(f => f))
            {
                string key = Path.GetFileNameWithoutExtension(input);
                string result = Path.ChangeExtension(input, ".result");

                // An input with no outcome is a round still in flight, or one whose flight was
                // never finalised. Left in place, so a later import picks it up once it lands.
                if (!File.Exists(result)) { incomplete++; continue; }

                string destInput = Path.Combine(to, key + ".solveinput");
                string destResult = Path.Combine(to, key + ".result");
                if (File.Exists(destInput) && File.Exists(destResult)) { already++; continue; }

                File.Copy(input, destInput, false);
                File.Copy(result, destResult, false);
                added++;
                Console.WriteLine($"  added  {key}");
            }

            Console.WriteLine();
            Console.WriteLine($"{added} added, {already} already present, {incomplete} awaiting an outcome.");
            Console.WriteLine($"corpus: {Path.GetFullPath(to)} " +
                              $"({Directory.GetFiles(to, "*.result").Length} round(s) total)");
            return 0;
        }

        /// <summary>The corpus checked in beside the lab, which is the one that should grow.</summary>
        private static string DefaultCorpus()
        {
            // Walk up out of bin/Debug/netX so a plain `dotnet run` finds the source tree copy.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lab.csproj")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? ".", "corpus");
        }

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
                                  Math.Abs(stepHi - stepLo) <= 0.01f * stepHi;

                string reason =
                    !B(r, "Arrived")   ? $"no arrival ({F(r, "FinalRangeM"):F0} m out)"
                  : B(r, "Retargeted") ? "seeker switched target, flight is to a different ship"
                  : I(r, "Claims") > 1 ? "several rounds shared one solver input"
                  : (stepHi > 0f && !stepSteady)
                        ? $"physics step moved in flight ({stepLo:F4}s to {stepHi:F4}s), " +
                          "so no single step describes it"
                  : null;
                if (reason != null) { skipped.Add($"{label}: {reason}"); continue; }

                Dictionary<string, string> kv = ReadDump(file);
                AmmunitionParameters ap = BuildAmmo(kv);
                FlightTime.SolveInput input = BuildInput(kv);

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
            foreach (float dt in steps) Console.Write($"{"gap@" + dt.ToString("0.####", Inv),12}");
            Console.Write($"{"flown step",12}{"gap@flown",12}");
            Console.WriteLine();
            Console.WriteLine(new string('-', 59 + 12 * steps.Length));

            foreach (var row in rows.OrderBy(r => r.Grouped).ThenBy(r => r.Label))
            {
                string name = row.Grouped ? Trim(row.Label, 22) + " [grp]" : Trim(row.Label, 25);
                Console.Write($"{name,-26}{row.Actual,8:F1}s");
                for (int k = 0; k < steps.Length; k++)
                    Console.Write(row.Est[k] > 0f ? $"{row.Est[k] - row.Actual,12:+0.00;-0.00}" : $"{"failed",12}");
                Console.Write(row.Flown > 0f ? $"{row.Flown,12:F4}" : $"{"-",12}");
                Console.Write(row.FlownEst > 0f ? $"{row.FlownEst - row.Actual,12:+0.00;-0.00}" : $"{"-",12}");
                Console.WriteLine();
            }

            Console.WriteLine(new string('-', 59 + 12 * steps.Length));

            MeanRow("mean |gap| solo", rows.Where(r => !r.Grouped).ToList(), steps);
            var grouped = rows.Where(r => r.Grouped).ToList();
            if (grouped.Count > 0) MeanRow("mean |gap| grouped", grouped, steps);

            Console.WriteLine($"\n{rows.Count} round(s) scored, {skipped.Count} excluded.");
            foreach (string sk in skipped) Console.WriteLine("  excluded  " + sk);
            return 0;
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

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";

        private static void Replay(string file, float[] steps)
        {
            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv);

            Console.WriteLine();
            Console.WriteLine($"=== {kv.GetValueOrDefault("Label", Path.GetFileNameWithoutExtension(file))} ===");
            Console.WriteLine($"    range {input.FlatDistTotal:F0}u, {(input.NonKin ? "non-kinematic" : "kinematic")}, " +
                              $"max {input.MaxVelKn:F0}kn, launch pitch {input.LaunchPitch:F1} deg");
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
                string delta = ReferenceEquals(null, null) && dt == steps[0]
                    ? "     -   "
                    : $"{est - baseline,+9:F3}";

                Console.WriteLine($"    {dt,6:F4}    {est,7:F2}s   {delta}   {(int)(est / dt),7}    {sw.Elapsed.TotalMilliseconds,6:F1}ms");
            }
        }

        private static Dictionary<string, string> ReadDump(string file)
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return kv;
        }

        /// <summary>
        /// Rebuilds the ammunition object by field, because the game populates it from an ini at
        /// load and there is no constructor that takes values.
        /// </summary>
        private static AmmunitionParameters BuildAmmo(Dictionary<string, string> kv)
        {
            var ap = new AmmunitionParameters();
            Type t = typeof(AmmunitionParameters);

            // MinVelocity is not stored: the game computes it as min(_maxVelocityInKnots * 0.33,
            // 600), so setting the max speed below reproduces it. The dump still records it, and
            // the check after this block confirms the reconstruction agrees with what was logged.
            SetMember(t, ap, "LiftFactor", F(kv, "Ammo.LiftFactor"));
            SetMember(t, ap, "_acceleration", F(kv, "Ammo.Acceleration"));
            SetMember(t, ap, "_accelerationTime", F(kv, "Ammo.AccelerationTime"));
            SetMember(t, ap, "_sustainerBurnAcceleration", F(kv, "Ammo.SustainerBurnAcceleration"));
            SetMember(t, ap, "_sustainerBurnTime", F(kv, "Ammo.SustainerBurnTime"));
            SetMember(t, ap, "_maxVelocityInKnots", F(kv, "Ammo.MaxVelocityInKnots"));

            // Kinematics is derived too: the game reads a nullable bool out of the ini and
            // reports Full only when it is explicitly true.
            string kin = kv.GetValueOrDefault("Ammo.Kinematics", "None");
            SetMember(t, ap, "_useKinematics", (bool?)(kin == "Full"));

            float expected = F(kv, "Ammo.MinVelocity");
            if (expected > 0f && Mathf.Abs(ap.MinVelocity - expected) > 0.01f)
                throw new InvalidOperationException(
                    $"rebuilt ammunition disagrees with the dump: MinVelocity {ap.MinVelocity} " +
                    $"but the game logged {expected}");

            return ap;
        }

        private static void SetMember(Type t, object target, string name, object value)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, Any);
            if (f != null) { f.SetValue(target, Coerce(value, f.FieldType)); return; }

            PropertyInfo p = t.GetProperty(name, Any);
            if (p != null && p.CanWrite) { p.SetValue(target, Coerce(value, p.PropertyType)); return; }

            // A backing field is still writable even when the property is get-only.
            FieldInfo backing = t.GetField($"<{name}>k__BackingField", Any);
            if (backing != null) { backing.SetValue(target, Coerce(value, backing.FieldType)); return; }

            throw new InvalidOperationException($"AmmunitionParameters has no writable member '{name}'");
        }

        /// <summary>Convert.ChangeType cannot target Nullable&lt;T&gt;, which _useKinematics is.</summary>
        private static object Coerce(object value, Type target)
        {
            Type inner = Nullable.GetUnderlyingType(target);
            if (inner != null) return value == null ? null : Convert.ChangeType(value, inner);
            return Convert.ChangeType(value, target);
        }

        private static FlightTime.SolveInput BuildInput(Dictionary<string, string> kv) =>
            new FlightTime.SolveInput(
                Nodes(kv, "AltNodes"),
                kv.GetValueOrDefault("AmmoLabel", ""),
                F(kv, "BoostClimbDeg"),
                F(kv, "DecelRate"),
                F(kv, "DescentDeg"),
                F(kv, "DescentOnsetDeg"),
                F(kv, "DragFactor"),
                F(kv, "CruiseAlt"),
                F(kv, "FinalFlightAlt"),
                F(kv, "FinalFlightDist"),
                F(kv, "FinalDist"),
                F(kv, "FlatDistTotal"),
                F(kv, "InitialPhaseDur"),
                B(kv, "IsAir"),
                B(kv, "IsHighBallisticLofter"),
                B(kv, "IsTerminalLoft"),
                F(kv, "LaunchPitch"),
                F(kv, "LoftAlt"),
                F(kv, "LoftEntryDist"),
                F(kv, "LoftSpeedHoldDist"),
                F(kv, "LoftVelKn"),
                B(kv, "Lofting"),
                F(kv, "MaxFlight"),
                F(kv, "MaxVelKn"),
                B(kv, "NonKin"),
                F(kv, "TargetAlt0"),
                V3(kv, "TargetPos"),
                V3(kv, "TargetVel"),
                F(kv, "TermAlt"),
                F(kv, "TermDist"),
                F(kv, "TermVelKn"),
                false,                       // TrackDiag: the lab reports its own comparison
                F(kv, "TurnRate"),
                F(kv, "ToBearingTurnRate"),
                F(kv, "TurnRateBase"),
                F(kv, "TurnDerateThresholdKn"),
                I(kv, "AltLatchPhase"),
                B(kv, "AltLatched"),
                Q(kv, "Att"),
                V3(kv, "LaunchHeading"),
                F(kv, "NextSample"),
                V3(kv, "Pos"),
                F(kv, "PrevAltErr"),
                F(kv, "PrevFlat"),
                F(kv, "PrevPitch"),
                F(kv, "T"),
                B(kv, "TlGliding"),
                F(kv, "VelKnots"));

        private static float F(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v.Length > 0 ? float.Parse(v, Inv) : 0f;

        private static int I(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v.Length > 0 ? int.Parse(v, Inv) : 0;

        private static bool B(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v == "true";

        private static Vector3 V3(Dictionary<string, string> kv, string k)
        {
            string[] p = kv.GetValueOrDefault(k, "0,0,0").Split(',');
            return new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv));
        }

        private static Quaternion Q(Dictionary<string, string> kv, string k)
        {
            string[] p = kv.GetValueOrDefault(k, "0,0,0,1").Split(',');
            return new Quaternion(float.Parse(p[0], Inv), float.Parse(p[1], Inv),
                                  float.Parse(p[2], Inv), float.Parse(p[3], Inv));
        }

        private static Vector2[] Nodes(Dictionary<string, string> kv, string k)
        {
            string v = kv.GetValueOrDefault(k, "");
            if (string.IsNullOrEmpty(v)) return null;
            return v.Split(';').Select(pair =>
            {
                string[] p = pair.Split(',');
                return new Vector2(float.Parse(p[0], Inv), float.Parse(p[1], Inv));
            }).ToArray();
        }
    }
}
