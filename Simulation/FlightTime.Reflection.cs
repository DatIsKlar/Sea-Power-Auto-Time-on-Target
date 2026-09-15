using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        // Game-side names resolved by reflection, named once because a typo in any copy fails silently as "method not
        // found".
        internal const string MissileSimulatorType = "SeaPower.MissileSimulator";
        internal const string ThrustMethodName = "CalculateThrustOverTime";
        internal const string DragMethodName = "CalculateDrag";
        internal const string LoftCapMethodName = "LoftCap";
        internal const string OptimalLoftMethodName = "SearchOptimalLoftAltitude";
        internal const string AltitudeNodesMethodName = "BuildAltitudeNodes";
        internal const int OptimalLoftParameterCount = 9;
        internal const int AltitudeNodesParameterCount = 6;

        // AmmunitionParameters fields that do not exist on the public branch. Mono faults a method reading a
        // missing field when it compiles the method, before any guard inside can run, so both are read by
        // reflection (as LauncherFacts reads WeaponParameters._automatic). Null on public.
        internal static readonly FieldInfo BallisticLoftField =
            typeof(AmmunitionParameters).GetField("_ballisticLoft", BindingFlags.Public | BindingFlags.Instance);
        internal static readonly FieldInfo OptimizeLoftAltField =
            typeof(AmmunitionParameters).GetField("_optimizeLoftAlt", BindingFlags.Public | BindingFlags.Instance);
        private static bool _ballisticLoftMissWarned;

        /// <summary>
        /// <c>ap._ballisticLoft</c>. Reached only once the integrator has bound, which needs the newer game
        /// build, so a null field means a game update renamed it: said once, and read as false, flying a
        /// ballistic lofter as an ordinary one.
        /// </summary>
        private static bool BallisticLoftOf(AmmunitionParameters ap)
        {
            if (BallisticLoftField != null) return (bool)BallisticLoftField.GetValue(ap);
            if (!_ballisticLoftMissWarned)
            {
                _ballisticLoftMissWarned = true;
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] estimator: beta branch detected, but AmmunitionParameters._ballisticLoft " +
                    "was not found. Ballistic-loft rounds are flown as ordinary lofters; a game update " +
                    "has probably renamed the field.");
            }
            return false;
        }

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

        // The integrator calls these two once per 0.1 s step, tens of thousands of calls on a long flight.
        // MethodInfo.Invoke boxes every argument; a bound delegate does not. The MethodInfo lookup still
        // finds the method (signatures differ by branch), and these are bound from it. Null when binding
        // fails, in which case Invoke runs.
        private delegate float ThrustFn(AmmunitionParameters ap, bool isAirLaunched,
                                        float timeSinceLaunch, float timeWindow);
        private delegate float DragFn(float altitude, float velocity, float time, float pitch,
                                      float dragFactor, bool motorBurning, float targetAltitude,
                                      float liftFactor, float stallSpeedKnots, float pitchRateDegPerSec);
        private static ThrustFn _thrustFn;
        private static DragFn _dragFn;
        private static MethodInfo _loftCapMethod;
        private static MethodInfo _altNodesMethod;
        private static MethodInfo _optimalLoftMethod;

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
                        _optimalLoftMethod = ms.GetMethod(OptimalLoftMethodName, new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(Vector3), typeof(Vector3),
                            typeof(Vector3), typeof(float), typeof(bool), typeof(float),
                            typeof(float), typeof(float)
                        });

                        if (_thrustMethod == null)
                            _thrustMethod = ResolveByName(ms, ThrustMethodName, 4);
                        if (_dragMethod == null)
                            _dragMethod = ResolveByName(ms, DragMethodName, 10);
                        if (_loftCapMethod == null)
                            _loftCapMethod = ResolveByName(ms, LoftCapMethodName, 3);
                        if (_optimalLoftMethod == null)
                            _optimalLoftMethod = ResolveByName(ms, OptimalLoftMethodName,
                                                               OptimalLoftParameterCount);

                        const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;
                        _altNodesMethod = ms.GetMethod(AltitudeNodesMethodName, NPS, null, new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(float), typeof(float), typeof(float),
                            typeof(float), typeof(float).MakeByRefType()
                        }, null);

                        BindFastPath();

                        // Either miss changes the altitude law lofting rounds are flown on, and the
                        // sim-init line that would name it is verbose-only. Said once, ungated.
                        if (_optimalLoftMethod == null || _altNodesMethod == null)
                            Bootstrap.Log.LogWarning(
                                "[AutoTOT] estimator: beta branch detected, but " +
                                (_optimalLoftMethod == null ? OptimalLoftMethodName + " " : "") +
                                (_altNodesMethod == null ? AltitudeNodesMethodName + " " : "") +
                                "did not bind. Lofting rounds fall back to the model's own altitude " +
                                "law and may be several seconds off; a game update has probably " +
                                "changed the method.");

                        LogSimInit(ms);
                    }
                }
                LogEstimatorBinding();
            }
        }

        /// <summary>
        /// Report which estimator the integrator bound to, once per session regardless of VerboseLog: on the
        /// public branch it binds nothing and every flight time silently comes from <c>MaxRangePrecise</c>.
        /// Correct, but invisible in a log taken with verbose logging off, which is how performance runs are taken.
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
            foreach (MethodInfo m in t.GetMethods(F))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != paramCount) continue;
                bool byRef = false;
                foreach (ParameterInfo p in ps) if (p.ParameterType.IsByRef) { byRef = true; break; }
                if (!byRef) return m;   // the first match, as before
            }
            return null;
        }

        private static void LogSimInit(Type ms)
        {
            if (!Coordinator.VerboseLog) return;
            try
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] sim-init: beta {_simIsBeta}, thrust {(_thrustMethod != null)}, " +
                    $"drag {(_dragMethod != null)}, loftCap {(_loftCapMethod != null)}, " +
                    $"optimalLoft {(_optimalLoftMethod != null)}, " +
                    $"altNodes {(_altNodesMethod != null)}, " +
                    $"fastPath {(_thrustFn != null && _dragFn != null)}");
                foreach (var pair in new (string name, MethodInfo mi)[]
                {
                    (ThrustMethodName, _thrustMethod), (DragMethodName, _dragMethod),
                    (LoftCapMethodName, _loftCapMethod), (OptimalLoftMethodName, _optimalLoftMethod),
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
