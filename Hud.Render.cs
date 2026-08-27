using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) — panel content rendering: header, missile rows, ENGAGEMENTS overview,
    /// fire actions, and small draw helpers. Data comes from the core partial; styling from
    /// the styles partial.
    /// </summary>
    internal sealed partial class Hud
    {
        private const float SelectionLabelW = 80f;   // TARGET / SHOOTERS label column

        // TARGET / SHOOTERS rows at the top of the expanded panel.
        private void DrawSelectionHeader(bool haveTarget)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("TARGET", _hdr, GUILayout.Width(SelectionLabelW));
            GUI.color = haveTarget ? TargetCol : TargetMissing;
            GUILayout.Label(haveTarget ? TargetLabel() : "click an enemy contact to set target", _row);
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("SHOOTERS", _hdr, GUILayout.Width(SelectionLabelW));
            GUILayout.Label(_anchor != null ? Name(_anchor) : "click one of your ships", _row);
            GUILayout.FlexibleSpace();
            _wholeFormation = DrawCheckbox(_wholeFormation, "whole formation", _hdr);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // The title bar — always visible, draggable, carries the collapse toggle and auto status.
        private void DrawHeader()
        {
            GUILayout.BeginHorizontal(GUILayout.Height(HeaderH));
            if (GUILayout.Button(_open ? "▾" : "▸", _chev, GUILayout.Width(26), GUILayout.Height(HeaderH)))
                _open = !_open;
            GUILayout.Label("TIME-ON-TARGET", _title, GUILayout.Height(HeaderH));
            GUILayout.FlexibleSpace();
            // Live engagement count — visible even while minimized.
            int rounds = 0, tgts = _salvos.Count;
            foreach (var e in _salvos) rounds += e.Queued + e.InFlight;
            if (tgts > 0)
            {
                GUI.color = TargetCol;
                GUILayout.Label($"● {tgts} tgt / {rounds} msl", _hdr, GUILayout.Height(HeaderH));
                GUILayout.Space(8);
            }
            GUI.color = Coordinator.Active ? Accent : TextDim;
            GUILayout.Label(Coordinator.Active ? "● AUTO" : "○ AUTO", _hdr, GUILayout.Height(HeaderH));
            GUI.color = Color.white;
            GUILayout.Space(4);
            GUILayout.EndHorizontal();
        }

        private void DrawMissileRow(ObjectBase ship, Row r)
        {
            string key = Key(ship, r.AmmoId);
            if (!_checked.ContainsKey(key)) _checked[key] = true;
            if (!_salvo.ContainsKey(key)) _salvo[key] = 1;

            bool haveTarget = _target != null && !_target.IsDestroyed;
            GUILayout.BeginHorizontal();

            string eta = "--", range = "";
            if (haveTarget)
            {
                eta = FormatTime(FlightTime.Estimate(ship, r.AmmoId, _target));
                float nm = GameUnits.NmBetween(ship, _target);
                range = $"{nm:0.0}nm";
            }

            GUI.enabled = r.InRange;                       // out-of-range rows can't be picked
            
            // Clickable missile name with checkmark prefix (like Sea Power menu items)
            bool isChecked = _checked[key] && r.InRange;
            string missileLabel = (isChecked ? "✓ " : "  ") + $"{r.AmmoId}  x{r.Count}";
            
            // Set color based on range
            GUI.color = r.InRange ? TextMain : OutOfRange;
            if (GUILayout.Button(missileLabel, _menuItem, GUILayout.Height(RowHeight)))
                _checked[key] = !isChecked;
            GUI.color = Color.white;
            
            // ETA and range labels
            GUI.color = r.InRange ? Accent : OutOfRange;
            GUILayout.Label($"ETA {eta}", _row, GUILayout.Width(95));
            GUI.color = r.InRange ? TextMain : OutOfRange;
            GUILayout.Label(r.InRange ? range : range + " (out of range)", _row, GUILayout.Width(170));
            GUI.color = Color.white;
            
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("–", _btn, GUILayout.Width(30))) _salvo[key] = Mathf.Max(1, _salvo[key] - 1);
            GUILayout.Label($"{_salvo[key]}", _row, GUILayout.Width(28));
            if (GUILayout.Button("+", _btn, GUILayout.Width(30))) _salvo[key] = Mathf.Min(r.Count, _salvo[key] + 1);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Warn when the chosen salvo can't fire before the launcher must reload.
            // The strike still fires as one order (the game paces the reload); it just arrives in
            // waves, which the ENGAGEMENTS overview then shows split out.
            if (r.InRange && _checked[key] &&
                LauncherFactsSource.WillNeedReload(ship, r.AmmoId, _salvo[key], out int ready, out int waves))
            {
                GUI.color = Warn;
                GUILayout.Label($"     ⚠ needs reload — {ready} ready, fires in {waves} waves", _row);
                GUI.color = Color.white;
            }
        }

        // Live overview of every salvo we're coordinating: target + rounds queued / in flight,
        // and the synced impact countdown for shots still being held.
        private void DrawEngagements()
        {
            DrawDivider();
            GUILayout.Label("ENGAGEMENTS", _hdr);
            if (_salvos.Count == 0)
            {
                GUI.color = TextDim;
                GUILayout.Label("   none in progress", _row);
                GUI.color = Color.white;
                return;
            }

            float now = GameTime.time;
            foreach (EngagementBoard.SalvoLine e in _salvos)
            {
                GUILayout.BeginHorizontal();
                GUI.color = TargetCol;
                GUILayout.Label(FoggedLabel(e.Target), _row, GUILayout.Width(210));

                GUI.color = TextMain;
                string status = "";
                if (e.Queued > 0) status += $"{e.Queued} queued";
                if (e.InFlight > 0) status += (status.Length > 0 ? "  ·  " : "") + $"{e.InFlight} in flight";
                if (e.AnchorTotal > 0) status += (status.Length > 0 ? "  ·  " : "") + $"anchoring {e.AnchorLaunched}/{e.AnchorTotal}";
                GUILayout.Label(status, _row, GUILayout.Width(170));

                GUILayout.FlexibleSpace();
                if (e.ImpactSim > 0f)
                {
                    GUI.color = Accent;
                    if (e.Waves > 1 && e.WaveGap > 0f)
                    {
                        // Reload-separated waves: show each wave's arrival (wave k = base + k*gap).
                        string arrival = $"wave 1 {FormatTime(Mathf.Max(0f, e.ImpactSim - now))}";
                        arrival += $"  ·  wave {e.Waves} {FormatTime(Mathf.Max(0f, e.ImpactSim + (e.Waves - 1) * e.WaveGap - now))}";
                        GUILayout.Label(arrival, _row);
                    }
                    else
                    {
                        string arrival = $"arrival {FormatTime(Mathf.Max(0f, e.ImpactSim - now))}";
                        if (e.ImpactSpread > MinSpreadToDisplay)
                            arrival += $" ±{e.ImpactSpread:0.0}s";
                        GUILayout.Label(arrival, _row);
                    }
                }
                else if (e.InFlight > 0)
                {
                    GUI.color = TextDim;
                    GUILayout.Label("inbound", _row);
                }
                GUI.color = Color.white;
                GUILayout.EndHorizontal();
            }
        }

        // Visual only — the actual resize is driven from Update/HandleResizeInput.
        private void DrawResizeGrip()
        {
            GUI.color = _resizing ? TextMain : TextDim;
            GUI.Label(new Rect(_win.width - 20, _win.height - 20, 18, 18), "◢");
            GUI.color = Color.white;
        }

        private void FireSelected(List<ObjectBase> shooters, bool coordinated)
        {
            if (_target == null) return;
            var shots = new List<Coordinator.Shot>();
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                {
                    if (!r.InRange) continue;
                    string key = Key(ship, r.AmmoId);
                    if (_checked.TryGetValue(key, out bool on) && on)
                    {
                        int salvo = _salvo.TryGetValue(key, out int sv) ? sv : 1;
                        shots.Add(new Coordinator.Shot { Unit = ship, AmmoId = r.AmmoId, Salvo = salvo });
                    }
                }
            if (shots.Count == 0) return;

            if (coordinated) Coordinator.FireCoordinated(shots, _target);
            else foreach (var s in shots) Coordinator.FireNow(s.Unit, s.AmmoId, _target, s.Salvo);
        }

        // Draw a menu-item style checkbox row (checkmark prefix + hover highlight, like Sea Power context menu)
        private bool DrawCheckbox(bool value, string label, GUIStyle labelStyle)
        {
            string displayText = value ? ("✓ " + label) : ("  " + label);
            if (GUILayout.Button(displayText, _menuItem, GUILayout.Height(RowHeight)))
                value = !value;
            return value;
        }

        private void DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(1, 3);
            GUI.color = new Color(Border.r, Border.g, Border.b, 0.5f);
            GUI.DrawTexture(new Rect(r.x, r.y + 1, r.width, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
