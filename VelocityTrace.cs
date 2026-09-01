using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Part H trace — bisects every fixed-step speed change of a coordinated KINEMATIC missile into
    /// the part <c>PerformMoveForward</c> makes (per decompile the ONLY velocity writer in flight:
    /// thrust − CalculateDrag) and everything else inside <c>OnFixedUpdate</c>. Round 1 proved the
    /// mystery brake lives INSIDE PerformMoveForward (dTotal == dPMF exactly, residual 0 on every
    /// window) — yet re-evaluating the game's own CalculateDrag at 15s snapshot states can't
    /// reproduce it. Round 2 therefore REPLAYS the drag call per physics step with the mover's OWN
    /// fast-varying inputs (<c>_currentPitch</c>, <c>_prevPitch</c> → pitchRate, position, speed),
    /// accumulated per log window as <c>dExp</c>:
    ///   dExp ≈ dPMF  → the brake IS the helper, driven by per-step pitch dynamics the snapshots
    ///                   missed → extract the pitch history and fix the integrator's terminal model;
    ///   dExp ≫ dPMF  → the mover feeds the helper an argument we haven't reproduced → the dumped
    ///                   per-step inputs (pitch min/avg/max, |pitchRate| avg) show which diverges.
    /// Verbose-only, coordinated shots only (same scoping as the other diagnostics).
    /// </summary>
    internal static class VelocityTrace
    {
        private sealed class State
        {
            public float V0;          // speed at OnFixedUpdate entry
            public float PmfV0;       // speed at PerformMoveForward entry
            public float PmfDelta;    // speed change made by this step's PerformMoveForward
            public float StepExpKn;   // helper-predicted Δ for the current step (kn, += = accel)
            public double SumTotal;   // kn accumulated over the log window
            public double SumPmf;
            public double SumExp;
            public double SumPitchAbs, SumPitchRateAbs;
            public float PitchMin, PitchMax;
            public int Steps;
            public int AnomalousSteps; // steps whose |residual| exceeded tolerance
            public double AnomalySum;  // kn of residual accumulated in those steps
            public float WindowStartSim = -1f;
        }

        private const float WindowSimSeconds = 5f;    // log window (sim clock)
        private const float ResidualToleranceKn = 0.05f; // per-step float-noise tolerance
        private const float KU = 0.0076554087f;       // knots -> Unity units/s (game constant)
        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        // ---- per-step mover inputs (name-based reflection: branch-safe, no baked IL types) ----
        private static bool _handlesResolved;
        private static FieldInfo _currentPitchField, _prevPitchField, _airLaunchedField;
        private static MethodInfo _dragMethod;    // MissileSimulator.CalculateDrag (10-arg, the mover's)
        private static MethodInfo _burnEndMethod; // MissileSimulator.BurnEndTime(ap, isAir)

        private static void EnsureHandles()
        {
            if (_handlesResolved) return;
            _handlesResolved = true;
            const BindingFlags FI = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type missile = typeof(Missile);
            _currentPitchField = missile.GetField("_currentPitch", FI);
            _prevPitchField = missile.GetField("_prevPitch", FI);
            // _airLaunched lives on WeaponBase in some builds — walk the hierarchy.
            for (Type t = missile; t != null && _airLaunchedField == null; t = t.BaseType)
                _airLaunchedField = t.GetField("_airLaunched", FI);
            const BindingFlags FS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            Type ms = missile.Assembly.GetType("SeaPower.MissileSimulator");
            if (ms != null)
            {
                _dragMethod = ms.GetMethod("CalculateDrag", FS, null, new Type[]
                {
                    typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                    typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float)
                }, null);
                _burnEndMethod = ms.GetMethod("BurnEndTime", new Type[]
                { typeof(AmmunitionParameters), typeof(bool) });
            }
            if (Coordinator.VerboseLog)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] vel-trace handles: pitch {(_currentPitchField != null)}, " +
                    $"prevPitch {(_prevPitchField != null)}, airLaunched {(_airLaunchedField != null)}, " +
                    $"drag {(_dragMethod != null)}, burnEnd {(_burnEndMethod != null)}, " +
                    $"fixedDt {GameClock.FixedDt():0.0000}");
        }

        private static float ReadFloat(FieldInfo f, object o)
        {
            object v = f.GetValue(o);
            if (v is double d) return (float)d;
            if (v is float fl) return fl;
            return 0f;
        }

        internal static void OnFixedPre(Missile m)
        {
            if (!Coordinator.VerboseLog) return;
            AmmunitionParameters ap = m._ap;
            if (ap == null || ap.Kinematics == AmmunitionParameters.KinematicsLevel.None) return;
            if (m._velocityInKnots < 100f) return; // still on the rail / just launched
            if (!EngagementBoard.IsCoordinated(m.CurrentIntendedTargetObject)) return;
            State s = Get(m);
            s.V0 = m._velocityInKnots;
            s.PmfDelta = 0f;
        }

        internal static void OnFixedPost(Missile m)
        {
            if (!Coordinator.VerboseLog) return;
            if (!_states.TryGetValue(m.GetInstanceID(), out State s)) return;
            try
            {
                if (m.IsDestroyed) { _states.Remove(m.GetInstanceID()); return; }

                float total = m._velocityInKnots - s.V0;
                float residual = total - s.PmfDelta;
                s.SumTotal += total;
                s.SumPmf += s.PmfDelta;
                s.Steps++;
                if (residual > ResidualToleranceKn || residual < -ResidualToleranceKn)
                {
                    s.AnomalousSteps++;
                    s.AnomalySum += residual;
                }

                float now = GameClock.SimNow();
                if (s.WindowStartSim < 0f) s.WindowStartSim = now;
                if (now - s.WindowStartSim < WindowSimSeconds) return;

                float altU = m.transform != null ? m.transform.position.y : 0f;
                float pitchAvg = s.Steps > 0 ? (float)(s.SumPitchAbs / s.Steps) : 0f;
                float rateAvg = s.Steps > 0 ? (float)(s.SumPitchRateAbs / s.Steps) : 0f;
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] vel-trace {m._ap._ammunitionFileName}#{m.GetInstanceID()}: " +
                    $"t+{now - GameClock.LaunchStamp(m):0}s v {m._velocityInKnots:0}kn alt {altU:0} " +
                    $"stage {m._flightStage}: dTotal {s.SumTotal:+0.0;-0.0}kn dPMF {s.SumPmf:+0.0;-0.0}kn " +
                    $"dExp {s.SumExp:+0.0;-0.0}kn residual {s.SumTotal - s.SumPmf:+0.0;-0.0}kn " +
                    $"over {s.Steps} steps ({s.AnomalousSteps} anom {s.AnomalySum:+0.0;-0.0}kn) " +
                    $"pitch {s.PitchMin:0}/{pitchAvg:0}/{s.PitchMax:0}° |pRate| {rateAvg:0}°/s");

                s.SumTotal = 0; s.SumPmf = 0; s.SumExp = 0; s.Steps = 0;
                s.SumPitchAbs = 0; s.SumPitchRateAbs = 0;
                s.PitchMin = 0f; s.PitchMax = 0f;
                s.AnomalousSteps = 0; s.AnomalySum = 0; s.WindowStartSim = now;
            }
            catch
            {
                _states.Remove(m.GetInstanceID());
            }
        }

        internal static void PmfPre(Missile m)
        {
            if (!_states.TryGetValue(m.GetInstanceID(), out State s)) return;
            s.PmfV0 = m._velocityInKnots;
            s.StepExpKn = 0f;
            try
            {
                EnsureHandles();
                if (_dragMethod == null || _currentPitchField == null) return;
                AmmunitionParameters ap = m._ap;
                if (ap == null || m.transform == null) return;

                float dt = GameClock.FixedDt();
                if (dt <= 0f) return;
                float pitch = ReadFloat(_currentPitchField, m);
                float prevPitch = _prevPitchField != null ? ReadFloat(_prevPitchField, m) : pitch;
                bool airLaunched = _airLaunchedField != null &&
                    _airLaunchedField.GetValue(m) is bool b && b;

                bool burning = false;
                if (_burnEndMethod != null)
                    burning = GameClock.SimNow() - GameClock.LaunchStamp(m)
                        < (float)_burnEndMethod.Invoke(null, new object[] { ap, airLaunched });

                ObjectBase tgt = m.CurrentTarget;
                float tgtAlt = (tgt != null && tgt.transform != null)
                    ? tgt.transform.position.y : m.transform.position.y;

                // The mover's EXACT call: PerformMoveForward subtracts this from _velocityInKnots.
                float dragKn = (float)_dragMethod.Invoke(null, new object[]
                {
                    m.transform.position.y,
                    m._velocityInKnots * KU,
                    dt,
                    pitch,
                    ap.GetDragFactor(airLaunched),
                    burning,
                    tgtAlt,
                    ap.LiftFactor,
                    ap.MinVelocity,
                    (pitch - prevPitch) / dt,
                });
                s.StepExpKn = -dragKn; // PMF does v -= drag

                s.SumExp += s.StepExpKn;
                s.SumPitchAbs += Math.Abs(pitch);
                s.SumPitchRateAbs += Math.Abs((pitch - prevPitch) / dt);
                if (s.Steps == 0 || pitch < s.PitchMin) s.PitchMin = pitch;
                if (s.Steps == 0 || pitch > s.PitchMax) s.PitchMax = pitch;
            }
            catch
            {
                // Diagnostic must never disturb the game; expected-replay just goes silent.
            }
        }

        internal static void PmfPost(Missile m)
        {
            if (_states.TryGetValue(m.GetInstanceID(), out State s))
                s.PmfDelta = m._velocityInKnots - s.PmfV0;
        }

        private static State Get(Missile m)
        {
            int id = m.GetInstanceID();
            if (!_states.TryGetValue(id, out State s))
            {
                s = new State();
                _states[id] = s;
            }
            return s;
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.OnFixedUpdate))]
    internal static class Missile_OnFixedUpdate_VelocityTrace
    {
        private static void Prefix(Missile __instance) => VelocityTrace.OnFixedPre(__instance);
        private static void Postfix(Missile __instance) => VelocityTrace.OnFixedPost(__instance);
    }

    [HarmonyPatch(typeof(Missile), "PerformMoveForward", new System.Type[] { typeof(Vector3) })]
    internal static class Missile_MoveForward_VelocityTrace
    {
        private static void Prefix(Missile __instance) => VelocityTrace.PmfPre(__instance);
        private static void Postfix(Missile __instance) => VelocityTrace.PmfPost(__instance);
    }
}
