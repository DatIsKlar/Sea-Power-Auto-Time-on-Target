using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// On-screen planner. Remembers your last-selected friendly ship (the "anchor",
    /// with a This-ship / Whole-formation toggle) and your last-selected enemy as the
    /// target. Lists each shooter's missiles with live flight-time readouts and lets
    /// you fire a hand-picked set as a coordinated Time-on-Target strike.
    ///
    /// While the cursor is over the panel it sets the game's MouseControlState to "UI"
    /// so clicks/drags don't leak into the camera or world selection.
    /// </summary>
    internal sealed class Hud : MonoBehaviour
    {
        private bool _open = false;   // start minimized; expand via the ▸ chevron or Alt+G
        private ObjectBase _anchor;      // last selected friendly unit
        private ObjectBase _target;      // last selected enemy unit (real object, for firing)
        private Vehicle _targetVehicle;  // the enemy contact, for fog-of-war-correct display
        private bool _wholeFormation;

        private readonly Dictionary<string, bool> _checked = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _salvo = new Dictionary<string, int>();
        private readonly List<Coordinator.SalvoLine> _salvos = new List<Coordinator.SalvoLine>();

        // Per-frame cache for EngageRows — avoids recomputing expensive range/guidance
        // checks 3+ times per ship per OnGUI pass (IsMissileShip, draw loop, AnyChecked, FireSelected).
        private readonly Dictionary<int, List<Row>> _rowCache = new Dictionary<int, List<Row>>();
        private int _rowCacheFrame = -1;
        private int _pruneCounter;

        // Reusable shooter list — avoids allocating a new List<ObjectBase> every OnGUI call.
        private readonly List<ObjectBase> _shootersCache = new List<ObjectBase>();

        private Vector2 _scroll;
        private Rect _win = new Rect(0, 0, 540, 520);
        private float _expandedW = 540f, _expandedH = 520f;
        private bool _placed;
        private bool _resizing;
        private bool _lastOverUi;
        private bool _mouseDownOverUi;

        private const float CollapsedH = 34f;
        private const float HeaderH = 30f;

        private const float MetersPerUnity = 67.200066f;   // game's own unity->metre scale
        private const float UnityToNm = MetersPerUnity / 1852f;

        // ---- Palette sampled directly from the game's own panels (Screenshot 2026-08-23) ----
        // Panel body RGB(43,45,49); title bar RGB(30,31,34) (darker); chip outline RGB(98,102,108).
        private static readonly Color Panel     = new Color(0.169f, 0.176f, 0.192f, 0.97f);  // (43,45,49)
        private static readonly Color HeaderBg  = new Color(0.118f, 0.122f, 0.133f, 1f);     // (30,31,34)
        private static readonly Color Border    = new Color(0.235f, 0.247f, 0.267f, 1f);     // faint frame (~60)
        private static readonly Color BtnBg     = new Color(0.216f, 0.227f, 0.247f, 1f);     // (55,58,63)
        private static readonly Color BtnHover  = new Color(0.298f, 0.314f, 0.337f, 1f);     // (76,80,86)
        private static readonly Color BtnBorder = new Color(0.384f, 0.400f, 0.424f, 1f);     // (98,102,108)
        private static readonly Color TextMain  = new Color(0.78f, 0.80f, 0.82f, 1f);        // light gray
        // Functional colors taken from the game's OWN rich-text status hex tags (Seapower-Scripts).
        private static readonly Color TextDim   = new Color(0.592f, 0.596f, 0.600f, 1f);     // #979899 unavailable/dim
        private static readonly Color Accent    = new Color(0.427f, 0.714f, 0.929f, 1f);     // #6db6ed info/friendly
        private static readonly Color OutOfRange = new Color(1.000f, 0.749f, 0.000f, 1f);    // #ffbf00 warning amber
        private static readonly Color TargetCol  = new Color(0.808f, 0.067f, 0.141f, 1f);    // #ce1124 hostile red
        private static readonly Color FireGreen  = new Color(0.110f, 0.612f, 0.243f, 1f);    // #1c9c3e available/go

        private GUIStyle _winStyle, _title, _chev, _hdr, _row, _ship, _btn, _fire, _fireNow, _toggle, _checkboxBtn, _menuItem;
        private GUIStyle _scrollThumb, _scrollTrack, _hScrollThumb, _hScrollTrack;
        private Texture2D _panelTex, _headerTex, _fireTex, _btnTex, _btnHoverTex;
        private Texture2D _scrollThumbTex, _scrollTrackTex, _checkboxOffTex, _checkboxOnTex, _menuHoverTex, _transparentTex;

        // Only alive inside a running mission. In the main menu Globals._mainGameViewModel is null,
        // so the planner neither draws nor eats mouse input there.
        private static bool InMission() => Globals._mainGameViewModel != null;

        private void Update()
        {
            if (!InMission())
            {
                _resizing = false;
                SetOverUi(false);
                return;
            }

            TrackSelection();

            bool modOk = Bootstrap.ToggleModifier == KeyCode.None || Input.GetKey(Bootstrap.ToggleModifier);
            if (modOk && Input.GetKeyDown(Bootstrap.PanelKey)) _open = !_open;
            if (modOk && Input.GetKeyDown(Bootstrap.ToggleKey))
            {
                Coordinator.Active = !Coordinator.Active;
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(Coordinator.Active ? "ON" : "OFF")}");
            }

            HandleResizeInput();
            UpdateMouseCapture();
        }

        private void OnDisable() => SetOverUi(false);

        private void OnDestroy()
        {
            if (_panelTex != null) Object.Destroy(_panelTex);
            if (_headerTex != null) Object.Destroy(_headerTex);
            if (_fireTex != null) Object.Destroy(_fireTex);
            if (_btnTex != null) Object.Destroy(_btnTex);
            if (_btnHoverTex != null) Object.Destroy(_btnHoverTex);
        }

        // Frame-based resize: driven by raw Input every frame (not IMGUI drag events, which stop
        // being delivered to the window the moment a fast cursor outruns its rect). Grab the grip
        // on mouse-down, then track the global cursor until release — smooth at any speed.
        private void HandleResizeInput()
        {
            if (!_open) { _resizing = false; return; }

            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Rect grip = new Rect(_win.x + _win.width - 24f, _win.y + _win.height - 24f, 24f, 24f);

            if (Input.GetMouseButtonDown(0) && grip.Contains(m)) _resizing = true;
            if (!Input.GetMouseButton(0)) { _resizing = false; return; }

            if (_resizing)
            {
                _expandedW = Mathf.Max(420f, m.x - _win.x + 8f);
                _expandedH = Mathf.Max(320f, m.y - _win.y + 8f);
                _win.width = _expandedW;
                _win.height = _expandedH;
            }
        }

        private void TrackSelection()
        {
            // MainGameViewModel.SelectedObject is an ISelectableObject that is either an
            // ObjectBase (your ships) or a Vehicle contact (enemy) whose .Object is the ObjectBase.
            ISelectableObject sel = Globals._mainGameViewModel?.SelectedObject?.Value;

            if (sel is Vehicle v && v.Object != null)   // enemy contact
            {
                ObjectBase o = v.Object;
                if (o.IsDestroyed || !o.isUnit()) return;
                if (o.IsPlayerObject) { _anchor = o; }
                else { _target = o; _targetVehicle = v; }
                return;
            }

            if (sel is ObjectBase ob && !ob.IsDestroyed && ob.isUnit())
            {
                if (ob.IsPlayerObject) _anchor = ob;
                else { _target = ob; _targetVehicle = null; }
            }
        }

        // Tell the game the mouse is over UI while the cursor is on our panel.
        // Latch through a held drag so fast window moves never briefly unblock the camera.
        private void UpdateMouseCapture()
        {
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overNow = _win.Contains(m);

            if (Input.GetMouseButtonDown(0)) _mouseDownOverUi = overNow;
            bool held = Input.GetMouseButton(0);
            if (!held) _mouseDownOverUi = false;

            bool over = overNow || (held && _mouseDownOverUi) || _resizing;
            if (over != _lastOverUi) SetOverUi(over);
        }

        private void SetOverUi(bool over)
        {
            _lastOverUi = over;
            if (Singleton<MouseControlState>.InstanceExists())
                Singleton<MouseControlState>.Instance.setMouseIsOverUIWindow(over);
        }

        // ---------- GUI ----------

        private void OnGUI()
        {
            if (!Coordinator.Enabled || !Bootstrap.ShowIndicator || !InMission()) return;
            EnsureStyles();

            if (!_placed)   // first paint: drop it near the top-center
            {
                _win.x = Mathf.Max(8f, (Screen.width - _expandedW) * 0.5f);
                _win.y = 40f;
                _placed = true;
            }

            _win.width = _expandedW;
            _win.height = _open ? _expandedH : CollapsedH;
            _win = GUI.Window(0xA070F0, _win, DrawWindow, GUIContent.none, _winStyle);

            // Keep the window on-screen.
            _win.x = Mathf.Clamp(_win.x, -_win.width + 60f, Screen.width - 60f);
            _win.y = Mathf.Clamp(_win.y, 0f, Screen.height - CollapsedH);
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

        private void DrawWindow(int id)
        {
            Coordinator.CollectSalvos(_salvos);   // live engagement snapshot (used by header + list)

            // Reset the per-frame EngageRows cache.
            int frame = Time.frameCount;
            if (_rowCacheFrame != frame)
            {
                _rowCache.Clear();
                _rowCacheFrame = frame;
            }


            // Lighter title strip across the top, like the game's own panel headers.
            GUI.DrawTexture(new Rect(1, 1, _win.width - 2, HeaderH + 3), _headerTex);
            DrawHeader();
            if (!_open) { GUI.DragWindow(new Rect(0, 0, 100000, CollapsedH)); return; }

            DrawDivider();
            GUILayout.Space(4);

            bool haveTarget = _target != null && !_target.IsDestroyed;
            GUILayout.BeginHorizontal();
            GUILayout.Label("TARGET", _hdr, GUILayout.Width(80));
            GUI.color = haveTarget ? TargetCol : new Color(0.85f, 0.45f, 0.45f);
            GUILayout.Label(haveTarget ? TargetLabel() : "click an enemy contact to set target", _row);
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("SHOOTERS", _hdr, GUILayout.Width(80));
            GUILayout.Label(_anchor != null ? Name(_anchor) : "click one of your ships", _row);
            GUILayout.FlexibleSpace();
            _wholeFormation = DrawCheckbox(_wholeFormation, "whole formation", _hdr);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            DrawDivider();

            List<ObjectBase> shooters = GetShooters();

            // Periodically prune destroyed ships from _checked/_salvo to prevent unbounded growth.
            if (++_pruneCounter >= 300)
            {
                _pruneCounter = 0;
                PruneCheckSalvo();
            }
            // Apply our scrollbar styling only around our own scroll view, then restore — so we
            // never restyle other mods' IMGUI (BepInEx console etc.) via the shared GUI.skin.
            GUIStyle prevVBar = GUI.skin.verticalScrollbar, prevVThumb = GUI.skin.verticalScrollbarThumb;
            GUIStyle prevHBar = GUI.skin.horizontalScrollbar, prevHThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.verticalScrollbar = _scrollTrack;
            GUI.skin.verticalScrollbarThumb = _scrollThumb;
            GUI.skin.horizontalScrollbar = _hScrollTrack;
            GUI.skin.horizontalScrollbarThumb = _hScrollThumb;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (shooters.Count == 0)
                GUILayout.Label("No missile-armed ships selected. Click one of your ships.", _row);

            foreach (ObjectBase ship in shooters)
            {
                GUILayout.Space(3);
                GUILayout.Label(Name(ship), _ship);
                bool any = false;
                foreach (Row r in CachedEngageRows(ship))
                {
                    any = true;
                    DrawMissileRow(ship, r);
                }
                if (!any)
                    GUILayout.Label(haveTarget ? "   (no missiles that can engage this target)"
                                               : "   (no missiles aboard)", _row);
            }
            GUILayout.EndScrollView();

            GUI.skin.verticalScrollbar = prevVBar;
            GUI.skin.verticalScrollbarThumb = prevVThumb;
            GUI.skin.horizontalScrollbar = prevHBar;
            GUI.skin.horizontalScrollbarThumb = prevHThumb;

            DrawEngagements();
            DrawDivider();

            bool auto = DrawCheckbox(Coordinator.Active, "Also auto-coordinate normal group orders (Alt+T)", _row);
            if (auto != Coordinator.Active)
            {
                Coordinator.Active = auto;
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(auto ? "ON" : "OFF")}");
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUI.enabled = haveTarget && AnyChecked(shooters);
            if (GUILayout.Button("FIRE — TIME ON TARGET", _fire, GUILayout.Height(38)))
                FireSelected(shooters, coordinated: true);
            if (GUILayout.Button("FIRE NOW\n(no sync)", _fireNow, GUILayout.Height(38), GUILayout.Width(100)))
                FireSelected(shooters, coordinated: false);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            DrawResizeGrip();
            GUI.DragWindow(new Rect(0, 0, 100000, HeaderH));
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
                eta = FormatTime(Coordinator.EstimateEnroute(ship, r.AmmoId, _target));
                float nm = (_target.transform.position - ship.transform.position).magnitude * UnityToNm;
                range = $"{nm:0.0}nm";
            }

            GUI.enabled = r.InRange;                       // out-of-range rows can't be picked
            
            // Clickable missile name with checkmark prefix (like Sea Power menu items)
            bool isChecked = _checked[key] && r.InRange;
            string missileLabel = (isChecked ? "✓ " : "  ") + $"{r.AmmoId}  x{r.Count}";
            
            // Set color based on range
            GUI.color = r.InRange ? TextMain : OutOfRange;
            if (GUILayout.Button(missileLabel, _menuItem, GUILayout.Height(26)))
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
            foreach (Coordinator.SalvoLine e in _salvos)
            {
                GUILayout.BeginHorizontal();
                GUI.color = TargetCol;
                GUILayout.Label(FoggedLabel(e.Target), _row, GUILayout.Width(210));

                GUI.color = TextMain;
                string status = "";
                if (e.Queued > 0) status += $"{e.Queued} queued";
                if (e.InFlight > 0) status += (status.Length > 0 ? "  ·  " : "") + $"{e.InFlight} in flight";
                GUILayout.Label(status, _row, GUILayout.Width(170));

                GUILayout.FlexibleSpace();
                if (e.ImpactSim > 0f)
                {
                    GUI.color = Accent;
                    string arrival = $"arrival {FormatTime(Mathf.Max(0f, e.ImpactSim - now))}";
                    if (e.ImpactSpread > 0.1f)
                        arrival += $" ±{e.ImpactSpread:0.0}s";
                    GUILayout.Label(arrival, _row);
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

        // ---------- helpers ----------

        private List<ObjectBase> GetShooters()
        {
            _shootersCache.Clear();
            if (_anchor == null || _anchor.IsDestroyed) return _shootersCache;

            if (_wholeFormation && _anchor.Formation != null)
            {
                foreach (Station st in _anchor.Formation.Stations)
                {
                    ObjectBase u = st?.UnitObject;
                    if (IsMissileShip(u) && !_shootersCache.Contains(u)) _shootersCache.Add(u);
                }
                if (IsMissileShip(_anchor) && !_shootersCache.Contains(_anchor)) _shootersCache.Add(_anchor);
            }
            else if (IsMissileShip(_anchor))
            {
                _shootersCache.Add(_anchor);
            }
            return _shootersCache;
        }

        private struct Row { public string AmmoId; public int Count; public bool InRange; }

        private bool IsMissileShip(ObjectBase u)
        {
            if (u == null || u.IsDestroyed || !u.isUnit() || !u.IsPlayerObject) return false;
            foreach (Row r in CachedEngageRows(u))
                return true;
            return false;
        }

        // Cached wrapper around EngageRows — materialises the IEnumerable to a list once per
        // ship per frame, then returns the same list for all callers within that frame.
        private List<Row> CachedEngageRows(ObjectBase ship)
        {
            int id = ship.GetInstanceID();
            if (_rowCache.TryGetValue(id, out List<Row> cached)) return cached;
            var list = new List<Row>();
            foreach (Row r in EngageRows(ship)) list.Add(r);
            _rowCache[id] = list;
            return list;
        }

        // Missiles this ship carries that can engage the current target (by type). Each row
        // also reports whether the target is within that missile's max range. With no target,
        // all missiles are listed and treated as in range.
        private IEnumerable<Row> EngageRows(ObjectBase ship)
        {
            if (ship == null) yield break;
            bool haveTarget = _target != null && !_target.IsDestroyed;
            float dist = haveTarget
                ? (_target.transform.position - ship.transform.position).magnitude : 0f;

            foreach (KeyValuePair<string, int> kv in ship.AmmunitionAmountDictionary)
            {
                if (kv.Value <= 0) continue;
                Ammunition a = ship.getAmmunitionByName(kv.Key);
                if (a?._ap == null || a._ap._type != Ammunition.Type.Missile) continue;

                bool inRange = true;
                if (haveTarget)
                {
                    // Wrong target type or weapon incompatibility -> hide entirely.
                    if (!ship.DoesAmmoMatchTarget(a._ap, _target, out _))
                        continue;
                    inRange = dist <= ship.GetMaxRangeForAmmo(a, _target);
                }
                yield return new Row { AmmoId = kv.Key, Count = kv.Value, InRange = inRange };
            }
        }

        // Remove entries from _checked/_salvo for ships that no longer exist or are destroyed,
        // preventing unbounded dictionary growth over long sessions with many ships.
        private void PruneCheckSalvo()
        {
            var deadKeys = new List<string>();
            foreach (KeyValuePair<string, bool> kv in _checked)
            {
                int pipeIdx = kv.Key.IndexOf('|');
                if (pipeIdx <= 0) continue;
                if (!int.TryParse(kv.Key.Substring(0, pipeIdx), out int instanceId)) continue;

                bool alive = false;
                foreach (ObjectBase ship in _shootersCache)
                {
                    if (ship.GetInstanceID() == instanceId) { alive = true; break; }
                }
                if (!alive) deadKeys.Add(kv.Key);
            }
            for (int i = 0; i < deadKeys.Count; i++)
            {
                _checked.Remove(deadKeys[i]);
                _salvo.Remove(deadKeys[i]);
            }
        }

        private bool AnyChecked(List<ObjectBase> shooters)
        {
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                    if (r.InRange && _checked.TryGetValue(Key(ship, r.AmmoId), out bool on) && on) return true;
            return false;
        }

        private static string Key(ObjectBase u, string ammoId) => u.GetInstanceID() + "|" + ammoId;

        private static string Name(ObjectBase u)
        {
            try { return u.getUIDAndName(); } catch { return u.name; }
        }

        private string TargetLabel() => FoggedLabel(_target, _targetVehicle);

        // Fog-of-war-correct label for any object: friendly objects show their name; enemies show
        // their real class ONLY once classified, otherwise just the track number plus the game's own
        // echo/emission descriptor. The contact wrapper comes from the player's plotting table (the
        // same source the game uses), so nothing is exposed that the player hasn't identified.
        private static string FoggedLabel(ObjectBase o, Vehicle known = null)
        {
            if (o == null) return "—";
            if (o.IsPlayerObject)
            {
                try { return o.Name.Value; } catch { return Name(o); }
            }

            Vehicle v = known;
            if (v == null)
            {
                try { v = Globals._playerTaskforce?.PlottingTable?.VehicleForObject(o); } catch { v = null; }
            }
            if (v == null) return "Unknown contact";   // not on our plot -> reveal nothing

            if (v.Class.HasValue)
            {
                try { return v.Object.Name.Value; } catch { return $"Contact {v.Id}"; }
            }
            string s = $"Contact {v.Id}";
            try { if (v.HasSignalInfo()) s += " — " + v.IncomingSignalInfo(); } catch { }
            return s;
        }

        private static string FormatTime(float sec)
        {
            if (sec <= 0f) return "0:00";
            int s = Mathf.RoundToInt(sec);
            return $"{s / 60}:{s % 60:00}";
        }


        // Draw a menu-item style checkbox row (checkmark prefix + hover highlight, like Sea Power context menu)
        private bool DrawCheckbox(bool value, string label, GUIStyle labelStyle)
        {
            string displayText = value ? ("✓ " + label) : ("  " + label);
            if (GUILayout.Button(displayText, _menuItem, GUILayout.Height(26)))
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

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // A fill with a 1px border on all sides; stretched with GUIStyle.border it keeps a crisp frame.
        private static Texture2D Framed(Color fill, Color border)
        {
            const int n = 8;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t.SetPixel(x, y, (x == 0 || y == 0 || x == n - 1 || y == n - 1) ? border : fill);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (_winStyle != null) return;

            _panelTex    = Framed(Panel, Border);
            _headerTex   = Solid(HeaderBg);
            _fireTex     = Solid(FireGreen);
            _btnTex      = Solid(BtnBg);
            _btnHoverTex = Solid(BtnHover);

            // Scrollbar textures — light gray thumb on dark track (Sea Power style)
            _scrollTrackTex = Solid(new Color(0.169f, 0.176f, 0.192f, 0.5f));
            _scrollThumbTex = Solid(new Color(0.706f, 0.706f, 0.706f, 1f));
            
            // Menu-item hover highlight (subtle lighter background)
            _menuHoverTex = Solid(new Color(0.25f, 0.26f, 0.28f, 1f));
            
            // Transparent texture for "no background" states
            _transparentTex = Solid(new Color(0, 0, 0, 0));

            // Checkbox textures — visible squares (lighter than panel for contrast)
            _checkboxOffTex = Framed(new Color(0.25f, 0.25f, 0.27f, 1f), new Color(0.45f, 0.45f, 0.48f, 1f));
            _checkboxOnTex  = Framed(new Color(0.35f, 0.35f, 0.38f, 1f), new Color(0.55f, 0.55f, 0.58f, 1f));

            _winStyle = new GUIStyle(GUI.skin.window);
            _winStyle.normal.background = _winStyle.onNormal.background = _panelTex;
            _winStyle.border = new RectOffset(1, 1, 1, 1);
            _winStyle.padding = new RectOffset(10, 10, 4, 10);
            _winStyle.margin = new RectOffset(0, 0, 0, 0);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Normal, fontSize = 15, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 4, 0, 0),
            };
            _title.normal.textColor = TextMain;

            _chev = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold, fontSize = 16, alignment = TextAnchor.MiddleCenter,
            };
            _chev.normal.textColor = TextDim;
            _chev.hover.textColor = TextMain;

            _hdr = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Normal, fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _hdr.normal.textColor = TextDim;

            _row = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _row.normal.textColor = TextMain;

            _ship = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            _ship.normal.textColor = TextMain;

            // Small square button for –/+ controls (centered, flat)
            _btn = new GUIStyle()
            {
                fontStyle = FontStyle.Bold, fontSize = 16, alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(0, 0, 0, 0),
            };
            _btn.normal.background = _transparentTex;
            _btn.hover.background = _btnHoverTex;
            _btn.active.background = _btnTex;
            _btn.focused.background = _transparentTex;
            _btn.onNormal.background = _transparentTex;
            _btn.onHover.background = _btnHoverTex;
            _btn.onActive.background = _btnTex;
            _btn.onFocused.background = _transparentTex;
            _btn.normal.textColor = _btn.hover.textColor = _btn.active.textColor = _btn.focused.textColor = TextMain;
            _btn.onNormal.textColor = _btn.onHover.textColor = _btn.onActive.textColor = _btn.onFocused.textColor = TextMain;

            // Menu-item row style (for checkboxes and other interactive rows)
            // Flat text with hover highlight, checkmark prefix when active
            _menuItem = new GUIStyle()
            {
                fontStyle = FontStyle.Normal, fontSize = 14, alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(8, 8, 2, 2),
            };
            _menuItem.normal.background = _transparentTex;
            _menuItem.hover.background = _menuHoverTex;
            _menuItem.active.background = _menuHoverTex;
            _menuItem.focused.background = _menuHoverTex;
            _menuItem.onNormal.background = _transparentTex;
            _menuItem.onHover.background = _menuHoverTex;
            _menuItem.onActive.background = _menuHoverTex;
            _menuItem.onFocused.background = _menuHoverTex;
            _menuItem.normal.textColor = _menuItem.focused.textColor = TextMain;
            _menuItem.hover.textColor = _menuItem.active.textColor = Color.white;
            _menuItem.onNormal.textColor = _menuItem.onFocused.textColor = TextMain;
            _menuItem.onHover.textColor = _menuItem.onActive.textColor = Color.white;
            
            // Small square checkbox button (for missile row selection)
            _checkboxBtn = new GUIStyle(_btn)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 2, 0, 0),
            };
            _checkboxBtn.normal.background = _checkboxOffTex;
            _checkboxBtn.hover.background = _checkboxOffTex;
            _checkboxBtn.active.background = _checkboxOnTex;
            _checkboxBtn.focused.background = _checkboxOffTex;
            _checkboxBtn.onNormal.background = _checkboxOnTex;
            _checkboxBtn.onHover.background = _checkboxOnTex;
            _checkboxBtn.onActive.background = _checkboxOnTex;
            _checkboxBtn.onFocused.background = _checkboxOnTex;
            _checkboxBtn.normal.textColor = _checkboxBtn.hover.textColor = _checkboxBtn.active.textColor = Color.white;
            _checkboxBtn.onNormal.textColor = _checkboxBtn.onHover.textColor = _checkboxBtn.onActive.textColor = Color.white;
            
            // Toggle style (kept for backwards compatibility)
            _toggle = new GUIStyle()
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
            };
            _toggle.normal.textColor = _toggle.hover.textColor = TextMain;
            _toggle.onNormal.textColor = _toggle.onHover.textColor = Accent;

            // Scrollbar styles — flat light gray thumb, dark track (Sea Power style)
            // Applied directly to GUI.skin since the game uses NoesisGUI (WPF) for its own UI,
            // so this only affects Unity IMGUI elements (our mod + debug windows).
            
            // Vertical scrollbar
            _scrollTrack = new GUIStyle(GUI.skin.verticalScrollbar);
            _scrollTrack.normal.background = _scrollTrackTex;
            _scrollTrack.hover.background = _scrollTrackTex;
            _scrollTrack.active.background = _scrollTrackTex;
            _scrollTrack.border = new RectOffset(0, 0, 0, 0);
            
            // Horizontal scrollbar (same style)
            _hScrollTrack = new GUIStyle(GUI.skin.horizontalScrollbar);
            _hScrollTrack.normal.background = _scrollTrackTex;
            _hScrollTrack.hover.background = _scrollTrackTex;
            _hScrollTrack.active.background = _scrollTrackTex;
            _hScrollTrack.border = new RectOffset(0, 0, 0, 0);

            // Vertical thumb — flat rectangular block
            _scrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            _scrollThumb.normal.background = _scrollThumbTex;
            _scrollThumb.hover.background = _scrollThumbTex;
            _scrollThumb.active.background = _scrollThumbTex;
            _scrollThumb.border = new RectOffset(0, 0, 0, 0);
            
            // Horizontal thumb — flat rectangular block
            _hScrollThumb = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            _hScrollThumb.normal.background = _scrollThumbTex;
            _hScrollThumb.hover.background = _scrollThumbTex;
            _hScrollThumb.active.background = _scrollThumbTex;
            _hScrollThumb.border = new RectOffset(0, 0, 0, 0);

            // NOTE: these styles are applied only around our own scroll view (see DrawWindow),
            // then restored — we deliberately do NOT overwrite GUI.skin, which is process-global
            // and shared with the BepInEx console and every other IMGUI mod.

            // Fire button — green background but flat, menu-item height
            _fire = new GUIStyle(_btn)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _fire.normal.background = _fireTex;
            _fire.hover.background = _fireTex;
            _fire.active.background = _fireTex;
            _fire.focused.background = _fireTex;
            _fire.onNormal.background = _fireTex;
            _fire.onHover.background = _fireTex;
            _fire.onActive.background = _fireTex;
            _fire.onFocused.background = _fireTex;
            _fire.normal.textColor = _fire.hover.textColor = _fire.active.textColor = _fire.focused.textColor = Color.white;
            _fire.onNormal.textColor = _fire.onHover.textColor = _fire.onActive.textColor = _fire.onFocused.textColor = Color.white;

            // Fire Now button — flat like other buttons
            _fireNow = new GUIStyle(_btn)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _fireNow.normal.background = _transparentTex;
            _fireNow.hover.background = _menuHoverTex;
            _fireNow.active.background = _menuHoverTex;
            _fireNow.focused.background = _menuHoverTex;
            _fireNow.onNormal.background = _transparentTex;
            _fireNow.onHover.background = _menuHoverTex;
            _fireNow.onActive.background = _menuHoverTex;
            _fireNow.onFocused.background = _menuHoverTex;
            _fireNow.normal.textColor = _fireNow.focused.textColor = TextDim;
            _fireNow.hover.textColor = _fireNow.active.textColor = TextMain;
            _fireNow.onNormal.textColor = _fireNow.onFocused.textColor = TextDim;
            _fireNow.onHover.textColor = _fireNow.onActive.textColor = TextMain;
        }
    }
}
