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
    /// Split across four partial files:
    ///   Hud.cs         — lifecycle, selection tracking, window layout, shooter/row data
    ///   Hud.Render.cs  — panel content rendering + fire actions
    ///   Hud.Mouse.cs   — resize + mouse-over-UI capture
    ///   Hud.Styles.cs  — palette, textures, GUIStyle construction
    ///
    /// While the cursor is over the panel it sets the game's MouseControlState to "UI"
    /// so clicks/drags don't leak into the camera or world selection (see Hud.Mouse.cs).
    /// </summary>
    internal sealed partial class Hud : MonoBehaviour
    {
        private bool _visible = true; // fully shown; Alt+G hides the panel entirely (even the tab)
        private bool _open = false;   // when visible: expanded vs. collapsed tab; toggled by the ▸ chevron
        private ObjectBase _anchor;      // last selected friendly unit
        private ObjectBase _target;      // last selected enemy unit (real object, for firing)
        private Vehicle _targetVehicle;  // the enemy contact, for fog-of-war-correct display
        private bool _wholeFormation;

        private readonly Dictionary<string, bool> _checked = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _salvo = new Dictionary<string, int>();
        private readonly List<EngagementBoard.SalvoLine> _salvos = new List<EngagementBoard.SalvoLine>();

        // Per-frame cache for EngageRows — avoids recomputing expensive range/guidance
        // checks 3+ times per ship per OnGUI pass (IsMissileShip, draw loop, AnyChecked, FireSelected).
        private readonly Dictionary<int, List<Row>> _rowCache = new Dictionary<int, List<Row>>();
        private int _rowCacheFrame = -1;
        private int _pruneCounter;

        // Reusable shooter list — avoids allocating a new List<ObjectBase> every OnGUI call.
        private readonly List<ObjectBase> _shootersCache = new List<ObjectBase>();

        private Vector2 _scroll;
        private Rect _win = new Rect(0, 0, DefaultWindowW, DefaultWindowH);
        private float _expandedW = DefaultWindowW, _expandedH = DefaultWindowH;
        private bool _placed;
        private float _lastScale;   // previous EffectiveScale, to re-anchor the window on scale changes
        private bool _resizing;
        private bool _lastOverUi;
        private bool _mouseDownOverUi;

        private const float CollapsedH = 34f;
        private const float HeaderH = 30f;

        // Window geometry.
        private const float DefaultWindowW = 540f, DefaultWindowH = 520f;
        private const float MinWindowW = 420f, MinWindowH = 320f;   // enforced while resizing
        private const float InitialTopMargin = 40f;                  // first-paint placement
        private const float InitialSideMargin = 8f;
        private const float OffscreenMargin = 60f;                   // px the window always keeps on screen
        private const int WindowId = 0xA070F0;                       // "A070F0" ~ "AutoTOT" in leet hex

        // Content layout shared with the Render partial.
        internal const float RowHeight = 26f;                        // interactive rows (missile pick, checkbox)
        internal const float FireButtonHeight = 38f;
        internal const float MinSpreadToDisplay = 0.1f;              // smaller arrival spreads aren't shown
        private const int SelectionPruneIntervalFrames = 300;        // how often dead ships are pruned from selections

        // Only alive inside a running mission. In the main menu Globals._mainGameViewModel is null,
        // so the planner neither draws nor eats mouse input there.
        private static bool InMission() => Globals._mainGameViewModel != null;

        // Uniform UI scale for the whole panel. 0 in config = auto: 1x at 1080p, ~2x at 2160p.
        // Shared by OnGUI (GUI.matrix) and the Update-path input handlers (Hud.Mouse.cs), which
        // must divide real screen pixels by this to reach the panel's scaled GUI space.
        // Human-readable form of the configured hide combo (e.g. "Alt+G", or just "G" with no modifier).
        private static string HideHint()
        {
            string key = Bootstrap.PanelKey.ToString();
            if (Bootstrap.ToggleModifier == KeyCode.None) return key;
            string mod = Bootstrap.ToggleModifier.ToString()
                .Replace("Left", "").Replace("Right", "");   // "LeftAlt" -> "Alt"
            return mod + "+" + key;
        }

        private static float EffectiveScale()
        {
            float s = Bootstrap.UiScale;
            if (s <= 0f) s = Mathf.Max(1f, Screen.height / 1280f); // gentler auto: 1x up to 1280p, ~1.13x at 1440p, ~1.69x at 4K
            s *= Bootstrap.UiScaleMultiplier;
            return Mathf.Clamp(s, 0.5f, 4f);
        }

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
            if (modOk && Input.GetKeyDown(Bootstrap.PanelKey)) _visible = !_visible;
            if (modOk && Input.GetKeyDown(Bootstrap.ToggleKey))
            {
                Coordinator.Active = !Coordinator.Active;
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(Coordinator.Active ? "ON" : "OFF")}");
            }

            // While hidden, draw nothing and release any input capture so the camera is free.
            if (!_visible)
            {
                _resizing = false;
                SetOverUi(false);
                return;
            }

            HandleResizeInput();
            UpdateMouseCapture();
        }

        private void OnDisable()
        {
            // On mission end Unity disables us while the game HUD is already tearing down, so the
            // over-UI release can hit half-null game state. Reset our own flag unconditionally and
            // only poke the game when a mission is still live; SetOverUi also catches defensively.
            _lastOverUi = false;
            if (InMission()) SetOverUi(false);
        }

        private void OnDestroy()
        {
            // Destroy every texture we created, not just a subset — otherwise the rest leak.
            foreach (Texture2D t in new[]
            {
                _panelTex, _headerTex, _fireTex, _btnTex, _btnHoverTex,
                _scrollThumbTex, _scrollTrackTex, _menuHoverTex, _transparentTex,
            })
                if (t != null) Object.Destroy(t);
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

        // ---------- GUI ----------

        private void OnGUI()
        {
            if (!Coordinator.Enabled || !Bootstrap.ShowIndicator || !InMission() || !_visible) return;
            EnsureStyles();

            // Everything below works in scaled GUI space (see EffectiveScale). sw/sh are the
            // screen extents expressed in that space, so placement and clamps stay correct.
            float s = EffectiveScale();
            float sw = Screen.width / s, sh = Screen.height / s;

            // Keep the panel's on-screen top-left fixed when the scale changes (the matrix
            // scales about the screen origin, so a bare _win.x would drift as s changes).
            if (_lastScale > 0f && !Mathf.Approximately(_lastScale, s))
            {
                float k = _lastScale / s;
                _win.x *= k;
                _win.y *= k;
            }
            _lastScale = s;

            if (!_placed)   // first paint: drop it near the top-center
            {
                _win.x = Mathf.Max(InitialSideMargin, (sw - _expandedW) * 0.5f);
                _win.y = InitialTopMargin;
                _placed = true;
            }

            _win.width = _expandedW;
            _win.height = _open ? _expandedH : CollapsedH;

            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            _win = GUI.Window(WindowId, _win, DrawWindow, GUIContent.none, _winStyle);
            GUI.matrix = prevMatrix;

            // Keep the window on-screen.
            _win.x = Mathf.Clamp(_win.x, -_win.width + OffscreenMargin, sw - OffscreenMargin);
            _win.y = Mathf.Clamp(_win.y, 0f, sh - CollapsedH);
        }

        private string _lastDrawError;

        private void DrawWindow(int id)
        {
            // Log-only guard: capture which panel draw threw (with a full stack), then
            // re-throw so IMGUI behaviour is identical to before. Deduped by message so
            // a per-frame throw doesn't flood the log.
            try
            {
                DrawWindowInner(id);
            }
            catch (System.Exception e)
            {
                if (e.Message != _lastDrawError)
                {
                    _lastDrawError = e.Message;
                    Bootstrap.Log.LogError($"[AutoTOT] HUD DrawWindow threw:\n{e}");
                }
                throw;
            }
        }

        private void DrawWindowInner(int id)
        {
            EngagementBoard.CollectSalvos(_salvos);   // live engagement snapshot (used by header + list)

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
            DrawSelectionHeader(haveTarget);

            GUILayout.Space(4);
            DrawDivider();

            GUI.color = TextDim;
            GUILayout.Label("salvo ±:  Shift +10  ·  Ctrl +5", _hdr);
            GUI.color = Color.white;

            List<ObjectBase> shooters = GetShooters();

            // Periodically prune destroyed ships from _checked/_salvo to prevent unbounded growth.
            if (++_pruneCounter >= SelectionPruneIntervalFrames)
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

            GUILayout.BeginHorizontal();
            bool auto = DrawCheckbox(Coordinator.Active, "Also auto-coordinate normal group orders (Alt+T)", _row);
            if (auto != Coordinator.Active)
            {
                Coordinator.Active = auto;
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(auto ? "ON" : "OFF")}");
            }
            GUILayout.FlexibleSpace();
            DrawScaleControl();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUI.enabled = haveTarget && AnyChecked(shooters);
            if (GUILayout.Button("FIRE — TIME ON TARGET", _fire, GUILayout.Height(FireButtonHeight)))
                FireSelected(shooters, coordinated: true);
            if (GUILayout.Button("FIRE NOW\n(no sync)", _fireNow, GUILayout.Height(FireButtonHeight), GUILayout.Width(100)))
                FireSelected(shooters, coordinated: false);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            DrawResizeGrip();
            GUI.DragWindow(new Rect(0, 0, 100000, HeaderH));
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

                // Cap the selectable count at what the serving launchers can actually fire (loaded +
                // magazine reserve), not the ship-wide inventory — otherwise the salvo picker could
                // request rounds sitting behind an unusable launcher, and the strike fires short.
                int count = Mathf.Min(kv.Value, LauncherFactsSource.AvailableRounds(ship, kv.Key));
                if (count <= 0) continue;
                yield return new Row { AmmoId = kv.Key, Count = count, InRange = inRange };
            }
        }

        // Remove entries from _checked/_salvo for ships that no longer exist, preventing unbounded
        // dictionary growth over long sessions. Liveness is tested against the game's own unit list
        // (ObjectsManager._listOfAllUnits, which drops an object on destruction) — NOT against the
        // currently-selected shooters, which would wrongly wipe saved selections for every ship the
        // player isn't looking at right now.
        private static readonly HashSet<int> _liveIdScratch = new HashSet<int>();
        private readonly List<string> _deadKeyScratch = new List<string>();
        private void PruneCheckSalvo()
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;

            _liveIdScratch.Clear();
            List<ObjectBase> units = Singleton<ObjectsManager>.Instance._listOfAllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                ObjectBase u = units[i];
                if (u != null && !u.IsDestroyed) _liveIdScratch.Add(u.GetInstanceID());
            }

            _deadKeyScratch.Clear();
            foreach (KeyValuePair<string, bool> kv in _checked)
            {
                int pipeIdx = kv.Key.IndexOf('|');
                if (pipeIdx <= 0) continue;
                if (!int.TryParse(kv.Key.Substring(0, pipeIdx), out int instanceId)) continue;
                if (!_liveIdScratch.Contains(instanceId)) _deadKeyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _deadKeyScratch.Count; i++)
            {
                _checked.Remove(_deadKeyScratch[i]);
                _salvo.Remove(_deadKeyScratch[i]);
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
    }
}
