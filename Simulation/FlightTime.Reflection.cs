using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        // Game-side names this mod resolves by reflection. Named because each was spelled out at
        // three sites here (primary lookup, arity fallback, diagnostic dump) and again in
        // WaypointSim, and a typo in any one copy fails silently as "method not found".
        internal const string MissileSimulatorType = "SeaPower.MissileSimulator";
        internal const string ThrustMethodName = "CalculateThrustOverTime";
        internal const string DragMethodName = "CalculateDrag";
        internal const string LoftCapMethodName = "LoftCap";

        private static MethodInfo _simulateShotMethod;
        private static bool _simulateLookedUp;
        private static bool _simIsBeta;

        /// <summary>
        /// Which game branch the integrator bound against. Recorded with every corpus entry: the
        /// two branches expose different simulation surfaces, so a round captured on one is not
        /// necessarily reproducible on the other.
        /// </summary>
        internal static bool SimIsBeta { get { EnsureSimLookup(); return _simIsBeta; } }

        private static MethodInfo _thrustMethod;
        private static MethodInfo _dragMethod;

        // The integrator calls these two once per 0.1s step, so a 12-minute flight is tens of
        // thousands of calls and a busy frame is far more. MethodInfo.Invoke boxes every argument on
        // every call; a bound delegate does neither. The MethodInfo lookup above stays as the thing
        // that finds the method (the branches differ in signature), and these are bound from
        // whatever it found. Null when binding fails, in which case the Invoke path still runs.
        private delegate float ThrustFn(AmmunitionParameters ap, bool isAirLaunched,
                                        float timeSinceLaunch, float timeWindow);
        private delegate float DragFn(float altitude, float velocity, float time, float pitch,
                                      float dragFactor, bool motorBurning, float targetAltitude,
                                      float liftFactor, float stallSpeedKnots, float pitchRateDegPerSec);
        private static ThrustFn _thrustFn;
        private static DragFn _dragFn;
        private static MethodInfo _loftCapMethod;
        private static MethodInfo _altNodesMethod;

        private static void EnsureSimLookup()
        {
            if (!_simulateLookedUp)
            {
                _simulateLookedUp = true;
                _simulateShotMethod = typeof(Missile).GetMethod("SimulateShotLinear", new Type[]
                {
                    typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                    typeof(bool), typeof(float), typeof(float), typeof(float),
                    typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(float), typeof(bool)
                });
                _simIsBeta = false;
                if (_simulateShotMethod == null)
                {
                    Type ms = typeof(Missile).Assembly.GetType(MissileSimulatorType);
                    _simulateShotMethod = ms?.GetMethod("EstimateShot", new Type[]
                    {
                        typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                        typeof(bool), typeof(float), typeof(float), typeof(float), typeof(bool),
                        typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(bool)
                    });
                    _simIsBeta = _simulateShotMethod != null;

                    if (_simIsBeta)
                    {
                        _thrustMethod = ms.GetMethod(ThrustMethodName, new Type[]
                        { typeof(AmmunitionParameters), typeof(bool), typeof(float), typeof(float) });
                        _dragMethod = ms.GetMethod(DragMethodName, new Type[]
                        {
                            typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                            typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float)
                        });
                        _loftCapMethod = ms.GetMethod(LoftCapMethodName, new Type[]
                        { typeof(AmmunitionParameters), typeof(float), typeof(float) });

                        if (_thrustMethod == null)
                            _thrustMethod = ResolveByName(ms, ThrustMethodName, 4);
                        if (_dragMethod == null)
                            _dragMethod = ResolveByName(ms, DragMethodName, 10);
                        if (_loftCapMethod == null)
                            _loftCapMethod = ResolveByName(ms, LoftCapMethodName, 3);

                        const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;
                        _altNodesMethod = ms.GetMethod("BuildAltitudeNodes", NPS, null, new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(float), typeof(float), typeof(float),
                            typeof(float), typeof(float).MakeByRefType()
                        }, null);

                        BindFastPath();

                        LogSimInit(ms);
                        WaypointSim.EnsureLookup(typeof(Missile).Assembly);
                    }
                }
                LogEstimatorBinding();
            }
        }

        /// <summary>
        /// Report which estimator the integrator actually bound to, once per session and regardless
        /// of the verbose setting.
        ///
        /// This is deliberately not behind <c>VerboseLog</c>. The integrator needs the beta
        /// <c>MissileSimulator</c> internals; on the public branch it finds
        /// <c>Missile.SimulateShotLinear</c> instead, binds nothing, and declines every call, so
        /// every flight time silently comes from <c>MaxRangePrecise</c>. That degradation is correct
        /// behaviour but it is invisible, and the rule for performance runs is that verbose logging
        /// stays off, so the one configuration that hid it was the one used to measure. A single
        /// line per session makes "which estimator produced these numbers" answerable in any log.
        /// </summary>
        private static void LogEstimatorBinding()
        {
            bool integratorReady = _simIsBeta && _thrustMethod != null;
            string branch = _simIsBeta ? "beta" : "public";
            if (integratorReady)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] estimator: integrator ACTIVE ({branch} branch, " +
                    $"fastPath {(_thrustFn != null && _dragFn != null)})");
            }
            else
            {
                // Names MaxRangePrecise alone, deliberately. WaypointSim is beta-only by design:
                // EnsureLookup is called from the _simIsBeta branch below, so off beta it never
                // initializes, Ready stays false, and KinematicRaw skips that tier entirely.
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] estimator: integrator UNAVAILABLE on the {branch} branch " +
                    $"(SimulateShotLinear {(_simulateShotMethod != null && !_simIsBeta)}, " +
                    $"thrust {(_thrustMethod != null)}). Flight times will fall back to " +
                    $"MaxRangePrecise, which is less accurate. Time-on-target " +
                    $"coordination still works; timing precision does not match the tested model.");
            }
        }

        /// <summary>
        /// Bind the per-step thrust and drag calls to typed delegates. Purely a performance step:
        /// binding preserves the signature exactly, and a failure is not fatal, since the integrator
        /// falls back to invoking the MethodInfo.
        /// </summary>
        private static void BindFastPath()
        {
            try
            {
                if (_thrustMethod != null)
                    _thrustFn = (ThrustFn)Delegate.CreateDelegate(typeof(ThrustFn), _thrustMethod, false);
                if (_dragMethod != null)
                    _dragFn = (DragFn)Delegate.CreateDelegate(typeof(DragFn), _dragMethod, false);
            }
            catch (Exception e)
            {
                _thrustFn = null; _dragFn = null;
                ModLog.VerboseWarn("fast-path bind failed", e);
            }
        }

        private static MethodInfo ResolveByName(Type t, string name, int paramCount)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo best = null;
            foreach (MethodInfo m in t.GetMethods(F))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != paramCount) continue;
                bool byRef = false;
                foreach (ParameterInfo p in ps) if (p.ParameterType.IsByRef) { byRef = true; break; }
                if (byRef) continue;
                if (best == null) best = m;
            }
            return best;
        }

        private static void LogSimInit(Type ms)
        {
            if (!Coordinator.VerboseLog) return;
            try
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] sim-init: beta {_simIsBeta}, thrust {(_thrustMethod != null)}, " +
                    $"drag {(_dragMethod != null)}, loftCap {(_loftCapMethod != null)}, " +
                    $"altNodes {(_altNodesMethod != null)}, " +
                    $"fastPath {(_thrustFn != null && _dragFn != null)}");
                foreach (var pair in new (string name, MethodInfo mi)[]
                {
                    (ThrustMethodName, _thrustMethod), (DragMethodName, _dragMethod),
                    (LoftCapMethodName, _loftCapMethod),
                })
                {
                    if (pair.mi != null) continue;
                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    foreach (MethodInfo m in ms.GetMethods(F))
                    {
                        if (m.Name != pair.name) continue;
                        string sig = string.Join(", ", Array.ConvertAll(m.GetParameters(),
                            p => $"{p.ParameterType.Name} {p.Name}"));
                        Bootstrap.Log.LogInfo($"[AutoTOT] sim-init candidate {m.Name}({sig})");
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("sim-init dump failed", e);
            }
        }
    }
}
