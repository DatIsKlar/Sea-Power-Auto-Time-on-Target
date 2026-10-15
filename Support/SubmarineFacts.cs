using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Read-only snapshot of a submarine shooter's launch-depth state: reads the game's launch-depth gate
    /// (WeaponSystemLauncher.cs:414-421) and reports whether depth holds the launcher. All reads are
    /// null-guarded and public on both branches. See docs/ARCHITECTURE.md "Submarine launch-depth gating".
    /// </summary>
    internal static class SubmarineFacts
    {
        /// <summary>Depths are reported positive-down, so the sign flips out of Unity's y axis.</summary>
        private static float DepthFeet(float unityY) => -unityY * GameUnits.UnityToFeet;

        internal struct Snapshot
        {
            public bool Valid;              // false for surface ships and anything unreadable
            public float DepthFt;           // current, positive = below the surface
            public float DesiredDepthFt;    // ordered depth; the ascent the launcher asked for shows here
            public bool Submerged;
            public bool BelowPeriscope;
            public float PeriscopeDepthFt;
            public bool Diving;
            public float MaxLaunchDepthFt;  // deepest this weapon can launch from; 0 => surface only
            public bool RequiresFullSurface;
            public bool LaunchBlocked;      // the WeaponSystemLauncher gate above, evaluated here
            public string EngageState;      // distinct states across the launchers serving this ammo
            // Live vertical state, for the ascent model and the asc-track diagnostic.
            public float PitchDeg;          // hull pitch, positive nose-down
            public float SpeedKn;
            // Ordered speed, NaN when none is set. SubmarineAscent seeds from the instantaneous speed and
            // holds it, which is sound only if the two agree.
            public float CmdSpeedKn;
            public float BallastRateFtPerS; // _currentDepthChangeUsingTanks, positive down
            public float TargetDepthFt;     // where the launcher is taking it (0 when not commanded)
        }

        /// <summary>
        /// Depth the launcher takes the boat to for this weapon, its own command from
        /// WeaponSystemLauncher.cs:414-416: <c>-_maxDepthUnity + 0.115</c>, the weapon ceiling less 25.4 ft
        /// (75 ft for a 100 ft ceiling), or the surface for MaxDepth 0.
        /// </summary>
        private static float LaunchTargetDepthFt(float maxLaunchDepthFt)
            => Mathf.Max(0f, maxLaunchDepthFt - 0.115f * GameUnits.UnityToFeet);

        /// <summary>
        /// Seconds a submarine needs from the fire order to its first round: the ascent to the weapon's
        /// launch depth. Zero for a surface ship or a boat already shallow enough, where timing is exact.
        ///
        /// The ascent is stepped through the game's two-regime vertical model
        /// (<see cref="SubmarineAscent"/>): ballast and dive planes are mutually exclusive and differ
        /// several-fold, so no single rate fits. Returns 0 when the model says the boat never arrives; that
        /// is not "ready now", and the no-launch hold in <see cref="Coordinator"/> ends such an order.
        ///
        /// <para><paramref name="trace"/> collects a sampled ascent profile for the asc-sim diagnostic. Pass
        /// null on any per-frame path.</para>
        /// </summary>
        internal static float EstimateLaunchDelay(ObjectBase unit, string ammoId, System.Text.StringBuilder trace)
        {
            if (!TrySnapshot(unit, ammoId, out Snapshot s)) return 0f;
            if (float.IsNaN(s.MaxLaunchDepthFt)) return 0f;
            if (!(unit is Submarine sub) || sub.SP == null) return 0f;

            float targetFt = LaunchTargetDepthFt(s.MaxLaunchDepthFt);

            // The game's own gate decides this, not a depth comparison. WeaponSystemLauncher.cs:419 blocks on
            // `y < -MaxDepth && (MaxDepth > 0 || isSubmerged())`, so for MaxDepth 0 only isSubmerged() gates:
            // a boat with its deck awash is not submerged and owes no ascent. A depth comparison would also
            // charge a boat inside a nonzero ceiling but above the commanded depth. LaunchBlocked is that
            // gate, evaluated in the snapshot.
            if (!s.LaunchBlocked) return 0f;    // ready where it floats: exact, no estimate
            if (s.DepthFt - targetFt <= 0f) return 0f;

            SubmarineAscent.State st = new SubmarineAscent.State
            {
                DepthU = s.DepthFt / GameUnits.UnityToFeet,
                TargetDepthU = targetFt / GameUnits.UnityToFeet,
                // No GateDepthU: the launcher stops blocking at the ceiling, but in flight the first round leaves
                // when the boat reaches the commanded depth, 25.4 ft shallower, so the delay runs to there.
                PitchDeg = s.PitchDeg,
                BallastRateU = sub._currentDepthChangeUsingTanks,
                SpeedKn = s.SpeedKn,
                MaxSpeedKn = sub.SP._maxForwardVelocitySubmergedInKnots,
                MaxPitchDeg = sub.SP._maxPitchAngle,
                PitchRateDeg = sub.SP._pitchChangeRate,
                BallastAccelU = sub.SP._ballastTankChangeRate,
                MaxBallastU = sub.SP._maxDepthChangeUsingTanks,
            };

            // Seed description in the shape LaunchEnvelope writes for an aircraft, so envelope-sim reads
            // alike for both.
            trace?.Append($" from {s.DepthFt:0}ft to {targetFt:0}ft, spd {s.SpeedKn:0.0}kn " +
                          $"(cmd {(float.IsNaN(s.CmdSpeedKn) ? 0f : s.CmdSpeedKn):0.0}kn), pitch {s.PitchDeg:0.0} |");
            if (!SubmarineAscent.TryEstimate(st, out float ascent, trace)) return 0f;
            // Ascent only: the hatch cycle is part of Facts.LauncherCycle, which every platform pays through
            // StartupDelay, and Coordinator adds EnvelopeLead on top; adding it here would double-count.
            return ascent;
        }

        /// <summary>Cheap test used to gate the sim-cadence depth trace; no field reads.</summary>
        internal static bool IsSubmarine(ObjectBase unit) => unit is Submarine;

        /// <summary>
        /// Snapshot for <paramref name="unit"/> firing <paramref name="ammoId"/>; false when the unit is not
        /// a submarine.
        /// </summary>
        internal static bool TrySnapshot(ObjectBase unit, string ammoId, out Snapshot s)
        {
            s = default;
            if (!(unit is Submarine sub) || sub.IsDestroyed) return false;

            s.Valid = true;
            s.DepthFt = DepthFeet(sub.transform.position.y);
            s.DesiredDepthFt = DepthFeet(sub.DesiredAltitude.Value);
            s.Submerged = sub.isSubmerged();
            s.BelowPeriscope = sub.IsBelowPeriscopeDepth.Value;
            s.Diving = sub._divingInProgress;
            s.PeriscopeDepthFt = (sub.SP != null) ? sub.SP._periscopeDepth * GameUnits.UnityToFeet : -1f;

            // The weapon's launch-depth ceiling, and the game's own gate re-evaluated against it.
            AmmunitionParameters ap = (ammoId != null) ? unit.getAmmunitionByName(ammoId)?._ap : null;
            if (ap != null)
            {
                s.MaxLaunchDepthFt = ap._maxDepthUnity * GameUnits.UnityToFeet;
                s.RequiresFullSurface = ap._maxDepthUnity <= 0f;
                s.LaunchBlocked = sub.transform.position.y < -ap._maxDepthUnity
                                  && (ap._maxDepthUnity > 0f || s.Submerged);
            }
            else
            {
                s.MaxLaunchDepthFt = float.NaN;
            }

            s.PitchDeg = sub.getPitch();
            s.SpeedKn = sub._velocityInKnots;
            ISpeedCommand cmd = sub.SpeedCommand?.Value;
            s.CmdSpeedKn = (cmd != null) ? cmd.CommandSpeedInKnots : float.NaN;
            s.BallastRateFtPerS = sub._currentDepthChangeUsingTanks * GameUnits.UnityToFeet;
            s.TargetDepthFt = DepthFeet(sub.DesiredAltitude.Value);

            s.EngageState = EngageStates(unit, ammoId);
            return true;
        }

        /// <summary>
        /// Distinct <c>_engageState</c> values across the launchers serving this ammo, joined: port and
        /// starboard launchers can differ, and which one is stuck is what the shortfall lines need. Logged by
        /// name, so a renumbered enum still reads correctly.
        /// </summary>
        private static string EngageStates(ObjectBase unit, string ammoId)
        {
            if (ammoId == null) return "n/a";
            var launchers = unit.GetWeaponSystemsForAmmunition(ammoId);
            if (launchers == null || launchers.Count == 0) return "no-launcher";

            List<string> seen = null;
            for (int i = 0; i < launchers.Count; i++)
            {
                WeaponSystem ws = launchers[i];
                if (ws == null) continue;
                string name = ws._engageState.ToString();
                if (seen == null) seen = new List<string>(2);
                if (!seen.Contains(name)) seen.Add(name);
            }
            return (seen == null || seen.Count == 0) ? "n/a" : string.Join("+", seen.ToArray());
        }

        /// <summary>
        /// One-line suffix for a log line, or the empty string when the shooter is not a submarine; callers
        /// append it unconditionally.
        /// </summary>
        internal static string Describe(ObjectBase unit, string ammoId)
        {
            if (!TrySnapshot(unit, ammoId, out Snapshot s)) return "";
            return $" | sub depth {s.DepthFt:0}ft (desired {s.DesiredDepthFt:0}ft, periscope {s.PeriscopeDepthFt:0}ft), " +
                   $"spd {s.SpeedKn:0.0}kn (cmd {(float.IsNaN(s.CmdSpeedKn) ? 0f : s.CmdSpeedKn):0.0}kn), " +
                   $"submerged {s.Submerged}, belowPeriscope {s.BelowPeriscope}, diving {s.Diving}, " +
                   $"launchCeiling {s.MaxLaunchDepthFt:0}ft, needsSurface {s.RequiresFullSurface}, " +
                   $"blocked {s.LaunchBlocked}, engage {s.EngageState}";
        }
    }
}
