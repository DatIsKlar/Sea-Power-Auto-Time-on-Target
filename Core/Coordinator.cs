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
    internal static class Coordinator
    {
        // Tunables (wired to config in Bootstrap.LoadConfig).
        internal static bool Enabled = true;   // master switch (config)
        internal static bool Active = true;    // runtime toggle (hotkey / on-screen button)
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
            public bool Fired;          // anchor released; its launch ripple is being observed
            public bool RippleDone;     // impact finalized (wave-1 ripple complete or launches stalled)
            public int AnchorShots;     // launches anchoring keys on (first wave)
            public float IniInterval;   // a-priori per-round interval (seed until 2+ launches observed)
            public readonly List<float> LaunchTimes = new List<float>(); // observed launch times (sim s)
            public int LastLoggedLaunches = -1;
            public float FiredAtSim = -1f;
            // Last time the submarine depth trace sampled, so a boat that is stuck at 0 launches
            // still produces an ascent profile instead of a single line. -inf = never sampled.
            public float LastSubLogSim = float.NegativeInfinity;
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
        private const float PredictCacheTtlSim = 0.5f;
        // The measured cadence is quantised to milliseconds for the cache key, so a cadence that
        // wobbles below a millisecond does not invalidate the entry every tick.
        private const float PredictKeyMilliScale = 1000f;

        /// <summary>Called from the Harmony prefix. Returns true if the launch was deferred.</summary>
        internal static bool TryIntercept(
            ObjectBase unit, string ammoId, ObjectBase target,
            bool autoAttack, bool isFormationAttack, int shots, int priority)
        {
            if (!Enabled || !Active) return false; // master off, or toggled off -> fire normally
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

            Batch batch = _strikeBatch;
            if (batch == null && !_openBatches.TryGetValue(target, out batch))
            {
                batch = new Batch { FirstRealTime = Time.unscaledTime };
                _openBatches[target] = batch;
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
            _openBatches.Clear();
            _strikeBatch = null;
            _resetGeneration++;
            _scheduled.Clear();   // per-item caches live on Scheduled, so they go with it
            _contentionWarned.Clear();
            _contendedShooters.Clear();
            FlightTime.ClearCache();
            LauncherFactsSource.ClearCache();
            LaunchDiagnostics.Reset();
            EngagementBoard.Clear();
            _lastReleaseSimNow = -1f;
            CoordinatorProfiler.Reset();
            if (VerboseLog) Bootstrap.Log.LogInfo("[AutoTOT] coordinator state reset.");
        }

        /// <summary>Pumped every frame from Bootstrap.Pump (only inside a mission).</summary>
        internal static void Tick()
        {
            float simNow = GameClock.SimNow();

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
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                // Components of EnrouteWithLead, kept separate so the commit line below can show the
                // firing-decision flight estimate (FlightTime.Estimate is 0.5s-TTL cached ; this is a
                // hit; GroupDelay is computed once per commit here regardless).
                float flightEst;
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float seeded))
                {
                    // Already solved, possibly by a worker that finished while this loop ran.
                    CoordinatorProfiler.CountCachedHit();
                    flightEst = seeded;
                }
                else
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                    flightEst = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                    CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                }
                float groupDelay = GroupDelay(it, it.ReleaseLead);
                float needed = flightEst + it.ReleaseLead + it.StartupLead + groupDelay;
                if (needed > maxEnroute) { maxEnroute = needed; anchor = it; }
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
            return anchor;
        }

        private static void CommitBatch(Batch b)
        {
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
                    $"{LaunchDiagnostics.SafeName(anchor?.Target)}, impacts synced.");
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
            it.EnvelopeTracked = it.EnvelopeLead > 0f;
            // The seed description is written by whichever model ran, so this line is platform
            // agnostic. It used to be gated on a SubmarineFacts snapshot, which silently suppressed
            // the whole line for aircraft and left an aircraft residual impossible to localise.
            // Printed whenever a model had something to say, INCLUDING a zero result. A zero that
            // prints nothing is indistinguishable from a model that declined to run, and that
            // ambiguity has now cost two test runs.
            if (VerboseLog && ascTrace != null && ascTrace.Length > 0)
            {
                float hatch = LauncherFactsSource.HatchCycleSeconds(it.Unit, it.AmmoId);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-sim {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                    $"predict {it.EnvelopeLead:0.0}s (transit {it.EnvelopeLead - hatch:0.0}s " +
                    $"+ hatch {hatch:0.0}s) |{ascTrace}");
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
                Scheduled s = new Scheduled { Item = it, ImpactAtSim = baseImpact };
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

        /// <summary>
        /// Live impact prediction for a firing anchor from its observed launch ripple: the last
        /// round's projected launch time (last observed launch + measured cadence x rounds still
        /// to come) plus a live lone-flight estimate at the CURRENT geometry (the last round
        /// launches from wherever the ship is now), minus the ripple-centering lead for
        /// non-grouped salvos. Returns the entry's current impact time until at least one launch
        /// has been observed and a valid estimate exists.
        /// </summary>
        private static float PredictAnchorImpact(Scheduled a, Intent it, int k, int n, float interval)
        {
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictFlight);
            float est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictFlight);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictFlightCalls);
            
            if (k <= 0 || est <= FlightTime.MinValidSeconds) return a.ImpactAtSim;

            float lastLaunch = a.LaunchTimes[k - 1];
            float lastRoundLaunch = (k >= n) ? lastLaunch : lastLaunch + interval * (n - k);
            float span = lastRoundLaunch - a.LaunchTimes[0];
            
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictGroupDelay);
            float groupDelay = GroupDelay(it, span);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictGroupDelay);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictGroupDelayCalls);
            
            return lastRoundLaunch + est - (it.Grouped ? 0f : it.ReleaseLead) + groupDelay;
        }

        /// <summary>
        /// Observation anchoring. The batch anchor (longest enroute incl. span) is released first;
        /// its ACTUAL launches are watched here to extrapolate the launcher's live cadence and
        /// rewrite the shared impact time every held order releases against.
        ///
        /// WHY the anchor's last launch predicts the group's impact: a grouped salvo's convergent
        /// impact lands when its LAST round's solo flight ends (MissileGroup.cs:106-141 ;
        /// AdjustMembersVelocities applies symmetric ±40% speed clamps, so the farthest trailer
        /// flies at exactly solo speed until it closes up; then the group cashes in together,
        /// Missile.cs:839-842). Cadence is MEASURED, not read: no INI declares it, and the realized
        /// value comes from per-cell hatch animations and engage-task reassignment.
        ///
        /// The prediction updated every tick (k = launches observed, n = anchor shots):
        ///
        ///   interval  = (lastLaunch - firstLaunch) / (k-1)          once k >= 2,
        ///               else the INI interval (a-priori seed)
        ///   lastRound = lastLaunch + interval * (n - k)               while k < n,
        ///               else lastLaunch (ripple complete)
        ///   impact    = lastRound + liveEstimate - centering,
        ///               where centering = ReleaseLead for independent salvos (their arrivals are
        ///               centered on the trailing edge minus the lead) and 0 for grouped salvos
        ///               (they land tight at the trailing edge).
        ///
        /// Finalizes when the first wave has fully launched (k >= n), or when launches stall
        /// (shortfall / gating) ; held orders keep whatever prediction was last written.
        /// </summary>
        /// <summary>
        /// Launcher-intent-only form for callers with no <see cref="Scheduled"/> to carry the depth
        /// samples (the shortfall accounting in <see cref="LaunchDiagnostics"/>). Covers a launcher
        /// mid cycle and a submarine still held below its launch ceiling; it cannot tell a boat that
        /// is climbing from one that is parked, so callers must impose their own absolute bound.
        /// </summary>
        internal static bool ShooterStillWorking(ObjectBase unit, string ammoId)
        {
            if (unit == null || ammoId == null) return false;
            if (LauncherFactsSource.IsPreparingToFire(unit, ammoId)) return true;
            return SubmarineFacts.TrySnapshot(unit, ammoId, out SubmarineFacts.Snapshot s) && s.LaunchBlocked;
        }

        /// <summary>
        /// True while a shooter that has launched nothing is still visibly working toward its first
        /// round, so the no-launch stall timer should hold past <see cref="NoLaunchStallSim"/>
        /// (bounded by <see cref="NoLaunchMaxHoldSim"/>). Three signals, in order:
        ///
        /// 1. The launcher reports it is mid launch cycle
        ///    (<see cref="LauncherFactsSource.IsPreparingToFire"/>). This is the game stating its own
        ///    intent and is the primary test.
        /// 2. A submarine is depth-blocked and has come shallower since the last sample, which covers
        ///    the ascent before the launcher starts its cycle at all.
        /// 3. It cleared the block within the last <see cref="PostUnblockGraceSim"/> seconds, which
        ///    covers the handoff between the two: 1 and 2 are read from different places and do not
        ///    overlap, so without this an order can die in the gap.
        ///
        /// Neither an idle launcher nor a boat parked at a depth it will not leave counts, which is
        /// what lets a genuinely stuck order terminate: a radio-command weapon whose guidance radar
        /// needs periscope depth sits in LauncherTooLow at the weapon's own ceiling indefinitely,
        /// never clearing the block, so signal 3 never arms for it either.
        /// </summary>
        internal static bool ShooterStillWorking(Scheduled a, Intent it, float simNow)
        {
            if (a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchMaxHoldSim) return false;
            if (LauncherFactsSource.IsPreparingToFire(it.Unit, it.AmmoId)) return true;
            // The depth verdict is maintained by SampleShooterProgress, which UpdateAnchorTracking
            // runs every tick from the moment the anchor fires. This used to sample inline, but the
            // only caller sits behind a `> NoLaunchStallSim` short-circuit, so the boat was first
            // read 120s into an ascent that was over by 135s: envelope-track produced three lines
            // at the very end and could not be compared against envelope-sim at all.
            return a.LastAscending;
        }

        /// <summary>
        /// Samples the shooter's ascent on <see cref="DepthSampleIntervalSim"/>, maintaining the
        /// verdict <see cref="ShooterStillWorking(Scheduled, Intent, float)"/> returns and emitting
        /// the <c>envelope-track</c> trace. Called every tick for an anchor that has launched
        /// nothing; self-gating, so calling it more often costs one float compare.
        /// </summary>
        private static void SampleShooterProgress(Scheduled a, Intent it, float simNow)
        {
            if (it.Unit == null || it.Unit.IsDestroyed) return;
            if (simNow - a.LastDepthSim < DepthSampleIntervalSim) return;

            if (!SubmarineFacts.TrySnapshot(it.Unit, it.AmmoId, out SubmarineFacts.Snapshot s))
            {
                if (SampleAircraftProgress(a, it, simNow)) return;
                // Not a submarine or an aircraft, or unreadable: nothing to hold on. The
                // launcher-intent test in ShooterStillWorking still applies.
                a.LastDepthSim = simNow;
                a.LastAscending = false;
                return;
            }

            float prev = a.LastDepthFt;
            // envelope-track: reads line-for-line against envelope-sim. Rate is measured over the
            // sample interval, so the first sample (no previous depth) reports none.
            if (VerboseLog && a.FiredAtSim >= 0f)
            {
                float rate = float.IsNaN(prev) ? float.NaN
                           : (prev - s.DepthFt) / Mathf.Max(0.001f, simNow - a.LastDepthSim);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-track {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                    $"t+{simNow - a.FiredAtSim:0}s {s.DepthFt:0}ft p{s.PitchDeg:0.0} " +
                    $"r{(float.IsNaN(rate) ? 0f : rate):0.00}ft/s spd {s.SpeedKn:0.0}kn " +
                    $"cmd {(float.IsNaN(s.CmdSpeedKn) ? 0f : s.CmdSpeedKn):0.0}kn " +
                    $"tanks {s.BallastRateFtPerS:0.00}ft/s blocked {s.LaunchBlocked} engage {s.EngageState}");
            }
            a.LastDepthFt = s.DepthFt;
            a.LastDepthSim = simNow;

            if (!s.LaunchBlocked)
            {
                // In the launch envelope. The launcher has not been seen cycling yet (that test runs
                // first and wins outright), so hold for PostUnblockGraceSim to cover the handoff
                // rather than dropping the order in the gap between the two reads.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return;
            }

            // Slipped back below the ceiling: the grace is about the handoff, so it starts again.
            a.UnblockedAtSim = float.NegativeInfinity;
            // First sample: nothing to compare against, so allow one more interval.
            a.LastAscending = float.IsNaN(prev) || (prev - s.DepthFt) >= AscentProgressFeet;
        }

        /// <summary>
        /// Aircraft half of <see cref="SampleShooterProgress"/>: emits envelope-track and maintains
        /// the same progress verdict. False when the shooter is not a readable aircraft.
        ///
        /// <see cref="Scheduled.LastDepthFt"/> carries the altitude here rather than a depth. The two
        /// share a convention that makes the progress test identical: both quantities DECREASE as
        /// the shooter approaches its launch envelope, a boat rising and an aircraft descending.
        /// </summary>
        private static bool SampleAircraftProgress(Scheduled a, Intent it, float simNow)
        {
            if (!LaunchEnvelope.TrySnapshotAircraft(it.Unit, it.AmmoId, out LaunchEnvelope.AirSnapshot s))
                return false;

            float prev = a.LastDepthFt;
            if (VerboseLog && a.FiredAtSim >= 0f)
            {
                float rate = float.IsNaN(prev) ? float.NaN
                           : (prev - s.AltFt) / Mathf.Max(0.001f, simNow - a.LastDepthSim);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-track {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                    $"t+{simNow - a.FiredAtSim:0}s {s.AltFt:0}ft p{s.PitchDeg:0.0} " +
                    $"r{(float.IsNaN(rate) ? 0f : rate):0.00}ft/s tas {s.TasKn:0}kn " +
                    $"mach {s.Mach:0.000} (cmd {(float.IsNaN(s.CmdMach) ? 0f : s.CmdMach):0.000}) " +
                    $"gate {s.GateFt:0}ft aboveGate {s.AboveGate}");
            }
            a.LastDepthFt = s.AltFt;
            a.LastDepthSim = simNow;

            if (!s.AboveGate)
            {
                // Inside the launch band. Same handoff problem as the submarine clearing its ceiling:
                // the launcher needs a moment to start its cycle, and that read is cached in real
                // seconds. Hold for the same grace rather than dropping the order in the gap.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return true;
            }

            a.UnblockedAtSim = float.NegativeInfinity;
            a.LastAscending = float.IsNaN(prev) || (prev - s.AltFt) >= AscentProgressFeet;
            return true;
        }

        private static void UpdateAnchorTracking(float simNow)
        {
            if (_scheduled.Count == 0) return;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled a = _scheduled[i];
                if (!a.IsAnchor || !a.Fired || a.RippleDone) continue;
                Intent it = a.Item;
                // Dead shooter: ReleaseDueLaunches drops the entry; held orders keep the last
                // prediction already written into their ImpactAtSim.
                if (it.Unit == null || it.Unit.IsDestroyed) continue;
                // Dead anchor TARGET, post-launch. Re-election is impossible once the anchor has
                // fired, because its ripple IS the clock. With one target that only affects the
                // anchor's own shot; with several it would strand every surviving target on a
                // prediction that stops being updated while its followers keep waiting. So lock the
                // strike on the last prediction now and say so, rather than letting it drift.
                if (it.Target == null || it.Target.IsDestroyed)
                {
                    a.RippleDone = true;
                    _scheduled.RemoveAt(i);
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchor target lost after launch ({it.AmmoId} from " +
                        $"{LaunchDiagnostics.SafeName(it.Unit)}); impact locked at sim {a.ImpactAtSim:0.0} " +
                        $"for the {(a.Followers?.Count ?? 0)} order(s) still held.");
                    continue;
                }

                int k = a.LaunchTimes.Count;
                int n = Mathf.Max(1, a.AnchorShots);

                // Ascent sampling runs from the moment the anchor fires, not from the stall check,
                // so envelope-track covers the whole climb and lines up with envelope-sim.
                if (k == 0 && a.FiredAtSim >= 0f) SampleShooterProgress(a, it, simNow);

                // asc-residual: the one number that says whether the ascent model is good.
                // Observed order-to-first-round against what was predicted at commit, scored once
                // when the first launch lands. Positive = the boat took longer than predicted.
                if (VerboseLog && k >= 1 && !a.LoggedEnvelopeResidual && it.EnvelopeLead > 0f && a.FiredAtSim >= 0f)
                {
                    a.LoggedEnvelopeResidual = true;
                    float observed = a.LaunchTimes[0] - a.FiredAtSim;
                    float hatch = LauncherFactsSource.HatchCycleSeconds(it.Unit, it.AmmoId);
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] envelope-residual {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                        $"observed {observed:0.0}s, predicted {it.EnvelopeLead:0.0}s " +
                        $"(transit {it.EnvelopeLead - hatch:0.0}s + hatch {hatch:0.0}s), " +
                        $"residual {observed - it.EnvelopeLead:+0.0;-0.0}s");
                }

                // Live cadence: measured once 2+ launches are in; the INI interval seeds k<=1.
                float interval = a.IniInterval;
                if (k >= 2) interval = (a.LaunchTimes[k - 1] - a.LaunchTimes[0]) / (k - 1);
                if (interval <= 0f) interval = a.IniInterval > 0f ? a.IniInterval : LauncherFactsSource.FallbackShotInterval;

                // Prediction cache: reuse while the ripple state (k, interval) is unchanged AND
                // the entry is fresh within the sim TTL. An expired entry re-runs
                // PredictAnchorImpact so the live flight estimate tracks shooter/target motion
                // between launches. (An earlier int-mixed key collided once cadence >= 10 s and
                // had no TTL at all, freezing the prediction between launches.)
                PredictKey cacheKey = new PredictKey { Launches = k, IntervalMilli = Mathf.RoundToInt(interval * PredictKeyMilliScale) };
                float pred;
                if (a.HasPredict && a.Predict.Key.Equals(cacheKey) &&
                    (simNow - a.Predict.StampSim) < PredictCacheTtlSim)
                {
                    pred = a.Predict.Value;
                }
                else
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.AnchorPredict);
                    pred = PredictAnchorImpact(a, it, k, n, interval);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.AnchorPredict);
                    a.Predict = new PredictCacheEntry { Key = cacheKey, StampSim = simNow, Value = pred };
                    a.HasPredict = true;
                }
                // Held orders in this batch follow the live prediction; their release condition is
                // re-evaluated against it every tick. Uses anchor->followers index for O(followers).
                if (a.Followers != null)
                {
                    for (int j = 0; j < a.Followers.Count; j++)
                        a.Followers[j].ImpactAtSim = pred;
                }
                // Every target in the strike, not just the anchor's: the followers aimed elsewhere
                // are being retimed above, so their board rows have to follow or they show a frozen
                // impact and a drifting ETA for a shot that is in fact still on schedule.
                if (a.BoardTargets != null)
                {
                    for (int j = 0; j < a.BoardTargets.Count; j++)
                        EngagementBoard.UpdateImpact(a.BoardTargets[j], pred);
                }
                else
                {
                    EngagementBoard.UpdateImpact(it.Target, pred);
                }

                bool complete = k >= n;
                // Stall = launches stopped (gated/short), or NOTHING launched for a long while
                // (launcher inoperable, guidance wait, ship still turning into its firing arc).
                //
                // Both arms consult ShooterStillWorking. The mid-ripple arm used not to, and a Kynda
                // reported 2/8 while its remaining 6 rounds were still on the way: its real gap
                // between pairs is far longer than StallMinWindowSim, so the timer expired mid-salvo
                // and the batch impact was then anchored on a 2-round sample. The launcher stating
                // it is mid cycle outranks any timer. ShooterStillWorking self-limits at
                // NoLaunchMaxHoldSim from the first round, so a launcher that truly dies still ends.
                bool stalled = k > 0
                    ? (simNow - a.LaunchTimes[k - 1]) > Mathf.Max(StallCadenceMultiplier * interval, StallMinWindowSim)
                      && !ShooterStillWorking(a, it, simNow)
                    : a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchStallSim
                      && !ShooterStillWorking(a, it, simNow);
                if (complete || stalled)
                {
                    a.RippleDone = true;
                    _scheduled.RemoveAt(i);   // its caches are fields, so they go with it
                    float span = (k > 1) ? a.LaunchTimes[k - 1] - a.LaunchTimes[0] : 0f;

                    // Nothing launched: there is no shot to anchor on, so the prediction written to
                    // the board every tick above is for an impact that will never happen. Leaving it
                    // makes co-shooters hold their fire against a phantom. Drop the engagement and
                    // say so loudly instead.
                    if (k == 0)
                    {
                        EngagementBoard.Drop(it.Target);
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] order abandoned {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)} -> " +
                            $"{LaunchDiagnostics.SafeName(it.Target)}: nothing launched, engagement dropped so other " +
                            $"shooters are not held against it." + SubmarineFacts.Describe(it.Unit, it.AmmoId));
                    }
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchored {LaunchDiagnostics.SafeName(it.Target)}: {k}/{n} launched over {span:0.0}s " +
                        $"(cadence {interval:0.0}s), impact set to sim {pred:0.0}" +
                        (stalled && !complete ? " ; ripple stalled, anchored on launches observed" : "") +
                        (stalled && k == 0 ? " (NOTHING launched)" : "") +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));

                    // Diagnostic: the range-aware τ_form model's internals (now the LIVE model, so
                    // `candidate` == `applied groupDelay`). Kept for sanity-checking modded/untested
                    // missiles ; watch that `candidate` tracks the observed residual.
                    if (VerboseLog && it.Grouped && span > 0f &&
                        FlightTime.GroupFormingTauDiag(it.Unit, it.AmmoId, it.Target, span,
                            out float pSpan, out float tauForm, out float candidate))
                    {
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] group-tau {it.AmmoId}: span {span:0.0}s, Pspan {pSpan:0}, " +
                            $"2.5Pspan {2.5f * pSpan:0}, tauForm {tauForm:0.0}s, candidate {candidate:0.0}s " +
                            $"(applied groupDelay {GroupDelay(it, span):0.0}s)");
                    }
                }
                // Submarines re-log on a sim cadence even when k has not moved: a boat holding at
                // 0/8 while it comes up to launch depth is otherwise one line and no trace at all,
                // and "rising slowly" vs "not rising and never will" is the whole question.
                else if (VerboseLog && (k != a.LastLoggedLaunches ||
                         (SubmarineFacts.IsSubmarine(it.Unit) &&
                          simNow - a.LastSubLogSim >= TelemetryCadence.SampleIntervalSim)))
                {
                    a.LastLoggedLaunches = k;
                    a.LastSubLogSim = simNow;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchoring {LaunchDiagnostics.SafeName(it.Target)}: {k}/{n} launched, " +
                        $"cadence {interval:0.0}s, impact predicted sim {pred:0.0}" +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));
                }
            }
        }

        private static float _lastReleaseSimNow = -1f;

        /// <summary>
        /// Flight time for one scheduled item, refreshed or reused. Mutates the item's cached
        /// estimate and the per-frame fresh-sim budget, which is why it is not a pure function.
        /// </summary>
        private static float ResolveFlightEstimate(Scheduled s, Intent it, float simNow,
                                                   float timeLeft, float releaseGate)
        {
            // Two invariants bind here.
            //
            // 1. Release must use the SAME estimator the commit path scheduled the impact against
            //    (FlightTime.Estimate, whose real-0.5 s TTL cache both paths share). Any cheaper
            //    substitute biases one path against the other, slack goes negative, and the whole
            //    batch dumps on one tick with no stagger.
            // 2. Staleness is measured in SIM time, not real time, or slow ticks expire the cache
            //    every frame and force a full recompute, which is the feedback loop that made them
            //    slow.
            //
            // Proximity gate: far items reuse a prior Estimate on a long cadence (FlightRefreshSim),
            // near-release items on a short one (FlightRefreshNearSim). The reused value is always a
            // real Estimate output, so it cannot diverge from the commit path.
            // See docs/plans/done/2026-08-30-performance-analysis.md and
            // docs/plans/reference/estimator-cost.md.
            bool nearRelease = s.LastFlightEst >= 0f &&
                               timeLeft <= s.LastFlightEst + releaseGate + FlightGateMargin;
            float refreshCadence = nearRelease ? FlightRefreshNearSim : FlightRefreshSim;
            bool due = s.LastFlightEst < 0f || simNow - s.LastFlightEstSim >= refreshCadence;
            // Per-frame ceiling on fresh sims: a synchronized wave bunches many due refreshes into
            // one frame. An item with NO estimate always computes; correctness before budget.
            //
            // The budget is charged AFTER the call and only when it missed the cache. Charging it
            // before meant a cache hit spent a slot, and hits are the large majority: one measured
            // window served 454 hits against 123 misses, the hits costing 0.1 ms in total. A ceiling
            // of 12 therefore admitted about 2 real sims per frame and turned away roughly 64 items,
            // so the release path ran on estimates 8 to 12 s old with the budget mostly spent on
            // work that was free. Only a miss consumes the resource this exists to bound.
            bool budgeted = s.LastFlightEst < 0f || _flightEstimatesThisFrame < MaxFlightEstimatesPerFrame;
            float flightNow;
            if (due && budgeted)
            {
                // Three ways to answer, cheapest first.
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float cached))
                {
                    // Already computed, possibly by a worker that finished since the last tick.
                    // No Begin/End here: nothing ran, so there is no duration to attribute.
                    CoordinatorProfiler.CountCachedHit();
                    flightNow = cached;
                    s.LastFlightEst = flightNow;
                    s.LastFlightEstSim = simNow;
                }
                else if (s.LastFlightEst >= 0f &&
                         FlightTime.RequestRefresh(it.Unit, it.AmmoId, it.Target))
                {
                    // A refresh of a value we already hold: queue it and keep the previous number
                    // for now. LastFlightEstSim is deliberately NOT advanced, so the item stays due
                    // and adopts the fresh value the moment it lands. RequestRefresh dedupes by key,
                    // so staying due does not re-queue it.
                    flightNow = s.LastFlightEst;
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.FlightQueued);
                }
                else
                {
                    // No usable previous value (the anchor releases almost immediately after being
                    // scheduled, so it must be answered now), or the queue declined the work.
                    // Correctness before budget, as before.
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                    flightNow = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                    bool cacheHit = FlightTime.WasLastCallCacheHit;
                    CoordinatorProfiler.CountEstimate(cacheHit);
                    if (!cacheHit) _flightEstimatesThisFrame++;
                    s.LastFlightEst = flightNow;
                    s.LastFlightEstSim = simNow;
                }
            }
            else
            {
                flightNow = s.LastFlightEst;
                CoordinatorProfiler.Count(due ? CoordinatorProfiler.Counter.FlightBudgetSkipped
                                   : CoordinatorProfiler.Counter.FlightDeferred);
            }
            return flightNow;
        }

        private static void ReleaseDueLaunches(float simNow)
        {
            if (_scheduled.Count == 0) { _lastReleaseSimNow = simNow; return; }

            // Half-a-frame lookahead: releases evaluate "time left <= flight time" with a flight
            // time estimated THIS frame, but the missile actually launches a fraction of a sim
            // step later. The tiny lead absorbs shooter/target motion during the stagger and
            // corrects time-compression's late-bias. simStep is measured in SIM time so pause
            // (simStep=0) adds no lookahead.
            float simStep = (_lastReleaseSimNow >= 0f) ? Mathf.Max(0f, simNow - _lastReleaseSimNow) : 0f;
            float lookahead = LookaheadFraction * simStep;
            _lastReleaseSimNow = simNow;
            _flightEstimatesThisFrame = 0;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled s = _scheduled[i];
                Intent it = s.Item;

                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                {
                    _scheduled.RemoveAt(i);
                    if (s.IsAnchor && !s.Fired) PromoteNewAnchor(s, simNow);
                    // Never fires, so the target may never get a fired row, which is what the board's
                    // prune keys off. Drop it now or it leaks until the next mission Reset().
                    DropImpactDataIfUnscheduled(it.Target);
                    LogDroppedItem(s);
                    continue;
                }

                float timeLeft = s.ImpactAtSim - simNow;

                // Use cached GroupDelay if available (independent of the flight estimate; the
                // proximity gate below needs it, so resolve it first).
                if (s.GroupDelayCached < 0f)
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.GroupDelay);
                    s.GroupDelayCached = GroupDelay(it, it.ReleaseLead);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.GroupDelay);
                }
                float groupDelay = s.GroupDelayCached;
                CoordinatorProfiler.Count(CoordinatorProfiler.Counter.GroupDelayCalls);

                // A follower never fires before its anchor has actually put a round up. The anchor is
                // by definition the longest-enroute shot of the batch, so a follower going first can
                // only ever be too early. Normally a no-op (the anchor launches within seconds of its
                // own release); it earns its keep when the anchor is a submarine whose ascent ran
                // longer than the estimate folded into StartupLead. Bounded by RippleDone: when the
                // anchor completes, or is abandoned at NoLaunchMaxHoldSim, followers free next tick.
                if (!s.IsAnchor && s.Anchor != null && s.Anchor.Fired &&
                    !s.Anchor.RippleDone && s.Anchor.LaunchTimes.Count == 0)
                {
                    if (VerboseLog && !s.LoggedAnchorWait)
                    {
                        s.LoggedAnchorWait = true;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] holding {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                            $"anchor {s.Anchor.Item?.AmmoId} from {LaunchDiagnostics.SafeName(s.Anchor.Item?.Unit)} " +
                            $"has launched nothing yet.");
                    }
                    continue;
                }

                RefreshEnvelopeLead(s, it, simNow);

                float releaseGate = it.ReleaseLead + it.StartupLead + groupDelay + lookahead;
                float flightNow = ResolveFlightEstimate(s, it, simNow, timeLeft, releaseGate);

                // Release early by: the ripple lead (centers the salvo on the coordinated impact),
                // the fixed startup offset (PreLaunchDelay + expected reaction) the engage cycle burns
                // before round 1 leaves the rail, PLUS the group-drag delay (a grouped salvo flies
                // slower than the solo estimate, so it must leave earlier to still arrive on time).
                if (timeLeft <= flightNow + it.ReleaseLead + it.StartupLead + groupDelay + lookahead)
                {
                    if (s.IsAnchor)
                    {
                        // The anchor stays in _scheduled after firing: UpdateAnchorTracking observes
                        // its launch ripple and finalizes the batch impact from it.
                        if (s.Fired) continue;
                        s.Fired = true;
                        s.FiredAtSim = simNow;
                    }
                    else
                    {
                        _scheduled.RemoveAt(i);
                    }
                    // Regression guardrail for invariant 1 in ResolveFlightEstimate: large positive
                    // overshoot means the item cannot make its scheduled impact and the batch has
                    // collapsed onto one tick with no stagger. Once per item, independent of
                    // VerboseLog.
                    float overshoot = flightNow - timeLeft;
                    if (overshoot > SlackWarnSeconds)
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] {it.AmmoId} from {it.Unit.getUIDAndName()} released {overshoot:0.0}s " +
                            $"past-due (est flight {flightNow:0.0}s > time-to-impact {timeLeft:0.0}s) ; " +
                            $"launch stagger lost; commit/release flight estimates likely diverged.");
                    if (VerboseLog)
                    {
                        AmmunitionParameters ap = it.Unit.getAmmunitionByName(it.AmmoId)?._ap;
                        // Verbose-only, but it runs the model, so it is timed like any other sim.
                        CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                        float kinematicEst = (ap != null) ? FlightTime.Kinematic(it.Unit, ap, it.Target) : -1f;
                        CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                        CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                        string src = (kinematicEst > FlightTime.MinValidSeconds) ? "kinematic" : "straight-line fallback";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] launch {it.AmmoId} from {it.Unit.getUIDAndName()}" +
                            $"{(s.IsAnchor ? " (anchor)" : "")}: " +
                            $"est flight {flightNow:0.0}s ({src}), " +
                            $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                            $"groupDelay {groupDelay:0.0}s, " +
                            $"impactAt {s.ImpactAtSim:0.0}, now {simNow:0.0}, " +
                            $"simStep {simStep:0.0}s, overshoot {overshoot:0.0}s" +
                            SubmarineFacts.Describe(it.Unit, it.AmmoId));
                    }
                    // Age of the estimate this release used: what the per-frame sim cap costs in
                    // freshness. 0 = recomputed this frame, -1 = no estimate was ever taken.
                    CoordinatorProfiler.ReleaseStaleness(s.LastFlightEst >= 0f ? simNow - s.LastFlightEstSim : -1f);
                    Fire(it, s.IsAnchor ? s : null);
                }
            }
        }

        /// <summary>
        /// Re-elect an anchor after the sitting one is dropped BEFORE it released anything (its
        /// shooter or its target died while the strike was still being held).
        ///
        /// With one target per strike this never mattered: losing the anchor's target lost the only
        /// engagement. A multi-target strike is different. The other targets' orders are still live,
        /// still held, and still keyed to an anchor that will now never launch, so without this they
        /// coast forever on the impact time fixed at commit. The longest-enroute survivor takes over,
        /// exactly as <see cref="Schedule"/> would have chosen it had the loser never been there.
        ///
        /// The shared impact time is deliberately NOT recomputed. It was set from the lost anchor's
        /// enroute time, which was the largest, so every survivor can still make it; re-deriving it
        /// would pull the whole strike earlier and could put a shot past due.
        /// </summary>
        private static void PromoteNewAnchor(Scheduled lost, float simNow)
        {
            List<Scheduled> followers = lost.Followers;
            if (followers == null || followers.Count == 0) return;

            Scheduled best = null;
            float bestEnroute = float.NegativeInfinity;
            var survivors = new List<Scheduled>(followers.Count);
            foreach (Scheduled f in followers)
            {
                Intent fi = f.Item;
                if (fi.Unit == null || fi.Unit.IsDestroyed || fi.Target == null || fi.Target.IsDestroyed)
                    continue;   // its own drop pass will deal with it
                survivors.Add(f);
                // Reuse the estimate the release path already holds where there is one; this runs in
                // a frame that is already doing removal work, and a fresh sim per survivor is not
                // worth it to order a list.
                float flightEst = f.LastFlightEst >= 0f
                    ? f.LastFlightEst
                    : FlightTime.Estimate(fi.Unit, fi.AmmoId, fi.Target);
                float enroute = flightEst + fi.ReleaseLead + fi.StartupLead + GroupDelay(fi, fi.ReleaseLead);
                if (enroute > bestEnroute) { bestEnroute = enroute; best = f; }
            }
            if (best == null) return;

            best.IsAnchor = true;
            best.AnchorShots = Mathf.Max(1, best.Item.AnchorShots);
            LauncherFactsSource.Facts f2 = LauncherFactsSource.Get(best.Item.Unit, best.Item.AmmoId);
            best.IniInterval = (f2.Valid && f2.ShotInterval > 0f) ? f2.ShotInterval : LauncherFactsSource.FallbackShotInterval;
            best.Anchor = null;
            best.HasPredict = false;
            best.LoggedAnchorWait = false;

            var newFollowers = new List<Scheduled>(survivors.Count);
            foreach (Scheduled f in survivors)
            {
                if (f == best) continue;
                f.Anchor = best;
                f.LoggedAnchorWait = false;
                newFollowers.Add(f);
            }
            best.Followers = newFollowers;

            // Carry the board rows over, minus the one whose target is gone.
            var boards = new List<ObjectBase>();
            if (lost.BoardTargets != null)
                foreach (ObjectBase t in lost.BoardTargets)
                    if (t != null && !t.IsDestroyed) boards.Add(t);
            best.BoardTargets = boards;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchor re-elected before release: {lost.Item.AmmoId} from " +
                $"{LaunchDiagnostics.SafeName(lost.Item.Unit)} is gone; " +
                $"{best.Item.AmmoId} from {LaunchDiagnostics.SafeName(best.Item.Unit)} -> " +
                $"{LaunchDiagnostics.SafeName(best.Item.Target)} takes over " +
                $"({bestEnroute:0.0}s enroute) for {newFollowers.Count} follower(s), " +
                $"impact held at sim {best.ImpactAtSim:0.0}.");
        }

        /// <summary>
        /// Re-read a held order's launch-envelope transit from the platform's CURRENT state.
        ///
        /// <see cref="PrepareIntent"/> computes <see cref="Intent.EnvelopeLead"/> once, at commit,
        /// and the release can be minutes later. A boat that was still settling at 430 ft when the
        /// strike was planned, and sat at 420 ft by the time its gate opened, was released against a
        /// 120.2 s ascent it no longer had to make: it launched 4.4 s early and its round arrived
        /// 6.0 s ahead of the rest. Scored against the depth it actually left from, the same model
        /// was off by 1.4 s, in line with every other run. The model was right and the input was
        /// stale.
        ///
        /// Refreshing is safe for the release gate in a way it would not be for the flight estimate:
        /// the shared impact time is already fixed, and this only answers "how long does this
        /// platform need FROM NOW", which is exactly the question the gate asks.
        /// </summary>
        private static void RefreshEnvelopeLead(Scheduled s, Intent it, float simNow)
        {
            if (!it.EnvelopeTracked || s.Fired) return;
            if (simNow - s.LastEnvelopeSim < EnvelopeRefreshSim) return;
            s.LastEnvelopeSim = simNow;

            float fresh = LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId);
            float delta = fresh - it.EnvelopeLead;
            if (Mathf.Approximately(delta, 0f)) return;

            it.EnvelopeLead = fresh;
            it.StartupLead = Mathf.Max(0f, it.StartupLead + delta);

            if (VerboseLog && Mathf.Abs(delta) >= EnvelopeDriftLogSeconds)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-refresh {it.AmmoId} from {LaunchDiagnostics.SafeName(it.Unit)}: " +
                    $"lead now {fresh:0.0}s ({delta:+0.0;-0.0}s vs the value at commit), " +
                    $"startupLead {it.StartupLead:0.0}s.");
        }

        /// <summary>Verbose log for a held item dropped because its shooter or target is gone.</summary>
        private static void LogDroppedItem(Scheduled s)
        {
            if (!VerboseLog) return;
            Intent it = s.Item;
            bool targetGone = it.Target == null || it.Target.IsDestroyed;
            string reason = targetGone ? "target already destroyed" : "shooter gone";
            if (s.Fired)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor {it.AmmoId} lost after launch ({reason}); " +
                    $"held orders keep the last predicted impact.");
            else
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] dropped held {it.AmmoId} from " +
                    $"{(it.Unit != null ? it.Unit.getUIDAndName() : "?")}: {reason} before release.");
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

        /// <summary>
        /// Fire a hand-picked set of missile shots, staggered so they all arrive together. The shots
        /// need not share a target: one anchor is elected across the whole set and every other shot
        /// follows it, so a strike spanning several targets lands on one time-on-target.
        /// </summary>
        internal static void FireCoordinated(List<Shot> shots)
        {
            if (shots == null || shots.Count == 0) return;

            var items = new List<Intent>(shots.Count);
            _targetScratch.Clear();
            foreach (Shot s in shots)
            {
                if (s.Target == null) continue;
                // Per TARGET, not per strike: the game's formation-attack flag means "several
                // shooters are engaging this contact together", so counting the whole strike would
                // mislabel a one-shooter target inside a multi-target strike.
                int atTarget = 0;
                foreach (Shot o in shots) if (o.Target == s.Target) atTarget++;
                var it = new Intent
                {
                    Unit = s.Unit,
                    AmmoId = s.AmmoId,
                    Target = s.Target,
                    Shots = Mathf.Max(1, s.Salvo),
                    Priority = PlannerTaskPriority,
                    IsFormation = atTarget > 1,
                };
                PrepareIntent(it);
                items.Add(it);
                if (!_targetScratch.Contains(s.Target)) _targetScratch.Add(s.Target);
            }
            if (items.Count == 0) return;

            Intent anchor = PickAnchor(items, out float maxEnroute);

            Schedule(items, GameClock.SimNow() + maxEnroute, anchor);

            string where = _targetScratch.Count == 1
                ? _targetScratch[0].getUIDAndName()
                : $"{_targetScratch.Count} targets";
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] planner firing {items.Count} order(s) at {where}: " +
                $"longest enroute {maxEnroute:0.0}s, anchor {anchor?.AmmoId} -> " +
                $"{LaunchDiagnostics.SafeName(anchor?.Target)}, impacts synced.");
            WarnOnLauncherContention(items);
        }

        /// <summary>
        /// Fire the planner's staged strike. Any orders the player also issued in-game while the
        /// strike was armed are already sitting in the armed batch, so the staged picks are folded
        /// into it and the whole lot commits as ONE strike with one anchor. With nothing armed this
        /// is just a coordinated multi-target fire of the staged picks.
        /// </summary>
        internal static void FireStrike(List<Shot> staged)
        {
            if (_strikeBatch == null) { FireCoordinated(staged); return; }

            int firstAdded = _strikeBatch.Items.Count;
            if (staged != null)
            {
                foreach (Shot s in staged)
                {
                    if (s.Unit == null || s.Target == null) continue;
                    _strikeBatch.Items.Add(new Intent
                    {
                        Unit = s.Unit,
                        AmmoId = s.AmmoId,
                        Target = s.Target,
                        Shots = Mathf.Max(1, s.Salvo),
                        Priority = PlannerTaskPriority,
                    });
                }
                // Formation-attack flag, per target and over the WHOLE batch: an intercepted order
                // and a staged pick at the same contact are one formation attack on it. Intercepted
                // items keep the flag the game itself passed.
                List<Intent> all = _strikeBatch.Items;
                for (int i = firstAdded; i < all.Count; i++)
                {
                    int atTarget = 0;
                    for (int j = 0; j < all.Count; j++)
                        if (all[j].Target == all[i].Target) atTarget++;
                    all[i].IsFormation = atTarget > 1;
                }
            }
            ExecuteStrike();
        }

        /// <summary>
        /// Warn when a shooter is being committed to more targets than its launcher can service at
        /// once. The game queues every engage task (ObjectBase.InsertEngageTask appends and re-sorts;
        /// nothing is replaced), but HandleEngageTasks will only execute a task whose weapon system
        /// is actually free, so a launcher already engaging one target cannot start another. On a
        /// non-per-container launcher, a box mount, those orders therefore leave SERIALLY and their
        /// impacts cannot be synchronised no matter what this mod schedules.
        ///
        /// Diagnostic only. The shot the player asked for is still fired.
        /// </summary>
        private static void WarnOnLauncherContention(List<Intent> items)
        {
            _contendedShooters.Clear();
            foreach (Intent it in items)
            {
                if (it.Unit == null) continue;

                // Orders already open on this shooter for this ammo, excluding the ones just added.
                int open = 0;
                for (int i = 0; i < _scheduled.Count; i++)
                {
                    Intent other = _scheduled[i].Item;
                    if (other == null || other == it) continue;
                    if (other.Unit == it.Unit && other.AmmoId == it.AmmoId && other.Target != it.Target)
                        open++;
                }
                string key = it.Unit.GetInstanceID() + "/" + it.AmmoId;
                if (open == 0) { _contentionWarned.Remove(key); continue; }

                LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
                bool parallel = f.Valid && f.PerContainer;
                if (parallel) continue;   // VLS cells cycle independently; no serialisation to warn about

                // The HUD marks these rows. Recorded before the once-per-shooter log gate below, so
                // the panel keeps showing the condition after the log has said its piece.
                _contendedShooters.Add(key);

                // Once per shooter and ammo while the contention lasts; re-warning as the player adds
                // targets says nothing new.
                if (_contentionWarned.Contains(key)) continue;
                _contentionWarned.Add(key);

                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] launcher contention: {it.Unit.getUIDAndName()} now has {open + 1} open " +
                    $"{it.AmmoId} order(s) at different targets on a non-parallel launcher. The game " +
                    $"services these one at a time, so rounds will leave serially and time-on-target " +
                    $"across these targets will not hold. Spread the shots across more shooters.");
            }
        }

        /// <summary>Fire one shot immediately, uncoordinated (used by the planner's "Fire now").</summary>
        internal static void FireNow(ObjectBase unit, string ammoId, ObjectBase target, int salvo)
        {
            if (unit == null || unit.IsDestroyed || target == null || target.IsDestroyed) return;
            InsertEngageTask_Patch.Bypass = true;
            try
            {
                unit.InsertEngageTask(ammoId, target, Vector3.zero, Mathf.Max(1, salvo), PlannerTaskPriority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: false);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] fire-now failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }
        }

        private static void Fire(Intent it, Scheduled sched)
        {
            ObjectBase unit = it.Unit;
            ObjectBase target = it.Target;

            if (unit == null || unit.IsDestroyed) return;
            if (target == null || target.IsDestroyed) return;

            InsertEngageTask_Patch.Bypass = true;
            try
            {
                unit.InsertEngageTask(it.AmmoId, target, Vector3.zero, it.Shots, it.Priority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: it.IsFormation);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] launch failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }

            EngagementBoard.MarkFired(target);
            LaunchDiagnostics.RegisterExpectation(it, sched);

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] launched {unit.getUIDAndName()} -> {target.getUIDAndName()}");
        }
    }
}
