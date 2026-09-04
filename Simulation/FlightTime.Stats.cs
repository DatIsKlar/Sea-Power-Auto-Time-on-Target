using System.Diagnostics;

namespace AutoTOT
{
    /// <summary>
    /// Cost counters for the estimator itself, so a slow frame can be attributed to something more
    /// specific than "the flight estimate was slow".
    ///
    /// Deliberately NOT timed per integration step. The step loop runs at dt = 0.1 s, so a 12-minute
    /// flight is ~7,000 steps and one frame may run twelve of those. Two timestamp calls per step
    /// would cost more than the work being measured. Steps are counted with a plain increment, and
    /// wall time is taken twice per sim (setup, then loop), which is enough to derive a per-step
    /// cost without perturbing it.
    ///
    /// Every counter here is only written when <see cref="Enabled"/>, which tracks the profiling
    /// config key, so normal play pays one bool test per sim.
    /// </summary>
    internal static class ModelStats
    {
        /// <summary>Which tier of <c>KinematicRaw</c> produced the answer.</summary>
        internal enum Tier { Integrator, Waypoint, MaxRangePrecise, Failed }

        internal static bool Enabled;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        internal static int Sims;              // integrator runs started
        internal static long Steps;            // integration steps executed across those runs
        internal static double SetupMs;        // per-sim work before the step loop
        internal static double LoopMs;         // the step loop itself
        internal static int Stalls;            // runs that bailed out (returned -1)
        private static readonly int[] _tier = new int[4];

        private static long _setupStart, _loopStart;

        internal static int TierCount(Tier t) => _tier[(int)t];

        internal static void SimStarted()
        {
            if (!Enabled) return;
            Sims++;
            _setupStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Called once the per-sim setup is done and the step loop is about to begin.</summary>
        internal static void SetupDone()
        {
            if (!Enabled) return;
            _loopStart = Stopwatch.GetTimestamp();
            SetupMs += (_loopStart - _setupStart) * MsPerTick;
        }

        /// <summary>Called when the step loop exits, however it exits.</summary>
        internal static void LoopDone(int steps)
        {
            if (!Enabled) return;
            LoopMs += (Stopwatch.GetTimestamp() - _loopStart) * MsPerTick;
            Steps += steps;
        }

        internal static void Stalled()
        {
            if (!Enabled) return;
            Stalls++;
        }

        internal static void TierUsed(Tier t)
        {
            if (!Enabled) return;
            _tier[(int)t]++;
        }

        internal static void Reset()
        {
            Sims = 0; Steps = 0; Stalls = 0;
            SetupMs = LoopMs = 0d;
            for (int i = 0; i < _tier.Length; i++) _tier[i] = 0;
        }
    }
}
