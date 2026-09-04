using System;
using System.Reflection;
using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// Version-agnostic accessor for the game's sim clock. The beta build removed
    /// <c>GameTime.time</c> (float) and the public build still has it, so the getter is resolved
    /// once by reflection and cached; one DLL then runs on both branches.
    ///
    /// The clock is <c>GameTime.missionElapsedTime</c> (double), NOT <c>simulationTime</c>. The game
    /// stamps a weapon's <c>_launchTime</c> from <c>missionElapsedTime</c> and the mod compares
    /// launch times against its own clock, so the two must be the same one. <c>simulationTime</c>
    /// advances by a physics-capped <c>fixedDeltaTime</c> and diverges under high time compression.
    /// Do NOT use it.
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
        /// A weapon's launch timestamp (sim-clock seconds), read via reflection: the field's TYPE
        /// differs between branches (<c>float</c> on public, <c>double</c> on beta) and a
        /// compile-time reference bakes one of them into the IL, so a DLL built against either
        /// branch throws on the other. Returns -1 if the weapon or the field cannot be resolved.
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
