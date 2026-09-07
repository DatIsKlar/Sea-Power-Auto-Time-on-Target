using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// On-screen planner. Remembers your last-selected friendly ship (the "anchor") and your
    /// last-selected enemy as the target. The anchor can be held in a persistent shooter roster,
    /// one hull or one formation at a time, drawn from as many formations as you like. Lists each shooter's missiles with live flight-time readouts and lets
    /// you fire a hand-picked set as a coordinated Time-on-Target strike.
    ///
    /// Split across four partial files:
    ///   Hud.cs         ; lifecycle, selection tracking, window layout, shooter/row data
    ///   Hud.Render.cs  ; panel content rendering + fire actions
    ///   Hud.Mouse.cs   ; resize + mouse-over-UI capture
    ///   Hud.Styles.cs  ; palette, textures, GUIStyle construction
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
        // ENGAGEMENTS reports what is already in the air, so it sits below the planning controls and
        // can be folded away entirely while a strike is being built.
        private bool _engagementsOpen = true;
        // The hotkey list, behind a ? in the title bar: discoverable once rather than shouted every
        // frame from a hint line.
        private bool _showHelp;

        private readonly Dictionary<string, bool> _checked = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _salvo = new Dictionary<string, int>();
        private readonly List<EngagementBoard.SalvoLine> _salvos = new List<EngagementBoard.SalvoLine>();

        // The persistent strike group: shooters the player has added, kept across selection changes
        // so the group can be assembled BEFORE any target is picked. Empty means "follow the current
        // selection", which is the original single-selection behaviour.
        private readonly List<ObjectBase> _group = new List<ObjectBase>();

        // Shots staged for a multi-target strike. Each carries its own target, so the panel stays a
        // single-target editor and a strike is assembled from repeated single-target picks.
        private readonly List<Coordinator.Shot> _strike = new List<Coordinator.Shot>();
        // The Coordinator reset the staged list was built against. A mission end clears coordinator
        // state, so anything staged refers to units that no longer exist.
        private int _strikeGeneration;

        // Per-frame cache for EngageRows ; avoids recomputing expensive range/guidance
        // checks 3+ times per ship per OnGUI pass (IsMissileShip, draw loop, AnyChecked, FireSelected).
        private readonly Dictionary<int, List<Row>> _rowCache = new Dictionary<int, List<Row>>();
        private int _rowCacheFrame = -1;
        private int _pruneCounter;

        // Reusable shooter list ; avoids allocating a new List<ObjectBase> every OnGUI call.
        private readonly List<ObjectBase> _shootersCache = new List<ObjectBase>();

        /// <summary>
        /// A shared-missile-group finding for one row: several checked shooters are close enough to
        /// feed ONE group, and between them they have staged more rounds than that group can hold.
        /// </summary>
        private struct GroupShare
        {
            public int Combined;    // rounds staged across every shooter sharing this group
            public int Cap;         // ap._maxGroupSize
            public float JoinNm;    // ap._groupJoinRangeUnity, in nautical miles
            public int Shooters;    // how many shooters are inside that radius, this one included
        }

        // Rebuilt once per drawn frame, keyed like _checked/_salvo. Cross-shooter by nature, so it
        // cannot be computed from inside a single row. Frame-gated like _rowCache: this pass is
        // O(shooters^2) and OnGUI fires several events per frame.
        private readonly Dictionary<string, GroupShare> _groupShare = new Dictionary<string, GroupShare>();
        private int _groupShareFrame = -1;

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
        private const float MinWindowW = 480f, MinWindowH = 320f;   // enforced while resizing;
                                                            // MinWindowW must hold the three
                                                            // secondary commit buttons in a row
        private const float InitialTopMargin = 40f;                  // first-paint placement
        private const float InitialSideMargin = 8f;
        private const float OffscreenMargin = 60f;                   // px the window always keeps on screen
        private const float AutoScaleRefHeight = 1280f;
        // Bounds for the panel scale. Named here because Bootstrap declares the same numbers as
        // config AcceptableValueRanges and as the clamp in SetUiScaleMultiplier; three copies of a
        // range drift the moment one is widened.
        internal const float MinUiScale = 0.5f, MaxUiScale = 4f;
        internal const float MinUiScaleMultiplier = 0.5f, MaxUiScaleMultiplier = 2.0f;
        internal const float UiScaleStep = 0.1f;   // one press of the on-panel - / + buttons
        private const int WindowId = 0xA070F0;                       // "A070F0" ~ "AutoTOT" in leet hex

        // Content layout shared with the Render partial.
        internal const float RowHeight = 26f;                        // interactive rows (missile pick, checkbox)
        internal const float FireButtonHeight = 38f;
        internal const float MinSpreadToDisplay = 0.1f;              // smaller arrival spreads aren't shown
        internal const float ResizeGripClearance = 18f;             // px kept clear of the corner grip
        private const int SelectionPruneIntervalFrames = 300;        // how often dead ships are pruned from selections

        // Only alive inside a running mission. In the main menu Globals._mainGameViewModel is null,
        // so the planner neither draws nor eats mouse input there.
        private static bool InMission() => Globals._mainGameViewModel != null;

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

        // Uniform UI scale for the whole panel. 0 in config = auto: 1x at 1080p, ~2x at 2160p.
        // Shared by OnGUI (GUI.matrix) and the Update-path input handlers (Hud.Mouse.cs), which
        // must divide real screen pixels by this to reach the panel's scaled GUI space.
        private static float EffectiveScale()
        {
            float s = Bootstrap.UiScale;
            if (s <= 0f) s = Mathf.Max(1f, Screen.height / AutoScaleRefHeight); // gentler auto: 1x up to 1280p, ~1.13x at 1440p, ~1.69x at 4K
            s *= Bootstrap.UiScaleMultiplier;
            return Mathf.Clamp(s, MinUiScale, MaxUiScale);
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
            if (modOk && Input.GetKeyDown(Bootstrap.StrikeArmKey)) ToggleStrikeArmed();

            // A mission end clears the coordinator, and with it every unit the staged strike points
            // at. Checked here rather than in OnGUI so the list cannot be rendered stale for a frame.
            if (_strikeGeneration != Coordinator.ResetGeneration)
            {
                _strikeGeneration = Coordinator.ResetGeneration;
                _strike.Clear();
                _group.Clear();
                // Unity reuses instance IDs after destruction, so a ship that threw while building
                // its rows in one mission could inherit the ID of a healthy ship in the next and be
                // silently dropped from the panel. The set exists only to stop one log line per
                // frame within a mission, so a mission boundary is the right place to forget it.
                _rowErrorLogged.Clear();
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
            // Destroy every texture we created, not just a subset ; otherwise the rest leak.
            foreach (Texture2D t in new[]
            {
                _panelTex, _headerTex, _fireTex, _btnTex, _btnHoverTex,
                _scrollThumbTex, _scrollTrackTex, _menuHoverTex, _transparentTex,
                _outlineTex, _outlineHoverTex,
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
                float rescale = _lastScale / s;
                _win.x *= rescale;
                _win.y *= rescale;
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
            // Reset per-frame caches. OnGUI fires 2+ events per frame (Layout, Repaint, ...), so
            // recompute the engagement snapshot and per-ship EngageRows once per frame only.
            int frame = Time.frameCount;
            if (_rowCacheFrame != frame)
            {
                _rowCache.Clear();
                EngagementBoard.CollectSalvos(_salvos);   // live engagement snapshot (header + list)
                _rowCacheFrame = frame;
            }

            // Lighter title strip across the top, like the game's own panel headers.
            GUI.DrawTexture(new Rect(1, 1, _win.width - 2, HeaderH + 3), _headerTex);
            DrawHeader();
            if (!_open) { GUI.DragWindow(new Rect(0, 0, 100000, CollapsedH)); return; }

            DrawDivider();
            GUILayout.Space(4);

            if (_showHelp) DrawHelpOverlay();

            bool haveTarget = _target != null && !_target.IsDestroyed;
            DrawSelectionHeader(haveTarget);

            List<ObjectBase> shooters = GetShooters();

            GUILayout.Space(4);
            DrawDivider();
            DrawStatusLine(shooters, haveTarget);

            // Periodically prune destroyed ships from _checked/_salvo to prevent unbounded growth.
            if (++_pruneCounter >= SelectionPruneIntervalFrames)
            {
                _pruneCounter = 0;
                PruneCheckSalvo();
            }
            // Apply our scrollbar styling only around our own scroll view, then restore ; so we
            // never restyle other mods' IMGUI (BepInEx console etc.) via the shared GUI.skin.
            GUIStyle prevVBar = GUI.skin.verticalScrollbar, prevVThumb = GUI.skin.verticalScrollbarThumb;
            GUIStyle prevHBar = GUI.skin.horizontalScrollbar, prevHThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.verticalScrollbar = _scrollTrack;
            GUI.skin.verticalScrollbarThumb = _scrollThumb;
            GUI.skin.horizontalScrollbar = _hScrollTrack;
            GUI.skin.horizontalScrollbarThumb = _hScrollThumb;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (shooters.Count == 0)
                GUILayout.Label(_group.Count > 0
                    ? "No shooters held. Click one of your ships, then + SHIP."
                    : "No missile-armed ships selected. Click one of your ships, or + SHIP to hold a roster.",
                    _row);

            RecomputeGroupSharing(shooters);

            for (int si = 0; si < shooters.Count; si++)
            {
                ObjectBase ship = shooters[si];
                GUILayout.Space(3);
                GUILayout.BeginHorizontal();
                GUILayout.Label(UnitNaming.SafeName(ship), _ship);
                GUILayout.FlexibleSpace();
                if (_group.Count > 0 && GUILayout.Button("remove", _btn, GUILayout.Width(70)))
                    _group.Remove(ship);
                GUILayout.EndHorizontal();
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

            // The staged list sits directly above the button that commits it. Staging needs exactly
            // what firing needs: a target and at least one checked row.
            DrawStagedStrike();
            DrawCommitRow(shooters, haveTarget && AnyChecked(shooters));

            // Live, post-launch state. It reports what is already in the air rather than what is
            // being built, so it goes below everything that builds.
            DrawEngagements();
            DrawDivider();

            GUILayout.BeginHorizontal();
            bool auto = DrawCheckbox(Coordinator.Active, $"Also auto-coordinate normal group orders ({ToggleHint()})");
            if (auto != Coordinator.Active)
            {
                Coordinator.Active = auto;
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(auto ? "ON" : "OFF")}");
            }
            GUILayout.FlexibleSpace();
            DrawScaleControl();
            GUILayout.Space(ResizeGripClearance);   // settings is the bottom row now: keep it clear of the grip
            GUILayout.EndHorizontal();

            DrawResizeGrip();
            GUI.DragWindow(new Rect(0, 0, 100000, HeaderH));
        }

        private List<ObjectBase> GetShooters()
        {
            _shootersCache.Clear();

            // A group, once assembled, IS the shooter list. Members are listed even when they carry
            // nothing that can engage the current target: the per-ship "no missiles that can engage
            // this target" line is the answer the player needs, and silently dropping the ship
            // instead looks like the group forgot it.
            if (_group.Count > 0)
            {
                for (int i = 0; i < _group.Count; i++)
                {
                    ObjectBase u = _group[i];
                    if (u != null && !u.IsDestroyed && !_shootersCache.Contains(u)) _shootersCache.Add(u);
                }
                return _shootersCache;
            }

            if (_anchor == null || _anchor.IsDestroyed) return _shootersCache;

            // No roster means "follow the selection", and the selection is one ship. Shooting with
            // more than one hull goes through the roster, which is the only place the panel keeps
            // a shooter list; there is no second, invisible way to assemble one.
            if (IsMissileShip(_anchor)) _shootersCache.Add(_anchor);
            return _shootersCache;
        }

        /// <summary>
        /// Finds rows whose shooters will share one missile group and have staged more rounds than
        /// the group can hold. Rebuilt once per drawn frame, before the row loop.
        ///
        /// Grouped ammo (GroupSize > 1) does not consume a fire-control channel per round: the first
        /// round away forms a group and holds one channel, and every later round that JOINS that
        /// group skips the channel check entirely. One ship therefore empties its magazine through a
        /// single channel, which is why a lone Slava fires all 16 SS-N-12.
        ///
        /// The trap is a second shooter within the ammo's GroupJoinRange. Its rounds join the FIRST
        /// ship's group instead of forming their own, so it never engages its own channels; the
        /// shared group fills at GroupSize, nothing more can join, and neither ship has a spare
        /// channel left. Both launchers then sit in the game's ReadyUpWhileWaiting with loaded tubes.
        /// Measured: two Slavas 8 or 13 nm apart put up 16 rounds between them, the same pair at 25
        /// or 40 nm put up all 32. Beyond the join range each ship forms its own group and is fine.
        ///
        /// The game measures group leader to firing mount, not ship to ship. Ship separation is the
        /// proxy used here because the leader starts at its shooter, and at decision time (before
        /// anything has launched) there is no leader to measure against.
        /// </summary>
        private void RecomputeGroupSharing(List<ObjectBase> shooters)
        {
            if (_groupShareFrame == Time.frameCount) return;
            _groupShareFrame = Time.frameCount;
            _groupShare.Clear();
            if (_target == null || _target.IsDestroyed || shooters.Count < 2) return;

            for (int i = 0; i < shooters.Count; i++)
            {
                ObjectBase a = shooters[i];
                if (a == null || a.IsDestroyed) continue;

                foreach (Row r in CachedEngageRows(a))
                {
                    if (!r.InRange) continue;
                    string key = Key(a, r.AmmoId);
                    if (!_checked.TryGetValue(key, out bool on) || !on) continue;

                    LauncherFactsSource.Facts f = LauncherFactsSource.Get(a, r.AmmoId);
                    if (!f.Valid || !f.CanGroup || f.MaxGroupSize <= 1 || f.GroupJoinRangeU <= 0f) continue;

                    // The game groups on _ammunitionFileName, not on the ammo id, and the two are
                    // not interchangeable elsewhere in this codebase. Resolve both sides.
                    string ammoFile = a.getAmmunitionByName(r.AmmoId)?._ap?._ammunitionFileName;
                    if (string.IsNullOrEmpty(ammoFile)) continue;

                    int combined = _salvo.TryGetValue(key, out int mine) ? mine : 1;
                    int peers = 1;
                    float joinSq = f.GroupJoinRangeU * f.GroupJoinRangeU;
                    for (int j = 0; j < shooters.Count; j++)
                    {
                        if (j == i) continue;
                        ObjectBase b = shooters[j];
                        if (b == null || b.IsDestroyed) continue;
                        if ((b.transform.position - a.transform.position).sqrMagnitude > joinSq) continue;

                        foreach (Row br in CachedEngageRows(b))
                        {
                            if (!br.InRange) continue;
                            if (b.getAmmunitionByName(br.AmmoId)?._ap?._ammunitionFileName != ammoFile) continue;
                            string bKey = Key(b, br.AmmoId);
                            if (!_checked.TryGetValue(bKey, out bool bOn) || !bOn) continue;
                            int bSalvo = _salvo.TryGetValue(bKey, out int sv) ? sv : 1;
                            if (bSalvo <= 0) continue;
                            combined += bSalvo;
                            peers++;
                        }
                    }

                    if (peers > 1 && combined > f.MaxGroupSize)
                        _groupShare[key] = new GroupShare
                        {
                            Combined = combined,
                            Cap = f.MaxGroupSize,
                            JoinNm = f.GroupJoinRangeU * GameUnits.UnityToNm,
                            Shooters = peers,
                        };
                }
            }
        }

        private struct Row { public string AmmoId; public int Count; public bool InRange; }

        private bool IsMissileShip(ObjectBase u)
        {
            if (u == null || u.IsDestroyed || !u.isUnit() || !u.IsPlayerObject) return false;
            foreach (Row r in CachedEngageRows(u))
                return true;
            return false;
        }

        // Cached wrapper around EngageRows ; materialises the IEnumerable to a list once per
        // ship per frame, then returns the same list for all callers within that frame.
        private List<Row> CachedEngageRows(ObjectBase ship)
        {
            int id = ship.GetInstanceID();
            if (_rowCache.TryGetValue(id, out List<Row> cached)) return cached;
            var list = new List<Row>();
            try
            {
                foreach (Row r in EngageRows(ship)) list.Add(r);
            }
            catch (System.Exception e)
            {
                // One unreadable ship used to take the whole panel down through DrawWindow, and the
                // stack that came out named neither the ship nor the ammo. Contain it here: the ship
                // lists as carrying nothing, everything else still draws, and the log says which hull
                // and how many rows it managed before it threw.
                list.Clear();
                if (_rowErrorLogged.Add(id))
                    Bootstrap.Log.LogError(
                        $"[AutoTOT] engage rows failed for {UnitNaming.SafeName(ship)}; listing it as carrying " +
                        $"nothing. This ship is excluded until the mission ends.\n{e}");
            }
            _rowCache[id] = list;
            return list;
        }

        // Ships whose row build already threw, so the log carries one report each rather than one
        // per frame. Instance ids, cleared with the rest of the selection state.
        private readonly HashSet<int> _rowErrorLogged = new HashSet<int>();

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
                // magazine reserve), not the ship-wide inventory ; otherwise the salvo picker could
                // request rounds sitting behind an unusable launcher, and the strike fires short.
                int count = Mathf.Min(kv.Value, LauncherFactsSource.AvailableRounds(ship, kv.Key));
                // Second cap: a radio-command weapon that cannot group holds one fire-control
                // channel per round in flight, so the boat physically cannot put up more than it
                // has channels. Offering more would issue an order that fires short and then
                // truncates the wave's shared impact time. See LauncherFactsSource.GuidanceChannelCap.
                count = Mathf.Min(count, LauncherFactsSource.GuidanceChannelCap(ship, kv.Key));
                if (count <= 0) continue;
                yield return new Row { AmmoId = kv.Key, Count = count, InRange = inRange };
            }
        }

        // Remove entries from _checked/_salvo for ships that no longer exist, preventing unbounded
        // dictionary growth over long sessions. Liveness is tested against the game's own unit list
        // (ObjectsManager._listOfAllUnits, which drops an object on destruction) ; NOT against the
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

            // Same liveness test for the staged strike: a shot whose shooter or target has died can
            // never be fired, and holding the reference keeps a destroyed object alive.
            _strike.RemoveAll(sh =>
                sh.Unit == null || sh.Target == null ||
                !_liveIdScratch.Contains(sh.Unit.GetInstanceID()) ||
                !_liveIdScratch.Contains(sh.Target.GetInstanceID()));

            _group.RemoveAll(u => u == null || !_liveIdScratch.Contains(u.GetInstanceID()));
        }

        private bool AnyChecked(List<ObjectBase> shooters)
        {
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                    if (r.InRange && _checked.TryGetValue(Key(ship, r.AmmoId), out bool on) && on) return true;
            return false;
        }

        private static string Key(ObjectBase u, string ammoId) => u.GetInstanceID() + "|" + ammoId;

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

            if (v.Class.HasValue)
            {
                try { return v.Object.Name.Value; } catch { return $"Contact {v.Id}"; }
            }
            string s = $"Contact {v.Id}";
            try { if (v.HasSignalInfo()) s += "; " + v.IncomingSignalInfo(); } catch { }
            return s;
        }

        // Human-readable form of the configured strike-arm combo, for the panel hint.
        private static string StrikeHint() => Combo(Bootstrap.StrikeArmKey);

        private void ToggleStrikeArmed()
        {
            if (Coordinator.StrikeArmed) Coordinator.CancelStrike();
            else Coordinator.ArmStrike();
        }

        /// <summary>
        /// Move the currently checked rows into the staged strike against the current target, then
        /// clear the checkboxes so the next formation and target can be picked. Rows already staged
        /// for the same shooter, ammo and target are replaced rather than doubled up.
        /// </summary>
        private void AddSelectionToStrike(List<ObjectBase> shooters)
        {
            if (_target == null || _target.IsDestroyed) return;
            int added = 0;
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                {
                    if (!r.InRange) continue;
                    string key = Key(ship, r.AmmoId);
                    if (!_checked.TryGetValue(key, out bool on) || !on) continue;
                    int salvo = Mathf.Min(_salvo.TryGetValue(key, out int sv) ? sv : 1, r.Count);
                    _strike.RemoveAll(x => x.Unit == ship && x.AmmoId == r.AmmoId && x.Target == _target);
                    _strike.Add(new Coordinator.Shot
                    {
                        Unit = ship, AmmoId = r.AmmoId, Salvo = salvo, Target = _target,
                    });
                    _checked[key] = false;
                    added++;
                }
            if (added > 0) _strikeGeneration = Coordinator.ResetGeneration;
        }

        /// <summary>
        /// Add the current selection to the shooter roster: just the selected ship, or every
        /// missile-armed ship in its formation. Which one is the caller's choice, not a mode read
        /// off the panel. No target is needed, which is the point: the roster is what you assemble
        /// first, then aim, and it is not limited to one formation.
        /// </summary>
        private void AddSelectionToGroup(bool wholeFormation)
        {
            if (_anchor == null || _anchor.IsDestroyed) return;

            if (wholeFormation && _anchor.Formation != null)
            {
                foreach (Station st in _anchor.Formation.Stations)
                {
                    ObjectBase u = st?.UnitObject;
                    if (IsMissileShip(u) && !_group.Contains(u)) _group.Add(u);
                }
            }
            if (IsMissileShip(_anchor) && !_group.Contains(_anchor)) _group.Add(_anchor);
            _strikeGeneration = Coordinator.ResetGeneration;
        }

        /// <summary>
        /// True when every ship + FORMATION would add is already in the roster. Tested against
        /// IsMissileShip for the same reason AddSelectionToGroup filters on it: a formation's
        /// non-missile hulls are never added, so counting them would leave the button reading
        /// "+ FORMATION" forever with nothing left to add.
        /// </summary>
        private bool FormationFullyHeld(ObjectBase anchor)
        {
            if (anchor?.Formation == null) return false;
            bool any = false;
            foreach (Station st in anchor.Formation.Stations)
            {
                ObjectBase u = st?.UnitObject;
                if (!IsMissileShip(u)) continue;
                any = true;
                if (!_group.Contains(u)) return false;
            }
            // AddSelectionToGroup also adds the anchor itself, whether or not it holds a station.
            if (IsMissileShip(anchor))
            {
                any = true;
                if (!_group.Contains(anchor)) return false;
            }
            return any;
        }

        /// <summary>Distinct targets in the staged strike, in the order they were first staged.</summary>
        private void CollectStrikeTargets(List<ObjectBase> into)
        {
            into.Clear();
            foreach (Coordinator.Shot s in _strike)
                if (s.Target != null && !into.Contains(s.Target)) into.Add(s.Target);
        }

        private static string FormatTime(float sec)
        {
            if (sec <= 0f) return "0:00";
            int s = Mathf.RoundToInt(sec);
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
