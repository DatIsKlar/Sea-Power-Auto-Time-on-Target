namespace AutoTOT
{
    /// <summary>
    /// Sampling cadence shared by <c>sim-track</c> and <c>track</c> traces. Worker-safe:
    /// compile-time constants and pure arithmetic. See docs/model/07-diagnostics.md §7.2.
    /// </summary>
    internal static class TelemetryCadence
    {
        // Not a round number: a 15 s sampler aliased a 15 s limit cycle and showed a steady altitude.
        internal const float SampleIntervalSim = 7f;

        // The launch phase (up to about 19 s) is sampled densely; 7 s would leave one point inside it.
        internal const float LaunchBurstWindowSim = 20f;
        internal const float LaunchBurstIntervalSim = 1f;

        // Inner tier for the nose-over, which a vertically launched sea-skimmer completes in about 2 s.
        internal const float NoseOverWindowSim = 5f;
        internal const float NoseOverIntervalSim = 0.25f;

        /// <summary>
        /// Seconds until the next sample, for a round <paramref name="sinceLaunchSim"/> seconds into
        /// its flight: 0.25s through the nose-over, 1s through the rest of the launch phase, 7s for the cruise.
        /// </summary>
        internal static float IntervalFor(float sinceLaunchSim)
            => (sinceLaunchSim < NoseOverWindowSim) ? NoseOverIntervalSim
             : (sinceLaunchSim < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
             : SampleIntervalSim;
    }
}
