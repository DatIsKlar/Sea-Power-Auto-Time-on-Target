using System;

namespace AutoTOT
{
    /// <summary>
    /// The handful of ambient services the solver source touches, supplied here so the integrator
    /// can be compiled without the mod's Unity- and BepInEx-bound half.
    ///
    /// None of these participate in the numerical result. They are logging, two diagnostic gates
    /// held off, a profiler counter, and a simulation clock the solve path reads only to stamp
    /// trace lines. The step loop itself is a pure function of its SolveInput, which is precisely
    /// what makes this substitution safe.
    /// </summary>
    internal static class Bootstrap
    {
        internal static readonly LabLog Log = new LabLog();
    }

    internal sealed class LabLog
    {
        internal bool Echo;
        public void LogInfo(object m) { if (Echo) Console.WriteLine(m); }
        public void LogWarning(object m) { if (Echo) Console.WriteLine("WARN " + m); }
        public void LogError(object m) => Console.WriteLine("ERROR " + m);
        public void LogDebug(object m) { if (Echo) Console.WriteLine(m); }
    }

    internal static class Coordinator
    {
        // Both diagnostic gates stay off: the lab reports its own comparison rather than the
        // in-game traces, and a per-step trace would swamp a sweep.
        internal static bool TraceFlightModel;
        internal static bool VerboseLog;
    }

    internal static class CoordinatorProfiler
    {
        internal static double MsPerTick => 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        internal static void CountCachedHit() { }
    }

    internal static class GameClock
    {
        // No mission is running, so every trace stamp reads zero.
        internal static float SimNow() => 0f;
    }

    internal static class SolveInputDump
    {
        // Dumping is a game-side concern; the lab consumes dumps, it does not write them.
        internal static string Write(string label, in FlightTime.SolveInput i,
                                     SeaPower.AmmunitionParameters ap) => null;
    }
}
