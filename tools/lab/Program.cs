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
                    "       autotot-lab trace <solveinput-file> [step] [--motor v]\n" +
                    "       autotot-lab climb <solveinput-file>\n" +
                    "       autotot-lab motor <solveinput-file> [scale ...]\n" +
                    "       autotot-lab selftest [corpus-dir]\n" +
                    "       autotot-lab parity [corpus-dir] [--launch-estimate]\n" +
                    "       autotot-lab schedule <runs-dir> [--overhead s] [--orders]");
                return 2;
            }

            // Scores the same build with the non-kinematic waypoint route on and off.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_WAYPOINT_LEGS") == "1")
                FlightTime.WaypointLegSteering = false;
            // Scores the same build with and without the ToBearing pitch rule (B3).
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_TOBEARING_OWN_PITCH") == "1")
                FlightTime.ToBearingOverrideOwnsPitch = false;
            // The upstream corrections (max-solo-error-below-1s.md, "Variant A build" S2), each off for an A/B.
            // The look-ahead sees a pop-up leg's own curve.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_POPUP_LOOKAHEAD") == "1")
                FlightTime.PopUpLookAhead = false;
            // The terminal vertical turn rate for the whole TerminalApproach stage.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_TERMINAL_TURN_STAGE") == "1")
                FlightTime.TerminalTurnWholeStage = false;
            // The active pop-up leg's pitch is held to its pop-up angle.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_POPUP_CLAMP") == "1")
                FlightTime.PopUpAngleClamp = false;
            // A kinematic loft round's command from the live KappaLoft curve.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_LIVE_KAPPA") == "1")
                FlightTime.LiveKappaLoft = false;
            // Vector3.RotateTowards's dead band in the kinematic mover.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_ROTATE_DEADBAND") == "1")
                FlightTime.RotateDeadBand = false;
            // A kinematic terminal leg entered at TermDist and flown on its line.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_KIN_TERMINAL_LINE") == "1")
                FlightTime.KinematicTerminalLine = false;
            // Counterfactual: the pitch rate handed to CalculateDrag, scaled.
            FlightTime.OfflineDragPitchRateScale = float.TryParse(
                System.Environment.GetEnvironmentVariable("AUTOTOT_DRAG_PITCHRATE"),
                NumberStyles.Float, Inv, out float dragRateScale) ? dragRateScale : 1f;
            // Counterfactuals Q, R and K of the production-step lateness (max-solo-error-below-1s.md, "PL2 revised").
            FlightTime.OfflineArrivalInterp = System.Environment.GetEnvironmentVariable("AUTOTOT_ARRIVAL_INTERP") == "1";
            FlightTime.OfflineGameRounding = System.Environment.GetEnvironmentVariable("AUTOTOT_GAME_ROUNDING") == "1";
            FlightTime.OfflineMotorSubsteps = System.Environment.GetEnvironmentVariable("AUTOTOT_MOTOR_SUBSTEPS") == "1";
            // The seeker-window drag brake and the kinematic terminal line's look-ahead, each off for an A/B.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_SEEKER_BRAKE") == "1")
                FlightTime.SeekerWindowBrake = false;
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_KIN_LINE_LOOKAHEAD") == "1")
                FlightTime.KinematicLineLookAhead = false;
            // The double-summed flight time, off to compare with the float32 clock.
            if (System.Environment.GetEnvironmentVariable("AUTOTOT_NO_DOUBLE_CLOCK") == "1")
                FlightTime.DoubleClock = false;
            // Counterfactuals M1 (ToBearing left for good) and M2 (TerminalDelay in every terminal consumer) of
            // docs/plans/open/model-and-launcher-audit.md.
            FlightTime.OfflineToBearingLatch = System.Environment.GetEnvironmentVariable("AUTOTOT_TOBEARING_LATCH") == "1";
            FlightTime.OfflineTerminalDelayGate = System.Environment.GetEnvironmentVariable("AUTOTOT_TERMINAL_DELAY_GATE") == "1";
            // Counterfactual M4: substeps only on the step in which the motor burns out.
            FlightTime.OfflineBurnoutSubsteps = System.Environment.GetEnvironmentVariable("AUTOTOT_BURNOUT_SUBSTEPS") == "1";

            if (args[0] == "import") return Import(args.Skip(1).ToArray());
            if (args[0] == "index") return Index(args.Skip(1).ToArray());
            if (args[0] == "trace") return Trace(args.Skip(1).ToArray());
            if (args[0] == "leadcheck") return LeadCheck(args.Skip(1).ToArray());
            if (args[0] == "pathcmp") return PathCompare(args.Skip(1).ToArray());
            if (args[0] == "stepcmp") return StepCompare(args.Skip(1).ToArray());
            if (args[0] == "popcheck") return PopUpCheck(args.Skip(1).ToArray());
            if (args[0] == "kappacheck") return KappaCheck(args.Skip(1).ToArray());
            if (args[0] == "climb") return Climb(args.Skip(1).ToArray());
            if (args[0] == "motor") return Motor(args.Skip(1).ToArray());
            if (args[0] == "selftest") return SelfTest(args.Skip(1).ToArray());
            if (args[0] == "parity") return Parity(args.Skip(1).ToArray());
            if (args[0] == "schedule") return ScheduleCommand(args.Skip(1).ToArray());
            if (args[0] == "gates") return GatesCommand(args.Skip(1).ToArray());

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
