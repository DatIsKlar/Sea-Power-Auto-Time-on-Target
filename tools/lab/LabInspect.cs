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
    /// Single-capture inspection: the trace and climb commands, and the bare step-size replay.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>
        /// Puts the round's recorded altitude against the model's, sample for sample.
        ///
        /// Peak altitude alone cannot separate the two ways a round ends up below its commanded
        /// altitude. If the recorded climb is still rising when it turns over, the round ran out of
        /// distance and the climb is rate-limited by something we do not model. If it flattens
        /// early and holds, it was commanded to a lower altitude than we think.
        /// </summary>
        /// <summary>
        /// How much the model's flight time moves with the per-round thrust roll, for one captured
        /// round. The game rolls <c>1.02 - rand/25</c> and multiplies thrust by it, and the scorer
        /// replays each round with the value it flew on the grounds that this removes the roll. It
        /// only removes it if the model's response matches the game's, and on `plan_yj-20` at 65 nm
        /// it does not: see docs/plans/open/yj-20-motor-sensitivity.md. Live rounds give the game's
        /// side of that comparison but carry sampling noise; this gives the model's side exactly.
        ///
        /// Reported as seconds per unit of motor and as a multiple of the round's own flight time,
        /// which is the form the two sides are compared in. The game's figure sits at 1.14 to 1.18
        /// on every family measured so far.
        /// </summary>
        private static int Motor(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab motor <solveinput-file> [scale ...]");
                return 2;
            }

            string file = args[0];
            float[] scales = args.Skip(1).Select(a => float.Parse(a, Inv)).ToArray();
            if (scales.Length == 0) scales = new[] { 0.98f, 0.99f, 1.00f, 1.01f, 1.02f };

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);

            Console.WriteLine($"{"motor",8}{"estimate",12}");
            Console.WriteLine(new string('-', 20));
            var pts = new List<(float m, float est)>();
            foreach (float m in scales)
            {
                FlightTime.OfflineMotorScale = m;
                float est = FlightTime.SolveOffline(in input, ap, 0.1f, out _);
                FlightTime.OfflineMotorScale = 1f;
                pts.Add((m, est));
                Console.WriteLine($"{m,8:F4}{est,12:F3}");
            }

            // Least squares over whatever scales were asked for, so an uneven sweep still reports
            // one slope rather than a pair of endpoints that happen to bracket a kink.
            float mx = pts.Average(p => p.m), my = pts.Average(p => p.est);
            float den = pts.Sum(p => (p.m - mx) * (p.m - mx));
            float slope = den > 0f ? pts.Sum(p => (p.m - mx) * (p.est - my)) / den : 0f;
            Console.WriteLine(new string('-', 20));
            Console.WriteLine($"d(estimate)/d(motor) = {slope:F1} s per unit, " +
                              $"{Math.Abs(slope) / Math.Max(my, 0.001f):F2} x the flight");
            return 0;
        }

        private static int Climb(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: autotot-lab climb <solveinput-file>");
                return 2;
            }

            string file = args[0];
            string resultFile = Path.ChangeExtension(file, ".result");
            if (!File.Exists(resultFile))
            {
                Console.Error.WriteLine("no recorded outcome beside that input");
                return 1;
            }

            Dictionary<string, string> r = ReadDump(resultFile);
            string track = r.GetValueOrDefault("Track", "");
            if (track.Length == 0)
            {
                Console.Error.WriteLine(
                    "this round predates the recorded track; re-fly it to compare climbs");
                return 1;
            }

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: true);
            FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                ? F(r, "MotorPerformance") : 1f;

            var sw = new StringWriter();
            TextWriter prev = Console.Out;
            Console.SetOut(sw);
            Bootstrap.Log.Echo = true;
            try { FlightTime.SolveOffline(in input, ap, 0.1f, out _); }
            finally
            {
                // A throwing solve must not leave the rest of the run printing into the buffer.
                Bootstrap.Log.Echo = false;
                Console.SetOut(prev);
            }
            FlightTime.OfflineMotorScale = 1f;

            // Model samples from the trace it just produced. Speed as well as altitude: the trace
            // has always carried it and the column dropped it, which left the one question the
            // command cannot otherwise answer, whether a timing difference is a path difference or
            // a speed difference, needing a second tool.
            var model = new List<(float T, float Alt, float Spd)>();
            foreach (Match m in Regex.Matches(sw.ToString(), @"t\+([0-9.]+)s spd (\d+)kn alt ([0-9.]+)"))
                model.Add((float.Parse(m.Groups[1].Value, Inv),
                           float.Parse(m.Groups[3].Value, Inv),
                           float.Parse(m.Groups[2].Value, Inv)));

            Console.WriteLine();
            Console.WriteLine($"{"t",8}{"realAlt",10}{"modelAlt",10}{"diff",9}" +
                              $"{"realSpd",9}{"modelSpd",10}{"diff%",8}");
            Console.WriteLine(new string('-', 64));

            float ceiling = F(kv, "LoftAlt");
            float peakReal = 0f, peakModel = 0f;
            foreach (string sample in track.Split(';'))
            {
                string[] p = sample.Split(':');
                if (p.Length < 3) continue;
                float t = float.Parse(p[0], Inv), alt = float.Parse(p[1], Inv), spd = float.Parse(p[2], Inv);
                (float ma, float ms) = NearestSample(model, t);
                peakReal = Math.Max(peakReal, alt);
                peakModel = Math.Max(peakModel, ma);
                string spdDiff = spd > 1f ? $"{100f * (ms - spd) / spd:+0.0;-0.0}" : "";
                Console.WriteLine($"{t,8:F0}{alt,10:F0}{ma,10:F0}{ma - alt,9:+0;-0}" +
                                  $"{spd,9:F0}{ms,10:F0}{spdDiff,8}");
            }

            Console.WriteLine(new string('-', 64));
            Console.WriteLine($"commanded ceiling {ceiling:F0}u, real peak {peakReal:F0}u, model peak {peakModel:F0}u");
            Console.WriteLine(r.GetValueOrDefault("StageChanges", ""));
            return 0;
        }

        /// <summary>Model altitude and speed at the sample nearest this time. The recorded track
        /// and the model trace run on different cadences, so neither can be indexed by the other.
        /// </summary>
        private static (float Alt, float Spd) NearestSample(List<(float T, float Alt, float Spd)> model,
                                                            float t)
        {
            float bestAlt = 0f, bestSpd = 0f, bestD = float.MaxValue;
            foreach (var m in model)
            {
                float d = Math.Abs(m.T - t);
                if (d < bestD) { bestD = d; bestAlt = m.Alt; bestSpd = m.Spd; }
            }
            return (bestAlt, bestSpd);
        }

        /// <summary>
        /// Replays one round with the step trace on, printing the same sim-track lines the mod
        /// emits in game. This is how a disagreement gets localised to a phase.
        /// </summary>
        private static int Trace(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(
                    "usage: autotot-lab trace <solveinput-file> [step] [--motor <value>]");
                return 2;
            }

            // --motor replays the same input at a thrust multiplier of your choosing instead of the
            // one the round flew. Two traces of one input at two multipliers isolate the model's
            // response to thrust from every other difference between two rounds, which is what the
            // live salvo cannot do: its rounds differ in geometry as well as motor.
            float motorOverride = -1f;
            var rest = new List<string>();
            for (int k = 0; k < args.Length; k++)
            {
                if (args[k] == "--motor" && k + 1 < args.Length)
                { motorOverride = float.Parse(args[++k], Inv); continue; }
                rest.Add(args[k]);
            }

            string file = rest[0];
            float dt = rest.Count > 1 ? float.Parse(rest[1], Inv) : 0.1f;

            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: true);
            Console.WriteLine("route " + (input.Legs == null ? "none (altitude-command law)"
                : string.Join(" ; ", input.Legs.Select(l => l.ToString()))));

            string resultFile = Path.ChangeExtension(file, ".result");
            if (File.Exists(resultFile))
            {
                Dictionary<string, string> r = ReadDump(resultFile);
                FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f
                    ? F(r, "MotorPerformance") : 1f;
                Console.WriteLine($"actual {F(r, "ActualFlight"):F1}s, motor {FlightTime.OfflineMotorScale:F3}");
            }
            if (motorOverride > 0f)
            {
                FlightTime.OfflineMotorScale = motorOverride;
                Console.WriteLine($"motor OVERRIDDEN to {motorOverride:F4}; the recorded outcome " +
                                  "above belongs to a different multiplier");
            }

            Bootstrap.Log.Echo = true;
            float est = FlightTime.SolveOffline(in input, ap, dt, out FlightTime.IntegratedPhases phases);
            Bootstrap.Log.Echo = false;
            FlightTime.OfflineMotorScale = 1f;

            Console.WriteLine($"est {est:F2}s at step {dt}, model peak {phases.PeakAltU:F1}u, " +
                              $"terminal {phases.VTerm:F0}kn");
            Console.WriteLine($"phases climb {phases.ClimbTime:F2}s, cruise {phases.CruiseTime:F2}s, " +
                              $"terminal {phases.DescentTime:F2}s; dive starts " +
                              $"{phases.DiveStartU:F1}u from target, final flight starts " +
                              $"{phases.FinalFlightStartU:F1}u from target");
            return 0;
        }

        /// <summary>
        /// leadcheck: every lead frame of the guard tracks under the given directories, replayed
        /// through FlightTime.LeadInterceptTime with the frame's own recorded known target, known
        /// velocity, position, speed and travelled distance, against the intercept point the game
        /// recorded on that frame. This checks the port, not the step loop: the step loop supplies
        /// the same inputs from its own modelled state. Frames within 5 u of the known target are
        /// skipped, as in the Python reference (max-solo-error-below-1s.leadport.py).
        /// </summary>
        private static int LeadCheck(string[] args)
        {
            var misses = new List<float>();
            var switchMisses = new List<(float Miss, string Transition, string Round, string T)>();
            foreach (string dir in args)
            {
                string solveDir = Directory.Exists(Path.Combine(dir, "solve")) ? Path.Combine(dir, "solve") : dir;
                foreach (string res in Directory.GetFiles(solveDir, "*.result").OrderBy(f => f, StringComparer.Ordinal))
                {
                    Dictionary<string, string> r = ReadDump(res);
                    string[] names = r.GetValueOrDefault("GuardTrackFields", "").Split(':');
                    if (Array.IndexOf(names, "knownTgt") < 0) continue;
                    Dictionary<string, string> kv = ReadDump(Path.ChangeExtension(res, ".solveinput"));
                    var frames = r.GetValueOrDefault("GuardTrack", "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => names.Zip(line.Split(':'), (n, v) => (n, v))
                            .GroupBy(p => p.n).ToDictionary(g => g.Key, g => g.First().v))
                        .ToList();
                    for (int k = 0; k < frames.Count; k++)
                    {
                        var f = frames[k];
                        if (f.GetValueOrDefault("leadOn") != "1") continue;
                        if (!TryVec(f, "pos", out Vector3 pos) || !TryVec(f, "knownTgt", out Vector3 known) ||
                            !TryVec(f, "knownVel", out Vector3 knownVel) || !TryVec(f, "intercept", out Vector3 recorded))
                            continue;
                        if ((pos - known).magnitude < 5f) continue;
                        float speedU = float.Parse(f["speedU"], Inv);
                        float vy = f.TryGetValue("vy", out string vys) ? float.Parse(vys, Inv) : 0f;
                        float pitch = speedU > 0f ? Mathf.Asin(Mathf.Clamp(vy / speedU, -1f, 1f)) * Mathf.Rad2Deg : 0f;
                        float leadTime = FlightTime.LeadInterceptTimeOffline(pos, speedU / GameUnits.KnotsToUnityPerSecond,
                            float.Parse(f["travel"], Inv), known, knownVel, F(kv, "Ammo.MaxVelocityInKnots"),
                            F(kv, "Ammo.MaxLoftAltUnity"), F(kv, "Ammo.FinalFlightPhaseMaxAngle"), pitch,
                            F(kv, "DragFactor"), F(kv, "Ammo.LiftFactor"), F(kv, "Ammo.MinVelocity"));
                        if (leadTime <= 0f) continue;
                        Vector3 port = known + knownVel * leadTime;
                        float miss = new Vector2(port.x - recorded.x, port.z - recorded.z).magnitude;
                        misses.Add(miss);
                        if (k + 1 < frames.Count && frames[k + 1].GetValueOrDefault("stage") != f.GetValueOrDefault("stage"))
                            switchMisses.Add((miss, f.GetValueOrDefault("stage") + ">" + frames[k + 1].GetValueOrDefault("stage"),
                                              Path.GetFileNameWithoutExtension(res), f.GetValueOrDefault("t")));
                    }
                }
            }
            if (misses.Count == 0) { Console.WriteLine("leadcheck: no lead frames found"); return 1; }
            misses.Sort();
            Console.WriteLine($"leadcheck: {misses.Count} lead frames, median miss {misses[misses.Count / 2]:F4} u, " +
                              $"p90 {misses[9 * misses.Count / 10]:F4} u, max {misses[^1]:F4} u");
            Console.WriteLine($"leadcheck: {switchMisses.Count} last frames before a stage change, max miss " +
                              $"{(switchMisses.Count > 0 ? switchMisses.Max(m => m.Miss) : float.NaN):F4} u");
            foreach (var g in switchMisses.GroupBy(m => m.Transition).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {g.Key,-50} {g.Count(),4} frames, max miss {g.Max(m => m.Miss):F4} u");
            foreach (var m in switchMisses.Where(m => m.Miss > 0.05f).OrderByDescending(m => m.Miss))
                Console.WriteLine($"  over 0.05 u: {m.Miss,8:F4} u  {m.Transition}  t {m.T}  {m.Round}");
            return 0;
        }

        /// <summary>
        /// pathcmp: the model's flight beside the recorded one, matched on flat distance to the target's
        /// centre, so a timing error can be put at the phase that makes it. dt is the model's time at a
        /// recorded point minus the real time there; a dt that steps between two rows was made between
        /// them. Replays the way score does: the rail flown, the target velocity rounded, the round's own
        /// motor roll and physics steps. --no-hull ends the replay 3 u from the centre instead of at the hull.
        /// </summary>
        /// <summary>
        /// The motor clock for the AUTOTOT_THRUST_FRAMES and AUTOTOT_THRUST_LAG counterfactuals. The
        /// game reads the motor clock from missionElapsedTime, which advances once per rendered frame.
        /// FlightTrack rows are stamped with that clock at a 0.25 s cadence for the first 5 s, so each
        /// row is a frame start; each gap is split into the given number of equal frames.
        /// </summary>
        internal static void SetThrustClock(Dictionary<string, string> r)
        {
            FlightTime.OfflineThrustFrameClock = null;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("AUTOTOT_THRUST_FRAMES"), out int n) && n >= 1)
            {
                var ages = new List<float>();
                foreach (string e in r.GetValueOrDefault("FlightTrack", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    float a = float.Parse(e.Substring(0, e.IndexOf(':')), Inv);
                    if (a < 5f) ages.Add(a);
                }
                if (ages.Count >= 2)
                {
                    var clock = new List<float>();
                    for (int i = 0; i + 1 < ages.Count; i++)
                        for (int k = 0; k < n; k++)
                            clock.Add(ages[i] + (ages[i + 1] - ages[i]) * k / n);
                    clock.Add(ages[^1]);
                    FlightTime.OfflineThrustFrameClock = clock.ToArray();
                }
            }
            // "auto": the expected overrun, half a frame of game time at the round's compression and 60 fps.
            // "recorded": the round's own overrun, from a disposable enrichment's ThrustClockLagSec (0 if absent).
            string lagArg = System.Environment.GetEnvironmentVariable("AUTOTOT_THRUST_LAG");
            FlightTime.OfflineThrustClockLagSec = lagArg == "auto"
                ? 0.5f * Math.Max(F(r, "MaxCompression"), 1f) / 60f
                : lagArg == "recorded"
                    ? (r.ContainsKey("ThrustClockLagSec") ? Math.Max(F(r, "ThrustClockLagSec"), 0f) : 0f)
                    : float.TryParse(lagArg, System.Globalization.NumberStyles.Float, Inv, out float lag) ? lag : 0f;
        }

        internal static void ClearThrustClock()
        {
            FlightTime.OfflineThrustFrameClock = null;
            FlightTime.OfflineThrustClockLagSec = 0f;
        }

        private static int PathCompare(string[] args)
        {
            bool hull = !args.Contains("--no-hull");
            bool byTime = args.Contains("--time");
            string file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            string resultFile = file == null ? null : Path.ChangeExtension(file, ".result");
            if (resultFile == null || !File.Exists(resultFile))
            {
                Console.Error.WriteLine("usage: autotot-lab pathcmp <solveinput-file> [--no-hull] [--time]");
                return 2;
            }
            const float M = GameUnits.MetersPerUnity;
            Dictionary<string, string> kv = ReadDump(file), r = ReadDump(resultFile);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);
            if (FlownRail(r, in input, out Vector3 flownHeading, out _, out _))
                input = input with
                {
                    LaunchHeading = flownHeading,
                    Att = GameMath.QEuler(-(input.LaunchPitch >= 0f ? input.LaunchPitch : 0f),
                                          FlightTime.YawOf(flownHeading), 0f),
                };
            if (RoundedTargetVel(kv, r, in input, out Vector3 roundedVel))
                input = input with { TargetVel = roundedVel };
            FlightTime.OfflineMotorScale = F(r, "MotorPerformance") > 0f ? F(r, "MotorPerformance") : 1f;
            float stepLo = F(r, "MinFlownStep"), stepHi = F(r, "MaxFlownStep");
            bool steady = stepLo > 0f && stepHi > 0f &&
                          Math.Abs(stepHi - stepLo) <= SteadyStepRelativeTolerance * stepHi;
            FlightTime.OfflineStepSchedule schedule =
                steady ? null : ParseStepSchedule(r.GetValueOrDefault("StepChanges", ""));
            float dt = steady ? stepHi : schedule != null ? schedule.Dt[0] : 0.0166667f;
            FlightTime.OfflineSteps = schedule;
            input = input with
            {
                Hull = !hull ? null : kv.ContainsKey("HullSource") ? input.Hull
                    : HullBoxOf(r, in input, HullsByTargetName(
                        Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(file)), "*.solveinput")))
            };
            var path = new List<(float T, float Flat, float Height, float Kn, float Pitch)>();
            FlightTime.OfflinePath = path;
            SetThrustClock(r);
            float est;
            try { est = FlightTime.SolveOffline(in input, ap, dt, out _); }
            finally
            {
                FlightTime.OfflinePath = null;
                FlightTime.OfflineSteps = null;
                FlightTime.OfflineMotorScale = 1f;
                ClearThrustClock();
            }
            float actual = ActualEnd(r);
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(file)}  est {est:F2} s, actual {actual:F2} s, " +
                              $"offGap {actual - est:+0.00;-0.00} s, hull {(hull ? "on" : "off")}, final range " +
                              $"recorded {F(r, "FinalRangeM"):F0} m, model {(path.Count > 0 ? path[^1].Flat * M : 0f):F0} m " +
                              $"at height {(path.Count > 0 ? path[^1].Height * M : 0f):F0} m");
            Console.WriteLine($"{"tReal",8} {"source",-34}{"flatM",9}{"realH",8}{"modelH",8}{"realKn",8}{"modelKn",8}" +
                              $"{"realP",7}{"modelP",7}{"tModel",9}{"dt",8}");

            void Row(float tReal, string source, float flatM, float realH, float realKn, float realPitch)
            {
                int j = byTime ? path.FindIndex(q => q.T >= tReal) : path.FindIndex(q => q.Flat * M <= flatM);
                if (j <= 0)
                {
                    Console.WriteLine($"{tReal,8:F1} {source,-34}{flatM,9:F0}{realH,8:F0}{"-",8}{realKn,8:F0}{"-",8}{realPitch,7:F0}");
                    return;
                }
                var a = path[j - 1];
                var b = path[j];
                float f = byTime ? (tReal - a.T) / Math.Max(b.T - a.T, 1e-6f)
                                 : (a.Flat * M - flatM) / Math.Max((a.Flat - b.Flat) * M, 1e-6f);
                float tm = a.T + f * (b.T - a.T);
                float hm = (a.Height + f * (b.Height - a.Height)) * M;
                float km = a.Kn + f * (b.Kn - a.Kn);
                float pm = a.Pitch + f * (b.Pitch - a.Pitch);
                Console.WriteLine($"{tReal,8:F1} {source,-34}{flatM,9:F0}{realH,8:F0}{hm,8:F0}{realKn,8:F0}{km,8:F0}" +
                                  $"{realPitch,7:F0}{pm,7:F0}{tm,9:F2}{tm - tReal,+8:+0.00;-0.00}");
            }

            var rows = new List<(float T, Action Print)>();
            foreach (string e in r.GetValueOrDefault("FlightTrack", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = e.Split(':');
                if (p.Length < 10) continue;
                float tr = float.Parse(p[0], Inv), hU = float.Parse(p[2], Inv), kn = float.Parse(p[4], Inv),
                      pitch = float.Parse(p[5], Inv), flatM = float.Parse(p[8], Inv);
                string stage = p[7];
                rows.Add((tr, () => Row(tr, "track " + stage, flatM, hU * M, kn, pitch)));
            }
            foreach (string e in r.GetValueOrDefault("StageEvents", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = e.Split(':');
                if (p.Length < 6 || !p[1].Contains('>') || p[1].StartsWith("seek", StringComparison.Ordinal)) continue;
                float tr = float.Parse(p[0], Inv), flatM = float.Parse(p[2], Inv) * M, hU = float.Parse(p[4], Inv),
                      kn = float.Parse(p[5], Inv);
                string name = "event " + p[1];
                rows.Add((tr, () => Row(tr, name.Length > 34 ? name.Substring(0, 34) : name, flatM, hU * M, kn, float.NaN)));
            }
            foreach (var row in rows.OrderBy(q => q.T)) row.Print();
            Console.WriteLine($"{actual,8:F1} {"impact",-34}{F(r, "FinalRangeM"),9:F0}{"",8}{"",8}{"",8}{"",8}{"",7}{"",7}{est,9:F2}{est - actual,+8:+0.00;-0.00}");
            return 0;
        }

        /// <summary>
        /// popcheck: the model's waypoint look-ahead (FlightTime.WaypointPointAtTime) against the game's own
        /// DesiredPosition (1.5 s) and FuturePosition (8 s), which the guard track records, on every frame
        /// whose 8 s look-ahead reaches a terminal pop-up leg. Each frame's own recorded state is the input,
        /// so this checks the look-ahead port and nothing upstream of it. Heights in Unity units.
        /// </summary>
        private static int PopUpCheck(string[] args)
        {
            // Waypoint.cs:383-387, the same two look-ahead times the step loop uses.
            const float SteerLook = 1.5f, FutureLook = 8f;
            foreach (string path in args)
            {
                string dir = Directory.Exists(Path.Combine(path, "solve")) ? Path.Combine(path, "solve") : path;
                string[] results = File.Exists(path) ? new[] { path }
                    : Directory.GetFiles(dir, "*.result").OrderBy(f => f, StringComparer.Ordinal).ToArray();
                foreach (string res in results)
                {
                    string si = Path.ChangeExtension(res, ".solveinput");
                    if (!File.Exists(si)) continue;
                    Dictionary<string, string> kv = ReadDump(si), r = ReadDump(res);
                    if (!B(kv, "TerminalPopUpEnabled")) continue;
                    FlightTime.WaypointLeg[] legs = Legs(kv);
                    if (legs == null || legs.Length < 2 || legs[^1].Mode != FlightTime.WaypointLegMode.Kappa) continue;
                    string[] names = r.GetValueOrDefault("GuardTrackFields", "").Split(':');
                    if (Array.IndexOf(names, "desired") < 0 || Array.IndexOf(names, "ctxFlatV") < 0) continue;
                    float popUpTargetAlt = F(kv, "TargetAlt0"), popUpHeight = F(kv, "TerminalPopUpHeight");
                    float popUpAngle = F(kv, "TerminalPopUpAngle");
                    Console.WriteLine($"== {Path.GetFileNameWithoutExtension(res)}  route {string.Join(" ; ", legs.Select(l => l.ToString()))}");
                    Console.WriteLine($"{"t",8}{"leg",4}{"pct",7}{"flat",8}{"posY",7}{"vy",7}{"pitch",7} |" +
                                      $"{"des rec",8}{"off",7}{"on",7} |{"fut rec",8}{"off",7}{"on",7} |{"desFlat rec",12}{"on",8}");
                    foreach (string line in r.GetValueOrDefault("GuardTrack", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = names.Zip(line.Split(':'), (n, v) => (n, v)).GroupBy(p => p.n)
                                     .ToDictionary(g => g.Key, g => g.First().v);
                        if (!int.TryParse(f.GetValueOrDefault("wpCount"), out int wpCount)) continue;
                        int active = legs.Length - wpCount;
                        if (active < 0 || active >= legs.Length - 1) continue;
                        if (!TryVec(f, "pos", out Vector3 pos) || !TryVec(f, "desired", out Vector3 des) ||
                            !TryVec(f, "future", out Vector3 fut) || !TryVec(f, "lastWp", out Vector3 last) ||
                            !TryVec(f, "intercept", out Vector3 icpt)) continue;
                        float pct = float.Parse(f["pctDone"], Inv), flatV = float.Parse(f["ctxFlatV"], Inv);
                        float vy = float.Parse(f["vy"], Inv);
                        float flat = new Vector2(icpt.x - pos.x, icpt.z - pos.z).magnitude;
                        if (flatV <= 0f || flat - legs[active].EndDist > flatV * FutureLook) continue;
                        FlightTime.WaypointLeg legEff = legs[active];
                        Vector2 Point(float look, bool on) =>
                            FlightTime.WaypointPointAtTime(legs, active, in legEff, pct, last.y, look, flat,
                                                           pos.y, flatV, vy, popUpTargetAlt,
                                                           on ? popUpHeight : 0f, popUpAngle);
                        Vector2 dOff = Point(SteerLook, false), dOn = Point(SteerLook, true);
                        Vector2 fOff = Point(FutureLook, false), fOn = Point(FutureLook, true);
                        float desFlatRec = new Vector2(icpt.x - des.x, icpt.z - des.z).magnitude;
                        Console.WriteLine($"{float.Parse(f["t"], Inv),8:F2}{active,4}{pct,7:F3}{flat,8:F2}{pos.y,7:F2}{vy,7:F2}" +
                                          $"{float.Parse(f["pitch"], Inv),7:F1} |{des.y,8:F3}{dOff.y,7:F3}{dOn.y,7:F3} |" +
                                          $"{fut.y,8:F3}{fOff.y,7:F3}{fOn.y,7:F3} |{desFlatRec,12:F2}{dOn.x,8:F2}");
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// stepcmp: the model's own flight from one capture at two integration steps, side by side at common
        /// times, to find where a step-size difference is made ("Production-step lateness" in
        /// max-solo-error-below-1s.md). The input as dumped, motor 1, constant steps. dT converts the flat
        /// distance difference into seconds at the first step's closing speed; it grows where the difference
        /// is being made.
        /// </summary>
        private static int StepCompare(string[] args)
        {
            string file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (file == null || !File.Exists(file))
            {
                Console.Error.WriteLine("usage: autotot-lab stepcmp <solveinput-file> [stepA] [stepB] [every-s]");
                return 2;
            }
            float[] steps =
            {
                args.Length > 1 ? float.Parse(args[1], Inv) : 0.1f,
                args.Length > 2 ? float.Parse(args[2], Inv) : 1f / 60f,
            };
            float every = args.Length > 3 ? float.Parse(args[3], Inv) : 5f;
            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap, trackDiag: false);
            FlightTime.OfflineMotorScale = 1f;
            var paths = new List<(float T, float Flat, float Height, float Kn, float Pitch)>[2];
            var geoms = new List<(float T, float Dx, float Dz, float DirX, float DirZ, float WantX, float WantZ)>[2];
            float[] est = new float[2];
            for (int k = 0; k < 2; k++)
            {
                paths[k] = new List<(float T, float Flat, float Height, float Kn, float Pitch)>();
                geoms[k] = new List<(float T, float Dx, float Dz, float DirX, float DirZ, float WantX, float WantZ)>();
                FlightTime.OfflinePath = paths[k];
                FlightTime.OfflineGeom = geoms[k];
                try { est[k] = FlightTime.SolveOffline(in input, ap, steps[k], out _); }
                finally { FlightTime.OfflinePath = null; FlightTime.OfflineGeom = null; }
            }
            const float M = GameUnits.MetersPerUnity;
            Console.WriteLine($"est {est[0]:F3}s at {steps[0]:G4}, {est[1]:F3}s at {steps[1]:G4}: difference {est[0] - est[1]:+0.000;-0.000}s");
            Console.WriteLine($"{"t",7} |{"flatA km",10}{"dFlat m",9}{"dT s",8} |{"hA m",8}{"dH m",7} |{"knA",8}{"dKn",7} |{"pitchA",8}{"dPitch",8}");
            static (float T, float Flat, float Height, float Kn, float Pitch) At(
                List<(float T, float Flat, float Height, float Kn, float Pitch)> p, float t)
            {
                int hi = 0;
                while (hi < p.Count && p[hi].T < t) hi++;
                if (hi == 0) return p[0];
                if (hi >= p.Count) return p[p.Count - 1];
                var a = p[hi - 1]; var b = p[hi];
                float u = b.T > a.T ? (t - a.T) / (b.T - a.T) : 0f;
                return (t, a.Flat + u * (b.Flat - a.Flat), a.Height + u * (b.Height - a.Height),
                        a.Kn + u * (b.Kn - a.Kn), a.Pitch + u * (b.Pitch - a.Pitch));
            }
            // --raw t0 t1: every step of both flights in that window, with the flat closure each step made.
            int rawIdx = Array.IndexOf(args, "--raw");
            if (rawIdx > 0 && rawIdx + 2 < args.Length)
            {
                float t0 = float.Parse(args[rawIdx + 1], Inv), t1 = float.Parse(args[rawIdx + 2], Inv);
                for (int k = 0; k < 2; k++)
                {
                    Console.WriteLine($"-- step {steps[k]:G4}: t, flat u, closure m/s, height m, pitch, " +
                                      "flown yaw minus bearing to target, wanted yaw minus bearing (deg)");
                    var p = paths[k];
                    var g = geoms[k];
                    for (int i = 1; i < p.Count && i < g.Count; i++)
                        if (p[i].T >= t0 && p[i].T <= t1)
                        {
                            float bearing = Mathf.Atan2(g[i].Dx, g[i].Dz) * Mathf.Rad2Deg;
                            float flown = Mathf.DeltaAngle(bearing, Mathf.Atan2(g[i].DirX, g[i].DirZ) * Mathf.Rad2Deg);
                            float wanted = Mathf.DeltaAngle(bearing, Mathf.Atan2(g[i].WantX, g[i].WantZ) * Mathf.Rad2Deg);
                            Console.WriteLine($"{p[i].T,9:F4} {p[i].Flat,12:F5} {(p[i - 1].Flat - p[i].Flat) * M / (p[i].T - p[i - 1].T),10:F3}" +
                                              $" {p[i].Height * M,9:F3} {p[i].Pitch,8:F4} {flown,9:F4} {wanted,9:F4}");
                        }
                }
                return 0;
            }
            float end = Math.Min(paths[0][^1].T, paths[1][^1].T);
            for (float t = 0f; t <= end + 1e-3f; t += every)
            {
                var a = At(paths[0], t); var b = At(paths[1], t);
                float closing = Math.Max(a.Kn * GameUnits.KnotsToUnityPerSecond *
                                         Math.Abs(Mathf.Cos(a.Pitch * Mathf.Deg2Rad)), 1e-4f);
                // The flight with less flat distance left is ahead; dT > 0 means A is behind B.
                float dFlat = a.Flat - b.Flat;
                Console.WriteLine($"{t,7:F1} |{a.Flat * M / 1000f,10:F3}{dFlat * M,9:F2}{dFlat / closing,8:F3} |" +
                                  $"{a.Height * M,8:F1}{(a.Height - b.Height) * M,7:F2} |{a.Kn,8:F2}{a.Kn - b.Kn,7:F3} |" +
                                  $"{a.Pitch,8:F3}{a.Pitch - b.Pitch,8:F3}");
            }
            return 0;
        }

        /// <summary>
        /// kappacheck (D-SAM1): on each loft guard frame of a kinematic round, the game's recorded
        /// DesiredPosition height against (a) a port of Waypoint.GetOrComputeLoftParams and
        /// GetKappaLoftAltitude fed the frame's own state, and (b) the model's Kappa-block command
        /// replicated with the same state and progress. Heights in Unity units, pitch in degrees.
        /// The replication of (b) is a copy of FlightTime.Solve's arithmetic for this check only.
        /// </summary>
        private static int KappaCheck(string[] args)
        {
            const float SteerLook = 1.5f, FutureLook = 8f, Eps = 0.001f;
            var rows = new List<(string Label, bool TerminalLoft, float Port, float Model, float Chord, float RecPitch,
                                 float PortPitch, float ModelPitch, float ChordPitch)>();
            foreach (string path in args)
            {
                string[] results = File.Exists(path) ? new[] { path }
                    : Directory.GetFiles(Directory.Exists(Path.Combine(path, "solve")) ? Path.Combine(path, "solve") : path, "*.result")
                               .OrderBy(f => f, StringComparer.Ordinal).ToArray();
                foreach (string res in results)
                {
                    string si = Path.ChangeExtension(res, ".solveinput");
                    if (!File.Exists(si)) continue;
                    Dictionary<string, string> kv = ReadDump(si), r = ReadDump(res);
                    if (B(kv, "NonKin") || !B(kv, "Lofting")) continue;
                    Vector2[] nodes = Nodes(kv, "AltNodes");
                    if (nodes == null || nodes.Length < 2) continue;
                    string[] names = r.GetValueOrDefault("GuardTrackFields", "").Split(':');
                    if (Array.IndexOf(names, "desired") < 0 || Array.IndexOf(names, "lastWp") < 0) continue;
                    string label = r.GetValueOrDefault("Label", "?");
                    Vector3 launch = V3(kv, "Pos");
                    float flatTotal = F(kv, "FlatDistTotal"), kappaEnd = F(kv, "KappaEndFlat");
                    float loftHeight = F(kv, "LoftAlt"), climbDeg = F(kv, "Ammo.MaxLoftAngle");
                    float descentDeg = F(kv, "Ammo.FinalFlightPhaseMaxAngle");
                    float startAngle = F(kv, "LaunchPitch"), boost = F(kv, "BoostClimbDeg"), modelDescent = F(kv, "DescentDeg");
                    int printed = 0;
                    bool terminalLoft = B(kv, "IsTerminalLoft");
                    Console.WriteLine($"== {Path.GetFileNameWithoutExtension(res)}  loft {loftHeight:F1} u, climb {climbDeg}, descent {descentDeg}, kappaEnd {kappaEnd:F0} of {flatTotal:F0}{(terminalLoft ? ", TerminalLoft" : "")}");
                    Console.WriteLine($"{"t",7}{"pct",8}{"posY",8}{"mag",8}{"desRec",8}{"port",8}{"model",8}{"mChord",8}{"pRec",7}{"pPort",7}{"pModel",7}{"pChord",7}");
                    foreach (string line in r.GetValueOrDefault("GuardTrack", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = names.Zip(line.Split(':'), (n, v) => (n, v)).GroupBy(p => p.n).ToDictionary(g => g.Key, g => g.First().v);
                        if (f.GetValueOrDefault("pitchMode") != "KappaLoft") continue;
                        string stage = f.GetValueOrDefault("stage");
                        if (stage != "ToBearing" && stage != "MaintainLoftAlt") continue;
                        if (!TryVec(f, "pos", out Vector3 pos) || !TryVec(f, "desired", out Vector3 des) ||
                            !TryVec(f, "lastWp", out Vector3 last) || !TryVec(f, "intercept", out Vector3 tgt)) continue;
                        float pct = float.Parse(f["pctDone"], Inv), flatV = float.Parse(f["ctxFlatV"], Inv);
                        float boundary = float.Parse(f.GetValueOrDefault("boundary", "0"), Inv);
                        if (pct >= 0.95f || flatV <= 0f || pos.y < 0.3f) continue;
                        // The waypoint's end point: on the flat line from its start point to the target, the
                        // waypoint's distance short of the target, at the target's height (OnLineToTarget).
                        Vector2 toStart = new Vector2(last.x - tgt.x, last.z - tgt.z);
                        Vector2 dir = toStart.sqrMagnitude > 1e-6f ? toStart.normalized : Vector2.zero;
                        Vector3 aim = new Vector3(tgt.x + dir.x * boundary, tgt.y, tgt.z + dir.y * boundary);
                        float magnitude = new Vector2(aim.x - last.x, aim.z - last.z).magnitude;
                        // The steering point's progress is the lead over the distance to the TARGET, while the
                        // curve is measured over the leg's own length (the first run's 0.743 against 0.747).
                        float remaining = new Vector2(tgt.x - pos.x, tgt.z - pos.z).magnitude;
                        if (magnitude < 1f || remaining < Eps) continue;
                        // Waypoint.CalculatePointAtTime's progress for the 1.5 s point.
                        float lead = flatV * SteerLook;
                        if (lead > remaining) lead = remaining;
                        float prog = Math.Min(pct + (1f - pct) * lead / remaining, 1f);
                        // GetOrComputeLoftParams, non-ballistic, no height limits, motor burning.
                        float tanC = MathF.Tan(Mathf.Clamp(climbDeg, 1e-6f, 89.9999f) * Mathf.Deg2Rad);
                        float tanD = MathF.Tan(Mathf.Clamp(descentDeg, 1e-6f, 89.9999f) * Mathf.Deg2Rad);
                        float invC = 1f / tanC, invD = 1f / tanD;
                        float geo = (magnitude + last.y * invC + aim.y * invD) / (invC + invD);
                        float hPeak = Mathf.Max(Mathf.Max(Mathf.Min(loftHeight, geo), last.y), aim.y);
                        float climbRun = (hPeak - last.y) * invC, descentRun = (hPeak - aim.y) * invD;
                        float midA = MathF.Min(0.95f, climbRun / magnitude);
                        float midB = MathF.Min(0.95f, climbRun / MathF.Max(0.1f, climbRun + descentRun));
                        float mid;
                        if (loftHeight > aim.y) mid = midA;
                        else
                        {
                            float span = aim.y - last.y;
                            mid = Mathf.Lerp(midB, midA, span > 0.001f ? Mathf.Clamp01((loftHeight - last.y) / span) : 1f);
                        }
                        float startSlope = 0f;
                        float sa = Mathf.Max(0f, Mathf.Min(startAngle, climbDeg));
                        if (sa > 0.001f && climbDeg > 0.001f)
                            startSlope = Mathf.Clamp01(MathF.Tan(sa * Mathf.Deg2Rad) / MathF.Tan(Mathf.Clamp(climbDeg, 0.001f, 89f) * Mathf.Deg2Rad));
                        float Curve(float p)
                        {
                            float m = Mathf.Max(mid, 1e-5f);
                            if (p < mid)
                            {
                                float u = p / m, sl = MathF.Max(1f, startSlope);
                                return last.y + (hPeak - last.y) * (u * u * (3f - 2f * u) + sl * u * (1f - u) * (1f - u));
                            }
                            float v = (p - mid) / Mathf.Max(1f - mid, 1e-5f);
                            float v2 = v * v, v3 = v2 * v;
                            return (2f * v3 - 3f * v2 + 1f) * hPeak + (-2f * v3 + 3f * v2) * aim.y + (v3 - v2) * 3f * (aim.y - hPeak);
                        }
                        // GetKappaLoftAltitude at the desired point's progress.
                        float d = prog - pct;
                        float h0 = Curve(pct);
                        float slopeAngle = Mathf.Atan2(Curve(pct + d) - h0, d * magnitude);
                        float run = Mathf.Max(d * magnitude, 0.001f);
                        float chord = h0 + run * Mathf.Tan(slopeAngle);
                        float floorRun = Mathf.Clamp(flatV * FutureLook, run, Mathf.Max((1f - pct) * magnitude, 0.001f));
                        float hFloor = Curve(Mathf.Min(pct + floorRun / magnitude, 1f));
                        float floorAngle = Mathf.Min(Mathf.Atan2(pos.y - hFloor, floorRun), MathF.PI * 89f / 180f);
                        float port = Mathf.Max(chord, pos.y - run * Mathf.Tan(floorAngle));

                        // The model's Kappa block, same state, progress taken from the frame.
                        float xNow = new Vector2(pos.x - launch.x, pos.z - launch.z).magnitude;
                        float flatDist = new Vector2(tgt.x - pos.x, tgt.z - pos.z).magnitude;
                        float routeDist = Mathf.Max(kappaEnd > 0f ? kappaEnd : xNow + flatDist, Eps);
                        float look = Mathf.Clamp(flatV * SteerLook, Eps, Mathf.Max(flatDist, Eps));
                        float kp = pct;
                        float futureProgress = Mathf.Min(kp + (1f - kp) * look / Mathf.Max(flatDist, Eps), 1f);
                        float curveNow = FlightTime.InterpNodeAltOffline(nodes, routeDist * kp);
                        float curveFuture = FlightTime.InterpNodeAltOffline(nodes, routeDist * futureProgress);
                        float curveRun = Mathf.Max((futureProgress - kp) * routeDist, Eps);
                        float tangentAlt = curveNow + look * (curveFuture - curveNow) / curveRun;
                        float correctionRun = Mathf.Clamp(flatV * FutureLook, look, Mathf.Max((1f - kp) * routeDist, look));
                        float correctionProgress = Mathf.Min(kp + correctionRun / routeDist, 1f);
                        float correctionAlt = FlightTime.InterpNodeAltOffline(nodes, routeDist * correctionProgress);
                        float floorAlt = pos.y + look * (correctionAlt - pos.y) / correctionRun;
                        float model = Mathf.Max(tangentAlt, floorAlt);

                        // The model's own curve (AltNodes over the model's route length) through the game's
                        // look-ahead arithmetic: the curve's height at the steering point's progress, placed at the
                        // lead distance, with the game's 8 s floor.
                        float dN = prog - pct;
                        float runN = Mathf.Max(dN * routeDist, 0.001f);
                        float h0N = FlightTime.InterpNodeAltOffline(nodes, routeDist * pct);
                        float chordN = h0N + runN * Mathf.Tan(Mathf.Atan2(
                            FlightTime.InterpNodeAltOffline(nodes, routeDist * (pct + dN)) - h0N, dN * routeDist));
                        float floorRunN = Mathf.Clamp(flatV * FutureLook, runN, Mathf.Max((1f - pct) * routeDist, 0.001f));
                        float hFloorN = FlightTime.InterpNodeAltOffline(nodes, routeDist * Mathf.Min(pct + floorRunN / routeDist, 1f));
                        float floorAngleN = Mathf.Min(Mathf.Atan2(pos.y - hFloorN, floorRunN), MathF.PI * 89f / 180f);
                        float nodeChord = Mathf.Max(chordN, pos.y - runN * Mathf.Tan(floorAngleN));

                        float recPitch = float.Parse(f["pitch"], Inv);
                        float portPitch = Mathf.Atan2(port - pos.y, lead) * Mathf.Rad2Deg;
                        float modelPitch = Mathf.Atan2(model - pos.y, look) * Mathf.Rad2Deg;
                        float chordPitch = Mathf.Atan2(nodeChord - pos.y, lead) * Mathf.Rad2Deg;
                        rows.Add((label, terminalLoft, port - des.y, model - des.y, nodeChord - des.y, recPitch, portPitch, modelPitch, chordPitch));
                        if (printed++ < 12 || f.GetValueOrDefault("why") == "change")
                            Console.WriteLine($"{float.Parse(f["t"], Inv),7:F1}{pct,8:F4}{pos.y,8:F2}{magnitude,8:F1}{des.y,8:F2}{port,8:F2}{model,8:F2}{nodeChord,8:F2}" +
                                              $"{recPitch,7:F1}{portPitch,7:F1}{modelPitch,7:F1}{chordPitch,7:F1}");
                    }
                }
            }
            if (rows.Count == 0) { Console.WriteLine("kappacheck: no loft frames"); return 1; }
            float Median(IEnumerable<float> v) { var sorted = v.OrderBy(x => x).ToList(); return sorted[sorted.Count / 2]; }
            string Within(IEnumerable<float> v) { var l = v.ToList(); return $"{100f * l.Count(x => Math.Abs(x) <= 0.3f) / l.Count,5:F1}%"; }
            var kept = rows.Where(x => !x.TerminalLoft).ToList();
            Console.WriteLine($"kappacheck: {rows.Count} frames ({kept.Count} not TerminalLoft). Within 0.3 u of the recording, not TerminalLoft: " +
                              $"port {Within(kept.Select(x => x.Port))}, model {Within(kept.Select(x => x.Model))}, model curve through game arithmetic {Within(kept.Select(x => x.Chord))}");
            foreach (var g in rows.GroupBy(x => x.Label).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {g.Key,-15}{(g.First().TerminalLoft ? " TL" : "   ")} {g.Count(),4} frames | within 0.3 u: port {Within(g.Select(x => x.Port))} " +
                                  $"model {Within(g.Select(x => x.Model))} chord {Within(g.Select(x => x.Chord))} | median miss port {Median(g.Select(x => x.Port)),6:F3} " +
                                  $"model {Median(g.Select(x => x.Model)),6:F3} chord {Median(g.Select(x => x.Chord)),6:F3} u | pitch rec {Median(g.Select(x => x.RecPitch)),5:F1} " +
                                  $"port {Median(g.Select(x => x.PortPitch)),5:F1} model {Median(g.Select(x => x.ModelPitch)),5:F1} chord {Median(g.Select(x => x.ChordPitch)),5:F1}");
            return 0;
        }

        private static bool TryVec(Dictionary<string, string> f, string key, out Vector3 v)
        {
            v = default;
            if (!f.TryGetValue(key, out string s)) return false;
            string[] p = s.Split(',');
            if (p.Length != 3) return false;
            v = new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv));
            return true;
        }

        private static void Replay(string file, float[] steps)
        {
            Dictionary<string, string> kv = ReadDump(file);
            AmmunitionParameters ap = BuildAmmo(kv);
            FlightTime.SolveInput input = BuildInput(kv, ap);

            Console.WriteLine();
            Console.WriteLine($"=== {kv.GetValueOrDefault("Label", Path.GetFileNameWithoutExtension(file))} ===");
            Console.WriteLine($"    range {input.FlatDistTotal:F0}u, {(input.NonKin ? "non-kinematic" : "kinematic")}, " +
                              $"max {input.MaxVelKn:F0}kn, launch pitch {input.LaunchPitch:F1} deg");
            if (input.ConfiguredLoftAlt > 0f)
                Console.WriteLine($"    loft configured {input.ConfiguredLoftAlt:F1}u, " +
                                  $"resolved {input.LoftAlt:F1}u, " +
                                  $"node peak {(input.AltNodes == null ? 0f : input.AltNodes.Max(n => n.y)):F1}u, " +
                                  $"Kappa end {input.KappaEndFlat:F1}u");
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
                string delta = dt == steps[0]
                    ? "     -   "
                    : $"{est - baseline,+9:F3}";

                Console.WriteLine($"    {dt,6:F4}    {est,7:F2}s   {delta}   {(int)(est / dt),7}    {sw.Elapsed.TotalMilliseconds,6:F1}ms");
            }
        }
    }
}
