using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Observational diagnostics for coordinated launches: the flight tracker records each missile's outcome,
    /// and launch expectations tally requested against actual launches. The tracker feeds observation
    /// anchoring. See docs/ARCHITECTURE.md, "Launch diagnostics design".
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        // Per-missile record kept while a friendly missile is airborne, so its outcome can be reported after
        // the object is gone. A class, so the per-frame update mutates it in place.
        internal sealed class FlightSample
        {
            public float LaunchTime; public string AmmoName; public string TargetName;
            public float LastDistM; public float LastSeenTime;
            // Position and speed at LastSeenTime, dumped rounds only; the retire pass measures from here to the hit.
            public Vector3 LastSeenPosU; public float LastSeenSpeedU; public Vector3 LastSeenDirU;
            public ObjectBase Target;      // kept for residual lookup (predicted vs observed impact)
            // The target the seeker is on now, refreshed every tick. A round sent into a formation can lock
            // another ship, and the impact and gap lines must measure against the ship actually struck.
            public ObjectBase CurrentTarget;
            public string CurrentTargetName;
            public float PredictedImpact;  // stamped from the board, so a residual survives the target. -1 = none
            public float LastTelemetrySim; // throttle for the periodic `track` telemetry line
            // Estimates at first sighting, for the gap line. -1 = unavailable.
            public float KinEstAtLaunch;   // the active estimator (the integrator on beta)
            public float LegacyEstAtLaunch; // the game's own EstimateShot intercept time
            public float WpEstAtLaunch;    // the ported waypoint sim
            public float PeakSpeedKn;      // highest speed the live missile reached (kn)
            public float PeakAltU;         // highest altitude seen (Unity units)
            public float LastSpeedKn;      // most recent speed (kn), roughly the impact speed
            // Shooter state at launch, so a round that switched targets can be re-estimated against the ship
            // it hit. Zero position = not captured.
            public Vector3 LaunchPosU;
            public float LaunchVelKn;
            // The round's flat heading at first sighting, the rail it left on (zero = unresolved). A pair of
            // fixed launchers can swap canisters after the solve, so per-round estimates use this, not the pick.
            public Vector3 LaunchHeadingU;
            public ObjectBase Shooter;     // needed to re-run the estimate; always guard, it can die
            public string ShooterAmmoId;
            public bool Coordinated;       // at first sighting: AutoTOT fired it (its target has a board row)
            // Last value of the game's flight-stage machine, so `stage-obs` fires on each change.
            public WeaponBase.FlightStage LastStage;
            // Corpus pairing: DumpKey names the .solveinput this round was solved from; the fields below are
            // what a replay needs to test the model rather than the game's randomness.
            public string DumpKey;
            public string AmmoFingerprint;
            public float MotorPerformance = 1f;
            public float MinCompression = float.MaxValue;
            public float MaxCompression;
            // The physics step the round was actually integrated with (60 or 30 Hz by frame rate, larger
            // above 10x). A round flown at a steady step is reproducible; one whose step changed is not.
            public float MinFlownStep = float.MaxValue;
            public float MaxFlownStep;
            // When the step changed, as "t:step" per change (capped): an early change after mission load and
            // one at apex are different facts.
            public readonly List<string> StepChanges = new List<string>();
            public float LastStepSeen;
            // The delay terms the order was released on, so delay residuals can be scored across the corpus.
            // -1 = no matching expectation, normal for an uncoordinated round. The target's hull: the ini
            // length and the collider's world box, which with the heading gives beam and height. Read once at
            // first sighting.
            public float TargetLengthM = -1f;
            public Vector3 TargetBoundsM;
            public float RegisteredSim = -1f;
            public float StartupLead = -1f;
            public float EnvelopeLead = -1f;
            public float ReleaseLead = -1f;
            public float ImpactAtSim = -1f;
            public bool WasAnchor;
            // Grouped flight is not in the integrator (MissileGroup.cs:135 slows the leader while the
            // formation forms), so grouped rounds are recorded to be scored as their own cohort. Every stage
            // boundary the game crossed, as "Stage@t+12.3s", capped: it localises a disagreeing flight time
            // to a phase.
            public readonly List<string> StageChanges = new List<string>();
            // Altitude and speed against time, sampled coarsely, to tell an early level-off from a late climb.
            public readonly List<string> Track = new List<string>();
            public float NextTrackSample;
            // Target-relative flight evidence at the log's adaptive cadence; Track stays for the `climb` command.
            public readonly List<string> FlightTrack = new List<string>();
            public float NextFlightTrackSample;
            // Every term of Missile.SetMissileCourse's pre-apex guard from launch, sampled on a cadence and
            // on both sides of any change in its discrete state, so a short-lived guard is captured.
            public readonly List<string> GuardTrack = new List<string>();
            public float NextGuardSample;
            public GuardFrame GuardPrev;   // last tick's values, for the line before a change
            public GuardFrame GuardNext;   // the spare frame the next tick fills
            public bool GuardLastWritten;
            // One-second pools of the pitch rate the game's drag function reads, sampled per frame. See
            // MaybeRecordPitchRate.
            public readonly List<string> PitchRateTrack = new List<string>();
            public float PitchRateWindow;
            public int PitchRateN, PitchRateAtOrBelowZero;
            public float PitchRateMin, PitchRateMax;
            public double PitchRateSum, PitchRateSq, PitchRateLlf2, PitchRatePitch, PitchRateSpeedU;
            public long PitchRateTickFirst, PitchRateTickLast;
            // Detailed counterparts to StageChanges, plus group state transitions.
            public readonly List<string> StageEvents = new List<string>();
            // -1 until first sighting. Packs seeker-active and echo-present, which decide CurrentTarget and
            // so the altitude handed to the drag function.
            public int LastSeekerState = -1;
            public readonly List<string> GroupChanges = new List<string>();
            public bool LastGroupActive;
            public bool LastGroupLeader;
            // Target course and speed at launch and position at impact. The solver freezes target velocity at
            // launch, so a target that manoeuvred afterwards must be excluded from scoring.
            public Vector3 TargetPosAtLaunch;
            public float TargetCourseAtLaunch;
            public float TargetSpeedAtLaunch;
            public Vector3 TargetPosAtEnd;
            public float TargetCourseAtEnd;
            public float TargetSpeedAtEnd;
            // The speed a Vessel target was converging on when the capture was solved, and each change after.
            // The solve uses the speed the ship has now, which holds for the flight only if the ship keeps it.
            public bool HasSpeedCommand;
            public SpeedCommand SpeedCommandAtLaunch;
            public float LastWantedSeen, LastConvergeSeen;
            public readonly List<string> SpeedCommandChanges = new List<string>();
            // Preformatted launch-cell lines for the .result, read at first sighting.
            public string LaunchCellBlock;
            public bool InGroup;
            public bool GroupLeader;
            public int MaxGroupSize;
        }

        // The `track` cadence lives in TelemetryCadence, shared with the integrator's `sim-track`.
        private static readonly Dictionary<WeaponBase, FlightSample> _flightTracker =
            new Dictionary<WeaponBase, FlightSample>();

        private static readonly List<WeaponBase> _trackerScratch = new List<WeaponBase>();

        /// <summary>The tracker fields only the corpus writer reads, refreshed each tick for a dumped round.</summary>
        private static void UpdateCorpusTail(WeaponBase w, ObjectBase tgt, FlightSample s, float simNow)
        {
            // Group membership is assigned a few frames after launch (Missile.JoinOrCreateMissileGroup), so
            // it is kept as a flight-wide observation.
            CaptureGroupMembership(w, s);
            if (w.transform != null)
            {
                s.LastSeenPosU = w.transform.position;
                s.LastSeenSpeedU = w._velocityInUnity;
                s.LastSeenDirU = w.transform.forward;
            }
            // Compression band over the flight: above 10x the game enlarges its physics step, which the
            // corpus must know.
            float comp = GameTime.TimeCompression;
            if (comp < s.MinCompression) s.MinCompression = comp;
            if (comp > s.MaxCompression) s.MaxCompression = comp;
            float step = GameTime.fixedDeltaTime;
            if (step > 0f)
            {
                if (step < s.MinFlownStep) s.MinFlownStep = step;
                if (step > s.MaxFlownStep) s.MaxFlownStep = step;
                // Compared relatively, on the lab's steady-step tolerance, so float noise cannot register as a change.
                if (s.LastStepSeen <= 0f ||
                    Mathf.Abs(step - s.LastStepSeen) > StepChangeRelativeTolerance * step)
                {
                    if (s.StepChanges.Count < StepChangeCap)
                        s.StepChanges.Add($"{simNow - s.LaunchTime:0.000}:{step:0.0000}");
                    s.LastStepSeen = step;
                }
            }
            s.TargetPosAtEnd = tgt.transform != null ? tgt.transform.position : s.TargetPosAtEnd;
            s.TargetCourseAtEnd = tgt.getHeading();
            s.TargetSpeedAtEnd = tgt._velocityInKnots;
            NoteSpeedCommand(tgt, s, simNow);
        }

        /// <summary>
        /// Keep group state once the game assigns it: groups form after launch, so a first-sighting read
        /// alone misses rounds that join later.
        /// </summary>
        private static void CaptureGroupMembership(WeaponBase weapon, FlightSample sample)
        {
            Missile missile = weapon as Missile;
            if (missile == null || !missile._inMissileGroup) return;

            sample.InGroup = true;
            if (missile.GroupLeader) sample.GroupLeader = true;
        }

        internal static int LastWeaponCount;

        internal static int LastTrackedMissiles;

        internal static float LastScanLoopMs;

        internal static float LastFinalizeMs;

        internal static float LastCleanupMs;
        internal static float LastEngageStateMs;
        internal static float LastRetireMs;

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
        /// Launchers being traced, keyed by reference identity. The state machine belongs to the launcher, so
        /// orders sharing one read one trace. Cleared when the last order closes.
        /// </summary>
        private static readonly Dictionary<int, LauncherTrace> _launcherTraces =
            new Dictionary<int, LauncherTrace>();

        /// <summary>Launchers already sampled this tick, so a shared one logs once.</summary>
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
            _faultsReported.Clear();
            UncreditedLaunches = 0;
        }

        // Ammunition already reported for a no-arrival, so the warning is once per ammunition per mission.
        private static readonly HashSet<string> _noArrivalWarned = new HashSet<string>();

        /// <summary>
        /// Each tick: open a tracker the first time a friendly missile is seen, refresh it, report the
        /// outcome of each missile that vanished, and close out launch expectations whose window has passed.
        /// </summary>
        internal static void Tick(float simNow)
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            ScanTrackedMissiles(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastScanLoopMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            LastTrackedMissiles = _flightTracker.Count;

            // Timed as sub-phases: engage-state sampling reflects per launcher per order, and retiring can
            // write corpus files.
            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            SampleEngageStates(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastEngageStateMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            FinalizeExpectations(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastFinalizeMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            CollectDeadTrackers();
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastCleanupMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            RetireDeadTrackers();
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastRetireMs = (float)(_subSw.Elapsed.TotalMilliseconds); }
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

                // Each round on its own guard, so a failed diagnostic read cannot skip the launch credit or
                // the rounds after it. See RecoverFromScanFault.
                bool creditAttempted = false;
                try
                {
                    float distM = GameUnits.MetersBetween(w, tgt);
                    // The per-missile flight-model block (two extra flight sims and a waypoint sim per round,
                    // for the gap comparison) runs under TraceFlightModel only.
                    bool trace = Coordinator.TraceFlightModel;
                    if (_flightTracker.TryGetValue(w, out FlightSample existing))
                    {
                        existing.LastDistM = distM;
                        existing.LastSeenTime = simNow;
                        // Fields only the corpus writer reads, so only dumped rounds pay for them.
                        if (existing.DumpKey != null) UpdateCorpusTail(w, tgt, existing, simNow);
                        // Every 5 s of flight, capped.
                        if (trace && simNow >= existing.NextTrackSample && existing.Track.Count < 150)
                        {
                            existing.NextTrackSample = simNow + TrackSampleIntervalSim;
                            float tAlt = w.transform != null ? w.transform.position.y : 0f;
                            existing.Track.Add(
                                $"{simNow - existing.LaunchTime:0.#}:{tAlt:0.#}:{w._velocityInKnots:0}");
                        }
                        if (trace && existing.Coordinated)
                        {
                            MaybeRecordFlightTrack(w, tgt, existing, simNow);
                            MaybeRecordSeekerEvent(w, tgt, existing, simNow);
                            MaybeRecordGroupChange(w, existing, simNow);
                            RecordGuardTrack(w, existing, simNow);
                            MaybeRecordPitchRate(w, existing, simNow);
                        }
                        if (!ReferenceEquals(existing.CurrentTarget, tgt))
                        {
                            existing.CurrentTarget = tgt;
                            existing.CurrentTargetName = UnitNaming.SafeName(tgt);
                        }
                        if (trace)
                        {
                            float altU = w.transform != null ? w.transform.position.y : 0f;
                            if (altU > existing.PeakAltU) existing.PeakAltU = altU;
                            existing.LastSpeedKn = w._velocityInKnots;
                            if (w._velocityInKnots > existing.PeakSpeedKn) existing.PeakSpeedKn = w._velocityInKnots;
                        }
                        // Refresh the stamped prediction while the board row lives; keep the last value once
                        // it is gone.
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
                        // Only missiles AutoTOT fired (their target has a board row) get per-missile
                        // diagnostics; escorts' defensive SAMs do not.
                        bool coordinated = EngagementBoard.IsCoordinated(tgt);
                        // The state this round left the rail in: its own position, the launcher cell it
                        // spawned in rather than the ship's centre, and its own heading, the rail it used. A
                        // pair of fixed launchers can hand a round the other canister after the solve, so
                        // this differs from the solve's input in heading alone.
                        Vector3 roundHeading;
                        if (w.transform == null || !GameMath.TryFlatDirection(w.transform.forward, out roundHeading))
                            roundHeading = Vector3.zero;
                        FlightTime.LaunchState launchState = w._launchPlatform != null
                            ? new FlightTime.LaunchState(w.transform != null ? w.transform.position
                                                                             : w._launchPlatform.transform.position,
                                                         w._launchPlatform._velocityInKnots, roundHeading)
                            : default;
                        // The model's flight time for this shot at launch, for the impact line's gap.
                        // Uncached, since it is a constant of the launch. Falls back to the live estimate
                        // where the integrator declines (always on public). Trace only: a sim per new missile
                        // would be wasted in normal play.
                        float est = -1f;
                        float legacyEst = -1f;
                        if (trace && coordinated && w._launchPlatform != null && w._ap != null)
                        {
                            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                            est = FlightTime.EstimateFromLaunch(w._launchPlatform, w._ap._ammunitionFileName, tgt,
                                                                launchState);
                            bool cacheHit = false;
                            if (est <= FlightTime.MinValidSeconds)
                            {
                                est = FlightTime.Estimate(w._launchPlatform, w._ap._ammunitionFileName, tgt);
                                cacheHit = FlightTime.WasLastCallCacheHit;
                            }
                            CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                            CoordinatorProfiler.CountEstimate(cacheHit);
                            // The game's own EstimateShot intercept time, printed beside the integrator's on
                            // the gap line.
                            legacyEst = FlightTime.MaxRangePreciseEndTime(w._launchPlatform, w._ap, tgt);
                        }
                        // The ported waypoint sim's estimate, printed beside the others on the gap line.
                        float wpEst = -1f;
                        if (trace && coordinated && WaypointSim.Ready && WaypointSim.FullReady &&
                            w._launchPlatform != null && w._ap != null)
                            wpEst = WaypointSim.EndTime(w._launchPlatform, w._ap, tgt, emitDiag: true);
                        string dumpKey = (Coordinator.TraceFlightModel && coordinated && w._ap != null &&
                                          w._launchPlatform != null)
                            ? FlightTime.DumpInputFor(w._launchPlatform, w._ap._ammunitionFileName, tgt, launchState)
                            : null;
                        var fresh = new FlightSample
                        {
                            LaunchTime = GameClock.LaunchStampOr(w, simNow),
                            AmmoName = (w._ap != null ? w._ap._ammunitionFileName : "?"),
                            TargetName = UnitNaming.SafeName(tgt),
                            LastDistM = distM,
                            LastSeenTime = simNow,
                            Target = tgt,
                            CurrentTarget = tgt,
                            CurrentTargetName = UnitNaming.SafeName(tgt),
                            PredictedImpact = pred0,   // -1 if this target isn't (yet) coordinated
                            LastTelemetrySim = -1f,
                            KinEstAtLaunch = est,
                            LegacyEstAtLaunch = legacyEst,
                            WpEstAtLaunch = wpEst,
                            PeakAltU = trace && w.transform != null ? w.transform.position.y : 0f,
                            LastSpeedKn = trace ? w._velocityInKnots : 0f,
                            PeakSpeedKn = trace ? w._velocityInKnots : 0f,
                            LaunchPosU = w.transform != null ? w.transform.position
                                       : w._launchPlatform != null ? w._launchPlatform.transform.position
                                       : Vector3.zero,
                            LaunchVelKn = w._launchPlatform != null ? w._launchPlatform._velocityInKnots : 0f,
                            LaunchHeadingU = roundHeading,
                            Shooter = w._launchPlatform,
                            ShooterAmmoId = (w._ap != null ? w._ap._ammunitionFileName : null),
                            Coordinated = coordinated,
                            LastStage = w._flightStage,
                            DumpKey = dumpKey,
                            // 48 formatted fields and a hash, read only by the corpus writer.
                            AmmoFingerprint = dumpKey != null && w._ap != null
                                ? SolveInputDump.AmmunitionFingerprint(w._ap) : string.Empty,
                            // Read only by the corpus writer, so skipped without a dump key: a reflection
                            // read and a collider search per missile, and a renamed game field must not reach
                            // normal play.
                            MotorPerformance = dumpKey != null ? MotorPerformanceOf(w) : 1f,
                            TargetLengthM = dumpKey != null ? TargetLengthOf(tgt) : -1f,
                            TargetBoundsM = dumpKey != null ? TargetBoundsOf(tgt) : Vector3.zero,
                            TargetPosAtLaunch = tgt.transform != null ? tgt.transform.position : Vector3.zero,
                            TargetCourseAtLaunch = tgt.getHeading(),
                            TargetSpeedAtLaunch = tgt._velocityInKnots,
                            TargetPosAtEnd = tgt.transform != null ? tgt.transform.position : Vector3.zero,
                            TargetCourseAtEnd = tgt.getHeading(),
                            TargetSpeedAtEnd = tgt._velocityInKnots,
                            InGroup = InGroupNow(w),
                            GroupLeader = IsGroupLeader(w),
                            MaxGroupSize = w._ap != null ? w._ap._maxGroupSize : 0,
                        };
                        Missile freshMissile = w as Missile;
                        fresh.LastGroupActive = freshMissile != null && freshMissile._inMissileGroup;
                        fresh.LastGroupLeader = freshMissile != null && freshMissile.GroupLeader;
                        // Groups normally form after this first pass; later passes update the sticky flags.
                        CaptureGroupMembership(w, fresh);
                        if (dumpKey != null)
                            fresh.GroupChanges.Add(
                                $"{simNow - fresh.LaunchTime:0.000}:{(fresh.LastGroupActive ? 1 : 0)}:" +
                                $"{(fresh.LastGroupLeader ? 1 : 0)}:" +
                                $"{(freshMissile != null ? freshMissile._inGroupVelocityInKnots : -1f):0.000}");
                        if (trace && coordinated)
                            MaybeRecordFlightTrack(w, tgt, fresh, simNow);
                        fresh.MinCompression = GameTime.TimeCompression;
                        fresh.MaxCompression = GameTime.TimeCompression;
                        // First sighting means the round just left the rail: credit it to the matching order,
                        // whatever its coordination. Marked before the call, so a credit that throws is never
                        // tried twice.
                        creditAttempted = true;
                        CreditLaunch(w, tgt);
                        if (coordinated)
                        {
                            LogTrackInit(w, est, launchState);
                            MaybeLogTelemetry(w, tgt, fresh, distM, simNow);
                        }
                        // Read here, not in the initializer, because the expectation lookup is a scan run
                        // once per round.
                        if (dumpKey != null && w._ap != null &&
                            TryGetDelayTerms(w._launchPlatform, w._ap._ammunitionFileName, tgt,
                                             out float regSim, out float startLead, out float envLead,
                                             out float relLead, out float impactAt, out bool wasAnchor))
                        {
                            fresh.RegisteredSim = regSim;
                            fresh.StartupLead = startLead;
                            fresh.EnvelopeLead = envLead;
                            fresh.ReleaseLead = relLead;
                            fresh.ImpactAtSim = impactAt;
                            fresh.WasAnchor = wasAnchor;
                        }
                        // Read at the instant DumpInputFor solved the capture above.
                        if (dumpKey != null) NoteSpeedCommand(tgt, fresh, simNow);
                        if (dumpKey != null) fresh.LaunchCellBlock = DescribeLaunchCell(w, tgt);
                        _flightTracker[w] = fresh;
                    }
                }
                catch (System.Exception e)
                {
                    RecoverFromScanFault(w, tgt, simNow, creditAttempted, e);
                }
            }
        }

        /// <summary>
        /// A round's scan threw part way. The fault is reported once, and a round seen for the first time is
        /// still tracked and credited exactly once, with only the fields the outcome line and in-flight
        /// checks read and no corpus key, so a renamed game field cannot stop launch crediting.
        /// </summary>
        private static void RecoverFromScanFault(WeaponBase w, ObjectBase tgt, float simNow,
                                                 bool creditAttempted, System.Exception e)
        {
            ReportFault("missile scan", e);
            // Already tracked: a later refresh failed, and the launch was credited when first seen.
            if (_flightTracker.ContainsKey(w)) return;
            EngagementBoard.TryGetPredictedImpact(tgt, out float pred);
            string ammo = w._ap != null ? w._ap._ammunitionFileName : null;
            // Stored before the credit, so a credit that throws cannot bring the round back as a
            // first sighting on the next tick.
            _flightTracker[w] = new FlightSample
            {
                LaunchTime = GameClock.LaunchStampOr(w, simNow),
                AmmoName = ammo ?? "?",
                TargetName = UnitNaming.SafeName(tgt),
                LastDistM = GameUnits.MetersBetween(w, tgt),
                LastSeenTime = simNow,
                Target = tgt,
                CurrentTarget = tgt,
                CurrentTargetName = UnitNaming.SafeName(tgt),
                PredictedImpact = pred,
                LastTelemetrySim = -1f,
                KinEstAtLaunch = -1f,
                LegacyEstAtLaunch = -1f,
                WpEstAtLaunch = -1f,
                Shooter = w._launchPlatform,
                ShooterAmmoId = ammo,
                Coordinated = EngagementBoard.IsCoordinated(tgt),
                LastStage = w._flightStage,
            };
            if (!creditAttempted) CreditLaunch(w, tgt);
        }

        // Faults already reported this mission, keyed on stage and message.
        private static readonly HashSet<string> _faultsReported = new HashSet<string>();

        /// <summary>
        /// Log a launch-tracking fault once per stage and message per mission, with its stack. Unconditional,
        /// like SHORTFALL: it is the line a user sends in after a game update breaks a read.
        /// </summary>
        internal static void ReportFault(string stage, System.Exception e)
        {
            if (e == null || !_faultsReported.Add(stage + "|" + e.GetType().Name + "|" + e.Message)) return;
            Bootstrap.Log.LogError(
                $"[AutoTOT] launch tracking fault in {stage}, reported once per mission. Launches are " +
                $"still counted and strikes still release; some diagnostics for this round are missing. " +
                $"A game update has probably changed a field the mod reads, please send this log.\n{e}");
        }

        // ObjectBaseParameters._length is a field on public and on stable up to s366, and a property from
        // s367. A direct read would fault the calling method at JIT on a build without the field, so both
        // forms are resolved by reflection.
        private static readonly System.Reflection.FieldInfo TargetLengthField =
            typeof(ObjectBaseParameters).GetField("_length",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.PropertyInfo TargetLengthProperty =
            typeof(ObjectBaseParameters).GetProperty("_length",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        /// <summary>The target's declared hull length in metres, the ini's figure, or -1 when there is none.</summary>
        private static float TargetLengthOf(ObjectBase tgt)
        {
            try
            {
                ObjectBaseParameters obp = tgt != null ? tgt._obp : null;
                if (obp == null) return -1f;
                object v = TargetLengthField != null ? TargetLengthField.GetValue(obp)
                         : TargetLengthProperty != null ? TargetLengthProperty.GetValue(obp, null)
                         : null;
                return v is float f ? f : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>
        /// The target's collider box in metres, which the game's arrival tests against
        /// (Vessel.OnTriggerEnter). World-axis aligned, so read it with the target's heading. Zero when no
        /// collider is visible.
        /// </summary>
        private static Vector3 TargetBoundsOf(ObjectBase tgt)
        {
            try
            {
                if (tgt == null) return Vector3.zero;
                Collider c = tgt.GetComponentInChildren<Collider>();
                return c != null ? c.bounds.size * GameUnits.MetersPerUnity : Vector3.zero;
            }
            catch { return Vector3.zero; }
        }

        /// <summary>Flight-time seconds between samples of the recorded altitude/speed track.</summary>
        private const float TrackSampleIntervalSim = 5f;

        /// <summary>A physics step counts as changed beyond this fraction of itself; the lab uses the same
        /// tolerance.</summary>
        private const float StepChangeRelativeTolerance = 0.01f;

        /// <summary>Most step changes one round records; the frame rate can hunt across the 30/40 fps band.</summary>
        private const int StepChangeCap = 24;

        /// <summary>A wanted or capped speed counts as changed beyond this many knots.</summary>
        private const float SpeedCommandChangeKn = 0.01f;

        /// <summary>Most speed-command entries, and log lines, one round records.</summary>
        private const int SpeedCommandChangeCap = 16;

        /// <summary>
        /// Record a Vessel target's speed command when first read, the instant
        /// <see cref="FlightTime.DumpInputFor"/> solved the capture, then each change beyond
        /// <see cref="SpeedCommandChangeKn"/>. Each entry is "t:wanted:converge:speed", t from launch.
        /// </summary>
        private static void NoteSpeedCommand(ObjectBase tgt, FlightSample s, float simNow)
        {
            if (!TryReadSpeedCommand(tgt, out SpeedCommand c)) return;
            bool first = !s.HasSpeedCommand;
            if (!first && Mathf.Abs(c.Wanted - s.LastWantedSeen) <= SpeedCommandChangeKn &&
                Mathf.Abs(c.Converge - s.LastConvergeSeen) <= SpeedCommandChangeKn)
                return;
            if (first)
            {
                s.HasSpeedCommand = true;
                s.SpeedCommandAtLaunch = c;
            }
            s.LastWantedSeen = c.Wanted;
            s.LastConvergeSeen = c.Converge;
            if (s.SpeedCommandChanges.Count >= SpeedCommandChangeCap) return;
            float t = simNow - s.LaunchTime;
            s.SpeedCommandChanges.Add($"{t:0.000}:{c.Wanted:0.000}:{c.Converge:0.000}:{c.Speed:0.000}");
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] tgt-speed-command {UnitNaming.SafeName(tgt)} {(first ? "at launch" : "CHANGED")} " +
                $"of {s.AmmoName} t+{t:0.0}s: speed {c.Speed:0.000}kn wanted {c.Wanted:0.000}kn " +
                $"converge {c.Converge:0.000}kn rudder {c.Rudder:0.00}deg sea {c.SeaState} " +
                $"damping/tick {c.DampingPerTick:0.000000} step {GameTime.fixedDeltaTime:0.0000}");
        }

        /// <summary>
        /// The per-round thrust multiplier the game rolled at spawn (Missile.cs:62), or 1 when unreadable, so
        /// a replay reproduces the flight instead of averaging over the roll.
        /// </summary>
        private static float MotorPerformanceOf(WeaponBase w)
        {
            var missile = w as Missile;
            return missile != null ? missile._motorPerformance : 1f;
        }

        /// <summary>
        /// Whether this round is flying in a missile group. Read at first sighting, before the formation has
        /// assembled, so it reports the intent to group.
        /// </summary>
        private static bool InGroupNow(WeaponBase w)
        {
            var missile = w as Missile;
            return missile != null && !ReferenceEquals(missile._missileGroup, null);
        }

        /// <inheritdoc cref="InGroupNow"/>
        private static bool IsGroupLeader(WeaponBase w)
        {
            var missile = w as Missile;
            return missile != null && missile.GroupLeader;
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
        /// Floor on the mean speed a reconstructed substitute geometry may imply, as a fraction of the
        /// round's observed peak. Leaves room for a genuine dogleg while catching a broken reconstruction.
        /// </summary>
        private const float SubGapMinMeanSpeedFraction = 0.4f;

        /// <summary>
        /// Ceiling on the same quantity. A round cannot average more than its peak; the slack above 1.0
        /// covers the peak being a per-tick sample and the launch position a snapshot.
        /// </summary>
        private const float SubGapMaxMeanSpeedFraction = 1.1f;

        /// <summary>
        /// The estimator gap for a round whose seeker switched, measured against the ship it actually hit, as
        /// a separately named line (`gap-sub`) so it is never taken for a reference measurement. Against a
        /// formation most rounds switch, so without it a formation strike reports no model error at all.
        ///
        /// The substitute's position at launch was never recorded, so it is back-projected along its velocity
        /// from the impact; the line prints how far it rewound and the tolerance that implies. A
        /// reconstruction needing a mean speed outside <see cref="SubGapMinMeanSpeedFraction"/> to
        /// <see cref="SubGapMaxMeanSpeedFraction"/> of the round's peak prints as IMPLAUSIBLE, with no gap.
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
                // Rewind the substitute along its own velocity to where it was at launch. Straight-line,
                // since a curve fitted to one velocity sample would be false precision.
                Vector3 subVel = sub._velocityVecInUnity;
                Vector3 subAtLaunch = sub.transform.position - subVel * flightTime;
                float rewoundM = (sub.transform.position - subAtLaunch).magnitude
                                 * GameUnits.MetersPerUnity;

                // The range the estimate is asked to solve, and the mean speed the round would have needed;
                // both printed.
                float subRangeM = (subAtLaunch - s.LaunchPosU).magnitude * GameUnits.MetersPerUnity;
                float meanSpeedKn = subRangeM / flightTime / GameUnits.KnotsToMs;

                // Plausibility checked before the sim runs, against the round's measured peak speed.
                if (s.PeakSpeedKn > 1f
                    && (meanSpeedKn > s.PeakSpeedKn * SubGapMaxMeanSpeedFraction
                        || meanSpeedKn < s.PeakSpeedKn * SubGapMinMeanSpeedFraction))
                {
                    // The same range to the assigned ship, from the same launch position: if both are
                    // implausibly short the launch position is stale; if only the substitute's is, the target
                    // position read here is.
                    bool haveAssigned = s.Target != null && !s.Target.IsDestroyed;
                    float assignedKm = haveAssigned
                        ? (s.Target.transform.position - s.LaunchPosU).magnitude
                          * GameUnits.MetersPerUnity / 1000f
                        : 0f;
                    string assigned = haveAssigned
                        ? $"{assignedKm:0.0}km"
                        : "unavailable, assigned target gone";
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] gap-sub {s.AmmoName} -> {s.CurrentTargetName} " +
                        $"(seeker switched from {s.TargetName}): NO GAP, the reconstructed geometry " +
                        $"is not believable. Range {subRangeM / 1000f:0.0}km over {flightTime:0.0}s " +
                        $"needs a mean {meanSpeedKn:0}kn, and this round peaked at {s.PeakSpeedKn:0}kn " +
                        $"| same launch position to the assigned {s.TargetName}: {assigned}, " +
                        $"back-projected {rewoundM / 1000f:0.0}km");
                    return;
                }

                var launch = new FlightTime.LaunchState(s.LaunchPosU, s.LaunchVelKn,
                                                        s.LaunchHeadingU, subAtLaunch);
                float est = FlightTime.EstimateFromLaunch(s.Shooter, s.ShooterAmmoId, sub, launch);
                if (est <= FlightTime.MinValidSeconds)
                {
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] gap-sub {s.AmmoName} -> {s.CurrentTargetName}: no estimate, the " +
                        "integrator declined for the substitute's launch geometry.");
                    return;
                }
                // Tolerance: the rewind distance in seconds at the speed this round averaged.
                float meanSpeedMs = meanSpeedKn * GameUnits.KnotsToMs;
                string tol = meanSpeedMs > 1f
                    ? $"about {rewoundM / meanSpeedMs:0.#}s"
                    : "unknown";
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] gap-sub {s.AmmoName} -> {s.CurrentTargetName} " +
                    $"(seeker switched from {s.TargetName}): est {est:0.0}s, actual {flightTime:0.0}s, " +
                    $"gap {flightTime - est:+0.0;-0.0}s | APPROXIMATE: range {subRangeM / 1000f:0.0}km " +
                    $"(mean {meanSpeedKn:0}kn, peak {s.PeakSpeedKn:0}kn), the substitute's launch " +
                    $"position was back-projected {rewoundM / 1000f:0.0}km, tolerance {tol}");
            }
            catch (System.Exception e)
            {
                ModLog.VerboseWarn("substitute gap failed", e);
            }
        }

        private static readonly RaycastHit[] _impactHits = new RaycastHit[16];

        /// <summary>
        /// The collision time of a round that has just vanished, from its last sighting. The flight ends in
        /// WeaponBase.CheckCollision, a raycast along each tick's step (mask -131073) credited to the hit
        /// root's ObjectBase. Repeating that cast from the last-sighted position along the last-sighted nose,
        /// no further than the round could fly by <paramref name="latestAge"/>, finds the hit; the distance
        /// over the last speed is the time. The round's transform is useless: it returns to PoolManager on
        /// the collision tick. -1 when the cast meets no collider of the target.
        /// </summary>
        private static float ImpactFromLastSighting(FlightSample s, float lastSeenAge, float latestAge)
        {
            try
            {
                ObjectBase target = s.CurrentTarget != null ? s.CurrentTarget : s.Target;
                float reach = (latestAge - lastSeenAge) * s.LastSeenSpeedU;
                if (target == null || reach <= 0f || s.LastSeenDirU.sqrMagnitude < 0.5f) return -1f;
                int n = Physics.RaycastNonAlloc(s.LastSeenPosU, s.LastSeenDirU.normalized, _impactHits,
                                                reach, -131073);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Collider c = _impactHits[i].collider;
                    if (c == null || c.gameObject.layer == 17 || c.name == "col_missile_proximity") continue;
                    if (c.transform.root.GetComponentInChildren<ObjectBase>() != target) continue;
                    if (_impactHits[i].distance < best) best = _impactHits[i].distance;
                }
                return best < float.MaxValue ? lastSeenAge + best / s.LastSeenSpeedU : -1f;
            }
            catch (System.Exception)
            {
                return -1f;
            }
        }

        /// <summary>Drop the gathered trackers, logging each round's impact and gap on the way out.</summary>
        private static void RetireDeadTrackers()
        {
            for (int i = 0; i < _trackerScratch.Count; i++)
            {
                WeaponBase w = _trackerScratch[i];
                if (_flightTracker.TryGetValue(w, out FlightSample s))
                {
                    float flightTime = s.LastSeenTime - s.LaunchTime;

                    // flightTime ends at the last frame the round was seen, so the collision lies between
                    // that frame and this one. See ImpactFromLastSighting.
                    const float ImpactToleranceSec = 0.02f;
                    float flightEndGone = GameClock.SimNow() - s.LaunchTime;
                    float flightEndImpact = s.DumpKey != null
                        ? ImpactFromLastSighting(s, flightTime, flightEndGone + ImpactToleranceSec)
                        : -1f;
                    // The impact, gap and residual lines report the hit when the cast found it, else the last
                    // sighting. The capture keeps both; only dumped rounds carry the hit.
                    float flightMeasured = flightEndImpact >= 0f ? flightEndImpact : flightTime;
                    string measuredTo = flightEndImpact >= 0f ? " to the hit" : "";

                    // Outside the VerboseLog/TraceFlightModel gate: a round that ends without arriving is
                    // caught by neither SHORTFALL nor the impact line, so without this a wasted round goes
                    // unreported. A dead target is excluded, since every round aimed at a sunk ship retires
                    // far out through no fault of its own; Unity's == is wanted, because a destroyed object
                    // equals null.
                    bool targetGone = s.Target == null || s.Target.IsDestroyed;
                    if (s.Coordinated && s.LastDistM > HitRangeM && !targetGone
                        && _noArrivalWarned.Add(s.AmmoName))
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] no arrival: a coordinated {s.AmmoName} ended {s.LastDistM:0} m " +
                            $"from {s.TargetName} after {flightTime:0.0}s, with the target still afloat. " +
                            $"Shot down, decoyed, or guidance lost. Reported once per ammunition per " +
                            $"mission; VerboseLogging gives the per-round impact lines.");
                    // VerboseLog or TraceFlightModel: the impact line is a strike's outcome, so it must not
                    // hide behind the estimator-internals gate that performance runs keep off.
                    if (s.Coordinated && (Coordinator.VerboseLog || Coordinator.TraceFlightModel))
                    {
                        // Keyed off the predicate, not the display string.
                        bool hit = s.LastDistM <= HitRangeM;
                        // ARRIVED, not HIT: see HitRangeM.
                        string outcome = hit ? "ARRIVED" : "ended";
                        // Did the seeker end on a different ship? Then LastDistM is to the substitute.
                        // ReferenceEquals, because Unity's == makes a destroyed substitute equal null and
                        // would hide the switch.
                        bool retargeted = !ReferenceEquals(s.CurrentTarget, s.Target)
                                       && !ReferenceEquals(s.CurrentTarget, null)
                                       && !ReferenceEquals(s.Target, null);
                        string switched = retargeted ? $" [RETARGETED -> {s.CurrentTargetName}]" : "";
                        // Residual = observed impact - predicted impact, from the sample so it prints after
                        // the target is gone. Arrivals only: a round retired because its target sank under
                        // another round never got there.
                        string residual = "";
                        if (hit)
                        {
                            if (s.PredictedImpact >= 0f)
                                residual = $", predicted {s.PredictedImpact:0.0}, residual {s.LaunchTime + flightMeasured - s.PredictedImpact:+0.0;-0.0}s";
                        }
                        else
                        {
                            string pred = s.PredictedImpact >= 0f ? $", predicted {s.PredictedImpact:0.0}" : "";
                            residual = $"{pred}, NO ARRIVAL: still {s.LastDistM:0} m out when the tracker " +
                                       "retired, so sim time above is not an arrival and there is no residual";
                        }
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] impact {s.AmmoName} -> {s.TargetName}: {outcome} at sim {s.LaunchTime + flightMeasured:0.0} " +
                            $"(flight {flightMeasured:0.0}s{measuredTo}, final range {s.LastDistM:0} m){switched}{residual}");
                        // One corpus entry per dumped round, arrival or not: the lab needs a no-arrival to exclude it.
                        if (!string.IsNullOrEmpty(s.DumpKey))
                            SolveInputDump.WriteResult(
                                s.DumpKey, s.AmmoName, s.AmmoFingerprint, s.TargetName,
                                flightTime, hit, s.LastDistM,
                                retargeted, s.MotorPerformance,
                                s.MinCompression == float.MaxValue ? 1f : s.MinCompression,
                                s.MaxCompression,
                                s.MinFlownStep == float.MaxValue ? 0f : s.MinFlownStep,
                                s.MaxFlownStep, s.KinEstAtLaunch, s.LaunchTime,
                                s.InGroup, s.GroupLeader, s.MaxGroupSize,
                                s.PeakSpeedKn, s.PeakAltU, s.LastSpeedKn, s.Track,
                                s.FlightTrack, s.GuardTrack, s.PitchRateTrack,
                                s.WpEstAtLaunch, s.LegacyEstAtLaunch,
                                s.StageChanges, s.StageEvents, s.GroupChanges, s.StepChanges,
                                s.RegisteredSim, s.StartupLead, s.EnvelopeLead, s.ReleaseLead,
                                s.ImpactAtSim, s.WasAnchor, s.TargetLengthM, s.TargetBoundsM,
                                s.TargetCourseAtLaunch, s.TargetSpeedAtLaunch,
                                s.TargetCourseAtEnd, s.TargetSpeedAtEnd,
                                (s.TargetPosAtEnd - s.TargetPosAtLaunch).magnitude,
                                s.HasSpeedCommand, s.SpeedCommandAtLaunch, s.SpeedCommandChanges,
                                s.LaunchCellBlock, flightEndGone, flightEndImpact);

                        // gap = actual flight - the estimate at launch (positive: the sim under-predicts),
                        // with peak altitude and terminal speed to say where. Arrivals only; a retargeted
                        // round's gap measures formation geometry, so it is reported separately.
                        if (retargeted && s.KinEstAtLaunch > 0f && hit && Coordinator.TraceFlightModel)
                        {
                            Bootstrap.Log.LogInfo(
                                $"[AutoTOT] gap {s.AmmoName} -> {s.TargetName}: SKIPPED, seeker switched to " +
                                $"{s.CurrentTargetName}. Flight {flightMeasured:0.0}s is to that ship; " +
                                $"simEst {s.KinEstAtLaunch:0.0}s was for the assigned one. Not estimator error.");
                            LogSubstituteGap(s, flightMeasured);
                        }
                        else if (s.KinEstAtLaunch > 0f && hit && Coordinator.TraceFlightModel)
                        {
                            string legacy = s.LegacyEstAtLaunch > 0f
                                ? $", legacyEst {s.LegacyEstAtLaunch:0.0}s (gap {flightMeasured - s.LegacyEstAtLaunch:+0.0;-0.0}s)"
                                : "";
                            string wp = s.WpEstAtLaunch > 0f
                                ? $", wpEst {s.WpEstAtLaunch:0.0}s (gap {flightMeasured - s.WpEstAtLaunch:+0.0;-0.0}s)"
                                : "";
                            Bootstrap.Log.LogInfo(
                                $"[AutoTOT] gap {s.AmmoName} -> {s.TargetName}: simEst {s.KinEstAtLaunch:0.0}s, " +
                                $"actual {flightMeasured:0.0}s{measuredTo}, gap {flightMeasured - s.KinEstAtLaunch:+0.0;-0.0}s, " +
                                $"peakAlt {s.PeakAltU:0}u, realPeakSpd {s.PeakSpeedKn:0}kn, termSpd {s.LastSpeedKn:0}kn{wp}{legacy}");
                        }
                    }
#if LIVETEST
                    // The unattended test runner records every retired round, outcome or not.
                    LiveTestRunner.OnRoundRetired(s, flightMeasured);
#endif
                }
                _flightTracker.Remove(w);
            }
        }
    }
}
