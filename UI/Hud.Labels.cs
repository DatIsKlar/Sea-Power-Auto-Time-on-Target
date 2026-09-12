using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) ; labels: target names under fog of war, hotkey hints and time formatting.
    /// </summary>
    internal sealed partial class Hud
    {
        // Human-readable form of a configured combo (e.g. "Alt+G", or just "G" with no modifier).
        private static string Combo(KeyCode key)
        {
            if (Bootstrap.ToggleModifier == KeyCode.None) return key.ToString();
            string mod = Bootstrap.ToggleModifier.ToString()
                .Replace("Left", "").Replace("Right", "");   // "LeftAlt" -> "Alt"
            return mod + "+" + key;
        }

        private static string HideHint() => Combo(Bootstrap.PanelKey);

        private static string ToggleHint() => Combo(Bootstrap.ToggleKey);

        private string TargetLabel() => FoggedLabel(_target, _targetVehicle);

        // Fog-of-war-correct label for any object: friendly objects show their name; enemies show
        // their real class ONLY once classified, otherwise just the track number plus the game's own
        // echo/emission descriptor. The contact wrapper comes from the player's plotting table (the
        // same source the game uses), so nothing is exposed that the player hasn't identified.
        private static string FoggedLabel(ObjectBase o, Vehicle known = null)
        {
            if (o == null) return "-";
            if (o.IsPlayerObject)
            {
                try { return o.Name.Value; } catch { return UnitNaming.SafeName(o); }
            }

            Vehicle v = known;
            if (v == null)
            {
                try { v = Globals._playerTaskforce?.PlottingTable?.VehicleForObject(o); } catch { v = null; }
            }
            if (v == null) return "Unknown contact";   // not on our plot -> reveal nothing

            // Keep the track number on a classified contact too. Two contacts of the same class
            // share a name, and the number is the only thing that tells the player which of them
            // the panel is aimed at.
            if (v.Class.HasValue)
            {
                try { return $"[{v.Id}] {v.Object.Name.Value}"; } catch { return $"Contact {v.Id}"; }
            }
            string s = $"Contact {v.Id}";
            try { if (v.HasSignalInfo()) s += "; " + v.IncomingSignalInfo(); } catch { }
            return s;
        }

        // Human-readable form of the configured strike-arm combo, for the panel hint.
        private static string StrikeHint() => Combo(Bootstrap.StrikeArmKey);

        private static string FireHint() => Combo(Bootstrap.FireStrikeKey);

        private static string FormatTime(float sec)
        {
            if (sec <= 0f) return "0:00";
            int s = Mathf.RoundToInt(sec);
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
