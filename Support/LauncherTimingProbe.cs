using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// D11 of docs/plans/open/launcher-startup-delay.md: name the declared field behind a launcher
    /// state the startup model does not cost.
    ///
    /// The 2026-09-09 Kidd-class MK26 launched 11.69 s after the order against a predicted 0.00 s,
    /// and the engage-state trace decomposed it into WarmingUp 7.00 s, ChoosingContainer 2.69 s and
    /// AligningLauncher 1.33 s. `LauncherCycle = DeclaredOnRailWarmup + HatchCycle` is correctly
    /// zero for that mount: those three states are simply not terms in the model. Fitting a
    /// constant to 11.69 s would break on the next rail launcher, so this reports WHICH field
    /// carries each duration, the way D1 settled OnRailWarmup at declared 5.00 s = observed 5.00 s.
    ///
    /// On the way OUT of an uncosted state, every finite non-zero float on the launcher object's
    /// type hierarchy and on its WeaponParameters is compared against the observed hold. Fields
    /// within <see cref="MatchTolerance"/> are named as candidates; the rest are listed once so a
    /// term this pass fails to match is still recoverable from the log.
    ///
    /// Diagnostics only. Nothing here feeds a timing decision, and it is gated behind
    /// VerboseLogging, fires at most once per launcher system and state per mission, and only for a
    /// hold long enough to matter.
    /// </summary>
    internal static class LauncherTimingProbe
    {
        /// <summary>
        /// A hold shorter than this is not worth a field hunt: the engage-state sampler runs on a
        /// 0.25 s cadence, so anything near it is quantisation rather than a declared duration.
        /// </summary>
        internal const float MinHoldSeconds = 0.75f;

        /// <summary>
        /// How close a declared value must sit to the observed hold to be called a candidate. Wide
        /// enough to absorb the 0.25 s sample cadence at both ends of the state, tight enough that
        /// a ship carrying dozens of floats does not name half of them.
        /// </summary>
        internal const float MatchTolerance = 0.35f;

        /// <summary>
        /// States whose duration <see cref="LauncherFactsSource"/> already sources from a declared
        /// field. Everything else a launcher can sit in is uncosted, and is what this probe is for.
        /// Matched by name, like PreparingStates, because one DLL serves two game branches.
        /// </summary>
        private static readonly HashSet<string> ModelledStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnRailWarmup",     // DeclaredOnRailWarmup, settled by D1
            "OpeningHatches",   // HatchCycleSeconds, from the animation asset
            "Idle",             // not a cost; the dispatch tick, under the sample cadence
            "SharedLaunchDelay", "FirerateDelay",   // ship-level interval, already read
        };

        // One line per system name and state per mission. A four-launcher mount would otherwise
        // print the same field dump four times, and a ripple would print it once per round.
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        // system/state pairs already folded into _coldStart, so a warm re-run does not add again.
        private static readonly HashSet<string> _coldSeen = new HashSet<string>(StringComparer.Ordinal);

        // Type -> its float fields, resolved once. A ship firing a ripple walks this per round, and
        // GetFields allocates an array every call.
        private static readonly Dictionary<Type, FieldInfo[]> _floatFields = new Dictionary<Type, FieldInfo[]>();

        internal static void Reset()
        {
            _reported.Clear();
            _floatFields.Clear();
            _coldStart.Clear();
            _coldSeen.Clear();
        }

        /// <summary>
        /// The states a COLD launcher walks before its first round, measured rather than declared.
        ///
        /// The declared route was tried and abandoned. `WarmingUp` on a Kidd/Chandler MK26 does
        /// equal `WeaponParameters._magazineReloadTime` (7.00 s declared, 6.83 to 7.06 s observed),
        /// but the same field reads 900.00 s on a Spruance Sea Sparrow and 600.00 s on a Type 055
        /// HQ-10, where it is a full magazine reload cycle and no launcher ever enters the state.
        /// `_perContainerReload` does not separate those cases: all three are False. Charging the
        /// declared value would have added fifteen minutes of startup lead to a Sea Sparrow.
        ///
        /// So the cost is learned from the engage-state trace instead. A mount that never runs
        /// these states never accumulates anything and is never charged, which is the property the
        /// declared route could not give.
        /// </summary>
        private static readonly HashSet<string> ColdStartStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "WarmingUp", "ChoosingContainer", "AligningLauncher",
        };

        // system name -> summed cold-start seconds observed on the first launcher to walk them.
        private static readonly Dictionary<string, float> _coldStart = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// Observed cold-start seconds for this launcher system, or 0 when nothing has been seen.
        /// Only ever read for a ship with a cold launcher; see LauncherProbe.AnyLauncherCold.
        /// </summary>
        internal static float ColdStartSeconds(string systemName)
        {
            if (string.IsNullOrEmpty(systemName)) return 0f;
            return _coldStart.TryGetValue(systemName, out float v) ? v : 0f;
        }

        /// <summary>
        /// Report the declared fields that could be behind <paramref name="state"/> holding for
        /// <paramref name="held"/> seconds on <paramref name="launcher"/>. No-op unless verbose, the
        /// state is uncosted, the hold is long enough, and this system/state pair is new.
        /// </summary>
        internal static void OnStateLeft(ObjectBase ship, string ammoId, WeaponSystem launcher,
                                         string systemName, string state, float held)
        {
            if (launcher == null || string.IsNullOrEmpty(state)) return;

            // Accumulate BEFORE the verbose gate: the cold-start estimate is a timing input and
            // must not depend on whether the player has logging on.
            if (ColdStartStates.Contains(state) && held >= MinHoldSeconds && IsFinite(held)
                && !string.IsNullOrEmpty(systemName))
            {
                _coldStart.TryGetValue(systemName, out float sum);
                // First cold pass only. A warm launcher re-runs ChoosingContainer and
                // AligningLauncher on every order, and adding those would grow the term without
                // bound over a mission.
                if (!_coldSeen.Contains(systemName + "/" + state))
                {
                    _coldSeen.Add(systemName + "/" + state);
                    _coldStart[systemName] = sum + held;
                }
            }

            if (!Coordinator.VerboseLog) return;
            if (held < MinHoldSeconds || !IsFinite(held)) return;
            if (ModelledStates.Contains(state)) return;
            if (!_reported.Add((systemName ?? "?") + "/" + state)) return;

            try
            {
                var hits = new List<string>();
                var others = new List<string>();
                Scan(launcher, launcher.GetType(), "launcher", held, hits, others);
                WeaponParameters vwp = launcher._vwp;
                if (vwp != null) Scan(vwp, vwp.GetType(), "vwp", held, hits, others);

                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] timing-probe {ammoId} from {UnitNaming.SafeName(ship)} " +
                    $"[{systemName}]: {state} held {held:0.00}s, " +
                    (hits.Count > 0
                        ? $"candidate field(s) within {MatchTolerance:0.00}s: {string.Join(", ", hits.ToArray())}"
                        : "NO field matches that hold") +
                    $" | other non-zero floats: {Join(others)}. D11 of the launcher-startup plan.");
            }
            catch (Exception e)
            {
                // Say so rather than swallow: a silent miss here looks identical to a mount that
                // genuinely declares nothing, and those need very different follow-ups.
                Bootstrap.Log.LogWarning($"[AutoTOT] timing-probe failed for {systemName}/{state}: {e.Message}");
            }
        }

        /// <summary>
        /// Walk one object's float and double fields, up its whole declaring hierarchy, sorting each
        /// non-zero value into the candidates or the also-ran list. Private fields included: the
        /// duration behind a state is far more likely to be a backing field than a public one.
        /// </summary>
        private static void Scan(object target, Type type, string tag, float held,
                                 List<string> hits, List<string> others)
        {
            foreach (FieldInfo f in FloatFieldsOf(type))
            {
                float v;
                try
                {
                    object raw = f.GetValue(target);
                    v = raw is float fl ? fl : raw is double d ? (float)d : 0f;
                }
                catch { continue; }

                if (!IsFinite(v) || v <= 0f) continue;
                // A clock stamp, not a duration: sim times run to thousands of seconds and would
                // fill the line with noise no launcher parameter could ever match.
                if (v > 600f) continue;

                string entry = $"{tag}.{f.Name}={v:0.00}";
                if (Math.Abs(v - held) <= MatchTolerance) hits.Add(entry);
                else others.Add(entry);
            }
        }

        private static FieldInfo[] FloatFieldsOf(Type type)
        {
            if (_floatFields.TryGetValue(type, out FieldInfo[] cached)) return cached;

            var found = new List<FieldInfo>();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly;
            // DeclaredOnly plus an explicit walk up the base types: the flat GetFields hides a
            // private field on a base class, which is exactly where a launcher timer would live.
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(F))
                    if (f.FieldType == typeof(float) || f.FieldType == typeof(double))
                        found.Add(f);

            FieldInfo[] arr = found.ToArray();
            _floatFields[type] = arr;
            return arr;
        }

        /// <summary>Cap the also-ran list: a WeaponParameters carries a lot, and the candidates are the point.</summary>
        private static string Join(List<string> items)
        {
            const int Max = 24;
            if (items.Count == 0) return "none";
            if (items.Count <= Max) return string.Join(", ", items.ToArray());
            string[] head = items.GetRange(0, Max).ToArray();
            return string.Join(", ", head) + $", +{items.Count - Max} more";
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
