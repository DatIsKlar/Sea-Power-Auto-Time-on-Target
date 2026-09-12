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
    /// The corpus on disk: importing captures, building the index, and resolving corpus arguments.
    /// </summary>
    internal static partial class Program
    {
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
            string dir = CorpusArg(args, 0);
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"corpus not found: {dir}");
                return 1;
            }

            var buildMap = ReadBuildMap(dir);
            var rows = new List<(string Result, Dictionary<string, string> Outcome,
                                 Dictionary<string, string> Input, string Key, string Kinematics,
                                 string Schema, string Build, string Sha, string Note)>();
            foreach (string result in Directory.GetFiles(dir, "*.result"))
            {
                Dictionary<string, string> outcome = ReadDump(result);
                string inputFile = Path.ChangeExtension(result, ".solveinput");
                Dictionary<string, string> input = File.Exists(inputFile)
                    ? ReadDump(inputFile) : new Dictionary<string, string>();
                string key = outcome.GetValueOrDefault(
                    "Key", Path.GetFileNameWithoutExtension(result));
                string kinematics = KinematicsClass(input);
                string schema = input.GetValueOrDefault("CaptureSchema",
                    outcome.GetValueOrDefault("CaptureSchema", ""));
                if (schema.Length == 0) schema = input.ContainsKey("Ammo.TerminalAltRaw") ? "raw-waypoint"
                    : input.ContainsKey("ConfiguredLoftAlt") ? "optimized-loft" : "legacy";
                string build = input.GetValueOrDefault(
                    "ModBuild", outcome.GetValueOrDefault("ModBuild", "unknown"));
                string sha = "", note = "";
                if (buildMap.TryGetValue(key, out var known))
                {
                    build = known.Build;
                    sha = known.Sha;
                    note = known.Note;
                }
                rows.Add((result, outcome, input, key, kinematics, schema, build, sha, note));
            }

            var b = new System.Text.StringBuilder();
            b.AppendLine("key,kinematics,ammo,ammoFingerprint,modBuild,artifactSha256,captureSchema,buildNote," +
                         "target,rangeU,actualFlight,estAtLaunch,actualMinusLaunchEstimate,arrived,finalRangeM," +
                         "retargeted,motor,minCompression,maxCompression,flownStep," +
                         "peakSpeedKn,peakAltU,terminalSpeedKn,wpEst,legacyEst," +
                         "targetCourse0,targetSpeed0,targetCourse1,targetSpeed1,targetMovedU," +
                         "trackSamples,flightTrackSamples,stageChanges,stageEvents,groupChanges,inGroup," +
                         "groupLeader,maxGroupSize,gameVersion,gameBranch,modVersion,capturedAt");

            int n = 0;
            foreach (var row in rows.OrderBy(r => r.Kinematics == "kinematic" ? 0 : 1)
                                    .ThenBy(r => r.Outcome.GetValueOrDefault("Label", ""))
                                    .ThenBy(r => r.Outcome.GetValueOrDefault("CapturedAt", ""))
                                    .ThenBy(r => r.Key))
            {
                Dictionary<string, string> r = row.Outcome;
                Dictionary<string, string> i = row.Input;

                // Production diagnostic convention: actual - estimate. Positive means the model
                // predicted too little flight time. This is the recorded launch estimate, not the
                // motor-normalized current-model replay printed by Score.
                float actual = F(r, "ActualFlight"), est = F(r, "EstAtLaunch");
                b.Append(Csv(row.Key)).Append(',')
                 .Append(row.Kinematics).Append(',')
                 .Append(Csv(r.GetValueOrDefault("Label", ""))).Append(',')
                 .Append(Csv(i.GetValueOrDefault("AmmoFingerprint",
                                  r.GetValueOrDefault("AmmoFingerprint", "")))).Append(',')
                 .Append(Csv(row.Build)).Append(',')
                 .Append(Csv(row.Sha)).Append(',')
                 .Append(row.Schema).Append(',')
                 .Append(Csv(row.Note)).Append(',')
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
                 .Append(N(F(r, "PeakSpeedKn"))).Append(',')
                 .Append(N(F(r, "PeakAltU"))).Append(',')
                 .Append(N(F(r, "TerminalSpeedKn"))).Append(',')
                 .Append(N(F(r, "WpEstAtLaunch"))).Append(',')
                 .Append(N(F(r, "LegacyEstAtLaunch"))).Append(',')
                 .Append(N(F(r, "TargetCourseAtLaunch"))).Append(',')
                 .Append(N(F(r, "TargetSpeedAtLaunch"))).Append(',')
                 .Append(N(F(r, "TargetCourseAtEnd"))).Append(',')
                 .Append(N(F(r, "TargetSpeedAtEnd"))).Append(',')
                 .Append(N(F(r, "TargetMovedU"))).Append(',')
                 .Append(SeriesCount(r.GetValueOrDefault("Track", ""))).Append(',')
                 .Append(SeriesCount(r.GetValueOrDefault("FlightTrack", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("StageChanges", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("StageEvents", ""))).Append(',')
                 .Append(Csv(r.GetValueOrDefault("GroupChanges", ""))).Append(',')
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

        private static string KinematicsClass(Dictionary<string, string> input)
        {
            if (input.ContainsKey("NonKin"))
                return B(input, "NonKin") ? "non-kinematic" : "kinematic";
            string raw = input.GetValueOrDefault("Ammo.Kinematics", "");
            if (raw.Length == 0) return "unknown";
            return string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase)
                ? "non-kinematic" : "kinematic";
        }

        /// <summary>Per-capture build provenance for artifacts that predate the automatic MVID
        /// field. Unknown historical builds stay unknown rather than being inferred from version.</summary>
        private static Dictionary<string, (string Build, string Sha, string Note)> ReadBuildMap(
            string dir)
        {
            var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
            string file = Path.Combine(dir, "build-map.csv");
            if (!File.Exists(file)) return map;
            foreach (string raw in File.ReadLines(file).Skip(1))
            {
                string[] p = raw.Split(new[] { ',' }, 4);
                if (p.Length < 3 || p[0].Length == 0) continue;
                map[p[0]] = (p[1], p[2], p.Length > 3 ? p[3] : "");
            }
            return map;
        }

        private static string N(float v) => v == 0f ? "" : v.ToString("0.###", Inv);

        private static int SeriesCount(string value) =>
            string.IsNullOrEmpty(value) ? 0 : value.Count(c => c == ';') + 1;

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
            string to = CorpusArg(args, 1);

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

        /// <summary>
        /// A corpus directory argument: absent, or "-", means the corpus checked in beside the lab
        /// (README, "Commands"). One helper, because the rule was written out five times and two of
        /// them (index, import) treated "-" as a folder name.
        /// </summary>
        private static string CorpusArg(string[] args, int index)
            => args.Length > index && args[index] != "-" ? args[index] : DefaultCorpus();

        /// <summary>The corpus checked in beside the lab, which is the one that should grow.</summary>
        private static string DefaultCorpus()
        {
            // Walk up out of bin/Debug/netX so a plain `dotnet run` finds the source tree copy.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lab.csproj")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? ".", "corpus");
        }
    }
}
