using System;
using System.Reflection;
using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// Version-agnostic accessor for the game's sim clock.
    ///
    /// The 2026-09-01 beta migrated timing from <c>float</c> to <c>double</c>: it removed
    /// <c>GameTime.time</c> (float). A compile-time reference to it would
    /// <see cref="MissingMethodException"/> on beta, and the current PUBLIC build still has it, so
    /// we resolve the getter once via reflection and cache it — the same cached-reflection pattern
    /// the flight-time integrator uses in FlightTime.cs.
    ///
    /// The replacement is <c>GameTime.missionElapsedTime</c> (double), NOT <c>simulationTime</c>.
    /// Verified against both decompiles: old <c>time</c> and <c>missionElapsedTime</c> share the
    /// exact same accumulation (<c>+= deltaTime</c>, the compression-scaled update clock), so
    /// <c>missionElapsedTime</c> is the behavioral continuation of old <c>time</c>. Critically, the
    /// game sets a weapon's <c>_launchTime</c> from this clock (WeaponBase: <c>_launchTime =
    /// GameTime.missionElapsedTime</c>), and the mod compares launch times against it — so the mod's
    /// clock MUST be the same one. <c>simulationTime</c> advances by a physics-capped
    /// <c>fixedDeltaTime</c> and DIVERGES from <c>missionElapsedTime</c> under high time compression;
    /// using it would desync scheduling from <c>_launchTime</c>. Do NOT use it.
    /// </summary>
    internal static class GameClock
    {
        private static Func<float> _getter;
        private static bool _resolved;
        private static System.Reflection.FieldInfo _launchTimeField;
        private static bool _launchTimeResolved;

        /// <summary>Current sim time in seconds (float). Returns 0 if neither property resolves.</summary>
        public static float SimNow()
        {
            if (!_resolved) Resolve();
            return _getter != null ? _getter() : 0f;
        }

        /// <summary>
        /// A weapon's launch timestamp (sim-clock seconds, float), read via reflection: the field's
        /// TYPE drifted in the 2026-09-01 beta (<c>float</c> on public, <c>double</c> on beta), and a
        /// compile-time reference bakes whichever type the build referenced into the IL — a DLL built
        /// against one branch <see cref="MissingFieldException"/>s on the other. Reading through the
        /// FieldInfo makes one DLL run on both branches (same cached-reflection pattern as above).
        /// Returns -1 if the weapon is null or the field cannot be resolved.
        /// </summary>
        public static float LaunchStamp(WeaponBase w)
        {
            if (w == null) return -1f;
            if (!_launchTimeResolved)
            {
                _launchTimeResolved = true;
                _launchTimeField = typeof(WeaponBase).GetField("_launchTime",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            if (_launchTimeField == null) return -1f;
            object v = _launchTimeField.GetValue(w);
            if (v is double d) return (float)d;
            if (v is float f) return f;
            return -1f;
        }

        private static void Resolve()
        {
            _resolved = true;
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            Type gt = typeof(GameTime);
            string resolved = "UNRESOLVED";

            // Preferred (both branches): missionElapsedTime (double). It is the behavioral
            // continuation of the removed float `time` and matches the clock `_launchTime` uses.
            PropertyInfo mel = gt.GetProperty("missionElapsedTime", F);
            if (mel != null && mel.GetGetMethod() != null)
            {
                MethodInfo get = mel.GetGetMethod();
                var d = (Func<double>)Delegate.CreateDelegate(typeof(Func<double>), get);
                _getter = () => (float)d();
                resolved = "missionElapsedTime";
            }
            else
            {
                // Fallback for any build without it: the old float `time`.
                PropertyInfo old = gt.GetProperty("time", F);
                if (old != null && old.GetGetMethod() != null)
                {
                    MethodInfo get = old.GetGetMethod();
                    _getter = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), get);
                    resolved = "time";
                }
            }

            if (Coordinator.VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] game-clock: {resolved}");
        }
    }
}
