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
        // The strike list scrolls on its own, so a long strike cannot push the shooter list off the
        // panel. Its height is a share of the window (see StrikeListShare), which keeps the two
        // lists in the same proportion at every window size.
        private Vector2 _strikeScroll;
        // The per-order breakdown under each strike target, collapsible: it is the confirmation the
        // player wants while building a strike and noise once the strike is built.
        private bool _strikeDetail = true;
        // Frame-based title-bar drag (see Hud.Mouse.cs). The rects are window-local and are
        // published by DrawHeader so a press on the chevron or on ? KEYS is a click, not a drag.
        private bool _dragging;
        private Vector2 _dragOffset;
        private Rect _chevRectWin, _helpRectWin;
        // Vertical budget shared by the two lists, measured from the panel itself rather than
        // guessed: _listsTopY is where the shooter list starts and _belowListsH is everything drawn
        // under the strike section (commit rows, engagements, footer). Both are sampled on Repaint
        // and used on the NEXT frame, so the Layout and Repaint passes of any one frame always see
        // the same numbers, which IMGUI requires.
        private float _listsTopY, _belowListsH = 180f;
        private float _listsAvailH = 200f;   // last computed budget; the strike section reads it
        private Rect _win = new Rect(0, 0, DefaultWindowW, DefaultWindowH);
        private float _expandedW = DefaultWindowW, _expandedH = DefaultWindowH;
        private bool _placed;
        private float _lastScale;   // previous EffectiveScale, to re-anchor the window on scale changes
        private bool _resizing;
        private bool _lastOverUi;
        private bool _mouseDownOverUi;

        private const float CollapsedH = 34f;
        // Room for the key list under the title bar while the panel is collapsed; measured from the
        // text itself (see HelpOverlayHeight), because the lines wrap differently at every panel
        // width and with every set of configured key names.
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
        internal const float RowHeight = 26f;                        // interactive rows (missile pick, buttons)
        internal const float FooterRowHeight = 20f;                  // the scale stepper row, the panel's thinnest
        internal const float FooterStripH = FooterRowHeight + 6f;    // that row plus its divider and air
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
                _dragging = false;
                SetOverUi(false);
                return;
            }

            // With the master switch off the mod does nothing at all, and that has to include this
            // component: OnGUI already refuses to paint, but Update went on running the hotkeys and
            // the mouse capture, so an invisible panel could still arm a strike and swallow a click
            // meant for the map. Finding 2 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md. The switch
            // is startup-only now, so this costs one bool per frame and never flickers.
            if (!Coordinator.Enabled)
            {
                _resizing = false;
                _dragging = false;
                SetOverUi(false);
                return;
            }

            TrackSelection();

            bool modOk = Bootstrap.ToggleModifier == KeyCode.None || Input.GetKey(Bootstrap.ToggleModifier);
            if (modOk && Input.GetKeyDown(Bootstrap.PanelKey)) _visible = !_visible;
            if (modOk && Input.GetKeyDown(Bootstrap.ToggleKey))
            {
                Coordinator.Active = !Coordinator.Active;
                LogDisabledEntry("auto-coordination toggle");
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(Coordinator.Active ? "ON" : "OFF")}");
            }
            if (modOk && Input.GetKeyDown(Bootstrap.StrikeArmKey)) { LogDisabledEntry("strike arm"); ToggleStrikeArmed(); }
            if (modOk && Input.GetKeyDown(Bootstrap.FireStrikeKey)) { LogDisabledEntry("fire strike"); FireStrikeNow(); }

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
                _dragging = false;
                SetOverUi(false);
                return;
            }

            // Nothing is painted while the indicator is off, so nothing may capture the mouse over
            // where the panel would have been. The hotkeys stay live: arming a strike still works
            // through the game's own order interface with the panel hidden. Second half of
            // finding 2.
            if (!Bootstrap.ShowIndicator)
            {
                _resizing = false;
                _dragging = false;
                SetOverUi(false);
                return;
            }

            HandleDragInput();
            HandleResizeInput();
            UpdateMouseCapture();
        }

        private static int _drewFrame = -1;

        /// <summary>
        /// D2 of the beta-release audit, kept after the fix as a regression guard. Nothing should
        /// reach a hotkey with the master switch off now that Update returns early, so a line here
        /// means that early return has been bypassed.
        /// </summary>
        private static void LogDisabledEntry(string what)
        {
            if (Coordinator.Enabled) return;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] disabled-hotkey: {what} ran with Enabled=false. " +
                $"D2 of the beta-release audit.");
        }

        /// <summary>
        /// D10 of the beta-release audit: whether OnGUI has painted since the last board census.
        /// The engagement board's only prune path is CollectSalvos, which is called from the draw
        /// path, so the census needs to know whether that path ran.
        /// </summary>
        internal static bool DrewSinceLastCensus()
        {
            bool drew = _drewFrame >= 0;
            _drewFrame = -1;
            return drew;
        }

        private void OnEnable()
        {
            // The coordinator needs the panel's staged picks to police its own intake, and only the
            // panel knows them. Cleared in OnDisable so a torn-down HUD cannot be called into.
            Coordinator.StagedRoundsProvider = StagedRoundsFor;
        }

        private void OnDisable()
        {
            Coordinator.StagedRoundsProvider = null;
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
            _drewFrame = Time.frameCount;   // D10: the board's prune path only runs from here
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
            _win.height = _open ? _expandedH
                                : CollapsedH + (_showHelp ? HelpOverlayHeight() : 0f);

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

            // Lighter title strip across the top, like the game's own panel headers. The strip runs
            // from the window's top edge to the bottom of the row the layout reserves for it, and
            // DrawHeader centres its contents on that same rect, so the bar reads as one box.
            Rect headerRow = GUILayoutUtility.GetRect(0f, HeaderH, GUILayout.ExpandWidth(true));
            var strip = new Rect(1f, 1f, _win.width - 2f, headerRow.yMax - 1f);
            GUI.DrawTexture(strip, _headerTex);
            DrawHeader(strip);
            if (!_open)
            {
                // The key list is the one part of the panel worth reading while it is shut, so the
                // ? KEYS toggle keeps working here instead of silently doing nothing.
                if (_showHelp) DrawHelpOverlay();
                return;
            }

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
            // Split the leftover height between the shooter list and the strike list. Both get an
            // explicit height, so neither can starve the other: an expanding view collapses to one
            // row the moment the window is short, which is exactly what a fixed-height strike list
            // beneath it used to cause.
            Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) _listsTopY = topProbe.y;
            _listsAvailH = Mathf.Max(2f * RowHeight,
                                     _win.height - _listsTopY - _belowListsH - ResizeGripClearance);
            float shooterH = Mathf.Max(RowHeight, _listsAvailH - StrikeSectionHeight());

            PushScrollSkin();

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(shooterH));
            if (shooters.Count == 0)
                GUILayout.Label(_group.Count > 0
                    ? "No shooters held. Click one of your units, then + UNIT."
                    : "No missile-armed units selected. Click one of your units, or + UNIT to hold a roster.",
                    _row);

            RecomputeGroupSharing(shooters);

            for (int si = 0; si < shooters.Count; si++)
            {
                ObjectBase ship = shooters[si];
                GUILayout.Space(3);
                GUILayout.BeginHorizontal();
                GUILayout.Label(UnitNaming.SafeName(ship), _ship);
                GUILayout.FlexibleSpace();
                if (_group.Count > 0 && GUILayout.Button("REMOVE", _btnDanger, GUILayout.Width(ClearButtonW),
                                                         GUILayout.Height(RowHeight)))
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

            PopScrollSkin();

            // The staged list sits directly above the button that commits it. Staging needs exactly
            // what firing needs: a target and at least one checked row.
            DrawStagedStrike();

            // Everything from here down is the chrome the budget above has to leave room for.
            Rect belowProbe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            DrawCommitRow(shooters, haveTarget && AnyChecked(shooters));

            // Live, post-launch state. It reports what is already in the air rather than what is
            // being built, so it goes below everything that builds.
            DrawEngagements();

            // The footer is reserved in the flow so the lists' budget leaves room for it, but it is
            // PAINTED at the window's bottom edge (see DrawFooter). Laid out in the flow it ended
            // wherever the sections above happened to end, which parked it on the bottom border as
            // soon as a strike list appeared.
            GUILayoutUtility.GetRect(0f, FooterStripH, GUILayout.ExpandWidth(true));

            Rect endProbe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                _belowListsH = Mathf.Max(0f, endProbe.y - belowProbe.y);

            DrawFooter();
            DrawResizeGrip();
        }

        // Our scrollbar styling is applied only around our own scroll views and then restored, so
        // we never restyle other mods' IMGUI (the BepInEx console and so on) via the shared skin.
        // Both the shooter list and the strike list need it, hence the pair.
        private GUIStyle _prevVBar, _prevVThumb, _prevHBar, _prevHThumb;

        private void PushScrollSkin()
        {
            _prevVBar = GUI.skin.verticalScrollbar; _prevVThumb = GUI.skin.verticalScrollbarThumb;
            _prevHBar = GUI.skin.horizontalScrollbar; _prevHThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.verticalScrollbar = _scrollTrack;
            GUI.skin.verticalScrollbarThumb = _scrollThumb;
            GUI.skin.horizontalScrollbar = _hScrollTrack;
            GUI.skin.horizontalScrollbarThumb = _hScrollThumb;
        }

        private void PopScrollSkin()
        {
            GUI.skin.verticalScrollbar = _prevVBar;
            GUI.skin.verticalScrollbarThumb = _prevVThumb;
            GUI.skin.horizontalScrollbar = _prevHBar;
            GUI.skin.horizontalScrollbarThumb = _prevHThumb;
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

        private struct Row
        {
            public string AmmoId;
            public int Count;          // rounds selectable here: stock, launcher and channels applied
            public bool InRange;
            // Guidance-channel state, carried so the row can EXPLAIN a count of zero instead of
            // vanishing. int.MaxValue = this ammunition is not channel-limited at all.
            public int ChannelCap;
            public int ChannelsElsewhere;   // rounds of it already staged at other targets
        }

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

        /// <summary>
        /// Rounds of this ammo this ship already has staged at OTHER targets. Entries for the
        /// current target are excluded because committing the current selection replaces them
        /// (AddToStrike removes the matching Unit+Ammo+Target entry first), so counting them would
        /// charge the same rounds twice while the player edits a row they have already staged.
        /// </summary>
        private int StagedElsewhere(ObjectBase ship, string ammoId)
        {
            // Rounds the coordinator is already holding for this ship count too. They are a
            // different pool (orders taken in through Alt+H or auto-coordination, plus anything
            // committed and not yet away) and the panel used to ignore them entirely, so four
            // rounds collected in-game still left the panel offering four more.
            int n = Coordinator.HeldRounds(ship, ammoId);
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit == ship && s.AmmoId == ammoId && s.Target != _target)
                    n += Mathf.Max(1, s.Salvo);
            }
            return n;
        }

        /// <summary>
        /// True if anything staged in the panel has filled its shooter's guidance channels. The
        /// coordinator's own flag cannot see planner picks until the strike is fired, and the title
        /// bar is the only place this is readable with the panel collapsed.
        /// </summary>
        private bool AnyStagedChannelCap()
        {
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit != null && Coordinator.IsChannelCapped(s.Unit, s.AmmoId)) return true;
            }
            return false;
        }

        /// <summary>
        /// Rounds staged in the panel for this ship and ammunition, across every target. Published
        /// to the coordinator so the intake gate can see picks that have not been fired yet; the
        /// mirror of <see cref="StagedElsewhere"/> reading the coordinator's own holdings.
        /// </summary>
        private int StagedRoundsFor(ObjectBase ship, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit == ship && s.AmmoId == ammoId) n += Mathf.Max(1, s.Salvo);
            }
            return n;
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
                // magazine reserve), not the ship-wide inventory ; otherwise the salvo picker could
                // request rounds sitting behind an unusable launcher, and the strike fires short.
                int stock = Mathf.Min(kv.Value, LauncherFactsSource.AvailableRounds(ship, kv.Key));
                if (stock <= 0) continue;   // nothing physically firable: the row has no purpose
                // Second cap: a radio-command weapon that cannot group holds one fire-control
                // channel per round in flight, so the boat physically cannot put up more than it
                // has channels. Offering more cannot deliver them: the surplus launches, fails to
                // win a channel and self-destructs. See LauncherFactsSource.GuidanceChannelCap.
                //
                // Budget is per SHIP, not per target, so rounds this ship already has staged at
                // other targets come off the count offered here. Without that subtraction each
                // target was offered the full budget and a multi-target strike quietly ordered a
                // multiple of it. Coordinator.ClampToGuidanceChannels is the backstop that also
                // covers orders issued in the game's own interface; this only keeps the number the
                // panel shows honest while staging.
                int cap = LauncherFactsSource.GuidanceChannelCap(ship, kv.Key);
                int elsewhere = cap == int.MaxValue ? 0 : StagedElsewhere(ship, kv.Key);
                int left = cap == int.MaxValue ? int.MaxValue : Mathf.Max(0, cap - elsewhere);

                // Count may legitimately be 0 here, and the row is still emitted. Dropping it left
                // the ammunition simply missing from the list the moment its channels were spent at
                // another target, with nothing to say why; the row explains itself instead and
                // renders unselectable, the same way an out-of-range one does.
                yield return new Row
                {
                    AmmoId = kv.Key,
                    Count = Mathf.Min(stock, left),
                    InRange = inRange,
                    ChannelCap = cap,
                    ChannelsElsewhere = elsewhere,
                };
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

        /// <summary>
        /// Fire whatever the strike holds, from the hotkey. Same commit the panel's FIRE STRIKE
        /// button makes, so a strike built earlier can be launched with the panel hidden; a hidden
        /// panel is a normal way to play, and having to open one to press a button you already
        /// decided on is a delay the shot cannot always afford.
        /// </summary>
        private void FireStrikeNow()
        {
            int held = _strike.Count + Coordinator.StrikeCount;
            if (held == 0)
            {
                Bootstrap.Log.LogInfo("[AutoTOT] fire-strike pressed with nothing staged.");
                return;
            }
            Bootstrap.Log.LogInfo($"[AutoTOT] firing strike from hotkey: {held} order(s).");
            Coordinator.FireStrike(_strike);
            _strike.Clear();
        }

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
                    // Count 0 means every guidance channel is already staged at another target.
                    // Without this the row would stage a Salvo of 0, which the coordinator floors
                    // back up to 1 and orders a round the ship cannot guide.
                    if (!r.InRange || r.Count <= 0) continue;
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
        // Targets in the whole strike: the panel's staged rows first, then any target that only the
        // collected in-game orders name. Both halves fire together, so both belong in the list and
        // in the counts on the commit button.
        private void CollectStrikeTargets(List<ObjectBase> into)
        {
            into.Clear();
            foreach (Coordinator.Shot s in _strike)
                if (s.Target != null && !into.Contains(s.Target)) into.Add(s.Target);
            foreach (Coordinator.Shot s in CollectedOrders())
                if (s.Target != null && !into.Contains(s.Target)) into.Add(s.Target);
        }

        // Orders the coordinator has caught from the game's own interface, refreshed once a frame.
        private readonly List<Coordinator.Shot> _collected = new List<Coordinator.Shot>();
        private int _collectedFrame = -1;

        private List<Coordinator.Shot> CollectedOrders()
        {
            if (_collectedFrame != Time.frameCount)
            {
                _collectedFrame = Time.frameCount;
                Coordinator.CollectStrikeIntents(_collected);
            }
            return _collected;
        }

        private static string FormatTime(float sec)
        {
            if (sec <= 0f) return "0:00";
            int s = Mathf.RoundToInt(sec);
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
