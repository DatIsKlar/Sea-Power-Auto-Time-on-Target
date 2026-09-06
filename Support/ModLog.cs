using System;

namespace AutoTOT
{
    /// <summary>
    /// The mod's two exception-reporting shapes, written once.
    ///
    /// Seven sites logged the identical
    /// <c>"[AutoTOT] &lt;what&gt; failed: {type}: {message}"</c> string, four of them behind
    /// <c>VerboseLog</c> and three not, with no stated reason for the split. Routing them through
    /// here makes that choice explicit at each call site and keeps the format in one place.
    ///
    /// Message only, no stack: these wrap reflection and game-API calls where the type and message
    /// identify the problem and a full trace would be noise. Sites that genuinely want the stack
    /// (the Harmony prefix, the HUD draw guard, the coordinator tick) log it directly and are
    /// deliberately not routed through here.
    ///
    /// Main thread, in practice. The one caller that is not is the solve worker's catch-all in
    /// FlightTime.Async.cs; BepInEx's ManualLogSource is not documented as thread-safe, so a worker
    /// exception during a verbose run can interleave with main-thread lines. Pre-existing, and worth
    /// knowing before trusting line order in such a log.
    /// </summary>
    internal static class ModLog
    {
        /// <summary>Report a swallowed exception. Always logged.</summary>
        internal static void Warn(string what, Exception e)
            => Bootstrap.Log.LogWarning($"[AutoTOT] {what}: {e.GetType().Name}: {e.Message}");

        /// <summary>
        /// Report a swallowed exception only under <c>VerboseLog</c>. For failures the mod recovers
        /// from by design, where the fallback is correct behaviour rather than a problem: an
        /// estimator tier declining, a diagnostic surface that will not resolve.
        /// </summary>
        internal static void VerboseWarn(string what, Exception e)
        {
            if (Coordinator.VerboseLog) Warn(what, e);
        }
    }
}
