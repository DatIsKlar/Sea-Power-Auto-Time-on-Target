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
    internal static class LaunchDiagnostics
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

        // Per-ship shot-count accounting
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
            public float RegisteredSim;       // when the order was issued, the reference point for an
                                              // order that has not launched anything yet
            public float EnvelopeLead;        // predicted order-to-first-round transit into the launch
                                              // envelope (submarine ascent, aircraft descent); 0 = none
            public float StartupLead;         // full predicted order-to-first-round offset, launcher
                                              // startup plus EnvelopeLead
            public string LastEngageState;    // launcher state at the last sample, for the transition
                                              // trace between the order and its first round
            public float EngageStateSinceSim; // when the launcher entered LastEngageState
            public float LastEngageSampleSim = float.NegativeInfinity;
        }
        private static readonly List<LaunchExpectation> _launchExpectations = new List<LaunchExpectation>();
        // Last observed launch per (ship, ammo file), updated for EVERY sighting including rounds that
        // match no open order. A ship working through a queue of engage tasks is not stalled, and an
        // order sitting behind others in that queue must not be reported short while rounds are still
        // leaving. Keyed by instance id and ammo file rather than by ObjectBase, so a destroyed
        // shooter cannot keep the entry alive.
        private static readonly Dictionary<ShipAmmoKey, float> _shipLastLaunchSim =
            new Dictionary<ShipAmmoKey, float>();

        /// <summary>
        /// (firing ship, ammo file) identity for <see cref="_shipLastLaunchSim"/>. A struct rather
        /// than a concatenated string because FinalizeExpectations looks this up once per open order
        /// per tick, and a string key allocates on every one of those. Same pattern as
        /// <c>FlightTime.TofKey</c> and <c>LauncherFactsSource.FactsKey</c>. Keyed on the instance
        /// id, not the ObjectBase, so a destroyed shooter cannot keep the entry alive.
        /// </summary>
        private struct ShipAmmoKey : System.IEquatable<ShipAmmoKey>
        {
            public int UnitId;
            public string AmmoFile;
            public ShipAmmoKey(ObjectBase unit, string ammoFile)
            {
                UnitId = unit != null ? unit.GetInstanceID() : 0;
                AmmoFile = ammoFile;
            }
            public bool Equals(ShipAmmoKey o) => UnitId == o.UnitId && AmmoFile == o.AmmoFile;
            public override bool Equals(object obj) => obj is ShipAmmoKey k && Equals(k);
            public override int GetHashCode()
            { unchecked { return (UnitId * 397) ^ (AmmoFile?.GetHashCode() ?? 0); } }
        }
        // Missiles seen leaving that matched no open order. Non-zero means orders are being credited
        // to the wrong place, or expectations are being retired too early.
        internal static int UncreditedLaunches;
        private const float ExpectationMarginSim = 10f; // slack (sim s) beyond the computed ripple time
        private const float HitRangeM = 500f;           // closer than this when a missile vanishes => counted as a HIT

        internal static void Reset()
        {
            _flightTracker.Clear();
            _trackerScratch.Clear();
            _launchExpectations.Clear();
            _shipLastLaunchSim.Clear();
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
                bool verbose = Coordinator.VerboseLog;
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
                    if (Coordinator.VerboseLog && s.Coordinated)
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

        private const float EngageStateSampleSim = 0.25f; // sim-time cadence for the state trace

        /// <summary>
        /// Trace the launcher's own state machine for the whole life of an order, logging each
        /// transition, how long the previous state held, and the shot count at that moment.
        ///
        /// This exists because the measured order-to-first-round time on a guidance-requiring mount
        /// is a repeatable 2.7 s that no modelled term accounts for: PreLaunchDelay, the reaction
        /// draw and the whole OODA stack are all zero on those launchers. The states in
        /// WeaponSystemLauncher.Update each carry a real duration, and this says WHICH one holds,
        /// rather than inferring it from a total.
        ///
        /// It used to stop at the first round, which made the second half of a ripple invisible.
        /// That is where the Kynda problem lives: it fires a linked fore/aft pair, turns the ship to
        /// unmask again, then fires the rest, and the gap in the middle is a ship turn rather than
        /// anything the launcher is doing. Covering the whole order is what makes that legible.
        ///
        /// Volume is bounded by logging transitions only, so a launcher sitting in one state is
        /// silent no matter how long it sits. Verbose only.
        /// </summary>
        private static void SampleEngageStates(float simNow)
        {
            if (!Coordinator.VerboseLog || _launchExpectations.Count == 0) return;

            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Unit == null || e.Unit.IsDestroyed) continue;
                if (simNow - e.LastEngageSampleSim < EngageStateSampleSim) continue;
                e.LastEngageSampleSim = simNow;

                string state = SubmarineFacts.EngageStatesFor(e.Unit, e.AmmoId);
                if (state == e.LastEngageState) continue;

                if (e.LastEngageState != null)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] engage-state {e.AmmoId} from {SafeName(e.Unit)}: " +
                        $"{e.LastEngageState} held {simNow - e.EngageStateSinceSim:0.00}s " +
                        $"-> {state} at t+{simNow - e.RegisteredSim:0.00}s, " +
                        $"launched {e.Launched}/{e.Requested}{Unmasking(e)}");
                e.LastEngageState = state;
                e.EngageStateSinceSim = simNow;
            }
        }

        /// <summary>
        /// Training context for the engage-state line: where the target sits relative to the ship's
        /// head, and whether the ship is currently steering to fix that.
        ///
        /// This is what tells an arc problem apart from a launcher problem. A Kynda's SS-N-3 mounts
        /// bear only through FiringArcs=-105,-75|75,105, two 30-degree windows abeam, so the ship
        /// must hold the target within 15 degrees of a beam to shoot at all. Its 8 rounds go out as
        /// linked fore/aft pairs with a long turn in the middle, and without the relative bearing
        /// there is no way to tell from a log whether it is still turning, has swung past the
        /// window, or is waiting on something else.
        ///
        /// Relative bearing rather than a real arc test, deliberately: targetIsInFiringArc wants an
        /// intercept point that does not exist before launch, and a bearing the reader can check
        /// against the ammo's own FiringArcs is both cheaper and harder to get subtly wrong.
        /// </summary>
        private static string Unmasking(LaunchExpectation e)
        {
            ObjectBase u = e.Unit, t = e.Target;
            if (u == null || t == null || t.IsDestroyed) return "";
            Vector3 to = t.transform.position - u.transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) return "";
            // Signed about world up: negative to port, positive to starboard, so it reads directly
            // against the FiringArcs pairs in the ship's ini.
            float rel = Vector3.SignedAngle(u.transform.forward, to, Vector3.up);
            return $", target rel brg {rel:+0;-0} deg, aligning {u.Aligning}";
        }

        /// <summary>
        /// True while an order issued at this target still has rounds that have not been seen
        /// leaving. A launcher can take a long time between the order and its first round: a
        /// submerged boat ordered up from 400 ft took 111 s. During that window the target has a
        /// fired row, nothing queued and nothing in flight, which is exactly what the engagement
        /// board's grace prune treats as finished. Pruning there is wrong twice over: the round
        /// vanishes from the HUD while it is still coming, and it is stamped uncoordinated at first
        /// sighting, which silently drops every per-missile diagnostic for it.
        /// </summary>
        internal static bool HasPendingLaunch(ObjectBase target)
        {
            if (target == null) return false;
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Target == target && e.Launched < e.Requested) return true;
            }
            return false;
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
        // registered even as single shots ; their observed launches finalize the batch impact.
        internal static void RegisterExpectation(Coordinator.Intent it, Coordinator.Scheduled sched)
        {
            if (it.Unit == null || it.Target == null) return;
            int shots = Mathf.Max(1, it.Shots);
            bool isAnchor = sched != null && sched.IsAnchor;
            // A single shot cannot come up "short", so it is normally not worth an expectation. The
            // exception is a shot that must first climb or dive into its launch envelope: the
            // follower case is precisely where nothing corrects the envelope estimate, so it is the
            // case that most needs scoring, and it was silent because envelope-residual is emitted
            // from the anchor-tracking loop only.
            // Under VerboseLog every order is registered, so the order-to-first-round residual
            // below can be scored on ordinary launchers too. That is research instrumentation and
            // costs a list entry per order, so normal play keeps the original rule.
            if (shots <= 1 && !isAnchor && it.EnvelopeLead <= 0f && !Coordinator.VerboseLog) return;

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
                RegisteredSim = GameClock.SimNow(),
                DeadlineSim = GameClock.SimNow() + ripple + waveTail + ExpectationMarginSim,
                Linked = isAnchor ? sched : null,
                IniInterval = interval,
                WaveTailSim = waveTail,
                EnvelopeLead = it.EnvelopeLead,
                StartupLead = it.StartupLead,
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

            float launchStamp = GameClock.LaunchStamp(w);   // one reflection read, reused

            // Stamp SHIP activity first, and unconditionally. This round proves the launcher is still
            // working even if it belongs to no order we are tracking, which is what keeps a queued
            // order from being reported short.
            _shipLastLaunchSim[new ShipAmmoKey(platform, ammoFile)] = launchStamp;

            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Launched < e.Requested && e.Unit == platform && e.Target == tgt && e.AmmoFile == ammoFile)
                {
                    e.Launched++;
                    e.LastLaunchSim = launchStamp;
                    if (e.Launched == 1) ScoreEnvelopeLead(e, launchStamp);
                    // Feed the batch anchor's live impact prediction (observation anchoring).
                    if (e.Linked != null && e.Linked.IsAnchor && !e.Linked.RippleDone)
                        e.Linked.LaunchTimes.Add(launchStamp);
                    return;
                }
            }

            // No expectation matched. That is NORMAL for most rounds: expectations only exist for
            // anchors and multi-shot orders, so a salvo of single-shot orders has nothing to match
            // against and counting those made the figure meaningless (74 on a clean run).
            // Only a launch from a ship that DOES have an open order for this ammo, but at another
            // target, indicates real misattribution. That is the case worth counting.
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Launched < e.Requested && e.Unit == platform && e.AmmoFile == ammoFile)
                {
                    UncreditedLaunches++;
                    return;
                }
            }
        }

        /// <summary>
        /// Order-to-first-round residual for a FOLLOWER: what the launcher actually took against
        /// what was predicted at commit. Two flavours, one per model: <c>envelope-residual</c> when
        /// the platform had to climb or dive into its launch envelope, <c>startup-residual</c> for
        /// an ordinary mount, whose whole prediction is <see cref="Facts.StartupDelay"/>. Positive means the platform took longer than
        /// predicted. Anchors are scored in the anchor-tracking loop instead, which also has the
        /// per-step envelope-track trace; a follower is out of that loop the moment it releases, so
        /// this is the only place its model can be read against reality. It is also the case that
        /// matters most: an anchor's real launch rewrites the shared impact, a follower's does not,
        /// so a follower's envelope error lands directly on the arrival time.
        /// </summary>
        private static void ScoreEnvelopeLead(LaunchExpectation e, float launchStamp)
        {
            if (!Coordinator.VerboseLog) return;
            if (e.Linked != null && e.Linked.IsAnchor) return;   // the anchor path already logs it

            float observed = launchStamp - e.RegisteredSim;
            if (e.LastEngageState != null)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] engage-state {e.AmmoId} from {SafeName(e.Unit)}: " +
                    $"{e.LastEngageState} held {launchStamp - e.EngageStateSinceSim:0.00}s " +
                    $"-> round away at t+{observed:0.00}s");
            if (e.EnvelopeLead > 0f)
            {
                float hatch = LauncherFactsSource.HatchCycleSeconds(e.Unit, e.AmmoId);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-residual {e.AmmoId} from {SafeName(e.Unit)} (follower): " +
                    $"observed {observed:0.0}s, predicted {e.EnvelopeLead:0.0}s " +
                    $"(transit {e.EnvelopeLead - hatch:0.0}s + hatch {hatch:0.0}s), " +
                    $"residual {observed - e.EnvelopeLead:+0.0;-0.0}s");
                return;
            }

            // Ordinary launcher, no envelope transit. StartupDelay is PreLaunchDelay plus half the
            // reaction draw, and both are 0 on several mounts, yet the observed order-to-rail time
            // on those is seconds. It lands on a follower's arrival in full, so it is worth the same
            // scrutiny the envelope models got. Printed even when predicted is 0.0: a zero that logs
            // nothing cannot be told from a model that declined to run.
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] startup-residual {e.AmmoId} from {SafeName(e.Unit)} (follower): " +
                $"observed {observed:0.0}s, predicted {e.StartupLead:0.0}s " +
                $"(hatch cycle {LauncherFactsSource.HatchCycleSeconds(e.Unit, e.AmmoId):0.0}s), " +
                $"residual {observed - e.StartupLead:+0.0;-0.0}s");
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
                // pace ; a fixed deadline fired mid-ripple and logged false SHORTFALLs. Only a
                // true stall (no launch for max(4x measured cadence, 30s), plus any reload-wave
                // tail) counts as a shortfall now.
                if (e.Launched < e.Requested)
                {
                    float interval = e.IniInterval;
                    List<float> lt = e.Linked?.LaunchTimes;
                    if (lt != null && lt.Count >= 2)
                        interval = (lt[lt.Count - 1] - lt[0]) / (lt.Count - 1);
                    if (interval <= 0f) interval = LauncherFactsSource.FallbackShotInterval;

                    // Reference point: this order's own last launch if it has started, otherwise the
                    // SHIP's last launch of this ammo, otherwise when the order was issued. A ship
                    // still cycling its launcher is not stalled, and an order queued behind others is
                    // not short -- it has not had its turn. Before this, a one-shot order got 10s and
                    // no extension at all, so a ship handed several targets reported false shortfalls
                    // for the tail of its queue while the rounds were still going out.
                    float since = e.LastLaunchSim;
                    if (since < 0f)
                    {
                        if (!_shipLastLaunchSim.TryGetValue(new ShipAmmoKey(e.Unit, e.AmmoFile), out since))
                            since = e.RegisteredSim;
                        else if (since < e.RegisteredSim) since = e.RegisteredSim;
                    }
                    float adaptive = since
                    + Mathf.Max(Coordinator.StallCadenceMultiplier * interval, Coordinator.StallMinWindowSim)
                    + e.WaveTailSim;

                    // A shooter may be legitimately mid launch cycle or, for a submarine, still
                    // rising to its launch depth. Both routinely outlast the window above: an Oscar
                    // ordered up from 350ft needed ~70s of ascent and then over a minute opening 24
                    // hatches, and reported a SHORTFALL of 0/24 while doing exactly what it was told.
                    // Hold the deadline off while that is true, bounded absolutely from the order so
                    // a boat parked at a depth it will not leave still reports.
                    //
                    // This applies mid-ripple too, not only before the first round. A Kynda's gap
                    // between SS-N-3b pairs is far longer than StallMinWindowSim, so a launched > 0
                    // order was closed out at 2/8 and the remaining 6 rounds, already on their way,
                    // were never credited.
                    if (e.Unit != null && !e.Unit.IsDestroyed &&
                        simNow - e.RegisteredSim < Coordinator.NoLaunchMaxHoldSim &&
                        Coordinator.ShooterStillWorking(e.Unit, e.AmmoId))
                        adaptive = simNow + Mathf.Max(interval, Coordinator.StallMinWindowSim);

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
                string quiet = "never";
                if (_shipLastLaunchSim.TryGetValue(new ShipAmmoKey(e.Unit, e.AmmoFile), out float shipLast))
                    quiet = $"{simNow - shipLast:0.0}s ago";
                string detail =
                    $"launched {e.Launched}/{e.Requested}, ready {ready}, reserve {reserve}, inventory {inv}, " +
                    $"ship last fired this ammo {quiet}, targetGone {targetGone}, shooterGone {shooterGone}" +
                    // Unconditional, not VerboseLog-gated: SHORTFALL is the line users send in, and
                    // for a submarine shooter the depth state is the whole explanation.
                    (shooterGone ? "" : SubmarineFacts.Describe(e.Unit, e.AmmoId)) +
                    // Likewise for grouped ammo: "ready N, reserve 0" with rounds still aboard reads
                    // as a mod bug until you can see that the group those rounds would join is full.
                    (shooterGone ? "" : LauncherFactsSource.DescribeGroups(e.Unit, e.AmmoId));

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

            // Grounded-integrator per-phase breakdown ; pins WHICH loft phase (climb/cruise/descent)
            // the model gets wrong when its intercept time disagrees with the actual flown time.
            // peakAlt vs loftAlt = climb-height error; a slow VTerm + long DescentTime = descent error.
            // Hoisted out of the `if` so the integrator run can be timed. TryIntegratedPhaseDiag
            // calls IntegratedEndTimeCore directly rather than going through FlightTime.Estimate, so
            // without this the profiler misses it and the stage totals fall short of `model: loop`.
            bool phaseDiagOk = false;
            float intT = 0f;
            FlightTime.IntegratedPhases ph = default;
            if (w._launchPlatform != null && w.CurrentIntendedTargetObject != null)
            {
                CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                phaseDiagOk = FlightTime.TryIntegratedPhaseDiag(w._launchPlatform, ap._ammunitionFileName,
                    w.CurrentIntendedTargetObject, out intT, out ph);
                CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                CoordinatorProfiler.CountEstimate(cacheHit: false);   // always a fresh sim, never cached
            }
            if (phaseDiagOk)
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
        // The only direct ground truth for the boundaries the model derives from ini fields: where
        // the real missile leaves the loft is the model's finalDist, and where it enters
        // TerminalApproach is its diveStart. `track` prints the stage too, but samples every 15s,
        // which on a Mach-10 lofter brackets a transition to a ~43km window.
        //
        // Distances are reported three ways because `track` logs 3D SLANT and `sim-track` logs flat;
        // flat is the one comparable to the model's boundaries.
        //
        // Change-gated, so a full flight emits under a dozen lines. VerboseLog + Coordinated only.
        private static void MaybeLogStageChange(WeaponBase w, ObjectBase tgt, FlightSample s, float simNow)
        {
            if (!Coordinator.VerboseLog) return;
            WeaponBase.FlightStage now = w._flightStage;
            if (now == s.LastStage) return;
            WeaponBase.FlightStage prev = s.LastStage;
            s.LastStage = now;

            float altU = w.transform != null ? w.transform.position.y : 0f;
            float slantM = GameUnits.MetersBetween(w, tgt);
            float slantU = slantM / GameUnits.MetersPerUnity;
            float altDelta = altU - (tgt.transform != null ? tgt.transform.position.y : 0f);
            float flatU = GameMath.FlatFromSlant(slantU, altDelta);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] stage-obs {(w._ap != null ? w._ap._ammunitionFileName : "?")}#{w.GetInstanceID()}: " +
                $"{prev} -> {now} at t+{simNow - s.LaunchTime:0.0}s, " +
                $"flat {flatU:0}u ({flatU * GameUnits.MetersPerUnity / 1000f:0.0}km), " +
                $"slant {slantM / 1000f:0.0}km, alt {altU:0.0}u, spd {w._velocityInKnots:0}kn");
        }

        // Throttled per-missile telemetry: actual vs nominal speed, group/leader state, in-group
        // commanded speed, flight stage, altitude, distance. Separates group-drag (leader spd ~0.6x
        // nominal) from a solo estimate error. VerboseLog only. Updates the sample's throttle stamp.
        private static void MaybeLogTelemetry(WeaponBase w, ObjectBase tgt, FlightSample s, float distM, float simNow)
        {
            if (!Coordinator.VerboseLog) return;
            float interval = TelemetryCadence.IntervalFor(simNow - s.LaunchTime);
            if (s.LastTelemetrySim >= 0f && (simNow - s.LastTelemetrySim) < interval) return;
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
        }

        internal static string SafeName(ObjectBase o)
        {
            if (o == null) return "?";
            try { return o.getUIDAndName(); } catch { return "?"; }
        }
    }
}
