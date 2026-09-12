using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SeaPower;
using UnityEngine;

namespace AutoTOT.Lab
{
    /// <summary>
    /// The capture format: reading a solver-input dump back into AmmunitionParameters and a
    /// SolveInput, including the reconstruction of fields older captures lack.
    /// </summary>
    internal static partial class Program
    {
        private static Dictionary<string, string> ReadDump(string file)
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return kv;
        }

        /// <summary>
        /// Rebuilds the ammunition object by field, because the game populates it from an ini at
        /// load and there is no constructor that takes values.
        /// </summary>
        private static AmmunitionParameters BuildAmmo(Dictionary<string, string> kv)
        {
            var ap = new AmmunitionParameters();
            Type t = typeof(AmmunitionParameters);

            // MinVelocity is not stored: the game computes it as min(_maxVelocityInKnots * 0.33,
            // 600), so setting the max speed below reproduces it. The dump still records it, and
            // the check after this block confirms the reconstruction agrees with what was logged.
            SetMember(t, ap, "LiftFactor", F(kv, "Ammo.LiftFactor"));
            SetMember(t, ap, "_acceleration", F(kv, "Ammo.Acceleration"));
            SetMember(t, ap, "_accelerationTime", F(kv, "Ammo.AccelerationTime"));
            SetMember(t, ap, "_sustainerBurnAcceleration", F(kv, "Ammo.SustainerBurnAcceleration"));
            SetMember(t, ap, "_sustainerBurnTime", F(kv, "Ammo.SustainerBurnTime"));
            SetMember(t, ap, "_maxVelocityInKnots", F(kv, "Ammo.MaxVelocityInKnots"));

            // Kinematics is derived too: the game reads a nullable bool out of the ini and
            // reports Full only when it is explicitly true.
            string kin = kv.GetValueOrDefault("Ammo.Kinematics", "None");
            SetMember(t, ap, "_useKinematics", (bool?)(kin == "Full"));

            if (kv.ContainsKey("Ammo.TerminalAltRaw"))
                SetMember(t, ap, "_terminalAltUnity", F(kv, "Ammo.TerminalAltRaw"));
            if (kv.ContainsKey("Ammo.TerminalAltMode"))
                SetMember(t, ap, "_terminalAltMode", kv["Ammo.TerminalAltMode"]);
            if (kv.ContainsKey("Ammo.LoftToSkim"))
                SetMember(t, ap, "_loftToSkim", B(kv, "Ammo.LoftToSkim"));
            if (kv.ContainsKey("Ammo.TargetType"))
                SetMember(t, ap, "_targetType", kv["Ammo.TargetType"]);
            if (kv.ContainsKey("Ammo.PerformsSlightBallisticInitialFlight"))
                SetMember(t, ap, "_performsSlightBallisticInitialFlight",
                    B(kv, "Ammo.PerformsSlightBallisticInitialFlight"));
            if (kv.ContainsKey("Ammo.TerminalPopUpDist"))
                SetMember(t, ap, "_terminalPopUpDist", F(kv, "Ammo.TerminalPopUpDist"));
            if (kv.ContainsKey("Ammo.TerminalPopUpHeight"))
                SetMember(t, ap, "_terminalPopUpHeight", F(kv, "Ammo.TerminalPopUpHeight"));
            if (kv.ContainsKey("Ammo.TerminalPopUpAngle"))
                SetMember(t, ap, "_terminalPopUpAngle", F(kv, "Ammo.TerminalPopUpAngle"));
            if (kv.ContainsKey("Ammo.TerminalVerticalTurnRate"))
                SetMember(t, ap, "_terminalVerticalTurnRate", F(kv, "Ammo.TerminalVerticalTurnRate"));

            // Captures made before optimized loft altitude was recorded can still be replayed:
            // these are the immutable ammunition inputs consumed by SearchOptimalLoftAltitude and
            // BuildAltitudeNodes. They are reconstructed from values already present in the dump;
            // no measured outcome is consulted.
            if (!B(kv, "NonKin") && B(kv, "Lofting"))
            {
                SetMember(t, ap, "_ammunitionFileName", kv.GetValueOrDefault("AmmoLabel", ""));
                bool optimizeLoft = kv.ContainsKey("Ammo.OptimizeLoftAlt")
                    ? B(kv, "Ammo.OptimizeLoftAlt")
                    // The game's ini default is true. A new-format RIM-174B capture confirms that
                    // family uses the default too: its long shot resolves near the ceiling while
                    // its short shot resolves to 0.5u. Treating every legacy RIM as false made the
                    // short capture replay a 499u loft it never flew.
                    : true;
                SetMember(t, ap, "_optimizeLoftAlt", optimizeLoft);
                SetMember(t, ap, "_maxLoftAltUnity", F(kv, "LoftAlt"));
                // Unless the special high-ballistic 90-degree boost was selected, BoostClimbDeg
                // is the loaded MaxLoftAngle itself and is stronger evidence than a hand-edited
                // backfill. This catches rim-174b: its capture says boost 80 / highBallistic false,
                // while the later backfill incorrectly assigned the default 30-degree tangent.
                float maxLoftAngle = !B(kv, "IsHighBallisticLofter") && F(kv, "BoostClimbDeg") > 0f
                    ? F(kv, "BoostClimbDeg") : TanToDegrees(F(kv, "LoftClimbTan"));
                SetMember(t, ap, "_maxLoftAngle", maxLoftAngle);
                float descentAngle = F(kv, "LoftDescentTan") > 0f
                    ? TanToDegrees(F(kv, "LoftDescentTan")) : F(kv, "DescentDeg");
                SetMember(t, ap, "_finalFlightPhaseMaxAngle", descentAngle);
                SetMember(t, ap, "_ballisticLoft", B(kv, "BallisticLoft"));
                SetMember(t, ap, "_maxFlightTime", F(kv, "MaxFlight"));
                SetMember(t, ap, "_terminalLoft", B(kv, "IsTerminalLoft"));
                SetMember(t, ap, "_terminalApproachDist", F(kv, "TermDist"));
                // TermAlt in these old dumps is ResolveStageProfile's fallback, not necessarily
                // the raw ammunition field. A non-terminal-loft round with no final-flight leg and
                // TermAlt equal to its loft ceiling (rim-174b) came from an unset TerminalAlt; using
                // the fallback here invents a second climb and truncates the Kappa waypoint.
                bool rawTerminalAltUnset = !B(kv, "IsTerminalLoft") &&
                    F(kv, "FinalFlightDist") <= 0f &&
                    Mathf.Abs(F(kv, "TermAlt") - F(kv, "LoftAlt")) <
                    SerializedValueTolerance;
                if (!kv.ContainsKey("Ammo.TerminalAltRaw"))
                    SetMember(t, ap, "_terminalAltUnity",
                        rawTerminalAltUnset ? -1f : F(kv, "TermAlt"));
                SetMember(t, ap, "_finalFlightPhaseDistToTargetUnity",
                    F(kv, "FinalFlightDist"));
                SetMember(t, ap, "_finalFlightPhaseAltUnity", F(kv, "FinalFlightAlt"));
                SetMember(t, ap, "_seaSkimmingStartDistToTargetUnity",
                    F(kv, "LoftEntryDist"));
                SetMember(t, ap, "_seaSkimmingAltUnity", F(kv, "CruiseAlt"));
                SetMember(t, ap, "_maxLoftVelocityInKnots", F(kv, "LoftVelKn"));
                SetMember(t, ap, "_terminalVelocityInKnots", F(kv, "TermVelKn"));
            }

            float expected = F(kv, "Ammo.MinVelocity");
            if (expected > 0f &&
                Mathf.Abs(ap.MinVelocity - expected) > SerializedValueTolerance)
                throw new InvalidOperationException(
                    $"rebuilt ammunition disagrees with the dump: MinVelocity {ap.MinVelocity} " +
                    $"but the game logged {expected}");

            return ap;
        }

