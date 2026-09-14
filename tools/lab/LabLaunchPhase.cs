using System.Collections.Generic;
using System.Globalization;

namespace AutoTOT.Lab
{
    internal static partial class Program
    {
        /// <summary>M3 of docs/plans/open/model-and-launcher-audit.md, set by AUTOTOT_LAUNCH_PHASE_RECORDED=1.</summary>
        private static readonly bool UseRecordedLaunchPhase =
            System.Environment.GetEnvironmentVariable("AUTOTOT_LAUNCH_PHASE_RECORDED") == "1";

        /// <summary>
        /// The flight time at which the round left the Launch state, from the outcome's StageEvents
        /// ("time:Launch>ToBearing:..." entries separated by ';'). The game ends Launch on a per-frame clock, so at 10x it
        /// leaves 0.19 to 0.33 s after InitialFlightPhaseDuration and at 2x 0.03 to 0.06 s. False when the capture has no
        /// such event, which keeps the capture's own value.
        /// </summary>
        private static bool RecordedLaunchPhase(Dictionary<string, string> r, out float seconds)
        {
            seconds = 0f;
            if (!r.TryGetValue("StageEvents", out string events) || string.IsNullOrEmpty(events)) return false;
            foreach (string entry in events.Split(';'))
            {
                string[] parts = entry.Split(':');
                if (parts.Length < 2 || !parts[1].StartsWith("Launch>", System.StringComparison.Ordinal)) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float t) && t > 0f)
                {
                    seconds = t;
                    return true;
                }
            }
            return false;
        }
    }
}
