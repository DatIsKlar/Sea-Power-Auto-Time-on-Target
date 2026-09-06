using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Small geometry helpers the flight-time setup paths repeat: flattening a vector into the
    /// horizontal plane, and reading an elevation angle off a direction.
    ///
    /// Worker-safe. Everything here is pure <c>Mathf</c> and <c>Vector3</c> arithmetic with no
    /// Unity object access, no <c>Time</c> read and no game state, so it may be called from the
    /// solve workers as well as the main thread. (Contrast <see cref="TtlCache{TKey,TValue}"/>,
    /// which reads <c>Time.unscaledTime</c> and is main-thread only. See docs/ARCHITECTURE.md,
    /// "Threading contract".)
    ///
    /// Not called from the integrator's step loop, deliberately. See the note on that loop in
    /// docs/plans/done/2026-09-04-dead-code-and-readability.md.
    /// </summary>
    internal static class GameMath
    {
        /// <summary>
        /// Squared-length floor for treating a flattened vector as having a BEARING.
        ///
        /// 1e-4, which is what every call site asking this question already used. The case it exists
        /// for is a near-vertical launch rail, whose flattened forward vector is small but not
        /// denormal.
        ///
        /// <see cref="TryFlatDirection"/> rejects at or below it, matching the launch-heading seed's
        /// original strict-greater test, which is the one site where this threshold can move a
        /// flight time. The launcher-selection guard tested `&lt; 1e-4` instead, so it keeps its own
        /// comparison against this constant rather than going through the helper. The two differ
        /// only at exactly 1e-4.
        ///
        /// Distinct from the 1e-6 guards elsewhere in the integrator, which are deliberately not
        /// routed through here: those ask whether there is a line to the target at all, which is a
        /// different question with a different answer, and folding them in would be a change of
        /// meaning rather than a deduplication.
        /// </summary>
        internal const float MinFlatSqrMagnitude = 1e-4f;

        /// <summary>The vector projected onto the horizontal plane.</summary>
        internal static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>
        /// The horizontal direction of <paramref name="v"/>, normalized. False when the flattened
        /// vector is too short to carry a bearing (a vertical rail, or a target directly overhead),
        /// in which case <paramref name="dir"/> is <c>Vector3.zero</c>.
        /// </summary>
        internal static bool TryFlatDirection(Vector3 v, out Vector3 dir)
        {
            v.y = 0f;
            if (v.sqrMagnitude <= MinFlatSqrMagnitude) { dir = Vector3.zero; return false; }
            dir = v.normalized;
            return true;
        }

        /// <summary>
        /// Horizontal distance between two points, ignoring altitude.
        ///
        /// The body is <c>WaypointSim.Flat</c> verbatim. The integrator's setup half spelled the same
        /// thing a third way, as <c>new Vector2(dx, dz).magnitude</c>, which is the identical
        /// computation because Unity's <c>Vector2.magnitude</c> is <c>Mathf.Sqrt(x*x + y*y)</c>.
        ///
        /// The step loop in FlightTime.Solve.cs has a fourth copy and keeps it: it needs the
        /// intermediate dx and dz to build the heading vector on the next line, so a call that
        /// returned only the distance would make it recompute them.
        /// </summary>
        internal static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Slant range from a horizontal distance and an altitude difference.</summary>
        internal static float SlantFromFlat(float flat, float altDelta)
            => Mathf.Sqrt(flat * flat + altDelta * altDelta);

        /// <summary>
        /// The inverse: horizontal distance from a slant range and an altitude difference. Clamped at
        /// zero, because a near-overhead sample can put the subtraction slightly negative through
        /// float error.
        /// </summary>
        internal static float FlatFromSlant(float slant, float altDelta)
            => Mathf.Sqrt(Mathf.Max(slant * slant - altDelta * altDelta, 0f));

        /// <summary>
        /// Elevation of a direction above the horizontal, in degrees. Clamps before the asin, so a
        /// float-error component slightly outside [-1, 1] returns +/-90 rather than NaN. The vector
        /// is assumed normalized; pass <c>.normalized</c> if it is not.
        /// </summary>
        internal static float ElevationDeg(Vector3 unitDir)
            => Mathf.Asin(Mathf.Clamp(unitDir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
