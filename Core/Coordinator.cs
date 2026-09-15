using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The coordination pipeline: batches missile orders, estimates each shooter's flight time and
    /// releases each launch so the salvo lands together. The impact time is fixed at commit and refined
    /// by observing the anchor's launches. See docs/ARCHITECTURE.md.
    /// </summary>
    internal static partial class Coordinator
    {
        // Tunables (wired to config in Bootstrap.LoadConfig).
        internal static bool Enabled = true;   // master switch (config)

        internal static bool Active = true;    // runtime toggle (hotkey / on-screen button)

        /// <summary>
        /// Flight-model trace: how a flight time was computed, where <see cref="VerboseLog"/> covers what
        /// the coordinator decided. It is most of a traced log, and it runs two extra flight sims per round.
        /// </summary>
        internal static bool TraceFlightModel = false;

        // Also the config defaults in Bootstrap.LoadConfig; config overwrites these at load.
        internal const float DefaultDebounceSeconds = 0.75f;

        internal const float DefaultMaxWindowSeconds = 6.0f;

        internal static float DebounceSeconds = DefaultDebounceSeconds; // real time with no new orders => batch is complete

        internal static float MaxWindowSeconds = DefaultMaxWindowSeconds; // hard cap on how long a batch stays open

        internal static bool VerboseLog = false;

        /// <summary>Timing instrumentation, off by default. See <see cref="CoordinatorProfiler"/>.</summary>
        internal static bool ProfilingEnabled
        {
            get => CoordinatorProfiler.Enabled;
            set { CoordinatorProfiler.Enabled = value; ModelStats.Enabled = value; }
        }

        private static int _flightEstimatesThisFrame; // fresh sims run this frame (reset each ReleaseDueLaunches)

        // Timing constants for scheduling and observation anchoring.
        private const float LookaheadFraction = 0.5f;      // release lookahead, as a fraction of one sim step

        internal const float StallCadenceMultiplier = 4f;  // no launch for this many measured-cadence intervals => stall

        internal const float StallMinWindowSim = 30f;      // ...but never shorter than this (sim seconds)

        private const float NoLaunchStallSim = 120f;       // anchor fired but nothing launched for this long => stall

        // ...unless the shooter is visibly still working toward launch: its launcher is mid cycle, or a
        // submarine is still rising to the launch ceiling. This ceiling still ends a wedged shooter.
        internal const float NoLaunchMaxHoldSim = 300f;

        private const float AscentProgressFeet = 3f;       // depth must drop by this much between samples

        // Grace between a boat clearing its launch ceiling and its launcher visibly cycling: the depth block
        // clears at once, while the launcher takes a few sim seconds and is read through a real-time cache.
        // It does not rescue a boat that never clears its ceiling.
        private const float PostUnblockGraceSim = 30f;

        // Sampled on a sim cadence, not per frame: a boat rising 2-4 ft/s moves too little per frame to read
        // as progress.
        private const float DepthSampleIntervalSim = 5f;

        internal const int PlannerTaskPriority = 1000;     // task priority for planner-issued orders

        private const float NegligibleLeadSeconds = 0.1f;  // release leads below this aren't worth logging

        private const float SlackWarnSeconds = 5f;         // overshoot beyond this at release => WARN (stagger-loss guard)

        private const float FlightRefreshSim = 2f;         // max sim-time staleness of a reused far-from-release flight estimate

        private const float FlightRefreshNearSim = 0.3f;   // faster refresh cadence once an item is near its release gate

        private const float FlightGateMargin = 3f;         // within this much slack of release => treat as near-release

        private const int MaxFlightEstimatesPerFrame = 12; // per-frame ceiling on fresh kinematic sims (bounds worst-frame cost)

        private const float EnvelopeRefreshSim = 2f;      // sim-time cadence for re-reading a held order's launch envelope

        private const float EnvelopeDriftLogSeconds = 1f; // envelope change worth a verbose line

        /// <summary>
        /// One held player missile order, fired whole as one InsertEngageTask(shotsToFire = N), as the
        /// game's own UI does.
        /// </summary>
        internal sealed class Intent
        {
            public ObjectBase Unit;
            public string AmmoId;
            public ObjectBase Target;
            public int Shots;
            public int Priority;
            public bool IsFormation;
            // d(arrival)/d(release) = 1 - closingSpeed / effectiveWeaponSpeed: about 1 for a ship, near 0 for
            // an aircraft closing at its weapon's speed. See Holdability.
            public float Holdability = 1f;
            // Holdability says this order cannot be timed and it is not the anchor: release it when ready.
            public bool FireImmediately;
            public float ReleaseLead; // released this far before impact: half the ripple span, all of it when grouped
            public bool Pooled;       // shares its ship's launch clock, so ReleaseLead is the pool's lead
            public string PoolClock;  // SystemName of that clock; null when not pooled
            public float PoolDelta;   // the pool's longest commit flight minus this order's
            public bool EnvelopeTracked; // launch envelope can change while held; EnvelopeLead is refreshed
            // Launch-envelope part of StartupLead (submarine ascent, aircraft descent); envelope-residual scores it.
            public float EnvelopeLead;
            // Fire-to-first-launch offset paid once: it moves the release but is not in the arrival-centring span.
            public float StartupLead;
            public bool Grouped;     // ammo forms a missile group (GroupSize>1) => trailing-edge arrival
            public int Waves;        // reload-separated waves the order fires in (1 = no reload needed)
            public float WaveGap;    // sim seconds between successive wave impacts
            public int AnchorShots;  // launches the observation anchor keys on (first wave)
            public float CommitFlightEst = -1f;  // the flight estimate anchor selection used; -1 = none
            public float CommitEstSim = float.NegativeInfinity;  // sim time CommitFlightEst's geometry was read
            public bool CommitEstDone;  // the deferred commit has an answer, including a declined one (0)
        }

        /// <summary>
        /// Orders collected but not committed. An AUTO batch is keyed by target and commits on debounce or
        /// the window cap; a STRIKE batch spans targets and commits only when the player fires it.
        /// </summary>
        private sealed class Batch
        {
            public bool IsStrike;
            public float FirstRealTime;
            public float LastRealTime;
            public readonly List<Intent> Items = new List<Intent>();
        }

        // PredictAnchorImpact cache, keyed on the ripple state (launches seen, measured cadence), with a
        // sim-time TTL so shooter and target motion between launches is picked up.
        internal struct PredictKey : IEquatable<PredictKey>
        {
            public int Launches;        // observed launch count k
            public int IntervalMilli;   // measured cadence (milliseconds)
            public bool Equals(PredictKey o) => Launches == o.Launches && IntervalMilli == o.IntervalMilli;
            public override bool Equals(object obj) => obj is PredictKey k && Equals(k);
            public override int GetHashCode() { unchecked { return (Launches * 397) ^ IntervalMilli; } }
        }

        internal struct PredictCacheEntry
        {
            public PredictKey Key;
            public float StampSim;
            public float Value;
        }

        /// <summary>One held order awaiting release; an anchor stays after release to track its ripple.</summary>
        internal sealed class Scheduled
        {
            public Intent Item;
            // Target impact time; a held follower's tracks the anchor's live prediction until its ripple finalizes.
            public float ImpactAtSim;
            // Observation anchoring (grouped-salvo convergence)
            public bool IsAnchor;       // longest enroute of its batch; its real launches set the shared impact
            public bool Fired;          // released; a follower can no longer be re-timed
            public bool RippleDone;     // impact finalized (wave-1 ripple complete or launches stalled)
            public int AnchorShots;     // launches anchoring keys on (first wave)
            public float IniInterval;   // a-priori per-round interval (seed until 2+ launches observed)
            public readonly List<float> LaunchTimes = new List<float>(); // observed launch times (sim s)

            // Shooter position, velocity and heading at each launch, parallel to LaunchTimes. It feeds the
            // shared impact time, so it is recorded with every gate off.
            public readonly List<LaunchObservation> LaunchObs = new List<LaunchObservation>();

            // The impact anomaly warning fires once per anchor.
            public bool LoggedImpactAnomaly;
            // Last logged impact prediction, so the trace reports movement rather than every TTL expiry.
            // NaN = none yet.
            public float LastLoggedPred = float.NaN;
            // First prediction, so the `anchored` line can state the total slide.
            public float FirstPred = float.NaN;
            public int LastLoggedLaunches = -1;
            public float FiredAtSim = -1f;
            // Last submarine depth sample, so a boat stuck at 0 launches still logs an ascent profile.
            public float LastSubLogSim = float.NegativeInfinity;

            // For the anchor-liveness and impact-commitment lines only; nothing here changes timing.
            public float LastLivenessSim = float.NegativeInfinity;
            public float LastCommittedImpact = float.NaN;
            // Last shooter depth (ft, positive down) and when, for the ascent-progress hold. NaN = not sampled.
            public float LastDepthFt = float.NaN;
            public float LastDepthSim = float.NegativeInfinity;
            public bool LastAscending = true;   // verdict carried between depth samples
            // When the shooter first cleared its launch ceiling, for PostUnblockGraceSim. -inf = still blocked.
            public float UnblockedAtSim = float.NegativeInfinity;
            // Far-from-release items reuse this estimate on a sim cadence. See ResolveFlightEstimate.
            public float LastFlightEst = -1f;
            public float LastFlightEstSim = float.NegativeInfinity;
            // Launch envelope re-read cadence while held; the commit-time value describes where the platform was.
            public float LastEnvelopeSim = float.NegativeInfinity;

            // Per-item caches, held as fields so every removal path drops them with the item.
            public PredictCacheEntry Predict;      // PredictAnchorImpact memo (anchors only)
            public bool HasPredict;
            public float GroupDelayCached = -1f;   // GroupDelay(Item, ReleaseLead); -1 = not computed
            public List<Scheduled> Followers;      // anchors only: the held items keyed to this one
            public List<ObjectBase> BoardTargets;  // anchors only: every target in the strike, for the board rows
            public Scheduled Anchor;               // followers only: the anchor they sync to
            public bool LoggedAnchorWait;          // one "waiting for anchor" line per follower
            public bool LoggedEnvelopeResidual;         // one envelope-residual line per anchor

            // Diagnostic state. Whether a follower can be left holding on an anchor that has left
            // _scheduled: the follower keeps a reference to the anchor object, so the anchor has to
            // say when it went. -1 = still scheduled. ScheduledAtSim gives a hold its own age.
            public float LeftScheduleSim = -1f;
            public bool LoggedOrphan;
            public float ScheduledAtSim = -1f;
        }

        /// <summary>The batch's anchor and held items (fired anchors stay in until their ripple finalizes).</summary>
        internal static IReadOnlyList<Scheduled> ScheduledItems => _scheduled;

        // Open batches, keyed by the shared target. The armed strike is deliberately NOT in here:
        // it has no key, spans targets, and must not be committed by the debounce sweep.
        private static readonly Dictionary<ObjectBase, Batch> _openBatches = new Dictionary<ObjectBase, Batch>();

        private static readonly List<Scheduled> _scheduled = new List<Scheduled>();

        // The armed strike, if any. While one exists every intercepted order joins it instead of a
        // target-keyed batch, and nothing commits until ExecuteStrike is called: that is what makes
        // the collection window effectively unbounded, so the player can work through several
        // formations and several targets at their own pace.
        private static Batch _strikeBatch;

        private static readonly List<ObjectBase> _targetScratch = new List<ObjectBase>();

        /// <summary>True while orders are being collected into one multi-target strike.</summary>
        internal static bool StrikeArmed => _strikeBatch != null;

        /// <summary>
        /// Orders collected into the armed strike from the game's own interface, as <see cref="Shot"/>s, so
        /// the planner can list which orders were caught.
        /// </summary>
        internal static void CollectStrikeIntents(List<Shot> into)
        {
            into.Clear();
            Batch b = _strikeBatch;
            if (b == null) return;
            for (int i = 0; i < b.Items.Count; i++)
            {
                Intent it = b.Items[i];
                if (it.Unit == null || it.Target == null) continue;
                into.Add(new Shot { Unit = it.Unit, AmmoId = it.AmmoId, Salvo = it.Shots, Target = it.Target });
            }
        }

        /// <summary>
        /// Drop every collected order at one target, so the planner's remove acts on everything the player
        /// sees for it. Returns how many were removed.
        /// </summary>
        internal static int RemoveStrikeIntents(ObjectBase target)
        {
            Batch b = _strikeBatch;
            if (b == null || target == null) return 0;
            int before = b.Items.Count;
            b.Items.RemoveAll(it => it.Target == target);
            int removed = before - b.Items.Count;
            if (removed > 0)
                Bootstrap.Log.LogInfo($"[AutoTOT] strike: dropped {removed} collected order(s) at {UnitNaming.SafeName(target)}.");
            return removed;
        }

        /// <summary>Orders held in the armed strike (0 when nothing is armed).</summary>
        internal static int StrikeCount => _strikeBatch?.Items.Count ?? 0;

        /// <summary>Start collecting orders into one strike. No-op if one is already armed.</summary>
        internal static void ArmStrike()
        {
            if (_strikeBatch != null) return;
            OpenStrikeBatch();
            Bootstrap.Log.LogInfo("[AutoTOT] strike armed: orders at any target will be collected until you disarm it.");
        }

        private static void OpenStrikeBatch()
        {
            _strikeBatch = new Batch { IsStrike = true, FirstRealTime = Time.unscaledTime };
            _strikeBatch.LastRealTime = _strikeBatch.FirstRealTime;
        }

        /// <summary>
        /// Commit the armed strike and stay armed for the next one; collection ends only when the player
        /// disarms it, presses CLEAR, or the mission resets. The new batch opens after CommitBatch, so fired
        /// orders cannot land in it.
        /// </summary>
        internal static void ExecuteStrike()
        {
            Batch b = _strikeBatch;
            if (b == null) return;
            _strikeBatch = null;
            if (b.Items.Count == 0)
            {
                Bootstrap.Log.LogInfo("[AutoTOT] strike executed with no orders; nothing to coordinate. Still armed.");
                OpenStrikeBatch();
                return;
            }
            try
            {
                CommitBatch(b);
            }
            catch (Exception e)
            {
                // The strike left _strikeBatch before the commit. Its orders are not put back, since some may
                // already be committed and would fire twice; the mode stays armed and the log says what happened.
                OpenStrikeBatch();
                Bootstrap.Log.LogError(
                    $"[AutoTOT] strike failed to commit ({b.Items.Count} order(s)), so some or all of it " +
                    $"will not fire. Still armed: order again whatever did not launch.\n{e}");
                return;
            }
            OpenStrikeBatch();
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] strike fired ({b.Items.Count} order(s)) and still armed: the next orders " +
                $"you give are collected into a new strike. {StrikeDisarmHint}");
        }

        /// <summary>How the player turns collection off, quoted in the fire log so the mode is never a
        /// surprise.</summary>
        internal const string StrikeDisarmHint = "Press the strike key again, or CLEAR, to stop collecting.";

        /// <summary>Discard the armed strike without firing. The orders it held are never issued.</summary>
        internal static void CancelStrike()
        {
            if (_strikeBatch == null) return;
            int n = _strikeBatch.Items.Count;
            _strikeBatch = null;
            Bootstrap.Log.LogInfo($"[AutoTOT] strike cancelled, {n} held order(s) discarded.");
        }

        // Shooters already warned about launcher contention, once per shooter and ammo until their orders drain.
        private static readonly HashSet<ShipAmmoKey> _contentionWarned = new HashSet<ShipAmmoKey>();

        // The shooter+ammo pairs the last contention check found serialised, for the HUD to mark.
        private static readonly HashSet<ShipAmmoKey> _contendedShooters = new HashSet<ShipAmmoKey>();

        /// <summary>
        /// True if this shooter's launcher for this ammo is committed to more targets than it can service in
        /// parallel, as the last contention check found.
        /// </summary>
        internal static bool IsContended(ObjectBase unit, string ammoId)
            => unit != null && ammoId != null &&
               _contendedShooters.Contains(new ShipAmmoKey(unit, ammoId));

        // Shooters already warned about a guidance-channel trim, once per shooter and ammo until uncapped.
        private static readonly HashSet<ShipAmmoKey> _channelCapWarned = new HashSet<ShipAmmoKey>();

        // The shooter+ammo pairs the last commit had to trim, for the HUD to mark.
        private static readonly HashSet<ShipAmmoKey> _channelCappedShooters = new HashSet<ShipAmmoKey>();

        /// <summary>
        /// True if the last commit trimmed this shooter's salvo to its guidance channels, or its channel
        /// budget is already full.
        /// </summary>
        internal static bool IsChannelCapped(ObjectBase unit, string ammoId)
        {
            if (unit == null || ammoId == null) return false;
            if (_channelCappedShooters.Contains(new ShipAmmoKey(unit, ammoId))) return true;
            // Also answered live, so a budget filled only by planner staging, which never reaches intake or
            // commit, still shows.
            return GuidanceRoom(unit, ammoId) <= 0;
        }

        /// <summary>Any shooter at or over its channel budget, for the on-screen indicator.</summary>
        internal static bool AnyChannelCapped => _channelCappedShooters.Count > 0;

        // Shooters the last commit actually cut rounds from, as opposed to ones sitting at their limit.
        private static readonly HashSet<ShipAmmoKey> _channelTrimmedShooters = new HashSet<ShipAmmoKey>();

        /// <summary>True if the last commit removed rounds from this shooter to fit its channels.</summary>
        internal static bool IsChannelTrimmed(ObjectBase unit, string ammoId)
            => unit != null && ammoId != null &&
               _channelTrimmedShooters.Contains(new ShipAmmoKey(unit, ammoId));

        /// <summary>
        /// Contention, channel-cap and trim marks describe the last commit. Once the coordinator holds
        /// nothing they are stale and are cleared.
        /// </summary>
        private static void ClearStaleShooterMarks()
        {
            if (_contendedShooters.Count == 0 && _channelCappedShooters.Count == 0 &&
                _channelTrimmedShooters.Count == 0) return;
            if (_scheduled.Count > 0 || _openBatches.Count > 0 || _reservations.Count > 0 ||
                StrikeCount > 0 || _pending.Count > 0) return;
            _contendedShooters.Clear();
            _channelCappedShooters.Clear();
            _channelTrimmedShooters.Clear();
        }

        /// <summary>
        /// One observed anchor launch: when it left, the shooter's state at that instant, and the round.
        /// Lets PredictAnchorImpact integrate from the launch point rather than the shooter's current one.
        /// </summary>
        internal struct LaunchObservation
        {
            public float Sim;             // launch stamp, same clock as LaunchTimes
            public Vector3 ShooterPosU;   // shooter position at launch, Unity units
            public float ShooterVelKn;    // shooter speed at launch
            public Vector3 RailHeadingU;  // rail bearing at launch, flat; zero = not resolvable
            public WeaponBase Round;      // the round itself; may be destroyed later, always guard
            // Flight estimate from this launch state, computed once. 0 = not yet, negative = declined.
            public float Est;
        }

        /// <summary>
        /// The terms <see cref="PredictAnchorImpact"/> summed, returned so the trace logs the numbers that were used.
        /// </summary>
        internal struct AnchorPredictTerms
        {
            public bool Valid;             // false on the early-out paths, where pred is carried over
            public float Est;              // FlightTime.Estimate from the shooter's CURRENT position
            public ModelStats.Tier Tier;   // which estimator tier answered the live estimate, D6
            public bool Cached;            // the live estimate came from the cache: no tier ran
            public float LastRoundLaunch;  // launch stamp of the last round, extrapolated while k < n
            public float Centering;        // ReleaseLead subtracted, 0 for a grouped salvo
            public float GroupDelay;
            public bool FromLaunchState;   // est came from the recorded launch state, not from now

            /// <summary>Where <see cref="Est"/> came from, for the trace lines.</summary>
            public string Source => FromLaunchState ? "launch state" : Cached ? "cached" : Tier.ToString();
        }

        /// <summary>A one-step backward jump in the shared impact this large is the estimate failing.</summary>
        private const float ImpactCollapseSeconds = 60f;

        /// <summary>Anchor-liveness line interval while an anchor has launched nothing; matches
        /// envelope-track.</summary>
        private const float LivenessLogIntervalSim = 5f;

        /// <summary>Shared-impact movement worth reporting once a follower has released.</summary>
        private const float ImpactMoveLogSeconds = 1f;

        /// <summary>Impact-prediction movement worth a trace line; the prediction re-runs on a 0.5 s TTL.</summary>
        private const float AnchorSlideLogSeconds = 1f;

        private const float PredictCacheTtlSim = 0.5f;

        // Cadence quantised to milliseconds in the cache key, so sub-millisecond wobble keeps the entry.
        private const float PredictKeyMilliScale = 1000f;

        /// <summary>Called from the Harmony prefix. Returns true if the launch was deferred.</summary>
        internal static bool TryIntercept(
            ObjectBase unit, string ammoId, ObjectBase target,
            bool autoAttack, bool isFormationAttack, int shots, int priority)
        {
            InterceptQueued = false;
            if (!Enabled) return false;            // master off -> fire normally
            // An armed strike collects even with auto-coordination off: arming it is the request to collect.
            bool autoOff = !Active && _strikeBatch == null;

            if (autoAttack) return false;          // only player-issued orders
            if (unit == null || target == null) return false;
            if (!unit.IsPlayerObject) return false;
            // Grouped by shared target within the collection window: several ships at one target, or one ship
            // firing several missile types at it.

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            if (ammo == null || ammo._ap == null || ammo._ap._type != Ammunition.Type.Missile)
                return false; // only missiles

            if (!unit.DoesAmmoMatchTarget(ammo._ap, target, out _))
                return false; // weapon cannot engage this target type

            // Inside minimum launch range the game turns the ship to unmask and then never fires or ends the
            // order, so it is refused. Returning true means "the game must not fire this": the prefix fires
            // the order itself when this returns false.
            if (LaunchEnvelope.IsInsideMinimumRange(unit, ammo._ap, target,
                                                    out float tooCloseNm, out float minNm))
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order refused: {UnitNaming.SafeName(unit)} is {tooCloseNm:0.0}nm from " +
                    $"{UnitNaming.SafeName(target)}, inside the {minNm:0.0}nm minimum launch range " +
                    $"for {ammoId}. The game would turn the ship to unmask and then hold the order " +
                    $"without ever launching it. Open the range, or use a shooter further out.");
                return true;
            }

            // Verbose only: a player who switched auto off is not asking to be told.
            if (autoOff)
            {
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] not collected: auto off and no strike armed " +
                        $"({UnitNaming.SafeName(unit)} -> {UnitNaming.SafeName(target)}, " +
                        $"{ammoId} x{shots}).");
                return false;
            }

            Batch batch = _strikeBatch;
            bool createdBatch = false;
            if (batch == null && !_openBatches.TryGetValue(target, out batch))
            {
                batch = new Batch { FirstRealTime = Time.unscaledTime };
                _openBatches[target] = batch;
                createdBatch = true;
            }

            // Refuse at intake what the ship can never guide, so the held strike states what will really
            // launch. Returns true: the order is dropped, not handed to the game.
            int room = GuidanceRoom(unit, ammoId);
            // Intake side of the channel check: `room` uses the cached cap, the occupancy text a live read of
            // the sensors. They can disagree during a launcher warm-up.
            if (VerboseLog)
            {
                string occ = LauncherFactsSource.GuidanceOccupancyText(unit, ammoId);
                if (!string.IsNullOrEmpty(occ))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] channels-intake {ammoId} from {UnitNaming.SafeName(unit)}: asked for " +
                        $"{shots}, room {(room == int.MaxValue ? "uncapped" : room.ToString())}, " +
                        $"committedUnfired {CommittedUnfired(unit, ammoId)}, staged " +
                        $"{StagedRounds(unit, ammoId)}, live {occ}.");
            }
            if (room <= 0)
            {
                if (createdBatch) _openBatches.Remove(target);
                MarkChannelPressure(_strikeBatch?.Items ?? batch.Items);
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order refused: {UnitNaming.SafeName(unit)} has every guidance channel for " +
                    $"{ammoId} already committed, so these {shots} round(s) were not collected. They " +
                    $"would launch, find no channel and self-destruct. Fire the strike you have, or " +
                    $"use another shooter.");
                return true;
            }
            if (shots > room)
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order reduced: {UnitNaming.SafeName(unit)} can guide {room} more {ammoId} " +
                    $"round(s), so {shots} were collected as {room}.");
                shots = room;
            }

            batch.LastRealTime = Time.unscaledTime;
            batch.Items.Add(new Intent
            {
                Unit = unit,
                AmmoId = ammoId,
                Target = target,
                Shots = shots,
                Priority = priority,
                IsFormation = isFormationAttack,
            });
            InterceptQueued = true;   // held from here on; see InsertEngageTask_Patch

            // Flag channel pressure as the order lands, so a collapsed panel shows it before the rounds leave.
            MarkChannelPressure(batch.Items);

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] queued {UnitNaming.SafeName(unit)} -> {UnitNaming.SafeName(target)} ({ammoId} x{shots})");
            return true;
        }

        /// <summary>
        /// Whether the last <see cref="TryIntercept"/> got as far as holding its order. The prefix reads it
        /// when that call throws: a held order must not also go to the game.
        /// </summary>
        internal static bool InterceptQueued;

        /// <summary>
        /// Drop <paramref name="target"/>'s open batch if it is empty, as a <see cref="TryIntercept"/> that
        /// threw before adding its order leaves it.
        /// </summary>
        internal static void DiscardEmptyBatch(ObjectBase target)
        {
            try
            {
                if (target != null && _openBatches.TryGetValue(target, out Batch b) && b.Items.Count == 0)
                    _openBatches.Remove(target);
            }
            catch (Exception e) { ModLog.Warn("discarding an empty batch after a failed intercept", e); }
        }

        private static int _resetGeneration;

        /// <summary>
        /// Bumped by every <see cref="Reset"/>. UI holding picks compares it, so a staged strike cannot
        /// survive into the next mission.
        /// </summary>
        internal static int ResetGeneration => _resetGeneration;

        /// <summary>Clears all coordinator state. Called on mission end to prevent stale data.</summary>
        internal static void Reset()
        {
            // A reset drops an armed strike; say so, since the panel may still show it as armed.
            if (_strikeBatch != null)
                Bootstrap.Log.LogInfo($"[AutoTOT] strike discarded by coordinator reset " +
                                      $"({_strikeBatch.Items.Count} held order(s)).");
            ResetPending();
            _openBatches.Clear();
            _strikeBatch = null;
            _resetGeneration++;
            _scheduled.Clear();   // per-item caches live on Scheduled, so they go with it
            _contentionWarned.Clear();
            _contendedShooters.Clear();
            _channelCapWarned.Clear();
            _channelCappedShooters.Clear();
            _channelTrimmedShooters.Clear();
            FlightTime.ClearCache();
            FlightTime.ResetPhysicsStep();
            LauncherFactsSource.ClearCache();
            LaunchDiagnostics.Reset();
            EngagementBoard.Clear();
            _reservations.Clear();
            _lastReleaseSimNow = -1f;
            // The census cadence is sim time, and the next mission's clock restarts at zero.
            _lastCensusSim = float.NegativeInfinity;
            CoordinatorProfiler.Reset();
            if (VerboseLog) Bootstrap.Log.LogInfo("[AutoTOT] coordinator state reset.");
        }

        /// <summary>Pumped every frame from Bootstrap.Pump (only inside a mission).</summary>
        internal static void Tick()
        {
            float simNow = GameClock.SimNow();

            // Every frame, so the physics step a target ship moves at is known before the first order.
            // See FlightTime.PredictedPhysicsStep.
            FlightTime.ObservePhysicsStep(simNow);

            // Before the profiler's Tick stage, so the census is not charged to it.
            DiagnoseEstimatorCensus(simNow);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Tick);

            // Publish finished worker results before any stage reads the cache (main thread, one writer).
            FlightTime.DrainCompleted();

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Diag);
            // Guarded on its own: the stages below release the strike, and a launch tracking fault must not stop them.
            try { LaunchDiagnostics.Tick(simNow); }
            catch (Exception e) { LaunchDiagnostics.ReportFault("launch tracking", e); }
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Diag);
            CoordinatorProfiler.AddDiagPhases(LaunchDiagnostics.LastScanLoopMs,
                                   LaunchDiagnostics.LastEngageStateMs,
                                   LaunchDiagnostics.LastFinalizeMs,
                                   LaunchDiagnostics.LastCleanupMs,
                                   LaunchDiagnostics.LastRetireMs);

            // After DrainCompleted and before release, so a commit completed by this frame's results releases
            // on this tick.
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Pending);
            PumpPendingCommits();
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Pending);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Commit);
            CommitReadyBatches();
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Commit);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Anchor);
            UpdateAnchorTracking(simNow);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Anchor);

            PruneReservations(simNow);
            ClearStaleShooterMarks();
            // Engagement-board cleanup, on the tick rather than on the draw. See EngagementBoard.Prune.
            EngagementBoard.Prune(simNow);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Release);
            ReleaseDueLaunches(simNow);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Release);

            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Tick);
            CoordinatorProfiler.FrameDone(_scheduled.Count);
        }

        private static void CommitReadyBatches()
        {
            if (_openBatches.Count == 0) return;

            List<ObjectBase> ready = null;
            float now = Time.unscaledTime;
            foreach (KeyValuePair<ObjectBase, Batch> kv in _openBatches)
            {
                Batch b = kv.Value;
                bool settled = (now - b.LastRealTime) >= DebounceSeconds;
                bool timedOut = (now - b.FirstRealTime) >= MaxWindowSeconds;
                if (settled || timedOut)
                {
                    (ready ??= new List<ObjectBase>()).Add(kv.Key);
                }
            }
            if (ready == null) return;

            foreach (ObjectBase key in ready)
            {
                Batch b = _openBatches[key];
                _openBatches.Remove(key);
                CommitBatch(b);
            }
        }

        /// <summary>
        /// How much an order's arrival moves when its release moves: 1 - closing speed / effective weapon
        /// speed. The release gate assumes holding delays arrival; for a shooter closing at its weapon's
        /// speed it does not, and the gate would never open. The weapon speed comes from the flight estimate
        /// over the current range. Returns 1 when it cannot be computed.
        /// </summary>
        private static float Holdability(Intent it, float flightEst)
        {
            if (it?.Unit == null || it.Target == null || flightEst <= 0f) return 1f;
            Vector3 toTarget = it.Target.transform.position - it.Unit.transform.position;
            float rangeU = toTarget.magnitude;
            if (rangeU <= 0.001f) return 1f;

            // Closing speed along the line of sight, in knots on both sides.
            Vector3 los = toTarget / rangeU;
            float closingKn = it.Unit._velocityInKnots * Vector3.Dot(it.Unit.transform.forward, los)
                            - it.Target._velocityInKnots * Vector3.Dot(it.Target.transform.forward, los);
            if (closingKn <= 0f) return 1f;   // opening or abeam: holding works normally

            float weaponKn = (rangeU * GameUnits.UnityToNm) / (flightEst / SecondsPerHour);
            if (weaponKn <= 0f) return 1f;
            return 1f - closingKn / weaponKn;
        }

        private const float SecondsPerHour = 3600f;

        /// <summary>
        /// Below this an order's arrival no longer follows its release. Ships read about 0.97, a closing
        /// aircraft about 0.
        /// </summary>
        private const float MinHoldability = 0.5f;

        /// <summary>
        /// Commit-time anchor: the order needing the most time (flight + launch-span lead + startup + group
        /// drag). It releases first, and its real launches set the shared impact. Also logs the verbose
        /// `commit` line per shot.
        /// </summary>
        private static Intent PickAnchor(List<Intent> items, out float maxEnroute)
        {
            maxEnroute = 0f;
            Intent anchor = null;
            // The runner-up, so an anchor change after a timing change is identifiable rather than read as a
            // regression.
            float runnerUpEnroute = 0f;
            Intent runnerUp = null;
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                // Components of EnrouteSeconds, kept separate so the commit line can print them.
                float flightEst;
                EstSource source;
                if (it.CommitEstDone)
                {
                    // Already resolved by the deferred commit; re-reading the cache could miss and put the
                    // solve back on this frame.
                    flightEst = it.CommitFlightEst;
                    source = EstSource.DeferredCommit;
                }
                else if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float seeded))
                {
                    // Already solved, possibly by a worker that finished while this loop ran.
                    CoordinatorProfiler.CountCachedHit();
                    flightEst = seeded;
                    source = EstSource.AsyncCache;
                }
                else
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                    flightEst = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                    CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                    source = EstSource.Fresh;
                }
                WarnOnImplausibleEstimate(it, flightEst, source);
                it.CommitFlightEst = flightEst;
                it.Holdability = Holdability(it, flightEst);
                it.FireImmediately = false;   // recomputed below; a re-commit must not inherit it
                float groupDelay = GroupDelay(it, it.ReleaseLead);
                float needed = EnrouteSeconds(it, flightEst, groupDelay);
                if (needed > maxEnroute)
                {
                    runnerUpEnroute = maxEnroute; runnerUp = anchor;
                    maxEnroute = needed; anchor = it;
                }
                else if (needed > runnerUpEnroute) { runnerUpEnroute = needed; runnerUp = it; }
                // One line per shot with the estimate that decided its timing, to pair with the `gap` line at impact.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] commit {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} -> " +
                        $"{UnitNaming.SafeName(it.Target)}: flightEst {flightEst:0.0}s, " +
                        $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                        $"groupDelay {groupDelay:0.0}s, enroute {needed:0.0}s, " +
                        // Above the physics time-scale cap a turning ship rotates less per sim second and its
                        // ripple changes, so the line marks such orders.
                        $"compression {GameTime.TimeCompression:0.#}x" +
                        (GameTime.TimeDilation > 1f
                            ? $", dilation {GameTime.TimeDilation:0.0#} PHYSICS APPROXIMATED"
                            : "") +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));
            }

            // Only for a real contest: the margin says how close a timing change is to flipping the anchor.
            if (VerboseLog && runnerUp != null)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor-margin {anchor?.AmmoId} from {UnitNaming.SafeName(anchor?.Unit)} " +
                    $"[{PlatformTag(anchor?.Unit)}] " +
                    $"at {maxEnroute:0.0}s beats {runnerUp.AmmoId} from {UnitNaming.SafeName(runnerUp.Unit)} " +
                    $"[{PlatformTag(runnerUp.Unit)}] " +
                    $"at {runnerUpEnroute:0.0}s by {maxEnroute - runnerUpEnroute:0.0}s");

            MarkUnholdableOrders(items, anchor);

            // Every estimate failed: all orders get an impact of now and release unstaggered. Warned, since
            // this is not a coordinated strike.
            if (anchor == null && items.Count > 0)
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] no anchor: none of the {items.Count} order(s) had a usable flight " +
                    $"estimate, so they fire now, uncoordinated.");
            return anchor;
        }

        /// <summary>
        /// Mark orders whose arrival does not follow their release, so the gate releases them when ready.
        /// The anchor stays the longest enroute: followers are delayed to match it, and forcing a closing
        /// aircraft to anchor puts every other shooter late. Nothing is dropped.
        /// </summary>
        private static void MarkUnholdableOrders(List<Intent> items, Intent anchor)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it.Holdability >= MinHoldability || ReferenceEquals(it, anchor)) continue;
                it.FireImmediately = true;
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} cannot make the shared " +
                    $"impact on {UnitNaming.SafeName(it.Target)}: closing at almost its own weapon's " +
                    $"speed (holdability {it.Holdability:0.00}), so its arrival is fixed by geometry " +
                    $"whenever it fires. Released as soon as it is ready rather than held, which would " +
                    $"only let it overfly and lose the shot. This round is not on the coordinated time.");
            }
        }

        /// <summary>
        /// Commit checks, in order: drop orders that can no longer fire, trim to guidance channels, report
        /// the magazine. Anchor selection needs the final shot counts, so it runs afterwards. This is the
        /// only check for orders issued in the game's own interface. False, with a warning, when nothing survives.
        /// </summary>
        private static bool TryPrepareCommit(List<Intent> items)
        {
            RevalidateCommit(items);
            if (items.Count == 0)
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] every order in this strike was dropped at commit: no shooter still " +
                    "has a usable weapon for its target. Nothing was fired.");
                return false;
            }
            ClampToGuidanceChannels(items);
            if (items.Count == 0)
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] every order in this strike was trimmed away by the guidance-channel " +
                    "limit; nothing was fired.");
                return false;
            }
            DiagnoseAggregateStock(items);
            return true;
        }

        private static void CommitBatch(Batch b)
        {
            if (!TryPrepareCommit(b.Items)) return;

            foreach (Intent it in b.Items)
                PrepareIntent(it);

            // The estimates are queued now and collected on a later tick (Coordinator.Pending.cs), so a large
            // strike does not freeze the frame.
            BeginCommit(b.Items, planner: false, isStrike: b.IsStrike);
        }

        /// <summary>
        /// Release-to-impact time: flight + ripple lead + startup lead + group-drag delay. Anchor selection
        /// and the release gate share this sum.
        /// </summary>
        private static float EnrouteSeconds(Intent it, float flightEst, float groupDelay)
            => flightEst + it.ReleaseLead + it.StartupLead + groupDelay;

        /// <summary>
        /// Group-drag delay for a grouped salvo, whose leader slows to let the ripple form, from
        /// <see cref="FlightTime.GroupFormingDelay"/>. 0 when not grouped; <paramref name="span"/> is the ripple span.
        /// </summary>
        private static float GroupDelay(Intent it, float span)
            => it.Grouped ? FlightTime.GroupFormingDelay(it.Unit, it.AmmoId, it.Target, span) : 0f;

        /// <summary>Fill an intent's timing from its ship's launcher facts: release lead, startup lead,
        /// reload waves.</summary>
        private static void PrepareIntent(Intent it)
        {
            it.ReleaseLead = 0f; it.StartupLead = 0f; it.EnvelopeLead = 0f;
            it.Grouped = false; it.Waves = 1; it.WaveGap = 0f;
            it.Pooled = false; it.PoolClock = null; it.PoolDelta = 0f;

            int n = Mathf.Max(1, it.Shots);
            it.AnchorShots = n;

            LauncherFactsSource.Facts fFacts = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            // Every order, even a single shot, pays PreLaunchDelay plus the expected reaction draw before round 1.
            if (fFacts.Valid) it.StartupLead = fFacts.StartupDelay;
            // A submerged boat must rise to launch depth and cycle hatches first. Folded into StartupLead,
            // which drives anchor selection and follower timing, so such a boat usually becomes the anchor,
            // which is right: its launch time is the uncertain one. envelope-sim logs the predicted transit
            // for comparison with envelope-track.
            System.Text.StringBuilder ascTrace = VerboseLog ? new System.Text.StringBuilder() : null;
            it.EnvelopeLead = LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId, ascTrace);
            it.StartupLead += it.EnvelopeLead;
            // Tracked for every platform whose launch envelope can change while held: an aircraft can climb
            // out of its band, a boat can be ordered deeper. Surface ships are ready where they stand. For a
            // too-low aircraft LaunchEnvelope returns 0 by design, so the commit value cannot tell ready from
            // unmodelled.
            it.EnvelopeTracked = it.EnvelopeLead > 0f
                              || it.Unit is Aircraft || it.Unit is Submarine;
            // Platform agnostic, and printed for a zero result too: a zero that prints nothing looks like a
            // model that never ran.
            if (VerboseLog && ascTrace != null && ascTrace.Length > 0)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-sim {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                    $"predict {it.StartupLead:0.0}s (transit {it.EnvelopeLead:0.0}s " +
                    $"+ launcher cycle {LauncherFactsSource.LauncherCycleSeconds(it.Unit, it.AmmoId):0.0}s) " +
                    $"|{ascTrace}");
            }

            if (n <= 1) return;

            LauncherFactsSource.Facts f = fFacts;
            if (!f.Valid || f.ShotInterval <= 0f) return;

            // Ready rounds before a reload; 0 means "unknown", so treat the whole order as one wave.
            int readyRounds = (f.ReadyRounds > 0) ? f.ReadyRounds : n;
            int wave1 = Mathf.Min(n, readyRounds);

            // A group holds MaxGroupSize rounds and the rest stay on the rails, so wave 1 is capped there.
            // Two shooters sharing one group are not modelled; the HUD warns instead (Hud.RecomputeGroupSharing).
            if (f.CanGroup && f.MaxGroupSize > 1 && wave1 > f.MaxGroupSize) wave1 = f.MaxGroupSize;

            it.AnchorShots = wave1;   // anchoring keys on wave 1; later waves arrive separately

            // Release lead: grouped missiles land together at the ripple's trailing edge, so the lead is the
            // full ripple span; independent salvos spread over the ripple, so the lead is half the span,
            // centring them on the time on target. The group flag comes from the ammo parameters, so modded
            // missiles work.
            AmmunitionParameters ap = it.Unit?.getAmmunitionByName(it.AmmoId)?._ap;
            it.Grouped = ap != null && ap._maxGroupSize > 1 && wave1 > 1;
            it.ReleaseLead = it.Grouped
                ? (wave1 - 1) * f.ShotInterval
                : (wave1 - 1) / 2f * f.ShotInterval;

            // Reload waves only when the order outruns the ready rounds and there is a magazine reserve.
            if (!f.PerContainer && f.ReadyRounds > 0 && f.Reserve > 0 && n > f.ReadyRounds)
            {
                it.Waves = Mathf.CeilToInt((float)n / f.ReadyRounds);
                it.WaveGap = f.ReadyRounds * f.ShotInterval + f.ReloadGap;
            }
        }

        /// <summary>
        /// An order on a ship-level shared launch clock, for <see cref="PoolSharedLaunchClock"/>. Sorted by
        /// ship and clock, longest flight first.
        /// </summary>
        private struct ClockEntry
        {
            public Intent It;
            public int UnitId;
            public string Clock;
        }

        private static readonly List<ClockEntry> _clockScratch = new List<ClockEntry>();

        private static readonly Comparison<ClockEntry> ClockOrder = (x, y) =>
        {
            int c = x.UnitId.CompareTo(y.UnitId);
            if (c == 0) c = string.CompareOrdinal(x.Clock, y.Clock);
            if (c == 0) c = y.It.CommitFlightEst.CompareTo(x.It.CommitFlightEst);
            return c;
        };

        /// <summary>
        /// Centre each ship's pooled ripple on the time on target, instead of each order's own. A ship fires
        /// every round of one SystemName on one clock, SharedLaunchInterval apart, across all its launchers
        /// and orders (WeaponSystemLauncher.cs:730-751), so centring each order alone left the second late.
        ///
        /// For a pool of N rounds, where order j fires n_j of them and its flight is Delta_j shorter than the longest:
        ///
        ///   lead = (N - 1) / 2 * interval  -  sum(n_j * Delta_j) / N
        ///
        /// An order joins the pool while its first round falls due before the pooled rounds drain. Runs after
        /// the flight estimates and before anchor election. Grouped salvos are left alone.
        /// </summary>
        private static void PoolSharedLaunchClock(List<Intent> items)
        {
            _clockScratch.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it?.Unit == null || it.CommitFlightEst <= FlightTime.MinValidSeconds) continue;
                string clock = LauncherProbe.SharedClockName(it.Unit, it.AmmoId);
                if (clock == null) continue;
                _clockScratch.Add(new ClockEntry { It = it, UnitId = it.Unit.GetInstanceID(), Clock = clock });
            }
            if (_clockScratch.Count >= 2)
            {
                _clockScratch.Sort(ClockOrder);
                for (int runStart = 0; runStart < _clockScratch.Count; )
                {
                    int runEnd = runStart + 1;
                    while (runEnd < _clockScratch.Count &&
                           _clockScratch[runEnd].UnitId == _clockScratch[runStart].UnitId &&
                           _clockScratch[runEnd].Clock == _clockScratch[runStart].Clock)
                        runEnd++;
                    if (runEnd - runStart >= 2) PoolClockRun(runStart, runEnd);
                    runStart = runEnd;
                }
            }
            _clockScratch.Clear();   // it holds intents, which must not outlive the commit
        }

        /// <summary>
        /// Split one ship's orders on one clock into the pools that will actually share it, and give
        /// each pool its lead. The range indexes <see cref="_clockScratch"/>, longest flight first.
        /// </summary>
        private static void PoolClockRun(int runStart, int runEnd)
        {
            ClockEntry head = _clockScratch[runStart];
            string where = $"shared-clock {head.Clock} on {UnitNaming.SafeName(head.It.Unit)}";
            for (int j = runStart; j < runEnd; j++)
            {
                if (!_clockScratch[j].It.Grouped) continue;
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] {where}: {runEnd - runStart} orders, not pooled because " +
                        $"{_clockScratch[j].It.AmmoId} is a grouped salvo, whose arrival under " +
                        $"interleaving is not modelled. Each order keeps its own lead.");
                return;
            }

            float interval = LauncherFactsSource.Get(head.It.Unit, head.It.AmmoId).ShotInterval;
            if (interval <= 0f)
            {
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] {where}: {runEnd - runStart} orders, not pooled: no shot interval.");
                return;
            }

            for (int start = runStart; start < runEnd; )
            {
                float longest = _clockScratch[start].It.CommitFlightEst;
                int rounds = _clockScratch[start].It.AnchorShots;
                int end = start + 1;
                while (end < runEnd && longest - _clockScratch[end].It.CommitFlightEst < rounds * interval)
                {
                    rounds += _clockScratch[end].It.AnchorShots;
                    end++;
                }
                if (end - start >= 2) ApplyClockPool(where, head.Clock, start, end, rounds, interval, longest);
                // Logged so an order left on its own lead is distinguishable from a pass that never ran.
                if (VerboseLog && end < runEnd)
                {
                    Intent next = _clockScratch[end].It;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] {where}: {next.AmmoId} -> {UnitNaming.SafeName(next.Target)} not pooled " +
                        $"with the {end - start} order(s) before it: its flight is " +
                        $"{longest - next.CommitFlightEst:0.0}s shorter, and their {rounds} round(s) at " +
                        $"{interval:0.00}s have drained by then.");
                }
                start = end;
            }
        }

        private static void ApplyClockPool(string where, string clock, int start, int end, int rounds,
                                           float interval, float longest)
        {
            float weighted = 0f;
            for (int j = start; j < end; j++)
            {
                Intent it = _clockScratch[j].It;
                weighted += it.AnchorShots * (longest - it.CommitFlightEst);
            }
            float offset = weighted / rounds;
            float lead = Mathf.Max(0f, (rounds - 1) * 0.5f * interval - offset);

            System.Text.StringBuilder own = VerboseLog ? new System.Text.StringBuilder() : null;
            for (int j = start; j < end; j++)
            {
                Intent it = _clockScratch[j].It;
                own?.Append(own.Length > 0 ? ", " : "")
                    .Append($"{it.AnchorShots} -> {UnitNaming.SafeName(it.Target)} at {it.ReleaseLead:0.0}s");
                it.Pooled = true;
                it.PoolClock = clock;
                it.PoolDelta = longest - it.CommitFlightEst;
                it.ReleaseLead = lead;
            }
            if (own != null)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] {where}: pooled {end - start} orders, {rounds} rounds {interval:0.00}s apart, " +
                    $"mean flight offset {offset:0.0}s, lead {lead:0.0}s (own leads {own}).");
        }

        private static int _nextStrikeId = 1;

        private static void Schedule(IEnumerable<Intent> items, float baseImpact, Intent anchorItem)
        {
            var added = new List<Scheduled>();
            var targets = new List<ObjectBase>();
            Scheduled anchorSched = null;
            foreach (Intent it in items)
            {
                Scheduled s = new Scheduled { Item = it, ImpactAtSim = baseImpact, ScheduledAtSim = GameClock.SimNow() };
                // Seed from the commit estimate; without it the first release pass would re-solve every order
                // on the main thread.
                if (it.CommitFlightEst > FlightTime.MinValidSeconds)
                {
                    s.LastFlightEst = it.CommitFlightEst;
                    s.LastFlightEstSim = float.IsNegativeInfinity(it.CommitEstSim)
                        ? s.ScheduledAtSim : it.CommitEstSim;
                }
                if (it == anchorItem)
                {
                    s.IsAnchor = true;
                    s.AnchorShots = Mathf.Max(1, it.AnchorShots);
                    s.IniInterval = LauncherFactsSource.Get(it.Unit, it.AmmoId).ShotIntervalOrFallback;
                    anchorSched = s;
                }
                added.Add(s);
                _scheduled.Add(s);
                if (it.Target != null && !targets.Contains(it.Target)) targets.Add(it.Target);
            }
            if (anchorSched != null)
            {
                var followers = new List<Scheduled>();
                foreach (Scheduled s in added)
                {
                    if (s.IsAnchor) continue;
                    s.Anchor = anchorSched;
                    followers.Add(s);
                }
                anchorSched.Followers = followers;
                // Every target in the set: followers' live impacts are rewritten regardless of target, so
                // every board row must be too.
                anchorSched.BoardTargets = targets;
            }

            if (targets.Count == 0) return;

            // One strike id for the set, so the board shows these arrivals as one coordinated strike.
            int strikeId = _nextStrikeId++;

            // Arrival shape is per target: set by the orders and launchers aimed at it.
            foreach (ObjectBase target in targets)
            {
                float maxLead = 0f, maxSpread = 0f, waveGap = 0f;
                int maxWaves = 1;
                foreach (Intent it in items)
                {
                    if (it.Target != target) continue;
                    if (it.ReleaseLead > maxLead) maxLead = it.ReleaseLead;
                    // A grouped order's full-span lead is not its arrival spread; the group lands tight.
                    if (!it.Grouped && it.ReleaseLead > maxSpread) maxSpread = it.ReleaseLead;
                    if (it.Waves > maxWaves) { maxWaves = it.Waves; waveGap = it.WaveGap; }
                }

                EngagementBoard.RecordScheduled(target, baseImpact, maxSpread, maxWaves, waveGap, strikeId);
                if (VerboseLog && (maxLead > NegligibleLeadSeconds || maxWaves > 1))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] scheduled {UnitNaming.SafeName(target)}: ±{maxLead:0.0}s, {maxWaves} wave(s)" +
                        (targets.Count > 1 ? $" (strike #{strikeId}, {targets.Count} targets)" : ""));
            }
        }

        // Remove the board row of a target left with no scheduled or fired item, when a held launch is
        // dropped before release.
        private static void DropImpactDataIfUnscheduled(ObjectBase target)
        {
            if (target == null) return;
            if (EngagementBoard.HasFired(target)) return; // grace prune in CollectSalvos will handle it
            foreach (Scheduled s in _scheduled)
                if (s.Item.Target == target) return; // still coordinating another order at this target
            EngagementBoard.Drop(target);
        }

        // Explicit fire from the planner panel

        /// <summary>One hand-picked shot from the planner: a shooter, an ammo type, a salvo size.</summary>
        internal struct Shot
        {
            public ObjectBase Unit;
            public string AmmoId;
            public int Salvo;
            public ObjectBase Target;
        }
    }
}
