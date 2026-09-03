using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Observational diagnostics for coordinated launches. Two subsystems share one tick:
    ///
    /// 1. Flight tracker — records a baseline the first time each friendly missile is seen
    ///    airborne, follows its distance-to-target, and reports the outcome (flight time + final
    ///    range) once the missile object is gone, so the log shows when each salvo member
    ///    ACTUALLY arrived, not just when it launched.
    ///
    /// 2. Launch expectations — per coordinated order, how many missiles were requested vs. how
    ///    many actually left the rail (attributed via <c>WeaponBase._launchPlatform</c>). Catches
    ///    the rare case where one ship fires short while its sister fires the identical order in
    ///    full. Deadlines are sim-time and adaptive, so time compression / pause don't produce
    ///    false shortfalls.
    ///
    /// The tracker also feeds coordination: each anchor's observed launch times are forwarded to
    /// its <see cref="Coordinator.Scheduled"/> entry for observation anchoring.
    /// </summary>
    internal static class LaunchDiagnostics
    {
        // Per-missile record kept while a friendly missile is airborne, so we can report its
        // impact (flight time + final range) after the missile object is gone.
        private struct FlightSample
        {
            public float LaunchTime; public string AmmoName; public string TargetName;
            public float LastDistM; public float LastSeenTime;
            public ObjectBase Target;      // kept for residual lookup (predicted vs observed impact)
            public float PredictedImpact;  // stamped from EngagementBoard, so the residual survives
                                           // the target dying (the board row is pruned on death). -1 = none.
            public float LastTelemetrySim; // throttle for the periodic `track` telemetry line.
            // ---- sim-vs-actual gap characterization (grounded flight-model investigation) ----
            public float KinEstAtLaunch;   // FlightTime.Estimate captured at first sighting (active
                                           // estimator; the grounded integrator on beta). -1 = unavailable.
            public float LegacyEstAtLaunch; // Game's own EstimateShot InterceptTime at launch, logged
                                           // beside KinEstAtLaunch to compare accuracy. -1 = unavailable.
            public float WpEstAtLaunch;    // Ported waypoint-sim InterceptTime at launch (Phase 2), logged
                                           // beside KinEstAtLaunch to A/B the port. -1 = unavailable.
            public float PeakSpeedKn;      // Highest speed the LIVE missile reached in flight (kn).
                                           // Ground truth for the integrator's stage-speed schedule.
            public float PeakAltU;         // max altitude (Unity units) seen in flight — loft-arc height.
            public float LastSpeedKn;      // most-recent speed (kn) — approx terminal/impact speed.
            public bool Coordinated;       // captured at first sighting: is this a missile AutoTOT
                                           // fired (target has an EngagementBoard row)? Scopes all
                                           // verbose diagnostics to our own shots (not defensive SAMs).
            // Last observed value of the game's own flight-stage machine, so a change can be
            // detected per tick. The `track` line samples the stage every 15s, which is far too
            // coarse to localize a transition; `stage-obs` fires on the change itself and is our
            // only direct ground truth for the region model's phase boundaries.
            public WeaponBase.FlightStage LastStage;
        }

        // Matches the integrator's own sim-track cadence so the two traces can be compared
        // sample-for-sample, and deliberately not a round number -- a 15s sampler once aliased a
        // limit cycle of period exactly 15.0s and reported a 10km oscillation as a constant.
        private const float TelemetryIntervalSim = 7f; // sim seconds between per-missile telemetry samples
        // Dense sampling through the launch phase, to match the integrator's sim-track burst: that
        // window lasts up to ~19s (initial flight phase + ToBearing) and is where the residual fixed
        // offset is created, so a 7s cadence leaves only one sample inside it.
        private const float LaunchBurstWindowSim = 20f;
        private const float LaunchBurstIntervalSim = 1f;
        // Inner tier matching the integrator's: the launch nose-over completes inside ~2s on a
        // vertically-launched sea-skimmer, so 1s cadence gives only two samples of it.
        private const float NoseOverWindowSim = 5f;
        private const float NoseOverIntervalSim = 0.25f;
        private static readonly Dictionary<WeaponBase, FlightSample> _flightTracker =
            new Dictionary<WeaponBase, FlightSample>();
        private static readonly List<WeaponBase> _trackerScratch = new List<WeaponBase>();
        private static readonly Stopwatch _tickSw = new Stopwatch();
        internal static float LastTickMs;
        internal static int LastWeaponCount;
        internal static int LastTrackedMissiles;
        internal static float LastScanLoopMs;
        internal static float LastFinalizeMs;
        internal static float LastCleanupMs;
        private static readonly Stopwatch _subSw = new Stopwatch();

        // ---- Per-ship shot-count accounting ----
        // Records, per coordinated order, how many missiles were requested vs. how many actually
        // left the rail. Sim-time deadlined so time compression / pause don't produce false
        // shortfalls.
        private sealed class LaunchExpectation
        {
            public ObjectBase Unit;
            public string AmmoId;      // order id (for facts / logging)
            public string AmmoFile;    // resolved _ammunitionFileName, for matching airborne missiles
            public ObjectBase Target;
            public int Requested;
            public int Launched;
            public float DeadlineSim;  // sim time by which the salvo should have fully rippled
            public Coordinator.Scheduled Linked;   // scheduled entry when this order is a batch anchor
            public float IniInterval;  // a-priori per-round interval (adaptive-deadline fallback)
            public float WaveTailSim;  // expected span+gaps of reload waves after wave 1
            public float LastLaunchSim = -1f; // most recent observed launch (sim s)
        }
        private static readonly List<LaunchExpectation> _launchExpectations = new List<LaunchExpectation>();
        private const float ExpectationMarginSim = 10f; // slack (sim s) beyond the computed ripple time
        private const float HitRangeM = 500f;           // closer than this when a missile vanishes => counted as a HIT

        internal static void Reset()
        {
            _flightTracker.Clear();
            _trackerScratch.Clear();
            _launchExpectations.Clear();
        }

        /// <summary>
        /// Each tick: record a baseline the first time we see each friendly missile airborne, keep
        /// its last-known distance/time updated, and when a tracked missile vanishes (hit target,
        /// intercepted, or ran out) report its outcome — flight time and final range. Also closes
        /// out launch expectations whose ripple window has elapsed.
        /// </summary>
        internal static void Tick(float simNow)
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;
            if (Coordinator.ProfilingEnabled) _tickSw.Restart();

            List<WeaponBase> weapons = Singleton<ObjectsManager>.Instance._listOfAllWeapons;
            LastWeaponCount = weapons.Count;
            
            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            for (int i = 0; i < weapons.Count; i++)
            {
                WeaponBase w = weapons[i];
                if (w == null || w.IsDestroyed) continue;
                if (w._type != ObjectBase.ObjectType.Missile || !w.IsPlayerObject) continue;
                ObjectBase tgt = w.CurrentIntendedTargetObject;
                if (tgt == null || tgt.IsDestroyed) continue;

                float distM = GameUnits.MetersBetween(w, tgt);
                // Trajectory characterization (peak alt / terminal speed) only feeds the verbose
                // `gap`/`sim-traj` lines — skip the work entirely in normal play.
                bool verbose = Coordinator.VerboseLog;
                if (_flightTracker.TryGetValue(w, out FlightSample existing))
                {
                    existing.LastDistM = distM;
                    existing.LastSeenTime = simNow;
                    if (verbose)
                    {
                        float altU = w.transform != null ? w.transform.position.y : 0f;
                        if (altU > existing.PeakAltU) existing.PeakAltU = altU;
                        existing.LastSpeedKn = w._velocityInKnots;
                        if (w._velocityInKnots > existing.PeakSpeedKn) existing.PeakSpeedKn = w._velocityInKnots;
                    }
                    // Refresh the stamped prediction while the board row still lives, so it picks up
                    // the anchor-finalized impact; keep the last non-negative value once it's gone.
                    if (EngagementBoard.TryGetPredictedImpact(tgt, out float livePred))
                        existing.PredictedImpact = livePred;
                    if (existing.Coordinated)
                    {
                        // Unthrottled: a stage change is an event, not a sample.
                        existing = MaybeLogStageChange(w, tgt, existing, simNow);
                        existing = MaybeLogTelemetry(w, tgt, existing, distM, simNow);
                    }
                    _flightTracker[w] = existing;
                }
                else
                {
                    EngagementBoard.TryGetPredictedImpact(tgt, out float pred0);
                    // Only missiles AutoTOT fired (target has a coordination row) get verbose
                    // per-missile diagnostics — defensive SAMs the escorts auto-fire target incoming
                    // threats, which never get a row, so we skip their sim work and logging entirely.
                    bool coordinated = EngagementBoard.IsCoordinated(tgt);
                    // Game's own single-shot sim InterceptTime for this shot, stamped so the impact
                    // line can print the sim-vs-actual gap. Cached (0.5s TTL) — LogTrackInit reuses it.
                    // Verbose-only: it feeds the verbose `gap` line, and a kinematic sim per new missile
                    // would be wasted work (and a spike under big salvos) in normal play.
                    float est = -1f;
                    float legacyEst = -1f;
                    if (verbose && coordinated && w._launchPlatform != null && w._ap != null)
                    {
                        est = FlightTime.Estimate(w._launchPlatform, w._ap._ammunitionFileName, tgt);
                        // The game's own EstimateShot InterceptTime, for side-by-side accuracy
                        // comparison against the (now primary) grounded integrator. See gap line.
                        legacyEst = FlightTime.MaxRangePreciseEndTime(w._launchPlatform, w._ap, tgt);
                    }
                    // Ported waypoint-sim estimate (Phase 2 A/B) — computed even when UseWaypointSim
                    // is off, so one fire compares wpEst vs simEst vs actual before it drives timing.
                    float wpEst = -1f;
                    if (verbose && coordinated && WaypointSim.Ready && WaypointSim.FullReady &&
                        w._launchPlatform != null && w._ap != null)
                        wpEst = WaypointSim.EndTime(w._launchPlatform, w._ap, tgt, emitDiag: true);
                    var fresh = new FlightSample
                    {
                        LaunchTime = GameClock.LaunchStamp(w),
                        AmmoName = (w._ap != null ? w._ap._ammunitionFileName : "?"),
                        TargetName = tgt.getUIDAndName(),
                        LastDistM = distM,
                        LastSeenTime = simNow,
                        Target = tgt,
                        PredictedImpact = pred0,   // -1 if this target isn't (yet) coordinated
                        LastTelemetrySim = -1f,
                        KinEstAtLaunch = est,
                        LegacyEstAtLaunch = legacyEst,
                        WpEstAtLaunch = wpEst,
                        PeakAltU = verbose && w.transform != null ? w.transform.position.y : 0f,
                        LastSpeedKn = verbose ? w._velocityInKnots : 0f,
                        PeakSpeedKn = verbose ? w._velocityInKnots : 0f,
                        Coordinated = coordinated,
                        LastStage = w._flightStage,
                    };
                    // First sighting of this missile => it just left the rail. Credit it to the
                    // matching pending order (this branch fires exactly once per WeaponBase, so no
                    // double count). Credited regardless of coordination (a no-op if nothing matches).
                    CreditLaunch(w, tgt);
                    if (coordinated)
                    {
                        LogTrackInit(w, est);
                        fresh = MaybeLogTelemetry(w, tgt, fresh, distM, simNow);
                    }
                    _flightTracker[w] = fresh;
                }
            }
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastScanLoopMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            LastTrackedMissiles = _flightTracker.Count;
            
            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            FinalizeExpectations(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastFinalizeMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            _trackerScratch.Clear();
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed || w._type != ObjectBase.ObjectType.Missile)
                    _trackerScratch.Add(w);
            }
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastCleanupMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) { _tickSw.Stop(); LastTickMs = (float)(_tickSw.Elapsed.TotalMilliseconds); }

            for (int i = 0; i < _trackerScratch.Count; i++)
            {
                WeaponBase w = _trackerScratch[i];
                if (_flightTracker.TryGetValue(w, out FlightSample s))
                {
                    float flightTime = s.LastSeenTime - s.LaunchTime;
                    if (Coordinator.VerboseLog && s.Coordinated)
                    {
                        string outcome = (s.LastDistM <= HitRangeM) ? "HIT" : "ended";
                        // Residual = observed impact − our predicted (anchor-finalized) impact, so the
                        // log shows coordination accuracy directly instead of hand-diffing. Uses the
                        // value stamped on the sample, so it still prints after the target (and its
                        // board row) is gone — the late/missed rounds we most want to measure.
                        string residual = "";
                        if (s.PredictedImpact >= 0f)
                            residual = $", predicted {s.PredictedImpact:0.0}, residual {s.LastSeenTime - s.PredictedImpact:+0.0;-0.0}s";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] impact {s.AmmoName} -> {s.TargetName}: {outcome} at sim {s.LastSeenTime:0.0} " +
                            $"(flight {flightTime:0.0}s, final range {s.LastDistM:0} m){residual}");
                        // Grounded flight-model signal: the game's own single-shot sim InterceptTime
                        // (captured at launch) vs the ACTUAL flown time. gap = actual − sim (positive =>
                        // sim UNDER-predicts, missile flies slower/longer than modelled). Peak altitude
                        // and terminal speed characterize WHERE the gap comes from (loft arc / terminal
                        // decel). Only meaningful for rounds that actually reached the target (HIT).
                        if (s.KinEstAtLaunch > 0f && outcome == "HIT")
                        {
                            string legacy = s.LegacyEstAtLaunch > 0f
                                ? $", legacyEst {s.LegacyEstAtLaunch:0.0}s (gap {flightTime - s.LegacyEstAtLaunch:+0.0;-0.0}s)"
                                : "";
                            string wp = s.WpEstAtLaunch > 0f
                                ? $", wpEst {s.WpEstAtLaunch:0.0}s (gap {flightTime - s.WpEstAtLaunch:+0.0;-0.0}s)"
                                : "";
                            Bootstrap.Log.LogInfo(
                                $"[AutoTOT] gap {s.AmmoName} -> {s.TargetName}: simEst {s.KinEstAtLaunch:0.0}s, " +
                                $"actual {flightTime:0.0}s, gap {flightTime - s.KinEstAtLaunch:+0.0;-0.0}s, " +
                                $"peakAlt {s.PeakAltU:0}u, realPeakSpd {s.PeakSpeedKn:0}kn, termSpd {s.LastSpeedKn:0}kn{wp}{legacy}");
                        }
                    }
                }
                _flightTracker.Remove(w);
            }
        }

        /// <summary>
        /// Invokes <paramref name="visit"/> for every friendly missile currently tracked in flight
        /// along with its live intended target (both guaranteed non-null and alive). Used by the
        /// engagement overview to count in-flight rounds per target.
        /// </summary>
        internal static void ForEachInFlight(System.Action<WeaponBase, ObjectBase> visit)
        {
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed) continue;
                ObjectBase t = w.CurrentIntendedTargetObject;
                if (t == null || t.IsDestroyed) continue;
                visit(w, t);
            }
        }

        // Record what one coordinated order asked for, so Tick can tally how many of its missiles
        // actually launch. Called from Coordinator.Fire after the order is issued. Anchors are
        // registered even as single shots — their observed launches finalize the batch impact.
        internal static void RegisterExpectation(Coordinator.Intent it, Coordinator.Scheduled sched)
        {
            if (it.Unit == null || it.Target == null) return;
            int shots = Mathf.Max(1, it.Shots);
            bool isAnchor = sched != null && sched.IsAnchor;
            if (shots <= 1 && !isAnchor) return; // a single shot can't come up "short"

            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            string ammoFile = it.Unit.getAmmunitionByName(it.AmmoId)?._ap?._ammunitionFileName;
            if (string.IsNullOrEmpty(ammoFile)) return; // can't attribute missiles without it

            float interval = (f.Valid && f.ShotInterval > 0f) ? f.ShotInterval : LauncherFactsSource.FallbackShotInterval;
            float reload = f.Valid ? f.ReloadGap : 0f;
            float ripple = (shots - 1) * interval + Mathf.Max(0, it.Waves - 1) * reload;
            // Reload waves after wave 1 launch AnchorShots*interval + reload later each; allow for
            // them up front so the adaptive deadline below doesn't flag them as a shortfall.
            float waveTail = it.Waves > 1
                ? (it.Waves - 1) * (Mathf.Max(1, it.AnchorShots) * interval + reload)
                : 0f;

            _launchExpectations.Add(new LaunchExpectation
            {
                Unit = it.Unit,
                AmmoId = it.AmmoId,
                AmmoFile = ammoFile,
                Target = it.Target,
                Requested = shots,
                Launched = 0,
                DeadlineSim = GameClock.SimNow() + ripple + waveTail + ExpectationMarginSim,
                Linked = isAnchor ? sched : null,
                IniInterval = interval,
                WaveTailSim = waveTail,
            });
        }

        // Credit a just-launched missile to the first still-open order that matches its firing
        // ship, ammo, and intended target.
        private static void CreditLaunch(WeaponBase w, ObjectBase tgt)
        {
            if (_launchExpectations.Count == 0 || w == null) return;
            ObjectBase platform = w._launchPlatform;
            string ammoFile = w._ap != null ? w._ap._ammunitionFileName : null;
            if (platform == null || ammoFile == null) return;

            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Launched < e.Requested && e.Unit == platform && e.Target == tgt && e.AmmoFile == ammoFile)
                {
                    float launchStamp = GameClock.LaunchStamp(w);   // one reflection read, reused
                    e.Launched++;
                    e.LastLaunchSim = launchStamp;
                    // Feed the batch anchor's live impact prediction (observation anchoring).
                    if (e.Linked != null && e.Linked.IsAnchor && !e.Linked.RippleDone)
                        e.Linked.LaunchTimes.Add(launchStamp);
                    return;
                }
            }
        }

        // Close out orders whose ripple window has elapsed. A shortfall against a live target is a
        // real anomaly (WARN); a shortfall when the target died mid-salvo is expected (info).
        private static void FinalizeExpectations(float simNow)
        {
            if (_launchExpectations.Count == 0) return;
            for (int i = _launchExpectations.Count - 1; i >= 0; i--)
            {
                LaunchExpectation e = _launchExpectations[i];

                // Adaptive deadline: every observed launch extends the window, because a launcher's
                // REALIZED cadence (hatch cycles, task reassignment) is often far slower than its INI
                // pace — a fixed deadline fired mid-ripple and logged false SHORTFALLs. Only a
                // true stall (no launch for max(4x measured cadence, 30s), plus any reload-wave
                // tail) counts as a shortfall now.
                if (e.Launched > 0 && e.Launched < e.Requested)
                {
                    float interval = e.IniInterval;
                    List<float> lt = e.Linked?.LaunchTimes;
                    if (lt != null && lt.Count >= 2)
                        interval = (lt[lt.Count - 1] - lt[0]) / (lt.Count - 1);
                    if (interval <= 0f) interval = LauncherFactsSource.FallbackShotInterval;
                    float adaptive = e.LastLaunchSim
                    + Mathf.Max(Coordinator.StallCadenceMultiplier * interval, Coordinator.StallMinWindowSim)
                    + e.WaveTailSim;
                    if (adaptive > e.DeadlineSim) e.DeadlineSim = adaptive;
                }

                bool done = e.Launched >= e.Requested;
                if (!done && simNow < e.DeadlineSim) continue;

                _launchExpectations.RemoveAt(i);
                if (e.Launched >= e.Requested)
                {
                    if (Coordinator.VerboseLog)
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] order complete {e.AmmoId} from {SafeName(e.Unit)} -> {SafeName(e.Target)}: " +
                            $"launched {e.Launched}/{e.Requested}.");
                    continue;
                }

                bool targetGone = e.Target == null || e.Target.IsDestroyed;
                bool shooterGone = e.Unit == null || e.Unit.IsDestroyed;
                int ready = 0, reserve = 0, inv = 0;
                if (!shooterGone)
                {
                    LauncherFactsSource.Facts f = LauncherFactsSource.Get(e.Unit, e.AmmoId);
                    ready = f.ReadyRounds; reserve = f.Reserve;
                    e.Unit.AmmunitionAmountDictionary.TryGetValue(e.AmmoId, out inv);
                }
                string detail =
                    $"launched {e.Launched}/{e.Requested}, ready {ready}, reserve {reserve}, inventory {inv}, " +
                    $"targetGone {targetGone}, shooterGone {shooterGone}";

                if (targetGone || shooterGone)
                {
                    if (Coordinator.VerboseLog)
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] order ended early {e.AmmoId} from {SafeName(e.Unit)} -> {SafeName(e.Target)}: {detail}");
                }
                else
                {
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] SHORTFALL {e.AmmoId} from {SafeName(e.Unit)} -> {SafeName(e.Target)}: {detail}");
                }
            }
        }

        // One-time per-missile line at first sighting: nominal speeds + our kinematic estimate, so a
        // late group can be read against what the game's own solo sim predicted. VerboseLog only.
        private static void LogTrackInit(WeaponBase w, float est)
        {
            if (!Coordinator.VerboseLog) return;
            AmmunitionParameters ap = w?._ap;
            if (ap == null) return;
            bool grouped = ap._maxGroupSize > 1;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] track-init {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                $"nominal cruise {ap._maxVelocityInKnots:0}/loft {ap._maxLoftVelocityInKnots:0}/" +
                $"term {ap._terminalVelocityInKnots:0} kn, kinEst {est:0.0}s, grouped {grouped}");

            // Grounded-integrator per-phase breakdown — pins WHICH loft phase (climb/cruise/descent)
            // the model gets wrong when its intercept time disagrees with the actual flown time.
            // peakAlt vs loftAlt = climb-height error; a slow VTerm + long DescentTime = descent error.
            if (w._launchPlatform != null && w.CurrentIntendedTargetObject != null &&
                FlightTime.TryIntegratedPhaseDiag(w._launchPlatform, ap._ammunitionFileName,
                    w.CurrentIntendedTargetObject, out float intT, out FlightTime.IntegratedPhases ph))
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] int-phases {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                    $"intercept {intT:0.0}s, lofting {ph.Lofting}, loftAlt {ph.LoftAltTarget:0}u, " +
                    $"peakAlt {ph.PeakAltU:0}u, climb {ph.ClimbTime:0.0}s/cruise {ph.CruiseTime:0.0}s/" +
                    $"descent {ph.DescentTime:0.0}s, spd start {ph.VStart:0}/climbExit {ph.VClimbExit:0}/" +
                    $"cruiseExit {ph.VCruiseExit:0}/term {ph.VTerm:0}kn, " +
                    $"finalDist {ph.FinalDistU:0}u/termDist {ph.TermDistU:0}u");

                // The region model's phase boundaries, printed in the same units the `stage-obs`
                // line reports so the two can be diffed directly. finalDist should line up with the
                // real MaintainLoftAlt -> Maintain{SeaSkimming,FinalFlightAlt} transition, and
                // diveStart with the real -> TerminalApproach transition. These boundaries are
                // hand-derived from ini fields and are the most fragile part of the model (see
                // docs/plans/done/2026-09-02-waypoint-sim-port.md, the rejected Part L), so this is the evidence
                // that says whether they are right rather than merely tuned.
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] stage-model {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                    $"finalDist {ph.FinalDistU:0}u, termDist {ph.TermDistU:0}u, " +
                    $"diveStart {(ph.DiveStartU >= 0f ? ph.DiveStartU.ToString("0") + "u" : "never")}, " +
                    $"loftAlt {ph.LoftAltTarget:0}u, onsetDeg {ph.DescentOnsetDeg:0.0}°");
            }

        }

        // Fires once per transition of the game's OWN flight-stage machine
        // (Launch -> ToBearing -> MoveToLoftAlt -> MaintainLoftAlt -> ... -> TerminalApproach).
        //
        // This is the only direct ground truth we have for the boundaries the region model
        // hand-derives from ini fields: the distance at which the real missile leaves the loft is
        // exactly the model's finalDist, and the distance at which it enters TerminalApproach is
        // exactly the model's diveStart. The `track` line already prints the stage but samples it
        // every 15s, which on a Mach-10 lofter brackets a transition to a ~43km window -- useless
        // for the boundaries we actually argue about.
        //
        // Distances are reported three ways because the two existing telemetry lines disagree:
        // `track` logs 3D SLANT while `sim-track` logs flat, and on a missile 80km up those differ
        // enormously. flat is the one comparable to the model's boundaries.
        //
        // Unthrottled but change-gated, so a full flight emits well under a dozen lines.
        // VerboseLog + Coordinated only. Returns the (stage-updated) sample.
        private static FlightSample MaybeLogStageChange(WeaponBase w, ObjectBase tgt, FlightSample s, float simNow)
        {
            if (!Coordinator.VerboseLog) return s;
            WeaponBase.FlightStage now = w._flightStage;
            if (now == s.LastStage) return s;
            WeaponBase.FlightStage prev = s.LastStage;
            s.LastStage = now;

            float altU = w.transform != null ? w.transform.position.y : 0f;
            float slantM = GameUnits.MetersBetween(w, tgt);
            float slantU = slantM / GameUnits.MetersPerUnity;
            float altDelta = altU - (tgt.transform != null ? tgt.transform.position.y : 0f);
            // Flat range from the slant range and the height difference. Clamped: a near-overhead
            // sample can go slightly negative under the sqrt through float error.
            float flatU = Mathf.Sqrt(Mathf.Max(slantU * slantU - altDelta * altDelta, 0f));

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] stage-obs {(w._ap != null ? w._ap._ammunitionFileName : "?")}#{w.GetInstanceID()}: " +
                $"{prev} -> {now} at t+{simNow - s.LaunchTime:0.0}s, " +
                $"flat {flatU:0}u ({flatU * GameUnits.MetersPerUnity / 1000f:0.0}km), " +
                $"slant {slantM / 1000f:0.0}km, alt {altU:0.0}u, spd {w._velocityInKnots:0}kn");

            return s;
        }

        // Throttled per-missile telemetry: actual vs nominal speed, group/leader state, in-group
        // commanded speed, flight stage, altitude, distance. Separates group-drag (leader spd ~0.6x
        // nominal) from a solo estimate error. VerboseLog only. Returns the (throttle-updated) sample.
        private static FlightSample MaybeLogTelemetry(WeaponBase w, ObjectBase tgt, FlightSample s, float distM, float simNow)
        {
            if (!Coordinator.VerboseLog) return s;
            float sinceLaunch = simNow - s.LaunchTime;
            float interval = (sinceLaunch < NoseOverWindowSim) ? NoseOverIntervalSim
                           : (sinceLaunch < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
                           : TelemetryIntervalSim;
            if (s.LastTelemetrySim >= 0f && (simNow - s.LastTelemetrySim) < interval) return s;
            float prevSampleSim = s.LastTelemetrySim;
            s.LastTelemetrySim = simNow;

            AmmunitionParameters ap = w._ap;
            float nominal = ap != null ? ap._maxVelocityInKnots : 0f;
            Missile m = w as Missile;
            string grp = m != null && m._inMissileGroup ? (m.GroupLeader ? "grpL" : "grp") : "solo";
            float vGrp = m != null ? m._inGroupVelocityInKnots : -1f;
            string stage = m != null ? m._flightStage.ToString() : "?";
            float altU = w.transform != null ? w.transform.position.y : 0f;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] track {(ap != null ? ap._ammunitionFileName : "?")}#{w.GetInstanceID()} -> {SafeName(tgt)}: " +
                $"t+{simNow - s.LaunchTime:0.0}s spd {w._velocityInKnots:0}/{nominal:0}kn {grp} " +
                $"vGrp {vGrp:0} stage {stage} alt {altU:0.0} dist {distM / 1000f:0.0}km");

            return s;
        }

        internal static string SafeName(ObjectBase o)
        {
            if (o == null) return "?";
            try { return o.getUIDAndName(); } catch { return "?"; }
        }
    }
}
