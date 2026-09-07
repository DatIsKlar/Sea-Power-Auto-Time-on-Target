using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Observational diagnostics for coordinated launches. Flight tracker records each missile's
    /// outcome (flight time, final range). Launch expectations tally requested vs. actual launches
    /// with sim-time adaptive deadlines. The tracker feeds observation anchoring.
    /// See docs/ARCHITECTURE.md "Launch diagnostics design".
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        // Per-missile record kept while a friendly missile is airborne, so we can report its
        // impact (flight time + final range) after the missile object is gone.
        //
        // A class, not a struct, and for the same reason LaunchExpectation below is: this is read,
        // mutated and stored back once per tracked missile per frame. As a struct that was a copy of
        // roughly twenty fields each way, and it forced the two MaybeLog helpers to take the sample
        // and hand it back so the caller could reassign it.
        private sealed class FlightSample
        {
            public float LaunchTime; public string AmmoName; public string TargetName;
            public float LastDistM; public float LastSeenTime;
            public ObjectBase Target;      // kept for residual lookup (predicted vs observed impact)
            // The target the SEEKER is on right now, refreshed every tick. These missiles carry their
            // own active seeker and, unlike grouped rounds, do not split up: one assigned to a ship
            // deep in a formation locks onto whatever it detects first and kills a ship in front.
            // Without this, the impact line reports the ORIGINAL assignment and the gap line compares
            // the flight to the ship actually struck against the estimate for the ship it was sent to,
            // which reads as a large negative gap and is not estimator error at all.
            public ObjectBase CurrentTarget;
            public string CurrentTargetName;
            public float PredictedImpact;  // stamped from EngagementBoard, so the residual survives
                                           // the target dying (the board row is pruned on death). -1 = none.
            public float LastTelemetrySim; // throttle for the periodic `track` telemetry line.
            // D7 of docs/plans/open/aircraft-anchor-impact-slide.md: the round-based impact
            // prediction, "now + range/speed", captured from the LIVE round at two fixed ages. This
            // is the quantity the proposed fix would use in place of re-estimating from the shooter,
            // so scoring it against the actual impact says whether the fix is sound. Two ages rather
            // than one, so a prediction that drifts with the target's motion is distinguishable from
            // one that is simply biased. -1 = never captured (flight too short, or not coordinated).
            public float RoundPredEarlySim = -1f;
            public float RoundPredLateSim = -1f;
            // sim-vs-actual gap characterization (grounded flight-model investigation)
            public float KinEstAtLaunch;   // FlightTime.Estimate captured at first sighting (active
                                           // estimator; the grounded integrator on beta). -1 = unavailable.
            public float LegacyEstAtLaunch; // Game's own EstimateShot InterceptTime at launch, logged
                                           // beside KinEstAtLaunch to compare accuracy. -1 = unavailable.
            public float WpEstAtLaunch;    // Ported waypoint-sim InterceptTime at launch (Phase 2), logged
                                           // beside KinEstAtLaunch to A/B the port. -1 = unavailable.
            public float PeakSpeedKn;      // Highest speed the LIVE missile reached in flight (kn).
                                           // Ground truth for the integrator's stage-speed schedule.
            public float PeakAltU;         // max altitude (Unity units) seen in flight ; loft-arc height.
            public float LastSpeedKn;      // most-recent speed (kn) ; approx terminal/impact speed.
            public bool Coordinated;       // captured at first sighting: is this a missile AutoTOT
                                           // fired (target has an EngagementBoard row)? Scopes all
                                           // verbose diagnostics to our own shots (not defensive SAMs).
            // Last observed value of the game's own flight-stage machine, so a change can be
            // detected per tick. The `track` line samples the stage every 15s, which is far too
            // coarse to localize a transition; `stage-obs` fires on the change itself and is our
            // only direct ground truth for the region model's phase boundaries.
            public WeaponBase.FlightStage LastStage;
        }

        // Sampling cadence for the `track` trace lives in TelemetryCadence, shared with the
        // integrator's `sim-track`, because the two are read against each other sample for sample.
        private static readonly Dictionary<WeaponBase, FlightSample> _flightTracker =
            new Dictionary<WeaponBase, FlightSample>();

        private static readonly List<WeaponBase> _trackerScratch = new List<WeaponBase>();

        internal static int LastWeaponCount;

        internal static int LastTrackedMissiles;

        internal static float LastScanLoopMs;

        internal static float LastFinalizeMs;

        internal static float LastCleanupMs;

        private static readonly Stopwatch _subSw = new Stopwatch();

        /// <summary>Rolling state of one launcher object, shared by every order it serves.</summary>
        private sealed class LauncherTrace
        {
            public int Id;                  // reference identity from LauncherProbe
            public string SystemName;
            public string State;
            public float SinceSim;          // when this launcher entered State
            public float LastWarmupHold = -1f;  // measured OnRailWarmup duration, D1's observed side
            public int WarmupCount;         // how many times it entered OnRailWarmup, D3's answer
        }

        /// <summary>
        /// Every launcher object currently being traced, keyed by its reference identity. The
        /// launcher's state machine belongs to the launcher, not to the order that woke it, so two
        /// orders sharing one launcher must read one trace: the second Tomahawk order in the
        /// 2026-09-06 run sat through the first order's cycles, and a per-order trace attributed
        /// those cycles to both. Cleared when the last order closes, so a mission's launchers do not
        /// accumulate.
        /// </summary>
        private static readonly Dictionary<int, LauncherTrace> _launcherTraces =
            new Dictionary<int, LauncherTrace>();

        /// <summary>Launchers already sampled this tick, so a shared one logs one line, not one per
        /// order.</summary>
        private static readonly HashSet<int> _tracedThisTick = new HashSet<int>();

        internal static void Reset()
        {
            _flightTracker.Clear();
            _trackerScratch.Clear();
            _launchExpectations.Clear();
            _launcherTraces.Clear();
            _shipLastLaunchSim.Clear();
            _retiredExpectations.Clear();
            UncreditedLaunches = 0;
        }

        /// <summary>
        /// Each tick: record a baseline the first time we see each friendly missile airborne, keep
        /// its last-known distance/time updated, and when a tracked missile vanishes (hit target,
        /// intercepted, or ran out) report its outcome ; flight time and final range. Also closes
        /// out launch expectations whose ripple window has elapsed.
        /// </summary>
        internal static void Tick(float simNow)
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            ScanTrackedMissiles(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastScanLoopMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            LastTrackedMissiles = _flightTracker.Count;

            SampleEngageStates(simNow);

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            FinalizeExpectations(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastFinalizeMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            CollectDeadTrackers();
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastCleanupMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            RetireDeadTrackers();
        }

        /// <summary>
        /// Walk every live player missile with a live target: refresh its tracking sample, or open
        /// one if this is the first time the round has been seen.
        /// </summary>
        private static void ScanTrackedMissiles(float simNow)
        {
            List<WeaponBase> weapons = Singleton<ObjectsManager>.Instance._listOfAllWeapons;
            LastWeaponCount = weapons.Count;
            for (int i = 0; i < weapons.Count; i++)
            {
                WeaponBase w = weapons[i];
                if (w == null || w.IsDestroyed) continue;
                if (w._type != ObjectBase.ObjectType.Missile || !w.IsPlayerObject) continue;
                ObjectBase tgt = w.CurrentIntendedTargetObject;
                if (tgt == null || tgt.IsDestroyed) continue;

                float distM = GameUnits.MetersBetween(w, tgt);
                // Trajectory characterization (peak alt / terminal speed) only feeds the verbose
                // `gap`/`sim-traj` lines ; skip the work entirely in normal play.
                // The per-missile flight-model block runs off this, including the two extra flight
                // sims and the waypoint sim it needs purely to print a comparison. On the flight-model
                // gate now, not VerboseLog: it is estimator internals, and it was 85 percent of a
                // verbose log's volume. See Coordinator.TraceFlightModel.
                bool verbose = Coordinator.TraceFlightModel;
                if (_flightTracker.TryGetValue(w, out FlightSample existing))
                {
                    existing.LastDistM = distM;
                    existing.LastSeenTime = simNow;
                    if (!ReferenceEquals(existing.CurrentTarget, tgt))
                    {
                        existing.CurrentTarget = tgt;
                        existing.CurrentTargetName = tgt.getUIDAndName();
                    }
                    if (verbose)
                    {
                        float altU = w.transform != null ? w.transform.position.y : 0f;
                        if (altU > existing.PeakAltU) existing.PeakAltU = altU;
                        existing.LastSpeedKn = w._velocityInKnots;
                        if (w._velocityInKnots > existing.PeakSpeedKn) existing.PeakSpeedKn = w._velocityInKnots;
                        if (existing.Coordinated) SampleRoundPrediction(w, existing, distM, simNow);
                    }
                    // Refresh the stamped prediction while the board row still lives, so it picks up
                    // the anchor-finalized impact; keep the last non-negative value once it's gone.
                    if (EngagementBoard.TryGetPredictedImpact(tgt, out float livePred))
                        existing.PredictedImpact = livePred;
                    if (existing.Coordinated)
                    {
                        // Unthrottled: a stage change is an event, not a sample.
                        MaybeLogStageChange(w, tgt, existing, simNow);
                        MaybeLogTelemetry(w, tgt, existing, distM, simNow);
                    }
                }
                else
                {
                    EngagementBoard.TryGetPredictedImpact(tgt, out float pred0);
                    // Only missiles AutoTOT fired (target has a coordination row) get verbose
                    // per-missile diagnostics ; defensive SAMs the escorts auto-fire target incoming
                    // threats, which never get a row, so we skip their sim work and logging entirely.
                    bool coordinated = EngagementBoard.IsCoordinated(tgt);
                    // Game's own single-shot sim InterceptTime for this shot, stamped so the impact
                    // line can print the sim-vs-actual gap. Cached (0.5s TTL) ; LogTrackInit reuses it.
                    // Verbose-only: it feeds the verbose `gap` line, and a kinematic sim per new missile
                    // would be wasted work (and a spike under big salvos) in normal play.
                    float est = -1f;
                    float legacyEst = -1f;
                    if (verbose && coordinated && w._launchPlatform != null && w._ap != null)
                    {
                        CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                        est = FlightTime.Estimate(w._launchPlatform, w._ap._ammunitionFileName, tgt);
                        CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                        CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                        // The game's own EstimateShot InterceptTime, for side-by-side accuracy
                        // comparison against the (now primary) grounded integrator. See gap line.
                        legacyEst = FlightTime.MaxRangePreciseEndTime(w._launchPlatform, w._ap, tgt);
                    }
                    // Ported waypoint-sim estimate (Phase 2 A/B) ; computed even when UseWaypointSim
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
                        CurrentTarget = tgt,
                        CurrentTargetName = tgt.getUIDAndName(),
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
                        MaybeLogTelemetry(w, tgt, fresh, distM, simNow);
                    }
                    _flightTracker[w] = fresh;
                }
            }
        }

        /// <summary>Gather trackers whose missile is gone, into the reused scratch list.</summary>
        private static void CollectDeadTrackers()
        {
            _trackerScratch.Clear();
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed || w._type != ObjectBase.ObjectType.Missile)
                    _trackerScratch.Add(w);
            }
        }

        /// <summary>
        /// Drop the gathered trackers, logging each round's impact and its estimate-vs-actual gap on
        /// the way out. Deliberately outside the timed region, matching the original instrumentation.
        /// </summary>
        private static void RetireDeadTrackers()
        {
            for (int i = 0; i < _trackerScratch.Count; i++)
            {
                WeaponBase w = _trackerScratch[i];
                if (_flightTracker.TryGetValue(w, out FlightSample s))
                {
                    float flightTime = s.LastSeenTime - s.LaunchTime;
                    if (Coordinator.TraceFlightModel && s.Coordinated)
                    {
                        // The predicate, not its rendering: the two gap branches below used to
                        // re-test this by comparing the display string back to "HIT".
                        bool hit = s.LastDistM <= HitRangeM;
                        string outcome = hit ? "HIT" : "ended";
                        // Did the seeker end up on a different ship than the one this round was
                        // assigned? If so LastDistM is measured to the SUBSTITUTE, and "HIT" means it
                        // killed something, just not what was ordered. ReferenceEquals throughout,
                        // deliberately: UnityEngine.Object overloads == so a DESTROYED object compares
                        // equal to null, which would hide the switch the moment the substitute sank.
                        bool retargeted = !ReferenceEquals(s.CurrentTarget, s.Target)
                                       && !ReferenceEquals(s.CurrentTarget, null)
                                       && !ReferenceEquals(s.Target, null);
                        string switched = retargeted ? $" [RETARGETED -> {s.CurrentTargetName}]" : "";
                        // Residual = observed impact − predicted (anchor-finalized) impact. Read from
                        // the sample, not the board, so it still prints after the target is gone,
                        // which is the late/missed case most worth measuring.
                        string residual = "";
                        if (s.PredictedImpact >= 0f)
                            residual = $", predicted {s.PredictedImpact:0.0}, residual {s.LastSeenTime - s.PredictedImpact:+0.0;-0.0}s";
                        residual += RoundPredictionScore(s);
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] impact {s.AmmoName} -> {s.TargetName}: {outcome} at sim {s.LastSeenTime:0.0} " +
                            $"(flight {flightTime:0.0}s, final range {s.LastDistM:0} m){switched}{residual}");
                        // gap = actual flown time − the sim estimate captured at launch (positive =>
                        // the sim UNDER-predicts). Peak altitude and terminal speed say WHERE the gap
                        // comes from. HIT only. A RETARGETED round is excluded and reported as such:
                        // its flight is to the ship the seeker picked while simEst was computed for
                        // the ship it was ordered against, so its gap measures formation geometry,
                        // not the estimator.
                        if (retargeted && s.KinEstAtLaunch > 0f && hit)
                        {
                            Bootstrap.Log.LogInfo(
                                $"[AutoTOT] gap {s.AmmoName} -> {s.TargetName}: SKIPPED, seeker switched to " +
                                $"{s.CurrentTargetName}. Flight {flightTime:0.0}s is to that ship; " +
                                $"simEst {s.KinEstAtLaunch:0.0}s was for the assigned one. Not estimator error.");
                        }
                        else if (s.KinEstAtLaunch > 0f && hit)
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
    }
}
