using System;
using System.Reflection;
using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// Branch-independent accessor for the game's sim clock. Newer builds removed <c>GameTime.time</c>
    /// (float), which the public branch still has, so the getter is resolved once by reflection and one DLL
    /// runs on both.
    ///
    /// The clock is <c>GameTime.missionElapsedTime</c> (double), not <c>simulationTime</c>: the game stamps a
    /// weapon's <c>_launchTime</c> from <c>missionElapsedTime</c>, while <c>simulationTime</c> advances by a
    /// physics-capped <c>fixedDeltaTime</c> and diverges under high time compression.
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
        /// A weapon's launch timestamp in sim-clock seconds, read by reflection: the field is float on public
        /// and double on newer builds, and a compile-time reference would throw on the other branch. -1 if it
        /// cannot be resolved.
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

        private static bool _launchStampWarned;

        /// <summary>
        /// <see cref="LaunchStamp"/>, or <paramref name="fallback"/> when the field cannot be read. Use this
        /// wherever the stamp feeds timing: a -1 in an anchor's launch times would put the shared impact in
        /// the past and release every held order at once. Warned once per session.
        /// </summary>
        public static float LaunchStampOr(WeaponBase w, float fallback)
        {
            float stamp = LaunchStamp(w);
            if (stamp >= 0f) return stamp;
            if (w != null && !_launchStampWarned)
            {
                _launchStampWarned = true;
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] launch stamp unreadable (WeaponBase._launchTime); timing each round " +
                    "from the moment it is first seen instead, which can be a frame late.");
            }
            return fallback;
        }

        private static void Resolve()
        {
            _resolved = true;
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            Type gt = typeof(GameTime);
            string resolved = "UNRESOLVED";

            // Preferred: missionElapsedTime (double), the continuation of the removed float `time` and the
            // clock _launchTime uses.
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