        private static void SetMember(Type t, object target, string name, object value)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, Any);
            if (f != null) { f.SetValue(target, Coerce(value, f.FieldType)); return; }

            PropertyInfo p = t.GetProperty(name, Any);
            if (p != null && p.CanWrite) { p.SetValue(target, Coerce(value, p.PropertyType)); return; }

            // A backing field is still writable even when the property is get-only.
            FieldInfo backing = t.GetField($"<{name}>k__BackingField", Any);
            if (backing != null) { backing.SetValue(target, Coerce(value, backing.FieldType)); return; }

            throw new InvalidOperationException($"AmmunitionParameters has no writable member '{name}'");
        }

        /// <summary>Convert.ChangeType cannot target Nullable&lt;T&gt;, which _useKinematics is.</summary>
        private static object Coerce(object value, Type target)
        {
            Type inner = Nullable.GetUnderlyingType(target);
            if (inner != null) return value == null ? null : Convert.ChangeType(value, inner);
            if (target.IsEnum && value is string text) return Enum.Parse(target, text, true);
            return Convert.ChangeType(value, target);
        }

        private static float TanToDegrees(float tangent) =>
            Mathf.Atan(tangent) * Mathf.Rad2Deg;

        /// <summary>
        /// Recreate the launch-time TuneWaypoints result for the small set of historical SAM
        /// captures whose loft angles were already backfilled from their ammunition INIs. New
        /// captures carry ConfiguredLoftAlt plus resolved AltNodes and bypass this path.
        /// </summary>
        private static void RetuneLegacyLoft(Dictionary<string, string> kv,
                                             AmmunitionParameters ap)
        {
            if (kv.ContainsKey("ConfiguredLoftAlt") ||
                B(kv, "NonKin") || !B(kv, "Lofting"))
                return;

            Type ms = typeof(Missile).Assembly.GetType(FlightTime.MissileSimulatorType);
            const BindingFlags FStatic = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static;
            MethodInfo search = ms?.GetMethod(FlightTime.OptimalLoftMethodName, FStatic);
            MethodInfo build = ms?.GetMethods(FStatic).FirstOrDefault(m =>
                m.Name == FlightTime.AltitudeNodesMethodName &&
                m.GetParameters().Length == FlightTime.AltitudeNodesParameterCount);
            if (search == null || build == null)
                throw new InvalidOperationException("game optimized-loft surface is unavailable");

            Vector3 launchPos = V3(kv, "Pos");
            Vector3 targetPos = V3(kv, "TargetPos");
            Vector3 targetVel = V3(kv, "TargetVel");
            float configured = F(kv, "LoftAlt");
            float floor = Mathf.Max(Mathf.Max(launchPos.y, 0f), F(kv, "TargetAlt0"));
            float resolved = (float)search.Invoke(null, new object[]
            {
                ap, launchPos, targetPos, targetVel, F(kv, "VelKnots"), B(kv, "IsAir"),
                F(kv, "DragFactor"), floor, configured
            });
            resolved = Mathf.Clamp(resolved, floor, configured);

            object[] args =
            {
                ap, Mathf.Max(launchPos.y, 0f), F(kv, "TargetAlt0"),
                F(kv, "FlatDistTotal"), resolved, 0f
            };
            var nodes = build.Invoke(null, args) as List<Vector2>;
            if (nodes == null || nodes.Count < FlightTime.SampledCurveMinimumNodeCount)
                throw new InvalidOperationException("game returned no optimized loft nodes");

            kv["ConfiguredLoftAlt"] = configured.ToString("R", Inv);
            kv["LoftAlt"] = resolved.ToString("R", Inv);
            kv["KappaEndFlat"] = ((float)args[5]).ToString("R", Inv);
            kv["AltNodes"] = string.Join(";", nodes.Select(n =>
                n.x.ToString("R", Inv) + "," + n.y.ToString("R", Inv)));
        }

        private static FlightTime.SolveInput BuildInput(Dictionary<string, string> kv,
                                                        AmmunitionParameters ap,
                                                        bool trackDiag = false)
        {
            RetuneLegacyLoft(kv, ap);
            bool hasRawTerminal = kv.ContainsKey("Ammo.TerminalAltRaw") &&
                                  kv.ContainsKey("Ammo.TargetType");
            float configuredLoft = kv.ContainsKey("ConfiguredLoftAlt")
                ? F(kv, "ConfiguredLoftAlt") : F(kv, "LoftAlt");
            bool legacyRawTerminalUnset = !B(kv, "IsTerminalLoft") &&
                F(kv, "FinalFlightDist") <= 0f &&
                Mathf.Abs(F(kv, "TermAlt") - configuredLoft) < SerializedValueTolerance;
            bool terminalLinear = kv.ContainsKey("TerminalLinear")
                ? B(kv, "TerminalLinear")
                : !B(kv, "NonKin") && !B(kv, "IsTerminalLoft") && !B(kv, "IsAir") &&
                  F(kv, "TermDist") > 0f && (hasRawTerminal || legacyRawTerminalUnset);
            float termAlt = kv.ContainsKey("TerminalEndAlt")
                ? F(kv, "TerminalEndAlt")
                : hasRawTerminal && terminalLinear
                    ? FlightTime.ResolveKinematicTerminalEndAlt(ap, F(kv, "TargetAlt0"))
                    : terminalLinear && legacyRawTerminalUnset
                        ? F(kv, "TargetAlt0")
                        : F(kv, "TermAlt");
            return new FlightTime.SolveInput(
                altNodes: Nodes(kv, "AltNodes"),
                ammoLabel: kv.GetValueOrDefault("AmmoLabel", ""),
                boostClimbDeg: F(kv, "BoostClimbDeg"),
                decelRate: F(kv, "DecelRate"),
                descentDeg: F(kv, "DescentDeg"),
                descentOnsetDeg: F(kv, "DescentOnsetDeg"),
                dragFactor: F(kv, "DragFactor"),
                cruiseAlt: F(kv, "CruiseAlt"),
                finalFlightAlt: F(kv, "FinalFlightAlt"),
                finalFlightDist: F(kv, "FinalFlightDist"),
                finalDist: F(kv, "FinalDist"),
                flatDistTotal: F(kv, "FlatDistTotal"),
                initialPhaseDur: F(kv, "InitialPhaseDur"),
                performsSlightBallisticInitialFlight: kv.ContainsKey("PerformsSlightBallisticInitialFlight")
                    ? B(kv, "PerformsSlightBallisticInitialFlight") : false,
                isAir: B(kv, "IsAir"),
                isHighBallisticLofter: B(kv, "IsHighBallisticLofter"),
                isTerminalLoft: B(kv, "IsTerminalLoft"),
                launchPitch: F(kv, "LaunchPitch"),
                configuredLoftAlt: kv.ContainsKey("ConfiguredLoftAlt")
                    ? F(kv, "ConfiguredLoftAlt") : F(kv, "LoftAlt"),
                loftAlt: F(kv, "LoftAlt"),
                loftEntryDist: F(kv, "LoftEntryDist"),
                loftSpeedHoldDist: F(kv, "LoftSpeedHoldDist"),
                loftVelKn: F(kv, "LoftVelKn"),
                ballisticLoft: B(kv, "BallisticLoft"),
                loftClimbTan: F(kv, "LoftClimbTan"),
                loftDescentTan: F(kv, "LoftDescentTan"),
                launchAltU: F(kv, "LaunchAltU"),
                lofting: B(kv, "Lofting"),
                maxFlight: F(kv, "MaxFlight"),
                maxVelKn: F(kv, "MaxVelKn"),
                nonKin: B(kv, "NonKin"),
                targetAlt0: F(kv, "TargetAlt0"),
                targetPos: V3(kv, "TargetPos"),
                targetVel: V3(kv, "TargetVel"),
                terminalLinear: terminalLinear,
                termAlt: termAlt,
                termDist: F(kv, "TermDist"),
                termVelKn: F(kv, "TermVelKn"),
                terminalPopUpEnabled: kv.ContainsKey("TerminalPopUpEnabled") && B(kv, "TerminalPopUpEnabled"),
                terminalPopUpDist: kv.ContainsKey("TerminalPopUpDist") ? F(kv, "TerminalPopUpDist") : 0f,
                terminalPopUpHeight: kv.ContainsKey("TerminalPopUpHeight") ? F(kv, "TerminalPopUpHeight") : 0f,
                terminalPopUpAngle: kv.ContainsKey("TerminalPopUpAngle") ? F(kv, "TerminalPopUpAngle") : 60f,
                terminalVerticalTurnRate: kv.ContainsKey("TerminalVerticalTurnRate")
                    ? F(kv, "TerminalVerticalTurnRate") : F(kv, "TurnRateBase"),
                trackDiag: trackDiag,   // off unless the trace command asked
                turnRate: F(kv, "TurnRate"),
                toBearingTurnRate: F(kv, "ToBearingTurnRate"),
                turnRateBase: F(kv, "TurnRateBase"),
                turnDerateThresholdKn: F(kv, "TurnDerateThresholdKn"),
                altLatchPhase: I(kv, "AltLatchPhase"),
                altLatched: B(kv, "AltLatched"),
                att: Q(kv, "Att"),
                launchHeading: V3(kv, "LaunchHeading"),
                nextSample: F(kv, "NextSample"),
                pos: V3(kv, "Pos"),
                prevAltErr: F(kv, "PrevAltErr"),
                prevFlat: F(kv, "PrevFlat"),
                prevPitch: F(kv, "PrevPitch"),
                t: F(kv, "T"),
                tlGliding: B(kv, "TlGliding"),
                kappaEndFlat: F(kv, "KappaEndFlat"),
                kappaProgress: F(kv, "KappaProgress"),
                velKnots: F(kv, "VelKnots"),
                legs: Legs(kv),
                // Every recorded target is a ship, so GroupSize alone decides leadership.
                groupLeader: F(kv, "Ammo.MaxGroupSize") > 1f);
        }

        /// <summary>Raw fields Missile.CreateWaypointConfigs reads. A capture that lacks any of
        /// them flies the older altitude law, exactly as it did before the route was ported.</summary>
        private static readonly string[] RouteFields =
        {
            "Ammo.MaxLoftAltUnity", "Ammo.MaxLoftAngle", "Ammo.LoftToSkim",
            "Ammo.SeaSkimmingStartDistToTargetUnity", "Ammo.SeaSkimmingAltUnity",
            "Ammo.SeaSkimmingMaxDescentAngle", "Ammo.FinalFlightPhaseDistToTargetUnity",
            "Ammo.FinalFlightPhaseAltUnity", "Ammo.FinalFlightPhaseMaxAngle",
            "Ammo.TerminalApproachDist", "Ammo.TerminalAltRaw", "Ammo.TerminalLoft",
            "Ammo.TerminalDiveDistance", "Ammo.TargetType", "Ammo.TerrainFollowFlight",
        };

        /// <summary>Rebuild the waypoint route the same way the mod's setup does. The capture does
        /// not record whether the target was airborne, so an AAW round stands in for that test;
        /// every non-kinematic round in the corpus was fired at a ship.</summary>
        private static FlightTime.WaypointLeg[] Legs(Dictionary<string, string> kv)
        {
            if (RouteFields.Any(k => !kv.ContainsKey(k))) return null;
            if (!B(kv, "NonKin") || B(kv, "IsTerminalLoft") || kv["Ammo.TargetType"] == "AAW")
                return null;
            FlightTime.WaypointLeg[] legs = FlightTime.BuildNonKinLegs(
                F(kv, "FlatDistTotal"), F(kv, "TargetAlt0"),
                F(kv, "Ammo.MaxLoftAltUnity"), F(kv, "Ammo.MaxLoftAngle"), B(kv, "Ammo.LoftToSkim"),
                F(kv, "Ammo.SeaSkimmingStartDistToTargetUnity"), F(kv, "Ammo.SeaSkimmingAltUnity"),
                F(kv, "Ammo.SeaSkimmingMaxDescentAngle"),
                F(kv, "Ammo.FinalFlightPhaseDistToTargetUnity"), F(kv, "Ammo.FinalFlightPhaseAltUnity"),
                F(kv, "Ammo.FinalFlightPhaseMaxAngle"),
                F(kv, "Ammo.TerminalApproachDist"), F(kv, "Ammo.TerminalAltRaw"),
                B(kv, "Ammo.TerminalLoft"), F(kv, "Ammo.TerminalDiveDistance"),
                kv.ContainsKey("TerminalPopUpEnabled") && B(kv, "TerminalPopUpEnabled"),
                kv.ContainsKey("TerminalPopUpDist") ? F(kv, "TerminalPopUpDist") : 0f,
                B(kv, "Ammo.TerrainFollowFlight"),
                // Every recorded target is a ship, so GroupSize alone decides leadership.
                F(kv, "Ammo.MaxGroupSize") > 1f && kv.ContainsKey("Ammo.GroupLeaderAltUnity")
                    ? F(kv, "Ammo.GroupLeaderAltUnity") : -1f);
            // A v3 capture records the route the mod built in game. The two must agree, or the
            // replay is not replaying what the round was solved with.
            if (kv.TryGetValue("WaypointLegs", out string recorded) && legs != null)
            {
                string rebuilt = string.Join(";", legs.Select(l => l.ToString()));
                if (rebuilt != recorded)
                    Console.Error.WriteLine($"route mismatch {kv.GetValueOrDefault("Key", "?")}: " +
                                            $"game {recorded} lab {rebuilt}");
            }
            return legs;
        }

        private static float F(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v.Length > 0 ? float.Parse(v, Inv) : 0f;

        private static int I(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v.Length > 0 ? int.Parse(v, Inv) : 0;

        private static bool B(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string v) && v == "true";

        private static Vector3 V3(Dictionary<string, string> kv, string k)
        {
            string[] p = kv.GetValueOrDefault(k, "0,0,0").Split(',');
            return new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv));
        }

        private static Quaternion Q(Dictionary<string, string> kv, string k)
        {
            string[] p = kv.GetValueOrDefault(k, "0,0,0,1").Split(',');
            return new Quaternion(float.Parse(p[0], Inv), float.Parse(p[1], Inv),
                                  float.Parse(p[2], Inv), float.Parse(p[3], Inv));
        }

        private static Vector2[] Nodes(Dictionary<string, string> kv, string k)
        {
            string v = kv.GetValueOrDefault(k, "");
            if (string.IsNullOrEmpty(v)) return null;
            return v.Split(';').Select(pair =>
            {
                string[] p = pair.Split(',');
                return new Vector2(float.Parse(p[0], Inv), float.Parse(p[1], Inv));
            }).ToArray();
        }
    }
}
