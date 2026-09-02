using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {
        private static MethodInfo _simulateShotMethod;
        private static bool _simulateLookedUp;
        private static bool _simIsBeta;

        private static MethodInfo _thrustMethod;
        private static MethodInfo _dragMethod;
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
                    Type ms = typeof(Missile).Assembly.GetType("SeaPower.MissileSimulator");
                    _simulateShotMethod = ms?.GetMethod("EstimateShot", new Type[]
                    {
                        typeof(AmmunitionParameters), typeof(Vector3), typeof(float), typeof(Vector3), typeof(Vector3),
                        typeof(bool), typeof(float), typeof(float), typeof(float), typeof(bool),
                        typeof(List<Vector3>), typeof(List<float>), typeof(List<float>), typeof(bool)
                    });
                    _simIsBeta = _simulateShotMethod != null;

                    if (_simIsBeta)
                    {
                        _thrustMethod = ms.GetMethod("CalculateThrustOverTime", new Type[]
                        { typeof(AmmunitionParameters), typeof(bool), typeof(float), typeof(float) });
                        _dragMethod = ms.GetMethod("CalculateDrag", new Type[]
                        {
                            typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                            typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float)
                        });
                        _loftCapMethod = ms.GetMethod("LoftCap", new Type[]
                        { typeof(AmmunitionParameters), typeof(float), typeof(float) });

                        if (_thrustMethod == null)
                            _thrustMethod = ResolveByName(ms, "CalculateThrustOverTime", 4);
                        if (_dragMethod == null)
                            _dragMethod = ResolveByName(ms, "CalculateDrag", 10);
                        if (_loftCapMethod == null)
                            _loftCapMethod = ResolveByName(ms, "LoftCap", 3);

                        const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;
                        _altNodesMethod = ms.GetMethod("BuildAltitudeNodes", NPS, null, new Type[]
                        {
                            typeof(AmmunitionParameters), typeof(float), typeof(float), typeof(float),
                            typeof(float), typeof(float).MakeByRefType()
                        }, null);

                        LogSimInit(ms);
                        WaypointSim.EnsureLookup(typeof(Missile).Assembly);
                    }
                }
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
                    $"altNodes {(_altNodesMethod != null)}");
                foreach (var pair in new (string name, MethodInfo mi)[]
                {
                    ("CalculateThrustOverTime", _thrustMethod), ("CalculateDrag", _dragMethod),
                    ("LoftCap", _loftCapMethod),
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
                Bootstrap.Log.LogWarning($"[AutoTOT] sim-init dump failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
