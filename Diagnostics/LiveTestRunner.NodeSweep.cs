using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The NodeSweep plan key: the model's ported <c>FlightTime.AltitudeNodes</c> against game 0.9.0's own
    /// <c>MissileSimulator.BuildAltitudeNodes(ref FlyoutBuffers, in MissileParams, ...)</c>, which needs Unity
    /// native buffers and so only runs inside the game. Every missile the game can load gets the geometry grid
    /// of <see cref="AltitudeNodeCheck"/>, then randomised ammunition reaches the branches real data does not.
    /// Nothing is fired. Plan: docs/plans/open/2026-11-12-game-090-bindings.md, D-A3.
    /// </summary>
    internal sealed partial class LiveTestRunner
    {
        private const int NodeSweepFuzzCases = 100000;

        private void RunNodeSweep()
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
            Type ms = typeof(Missile).Assembly.GetType(FlightTime.MissileSimulatorType);
            Type bufType = ms?.GetNestedType("FlyoutBuffers", Any);
            Type paramsType = ms?.GetNestedType("MissileParams", Any);
            MethodInfo create = bufType?.GetMethod("Create", Any | BindingFlags.Static, null, Type.EmptyTypes, null);
            MethodInfo dispose = bufType?.GetMethod("DisposeNative", Any | BindingFlags.Instance, null, Type.EmptyTypes, null);
            MethodInfo from = paramsType?.GetMethod("From", Any | BindingFlags.Static, null,
                                                    new[] { typeof(AmmunitionParameters) }, null);
            MethodInfo build = bufType == null || paramsType == null ? null : ms.GetMethod(
                FlightTime.AltitudeNodesMethodName, Any | BindingFlags.Static, null,
                new[]
                {
                    bufType.MakeByRefType(), paramsType.MakeByRefType(), typeof(float), typeof(float),
                    typeof(float), typeof(float), typeof(float).MakeByRefType()
                }, null);
            FieldInfo nodesField = bufType?.GetField("Nodes", Any | BindingFlags.Instance);
            Type listType = nodesField?.FieldType;
            FieldInfo countField = listType?.GetField("Count", Any | BindingFlags.Instance);
            FieldInfo overflowField = listType?.GetField("Overflowed", Any | BindingFlags.Instance);
            PropertyInfo item = listType?.GetProperty("Item", Any | BindingFlags.Instance);
            if (create == null || dispose == null || from == null || build == null || countField == null || item == null)
            {
                string missing = $"create {create != null}, dispose {dispose != null}, from {from != null}, " +
                                 $"build {build != null}, count {countField != null}, item {item != null}";
                Bootstrap.Log.LogWarning("[AutoTOT] livetest node sweep: 0.9.0 node API not found: " + missing);
                _notes.Add("node sweep: 0.9.0 node API not found (" + missing + ")");
                return;
            }

            object buffers = create.Invoke(null, null);
            int overflows = 0;
            AltitudeNodeCheck.Reference reference =
                (AmmunitionParameters ap, float la, float ta, float flat, float lo, out float end) =>
                {
                    object[] a = { buffers, from.Invoke(null, new object[] { ap }), la, ta, flat, lo, 0f };
                    build.Invoke(null, a);
                    buffers = a[0];
                    end = (float)a[6];
                    object list = nodesField.GetValue(buffers);
                    if (overflowField != null && (bool)overflowField.GetValue(list)) overflows++;
                    int count = (int)countField.GetValue(list);
                    var nodes = new List<Vector2>(count);
                    for (int i = 0; i < count; i++) nodes.Add((Vector2)item.GetValue(list, new object[] { i }));
                    return nodes;
                };

            var clock = Stopwatch.StartNew();
            var grid = new AltitudeNodeCheck(reference);
            var random = new AltitudeNodeCheck(reference);
            int missiles = 0, kinematic = 0, unreadable = 0;
            try
            {
                foreach (string file in Singleton<FileManager>.Instance.GetFiles("ammunition", "*.ini"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name == "damage") continue;
                    // Built the way the game's own missile benchmark builds them (DM_MissileGolden.SweepAmmo).
                    AmmunitionParameters ap;
                    try { ap = new AmmunitionParameters(name, 0, null); }
                    catch (Exception) { unreadable++; continue; }
                    if (ap._type != Ammunition.Type.Missile) continue;
                    missiles++;
                    if (ap.Kinematics == AmmunitionParameters.KinematicsLevel.None &&
                        ap._targetType != Ammunition.Target.AAW)
                        continue;
                    kinematic++;
                    grid.GridFor(name, ap);
                }

                var rng = new System.Random(20261112);
                for (int i = 0; i < NodeSweepFuzzCases; i++)
                {
                    var ap = new AmmunitionParameters();
                    AltitudeNodeCheck.Randomise(ap, rng);
                    float[] g = AltitudeNodeCheck.RandomGeometry(rng);
                    random.Compare($"fuzz#{i}", ap, g[0], g[1], g[2], g[3]);
                }
            }
            finally
            {
                dispose.Invoke(buffers, null);
            }

            var lines = new List<string>
            {
                $"node sweep: {missiles} missiles in the game's files, {kinematic} kinematic or AAW, {unreadable} unreadable, " +
                $"{clock.Elapsed.TotalSeconds:0.0}s, {overflows} game node-list overflows",
                grid.Summary("node sweep grid (game 0.9.0 vs port)"),
                random.Summary("node sweep fuzz (game 0.9.0 vs port)"),
            };
            foreach (string s in grid.Samples) lines.Add("  grid " + s);
            foreach (string s in random.Samples) lines.Add("  fuzz " + s);
            foreach (string s in lines)
            {
                Bootstrap.Log.LogInfo("[AutoTOT] livetest " + s);
                _notes.Add(s);
            }
        }
    }
}
