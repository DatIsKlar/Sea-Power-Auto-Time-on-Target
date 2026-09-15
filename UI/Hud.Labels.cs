using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>Hud (partial): labels, meaning target names under fog of war, hotkey hints and time formatting.</summary>
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

        // Fog-of-war label: friendly objects show their name; enemies show their class only once classified,
        // otherwise the track number and the game's echo or emission descriptor. The contact comes from the
        // player's plotting table, as in the game, so nothing unidentified is exposed.
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

            // The track number stays on a classified contact: two contacts of one class share a name.
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
