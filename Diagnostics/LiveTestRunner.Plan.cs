using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The plan file, the result file and the per-round record. Split from the state machine so
    /// the file layout lives in one place; the driver script parses the same layout.
    /// </summary>
    internal sealed partial class LiveTestRunner
    {
        internal static string Directory => Path.Combine(Paths.BepInExRootPath, "AutoTOT-livetest");
        private static string PlanPath => Path.Combine(Directory, "plan.ini");
        private static string RunningPath => Path.Combine(Directory, "plan.running");
        private static string ResultsDir => Path.Combine(Directory, "results");

        /// <summary>
        /// One plan section. The driver writes exactly one section per launch; if the file holds
        /// more, the first is run and the rest are reported as ignored.
        /// </summary>
        private sealed class Plan
        {
            public string Name = "unnamed";
            public string Mission;
            public float Compression = 1f;
            public float WaitForContactSec = 120f;
            public float TimeoutSec = 1500f;
            // Instrumentation, per section. Accuracy work wants the flight-model trace; the
            // performance scenario must not have it, because it runs extra sims per round and
            // roughly ten times the log volume, which distorts what it is trying to measure.
            // VerboseLogging additionally REGISTERS a launch expectation for every order, which
            // the runner's own done-detection reads, so it is forced on and not a plan key.
            public bool TraceFlightModel = true;
            public bool VerticalProfile;
            public bool AlignmentTrace;
            // Counterfactual for beam-only-launcher-alignment.md A7: EndAlignment removes the ending
            // alignment of a shared uid instead of its replacement. Runner plans only.
            public bool AlignmentUidFix;
            public bool Profiling;
            public bool VerifySolve;
            // Stage the panel on the first shot and hold it on screen for this long instead of
            // firing, so a screenshot can verify a label the runner cannot read. 0 disables. The
            // run still ends normally, with no rounds and no corpus captures.
            public float PanelHoldSec;
            // Fire through the planner instead of handing the coordinator the shot list: stage every
            // shot in the panel with the first shooter's whole formation rostered, keep it on screen
            // from contact on, and press FIRE THIS TARGET. PanelHoldSec wins if both are set.
            public bool PanelFire;
            // Send each shot as a direct order through Coordinator.FireNow, the panel's FIRE NOW path
            // without its range filter: nothing is coordinated or held. Watch becomes an observation
            // window that ends on TimeoutSec, since direct orders register no launch expectation.
            public bool FireNow;
            // Accept shots inside the weapon's minimum launch range at arm, so a plan can create the
            // order the game turns to unmask and then never launches.
            public bool AllowInsideMinRange;
            // Delay firing until the geometry is right. See LiveTestRunner.HoldSatisfied.
            public float HoldUntilSec;
            public string HoldShooter;
            public float HoldRangeNm;
            public readonly List<ShotLine> Shots = new List<ShotLine>();

            public sealed class ShotLine
            {
                public string Shooter; public string Target; public string Ammo = "*"; public int Salvo = 1;
            }

            // "Shooter | ammo | salvo": a player-style SAM order at whatever missile first threatens the
            // shooter inside that SAM's range. See LiveTestRunner.TickManualOrders.
            public readonly List<ManualLine> Manual = new List<ManualLine>();

            public sealed class ManualLine
            {
                public string Shooter; public string Ammo; public int Salvo = 1;
            }
        }

        private sealed class RoundRecord
        {
            public string Ammo, Target, Outcome, DumpKey;
            public float FinalRangeM, Flight, Predicted, Residual, Gap, MinComp, MaxComp;
            // The step the round was ACTUALLY integrated with. The game halves its physics rate
            // below 30 fps and restores it above 40, with nothing in between, so two runs of one
            // plan can integrate differently and neither says so. A round whose step moved
            // mid-flight is not reproducible and must not be scored.
            public float MinStep, MaxStep;
            public bool Retargeted, HasResidual, HasGap;
        }

        private readonly List<RoundRecord> _rounds = new List<RoundRecord>();

        /// <summary>
        /// Consume-once gate. True only when a plan was present, parsed and renamed. The rename
        /// happens before anything else so a run that dies leaves no plan behind to fire on the
        /// user's next manual launch.
        /// </summary>
        private bool ConsumePlanFile()
        {
            string planPath = PlanPath;
            if (!File.Exists(planPath)) return false;
            if (File.Exists(RunningPath)) File.Delete(RunningPath);
            File.Move(planPath, RunningPath);
            System.IO.Directory.CreateDirectory(ResultsDir);

            string text = File.ReadAllText(RunningPath);
            List<Plan> plans = ParsePlans(text);
            if (plans.Count == 0)
            {
                Finish("REJECTED plan file has no section");
                return true;
            }
            // One section per process, deliberately. Running a batch in one process was tried on
            // 2026-09-12 to save the 39 s each launch costs, and the game cannot do it: loading a
            // mission over another throws "Gameobject is not a root in a scene" out of
            // SceneManager_missionLoaded and registers the Effects SpawnPool twice, both as modal
            // dialogs. The driver splits sections into separate launches instead.
            _plan = plans[0];
            if (plans.Count > 1)
                _notes.Add($"{plans.Count - 1} further section(s) ignored; one section per launch");
            if (string.IsNullOrEmpty(_plan.Mission)) { Finish("REJECTED plan has no Mission="); return true; }
            if (_plan.Shots.Count == 0) { Finish("REJECTED plan has no Shot lines"); return true; }
            return true;
        }

        private static List<Plan> ParsePlans(string text)
        {
            var plans = new List<Plan>();
            Plan cur = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi).Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = new Plan { Name = line.Substring(1, line.Length - 2).Trim() };
                    plans.Add(cur);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0 || cur == null) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.Equals("Mission", StringComparison.OrdinalIgnoreCase)) cur.Mission = val;
                else if (key.Equals("Compression", StringComparison.OrdinalIgnoreCase)) cur.Compression = ParseF(val, 1f);
                else if (key.Equals("WaitForContactSec", StringComparison.OrdinalIgnoreCase)) cur.WaitForContactSec = ParseF(val, 120f);
                else if (key.Equals("TimeoutSec", StringComparison.OrdinalIgnoreCase)) cur.TimeoutSec = ParseF(val, 1500f);
                else if (key.Equals("TraceFlightModel", StringComparison.OrdinalIgnoreCase)) cur.TraceFlightModel = ParseB(val, true);
                else if (key.Equals("VerticalProfile", StringComparison.OrdinalIgnoreCase)) cur.VerticalProfile = ParseB(val, false);
                else if (key.Equals("AlignmentTrace", StringComparison.OrdinalIgnoreCase)) cur.AlignmentTrace = ParseB(val, false);
                else if (key.Equals("AlignmentUidFix", StringComparison.OrdinalIgnoreCase)) cur.AlignmentUidFix = ParseB(val, false);
                else if (key.Equals("Profiling", StringComparison.OrdinalIgnoreCase)) cur.Profiling = ParseB(val, false);
                else if (key.Equals("VerifySolve", StringComparison.OrdinalIgnoreCase)) cur.VerifySolve = ParseB(val, false);
                else if (key.Equals("PanelHoldSec", StringComparison.OrdinalIgnoreCase)) cur.PanelHoldSec = ParseF(val, 0f);
                else if (key.Equals("PanelFire", StringComparison.OrdinalIgnoreCase)) cur.PanelFire = ParseB(val, false);
                else if (key.Equals("FireNow", StringComparison.OrdinalIgnoreCase)) cur.FireNow = ParseB(val, false);
                else if (key.Equals("AllowInsideMinRange", StringComparison.OrdinalIgnoreCase)) cur.AllowInsideMinRange = ParseB(val, false);
                else if (key.Equals("HoldUntilSec", StringComparison.OrdinalIgnoreCase)) cur.HoldUntilSec = ParseF(val, 0f);
                else if (key.Equals("HoldUntilRangeNm", StringComparison.OrdinalIgnoreCase))
                {
                    // "Taskforce1Aircraft1:30"
                    int colon = val.LastIndexOf(':');
                    if (colon > 0)
                    {
                        cur.HoldShooter = val.Substring(0, colon).Trim();
                        cur.HoldRangeNm = ParseF(val.Substring(colon + 1), 0f);
                    }
                }
                else if (key.StartsWith("ManualAtInbound", StringComparison.OrdinalIgnoreCase))
                {
                    Plan.ManualLine m = ParseManual(val);
                    if (m != null) cur.Manual.Add(m);
                }
                else if (key.StartsWith("Shot", StringComparison.OrdinalIgnoreCase))
                {
                    Plan.ShotLine s = ParseShot(val);
                    if (s != null) cur.Shots.Add(s);
                }
            }
            return plans;
        }

        /// <summary>"Shooter > Target | ammo | salvo". Ammo and salvo are optional.</summary>
        private static Plan.ShotLine ParseShot(string val)
        {
            string[] parts = val.Split('|');
            string[] pair = parts[0].Split('>');
            if (pair.Length != 2) return null;
            var s = new Plan.ShotLine { Shooter = pair[0].Trim(), Target = pair[1].Trim() };
            if (parts.Length > 1 && parts[1].Trim().Length > 0) s.Ammo = parts[1].Trim();
            if (parts.Length > 2) s.Salvo = (int)ParseF(parts[2], 1f);
            return string.IsNullOrEmpty(s.Shooter) || string.IsNullOrEmpty(s.Target) ? null : s;
        }

        /// <summary>"Shooter | ammo | salvo". Salvo is optional.</summary>
        private static Plan.ManualLine ParseManual(string val)
        {
            string[] parts = val.Split('|');
            if (parts.Length < 2) return null;
            var m = new Plan.ManualLine { Shooter = parts[0].Trim(), Ammo = parts[1].Trim() };
            if (parts.Length > 2) m.Salvo = (int)ParseF(parts[2], 1f);
            return string.IsNullOrEmpty(m.Shooter) || string.IsNullOrEmpty(m.Ammo) ? null : m;
        }

        private static string SafeFileName(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.Length == 0 ? "unnamed" : sb.ToString();
        }

        private static bool ParseB(string v, bool dflt)
        {
            v = v.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            return dflt;
        }

        private static float ParseF(string v, float dflt)
            => float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : dflt;

        private void Record(LaunchDiagnostics.FlightSample s, float flightTime)
        {
            if (!s.Coordinated) return;
            bool hit = s.LastDistM <= LaunchDiagnostics.HitRangeM;
            bool retargeted = !ReferenceEquals(s.CurrentTarget, s.Target)
                           && !ReferenceEquals(s.CurrentTarget, null)
                           && !ReferenceEquals(s.Target, null);
            var r = new RoundRecord
            {
                Ammo = s.AmmoName,
                Target = s.TargetName + (retargeted ? " [RETARGETED -> " + s.CurrentTargetName + "]" : ""),
                Outcome = hit ? "ARRIVED" : "ended",
                FinalRangeM = s.LastDistM,
                Flight = flightTime,
                Retargeted = retargeted,
                DumpKey = s.DumpKey ?? "",
                MinComp = s.MinCompression == float.MaxValue ? 1f : s.MinCompression,
                MaxComp = s.MaxCompression,
                MinStep = s.MinFlownStep == float.MaxValue ? 0f : s.MinFlownStep,
                MaxStep = s.MaxFlownStep,
            };
            if (hit && s.PredictedImpact >= 0f)
            {
                // Same definition as the impact log line: launch plus the flight measured to the hit,
                // not the last frame the round was seen, which at 10x can be 0.2 to 0.3 s earlier.
                r.HasResidual = true; r.Predicted = s.PredictedImpact; r.Residual = s.LaunchTime + flightTime - s.PredictedImpact;
            }
            if (hit && !retargeted && s.KinEstAtLaunch > 0f)
            {
                r.HasGap = true; r.Gap = flightTime - s.KinEstAtLaunch;
            }
            _rounds.Add(r);
            Bootstrap.Log.LogInfo($"[AutoTOT] livetest: recorded {r.Ammo} -> {r.Target} {r.Outcome} ({_rounds.Count} so far)");
        }

        /// <summary>
        /// Write the result, then the DONE marker, then quit. Runs once; later calls are ignored.
        /// The marker is written before the quit so the result survives a hung exit.
        /// </summary>
        private void Finish(string status)
        {
            if (_state == State.Done) return;
            _state = State.Done;
            try
            {
                System.IO.Directory.CreateDirectory(ResultsDir);
                var sb = new StringBuilder();
                CultureInfo ic = CultureInfo.InvariantCulture;
                sb.Append("run=").Append(_plan?.Name ?? "unnamed").Append('\n');
                sb.Append("mission=").Append(_plan?.Mission ?? "").Append('\n');
                sb.Append("status=").Append(status).Append('\n');
                sb.Append("gates=verbose,")
                  .Append(_plan != null && _plan.TraceFlightModel ? "trace," : "")
                  .Append(_plan != null && _plan.VerticalProfile ? "vertical," : "")
                  .Append(_plan != null && _plan.AlignmentTrace ? "alignment," : "")
                  .Append(_plan != null && _plan.AlignmentUidFix ? "uidfix," : "")
                  .Append(_plan != null && _plan.Profiling ? "profiling," : "")
                  .Append(_plan != null && _plan.VerifySolve ? "verifysolve," : "")
                  .Append('\n');
                sb.Append("compression=").Append((_plan?.Compression ?? 1f).ToString("0.#", ic)).Append('\n');
                sb.Append("fired_sim=").Append(_firedSim.ToString("0.0", ic)).Append('\n');
                sb.Append("finished_sim=").Append(GameClock.SimNow().ToString("0.0", ic)).Append('\n');
                sb.Append("rounds=").Append(_rounds.Count).Append('\n');
                if (_plan != null && _plan.HoldUntilSec > 0f)
                    sb.Append("held_until_sec=").Append(_plan.HoldUntilSec.ToString("0.#", ic)).Append('\n');
                if (_plan != null && !string.IsNullOrEmpty(_plan.HoldShooter))
                    sb.Append("held_until_range=").Append(_plan.HoldShooter).Append(':')
                      .Append(_plan.HoldRangeNm.ToString("0.#", ic)).Append('\n');
                foreach (string n in _notes) sb.Append("note=").Append(n).Append('\n');
                foreach (RoundRecord r in _rounds)
                {
                    // ammo|target|outcome|finalRangeM|flight|predicted|residual|gap|retargeted|minComp|maxComp|minStep|maxStep|dumpKey
                    sb.Append("round=")
                      .Append(r.Ammo).Append('|')
                      .Append(r.Target).Append('|')
                      .Append(r.Outcome).Append('|')
                      .Append(r.FinalRangeM.ToString("0", ic)).Append('|')
                      .Append(r.Flight.ToString("0.0", ic)).Append('|')
                      .Append(r.HasResidual ? r.Predicted.ToString("0.0", ic) : "").Append('|')
                      .Append(r.HasResidual ? r.Residual.ToString("+0.0;-0.0", ic) : "").Append('|')
                      .Append(r.HasGap ? r.Gap.ToString("+0.0;-0.0", ic) : "").Append('|')
                      .Append(r.Retargeted ? "1" : "0").Append('|')
                      .Append(r.MinComp.ToString("0.#", ic)).Append('|')
                      .Append(r.MaxComp.ToString("0.#", ic)).Append('|')
                      .Append(r.MinStep.ToString("0.0000", ic)).Append('|')
                      .Append(r.MaxStep.ToString("0.0000", ic)).Append('|')
                      .Append(r.DumpKey).Append('\n');
                }
                string name = SafeFileName(_plan?.Name ?? "unnamed");
                File.WriteAllText(Path.Combine(ResultsDir, name + ".result"), sb.ToString());
                File.WriteAllText(Path.Combine(ResultsDir, "DONE"), status + "\n");
                Bootstrap.Log.LogInfo($"[AutoTOT] livetest: finished, status '{status}', {_rounds.Count} round(s) recorded. Quitting.");
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] livetest: could not write results:\n{e}");
            }
            // The log must reach disk: BepInEx's listener flushes on a timer, and the first smoke
            // run lost its own last lines to a quit that beat it.
            FlushDiskLog();
            _active = null;
            _quitAt = Time.realtimeSinceStartup + QuitDelay;
        }

        private const float QuitDelay = 1.5f;
        private float _quitAt = -1f;

        private static void FlushDiskLog()
        {
            try
            {
                foreach (BepInEx.Logging.ILogListener l in BepInEx.Logging.Logger.Listeners)
                    if (l is BepInEx.Logging.DiskLogListener d) d.LogWriter?.Flush();
            }
            catch (Exception e) { ModLog.Warn("livetest: log flush failed", e); }
        }
    }
}
