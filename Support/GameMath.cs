using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Small geometry helpers the flight-time setup paths repeat: flattening a vector into the horizontal plane and
    /// reading an elevation angle off a direction. Worker-safe: pure Mathf and Vector3 arithmetic with no Unity object,
    /// Time or game-state access (unlike <see cref="TtlCache{TKey,TValue}"/>; see docs/ARCHITECTURE.md "Threading
    /// contract"). The flattening helpers stay out of the integrator's step loop, because a float expression moved into
    /// a helper can change an estimate.
    /// </summary>
    internal static class GameMath
    {
        /// <summary>
        /// Squared-length floor for treating a flattened vector as having a bearing; the case is a
        /// near-vertical launch rail, whose flattened forward vector is small but not denormal.
        /// <see cref="TryFlatDirection"/> rejects at or below it, matching the launch-heading seed; the
        /// launcher-selection guard tests `&lt;` against it directly. The integrator's 1e-6 guards ask a
        /// different question (is there a line to the target at all) and stay separate.
        /// </summary>
        internal const float MinFlatSqrMagnitude = 1e-4f;

        /// <summary>
        /// True when <paramref name="v"/> is a real number. The ascent and descent solvers check their
        /// reflected game reads with it, since a NaN would propagate silently through every later step.
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
        /// Horizontal distance between two points, ignoring altitude. The step loop in FlightTime.Solve.cs
        /// keeps its own copy, because it needs the intermediate dx and dz for the heading vector.
        /// </summary>
        internal static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Horizontal distance from a slant range and an altitude difference, clamped at zero because float
        /// error can push a near-overhead sample slightly negative.
        /// </summary>
        internal static float FlatFromSlant(float slant, float altDelta)
            => Mathf.Sqrt(Mathf.Max(slant * slant - altDelta * altDelta, 0f));

        /// <summary>
        /// The velocity a transform actually crosses the map at when the game moves it with
        /// <c>transform.Translate(vel * step, Space.World)</c> once per physics tick, as
        /// <c>Vessel.applyMainThrust</c> does (Vessel.cs:438-443).
        ///
        /// A Unity position is float32, so each tick's add rounds to the nearest representable value. Within
        /// one power of two those are evenly spaced, <c>ulp(p) = 2^(floor(log2 |p|) - 23)</c>, so each tick
        /// moves the coordinate by <c>round(v * step / ulp(p))</c> spacings and the realised rate is that
        /// times <c>ulp(p) / step</c>. Far enough out, a slow ship's increment rounds to zero and it does not
        /// move at all.
        ///
        /// Computed as the game computes it, one float32 add with the difference taken back, so the result carries the
        /// game's rounding. It holds while the coordinate stays inside one power of two. Returns <paramref name="vel"/>
        /// unchanged for a non-positive step.
        /// </summary>
        internal static Vector3 TranslateRoundedVelocity(Vector3 pos, Vector3 vel, float step)
        {
            if (!(step > 0f)) return vel;
            return new Vector3(TranslateRoundedRate(pos.x, vel.x, step),
                               TranslateRoundedRate(pos.y, vel.y, step),
                               TranslateRoundedRate(pos.z, vel.z, step));
        }

        private static float TranslateRoundedRate(float p, float v, float step)
        {
            // The casts pin each intermediate to float32, which C# otherwise lets a runtime hold at higher
            // precision. The subtraction is exact: both operands are within a factor of two.
            float inc = (float)(v * step);
            float moved = (float)(p + inc);
            return (float)(moved - p) / step;
        }

        /// <summary>
        /// Pitch from a transform's local euler angles, normalized from 0..360 into -180..180. The game's
        /// convention makes a climb negative (nose-down positive); callers flip it where they need
        /// climb-positive. A submarine's hull pitch is <c>Submarine.getPitch()</c>, a different quantity.
        /// </summary>
        internal static float PitchDeg(Transform t)
        {
            float x = t.localEulerAngles.x;
            return (x > 180f) ? x - 360f : x;
        }

        /// <summary>
        /// Elevation of a unit direction above the horizontal, in degrees. Clamped before the asin, so float
        /// error outside [-1, 1] gives +/-90 rather than NaN.
        /// </summary>
        internal static float ElevationDeg(Vector3 unitDir)
            => Mathf.Asin(Mathf.Clamp(unitDir.y, -1f, 1f)) * Mathf.Rad2Deg;
        /// <summary>
        /// <c>Quaternion.Euler</c> and <c>Quaternion.RotateTowards</c>, routed through here so the step loop
        /// can run outside a live Unity engine, where both engine-native calls raise SecurityException. The
        /// <c>AUTOTOT_OFFLINE</c> build defines managed equivalents; the shipped mod calls Unity. The rest of
        /// the <c>Quaternion</c> struct is managed code and needs no substitute.
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
            // Inverse of QEuler's Z, X, Y composition, in Unity's 0..360 range. Diagnostic only: the step
            // loop's trace reads it for the roll column.
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
