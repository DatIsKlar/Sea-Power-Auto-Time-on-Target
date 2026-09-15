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
        internal sealed class FlightSample
        {
            public float LaunchTime; public string AmmoName; public string TargetName;
            public float LastDistM; public float LastSeenTime;
            // Where the round was, and how fast, at LastSeenTime; dumped rounds only. The retire pass
            // measures from here to the hit point the game leaves in a destroyed round's transform.
            public Vector3 LastSeenPosU; public float LastSeenSpeedU; public Vector3 LastSeenDirU;
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
            // The round's own flat heading at first sighting, which is the rail it left on; zero =
            // not resolvable. The solve-time pick can name the other canister of a pair of fixed
            // launchers, so this, not the pick, is what every per-round estimate is taken from.
            public Vector3 LaunchHeadingU;
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
            // Corpus pairing. DumpKey names the .solveinput this round was solved from, so the
            // outcome can be filed against it; the rest is what a replay needs in order to be a
            // fair test of the model rather than of the game's randomness.
            public string DumpKey;
            public string AmmoFingerprint;
            public float MotorPerformance = 1f;
            public float MinCompression = float.MaxValue;
            public float MaxCompression;
            // The physics step the round was ACTUALLY integrated with, which is the quantity that
            // matters rather than the compression that caused it: the game adapts it between 60 Hz
            // and 30 Hz on frame rate, and enlarges it above 10x compression. A round flown at a
            // steady step is reproducible whatever that step was; one whose step moved mid-flight
            // is not.
            public float MinFlownStep = float.MaxValue;
            public float MaxFlownStep;
            // WHEN the step moved, not merely that it did. A step change is the largest single
            // reason the corpus discards a round (59 of 94 exclusions on 2026-09-12), and the
            // range alone cannot tell a change that landed in the first second of flight, while
            // the frame rate was still settling after a mission load, from one that landed at
            // apex under real load. Those are a different fact about the round. "t:step" per
            // change, capped because a frame rate hunting across the 30/40 fps hysteresis band
            // could otherwise write a line per sample.
            public readonly List<string> StepChanges = new List<string>();
            public float LastStepSeen;
            // Chain audit B1: the delay terms the order was released on. Every one of them lived
            // only in LaunchExpectation and in log text that BepInEx overwrites per launch, so no
            // delay residual survived a session and none could be scored across the corpus the way
            // a flight time can. -1 means the round matched no expectation, which is normal for an
            // uncoordinated round.
            // E5A, the arrival radius. The model stops at CloseEnoughDistU, 3u from the target's
            // CENTRE, while the game's arrival is its trigger collider, so a bow-on sea-skimmer
            // ends 165 to 315 m out against a 349 m tanker and a diver that reaches the deck ends
            // 36 m out. Testing a hull-entry arrival needs the hull, and nothing recorded it.
            // Length is the ini's own figure; the bounds are the collider's world box, which with
            // the target's heading gives beam and height too. Read once at first sighting.
            public float TargetLengthM = -1f;
            public Vector3 TargetBoundsM;
            public float RegisteredSim = -1f;
            public float StartupLead = -1f;
            public float EnvelopeLead = -1f;
            public float ReleaseLead = -1f;
            public float ImpactAtSim = -1f;
            public bool WasAnchor;
            // Grouped flight is NOT in the integrator. MissileGroup.cs:135 cuts the leader's speed
            // by up to 40% while the formation forms and holds followers around it, and a leader
            // cruises at _groupLeaderAltUnity. Our only handling is GroupFormingDelay, which shifts
            // the RELEASE time and never touches the estimate, so a grouped round's gap still
            // carries the whole unmodelled effect. Recorded so those rounds can be scored as their
            // own cohort instead of dragging the solo mean around.
            // Every stage boundary the GAME crossed, as "Stage@t+12.3s". The single most
            // localising thing a round can tell us: a flight time that disagrees says only that
            // something is wrong, while a boost that ended two seconds late says where. Capped, so
            // a round that oscillates between stages cannot grow this without bound.
            public readonly List<string> StageChanges = new List<string>();
            // The round's own altitude and speed against time, sampled coarsely. Peak altitude
            // alone cannot tell a round that was still climbing when it had to nose over from one
            // that levelled off early, and those are different defects with different fixes.
            public readonly List<string> Track = new List<string>();
            public float NextTrackSample;
            // Complete target-relative flight evidence at the same adaptive cadence as the log.
            // Track remains for the existing `climb` command; FlightTrack is the extensible series
            // used when a model discrepancy needs more than altitude and speed to explain it.
            public readonly List<string> FlightTrack = new List<string>();
            public float NextFlightTrackSample;
            // Every term of Missile.SetMissileCourse's pre-apex guard, from launch on, with the
            // full desired and future vectors and the waypoint's own progress. Sampled at a cadence
            // and again on both sides of any change in its discrete state, so a guard that fires
            // for three ticks is captured rather than stepped over. It replaced an older recorder
            // that covered t+35..65 only, and carries every field that one had.
            public readonly List<string> GuardTrack = new List<string>();
            public float NextGuardSample;
            public GuardFrame GuardPrev;   // last tick's values, for the line before a change
            public GuardFrame GuardNext;   // the spare frame the next tick fills
            public bool GuardLastWritten;
            // One-second pools of the pitch rate the game's drag function reads, sampled once a
            // frame. See MaybeRecordPitchRate.
            public readonly List<string> PitchRateTrack = new List<string>();
            public float PitchRateWindow;
            public int PitchRateN, PitchRateAtOrBelowZero;
            public float PitchRateMin, PitchRateMax;
            public double PitchRateSum, PitchRateSq, PitchRateLlf2, PitchRatePitch, PitchRateSpeedU;
            public long PitchRateTickFirst, PitchRateTickLast;
            // Detailed counterparts to the compact, backwards-compatible StageChanges field, plus
            // the group state transitions that otherwise existed only in the rotating log.
            public readonly List<string> StageEvents = new List<string>();
            // -1 until the first sighting. Packs seeker-active and echo-present, the two bits that
            // decide CurrentTarget and therefore which altitude the game's drag function is given.
            public int LastSeekerState = -1;
            public readonly List<string> GroupChanges = new List<string>();
            public bool LastGroupActive;
            public bool LastGroupLeader;
            // Where the target was going at launch, and where it actually was at impact. The
            // solver freezes target velocity at launch, so a target that manoeuvred afterwards is
            // unpredictable in principle rather than mispredicted, and has to be excluded. Until
            // now that was a judgement the user had to make by hand.
            public Vector3 TargetPosAtLaunch;
            public float TargetCourseAtLaunch;
            public float TargetSpeedAtLaunch;
            public Vector3 TargetPosAtEnd;
            public float TargetCourseAtEnd;
            public float TargetSpeedAtEnd;
            // D3 of max-solo-error-below-1s: the speed a Vessel target was converging on at the
            // instant the capture was solved, and every change after it. The solve rounds the speed
            // the ship has NOW, which is right for the flight only if the ship keeps it.
            public bool HasSpeedCommand;
            public TargetMotionProbe.SpeedCommand SpeedCommandAtLaunch;
            public float LastWantedSeen, LastConvergeSeen;
            public readonly List<string> SpeedCommandChanges = new List<string>();
            // D-L: preformatted launch-cell lines for the .result, read at first sighting.
            public string LaunchCellBlock;
            public bool InGroup;
            public bool GroupLeader;
            public int MaxGroupSize;
        }

        // Sampling cadence for the `track` trace lives in TelemetryCadence, shared with the
        // integrator's `sim-track`, because the two are read against each other sample for sample.
        private static readonly Dictionary<WeaponBase, FlightSample> _flightTracker =
            new Dictionary<WeaponBase, FlightSample>();

        private static readonly List<WeaponBase> _trackerScratch = new List<WeaponBase>();

        /// <summary>The tracker fields only the corpus writer reads, refreshed each tick for a dumped round.</summary>
        private static void UpdateCorpusTail(WeaponBase w, ObjectBase tgt, FlightSample s, float simNow)
        {
            // Group membership is assigned by Missile.JoinOrCreateMissileGroup a few frames after
            // the round leaves the rail, so the first sighting often sees a valid grouped round as
            // solo. Kept as a flight-wide observation rather than a launch-time snapshot.
            CaptureGroupMembership(w, s);
            if (w.transform != null)
            {
                s.LastSeenPosU = w.transform.position;
                s.LastSeenSpeedU = w._velocityInUnity;
                s.LastSeenDirU = w.transform.forward;
            }
            // Compression band over the whole flight. Above 10x the game enlarges its own physics
            // step, so the round flies a different trajectory and the flight time stops being
            // comparable with a solve; the corpus needs to know that happened.
            float comp = GameTime.TimeCompression;
            if (comp < s.MinCompression) s.MinCompression = comp;
            if (comp > s.MaxCompression) s.MaxCompression = comp;
            float step = GameTime.fixedDeltaTime;
            if (step > 0f)
            {
                if (step < s.MinFlownStep) s.MinFlownStep = step;
                if (step > s.MaxFlownStep) s.MaxFlownStep = step;
                // Compared relatively, on the same tolerance the lab uses to call a step steady,
                // so float noise in the game's own 0.0166667 cannot register as a change.
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
        /// Preserve group state once the game assigns it.  Missile groups are formed asynchronously
        /// after launch, so reading <c>_inMissileGroup</c> only when a tracker is opened loses the
        /// label on rounds whose first sighting happens before formation completes.
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
            _faultsReported.Clear();
            UncreditedLaunches = 0;
            TargetMotionProbe.Reset();
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

            TargetMotionProbe.Tick(simNow);

            if (Coordinator.ProfilingEnabled) _subSw.Restart();
            ScanTrackedMissiles(simNow);
            if (Coordinator.ProfilingEnabled) { _subSw.Stop(); LastScanLoopMs = (float)(_subSw.Elapsed.TotalMilliseconds); }

            LastTrackedMissiles = _flightTracker.Count;

            // Timed like the rest: engage-state sampling does a reflection call per launcher per
            // open order, and retiring a tracker can write corpus files and run a re-estimate. Both
            // used to sit inside the Diag stage with no sub-phase, so the printed phases did not add
            // up to the stage and nothing said where the rest had gone.
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

                // Each round on its own guard. Most of this pass is diagnostics, and the launch credit
                // sits after the first-sighting capture, so one failed read used to skip the credit and
                // stop the loop for every round after it. See RecoverFromScanFault.
                bool creditAttempted = false;
                try
                {
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
                        // Fields only the corpus writer reads (SolveInputDump.WriteResult), which runs
                        // only for a round dumped at first sighting. They used to be updated for every
                        // friendly round on every tick, a game heading read included, with both
                        // diagnostic gates off.
                        if (existing.DumpKey != null) UpdateCorpusTail(w, tgt, existing, simNow);
                        // Every 5s of flight, capped: enough to see the shape of a climb without
                        // turning a 700s flight into a wall of text.
                        if (verbose && simNow >= existing.NextTrackSample && existing.Track.Count < 150)
                        {
                            existing.NextTrackSample = simNow + TrackSampleIntervalSim;
                            float tAlt = w.transform != null ? w.transform.position.y : 0f;
                            existing.Track.Add(
                                $"{simNow - existing.LaunchTime:0.#}:{tAlt:0.#}:{w._velocityInKnots:0}");
                        }
                        if (verbose && existing.Coordinated)
                        {
                            TargetMotionProbe.Watch(tgt, simNow);
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
                        // The state this round left the rail in: the ROUND'S own position, which is the
                        // launcher cell it spawned in (not the ship's centre, 37 to 48 m away on a VLS ship),
                        // and its own heading, which is the rail it actually used. A ship with two fixed
                        // launchers for one ammunition can hand a round the other canister after the
                        // solve, and the live pick then names a rail the round never flew: 180 deg out on
                        // a Spruance Harpoon, worth +5.3 s of false gap. This differs from the live
                        // solve's input in that heading alone, and only for a fixed rail
                        // (TryBuildSolveInput). See docs/plans/done/2026-09-13-launcher-pick-by-arc.md.
                        Vector3 roundHeading;
                        if (w.transform == null || !GameMath.TryFlatDirection(w.transform.forward, out roundHeading))
                            roundHeading = Vector3.zero;
                        FlightTime.LaunchState launchState = w._launchPlatform != null
                            ? new FlightTime.LaunchState(w.transform != null ? w.transform.position
                                                                             : w._launchPlatform.transform.position,
                                                         w._launchPlatform._velocityInKnots, roundHeading)
                            : default;
                        // The model's flight time for this shot as it left, stamped so the impact line can
                        // print the sim-vs-actual gap. Uncached: it is a constant of this launch, and the
                        // TTL cache is keyed on the live shooter. Falls back to the live estimate where
                        // the integrator declines, which is always the case on the public branch.
                        // Verbose-only: it feeds the verbose `gap` line, and a kinematic sim per new missile
                        // would be wasted work (and a spike under big salvos) in normal play.
                        float est = -1f;
                        float legacyEst = -1f;
                        if (verbose && coordinated && w._launchPlatform != null && w._ap != null)
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
                            PeakAltU = verbose && w.transform != null ? w.transform.position.y : 0f,
                            LastSpeedKn = verbose ? w._velocityInKnots : 0f,
                            PeakSpeedKn = verbose ? w._velocityInKnots : 0f,
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
                            MotorPerformance = MotorPerformanceOf(w),
                            TargetLengthM = TargetLengthOf(tgt),
                            TargetBoundsM = TargetBoundsOf(tgt),
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
                        // The group is normally created after this first tracker pass; subsequent
                        // passes update these sticky flags through CaptureGroupMembership above.
                        CaptureGroupMembership(w, fresh);
                        if (dumpKey != null)
                            fresh.GroupChanges.Add(
                                $"{simNow - fresh.LaunchTime:0.000}:{(fresh.LastGroupActive ? 1 : 0)}:" +
                                $"{(fresh.LastGroupLeader ? 1 : 0)}:" +
                                $"{(freshMissile != null ? freshMissile._inGroupVelocityInKnots : -1f):0.000}");
                        if (verbose && coordinated)
                            MaybeRecordFlightTrack(w, tgt, fresh, simNow);
                        fresh.MinCompression = GameTime.TimeCompression;
                        fresh.MaxCompression = GameTime.TimeCompression;
                        // First sighting of this missile => it just left the rail. Credit it to the
                        // matching pending order (this branch fires exactly once per WeaponBase, so no
                        // double count). Credited regardless of coordination (a no-op if nothing matches).
                        // Marked before the call, so a credit that throws part way is never tried twice.
                        creditAttempted = true;
                        CreditLaunch(w, tgt);
                        if (coordinated)
                        {
                            LogTrackInit(w, est, launchState);
                            MaybeLogTelemetry(w, tgt, fresh, distM, simNow);
                        }
                        // Chain audit B1. Read here rather than in the initializer because the
                        // expectation lookup is a scan and this is the one place per round it runs.
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
                        // D3: read at the instant DumpInputFor solved the capture above.
                        if (dumpKey != null) NoteSpeedCommand(tgt, fresh, simNow);
                        if (dumpKey != null) fresh.LaunchCellBlock = GuidanceStateProbe.DescribeLaunchCell(w, tgt);
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
        /// A round's scan threw part way. Most of that pass is diagnostics, and a game update that
        /// removes a field one of them reads used to take the launch count down with it: on stable
        /// s367 (2026-09-15) <see cref="TargetLengthOf"/> faulted before <see cref="CreditLaunch"/>
        /// was reached, so an anchor read "launched 0/11" while all eleven flew, and its follower was
        /// held until the target sank. So the fault is reported once, and a round seen for the first
        /// time is still tracked and credited exactly once. Its record carries only what the outcome
        /// line and the in-flight checks read, and no corpus key, so nothing half-built is written.
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
        /// Logs a launch-tracking fault once per stage and message per mission, stack included.
        /// Unconditional, like SHORTFALL: it is the line a user sends in when a game update has
        /// broken a read, and the strike itself carries on without the diagnostics that failed.
        /// </summary>
        internal static void ReportFault(string stage, System.Exception e)
        {
            if (e == null || !_faultsReported.Add(stage + "|" + e.GetType().Name + "|" + e.Message)) return;
            Bootstrap.Log.LogError(
                $"[AutoTOT] launch tracking fault in {stage}, reported once per mission. Launches are " +
                $"still counted and strikes still release; some diagnostics for this round are missing. " +
                $"A game update has probably changed a field the mod reads, please send this log.\n{e}");
        }

        // ObjectBaseParameters._length is a public field on public s358 and on stable up to s366, and
        // a property from stable s367 (2026-09-15), where it forwards to the simple parameters object.
        // A direct read compiles to a field reference, and Mono faults the calling method on a build
        // without the field before any try inside it can run. So both forms are resolved once here.
        private static readonly System.Reflection.FieldInfo TargetLengthField =
            typeof(ObjectBaseParameters).GetField("_length",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.PropertyInfo TargetLengthProperty =
            typeof(ObjectBaseParameters).GetProperty("_length",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        /// <summary>
        /// The target's declared hull length in metres, or -1 when the game does not give one. Read
        /// through the parameters object rather than a collider so it is the ini's own figure.
        /// </summary>
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
        /// The target's collider box in metres, which is what the game's arrival actually tests
        /// against (<c>Vessel.OnTriggerEnter</c>). World-axis aligned, so it is only a hull box when
        /// read together with the target's heading, which the outcome already records. Zero when
        /// the target carries no collider we can see.
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

        /// <summary>
        /// A physics step counts as changed beyond this fraction of itself. The same tolerance the
        /// offline lab uses to call a step steady, so the two agree on what a change is.
        /// </summary>
        private const float StepChangeRelativeTolerance = 0.01f;

        /// <summary>Most step changes one round records. The frame rate can hunt across the game's
        /// 30/40 fps hysteresis band, which would otherwise write a line per sample.</summary>
        private const int StepChangeCap = 24;

        /// <summary>D3: a wanted or capped speed counts as changed beyond this many knots. Neither
        /// moves while a ship holds one telegraph on a straight course; only its own speed does.</summary>
        private const float SpeedCommandChangeKn = 0.01f;

        /// <summary>Most speed-command entries, and log lines, one round records.</summary>
        private const int SpeedCommandChangeCap = 16;

        /// <summary>
        /// D3 of docs/plans/open/max-solo-error-below-1s.md: record a Vessel target's speed command
        /// the first time it is read, which is the instant <see cref="FlightTime.DumpInputFor"/>
        /// solved the capture, then each change of more than <see cref="SpeedCommandChangeKn"/>.
        /// Each entry is "t:wanted:converge:speed", t from launch.
        /// </summary>
        private static void NoteSpeedCommand(ObjectBase tgt, FlightSample s, float simNow)
        {
            if (!TargetMotionProbe.TryReadSpeedCommand(tgt, out TargetMotionProbe.SpeedCommand c)) return;
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
        /// The per-round thrust multiplier the game rolled at spawn (Missile.cs:62), or 1 when the
        /// round is not a missile or the field is unreadable. Recorded so a replay can reproduce
        /// the flight it is being scored against instead of averaging over the roll.
        /// </summary>
        private static float MotorPerformanceOf(WeaponBase w)
        {
            var missile = w as Missile;
            return missile != null ? missile._motorPerformance : 1f;
        }

        /// <summary>
        /// Is this round flying as part of a missile group right now? Read at first sighting, which
        /// is before the formation has finished assembling, so it reports the round's INTENT to
        /// group rather than a completed formation.
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
        /// Floor on the mean speed a reconstructed substitute geometry may imply, as a fraction of
        /// the round's own observed peak. A lofted round that decelerates into a subsonic terminal
        /// run still averages roughly three quarters of its peak over the straight line, so 0.4
        /// leaves room for a genuine dogleg of well over twice the direct distance before the line
        /// goes quiet, while still catching the 19% case that started this.
        /// </summary>
        private const float SubGapMinMeanSpeedFraction = 0.4f;

        /// <summary>Ceiling on the same quantity. A round cannot average more than its own peak, so
        /// the physical limit is 1.0, and the slack above it is for the measurement rather than the
        /// physics: the peak is a per-tick sample and the launch position is a snapshot, and a
        /// flat-flying supersonic round already averages within a few percent of its peak, which is
        /// too close to a hard 1.0 to spend a good line on.</summary>
        private const float SubGapMaxMeanSpeedFraction = 1.1f;

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
        ///
        /// <para><b>Why the reconstruction is checked before it is believed.</b> Two long-range
        /// rounds once reported gaps of +270 s and +321 s here, on a strike whose strict gap was
        /// -1.9 s. The integrator was fine: the geometry handed to it was not, because the range it
        /// solved worked out at 56 km on a shot the launch snapshot recorded as 270 km. The old line
        /// printed only the rewind distance, so that read as an ordinary "tolerance about 15.5s" and
        /// the defect was only visible by working the arithmetic backwards from the printed
        /// tolerance. The range and the implied mean speed are printed now, and a reconstruction
        /// that would need the round to fly slower or faster than it was seen flying prints as
        /// IMPLAUSIBLE with no gap number at all, since a wrong gap here is worse than none.</para>
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

                // The range the estimate is actually asked to solve, and the mean speed the round
                // would have needed to cover it in the time it flew. Both printed: this is the
                // quantity that was wrong the one time this line lied, and neither was on it.
                float subRangeM = (subAtLaunch - s.LaunchPosU).magnitude * GameUnits.MetersPerUnity;
                float meanSpeedKn = subRangeM / flightTime / GameUnits.KnotsToMs;

                // Plausibility ceiling, checked before the sim runs so a broken geometry costs
                // nothing. The round's own peak speed is the reference because it is measured, not
                // modelled: whatever the reconstruction claims, a missile cannot average more than
                // the fastest it was ever seen going, and one that averaged a small fraction of it
                // over a straight line would have had to fly several times the direct distance.
                if (s.PeakSpeedKn > 1f
                    && (meanSpeedKn > s.PeakSpeedKn * SubGapMaxMeanSpeedFraction
                        || meanSpeedKn < s.PeakSpeedKn * SubGapMinMeanSpeedFraction))
                {
                    // The same range to the ASSIGNED ship, measured from the same stored launch
                    // position, so one line separates the two candidates without another test run:
                    // if both ranges are implausibly short then LaunchPosU is what went stale, and
                    // if only the substitute's is short then it is the target position read here.
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
                // Tolerance: the rewind distance is the part of the geometry we had to reconstruct,
                // so express it in seconds at the speed this round actually averaged.
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
        /// The collision time of a round that has just vanished, measured from its last sighting.
        /// Against a ship the flight ends in WeaponBase.CheckCollision, a raycast along each tick's
        /// step with mask -131073 (everything but layer 17), and ResolveCollisionCandidate credits the
        /// hit to transform.root.GetComponentInChildren&lt;ObjectBase&gt;(). The same cast, from the
        /// last-sighted position along the last-sighted nose and only as far as the round could have
        /// flown by <paramref name="latestAge"/>, finds where it met its target; the distance over the
        /// last speed is the time. The round's own transform is no use here: WeaponBase.destroyObject
        /// hands a missile back to PoolManager on the collision tick. Returns -1 when the cast meets no
        /// collider of the target.
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

        /// <summary>
        /// Drop the gathered trackers, logging each round's impact and its actual-minus-estimate gap on
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

                    // flightTime ends at the last frame the round was seen, so the collision lies
                    // between that and this frame, the first one it was missed. See
                    // ImpactFromLastSighting for the collision itself.
                    const float ImpactToleranceSec = 0.02f;
                    float flightEndGone = GameClock.SimNow() - s.LaunchTime;
                    float flightEndImpact = s.DumpKey != null
                        ? ImpactFromLastSighting(s, flightTime, flightEndGone + ImpactToleranceSec)
                        : -1f;
                    // What the impact, gap and residual lines report: the hit when the cast found
                    // it, else the last sighting. The capture keeps both (ActualFlight stays the
                    // last sighting, FlightEndImpact the hit). Only dumped rounds carry the hit.
                    float flightMeasured = flightEndImpact >= 0f ? flightEndImpact : flightTime;
                    string measuredTo = flightEndImpact >= 0f ? " to the hit" : "";

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
                        // re-test this by comparing the display string back to the label.
                        bool hit = s.LastDistM <= HitRangeM;
                        // ARRIVED, not HIT: see HitRangeM. Everything downstream still keys off the
                        // predicate rather than off this string.
                        string outcome = hit ? "ARRIVED" : "ended";
                        // Did the seeker end up on a different ship than the one this round was
                        // assigned? If so LastDistM is measured to the SUBSTITUTE, and ARRIVED means it
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
                        // One corpus entry per round: the input it was solved from, and what the
                        // round then did. Written whenever the round was dumped, arrival or not,
                        // because a no-arrival is a fact the lab needs in order to exclude it.
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
                    // The unattended test runner records every retired round, outcome or not.
                    LiveTestRunner.OnRoundRetired(s, flightMeasured);
                }
                _flightTracker.Remove(w);
            }
        }
    }
}
