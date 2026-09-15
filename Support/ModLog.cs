using System;

namespace AutoTOT
{
    /// <summary>
    /// The mod's two exception-reporting shapes, <c>"[AutoTOT] &lt;what&gt; failed: {type}: {message}"</c>,
    /// logged always or under <c>VerboseLog</c>. Message only: these wrap reflection and game-API calls,
    /// where type and message identify the problem. Sites that want the stack log it directly.
    ///
    /// Main thread in practice, except the solve worker's catch-all in FlightTime.Async.cs. BepInEx's
    /// ManualLogSource is not documented as thread-safe, so a worker line can interleave with main-thread lines.
    /// </summary>
    internal static class ModLog
    {
        /// <summary>Report a swallowed exception. Always logged.</summary>
        internal static void Warn(string what, Exception e)
            => Bootstrap.Log.LogWarning($"[AutoTOT] {what}: {e.GetType().Name}: {e.Message}");

        /// <summary>
        /// Report a swallowed exception only under <c>VerboseLog</c>, for failures recovered by design, such
        /// as an estimator tier declining or a diagnostic surface that will not resolve.
        /// </summary>
        internal static void VerboseWarn(string what, Exception e)
        {
            if (Coordinator.VerboseLog) Warn(what, e);
        }
    }
}
