using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Unit conversions shared by the coordinator and the HUD. The game world is measured
    /// in Unity units; these are the game's own constants for converting to metres, nautical
    /// miles, and metres/second. Keep any new conversion here so the values live in one place.
    /// </summary>
    internal static class GameUnits
    {
        /// <summary>Unity world units -> metres (the game's ScaledBody / velocity scale).</summary>
        public const float MetersPerUnity = 67.200066f;

        /// <summary>Knots -> metres/second.</summary>
        public const float KnotsToMs = 0.5144447f;

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
