using System.Diagnostics;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Per-frame timing for the coordinator, reported every 60 frames when the profiling config key
    /// is on. Off by default and costs one bool test per call when off.
    ///
    /// Kept out of Coordinator so a measured call is written once. The instrumentation used to wrap
    /// each call in `if (ProfilingEnabled) { time it } else { call it }`, which meant every measured
    /// call appeared twice and could drift between the two copies.
    /// </summary>
    internal static class Profiler
    {
        /// <summary>Timed sections. Nesting is fine: Tick wraps the four stage entries.</summary>
        internal enum Stage
        {
            Tick, Diag, Commit, Anchor, Release,
            FlightEstimate, GroupDelay, AnchorPredict, PredictFlight, PredictGroupDelay,
            // Model runs driven by the HUD, which happen in OnGUI and are therefore OUTSIDE the tick
            // entirely. Reported separately so they cannot be mistaken for tick cost, or hidden by it.
            UiEstimate,
        }

        /// <summary>Per-frame tallies that are counted rather than timed.</summary>
        internal enum Counter
        {
            FlightCalls, FlightHits, FlightMisses, FlightDeferred, FlightBudgetSkipped, FlightQueued,
            GroupDelayCalls, PredictFlightCalls, PredictGroupDelayCalls, UiEstimateCalls,
        }

        internal static bool Enabled;

        private const int ReportIntervalFrames = 60;
        private static readonly int StageCount = System.Enum.GetValues(typeof(Stage)).Length;
        private static readonly int CounterCount = System.Enum.GetValues(typeof(Counter)).Length;

        // Raw timestamps rather than Stopwatch instances: no allocation, and each stage keeps its own
        // start so a nested section does not disturb the one enclosing it.
        private static readonly long[] _startTicks = new long[StageCount];
        private static readonly double[] _accMs = new double[StageCount];
        private static readonly int[] _counts = new int[CounterCount];
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static int _frameCount;
        private static float _accScanLoopMs, _accFinalizeMs, _accCleanupMs;
        private static int _accScheduled;
        // Split of FlightEstimate time by cache outcome. Timed here rather than counted, because the
        // cost of a hit and a miss differ by orders of magnitude and the average of the two is not
        // a useful number on its own.
        private static double _accFlightHitMs, _accFlightMissMs;
        private static double _lastFlightCallMs;
        // Worst single frame in the window, and what the release stage cost on it. An average hides
        // the spike that actually drops a frame: 60 quiet ticks and one 20ms tick average to 0.33ms.
        private static double _maxTickMs, _maxTickReleaseMs;
        private static double _frameTickMs, _frameReleaseMs;
        // Real frame time, so the tick figure can be read as a SHARE of a frame. Without it, "21.7ms"
        // is unanswerable: it could be a third of a frame or more than all of one, and the per-frame
        // sim cap cannot be set sensibly on any machine but the one it was tuned on.
        private static double _accFrameMs, _maxFrameMs;
        // How stale the flight estimate was, in sim seconds, at the moment an order actually fired.
        // The per-frame cap trades freshness for frame time; this is what that trade costs.
        private static double _accReleaseStaleness, _maxReleaseStaleness;
        private static int _releaseCount;

        internal static void Begin(Stage s)
        {
            if (!Enabled) return;
            _startTicks[(int)s] = Stopwatch.GetTimestamp();
        }

        internal static void End(Stage s)
        {
            if (!Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - _startTicks[(int)s]) * MsPerTick;
            _accMs[(int)s] += ms;
            if (s == Stage.FlightEstimate) _lastFlightCallMs = ms;
            else if (s == Stage.Release) _frameReleaseMs = ms;
            else if (s == Stage.Tick) _frameTickMs = ms;
        }

        internal static void Count(Counter c)
        {
            if (!Enabled) return;
            _counts[(int)c]++;
        }

        /// <summary>
        /// Attribute the flight estimate just timed by <see cref="Stage.FlightEstimate"/> to the
        /// cache hit or miss bucket. Call directly after the matching End.
        /// </summary>
        /// <summary>
        /// Record a cache hit that was NOT wrapped in a Begin/End pair, because it was served from
        /// the cache without running the estimator at all.
        ///
        /// Separate from <see cref="CountEstimate"/> on purpose: that one attributes the duration of
        /// the call just timed, and calling it without a matching Begin/End charges the PREVIOUS
        /// call's time to this one. That read as 792 hits costing 1.3 ms each with a negative
        /// unaccounted total, which is how it was found.
        /// </summary>
        internal static void CountCachedHit()
        {
            if (!Enabled) return;
            _counts[(int)Counter.FlightCalls]++;
            _counts[(int)Counter.FlightHits]++;
        }

        internal static void CountEstimate(bool cacheHit)
        {
            if (!Enabled) return;
            _counts[(int)Counter.FlightCalls]++;
            if (cacheHit) { _counts[(int)Counter.FlightHits]++; _accFlightHitMs += _lastFlightCallMs; }
            else { _counts[(int)Counter.FlightMisses]++; _accFlightMissMs += _lastFlightCallMs; }
        }

        /// <summary>
        /// Record how old the flight estimate was when an order was released, in sim seconds. Zero
        /// means it was computed on this very frame.
        /// </summary>
        internal static void ReleaseStaleness(float ageSim)
        {
            if (!Enabled || ageSim < 0f) return;
            _releaseCount++;
            _accReleaseStaleness += ageSim;
            if (ageSim > _maxReleaseStaleness) _maxReleaseStaleness = ageSim;
        }

        /// <summary>Sub-phase timings measured inside LaunchDiagnostics and reported here.</summary>
        internal static void AddDiagPhases(float scanLoopMs, float finalizeMs, float cleanupMs)
        {
            if (!Enabled) return;
            _accScanLoopMs += scanLoopMs;
            _accFinalizeMs += finalizeMs;
            _accCleanupMs += cleanupMs;
        }

        /// <summary>Close the frame, and emit the report once every <see cref="ReportIntervalFrames"/>.</summary>
        internal static void FrameDone(int scheduledCount)
        {
            if (!Enabled) return;
            _accScheduled += scheduledCount;
            _frameCount++;
            double frameMs = Time.unscaledDeltaTime * 1000.0;
            _accFrameMs += frameMs;
            if (frameMs > _maxFrameMs) _maxFrameMs = frameMs;
            if (_frameTickMs > _maxTickMs) { _maxTickMs = _frameTickMs; _maxTickReleaseMs = _frameReleaseMs; }
            _frameTickMs = _frameReleaseMs = 0d;
            if (_frameCount < ReportIntervalFrames) return;
            Report();
            Reset();
        }

        private static double Ms(Stage s) => _accMs[(int)s];
        private static int N(Counter c) => _counts[(int)c];

        private static void Report()
        {
            float inv = 1f / _frameCount;
            long totalHits = FlightTime.TofHits + FlightTime.ProfileHits + LauncherFactsSource.CacheHits;
            long totalMisses = FlightTime.TofMisses + FlightTime.ProfileMisses + LauncherFactsSource.CacheMisses;
            long total = totalHits + totalMisses;
            float hitRate = total > 0 ? (100f * totalHits / total) : 0f;

            double avgHitMs = N(Counter.FlightHits) > 0 ? _accFlightHitMs / N(Counter.FlightHits) : 0d;
            double avgMissMs = N(Counter.FlightMisses) > 0 ? _accFlightMissMs / N(Counter.FlightMisses) : 0d;
            double unaccountedMs = Ms(Stage.FlightEstimate) - _accFlightHitMs - _accFlightMissMs;

            long stepsPerSim = ModelStats.Sims > 0 ? ModelStats.Steps / ModelStats.Sims : 0;
            // Microseconds per thousand steps: the number to watch when changing anything inside the
            // integration loop, and the only figure here that is comparable between builds.
            double usPerKStep = ModelStats.Steps > 0 ? ModelStats.LoopMs * 1e6 / ModelStats.Steps : 0d;

            // Window TOTALS lead, per-frame averages follow in parentheses. This work arrives in rare
            // bursts (a few flight estimates across thousands of frames), so a per-frame average of a
            // one-off 0.5ms call is 0.008ms and rounds to nothing. The total is what says whether a
            // stage cost anything at all; the tick line already answers the per-frame question.
            double frameAvg = _accFrameMs * inv;
            // Tick PLUS the UI stage. UiEstimate runs in OnGUI, outside the tick by construction, so
            // a share computed from the tick alone understates the mod's real cost (it read 9.3%
            // where the honest figure was 26.8%).
            double autoTotMs = (Ms(Stage.Tick) + Ms(Stage.UiEstimate)) * inv;
            double share = frameAvg > 0.001 ? 100.0 * autoTotMs / frameAvg : 0d;
            double staleAvg = _releaseCount > 0 ? _accReleaseStaleness / _releaseCount : 0d;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT Profiling] {_frameCount} frames: tick {Ms(Stage.Tick):F1}ms total, {Ms(Stage.Tick) * inv:F3}ms avg, WORST {_maxTickMs:F2}ms (release {_maxTickReleaseMs:F2}ms)\n" +
                $"  frame {frameAvg:F2}ms avg ({1000.0 / (frameAvg > 0.001 ? frameAvg : 1):F0} fps), worst {_maxFrameMs:F2}ms => AutoTOT {autoTotMs:F2}ms/frame = {share:F1}% (tick + UI)\n" +
                $"  release staleness: {_releaseCount} released, estimate age {staleAvg:F2}s avg, {_maxReleaseStaleness:F2}s worst (sim seconds)\n" +
                $"  Diag {Ms(Stage.Diag):F1}ms: scan {_accScanLoopMs:F1} | finalize {_accFinalizeMs:F1} | cleanup {_accCleanupMs:F1} (weapons {LaunchDiagnostics.LastWeaponCount}, tracked {LaunchDiagnostics.LastTrackedMissiles}, uncredited {LaunchDiagnostics.UncreditedLaunches})\n" +
                $"  Commit {Ms(Stage.Commit):F1}ms | Anchor {Ms(Stage.Anchor):F1}ms (PredictAnchorImpact {Ms(Stage.AnchorPredict):F1}ms) | Release {Ms(Stage.Release):F1}ms (avg sched {_accScheduled * inv:F1})\n" +
                $"    -> FlightTime.Estimate: {Ms(Stage.FlightEstimate):F1}ms over {N(Counter.FlightCalls)} calls ({N(Counter.FlightCalls) * inv:F2}/frame)\n" +
                $"       hits: {N(Counter.FlightHits)} calls, {_accFlightHitMs:F1}ms total @ {avgHitMs:F3}ms avg\n" +
                $"       misses: {N(Counter.FlightMisses)} calls, {_accFlightMissMs:F1}ms total @ {avgMissMs:F3}ms avg\n" +
                $"       unaccounted: {unaccountedMs:F1}ms\n" +
                $"       deferred (proximity gate): {N(Counter.FlightDeferred)} | budget-skipped: {N(Counter.FlightBudgetSkipped)}\n" +
                $"       async: {FlightTime.WorkerCount} workers, {N(Counter.FlightQueued)} queued, {FlightTime.AsyncCompleted} completed, {FlightTime.AsyncDeclined} declined, depth {FlightTime.QueueDepth}/{FlightTime.InFlight} in-flight" +
                (FlightTime.VerifySolve ? $" | verify {FlightTime.VerifyChecked} checked, {FlightTime.VerifyMismatched} MISMATCHED" : "") + "\n" +
                $"       model: {ModelStats.Sims} sims, {ModelStats.Steps} steps ({stepsPerSim} avg), setup {ModelStats.SetupMs:F1}ms + loop {ModelStats.LoopMs:F1}ms, {usPerKStep:F0}us/1k steps\n" +
                $"       tiers: integrator {ModelStats.TierCount(ModelStats.Tier.Integrator)}, waypoint {ModelStats.TierCount(ModelStats.Tier.Waypoint)}, maxRange {ModelStats.TierCount(ModelStats.Tier.MaxRangePrecise)}, failed {ModelStats.TierCount(ModelStats.Tier.Failed)}, integrator declined {ModelStats.Stalls}\n" +
                $"    -> UI (outside tick): {Ms(Stage.UiEstimate):F1}ms over {N(Counter.UiEstimateCalls)} calls\n" +
                $"    -> GroupDelay: {Ms(Stage.GroupDelay):F1}ms over {N(Counter.GroupDelayCalls)} calls\n" +
                $"    -> PredictAnchorImpact: FlightEst {Ms(Stage.PredictFlight):F1}ms over {N(Counter.PredictFlightCalls)} calls | GroupDelay {Ms(Stage.PredictGroupDelay):F1}ms over {N(Counter.PredictGroupDelayCalls)} calls\n" +
                $"  Cache {hitRate:F0}% hit ({totalHits}/{total}) [ToT {FlightTime.TofHits}/{FlightTime.TofMisses} h/m, sz {FlightTime.TofCacheSize}, evTtl {FlightTime.TofEvictionsTtl}, evCap {FlightTime.TofEvictionsCapacity}] [Profile {FlightTime.ProfileHits}/{FlightTime.ProfileMisses} h/m, sz {FlightTime.ProfileCacheSize}, evTtl {FlightTime.ProfileEvictionsTtl}, evCap {FlightTime.ProfileEvictionsCapacity}] [Facts {LauncherFactsSource.CacheHits}/{LauncherFactsSource.CacheMisses} h/m, sz {LauncherFactsSource.CacheSize}]");
        }

        internal static void Reset()
        {
            _frameCount = 0;
            _accScheduled = 0;
            _accScanLoopMs = _accFinalizeMs = _accCleanupMs = 0f;
            _accFlightHitMs = _accFlightMissMs = 0d;
            _lastFlightCallMs = 0d;
            _maxTickMs = _maxTickReleaseMs = 0d;
            _frameTickMs = _frameReleaseMs = 0d;
            _accFrameMs = _maxFrameMs = 0d;
            _accReleaseStaleness = _maxReleaseStaleness = 0d;
            _releaseCount = 0;
            for (int i = 0; i < StageCount; i++) _accMs[i] = 0d;
            for (int i = 0; i < CounterCount; i++) _counts[i] = 0;
            ModelStats.Reset();
            FlightTime.ResetStats();
            LauncherFactsSource.ResetStats();
        }
    }
}
