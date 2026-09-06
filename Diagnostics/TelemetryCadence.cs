namespace AutoTOT
{
    /// <summary>
    /// Sampling cadence shared by <c>sim-track</c> and <c>track</c> traces. Worker-safe:
    /// compile-time constants and pure arithmetic. See docs/model/07-diagnostics.md §7.2.
    /// </summary>
    internal static class TelemetryCadence
    {
        // Deliberately NOT a round number: a 15s sampler aliased a 15.0s limit cycle and reported a
        // steady altitude while the missile swung 1115 <-> 1260u. 7 is prime relative to the
        // plausible cycle periods here.
        internal const float SampleIntervalSim = 7f;

        // The launch phase lasts up to ~19s (initial flight phase + ToBearing) and creates the
        // residual fixed offset, so sample it densely; 7s would leave one point inside it.
        internal const float LaunchBurstWindowSim = 20f;
        internal const float LaunchBurstIntervalSim = 1f;

        // Inner tier for the nose-over itself. A vertically-launched sea-skimmer reverses its whole
        // launch attitude inside ~2s, and at 1s cadence the same yj-18a data reads as either 19 or
        // 33.7 deg/s depending on the averaging window. 0.25s gives ~20 samples instead of two.
        internal const float NoseOverWindowSim = 5f;
        internal const float NoseOverIntervalSim = 0.25f;

        /// <summary>
        /// Seconds until the next sample, for a round <paramref name="sinceLaunchSim"/> seconds into
        /// its flight: 0.25s through the nose-over, 1s through the rest of the launch phase, 7s for
        /// the cruise.
        /// </summary>
        internal static float IntervalFor(float sinceLaunchSim)
            => (sinceLaunchSim < NoseOverWindowSim) ? NoseOverIntervalSim
             : (sinceLaunchSim < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
             : SampleIntervalSim;
    }
}
