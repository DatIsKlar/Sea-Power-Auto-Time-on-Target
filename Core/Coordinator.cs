using System;
using System.Diagnostics;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The coordination pipeline. Batches player missile orders by shared target, works out each
    /// shooter's flight time, and releases each launch late enough that the whole salvo converges
    /// on target simultaneously (Time-on-Target).
    ///
    /// Pipeline per frame (<see cref="Tick"/>, in order):
    ///   1. <c>LaunchDiagnostics.Tick</c>  — observe airborne missiles, credit launches to orders
    ///   2. <see cref="CommitReadyBatches"/> — orders quiet for the debounce window (or at the hard
    ///      cap) lock in; the longest-enroute intent becomes the batch ANCHOR
    ///   3. <see cref="UpdateAnchorTracking"/> — the anchor's REALIZED launch ripple is measured
    ///      live and rewrites the batch's shared impact time every tick (observation anchoring)
    ///   4. <see cref="ReleaseDueLaunches"/> — each held shot fires when
    ///      <c>timeLeft &lt;= liveFlightTime + releaseLead + lookahead</c>
    ///
    /// Scheduling is OPEN-LOOP: the impact time is fixed at commit (then refined by the anchor),
    /// and every held shot releases live against it. No aim-lead — missiles home themselves.
    /// See docs/ARCHITECTURE.md for the full walkthrough.
    /// </summary>
    internal static class Coordinator
    {
        // Tunables (wired to config in Bootstrap.LoadConfig).
        internal static bool Enabled = true;   // master switch (config)
        internal static bool Active = true;    // runtime toggle (hotkey / on-screen button)
        internal static float DebounceSeconds = 0.75f; // real time with no new orders => batch is complete
        internal static float MaxWindowSeconds = 6.0f; // hard cap on how long a batch stays open
        internal static bool VerboseLog = false;
        internal static bool ProfilingEnabled = false;

        private static readonly Stopwatch _tickSw = new Stopwatch();
        private static readonly Stopwatch _stageSw = new Stopwatch();
        private static int _frameCount;
        private static float _accDiagMs, _accCommitMs, _accAnchorMs, _accReleaseMs, _accTotalMs;
        private static int _accScheduled;
        private static float _accScanLoopMs, _accFinalizeMs, _accCleanupMs;
        private static float _accFlightEstimateMs, _accGroupDelayMs, _accAnchorPredictMs;
        private static float _accPredictFlightMs, _accPredictGroupDelayMs;
        private static int _accPredictFlightCalls, _accPredictGroupDelayCalls;
        private static int _accFlightCalls, _accGroupDelayCalls;
        private static float _accFlightEstimateHitMs, _accFlightEstimateMissMs;
        private static int _accFlightEstimateHits, _accFlightEstimateMisses;
        private static int _accFlightDeferred;   // proximity-gate reuses (not yet due for refresh)
        private static int _accFlightBudgetSkipped; // refresh due but deferred by the per-frame cap
        private static int _flightEstimatesThisFrame; // fresh sims run this frame (reset each ReleaseDueLaunches)

        // Timing constants for scheduling and observation anchoring.
        private const float LookaheadFraction = 0.5f;      // release lookahead, as a fraction of one sim step
        internal const float StallCadenceMultiplier = 4f;  // no launch for this many measured-cadence intervals => stall
        internal const float StallMinWindowSim = 30f;      // ...but never shorter than this (sim seconds)
        private const float NoLaunchStallSim = 120f;       // anchor fired but nothing launched for this long => stall
        internal const int PlannerTaskPriority = 1000;     // task priority for planner-issued orders
        private const float NegligibleLeadSeconds = 0.1f;  // release leads below this aren't worth logging
        private const float SlackWarnSeconds = 5f;         // overshoot beyond this at release => WARN (stagger-loss guard)
        private const float FlightRefreshSim = 2f;         // max sim-time staleness of a reused far-from-release flight estimate
        private const float FlightRefreshNearSim = 0.3f;   // faster refresh cadence once an item is near its release gate
        private const float FlightGateMargin = 3f;         // within this much slack of release => treat as near-release
        private const int MaxFlightEstimatesPerFrame = 12; // per-frame ceiling on fresh kinematic sims (bounds worst-frame cost)
        private const int ProfilingReportIntervalFrames = 60;

        /// <summary>
        /// One player missile order held for coordinated release. Orders stay WHOLE — each is
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

        private sealed class Batch
        {
            public ObjectBase Target;
            public float FirstRealTime;
            public float LastRealTime;
            public readonly List<Intent> Items = new List<Intent>();
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
            // --- Observation anchoring (grouped-salvo convergence) ---
            public bool IsAnchor;       // longest-enroute item of its batch; released first, its real
                                         // launches define the batch's shared impact time
            public bool Fired;          // anchor released; its launch ripple is being observed
            public bool RippleDone;     // impact finalized (wave-1 ripple complete or launches stalled)
            public int AnchorShots;     // launches anchoring keys on (first wave)
            public float IniInterval;   // a-priori per-round interval (seed until 2+ launches observed)
            public float PredictedImpact;
            public readonly List<float> LaunchTimes = new List<float>(); // observed launch times (sim s)
            public int LastLoggedLaunches = -1;
            public float FiredAtSim = -1f;
            // Per-item flight-estimate cache (proximity gate): far-from-release items reuse a
            // prior FlightTime.Estimate on a sim-time cadence instead of re-running the kinematic
            // sim every frame. Refreshed when stale or near release. Always a real Estimate output,
            // so it never diverges from the commit path.
            public float LastFlightEst = -1f;
            public float LastFlightEstSim = float.NegativeInfinity;
        }

        /// <summary>The batch's anchor and held items (fired anchors stay in until their ripple finalizes).</summary>
        internal static IReadOnlyList<Scheduled> ScheduledItems => _scheduled;

        // Open batches, keyed by the shared target.
        private static readonly Dictionary<ObjectBase, Batch> _openBatches = new Dictionary<ObjectBase, Batch>();
        private static readonly List<Scheduled> _scheduled = new List<Scheduled>();

        // Cache for PredictAnchorImpact to avoid recomputing every frame. Keyed on the ripple
        // state (observed launches k, measured cadence) with a sim-time TTL on top: while the
        // ripple state holds, the prediction is reused until the TTL expires, then re-run so the
        // live flight estimate picks up shooter/target motion between launches.
        private struct PredictKey : IEquatable<PredictKey>
        {
            public int Launches;        // observed launch count k
            public int IntervalMilli;   // measured cadence (milliseconds)
            public bool Equals(PredictKey o) => Launches == o.Launches && IntervalMilli == o.IntervalMilli;
            public override bool Equals(object obj) => obj is PredictKey k && Equals(k);
            public override int GetHashCode() { unchecked { return (Launches * 397) ^ IntervalMilli; } }
        }

        private struct PredictCacheEntry
        {
            public PredictKey Key;
            public float StampSim;
            public float Value;
        }

        private static readonly Dictionary<Scheduled, PredictCacheEntry> _predictCache = new Dictionary<Scheduled, PredictCacheEntry>();
        private static readonly Dictionary<Scheduled, float> _groupDelayCache = new Dictionary<Scheduled, float>();
        private const float PredictCacheTtlSim = 0.5f;

        // Anchor -> followers index for O(followers) propagation instead of O(all items)
        private static readonly Dictionary<Scheduled, List<Scheduled>> _anchorFollowers = new Dictionary<Scheduled, List<Scheduled>>();

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

            if (!_openBatches.TryGetValue(target, out Batch batch))
            {
                batch = new Batch { Target = target, FirstRealTime = Time.unscaledTime };
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

        /// <summary>Clears all coordinator state. Called on mission end to prevent stale data.</summary>
        internal static void Reset()
        {
            _openBatches.Clear();
            _scheduled.Clear();
            _predictCache.Clear();
            _groupDelayCache.Clear();
            _anchorFollowers.Clear();
            FlightTime.ClearCache();
            LauncherFactsSource.ClearCache();
            LaunchDiagnostics.Reset();
            EngagementBoard.Clear();
            _lastReleaseSimNow = -1f;
            ResetProfiling();
            if (VerboseLog) Bootstrap.Log.LogInfo("[AutoTOT] coordinator state reset.");
        }

        /// <summary>Pumped every frame from Bootstrap.Pump (only inside a mission).</summary>
        internal static void Tick()
        {
            float simNow = GameClock.SimNow();
            if (ProfilingEnabled)
            {
                _tickSw.Restart();

                _stageSw.Restart();
                LaunchDiagnostics.Tick(simNow);
                _stageSw.Stop();
                _accDiagMs += (float)_stageSw.Elapsed.TotalMilliseconds;
                _accScanLoopMs += LaunchDiagnostics.LastScanLoopMs;
                _accFinalizeMs += LaunchDiagnostics.LastFinalizeMs;
                _accCleanupMs += LaunchDiagnostics.LastCleanupMs;

                _stageSw.Restart();
                CommitReadyBatches();
                _stageSw.Stop();
                _accCommitMs += (float)_stageSw.Elapsed.TotalMilliseconds;

                _stageSw.Restart();
                UpdateAnchorTracking(simNow);
                _stageSw.Stop();
                _accAnchorMs += (float)_stageSw.Elapsed.TotalMilliseconds;

                _stageSw.Restart();
                ReleaseDueLaunches(simNow);
                _stageSw.Stop();
                _accReleaseMs += (float)_stageSw.Elapsed.TotalMilliseconds;

                _tickSw.Stop();
                _accTotalMs += (float)_tickSw.Elapsed.TotalMilliseconds;
                _accScheduled += _scheduled.Count;
                _frameCount++;

                if (_frameCount >= ProfilingReportIntervalFrames)
                {
                    float inv = 1f / _frameCount;
                    long totalHits = FlightTime.TofHits + FlightTime.ProfileHits + LauncherFactsSource.CacheHits;
                    long totalMisses = FlightTime.TofMisses + FlightTime.ProfileMisses + LauncherFactsSource.CacheMisses;
                    long total = totalHits + totalMisses;
                    float hitRate = total > 0 ? (100f * totalHits / total) : 0f;

                    float avgHitMs = _accFlightEstimateHits > 0 ? _accFlightEstimateHitMs / _accFlightEstimateHits : 0f;
                    float avgMissMs = _accFlightEstimateMisses > 0 ? _accFlightEstimateMissMs / _accFlightEstimateMisses : 0f;
                    float unaccountedMs = _accFlightEstimateMs - _accFlightEstimateHitMs - _accFlightEstimateMissMs;

                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT Profiling] avg tick {_accTotalMs * inv:F2}ms (over {_frameCount} frames)\n" +
                        $"  Diag {_accDiagMs * inv:F2}ms: scan {_accScanLoopMs * inv:F2}ms | finalize {_accFinalizeMs * inv:F2}ms | cleanup {_accCleanupMs * inv:F2}ms (weapons {LaunchDiagnostics.LastWeaponCount}, tracked {LaunchDiagnostics.LastTrackedMissiles})\n" +
                        $"  Commit {_accCommitMs * inv:F2}ms | Anchor {_accAnchorMs * inv:F2}ms (PredictAnchorImpact {_accAnchorPredictMs * inv:F2}ms) | Release {_accReleaseMs * inv:F2}ms (avg sched {_accScheduled * inv:F1})\n" +
                        $"    -> FlightTime.Estimate: {_accFlightEstimateMs * inv:F2}ms ({_accFlightCalls / _frameCount:F1} calls/frame)\n" +
                        $"       hits: {_accFlightEstimateHits / _frameCount:F1}/frame @ {avgHitMs:F3}ms avg ({_accFlightEstimateHitMs * inv:F2}ms total)\n" +
                        $"       misses: {_accFlightEstimateMisses / _frameCount:F1}/frame @ {avgMissMs:F3}ms avg ({_accFlightEstimateMissMs * inv:F2}ms total)\n" +
                        $"       unaccounted: {unaccountedMs * inv:F2}ms/frame\n" +
                        $"       deferred (proximity gate): {_accFlightDeferred / _frameCount:F1}/frame\n" +
                        $"       budget-skipped: {_accFlightBudgetSkipped / _frameCount:F1}/frame\n" +
                        $"    -> GroupDelay: {_accGroupDelayMs * inv:F2}ms ({_accGroupDelayCalls / _frameCount:F1} calls/frame)\n" +
                        $"    -> PredictAnchorImpact: FlightEst {_accPredictFlightMs * inv:F2}ms ({_accPredictFlightCalls / _frameCount:F1} calls) | GroupDelay {_accPredictGroupDelayMs * inv:F2}ms ({_accPredictGroupDelayCalls / _frameCount:F1} calls)\n" +
                        $"  Cache {hitRate:F0}% hit ({totalHits}/{total}) [ToT {FlightTime.TofHits}/{FlightTime.TofMisses} h/m, sz {FlightTime.TofCacheSize}, evTtl {FlightTime.TofEvictionsTtl}, evCap {FlightTime.TofEvictionsCapacity}] [Profile {FlightTime.ProfileHits}/{FlightTime.ProfileMisses} h/m, sz {FlightTime.ProfileCacheSize}, evTtl {FlightTime.ProfileEvictionsTtl}, evCap {FlightTime.ProfileEvictionsCapacity}] [Facts {LauncherFactsSource.CacheHits}/{LauncherFactsSource.CacheMisses} h/m, sz {LauncherFactsSource.CacheSize}]");

                    ResetProfiling();
                }
            }
            else
            {
                LaunchDiagnostics.Tick(simNow);
                CommitReadyBatches();
                UpdateAnchorTracking(simNow);
                ReleaseDueLaunches(simNow);
            }
        }

        private static void ResetProfiling()
        {
            _frameCount = 0;
            _accDiagMs = _accCommitMs = _accAnchorMs = _accReleaseMs = _accTotalMs = 0f;
            _accScheduled = 0;
            _accScanLoopMs = _accFinalizeMs = _accCleanupMs = 0f;
            _accFlightEstimateMs = _accGroupDelayMs = _accAnchorPredictMs = 0f;
            _accFlightCalls = _accGroupDelayCalls = 0;
            _accPredictFlightMs = _accPredictGroupDelayMs = 0f;
            _accPredictFlightCalls = _accPredictGroupDelayCalls = 0;
            _accFlightEstimateHitMs = _accFlightEstimateMissMs = 0f;
            _accFlightEstimateHits = _accFlightEstimateMisses = 0;
            _accFlightDeferred = 0;
            _accFlightBudgetSkipped = 0;
            FlightTime.ResetStats();
            LauncherFactsSource.ResetStats();
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
        private static Intent PickAnchor(List<Intent> items, ObjectBase target, out float maxEnroute)
        {
            maxEnroute = 0f;
            Intent anchor = null;
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                // Components of EnrouteWithLead, kept separate so the commit line below can show the
                // firing-decision flight estimate (FlightTime.Estimate is 0.5s-TTL cached — this is a
                // hit; GroupDelay is computed once per commit here regardless).
                float flightEst = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                float groupDelay = GroupDelay(it, it.ReleaseLead);
                float needed = flightEst + it.ReleaseLead + it.StartupLead + groupDelay;
                if (needed > maxEnroute) { maxEnroute = needed; anchor = it; }
                // One line per shot at the moment its firing timing is locked in: the estimate that
                // DROVE the decision. Pairs with the `gap` line at impact (simEst vs actual) so the
                // firing-sim's accuracy is verifiable without the per-frame planning spam.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] commit {it.AmmoId} from {it.Unit.getUIDAndName()} -> " +
                        $"{target?.getUIDAndName()}: flightEst {flightEst:0.0}s, " +
                        $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                        $"groupDelay {groupDelay:0.0}s, enroute {needed:0.0}s");
            }
            return anchor;
        }

        private static void CommitBatch(Batch b)
        {
            foreach (Intent it in b.Items)
                PrepareIntent(it);

            Intent anchor = PickAnchor(b.Items, b.Target, out float maxEnroute);
            Schedule(b.Items, GameClock.SimNow() + maxEnroute, anchor);

            if (VerboseLog || b.Items.Count > 1)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] coordinating {b.Items.Count} missile order(s) on {b.Target?.getUIDAndName()}: " +
                    $"longest enroute {maxEnroute:0.0}s, anchor {anchor?.AmmoId}, impacts synced.");
            }
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
        /// ship's launcher facts. Keeps the intent whole — the order is fired as one
        /// InsertEngageTask(shotsToFire=N), matching the game's own UI path.
        /// </summary>
        private static void PrepareIntent(Intent it)
        {
            it.ReleaseLead = 0f; it.StartupLead = 0f; it.Grouped = false; it.Waves = 1; it.WaveGap = 0f;

            int n = Mathf.Max(1, it.Shots);
            it.AnchorShots = n;

            LauncherFactsSource.Facts fFacts = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            // Fixed fire-to-first-launch offset applies to EVERY order (even a single shot): the
            // engage cycle pays PreLaunchDelay + the expected reaction draw before round 1 leaves.
            if (fFacts.Valid) it.StartupLead = fFacts.StartupDelay;

            if (n <= 1) return;

            LauncherFactsSource.Facts f = fFacts;
            if (!f.Valid || f.ShotInterval <= 0f) return;

            // Ready rounds before a reload; 0 means "unknown", so treat the whole order as one wave.
            int x = (f.ReadyRounds > 0) ? f.ReadyRounds : n;
            int wave1 = Mathf.Min(n, x);
            it.AnchorShots = wave1;   // anchoring keys on wave 1; later waves arrive separately

            // Release-lead math — the launcher ripples the N rounds over (wave1-1)*interval seconds,
            // and the release lead is how much BEFORE the coordinated impact that ripple must start:
            //
            //  - GROUPED missiles (GroupSize>1, e.g. SS-N-12/19) fly a formation and cash in
            //    together: the group's convergent impact lands when the LAST round's lone flight
            //    ends (see UpdateAnchorTracking for why), i.e. at the ripple's TRAILING edge.
            //    Lead = the FULL ripple span, so that trailing edge lands on the TOT.
            //  - INDEPENDENT salvos arrive spread out over the ripple, so lead = HALF the span,
            //    which CENTERS the arrival distribution on the TOT.
            //
            // (The group flag comes from the ammo's own params, so modded group missiles are
            // covered too.)
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

        private static void Schedule(IEnumerable<Intent> items, float baseImpact, Intent anchorItem)
        {
            ObjectBase target = null;
            float maxLead = 0f;
            float maxSpread = 0f;
            int maxWaves = 1;
            float waveGap = 0f;

            var added = new List<Scheduled>();
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
                if (it.Target != null)
                {
                    target = it.Target;
                    if (it.ReleaseLead > maxLead) maxLead = it.ReleaseLead;
                    // Arrival spread readout: a grouped order's full-span lead is NOT its arrival
                    // spread (the group lands tight), so it contributes ~0 to the ±Ns display.
                    if (!it.Grouped && it.ReleaseLead > maxSpread) maxSpread = it.ReleaseLead;
                    if (it.Waves > maxWaves) { maxWaves = it.Waves; waveGap = it.WaveGap; }
                }
            }
            if (anchorSched != null)
            {
                var followers = new List<Scheduled>();
                foreach (Scheduled s in added)
                {
                    if (!s.IsAnchor)
                        followers.Add(s);
                }
                _anchorFollowers[anchorSched] = followers;
            }

            if (target != null)
            {
                EngagementBoard.RecordScheduled(target, baseImpact, maxSpread, maxWaves, waveGap);
                if (VerboseLog && (maxLead > NegligibleLeadSeconds || maxWaves > 1))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] scheduled {target.getUIDAndName()}: ±{maxLead:0.0}s, {maxWaves} wave(s)");
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
            float est;
            if (ProfilingEnabled) { _stageSw.Restart(); est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target); _stageSw.Stop();
                _accPredictFlightMs += (float)_stageSw.Elapsed.TotalMilliseconds; _accPredictFlightCalls++; }
            else est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
            
            if (k <= 0 || est <= FlightTime.MinValidSeconds) return a.ImpactAtSim;

            float lastLaunch = a.LaunchTimes[k - 1];
            float lastRoundLaunch = (k >= n) ? lastLaunch : lastLaunch + interval * (n - k);
            float span = lastRoundLaunch - a.LaunchTimes[0];
            
            float groupDelay;
            if (ProfilingEnabled) { _stageSw.Restart(); groupDelay = GroupDelay(it, span); _stageSw.Stop();
                _accPredictGroupDelayMs += (float)_stageSw.Elapsed.TotalMilliseconds; _accPredictGroupDelayCalls++; }
            else groupDelay = GroupDelay(it, span);
            
            return lastRoundLaunch + est - (it.Grouped ? 0f : it.ReleaseLead) + groupDelay;
        }

        /// <summary>
        /// Observation anchoring. The batch anchor (longest enroute incl. span) was released first;
        /// here we watch its ACTUAL launches, extrapolate the launcher's live cadence, and keep
        /// rewriting the shared impact time every held order is released against.
        ///
        /// WHY the anchor's last launch predicts the group's impact: a grouped salvo's convergent
        /// impact lands when its LAST round's solo flight ends (MissileGroup.cs:106-141 —
        /// AdjustMembersVelocities applies symmetric ±40% speed clamps, so the farthest trailer
        /// flies at exactly solo speed until it closes up; then the group cashes in together,
        /// Missile.cs:839-842). No cadence constant exists anywhere: the realized cadence of many
        /// launchers is produced by machinery no INI declares (per-cell hatch animations,
        /// engage-task reassignment), so it is measured from the game's own launch timing instead
        /// of predicted.
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
        /// (shortfall / gating) — held orders keep whatever prediction was last written.
        /// </summary>
        private static void UpdateAnchorTracking(float simNow)
        {
            if (_scheduled.Count == 0) return;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled a = _scheduled[i];
                if (!a.IsAnchor || !a.Fired || a.RippleDone) continue;
                Intent it = a.Item;
                // Dead unit/target: ReleaseDueLaunches drops the entry; held orders keep the last
                // prediction already written into their ImpactAtSim.
                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed) continue;

                int k = a.LaunchTimes.Count;
                int n = Mathf.Max(1, a.AnchorShots);

                // Live cadence: measured once 2+ launches are in; the INI interval seeds k<=1.
                float interval = a.IniInterval;
                if (k >= 2) interval = (a.LaunchTimes[k - 1] - a.LaunchTimes[0]) / (k - 1);
                if (interval <= 0f) interval = a.IniInterval > 0f ? a.IniInterval : LauncherFactsSource.FallbackShotInterval;

                // Prediction cache: reuse while the ripple state (k, interval) is unchanged AND
                // the entry is fresh within the sim TTL. An expired entry re-runs
                // PredictAnchorImpact so the live flight estimate tracks shooter/target motion
                // between launches. (An earlier int-mixed key collided once cadence >= 10 s and
                // had no TTL at all, freezing the prediction between launches.)
                PredictKey cacheKey = new PredictKey { Launches = k, IntervalMilli = Mathf.RoundToInt(interval * 1000f) };
                float pred;
                if (_predictCache.TryGetValue(a, out PredictCacheEntry cached) &&
                    cached.Key.Equals(cacheKey) && (simNow - cached.StampSim) < PredictCacheTtlSim)
                {
                    pred = cached.Value;
                }
                else
                {
                    if (ProfilingEnabled) { _stageSw.Restart(); pred = PredictAnchorImpact(a, it, k, n, interval); _stageSw.Stop();
                        _accAnchorPredictMs += (float)_stageSw.Elapsed.TotalMilliseconds; }
                    else pred = PredictAnchorImpact(a, it, k, n, interval);
                    _predictCache[a] = new PredictCacheEntry { Key = cacheKey, StampSim = simNow, Value = pred };
                }
                a.PredictedImpact = pred;

                // Held orders in this batch follow the live prediction; their release condition is
                // re-evaluated against it every tick. Uses anchor->followers index for O(followers).
                if (_anchorFollowers.TryGetValue(a, out List<Scheduled> followers))
                {
                    for (int j = 0; j < followers.Count; j++)
                        followers[j].ImpactAtSim = pred;
                }
                EngagementBoard.UpdateImpact(it.Target, pred);

                bool complete = k >= n;
                // Stall = launches stopped (gated/short), or NOTHING launched for a long while
                // (launcher inoperable, guidance wait, ship still turning into its firing arc).
                bool stalled = k > 0
                    ? (simNow - a.LaunchTimes[k - 1]) > Mathf.Max(StallCadenceMultiplier * interval, StallMinWindowSim)
                    : a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchStallSim;
                if (complete || stalled)
                {
                    a.RippleDone = true;
                    _predictCache.Remove(a);
                    _anchorFollowers.Remove(a);
                    _groupDelayCache.Remove(a);
                    _scheduled.RemoveAt(i);
                    float span = (k > 1) ? a.LaunchTimes[k - 1] - a.LaunchTimes[0] : 0f;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchored {LaunchDiagnostics.SafeName(it.Target)}: {k}/{n} launched over {span:0.0}s " +
                        $"(cadence {interval:0.0}s), impact set to sim {pred:0.0}" +
                        (stalled && !complete ? " — ripple stalled, anchored on launches observed" : ""));

                    // Diagnostic: the range-aware τ_form model's internals (now the LIVE model, so
                    // `candidate` == `applied groupDelay`). Kept for sanity-checking modded/untested
                    // missiles — watch that `candidate` tracks the observed residual.
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
                else if (VerboseLog && k != a.LastLoggedLaunches)
                {
                    a.LastLoggedLaunches = k;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchoring {LaunchDiagnostics.SafeName(it.Target)}: {k}/{n} launched, " +
                        $"cadence {interval:0.0}s, impact predicted sim {pred:0.0}");
                }
            }
        }

        private static float _lastReleaseSimNow = -1f;

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
            _flightEstimatesThisFrame = 0; // reset the per-frame fresh-estimate budget

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled s = _scheduled[i];
                Intent it = s.Item;

                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                {
                    _groupDelayCache.Remove(s);
                    _predictCache.Remove(s);          // fired-anchor ripple entries, if any
                    _anchorFollowers.Remove(s);
                    _scheduled.RemoveAt(i);
                    // This item never fires, so its target may never get a fired row — which is
                    // what the engagement board's prune loop keys off. Drop the board row now if
                    // no other still-scheduled item shares this target, else it leaks until the
                    // next mission Reset().
                    DropImpactDataIfUnscheduled(it.Target);
                    LogDroppedItem(s);
                    continue;
                }

                float timeLeft = s.ImpactAtSim - simNow;

                // Use cached GroupDelay if available (independent of the flight estimate; the
                // proximity gate below needs it, so resolve it first).
                float groupDelay;
                if (!_groupDelayCache.TryGetValue(s, out groupDelay))
                {
                    if (ProfilingEnabled) { _stageSw.Restart(); groupDelay = GroupDelay(it, it.ReleaseLead); _stageSw.Stop();
                        _accGroupDelayMs += (float)_stageSw.Elapsed.TotalMilliseconds; }
                    else groupDelay = GroupDelay(it, it.ReleaseLead);
                    _groupDelayCache[s] = groupDelay;
                }
                if (ProfilingEnabled) _accGroupDelayCalls++;

                // Release must use the SAME estimator the commit path (EnrouteWithLead) scheduled
                // the impact against, so positive slack exists and the per-item launch stagger
                // engages. FlightTime.Estimate carries its own real-0.5 s TTL cache shared with the
                // commit path. (An earlier optimization added a separate sim-time cache + a
                // straight-line "out of max range" pre-filter here; the pre-filter substituted
                // distance/maxVelocity and overestimated slow long-range shots by ~120 s vs the
                // kinematic sim, driving slack negative so whole batches dumped on one tick.)
                //
                // Proximity gate: most scheduled items are minutes from release, yet re-running the
                // kinematic sim for all of them every frame dominated the tick (~536ms/435 items).
                // Reuse a prior Estimate for far items on a sim-time cadence; always recompute fresh
                // near release, so the launch decision uses a current value. The reused value is
                // always a real FlightTime.Estimate output, so it cannot diverge from the commit
                // path the way the removed pre-filter did. Using sim-time staleness (not real time)
                // also breaks the feedback loop where slow ticks expired FlightTime's real-0.5 s TTL
                // every frame and forced a full recompute.
                // Near-release items refresh on a short sim cadence (FlightRefreshNearSim); far
                // items on a long one (FlightRefreshSim). Time-on-target bunches many items into
                // the near-release window at once, so recomputing each every frame was still the
                // bulk of the cost — the estimate creeps slowly vs a multi-minute flight, so a
                // sub-second cadence is plenty accurate for a launch decision.
                float releaseGate = it.ReleaseLead + it.StartupLead + groupDelay + lookahead;
                bool nearRelease = s.LastFlightEst >= 0f &&
                                   timeLeft <= s.LastFlightEst + releaseGate + FlightGateMargin;
                float refreshCadence = nearRelease ? FlightRefreshNearSim : FlightRefreshSim;
                bool due = s.LastFlightEst < 0f || simNow - s.LastFlightEstSim >= refreshCadence;
                // Per-frame ceiling on fresh kinematic sims: a synchronized wave can bunch many
                // due refreshes into one frame. Cap them; an item that already has a value just
                // reuses it a frame longer. An item with NO estimate always computes (correctness
                // before budget) — those are first-sight only and rare.
                bool budgeted = s.LastFlightEst < 0f || _flightEstimatesThisFrame < MaxFlightEstimatesPerFrame;
                float flightNow;
                if (due && budgeted)
                {
                    _flightEstimatesThisFrame++;
                    if (ProfilingEnabled)
                    {
                        _stageSw.Restart();
                        flightNow = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                        _stageSw.Stop();
                        float callMs = (float)_stageSw.Elapsed.TotalMilliseconds;
                        _accFlightEstimateMs += callMs;
                        _accFlightCalls++;
                        if (FlightTime.WasLastCallCacheHit)
                        {
                            _accFlightEstimateHits++;
                            _accFlightEstimateHitMs += callMs;
                        }
                        else
                        {
                            _accFlightEstimateMisses++;
                            _accFlightEstimateMissMs += callMs;
                        }
                    }
                    else flightNow = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    s.LastFlightEst = flightNow;
                    s.LastFlightEstSim = simNow;
                }
                else
                {
                    flightNow = s.LastFlightEst;
                    if (ProfilingEnabled)
                    {
                        if (due) _accFlightBudgetSkipped++;
                        else _accFlightDeferred++;
                    }
                }
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
                        _groupDelayCache.Remove(s);
                        _scheduled.RemoveAt(i);
                    }
                    // Regression guardrail: large positive overshoot means the item is released
                    // well past-due — it cannot make its scheduled impact, and every item in the
                    // batch will have collapsed onto this tick (no launch stagger). That is the
                    // signature of the release estimate diverging from the impact the commit path
                    // scheduled (see PROJECT_MEMORY.md, "Commit/release estimator consistency").
                    // Logged once per item (release removes/marks it), independent of VerboseLog.
                    float overshoot = flightNow - timeLeft;
                    if (overshoot > SlackWarnSeconds)
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] {it.AmmoId} from {it.Unit.getUIDAndName()} released {overshoot:0.0}s " +
                            $"past-due (est flight {flightNow:0.0}s > time-to-impact {timeLeft:0.0}s) — " +
                            $"launch stagger lost; commit/release flight estimates likely diverged.");
                    if (VerboseLog)
                    {
                        AmmunitionParameters ap = it.Unit.getAmmunitionByName(it.AmmoId)?._ap;
                        float kin = (ap != null) ? FlightTime.Kinematic(it.Unit, ap, it.Target) : -1f;
                        string src = (kin > FlightTime.MinValidSeconds) ? "kinematic" : "straight-line fallback";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] launch {it.AmmoId} from {it.Unit.getUIDAndName()}" +
                            $"{(s.IsAnchor ? " (anchor)" : "")}: " +
                            $"est flight {flightNow:0.0}s ({src}), " +
                            $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                            $"groupDelay {groupDelay:0.0}s, " +
                            $"impactAt {s.ImpactAtSim:0.0}, now {simNow:0.0}, " +
                            $"simStep {simStep:0.0}s, overshoot {overshoot:0.0}s");
                    }
                    Fire(it, s.IsAnchor ? s : null);
                }
            }
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

        // ---- Explicit fire from the planner panel ----

        /// <summary>One hand-picked shot from the planner: a shooter, an ammo type, a salvo size.</summary>
        internal struct Shot
        {
            public ObjectBase Unit;
            public string AmmoId;
            public int Salvo;
        }

        /// <summary>
        /// Fire a hand-picked set of missile shots at one target, staggered so they arrive together.
        /// Returns the longest flight time in the group (seconds), for UI feedback.
        /// </summary>
        internal static float FireCoordinated(List<Shot> shots, ObjectBase target)
        {
            if (shots == null || shots.Count == 0 || target == null) return 0f;

            bool multi = shots.Count > 1;
            var items = new List<Intent>(shots.Count);
            foreach (Shot s in shots)
            {
                var it = new Intent
                {
                    Unit = s.Unit,
                    AmmoId = s.AmmoId,
                    Target = target,
                    Shots = Mathf.Max(1, s.Salvo),
                    Priority = PlannerTaskPriority,
                    IsFormation = multi,
                };
                PrepareIntent(it);
                items.Add(it);
            }

            Intent anchor = PickAnchor(items, target, out float maxEnroute);

            Schedule(items, GameClock.SimNow() + maxEnroute, anchor);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] planner firing {items.Count} order(s) at {target.getUIDAndName()}: " +
                $"longest enroute {maxEnroute:0.0}s, impacts synced.");
            return maxEnroute;
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
