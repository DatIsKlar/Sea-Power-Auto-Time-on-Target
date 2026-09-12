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
    ///
    /// This file holds the entry point and the command dispatch. The commands live beside it:
    /// LabScore.cs (score, parity), LabCorpus.cs (import, index), LabInspect.cs (trace, climb, the
    /// bare replay), LabSelfTest.cs (selftest), and LabDump.cs (reading a capture back into a
    /// SolveInput).
    /// </summary>
    internal static partial class Program
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // Tolerances for values serialized by different generations of captures.
        private const float SerializedValueTolerance = 0.01f;
        private const float SteadyStepRelativeTolerance = 0.01f;

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
                    "       autotot-lab index [corpus-dir]\n" +
                    "       autotot-lab trace <solveinput-file> [step]\n" +
                    "       autotot-lab climb <solveinput-file>\n" +
                    "       autotot-lab selftest [corpus-dir]\n" +
                    "       autotot-lab parity [corpus-dir] [--launch-estimate]");
                return 2;
            }

            // Scores the same build with the non-kinematic waypoint route on and off.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_WAYPOINT_LEGS") == "1")
                FlightTime.WaypointLegSteering = false;

            if (args[0] == "import") return Import(args.Skip(1).ToArray());
            if (args[0] == "index") return Index(args.Skip(1).ToArray());
            if (args[0] == "trace") return Trace(args.Skip(1).ToArray());
            if (args[0] == "climb") return Climb(args.Skip(1).ToArray());
            if (args[0] == "selftest") return SelfTest(args.Skip(1).ToArray());
            if (args[0] == "parity") return Parity(args.Skip(1).ToArray());

            bool score = args[0] == "score";
            if (score) args = args.Skip(1).ToArray();
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab score <corpus-dir> [step ...]");
                return 2;
            }

            string path = CorpusArg(args, 0);
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
    }
}
