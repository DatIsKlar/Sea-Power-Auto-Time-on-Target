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
        /// --launch-estimate falls back to EstAtLaunch for older captures. On the builds that made
        /// them that value came through the estimate cache and may be from an earlier solve at a
        /// different position, so a small difference there is not evidence of anything.
        /// </summary>
        /// <summary>
        /// The recorded physics-step timeline, "t:step" per change, into a schedule the replay can
        /// fly. Empty or malformed gives null, which leaves the round excluded exactly as it was
        /// before the timeline existed rather than replaying it at a guessed step.
        ///
        /// The first entry is the step at the round's first sighting, a few tenths of a second
        /// after launch, and it is taken to hold from t = 0. Nothing better is available for the
        /// frames before the tracker opened, and at 0.2 s of a 100 s flight the choice cannot
        /// matter.
        /// </summary>
        private static FlightTime.OfflineStepSchedule ParseStepSchedule(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;
            string[] parts = field.Split(';');
            var at = new List<float>(parts.Length);
            var dt = new List<float>(parts.Length);
            foreach (string entry in parts)
            {
                if (entry.Length == 0) continue;
                int colon = entry.IndexOf(':');
                if (colon <= 0) return null;
                if (!float.TryParse(entry.Substring(0, colon), NumberStyles.Float, Inv, out float t) ||
                    !float.TryParse(entry.Substring(colon + 1), NumberStyles.Float, Inv, out float step))
                    return null;
                if (step <= 0f) return null;
                // Ascending by construction; a file that is not is not trusted to be parsed.
                if (at.Count > 0 && t < at[at.Count - 1]) return null;
                at.Add(at.Count == 0 ? 0f : t);
                dt.Add(step);
            }
            if (at.Count == 0) return null;
            return new FlightTime.OfflineStepSchedule(at.ToArray(), dt.ToArray());
        }

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
        /// <summary>
        /// Which LAUNCH a round came from. <c>CapturedAt</c> is written once, when the run
        /// finishes, so every round of one flight carries the same value and two flights of one
        /// mission do not.
        ///
        /// The key's own timestamp will NOT do: it is stamped when that round's solve was dumped,
        /// so it differs round to round within a salvo and would defeat the formation test it
        /// exists to scope. Captures older than the field return the empty string and therefore
        /// share one bucket, which is exactly the behaviour they were scored with before.
        /// </summary>
        private static string RunOf(Dictionary<string, string> r)
            => r.GetValueOrDefault("CapturedAt", "");

        private static int Score(string[] files, float[] steps)
        {
            var rows = new List<(string Label, float Actual, float[] Est, float Flown, float FlownEst,
                                 bool Grouped, float Launch, float Impact, string Run, string Key)>();
            // Per-round keyed output, for asking why a family's rounds disagree with each other
            // rather than how far the family is from the game. The table cannot answer that: it
            // prints a row per round but no key, so a round cannot be joined back to its capture
            // and correlated against what it flew. Written only when --csv is given.
            var keyed = new List<string>();
            var skipped = new List<string>();
            // Launch state the game decided after the solve, replayed as flown; see FlownRail.
            bool useFlownRail = System.Environment.GetEnvironmentVariable("AUTOTOT_NO_FLOWN_HEADING") != "1";
            var railSubs = new List<string>();
            // Surface-ship target velocity as the game moves the ship; see RoundedTargetVel.
            bool useTargetRounding = System.Environment.GetEnvironmentVariable("AUTOTOT_NO_TARGET_ROUNDING") != "1";
            var roundedKeys = new HashSet<string>();

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
                // reproducible: the lab replays it at that step. And since the mod is used at high
                // compression, being right there is part of the job rather than a case to discard.
                //
                // A step that MOVED mid-flight used to be excluded for the same reason inverted:
                // no single step describes it. That is answered rather than tolerated now. The
                // outcome writer records the whole timeline as StepChanges, and the replay flies
                // it, so such a round is reproduced exactly instead of approximated. A round
                // captured before that recording existed still has no timeline and is still
                // excluded, because for it the objection stands.
                float stepLo = F(r, "MinFlownStep"), stepHi = F(r, "MaxFlownStep");
                bool stepSteady = stepLo > 0f && stepHi > 0f &&
                                  Math.Abs(stepHi - stepLo) <=
                                  SteadyStepRelativeTolerance * stepHi;
                FlightTime.OfflineStepSchedule schedule =
                    stepSteady ? null : ParseStepSchedule(r.GetValueOrDefault("StepChanges", ""));
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
                  : (stepHi > 0f && !stepSteady && schedule == null)
                        ? $"physics step moved in flight ({stepLo:F4}s to {stepHi:F4}s) and the " +
                          "capture predates the recorded timeline, so no single step describes it"
                  : null;
                if (reason != null) { skipped.Add($"{label}: {reason}"); continue; }

                Dictionary<string, string> kv = ReadDump(file);
                AmmunitionParameters ap = BuildAmmo(kv);
                FlightTime.SolveInput input = BuildInput(kv, ap);

                // Which of two fixed launchers a round leaves from is decided by the game after the
                // solve, the same way its motor roll is, so a round that left a different launcher
                // from the one its capture modelled replays on the rail it flew.
                bool railSub = FlownRail(r, in input, out Vector3 flownHeading, out float modelledDeg,
                                         out float flownDeg)
                               && useFlownRail;
                if (railSub)
                {
                    input = input with
                    {
                        LaunchHeading = flownHeading,
                        // The mod builds the opening attitude from the heading, so a capture made on
                        // the flown rail carries both. Rebuilt with the mod's own expression.
                        Att = GameMath.QEuler(-input.PrevPitch, FlightTime.YawOf(flownHeading), 0f),
                    };
                    railSubs.Add($"{r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(file))}: " +
                                 $"modelled {(modelledDeg + 360f) % 360f:F1} deg, flew {(flownDeg + 360f) % 360f:F1} deg");
                }

                // A capture solved before the mod rounded the target's velocity replays on the
                // rounded one, so old and new captures are judged on one model. See RoundedTargetVel.
                if (useTargetRounding && RoundedTargetVel(kv, r, in input, out Vector3 roundedVel))
                {
                    input = input with { TargetVel = roundedVel };
                    roundedKeys.Add(r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(file)));
                }

                // Replay the motor roll this particular round flew, rather than the 1.0 the
                // estimator has to assume in advance.
                FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                    ? F(r, "MotorPerformance") : 1f;

                // The sweep columns are constant-step by definition, including the 0.1 s
                // production column, so the schedule is off for them.
                float[] est = steps.Select(dt => FlightTime.SolveOffline(in input, ap, dt, out _)).ToArray();

                // The step the round itself flew. A steady round replays at its one step; a round
                // with a recorded timeline flies that timeline; a capture that predates the
                // recording leaves the column blank rather than guessing.
                float flown = stepSteady ? stepHi
                             : (schedule != null ? schedule.MeanStep(F(r, "ActualFlight")) : 0f);
                float flownEst;
                if (schedule != null)
                {
                    FlightTime.OfflineSteps = schedule;
                    // The seed step only matters until the first schedule entry takes effect on the
                    // first iteration, but pass the round's own opening step so a trace reads right.
                    flownEst = FlightTime.SolveOffline(in input, ap, schedule.Dt[0], out _);
                    FlightTime.OfflineSteps = null;
                }
                else
                {
                    flownEst = flown > 0f
                        ? FlightTime.SolveOffline(in input, ap, flown, out _) : -1f;
                }
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
                          launch, launch + F(r, "ActualFlight"), RunOf(r),
                          r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(file))));
                // The schema is part of the answer, not decoration. A capture without the raw
                // Ammo.* fields replays the older model law by design, so its gap measures a path
                // the mod no longer flies, and a family judged on a mixture of the two is not being
                // judged on the shipping model. usn_rim-66p reads 6.83 s over its pre-schema rounds
                // and 0.94 s over all seven of its current ones.
                keyed.Add(string.Join(",",
                    r.GetValueOrDefault("Key", Path.GetFileNameWithoutExtension(file)), label,
                    F(r, "ActualFlight").ToString("R", Inv), est[0].ToString("R", Inv),
                    (F(r, "ActualFlight") - est[0]).ToString("R", Inv),
                    F(r, "MotorPerformance").ToString("R", Inv),
                    (B(r, "InGroup") ? "1" : "0"),
                    kv.GetValueOrDefault("CaptureSchema", "pre-schema"),
                    (railSub ? "1" : "0")));
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
            // Grouped by RUN as well as by label and impact time. Without the run, two rounds from
            // two launches of the same mission collide: a repeat section is deliberately identical,
            // so both rounds impact at the same mission time and the salvo test read them as one
            // formation and deleted the earlier launch. That silently cost wp_ss-n-19 and
            // wp_ss-n-12 every round of a re-fly flown to clear them.
            foreach (var salvo in rows.Where(r => r.Grouped)
                                      .GroupBy(r => (r.Label, r.Run, Key: (int)Math.Round(r.Impact / 2.0)))
                                      .ToList())
            {
                // A group of ONE is not a formation. The game creates a missile group for a single
                // round of group-capable ammunition, and InGroup then reads true while the round
                // flies its own speed the whole way with nothing to wait for: a lone wp_ss-n-19
                // records `GroupChanges ...:1:1:1100` against its own 1100 kn maximum. Scoring it
                // as grouped hid two families from the solo table that had just been re-flown to
                // clear them.
                if (salvo.Count() == 1)
                {
                    var lone = salvo.First();
                    rows[rows.IndexOf(lone)] = (lone.Label, lone.Actual, lone.Est, lone.Flown,
                                                lone.FlownEst, false, lone.Launch, lone.Impact,
                                                lone.Run, lone.Key);
                    continue;
                }
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

            // Per-round rows LAST, so `scored` and `formation` carry the same verdict the table
            // above used. Written before the salvo pass they said only what the capture claimed,
            // and a reader joining on the `grouped` column got a different split from the one the
            // means were taken over.
            string csv = System.Environment.GetEnvironmentVariable("AUTOTOT_SCORE_CSV");
            if (!string.IsNullOrEmpty(csv))
            {
                var kept = rows.ToDictionary(r => r.Key, r => r.Grouped);
                var lines = new List<string> {
                    "key,label,actualFlight,offlineEst,offGap,motor,grouped,schema,flownRail,scored,formation,targetRounded" };
                foreach (string line in keyed)
                {
                    string k = line.Substring(0, line.IndexOf(','));
                    bool inTable = kept.TryGetValue(k, out bool grp);
                    lines.Add(line + "," + (inTable ? "1" : "0") + "," + (inTable && grp ? "1" : "0") +
                              "," + (roundedKeys.Contains(k) ? "1" : "0"));
                }
                File.WriteAllLines(csv, lines);
                Console.WriteLine($"per-round rows written to {csv}");
            }

            Console.WriteLine($"\n{rows.Count} round(s) scored, {skipped.Count} excluded.");
            foreach (string sk in skipped) Console.WriteLine("  excluded  " + sk);
            Console.WriteLine(useFlownRail
                ? $"{railSubs.Count} round(s) replayed on the rail they flew, not the launcher their capture " +
                  "modelled (AUTOTOT_NO_FLOWN_HEADING=1 turns this off)."
                : "Flown-rail substitution is OFF (AUTOTOT_NO_FLOWN_HEADING=1).");
            foreach (string s in railSubs) Console.WriteLine("  flown rail  " + s);
            Console.WriteLine(useTargetRounding
                ? $"{roundedKeys.Count} older capture(s) replayed on the target velocity the game realises for a " +
                  "surface ship, rounded as the mod now rounds it (AUTOTOT_NO_TARGET_ROUNDING=1 turns this off)."
                : "Target velocity rounding is OFF (AUTOTOT_NO_TARGET_ROUNDING=1).");
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

        /// <summary>A ship's transform sits within a fraction of a unit of the waterline, so a target
        /// further from it than this is not a surface ship.</summary>
        private const float SurfaceTargetMaxAbsYU = 1f;

        /// <summary>A captured target velocity further than this from the recorded course had the
        /// evasive boost applied, and rounding it would round the boosted vector.</summary>
        private const float BoostFreeCourseDeg = 1f;

        /// <summary>Time.fixedDeltaTime as GameTime.SetAdaptiveFixedDeltaTime sets it.</summary>
        private const float Step60Hz = 0.0166667f, Step30Hz = 0.0333333f;

        /// <summary>
        /// The target velocity the mod now solves on, for a capture solved before it rounded one (no
        /// TargetVelReported): a surface ship's velocity with the game's float32 position rounding
        /// applied (GameMath.TranslateRoundedVelocity, as FlightTime.TargetMapVelocity uses it). Done
        /// only where it can be done the mod's way: the target sits at the waterline with no vertical
        /// velocity, which a Vessel always has; the captured velocity points along the recorded
        /// course within <see cref="BoostFreeCourseDeg"/>, so no evasive boost is baked in; and the
        /// step is the one the target moved at for most of the flight (<see cref="PrevailingFlightStep"/>).
        /// False otherwise, and the capture replays as recorded.
        /// </summary>
        private static bool RoundedTargetVel(Dictionary<string, string> kv, Dictionary<string, string> r,
                                             in FlightTime.SolveInput input, out Vector3 rounded)
        {
            rounded = input.TargetVel;
            if (kv.ContainsKey("TargetVelReported") || !r.ContainsKey("TargetCourseAtLaunch")) return false;
            Vector3 v = input.TargetVel;
            if (v.y != 0f || Math.Abs(input.TargetPos.y) >= SurfaceTargetMaxAbsYU) return false;
            if (v.x * v.x + v.z * v.z < 1e-12f) return false;
            if (Math.Abs(DeltaAngle(FlightTime.YawOf(v), F(r, "TargetCourseAtLaunch"))) > BoostFreeCourseDeg)
                return false;
            rounded = GameMath.TranslateRoundedVelocity(input.TargetPos, v, PrevailingFlightStep(r));
            return true;
        }

        /// <summary>
        /// The physics step a round's target moved at for most of its flight: its one step when
        /// steady, else the step its recorded timeline spent longest at (written to four places, so
        /// snapped back to the game's own value), else 1/60 s. The mod predicts this from the minute
        /// before the solve (FlightTime.PredictedPhysicsStep); an old capture has no record of that
        /// minute, and in a session whose step is stable the two are the same. The launch step alone
        /// is wrong here: a round solved in the 30 Hz stretch after a mission load flies the rest of
        /// its flight at 60 Hz, and the same ship rounds to 1.014 of its speed at one and 0.870 at the
        /// other.
        /// </summary>
        private static float PrevailingFlightStep(Dictionary<string, string> r)
        {
            float lo = F(r, "MinFlownStep"), hi = F(r, "MaxFlownStep");
            if (lo > 0f && hi > 0f && Math.Abs(hi - lo) <= SteadyStepRelativeTolerance * hi) return lo;
            float flight = F(r, "ActualFlight");
            string[] entries = r.GetValueOrDefault("StepChanges", "")
                                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var spans = new Dictionary<float, float>();
            for (int i = 0; i < entries.Length; i++)
            {
                string[] p = entries[i].Split(':');
                if (p.Length != 2 || !float.TryParse(p[0], NumberStyles.Float, Inv, out float at) ||
                    !float.TryParse(p[1], NumberStyles.Float, Inv, out float step) || step <= 0f)
                    continue;
                float until = flight;
                if (i + 1 < entries.Length)
                {
                    string[] q = entries[i + 1].Split(':');
                    if (q.Length == 2 && float.TryParse(q[0], NumberStyles.Float, Inv, out float next))
                        until = Math.Min(next, flight);
                }
                if (i == 0) at = 0f;   // the first entry is the step the round was first seen at
                float snapped = Math.Abs(step - Step60Hz) < 5e-5f ? Step60Hz
                              : Math.Abs(step - Step30Hz) < 5e-5f ? Step30Hz : step;
                spans[snapped] = spans.GetValueOrDefault(snapped) + Math.Max(0f, until - at);
            }
            if (spans.Count > 0) return spans.OrderByDescending(e => e.Value).First().Key;
            return lo > 0f ? lo : Step60Hz;
        }

        /// <summary>
        /// A disagreement between the modelled and flown launch headings larger than this means the
        /// round left a different fixed launcher. Measured over the corpus on 2026-09-13: of 217
        /// fixed-rail, non-vertical captures, 192 agree to under 0.05 deg, seven wp_ss-n-3 rounds
        /// differ by a steady 2.5 deg, and 18 usn_rgm-84d rounds by 180 deg, with nothing between.
        /// The 2.5 deg is a separate question and deliberately stays below this line.
        /// </summary>
        private const float DifferentLauncherDeg = 90f;

        /// <summary>Above this pitch at t = 0 a round is climbing out of a vertical cell, and its
        /// heading says nothing about a rail.</summary>
        private const float VerticalLaunchPitchDeg = 75f;

        /// <summary>
        /// The heading a round actually left on, from the first sample of its own FlightTrack
        /// (pitch and heading are its sixth and seventh fields), when that disagrees with the
        /// capture's LaunchHeading by more than <see cref="DifferentLauncherDeg"/>. A ship with two
        /// opposed fixed launchers for one ammunition can hand a round the other canister after the
        /// solve, and captures made before e90c13f recorded the canister the solve named. False for
        /// a trainable mount (zero heading), a vertical launch, a capture without a track, or a
        /// round that flew the rail it was modelled on. See docs/plans/done/2026-09-13-launcher-pick-by-arc.md.
        /// </summary>
        private static bool FlownRail(Dictionary<string, string> r, in FlightTime.SolveInput input,
                                      out Vector3 flown, out float modelledDeg, out float flownDeg)
        {
            flown = Vector3.zero; modelledDeg = 0f; flownDeg = 0f;
            Vector3 modelled = input.LaunchHeading;
            if (modelled.x * modelled.x + modelled.z * modelled.z < 1e-8f) return false;
            string track = r.GetValueOrDefault("FlightTrack", "");
            int end = track.IndexOf(';');
            string[] p = (end >= 0 ? track.Substring(0, end) : track).Split(':');
            if (p.Length < 7
                || !float.TryParse(p[5], NumberStyles.Float, Inv, out float pitch0)
                || !float.TryParse(p[6], NumberStyles.Float, Inv, out flownDeg)
                || pitch0 > VerticalLaunchPitchDeg)
                return false;
            modelledDeg = FlightTime.YawOf(modelled);
            if (Math.Abs(DeltaAngle(modelledDeg, flownDeg)) <= DifferentLauncherDeg) return false;
            float rad = flownDeg * Mathf.Deg2Rad;
            flown = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            return true;
        }

        private static void MeanRow(string label,
            List<(string Label, float Actual, float[] Est, float Flown, float FlownEst, bool Grouped,
                  float Launch, float Impact, string Run, string Key)> rows,
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
