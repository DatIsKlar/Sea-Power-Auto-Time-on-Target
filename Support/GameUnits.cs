using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Unit conversions shared by the coordinator and the HUD, using the game's own constants. Keep new
    /// conversions here.
    /// </summary>
    internal static class GameUnits
    {
        /// <summary>Unity world units -> metres (the game's ScaledBody / velocity scale).</summary>
        public const float MetersPerUnity = 67.200066f;

        /// <summary>Knots -> metres/second.</summary>
        public const float KnotsToMs = 0.5144447f;

        /// <summary>Unity world units -> feet. The same scale as <see cref="MetersPerUnity"/>,
        /// kept separately because the altitude and depth paths report in feet throughout, matching
        /// the game's own cockpit and sonar readouts.</summary>
        public const float UnityToFeet = 220.47266f;

        /// <summary>Metres to Unity units, as the literal the game itself writes (Aircraft.cs,
        /// AircraftLoader.cs). One over <see cref="MetersPerUnity"/> to seven digits; kept as the
        /// game's own value so a conversion copied from the decompile matches it exactly.</summary>
        public const float UnityPerMetre = 0.0148809375f;

        /// <summary>Metres per nautical mile (international definition).</summary>
        public const float MetersPerNm = 1852f;

        /// <summary>Unity world units -> nautical miles.</summary>
        public const float UnityToNm = MetersPerUnity / MetersPerNm;

        /// <summary>Knots -> Unity world units per second (the game's velocity scale;
        /// ~ KnotsToMs / MetersPerUnity). Shared by the flight-time sims.</summary>
        public const float KnotsToUnityPerSecond = 0.0076554087f;

        /// <summary>Distance from <paramref name="from"/> to <paramref name="to"/> in metres.</summary>
        public static float MetersBetween(ObjectBase from, ObjectBase to)
            => (to.transform.position - from.transform.position).magnitude * MetersPerUnity;

        /// <summary>Distance from <paramref name="from"/> to <paramref name="to"/> in nautical miles.</summary>
        public static float NmBetween(ObjectBase from, ObjectBase to)
            => (to.transform.position - from.transform.position).magnitude * UnityToNm;
    }
}
