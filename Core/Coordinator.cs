using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The coordination pipeline. Batches player missile orders by shared target, works out each
    /// shooter's flight time, and releases each launch late enough that the whole salvo converges
    /// on target simultaneously (Time-on-Target). Open-loop scheduling: impact time fixed at commit,
    /// refined by observation anchoring. See docs/ARCHITECTURE.md for the full walkthrough.
    /// </summary>
    internal static partial class Coordinator
    {
        // Tunables (wired to config in Bootstrap.LoadConfig).
        internal static bool Enabled = true;   // master switch (config)

        internal static bool Active = true;    // runtime toggle (hotkey / on-screen button)

        /// <summary>
        /// Estimator-internals trace: how a flight time was COMPUTED, as opposed to what the
        /// coordinator decided with it. Separate from <see cref="VerboseLog"/> and off by default.
        ///
        /// These traces belong to investigations that are closed (the grounded integrator, the
        /// waypoint-sim port, the fidelity audit, the off-boresight collapse). They are worth
        /// keeping, because re-deriving a flight model without them cost several sessions, but they
        /// are enormous: on a 2026-09-07 run `sim-track`, `track` and `wp-track` alone were 5,561 of
        /// 6,567 lines, 85 percent of the log, and they buried the coordination lines a normal
        /// verbose run is read for.
        ///
        /// They also COST: the per-missile block they gate runs two extra flight sims and a waypoint
        /// sim per round, purely to print a comparison.
        ///
        /// The boundary: VerboseLog answers "what did the mod decide and why", this answers "how was
        /// that number produced".
        /// </summary>
        internal static bool TraceFlightModel = false;

        // Defaults, named because Bootstrap.LoadConfig binds the same two numbers as the config
        // defaults. Config overwrites these at load; they are what runs if it never does.
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

        // ...unless the shooter is visibly still working toward launch, in either of two senses:
        // the launcher reports it is mid launch cycle, or a submarine is still rising toward the
        // weapon's launch ceiling. A deep boat needs longer than NoLaunchStallSim for both: an Oscar
        // ordered up from 350ft spent ~70s ascending and then over a minute in OpeningHatches
        // cycling 24 tubes. Bounded by this ceiling so a wedged shooter still terminates.
        internal const float NoLaunchMaxHoldSim = 300f;

        private const float AscentProgressFeet = 3f;       // depth must drop by this much between samples

        // Grace between "no longer held below the launch ceiling" and "launcher visibly cycling".
        // These are two different reads of the boat and they do not hand over cleanly: the depth
        // block clears the instant the hull crosses the ceiling, while the launcher needs a few sim
        // seconds to enter OpeningSystem, and LauncherFacts serves that state from a REAL-seconds
        // cache, so under time compression the intent read can lag further still. A 2026-09-06 run
        // lost an Oscar at t+130s, unblocked and in OpeningSystem, ten seconds short of firing.
        // The grace starts only once the boat is actually unblocked, so it cannot rescue the
        // periscope-depth deadlock: that boat sits in LauncherTooLow at its ceiling and never
        // clears the block at all.
        private const float PostUnblockGraceSim = 30f;

        // Ascent is sampled on a sim cadence, NOT per frame: a boat making 2-4 ft/s covers far less
        // than AscentProgressFeet in one frame, so a frame-to-frame comparison would read every
        // ascent as stalled. It also keeps the snapshot off the per-frame tick path.
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
        /// One player missile order held for coordinated release. Orders stay WHOLE ; each is
        /// eventually fired as a single InsertEngageTask(shotsToFire=N), matching the game's own
        /// UI path; the launcher then ripples the rounds at its own cadence.
        /// </summary>
        internal sealed class Intent
        {
            public ObjectBase Unit;
            public string AmmoId;
            public ObjectBase Target;
            public int Shots;
            public int Priority;
            public bool IsFormation;
            public float ReleaseLead; // seconds to release before the coordinated impact. Independent
                                      // salvos use HALF their ripple span (centers the arrivals on the
                                      // TOT); grouped salvos use the FULL span, because the group's
                                      // convergent impact lands at the ripple's trailing edge.
                                      // See PrepareIntent.
            public bool EnvelopeTracked; // this platform had to reach its launch envelope at commit,
                                         // so EnvelopeLead is refreshed while the order is held
            public float EnvelopeLead;  // the launch-envelope part of StartupLead (a submarine's
                                        // ascent + hatch cycle, or an aircraft's descent into its
                                        // launch band), kept apart so envelope-residual can score
                                        // that model on its own rather than through StartupLead
            public float StartupLead; // fixed fire-to-first-launch offset (PreLaunchDelay + expected
                                      // reaction draw) the launcher pays ONCE before round 1 leaves.
                                      // Kept SEPARATE from ReleaseLead: it shifts when the order is
                                      // released, but is not part of the arrival-centering span the
                                      // anchor prediction subtracts. See PrepareIntent / FlightTime.
            public bool Grouped;     // ammo forms a missile group (GroupSize>1) => trailing-edge arrival
            public int Waves;        // reload-separated waves the order fires in (1 = no reload needed)
            public float WaveGap;    // sim seconds between successive wave impacts
            public int AnchorShots;  // launches the observation anchor keys on (first wave)
        }

        /// <summary>
        /// Orders collected but not yet committed. An AUTO batch is keyed by its shared target and
        /// commits itself on debounce or the window cap. A STRIKE batch has no key and no target of
        /// its own: it holds orders at any number of targets and commits only when the player
        /// executes it. The items list is the source of truth for which targets are involved, so
        /// neither kind carries a target field.
        /// </summary>
        private sealed class Batch
        {
            public bool IsStrike;
            public float FirstRealTime;
            public float LastRealTime;
            public readonly List<Intent> Items = new List<Intent>();

            /// <summary>The distinct targets in this batch, appended to <paramref name="into"/>.</summary>
            public void CollectTargets(List<ObjectBase> into)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    ObjectBase t = Items[i].Target;
                    if (t != null && !into.Contains(t)) into.Add(t);
                }
            }
        }

        // Cache for PredictAnchorImpact to avoid recomputing every frame. Keyed on the ripple
        // state (observed launches k, measured cadence) with a sim-time TTL on top: while the
        // ripple state holds, the prediction is reused until the TTL expires, then re-run so the
        // live flight estimate picks up shooter/target motion between launches.
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

        /// <summary>
        /// One held order awaiting release (plus, after release, the anchor's ripple-tracking entry).
        /// </summary>
        internal sealed class Scheduled
        {
            public Intent Item;
            public float ImpactAtSim;   // fixed target impact time; launch decided live. For a
                                         // HELD (non-anchor) item this is overwritten every tick with
                                         // the anchor's live impact prediction until the anchor ripple
                                         // finalizes, so the unchanged release formula tracks reality.
            // Observation anchoring (grouped-salvo convergence)
            public bool IsAnchor;       // longest-enroute item of its batch; released first, its real
                                         // launches define the batch's shared impact time
            public bool Fired;          // this order released. For an anchor its ripple is now being
                                        // observed; for a follower it is only a record that the
                                        // round is away and can no longer be re-timed (D2).
            public bool RippleDone;     // impact finalized (wave-1 ripple complete or launches stalled)
            public int AnchorShots;     // launches anchoring keys on (first wave)
            public float IniInterval;   // a-priori per-round interval (seed until 2+ launches observed)
            public readonly List<float> LaunchTimes = new List<float>(); // observed launch times (sim s)

            // D2/D4 diagnostics. LaunchTimes records WHEN each round left and nothing about WHERE
            // the shooter was, which is the quantity the anchor-slide error is proportional to.
            // Parallel list rather than a richer LaunchTimes, so every existing reader is untouched.
            // Verbose only: nothing is appended when the trace is off.
            public readonly List<LaunchObservation> LaunchObs = new List<LaunchObservation>();

            // D6: the impact anomaly warning fires once per anchor, not once per tick.
            public bool LoggedImpactAnomaly;
            // D1: the last impact prediction written to a log line, so the trace reports movement
            // rather than one line per 0.5s TTL expiry. NaN = nothing logged yet.
            public float LastLoggedPred = float.NaN;
            // The first prediction this anchor ever produced, so the `anchored` line can state the
            // total slide over the ripple's life without the reader diffing two lines by hand.
            public float FirstPred = float.NaN;
            public int LastLoggedLaunches = -1;
            public float FiredAtSim = -1f;
            // Last time the submarine depth trace sampled, so a boat that is stuck at 0 launches
            // still produces an ascent profile instead of a single line. -inf = never sampled.
            public float LastSubLogSim = float.NegativeInfinity;

            // D1 (anchor liveness) and D2 (impact commitment), both from
            // docs/plans/open/anchor-liveness.md. Diagnostics only; nothing here changes timing.
            public float LastLivenessSim = float.NegativeInfinity;
            public float LastCommittedImpact = float.NaN;
            // Last observed shooter depth (ft, positive down) and when, for the ascent-progress hold
            // in UpdateAnchorTracking. NaN = not sampled yet.
            public float LastDepthFt = float.NaN;
            public float LastDepthSim = float.NegativeInfinity;
            public bool LastAscending = true;   // verdict carried between depth samples
            // Sim time the shooter first stopped being held below its launch ceiling, for the
            // PostUnblockGraceSim handoff. Reset if it slips back below. -inf = still blocked.
            public float UnblockedAtSim = float.NegativeInfinity;
            // Proximity-gate cache: far-from-release items reuse a prior FlightTime.Estimate on a
            // sim-time cadence rather than re-running the sim every frame. See ResolveFlightEstimate.
            public float LastFlightEst = -1f;
            public float LastFlightEstSim = float.NegativeInfinity;
            // Cadence for re-reading the launch envelope while the order is held. See
            // RefreshEnvelopeLead: the commit-time value describes where the platform WAS.
            public float LastEnvelopeSim = float.NegativeInfinity;

            // Per-item caches. These were three Dictionary<Scheduled, T> beside _scheduled, which
            // meant every removal path had to remember all three; forgetting one was bug B2 in the
            // 2026-09-02 audit. As fields they cannot be left behind, because they die with the item.
            public PredictCacheEntry Predict;      // PredictAnchorImpact memo (anchors only)
            public bool HasPredict;
            public float GroupDelayCached = -1f;   // GroupDelay(Item, ReleaseLead); -1 = not computed
            public List<Scheduled> Followers;      // anchors only: the held items keyed to this one
            public List<ObjectBase> BoardTargets;  // anchors only: every distinct target in the strike,
                                                   // so the live impact prediction reaches each board row
                                                   // and not just the anchor's own target
            public Scheduled Anchor;               // followers only: the anchor they sync to
            public bool LoggedAnchorWait;          // one "waiting for anchor" line per follower
            public bool LoggedEnvelopeResidual;         // one asc-residual line per anchor

            // Phase 1 diagnostics of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md. D1 asks whether a
            // follower can be left holding on an anchor that has left _scheduled: the follower keeps
            // a reference to the anchor object, so the anchor has to say when it went. -1 = still
            // scheduled. D7 needs the hold's own age, which nothing recorded.
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
        /// The orders collected into the armed strike from the game's own interface, as the same
        /// <see cref="Shot"/> shape the panel stages. The planner lists these beside its own rows:
        /// a count alone ("3 in-game") does not tell the player WHICH orders were caught, which is
        /// the only thing that confirms the one they just issued is in the strike.
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
        /// Drop every collected order aimed at one target. The planner's per-target "remove" acts on
        /// the whole group the player sees, and half of that group can be orders the coordinator
        /// caught from the game; leaving those behind made the button look like it had failed.
        /// Returns how many were removed.
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
            _strikeBatch = new Batch { IsStrike = true, FirstRealTime = Time.unscaledTime };
            _strikeBatch.LastRealTime = _strikeBatch.FirstRealTime;
            Bootstrap.Log.LogInfo("[AutoTOT] strike armed: orders at any target will be collected until you fire it.");
        }

        /// <summary>Commit the armed strike as one coordinated set. No-op if nothing is armed.</summary>
        internal static void ExecuteStrike()
        {
            Batch b = _strikeBatch;
            _strikeBatch = null;
            if (b == null) return;
            if (b.Items.Count == 0)
            {
                Bootstrap.Log.LogInfo("[AutoTOT] strike executed with no orders; nothing to coordinate.");
                return;
            }
            CommitBatch(b);
        }

        /// <summary>Discard the armed strike without firing. The orders it held are never issued.</summary>
        internal static void CancelStrike()
        {
            if (_strikeBatch == null) return;
            int n = _strikeBatch.Items.Count;
            _strikeBatch = null;
            Bootstrap.Log.LogInfo($"[AutoTOT] strike cancelled, {n} held order(s) discarded.");
        }

        // Shooters already warned about launcher contention, so the warning fires once per shooter and
        // ammo rather than once per target added. Cleared when that shooter's open orders drain.
        private static readonly HashSet<string> _contentionWarned = new HashSet<string>();

        // The shooter+ammo pairs the last contention check found serialised, for the HUD to mark.
        private static readonly HashSet<string> _contendedShooters = new HashSet<string>();

        /// <summary>
        /// True if this shooter's launcher for this ammo is currently committed to more targets than
        /// it can service in parallel, as found by the last contention check. A multi-target strike
        /// makes this easy to do by accident, so the planner marks the row rather than leaving the
        /// explanation in the log.
        /// </summary>
        internal static bool IsContended(ObjectBase unit, string ammoId)
            => unit != null && ammoId != null &&
               _contendedShooters.Contains(unit.GetInstanceID() + "/" + ammoId);

        // Shooters already warned about a guidance-channel trim, so the warning fires once per
        // shooter and ammo rather than once per commit. Cleared when that pair stops being capped.
        private static readonly HashSet<string> _channelCapWarned = new HashSet<string>();

        // The shooter+ammo pairs the last commit had to trim, for the HUD to mark.
        private static readonly HashSet<string> _channelCappedShooters = new HashSet<string>();

        /// <summary>
        /// True if the last commit trimmed this shooter's salvo of this ammo to what its guiding
        /// sensor can actually control. Unlike <see cref="IsContended"/> this is not advisory: rounds
        /// the player asked for were NOT ordered, so the planner has to say so on the row.
        /// </summary>
        internal static bool IsChannelCapped(ObjectBase unit, string ammoId)
        {
            if (unit == null || ammoId == null) return false;
            if (_channelCappedShooters.Contains(unit.GetInstanceID() + "/" + ammoId)) return true;
            // Also answered live, so a budget filled entirely by planner staging counts. The marked
            // set is only written by the intake and commit paths, which panel picks never reach
            // until the strike is fired; without this a player who staged straight into the panel
            // got no indicator at all.
            return GuidanceRoom(unit, ammoId) <= 0;
        }

        /// <summary>Any shooter at or over its channel budget, for the on-screen indicator.</summary>
        internal static bool AnyChannelCapped => _channelCappedShooters.Count > 0;

        // Shooters the last COMMIT actually had to cut rounds from, as opposed to ones merely sitting
        // at their limit. Only this set justifies telling the player rounds were lost.
        private static readonly HashSet<string> _channelTrimmedShooters = new HashSet<string>();

        /// <summary>True if the last commit removed rounds from this shooter to fit its channels.</summary>
        internal static bool IsChannelTrimmed(ObjectBase unit, string ammoId)
            => unit != null && ammoId != null &&
               _channelTrimmedShooters.Contains(unit.GetInstanceID() + "/" + ammoId);

        /// <summary>
        /// One observed anchor launch: when it left, where the shooter was at that instant, and the
        /// round itself.
        ///
        /// D2 and D4 of docs/plans/open/aircraft-anchor-impact-slide.md. PredictAnchorImpact pairs a
        /// launch timestamp with a flight estimate taken from the shooter's CURRENT position, so the
        /// error it makes is the distance the shooter covered between the two. Nothing recorded that
        /// distance, which is why the slide could only ever be reconstructed after the fact.
        /// </summary>
        internal struct LaunchObservation
        {
            public float Sim;             // launch stamp, same clock as LaunchTimes
            public Vector3 ShooterPosU;   // shooter position at launch, Unity units
            public float ShooterVelKn;    // shooter speed at launch
            public Vector3 RailHeadingU;  // rail bearing at launch, flat; zero = not resolvable
            public WeaponBase Round;      // the round itself; may be destroyed later, always guard
            // Flight estimate from THIS launch state, memoised. A constant of the launch, so it is
            // integrated once and reused; 0 = not computed yet, negative = computed and declined.
            public float Est;
        }

        /// <summary>
        /// The terms <see cref="PredictAnchorImpact"/> summed, for the D1 trace. Returned rather
        /// than recomputed, because recomputing the flight estimate to log it would both cost a
        /// second integration and risk printing a different number from the one that was used.
        /// </summary>
        internal struct AnchorPredictTerms
        {
            public bool Valid;             // false on the early-out paths, where pred is carried over
            public float Est;              // FlightTime.Estimate from the shooter's CURRENT position
            public ModelStats.Tier Tier;   // which estimator tier answered, D6
            public float LastRoundLaunch;  // launch stamp of the last round, extrapolated while k < n
            public float Centering;        // ReleaseLead subtracted, 0 for a grouped salvo
            public float GroupDelay;
            public bool FromLaunchState;   // est came from the recorded launch state, not from now
        }

        /// <summary>A one-step backward jump in the shared impact this large is not the shooter
        /// closing, it is the estimate failing. D6.</summary>
        private const float ImpactCollapseSeconds = 60f;

        /// <summary>D1 of docs/plans/open/anchor-liveness.md: interval between anchor-liveness lines
        /// while an anchor has launched nothing. Matched to the envelope-track cadence so the two
        /// traces read side by side.</summary>
        private const float LivenessLogIntervalSim = 5f;

        /// <summary>D2: shared-impact movement worth reporting once a follower has released. Below
        /// this a move cannot meaningfully strand anything.</summary>
        private const float ImpactMoveLogSeconds = 1f;

        /// <summary>Impact-prediction movement worth a D1 line. The prediction re-runs on a 0.5s
        /// TTL, so an unthresholded trace would be two lines a second per anchor.</summary>
        private const float AnchorSlideLogSeconds = 1f;

        private const float PredictCacheTtlSim = 0.5f;

        // The measured cadence is quantised to milliseconds for the cache key, so a cadence that
        // wobbles below a millisecond does not invalidate the entry every tick.
        private const float PredictKeyMilliScale = 1000f;

        /// <summary>Called from the Harmony prefix. Returns true if the launch was deferred.</summary>
        internal static bool TryIntercept(
            ObjectBase unit, string ammoId, ObjectBase target,
            bool autoAttack, bool isFormationAttack, int shots, int priority)
        {
            if (!Enabled) return false;            // master off -> fire normally
            // Auto-coordination off is a statement about ORDINARY orders, not about an armed
            // strike: arming one is an explicit request to collect what you order next, so it
            // collects on its own. Gating it behind Active made the strike hotkey look broken
            // unless auto happened to be on, a dependency nothing in the UI stated.
            bool autoOff = !Active && _strikeBatch == null;

            if (autoAttack) return false;          // only player-issued orders
            if (unit == null || target == null) return false;
            if (!unit.IsPlayerObject) return false;
            // Coordinates BOTH cases: several ships firing at one target (formation attack),
            // and one ship firing several missile orders (different types) at one target.
            // Grouping is by shared target within the collection window.

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            if (ammo == null || ammo._ap == null || ammo._ap._type != Ammunition.Type.Missile)
                return false; // only missiles

            if (!unit.DoesAmmoMatchTarget(ammo._ap, target, out _))
                return false; // weapon cannot engage this target type

            // The auto-off report, moved BELOW the eligibility checks. Up at the top it fired for
            // the AI's own orders, for gunfire and for anything the mod would never have taken, so
            // a normal mission printed it repeatedly and it said nothing. Here it names only an
            // order AutoTOT could have coordinated and deliberately did not, which is the question
            // it exists to answer (D2 of the strike-arm investigation). Verbose, because a player
            // who has switched auto off is not asking to be told about it.
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

            // Guidance channels, decided HERE rather than left to the commit-time clamp. Collecting
            // an order the ship can never guide and trimming it later is the worse deal: the strike
            // count goes up, the panel implies the rounds are coming, and the player only learns
            // otherwise when they fire. Refusing at intake keeps the held strike an honest statement
            // of what will actually leave.
            //
            // Note the return value on refusal is TRUE, which reads backwards and is not. The
            // Harmony prefix fires the order itself whenever this returns false, so handing the
            // order back would launch exactly the unguidable rounds this is preventing. True means
            // "the game must not fire this", and by not queueing it the order is dropped outright.
            int room = GuidanceRoom(unit, ammoId);
            // D3 of the beta-release audit, intake side. `room` is budget arithmetic over the
            // CACHED cap; the occupancy text is a live read of the same sensors. When a second order
            // arrives during the first one's launcher warm-up, the reservation for the dispatched
            // order has already been dropped (CommittedUnfired skips Fired items) while the sensor
            // has not yet taken the rounds up, so the two disagree and this line records it.
            if (VerboseLog)
            {
                string occ = LauncherFactsSource.GuidanceOccupancyText(unit, ammoId);
                if (!string.IsNullOrEmpty(occ))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] channels-intake {ammoId} from {unit.getUIDAndName()}: asked for " +
                        $"{shots}, room {(room == int.MaxValue ? "uncapped" : room.ToString())}, " +
                        $"committedUnfired {CommittedUnfired(unit, ammoId)}, staged " +
                        $"{StagedRounds(unit, ammoId)}, live {occ}. D3 of the beta-release audit.");
            }
            if (room <= 0)
            {
                if (createdBatch) _openBatches.Remove(target);
                MarkChannelPressure(_strikeBatch?.Items ?? batch.Items);
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order refused: {unit.getUIDAndName()} has every guidance channel for " +
                    $"{ammoId} already committed, so these {shots} round(s) were not collected. They " +
                    $"would launch, find no channel and self-destruct. Fire the strike you have, or " +
                    $"use another shooter.");
                return true;
            }
            if (shots > room)
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] order reduced: {unit.getUIDAndName()} can guide {room} more {ammoId} " +
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

            // Flag channel pressure as the order lands, not at commit: with the panel collapsed the
            // commit-time answer arrives at the same moment the rounds do.
            MarkChannelPressure(batch.Items);

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] queued {unit.getUIDAndName()} -> {target.getUIDAndName()} ({ammoId} x{shots})");
            return true;
        }

        private static int _resetGeneration;

        /// <summary>
        /// Bumped by every <see cref="Reset"/>. UI that holds unfired picks across frames compares
        /// it and drops them, so a staged strike cannot survive into the next mission.
        /// </summary>
        internal static int ResetGeneration => _resetGeneration;

        /// <summary>Clears all coordinator state. Called on mission end to prevent stale data.</summary>
        internal static void Reset()
        {
            // D1 (alt-h-not-collecting): a reset drops an armed strike, and the only trace of it was
            // verbose-gated. If the panel says a strike is armed while the coordinator holds none,
            // this is where it went.
            if (_strikeBatch != null)
                Bootstrap.Log.LogInfo($"[AutoTOT] strike discarded by coordinator reset " +
                                      $"({_strikeBatch.Items.Count} held order(s)).");
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
            LauncherFactsSource.ClearCache();
            LaunchDiagnostics.Reset();
            EngagementBoard.Clear();
            _reservations.Clear();
            _lastReleaseSimNow = -1f;
            CoordinatorProfiler.Reset();
            if (VerboseLog) Bootstrap.Log.LogInfo("[AutoTOT] coordinator state reset.");
        }

        /// <summary>Pumped every frame from Bootstrap.Pump (only inside a mission).</summary>
        internal static void Tick()
        {
            float simNow = GameClock.SimNow();

            // D2 and D10 of the beta-release audit. Both run before the profiler's Tick stage, so
            // neither is charged to a stage it did not spend. D2 states what is still live while the
            // master switch is off; D10 states whether the engagement board is being pruned. See
            // docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
            DiagnoseDisabledWork(simNow);
            DiagnoseBoardCensus(simNow);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Tick);

            // Publish anything the solve workers finished since the last frame, before any stage
            // reads the cache. Main thread, so the cache keeps exactly one writer.
            FlightTime.DrainCompleted();

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Diag);
            LaunchDiagnostics.Tick(simNow);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Diag);
            CoordinatorProfiler.AddDiagPhases(LaunchDiagnostics.LastScanLoopMs,
                                   LaunchDiagnostics.LastFinalizeMs,
                                   LaunchDiagnostics.LastCleanupMs);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Commit);
            CommitReadyBatches();
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Commit);

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.Anchor);
            UpdateAnchorTracking(simNow);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.Anchor);

            PruneReservations(simNow);
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
        /// Commit-time anchor pick: the anchor is the item that needs the most time (lone flight
        /// + its launch-span lead + startup + group drag). It releases first; observation
        /// anchoring then keys the batch's shared impact off the anchor's ACTUAL launches. Also
        /// emits the per-shot verbose "commit" line with the estimate that drove the decision.
        /// Shared by the batch-commit and planner-fire paths.
        /// </summary>
        private static Intent PickAnchor(List<Intent> items, out float maxEnroute)
        {
            // Seed the solve workers before reading any answer. A multi-target strike commits
            // shooters x targets distinct cache keys in ONE frame with nothing cached (the key is
            // {unit, ammo, target}, so cross-target reuse is zero by construction), and this path
            // has no per-frame budget of its own. Queueing first lets the pool absorb what it can
            // while the loop below walks the list; whatever has not landed still falls back to a
            // synchronous solve, so the answer is identical either way. Nothing launches at commit,
            // so a frame of latency here costs nothing that the release gate would not forgive.
            if (items.Count > 1)
                for (int i = 0; i < items.Count; i++)
                    FlightTime.RequestRefresh(items[i].Unit, items[i].AmmoId, items[i].Target);

            maxEnroute = 0f;
            Intent anchor = null;
            // D5: the runner-up, so an anchor CHANGE is identifiable after a timing change rather
            // than being mistaken for a regression. Adding an unmodelled startup term to a shot can
            // push it past the current anchor, and the anchor is the shot every follower is timed
            // against, so a residual that moves for that reason has a different cause from one that
            // moves because the term was wrong.
            float runnerUpEnroute = 0f;
            Intent runnerUp = null;
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                // Components of EnrouteWithLead, kept separate so the commit line below can show the
                // firing-decision flight estimate (FlightTime.Estimate is 0.5s-TTL cached ; this is a
                // hit; GroupDelay is computed once per commit here regardless).
                float flightEst;
                bool fromCache;
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float seeded))
                {
                    // Already solved, possibly by a worker that finished while this loop ran.
                    CoordinatorProfiler.CountCachedHit();
                    flightEst = seeded;
                    fromCache = true;
                }
                else
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                    flightEst = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                    CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                    fromCache = false;
                }
                WarnOnImplausibleEstimate(it, flightEst, fromCache);
                float groupDelay = GroupDelay(it, it.ReleaseLead);
                float needed = flightEst + it.ReleaseLead + it.StartupLead + groupDelay;
                if (needed > maxEnroute)
                {
                    runnerUpEnroute = maxEnroute; runnerUp = anchor;
                    maxEnroute = needed; anchor = it;
                }
                else if (needed > runnerUpEnroute) { runnerUpEnroute = needed; runnerUp = it; }
                // One line per shot at the moment its firing timing is locked in: the estimate that
                // DROVE the decision. Pairs with the `gap` line at impact (simEst vs actual) so the
                // firing-sim's accuracy is verifiable without the per-frame planning spam.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] commit {it.AmmoId} from {it.Unit.getUIDAndName()} -> " +
                        $"{it.Target?.getUIDAndName()}: flightEst {flightEst:0.0}s, " +
                        $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                        $"groupDelay {groupDelay:0.0}s, enroute {needed:0.0}s, " +
                        // Time compression changes what the SHIP does, not just how fast the log
                        // scrolls. Above GameTime._physicsTimeScaleCap the hull's physics runs
                        // slower than sim time (TimeDilation > 1), so a ship that must turn to fire
                        // rotates less per sim second and its ripple comes out differently. Without
                        // this field a spread-out arrival looks like an estimator error and is not
                        // diagnosable after the fact. Same wording as VerticalProfiler's runs.
                        $"compression {GameTime.TimeCompression:0.#}x" +
                        (GameTime.TimeDilation > 1f
                            ? $", dilation {GameTime.TimeDilation:0.0#} PHYSICS APPROXIMATED"
                            : "") +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));
            }

            // D5. Printed for a real contest only: with one shot there is no runner-up, and the
            // margin is the number that says whether a future timing change is close to flipping
            // the anchor role.
            if (VerboseLog && runnerUp != null)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor-margin {anchor?.AmmoId} from {UnitNaming.SafeName(anchor?.Unit)} " +
                    $"[{PlatformTag(anchor?.Unit)}] " +
                    $"at {maxEnroute:0.0}s beats {runnerUp.AmmoId} from {UnitNaming.SafeName(runnerUp.Unit)} " +
                    $"[{PlatformTag(runnerUp.Unit)}] " +
                    $"at {runnerUpEnroute:0.0}s by {maxEnroute - runnerUpEnroute:0.0}s");
            return anchor;
        }

        private static void CommitBatch(Batch b)
        {
            // Before PrepareIntent: anchor selection and the shared impact time are both computed
            // from the shot counts, so they must see the counts that will actually be ordered. This
            // is also the only place the intercepted-order path is covered, since orders issued in
            // the game's own interface never pass through the planner's per-row cap.
            RevalidateCommit(b.Items);
            if (b.Items.Count == 0)
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] every order in this strike was dropped at commit: no shooter still " +
                    "has a usable weapon for its target. Nothing was fired.");
                return;
            }
            ClampToGuidanceChannels(b.Items);
            if (b.Items.Count == 0)
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] every order in this strike was trimmed away by the guidance-channel " +
                    "limit; nothing was fired.");
                return;
            }
            DiagnoseAggregateStock(b.Items);

            foreach (Intent it in b.Items)
                PrepareIntent(it);

            Intent anchor = PickAnchor(b.Items, out float maxEnroute);
            Schedule(b.Items, GameClock.SimNow() + maxEnroute, anchor);

            if (VerboseLog || b.Items.Count > 1)
            {
                _targetScratch.Clear();
                b.CollectTargets(_targetScratch);
                string where = _targetScratch.Count == 1
                    ? _targetScratch[0].getUIDAndName()
                    : $"{_targetScratch.Count} targets";
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] coordinating {b.Items.Count} missile order(s) on {where}: " +
                    $"longest enroute {maxEnroute:0.0}s, anchor {anchor?.AmmoId} -> " +
                    $"{UnitNaming.SafeName(anchor?.Target)}, impacts synced.");
            }
            if (b.IsStrike) WarnOnLauncherContention(b.Items);
        }

        /// <summary>
        /// Group-drag arrival delay for a grouped salvo: the leader throttles to 0.6x speed to let
        /// the ripple form, so the GROUP arrives later than lastRoundLaunch + soloFlight. Derived
        /// from the game's own shot-speed profile (see <see cref="FlightTime.GroupFormingDelay"/>).
        /// 0 for non-grouped orders. <paramref name="span"/> is the launcher's ripple span.
        /// </summary>
        private static float GroupDelay(Intent it, float span)
            => it.Grouped ? FlightTime.GroupFormingDelay(it.Unit, it.AmmoId, it.Target, span) : 0f;

        /// <summary>
        /// Fill in an intent's timing metadata (release lead + reload waves) from the firing
        /// ship's launcher facts. Keeps the intent whole ; the order is fired as one
        /// InsertEngageTask(shotsToFire=N), matching the game's own UI path.
        /// </summary>
        private static void PrepareIntent(Intent it)
        {
            it.ReleaseLead = 0f; it.StartupLead = 0f; it.EnvelopeLead = 0f;
            it.Grouped = false; it.Waves = 1; it.WaveGap = 0f;

            int n = Mathf.Max(1, it.Shots);
            it.AnchorShots = n;

            LauncherFactsSource.Facts fFacts = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            // Fixed fire-to-first-launch offset applies to EVERY order (even a single shot): the
            // engage cycle pays PreLaunchDelay + the expected reaction draw before round 1 leaves.
            if (fFacts.Valid) it.StartupLead = fFacts.StartupDelay;
            // A submerged submarine pays a far larger fire-to-first-round offset than any surface
            // ship: it must rise to the weapon's launch depth and then cycle its hatches. An Oscar
            // ordered up from 350ft took 133s. Folding it in HERE fixes both consumers at once,
            // since StartupLead feeds PickAnchor (anchor selection) and the release gate (follower
            // timing). A submerged boat then normally wins the anchor role, which is what we want:
            // its launch time is the uncertain one, and the anchor is the shot everyone measures.
            // envelope-sim: the predicted transit into the launch envelope, with its per-step
            // profile. Reads against the envelope-track line the shooter produces while actually
            // moving, the same way sim-track reads against track for missiles.
            // envelope-track covers submarines AND aircraft: SampleShooterProgress dispatches on
            // the shooter type, so both platforms produce per-step ground truth to read the
            // predicted profile against.
            System.Text.StringBuilder ascTrace = VerboseLog ? new System.Text.StringBuilder() : null;
            it.EnvelopeLead = LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId, ascTrace);
            it.StartupLead += it.EnvelopeLead;
            // Tracked for every platform whose launch envelope can CHANGE while the order is held,
            // not only for one that owed transit time at commit. An aircraft inside its band can
            // climb out of it, and a boat at launch depth can be ordered deeper; both used to be
            // marked ready once and never asked again, so the order released against a startup lead
            // that had stopped being true. A surface ship is ready where it stands and needs no
            // tracking. Finding 8 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
            //
            // The too-low aircraft case is the one this matters most for: LaunchEnvelope returns 0
            // there deliberately (climb is energy-limited and the model will not fabricate it), so
            // the commit-time value cannot distinguish "ready" from "not modelled".
            it.EnvelopeTracked = it.EnvelopeLead > 0f
                              || it.Unit is Aircraft || it.Unit is Submarine;
            // The seed description is written by whichever model ran, so this line is platform
            // agnostic. It used to be gated on a SubmarineFacts snapshot, which silently suppressed
            // the whole line for aircraft and left an aircraft residual impossible to localise.
            // Printed whenever a model had something to say, INCLUDING a zero result. A zero that
            // prints nothing is indistinguishable from a model that declined to run, and that
            // ambiguity has now cost two test runs.
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

            // A group holds MaxGroupSize members and no more. Rounds past that cannot join, and a
            // guided round that cannot join needs its own fire-control channel, which the launcher
            // no longer has (the group's leader took it). They stay on the rails. Timing the release
            // lead on rounds that will never fly would push the ones that DO fly out of the window,
            // so wave 1 is what the group can actually hold.
            //
            // This covers one ship overfilling a group on its own. It does NOT cover two shooters
            // inside GroupJoinRange sharing one group, because how the cap splits between them is
            // not modelled: observed splits were 9/7 and 8/8 on identical setups. The HUD warns on
            // that case instead, and repositioning removes it. See Hud.RecomputeGroupSharing.
            if (f.CanGroup && f.MaxGroupSize > 1 && wave1 > f.MaxGroupSize) wave1 = f.MaxGroupSize;

            it.AnchorShots = wave1;   // anchoring keys on wave 1; later waves arrive separately

            // Release-lead math ; the launcher ripples the N rounds over (wave1-1)*interval seconds,
            // and the release lead is how much BEFORE the coordinated impact that ripple must start:
            //
            //  - GROUPED missiles (GroupSize>1, e.g. SS-N-12/19) fly a formation and cash in
            //    together: the group's convergent impact lands when the LAST round's lone flight
            //    ends (see UpdateAnchorTracking for why), i.e. at the ripple's TRAILING edge.
            //    Lead = the FULL ripple span, so that trailing edge lands on the TOT.
            //  - INDEPENDENT salvos arrive spread out over the ripple, so lead = HALF the span,
            //    which CENTERS the arrival distribution on the TOT.
            //
            // The group flag comes from the ammo's own params, so modded group missiles are covered.
            AmmunitionParameters ap = it.Unit?.getAmmunitionByName(it.AmmoId)?._ap;
            it.Grouped = ap != null && ap._maxGroupSize > 1 && wave1 > 1;
            it.ReleaseLead = it.Grouped
                ? (wave1 - 1) * f.ShotInterval
                : (wave1 - 1) / 2f * f.ShotInterval;

            // Reload waves only when the order outruns the ready rounds AND there is a magazine
            // reserve to reload from. All-tubes-ready launchers (Slava: no reserve) stay one wave.
            if (!f.PerContainer && f.ReadyRounds > 0 && f.Reserve > 0 && n > f.ReadyRounds)
            {
                it.Waves = Mathf.CeilToInt((float)n / f.ReadyRounds);
                it.WaveGap = f.ReadyRounds * f.ShotInterval + f.ReloadGap;
            }
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
                if (it == anchorItem)
                {
                    s.IsAnchor = true;
                    s.AnchorShots = Mathf.Max(1, it.AnchorShots);
                    LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
                    s.IniInterval = (f.Valid && f.ShotInterval > 0f) ? f.ShotInterval : LauncherFactsSource.FallbackShotInterval;
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
                // Every target in the set, not just the anchor's own. UpdateAnchorTracking rewrites
                // the live impact for followers regardless of target, so the board rows have to be
                // rewritten the same way or the other targets display a frozen, drifting ETA.
                anchorSched.BoardTargets = targets;
            }

            if (targets.Count == 0) return;

            // One strike id shared by every row, so the board can say these arrivals are one
            // coordinated set rather than several that happen to land together.
            int strikeId = _nextStrikeId++;

            // Arrival shape is per TARGET: the spread a player sees at target A is set by the
            // orders aimed at A, and reload waves are a property of the launchers shooting it.
            foreach (ObjectBase target in targets)
            {
                float maxLead = 0f, maxSpread = 0f, waveGap = 0f;
                int maxWaves = 1;
                foreach (Intent it in items)
                {
                    if (it.Target != target) continue;
                    if (it.ReleaseLead > maxLead) maxLead = it.ReleaseLead;
                    // Arrival spread readout: a grouped order's full-span lead is NOT its arrival
                    // spread (the group lands tight), so it contributes ~0 to the ±Ns display.
                    if (!it.Grouped && it.ReleaseLead > maxSpread) maxSpread = it.ReleaseLead;
                    if (it.Waves > maxWaves) { maxWaves = it.Waves; waveGap = it.WaveGap; }
                }

                EngagementBoard.RecordScheduled(target, baseImpact, maxSpread, maxWaves, waveGap, strikeId);
                if (VerboseLog && (maxLead > NegligibleLeadSeconds || maxWaves > 1))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] scheduled {target.getUIDAndName()}: ±{maxLead:0.0}s, {maxWaves} wave(s)" +
                        (targets.Count > 1 ? $" (strike #{strikeId}, {targets.Count} targets)" : ""));
            }
        }

        // Remove the engagement-board row for a target that no longer has any scheduled item and
        // hasn't been fired (so no fired row will drive the board's prune). Called when a held
        // launch is dropped before release. No-op if another scheduled item still shares the
        // target, or the target is still in its post-fire grace window.
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
