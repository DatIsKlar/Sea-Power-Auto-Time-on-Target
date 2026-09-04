using System;
﻿using System.Diagnostics;
using System.Threading;

namespace AutoTOT
{
    /// <summary>
    /// Cost counters for the estimator itself, so a slow frame can be attributed to something more
    /// specific than "the flight estimate was slow".
    ///
    /// Deliberately NOT timed per step: at dt = 0.1 s a 12-minute flight is ~7,000 steps, and two
    /// timestamp calls per step would cost more than the work being measured. Steps are counted with
    /// a plain increment and wall time is taken twice per sim, which derives a per-step cost without
    /// perturbing it. Counters are written only when <see cref="Enabled"/>, so normal play pays one
    /// bool test per sim.
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

        // Per-thread, because the step loop can run on a worker: two sims in flight at once would
        // otherwise overwrite each other's start stamps and produce nonsense durations. The
        // accumulators below are shared, so they are merged under a lock taken twice per sim (never
        // per step), which is far too rare to matter.
        [ThreadStatic] private static long _setupStart;
        [ThreadStatic] private static long _loopStart;
        private static readonly object _sync = new object();

        internal static int TierCount(Tier t) => _tier[(int)t];

        internal static void SimStarted()
        {
            if (!Enabled) return;
            Interlocked.Increment(ref Sims);
            _setupStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Called once the per-sim setup is done and the step loop is about to begin.</summary>
        internal static void SetupDone()
        {
            if (!Enabled) return;
            _loopStart = Stopwatch.GetTimestamp();
            double ms = (_loopStart - _setupStart) * MsPerTick;
            lock (_sync) SetupMs += ms;
        }

        /// <summary>
        /// Stamp the start of the step loop on THIS thread. The asynchronous path splits setup and
        /// loop across two threads, so the worker stamps its own start here; SetupDone cannot do it,
        /// because the start it reads is thread-local to the main thread that built the input.
        /// </summary>
        internal static void LoopStarting()
        {
            if (!Enabled) return;
            _loopStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Called when the step loop exits, however it exits.</summary>
        internal static void LoopDone(int steps)
        {
            if (!Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - _loopStart) * MsPerTick;
            lock (_sync) { LoopMs += ms; Steps += steps; }
        }

        internal static void Stalled()
        {
            if (!Enabled) return;
            Interlocked.Increment(ref Stalls);
        }

        internal static void TierUsed(Tier t)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _tier[(int)t]);
        }

        internal static void Reset()
        {
            Sims = 0; Steps = 0; Stalls = 0;
            SetupMs = LoopMs = 0d;
            for (int i = 0; i < _tier.Length; i++) _tier[i] = 0;
        }
    }
}
