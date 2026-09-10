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

        /// <summary>
        /// True when <paramref name="v"/> is a real number. The ascent and descent solvers both
        /// guard their reflected game reads with this before feeding them into a step loop, where a
        /// NaN would propagate silently through every subsequent step.
        /// </summary>
        internal static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

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
        /// Pitch read off a transform's local euler angles, normalized from Unity's 0..360 range
        /// into -180..180 so a nose-down attitude reads negative.
        ///
        /// Note the sign convention this inherits from the game: for aircraft the game's pitch is
        /// NEGATIVE in a climb. Callers that want a hull-relative pitch for a submarine want
        /// <c>Submarine.getPitch()</c> instead, which is a different quantity; see the
        /// specialisation in VerticalProfiler.
        /// </summary>
        internal static float PitchDeg(Transform t)
        {
            float x = t.localEulerAngles.x;
            return (x > 180f) ? x - 360f : x;
        }

        /// <summary>
        /// Elevation of a direction above the horizontal, in degrees. Clamps before the asin, so a
        /// float-error component slightly outside [-1, 1] returns +/-90 rather than NaN. The vector
        /// is assumed normalized; pass <c>.normalized</c> if it is not.
        /// </summary>
        internal static float ElevationDeg(Vector3 unitDir)
            => Mathf.Asin(Mathf.Clamp(unitDir.y, -1f, 1f)) * Mathf.Rad2Deg;
        /// <summary>
        /// <c>Quaternion.Euler</c> and <c>Quaternion.RotateTowards</c>, routed through here so the
        /// step loop can run outside a live Unity engine.
        ///
        /// Unity implements both as engine-native calls. Outside the player they raise
        /// SecurityException ("ECall methods must be packaged into a system module"), which would
        /// otherwise make the integrator impossible to replay in a test harness. The
        /// <c>AUTOTOT_OFFLINE</c> build defines managed equivalents; the shipped mod keeps calling
        /// Unity, so production behaviour is exactly what it always was.
        ///
        /// The <c>Quaternion</c> STRUCT is ordinary managed code and needs no substitute: its
        /// constructor, <c>identity</c>, <c>normalized</c>, <c>Dot</c>, <c>Angle</c> and the
        /// vector-rotation operator all run fine outside the engine. Only these two factories do
        /// not.
        /// </summary>
        internal static Quaternion QEuler(float xDeg, float yDeg, float zDeg)
        {
#if AUTOTOT_OFFLINE
            // Unity composes Euler angles in Z, X, Y order, so the product is qY * qX * qZ.
            Quaternion qx = AxisQuat(Vector3.right,   xDeg);
            Quaternion qy = AxisQuat(Vector3.up,      yDeg);
            Quaternion qz = AxisQuat(Vector3.forward, zDeg);
            return Mul(Mul(qy, qx), qz);
#else
            return Quaternion.Euler(xDeg, yDeg, zDeg);
#endif
        }

        /// <inheritdoc cref="QEuler"/>
        internal static Quaternion QRotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
#if AUTOTOT_OFFLINE
            float angle = Quaternion.Angle(from, to);
            if (angle == 0f) return to;
            return SlerpUnclamped(from, to, Mathf.Min(1f, maxDegreesDelta / angle));
#else
            return Quaternion.RotateTowards(from, to, maxDegreesDelta);
#endif
        }

        /// <inheritdoc cref="QEuler"/>
        internal static Vector3 QEulerAngles(Quaternion q)
        {
#if AUTOTOT_OFFLINE
            // Inverse of the Z, X, Y composition QEuler builds, in the same 0..360 range Unity
            // reports. Diagnostic only: the step loop reads this for the roll column of its trace
            // and never for a decision.
            float sinX = 2f * (q.w * q.x - q.y * q.z);
            float x, y, z;
            if (Mathf.Abs(sinX) > 0.9999f)
            {
                // Looking straight up or down, yaw and roll describe the same rotation, so the
                // convention is to put all of it in yaw.
                x = Mathf.Sign(sinX) * 90f * Mathf.Deg2Rad;
                y = Mathf.Atan2(2f * (q.w * q.y + q.x * q.z), 1f - 2f * (q.y * q.y + q.z * q.z));
                z = 0f;
            }
            else
            {
                x = Mathf.Asin(sinX);
                y = Mathf.Atan2(2f * (q.w * q.y + q.x * q.z), 1f - 2f * (q.x * q.x + q.y * q.y));
                z = Mathf.Atan2(2f * (q.w * q.z + q.x * q.y), 1f - 2f * (q.x * q.x + q.z * q.z));
            }
            return new Vector3(Wrap360(x * Mathf.Rad2Deg), Wrap360(y * Mathf.Rad2Deg),
                               Wrap360(z * Mathf.Rad2Deg));
#else
            return q.eulerAngles;
#endif
        }

#if AUTOTOT_OFFLINE
        private static float Wrap360(float deg)
        {
            deg %= 360f;
            return deg < 0f ? deg + 360f : deg;
        }

        private static Quaternion AxisQuat(Vector3 axis, float deg)
        {
            float h = deg * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        private static Quaternion Mul(Quaternion a, Quaternion b)
            => new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        /// <summary>
        /// Shortest-arc spherical interpolation, with a linear fallback once the two orientations
        /// are close enough that sin(theta) stops being a safe divisor.
        /// </summary>
        private static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                dot = -dot;
            }

            float k0, k1;
            if (dot > 0.9995f)
            {
                k0 = 1f - t;
                k1 = t;
            }
            else
            {
                float theta = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
                float sin = Mathf.Sin(theta);
                k0 = Mathf.Sin((1f - t) * theta) / sin;
                k1 = Mathf.Sin(t * theta) / sin;
            }

            return new Quaternion(k0 * a.x + k1 * b.x, k0 * a.y + k1 * b.y,
                                  k0 * a.z + k1 * b.z, k0 * a.w + k1 * b.w).normalized;
        }
#endif
    }
}
