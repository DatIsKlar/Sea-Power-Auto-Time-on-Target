using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The inputs the game builds a missile's
    /// intercept point from, and the launcher cell a round spawns in.
    ///
    /// The YJ-18A's stage switches are timed against <c>_targetInterceptPosition</c>, a lead along the
    /// known target velocity (decompiled-beta-new Missile.cs:374-376, CalculateLeadInterceptPoint), and
    /// a ship's round spawns at one container cell, <c>_gunObject</c> plus
    /// <c>_attachmentPositions[attachmentId]</c> (WeaponContainer.createAmmunitionObjectInstance).
    /// Neither input was ever recorded, so a model correction could only be proven with oracles. The
    /// guard trace carries the in-flight fields (<see cref="InsDrift"/> is the one private read); this
    /// class also describes the spawn cell once per round.
    ///
    /// Diagnostics only, written for TraceFlightModel captures. Remove with the plan that owns it.
    /// </summary>
    internal static class GuidanceStateProbe
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static FieldInfo _insDriftField;
        private static bool _resolved;

        /// <summary>The INS drift the game rolls per missile at launch (private on both branches); NaN when unreadable.</summary>
        internal static float InsDrift(Missile m)
        {
            if (!_resolved)
            {
                _resolved = true;
                _insDriftField = typeof(Missile).GetField("_insDriftValue", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            try { return _insDriftField?.GetValue(m) is float f ? f : float.NaN; }
            catch (Exception) { return float.NaN; }
        }

        /// <summary>
        /// Preformatted <c>.result</c> lines: where the round is at first sighting, against every cell of
        /// the weapon system it left from and against the launching ship's centre, and whether the
        /// launcher the mod picks by firing arc is the one it left.
        /// </summary>
        internal static string DescribeLaunchCell(WeaponBase w, ObjectBase tgt)
        {
            try
            {
                var b = new StringBuilder();
                Vector3 spawn = w.transform.position;
                ObjectBase platform = w._launchPlatform;
                b.AppendLine("LaunchSpawnPosU = " + V(spawn));
                if (platform != null && platform.transform != null)
                    b.AppendLine("LaunchCentreDistM = " + M(spawn - platform.transform.position));

                WeaponSystem ws = w._weaponSystem;
                if (ws == null)
                {
                    b.AppendLine("LaunchSystem = none");
                    return b.ToString();
                }
                string name;
                try { name = ws._vwp?._systemName ?? "?"; } catch (Exception) { name = "?"; }
                b.AppendLine("LaunchSystem = " + name);

                int cells = 0, bestContainer = -1, bestCell = -1;
                float best = float.MaxValue;
                Vector3 sum = Vector3.zero, bestResidualLocal = Vector3.zero;
                if (ws._containers != null)
                {
                    for (int c = 0; c < ws._containers.Count; c++)
                    {
                        WeaponContainer container = ws._containers[c];
                        if (container == null || container._gunObject == null || container._attachmentPositions == null)
                            continue;
                        Transform gun = container._gunObject.transform;
                        for (int a = 0; a < container._attachmentPositions.Count; a++)
                        {
                            Vector3 world = gun.TransformPoint(container._attachmentPositions[a]);
                            sum += world;
                            cells++;
                            float d = (world - spawn).magnitude;
                            if (d < best)
                            {
                                best = d;
                                bestContainer = container._containerIndex;
                                bestCell = a;
                                bestResidualLocal = gun.InverseTransformPoint(spawn) - container._attachmentPositions[a];
                            }
                        }
                    }
                }
                b.AppendLine("LaunchCellCount = " + cells.ToString(Inv));
                if (cells > 0)
                {
                    b.AppendLine($"LaunchCellNearest = {bestContainer}:{bestCell}:{(best * GameUnits.MetersPerUnity).ToString("0.00", Inv)}");
                    b.AppendLine("LaunchCellResidualLocal = " + V(bestResidualLocal));
                    b.AppendLine("LaunchCellsMeanDistM = " + M(sum / cells - spawn));
                }

                if (platform != null && tgt != null && w._ap != null)
                {
                    var picks = WeaponSelector.GetWeaponSystemsForAmmunition(
                        platform, tgt.transform.position, w._ap._ammunitionFileName, null);
                    bool match = picks != null && picks.Count > 0 && ReferenceEquals(picks[0], ws);
                    b.AppendLine("LaunchPickMatches = " + (match ? "true" : "false"));
                }
                return b.ToString();
            }
            catch (Exception e)
            {
                return "LaunchCellError = " + e.GetType().Name + System.Environment.NewLine;
            }
        }

        private static string V(Vector3 v) =>
            v.x.ToString("0.000", Inv) + "," + v.y.ToString("0.000", Inv) + "," + v.z.ToString("0.000", Inv);

        private static string M(Vector3 d) => (d.magnitude * GameUnits.MetersPerUnity).ToString("0.00", Inv);
    }
}
