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
    /// The selftest command: fixed checks that the lab still reconstructs the solver input and the
    /// route the game builds, and still reads legacy captures.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>Small invariant tests for the stage/Kappa regression fixes. These deliberately
        /// exercise the same pure selectors used by Solve, plus both legacy dump reconstruction
        /// paths, so failures identify the rule rather than merely moving the corpus average.</summary>
        private static int SelfTest(string[] args)
        {
            string corpus = CorpusArg(args, 0);
            try
            {
                const float targetAlt = 3f;
                const float finalAlt = 120f;
                const float cruiseAlt = 480f;

                // An explicit final stage is resolved before the unstaged fallback and retains its
                // configured altitude. Only the kinematic/no-loft fallthrough becomes target-level.
                AssertNear("explicit final altitude",
                    FlightTime.CruiseOrFinalFlightAlt(40f, 100f, finalAlt, cruiseAlt), finalAlt);
                AssertNear("kinematic no-loft fallthrough",
                    FlightTime.ResolveUnstagedAltitude(false, false, targetAlt, 140f, 100f,
                                                       finalAlt, cruiseAlt), targetAlt);
                AssertNear("non-kinematic fallthrough unchanged",
                    FlightTime.ResolveUnstagedAltitude(true, false, targetAlt, 140f, 100f,
                                                       finalAlt, cruiseAlt), cruiseAlt);
                Pass("explicit final-stage preservation");

                // A low terminal endpoint is intentionally absent from this predicate: it cannot
                // take ownership until remaining range reaches the Kappa boundary.
                AssertTrue("Kappa owns altitude before boundary",
                    FlightTime.TerminalKappaOwnsAltitude(true, false, true, true,
                                                        700f, 1000f, 301f));
                AssertTrue("terminal owns altitude at boundary",
                    !FlightTime.TerminalKappaOwnsAltitude(true, false, true, true,
                                                         700f, 1000f, 300f));
                AssertTrue("cruise never enters kinematic Kappa guard",
                    !FlightTime.TerminalKappaOwnsAltitude(true, true, true, true,
                                                         700f, 1000f, 301f));
                Pass("Kappa boundary preservation");

                AssertTrue("inside boundary exposes Immediate waypoint",
                    FlightTime.ImmediateAltitudeWaypointOwns(false, true, 3000f, 2999f));
                AssertTrue("outside boundary retains Kappa waypoint",
                    !FlightTime.ImmediateAltitudeWaypointOwns(false, true, 3000f, 3001f));
                AssertTrue("cruise never enters Immediate kinematic gate",
                    !FlightTime.ImmediateAltitudeWaypointOwns(true, true, 3000f, 2999f));
                Pass("Immediate fast-forward boundary");

                // The SS-N-19 route GuardTrack recorded in flight on 2026-09-11 (362.6 s round):
                // loft to 2755.95, loft settings to 2204.76, final flight to 826.785 and a
                // terminal leg to 82.678, both at the group leader's 9.071u, then Linear to target.
                FlightTime.WaypointLeg[] route = FlightTime.BuildNonKinLegs(
                    3206.75f, 0.034f, 226.785f, 45f, false, 2755.95f, 0.372f, 45f,
                    2204.76f, 0.372f, 25f, 826.785f, 0.145f, false, 82.678f, false, 0f,
                    false, 9.071f);
                AssertTrue("route has five legs", route != null && route.Length == 5);
                float[] ends = { 2755.95f, 2204.76f, 826.785f, 82.678f, 0f };
                for (int k = 0; k < ends.Length; k++)
                    AssertNear($"leg {k} boundary", route[k].EndDist, ends[k]);
                AssertTrue("loft hands its settings on", route[0].SkipNext);
                AssertNear("final flight flies the leader height", route[2].Alt, 9.071f);
                AssertNear("terminal leg flies the leader height", route[3].Alt, 9.071f);
                AssertTrue("terminal leg may exceed its angles", !route[3].ClampAngles);
                AssertTrue("last leg is Linear", route[4].Mode == FlightTime.WaypointLegMode.Linear);
                // Launched inside the loft boundary, the loft leg is fast-forwarded away.
                FlightTime.WaypointLeg[] inside = FlightTime.BuildNonKinLegs(
                    2500f, 0.034f, 226.785f, 45f, false, 2755.95f, 0.372f, 45f,
                    2204.76f, 0.372f, 25f, 826.785f, 0.145f, false, 82.678f, false, 0f,
                    false, -1f);
                AssertNear("inside launch starts on the sea-skimming leg", inside[0].EndDist, 2204.76f);
                AssertNear("non-leader final flight height", inside[1].Alt, 0.372f);
                Pass("waypoint route construction");

                VerifyLegacyReconstruction(corpus);
                Pass("legacy capture compatibility");

                VerifyStepSchedule(corpus);
                Pass("recorded step schedule replay");

                VerifyLaunchAttitude(corpus);
                Pass("launch attitude rebuild");

                Console.WriteLine("selftest: 7 passed, 0 failed");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("selftest FAILED: " + e.Message);
                return 1;
            }
        }

        /// <summary>
        /// The recorded step timeline has to be a faithful generalisation of a constant step, not a
        /// second integration path that happens to give similar answers. Two invariants say so.
        ///
        /// A schedule of one entry must give bit-for-bit what that constant step gives. If it does
        /// not, the schedule branch has changed the loop rather than only its step, and every
        /// recovered round would be scored by different code from the rest of the corpus.
        ///
        /// A schedule whose entries are all the same step must equal that step too, whatever times
        /// the entries carry. That is what proves the cursor advances without disturbing anything.
        /// </summary>
        private static void VerifyStepSchedule(string corpus)
        {
            string file = Directory.GetFiles(corpus, "*.solveinput").OrderBy(f => f)
                .FirstOrDefault(f => ReadDump(f).ContainsKey("Ammo.TerminalAltRaw"));
            AssertTrue("a current-schema capture exists to replay", file != null);

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);

            const float dt = 0.0166667f;
            float constant = FlightTime.SolveOffline(in input, ap, dt, out _);
            AssertTrue("the reference replay produced an estimate", constant > 0f);

            FlightTime.OfflineSteps =
                new FlightTime.OfflineStepSchedule(new[] { 0f }, new[] { dt });
            float single = FlightTime.SolveOffline(in input, ap, dt, out _);
            FlightTime.OfflineSteps = null;
            AssertTrue("one-entry schedule equals the constant step", single.Equals(constant));

            FlightTime.OfflineSteps = new FlightTime.OfflineStepSchedule(
                new[] { 0f, 5f, 20f, 60f }, new[] { dt, dt, dt, dt });
            float repeated = FlightTime.SolveOffline(in input, ap, dt, out _);
            FlightTime.OfflineSteps = null;
            AssertTrue("repeated-entry schedule equals the constant step", repeated.Equals(constant));

            // A schedule that really does change the step must change the answer, or the branch is
            // being skipped and every recovered round is silently replayed at its seed step.
            FlightTime.OfflineSteps = new FlightTime.OfflineStepSchedule(
                new[] { 0f, 1f }, new[] { dt, 0.1f });
            float varied = FlightTime.SolveOffline(in input, ap, dt, out _);
            FlightTime.OfflineSteps = null;
            AssertTrue("a schedule that changes the step changes the answer",
                       !varied.Equals(constant));

            // Time weighting, which is what the reported step column prints.
            var half = new FlightTime.OfflineStepSchedule(new[] { 0f, 50f }, new[] { 0.02f, 0.04f });
            AssertNear("mean step is time weighted", half.MeanStep(100f), 0.03f);
        }

        private static void VerifyLegacyReconstruction(string corpus)
        {
            string[] files = Directory.GetFiles(corpus, "*.solveinput");
            Dictionary<string, string> corrected = null, passthrough = null;
            foreach (string file in files)
            {
                Dictionary<string, string> kv = ReadDump(file);
                if (kv.ContainsKey("Ammo.TerminalAltRaw")) continue;
                if (corrected == null && !B(kv, "NonKin") && !B(kv, "IsTerminalLoft") &&
                    F(kv, "FinalFlightDist") <= 0f &&
                    Math.Abs(F(kv, "TermAlt") -
                        (kv.ContainsKey("ConfiguredLoftAlt")
                            ? F(kv, "ConfiguredLoftAlt") : F(kv, "LoftAlt"))) <
                        SerializedValueTolerance)
                    corrected = kv;
                if (passthrough == null && B(kv, "NonKin")) passthrough = kv;
                if (corrected != null && passthrough != null) break;
            }

            AssertTrue("legacy corrected capture exists", corrected != null);
            AssertTrue("legacy passthrough capture exists", passthrough != null);

            AmmunitionParameters correctedAmmo = BuildAmmo(corrected);
            FlightTime.SolveInput correctedInput = BuildInput(corrected, correctedAmmo);
            AssertTrue("legacy unset terminal becomes linear", correctedInput.TerminalLinear);
            AssertNear("legacy unset terminal maps to target",
                       correctedInput.TermAlt, F(corrected, "TargetAlt0"));

            AmmunitionParameters passthroughAmmo = BuildAmmo(passthrough);
            FlightTime.SolveInput passthroughInput = BuildInput(passthrough, passthroughAmmo);
            AssertTrue("legacy cruise is not terminal-linear", !passthroughInput.TerminalLinear);
            AssertNear("legacy cruise terminal altitude passes through",
                       passthroughInput.TermAlt, F(passthrough, "TermAlt"));
        }

        /// <summary>
        /// The scorer's flown-rail substitution rebuilds the opening attitude as
        /// QEuler(-PrevPitch, YawOf(LaunchHeading), 0), which is the mod's own construction. Every
        /// capture with a rail heading carries both fields from the mod, so the rebuild has to
        /// reproduce the recorded Att on all of them, or a substituted round replays from an attitude
        /// no capture could contain. A quaternion and its negation are one rotation, hence |dot|.
        /// </summary>
        private static void VerifyLaunchAttitude(string corpus)
        {
            int checkedCount = 0;
            foreach (string file in Directory.GetFiles(corpus, "*.solveinput").OrderBy(f => f))
            {
                Dictionary<string, string> kv = ReadDump(file);
                if (!kv.ContainsKey("Att") || !kv.ContainsKey("LaunchHeading") || !kv.ContainsKey("PrevPitch"))
                    continue;
                Vector3 h = V3(kv, "LaunchHeading");
                if (h.x * h.x + h.z * h.z < 1e-8f) continue;
                Quaternion recorded = Q(kv, "Att");
                Quaternion rebuilt = GameMath.QEuler(-F(kv, "PrevPitch"), FlightTime.YawOf(h), 0f);
                float dot = Math.Abs(recorded.x * rebuilt.x + recorded.y * rebuilt.y +
                                     recorded.z * rebuilt.z + recorded.w * rebuilt.w);
                AssertTrue($"launch attitude rebuild matches {Path.GetFileName(file)} (|dot| {dot:R})",
                           dot > 1f - AttitudeDotTolerance);
                checkedCount++;
            }
            AssertTrue("captures with a rail heading exist to check the attitude rebuild", checkedCount > 0);
            Console.WriteLine($"      attitude rebuilt and matched on {checkedCount} capture(s)");
        }

        /// <summary>Serialised quaternion components, written at full round-trip precision.</summary>
        private const float AttitudeDotTolerance = 1e-5f;

        private static void AssertNear(string name, float actual, float expected)
        {
            if (Math.Abs(actual - expected) > SerializedValueTolerance)
                throw new InvalidOperationException(
                    $"{name}: expected {expected:R}, got {actual:R}");
        }

        private static void AssertTrue(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
        }

        private static void Pass(string name) => Console.WriteLine("PASS  " + name);
    }
}
