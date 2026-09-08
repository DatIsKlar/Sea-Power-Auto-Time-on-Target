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
            // The shooter's state at launch, so a round that switched targets can be re-estimated
            // against the ship it actually hit. Captured at first sighting, which is within a tick
            // of the round leaving the rail. Zero position = not captured.
            public Vector3 LaunchPosU;
            public float LaunchVelKn;
            public ObjectBase Shooter;     // needed to re-run the estimate; always guard, it can die
            public string ShooterAmmoId;
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
            _noArrivalWarned.Clear();
            UncreditedLaunches = 0;
        }

        // Ammunition already reported as having ended a flight without arriving, so the warning is
        // one line per ammunition per mission rather than one per round.
        private static readonly HashSet<string> _noArrivalWarned = new HashSet<string>();

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
                        LaunchPosU = w._launchPlatform != null ? w._launchPlatform.transform.position
                                                              : Vector3.zero,
                        LaunchVelKn = w._launchPlatform != null ? w._launchPlatform._velocityInKnots : 0f,
                        Shooter = w._launchPlatform,
                        ShooterAmmoId = (w._ap != null ? w._ap._ammunitionFileName : null),
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
        /// The estimator gap for a round whose seeker switched, measured against the ship it
        /// actually hit. The strict <c>gap</c> line stays suppressed for these, so nothing here can
        /// be mistaken for a reference measurement; this is a second, differently named line.
        ///
        /// <para><b>Why this exists.</b> Against a formation most rounds switch, so most rounds
        /// report no estimator measurement at all, and any error that only appears under formation
        /// conditions is invisible by construction. In one 152-order strike, 15 of 16 rounds that
        /// would otherwise have reported a gap were suppressed. A formation strike is also what
        /// players actually fly, so without this a user's log says nothing about the model.</para>
        ///
        /// <para><b>Why it cannot be precise, stated in the line itself.</b> The substitute's
        /// position AT LAUNCH was never recorded, because which ship a seeker will pick is not known
        /// in advance and snapshotting every candidate per round is exactly the per-round work that
        /// has cost frames before. It is back-projected from the impact position instead, so the
        /// answer carries the error in that back-projection: a ship that turned or changed speed
        /// during the flight was not where this puts it. The line prints how far it rewound and the
        /// tolerance that implies, so the number is never read as tighter than it is.</para>
        ///
        /// <para>Good to a few seconds, which is the point. Every estimator defect found so far has
        /// been 4 to 72 s.</para>
        /// </summary>
        private static void LogSubstituteGap(FlightSample s, float flightTime)
        {
            ObjectBase sub = s.CurrentTarget;
            if (sub == null || sub.IsDestroyed) return;
            if (s.Shooter == null || s.Shooter.IsDestroyed) return;
            if (s.ShooterAmmoId == null || s.LaunchPosU == Vector3.zero) return;
            if (flightTime <= 0f) return;
            try
            {
                // Rewind the substitute along its own velocity to where it was when this round left
                // the rail. Straight-line, deliberately: a curve fitted to one velocity sample would
                // be a guess dressed as precision.
                Vector3 subVel = sub._velocityVecInUnity;
                Vector3 subAtLaunch = sub.transform.position - subVel * flightTime;
                float rewoundM = (sub.transform.position - subAtLaunch).magnitude
                                 * GameUnits.MetersPerUnity;

                var launch = new FlightTime.LaunchState(s.LaunchPosU, s.LaunchVelKn,
                                                        Vector3.zero, subAtLaunch);
                float est = FlightTime.EstimateFromLaunch(s.Shooter, s.ShooterAmmoId, sub, launch);
                if (est <= FlightTime.MinValidSeconds)
                {
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] gap-sub {s.AmmoName} -> {s.CurrentTargetName}: no estimate, the " +
                        "integrator declined for the substitute's launch geometry.");
                    return;
                }
                // Tolerance: the rewind distance is the part of the geometry we had to reconstruct,
                // so express it in seconds at the speed this round actually averaged.
                float meanSpeedMs = (flightTime > 0f)
                    ? (s.LaunchPosU - sub.transform.position).magnitude
                      * GameUnits.MetersPerUnity / flightTime
                    : 0f;
                string tol = meanSpeedMs > 1f
                    ? $"about {rewoundM / meanSpeedMs:0.#}s"
                    : "unknown";
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] gap-sub {s.AmmoName} -> {s.CurrentTargetName} " +
                    $"(seeker switched from {s.TargetName}): est {est:0.0}s, actual {flightTime:0.0}s, " +
                    $"gap {flightTime - est:+0.0;-0.0}s | APPROXIMATE: the substitute's launch " +
                    $"position was back-projected {rewoundM / 1000f:0.0}km, tolerance {tol}");
            }
            catch (System.Exception e)
            {
                ModLog.VerboseWarn("substitute gap failed", e);
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

                    // Deliberately OUTSIDE the VerboseLog/TraceFlightModel gate below. A round that
                    // ends without arriving neither falls short at launch (the SHORTFALL line counts
                    // orders that never fired) nor impacts (the `impact` line needs an arrival), so
                    // with the default settings every instrument this mod has misses it by
                    // construction and the strike is reported as complete. That blind spot is how a
                    // ship could order more radio-command rounds than it had weapon channels and
                    // have the surplus launch, lose guidance and self-destruct, with nothing said.
                    //
                    // A dead target is NOT such a case, and excluding it is the whole difficulty.
                    // Every round aimed at one ship retires the instant that ship sinks, so in any
                    // strike that overkills its target the trailing rounds retire far out through no
                    // fault of their own. Unity's == is wanted here rather than ReferenceEquals: a
                    // destroyed object compares equal to null, which is exactly the test.
                    bool targetGone = s.Target == null || s.Target.IsDestroyed;
                    if (s.Coordinated && s.LastDistM > HitRangeM && !targetGone
                        && _noArrivalWarned.Add(s.AmmoName))
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] no arrival: a coordinated {s.AmmoName} ended {s.LastDistM:0} m " +
                            $"from {s.TargetName} after {flightTime:0.0}s, with the target still afloat. " +
                            $"Shot down, decoyed, or guidance lost. Reported once per ammunition per " +
                            $"mission; VerboseLogging gives the per-round impact lines.");
                    // Two gates, deliberately, per the rule in Coordinator.TraceFlightModel:
                    // VerboseLog is coordination, TraceFlightModel is estimator internals. The
                    // `impact` line is the OUTCOME of a strike (did it land when it was told to),
                    // which is coordination, and it used to sit behind TraceFlightModel with the
                    // `gap` line. That hid the mod's headline measurement behind the one setting
                    // that performance runs require to be off, so a coordinated strike could be
                    // flown start to finish and report nothing about whether it worked.
                    if (s.Coordinated && (Coordinator.VerboseLog || Coordinator.TraceFlightModel))
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
                        // the sample, not the board, so it still prints after the target is gone.
                        //
                        // ONLY meaningful when the round actually arrived. LastSeenTime is when the
                        // TRACKER retired, and in a coordinated strike that is normally the moment the
                        // TARGET died under a different round, not the moment this one got there. Every
                        // round aimed at a ship therefore retires at the same instant, and printing a
                        // residual for all of them reported one number as though several rounds had each
                        // landed on time. Observed 2026-09-07: three yj-18a HIT at +1.3/+1.6/+2.7s and
                        // nine rounds that were still 8.6 to 30.2 km out claimed those same residuals.
                        // RoundPredictionScore has the identical flaw (its err is also measured against
                        // LastSeenTime), so it is suppressed on the same condition.
                        string residual = "";
                        if (hit)
                        {
                            if (s.PredictedImpact >= 0f)
                                residual = $", predicted {s.PredictedImpact:0.0}, residual {s.LastSeenTime - s.PredictedImpact:+0.0;-0.0}s";
                            residual += RoundPredictionScore(s);
                        }
                        else
                        {
                            string pred = s.PredictedImpact >= 0f ? $", predicted {s.PredictedImpact:0.0}" : "";
                            residual = $"{pred}, NO ARRIVAL: still {s.LastDistM:0} m out when the tracker " +
                                       "retired, so sim time above is not an arrival and there is no residual";
                        }
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] impact {s.AmmoName} -> {s.TargetName}: {outcome} at sim {s.LastSeenTime:0.0} " +
                            $"(flight {flightTime:0.0}s, final range {s.LastDistM:0} m){switched}{residual}");
                        // gap = actual flown time − the sim estimate captured at launch (positive =>
                        // the sim UNDER-predicts). Peak altitude and terminal speed say WHERE the gap
                        // comes from. HIT only. A RETARGETED round is excluded and reported as such:
                        // its flight is to the ship the seeker picked while simEst was computed for
                        // the ship it was ordered against, so its gap measures formation geometry,
                        // not the estimator.
                        if (retargeted && s.KinEstAtLaunch > 0f && hit && Coordinator.TraceFlightModel)
                        {
                            Bootstrap.Log.LogInfo(
                                $"[AutoTOT] gap {s.AmmoName} -> {s.TargetName}: SKIPPED, seeker switched to " +
                                $"{s.CurrentTargetName}. Flight {flightTime:0.0}s is to that ship; " +
                                $"simEst {s.KinEstAtLaunch:0.0}s was for the assigned one. Not estimator error.");
                            LogSubstituteGap(s, flightTime);
                        }
                        else if (s.KinEstAtLaunch > 0f && hit && Coordinator.TraceFlightModel)
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
