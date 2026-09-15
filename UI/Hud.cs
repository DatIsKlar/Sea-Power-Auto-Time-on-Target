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
    /// Split across seven partial files:
    ///   Hud.cs          ; lifecycle, selection tracking, window layout
    ///   Hud.Rows.cs     ; shooter and missile-row data, per-frame row caches, selection prune
    ///   Hud.Render.cs   ; panel content rendering
    ///   Hud.Actions.cs  ; strike arm/fire, adding the selection to the strike or the roster
    ///   Hud.Labels.cs   ; fog-of-war target names, hotkey hints, time formatting
    ///   Hud.Mouse.cs    ; drag, resize and mouse-over-UI capture
    ///   Hud.Styles.cs   ; palette, textures, GUIStyle construction, shared-skin push/pop
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

#if LIVETEST
        // The live test build's hooks into the panel. The Workshop build has no runner to call them.
        /// <summary>The live panel, so an automated run can stage it. Set in OnEnable.</summary>
        private static Hud _instance;

        /// <summary>Set by StagePanelFor: hold the staged selection against TrackSelection, which
        /// otherwise reassigns both fields from the game's own selection every frame.</summary>
        private bool _stagedLock;

        /// <summary>
        /// Put the panel into the state a player would reach by clicking, so an automated run can
        /// photograph a label instead of asking someone to set it up. Picks the friendly shooter and
        /// the enemy target the panel would have taken from the game's selection, ticks one weapon
        /// row and sets its salvo. False if the panel is not up.
        ///
        /// <para>This exists because the live test runner drives the game through method calls and
        /// never touches the screen, so every warning the panel draws had to be verified by a person.
        /// Synthetic clicks into the
        /// Wine window were rejected for the reason the runner avoids them everywhere else: they
        /// depend on window focus, cursor position and compositor timing, none of which an
        /// unattended batch can rely on. The panel's state is code in this assembly, so setting it
        /// is both simpler and deterministic.</para>
        ///
        /// <para>It writes the same two fields <see cref="TrackSelection"/> writes, rather than the
        /// game's selection, because MainGameViewModel.SelectedObject is a
        /// ReadOnlyReactiveProperty. TrackSelection leaves both alone while nothing is selected,
        /// which is the state an automated run is in, so the staging holds.</para>
        /// </summary>
        internal static bool StagePanelFor(ObjectBase shooter, ObjectBase target, string ammoId, int salvo)
        {
            Hud h = _instance;
            if (h == null || shooter == null || target == null) return false;
            try
            {
                // The panel defaults to the collapsed tab, which draws a header and nothing else, so
                // a staged row would be photographed as an empty strip. Open it.
                h._open = true;
                h._stagedLock = true;
                h._anchor = shooter;
                h._target = target;
                h._targetVehicle = Globals._playerTaskforce?.PlottingTable?.VehicleForObject(target);
                string key = Key(shooter, ammoId);
                h._checked[key] = true;
                h._salvo[key] = Mathf.Max(1, salvo);
                return true;
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogWarning("[AutoTOT] StagePanelFor failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Stage a shot from more than one hull at one target, the way a player builds it: pick the
        /// first shooter, press + FORMATION (or + UNIT), then tick each weapon row and set its salvo.
        /// The roster is the only way the panel fires from several hulls (see GetShooters), so
        /// StagePanelFor alone would leave just one shooter. A shooter outside the anchor's formation
        /// is added the way + UNIT adds it. False if the panel is not up or the shots name more than
        /// one target, which the panel cannot hold.
        /// </summary>
        internal static bool StagePanelForShots(List<Coordinator.Shot> shots, bool wholeFormation)
        {
            Hud h = _instance;
            if (h == null || shots == null || shots.Count == 0) return false;
            Coordinator.Shot first = shots[0];
            if (!StagePanelFor(first.Unit, first.Target, first.AmmoId, first.Salvo)) return false;
            try
            {
                h._group.Clear();
                h.AddSelectionToGroup(wholeFormation);
                for (int i = 0; i < shots.Count; i++)
                {
                    Coordinator.Shot s = shots[i];
                    if (s.Unit == null) continue;
                    if (s.Target != first.Target)
                    {
                        Bootstrap.Log.LogWarning("[AutoTOT] StagePanelForShots: the panel holds one target, and these shots name two.");
                        return false;
                    }
                    if (!h._group.Contains(s.Unit)) h._group.Add(s.Unit);
                    string key = Key(s.Unit, s.AmmoId);
                    h._checked[key] = true;
                    h._salvo[key] = Mathf.Max(1, s.Salvo);
                }
                return true;
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogWarning("[AutoTOT] StagePanelForShots failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Press FIRE THIS TARGET the way a click does: the same shooter list, the same enable test
        /// DrawWindowInner passes to DrawCommitRow (a live target and a fireable ticked row) and the
        /// same FireSelected call. Called from the runner's Update, so like a real click it lands
        /// between OnGUI passes and cannot change state between one frame's Layout and Repaint.
        /// Returns null when it fired, otherwise the reason the button would have been disabled.
        /// </summary>
        internal static string PressFireThisTarget()
        {
            Hud h = _instance;
            if (h == null) return "the panel is not up";
            try
            {
                if (h._target == null || h._target.IsDestroyed) return "no live target";
                List<ObjectBase> shooters = h.GetShooters();
                if (!h.AnyFireable(shooters))
                    return "the button is disabled: no ticked row is in range with rounds ready";
                h.FireSelected(shooters, coordinated: true);
                return null;
            }
            catch (System.Exception e)
            {
                return "it threw: " + e.Message;
            }
        }
#endif

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
            // meant for the map. The switch
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
                Bootstrap.Log.LogInfo($"[AutoTOT] auto-coordination {(Coordinator.Active ? "ON" : "OFF")}");
            }
            if (modOk && Input.GetKeyDown(Bootstrap.StrikeArmKey)) ToggleStrikeArmed();
            if (modOk && Input.GetKeyDown(Bootstrap.FireStrikeKey)) FireStrikeNow();

            // A mission end clears the coordinator, and with it every unit the staged strike points
            // at. Checked here rather than in OnGUI so the list cannot be rendered stale for a frame.
            if (_strikeGeneration != Coordinator.ResetGeneration)
            {
                _strikeGeneration = Coordinator.ResetGeneration;
                _strike.Clear();
                _group.Clear();
                // The selection and the per-row picks describe the last mission's units as well.
                // Ticks and salvo counts persist across TARGETS on purpose; across a mission they
                // would attach to ships that no longer exist.
                _anchor = null;
                _target = null;
                _targetVehicle = null;
                _checked.Clear();
                _salvo.Clear();
                _rowFacts.Clear();
                // Unity reuses instance IDs after destruction, so a ship that threw while building
                // its rows in one mission could inherit the ID of a healthy ship in the next and be
                // silently dropped from the panel. The set exists only to stop one log line per
                // frame within a mission, so a mission boundary is the right place to forget it.
                _rowErrorLogged.Clear();
            }

            // Prune destroyed ships from the selections and the staged strike on a frame cadence.
            // Here and not in the draw: the draw path only runs with the panel expanded, so a
            // collapsed or hidden panel held destroyed ships for the rest of the mission, and when
            // it did run it counted OnGUI events, 2 to 4 per frame, not frames.
            if (++_pruneCounter >= SelectionPruneIntervalFrames)
            {
                _pruneCounter = 0;
                PruneCheckSalvo();
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
            // through the game's own order interface with the panel hidden.
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

        private void OnEnable()
        {
#if LIVETEST
            _instance = this;
#endif
            // The coordinator needs the panel's staged picks to police its own intake, and only the
            // panel knows them. Cleared in OnDisable so a torn-down HUD cannot be called into.
            Coordinator.StagedRoundsProvider = StagedRoundsFor;
        }

        private void OnDisable()
        {
#if LIVETEST
            if (ReferenceEquals(_instance, this)) _instance = null;
#endif
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
#if LIVETEST
            // An automated run has staged the panel deliberately; the game's own selection would
            // overwrite it on the very next frame and photograph the wrong ship.
            if (_stagedLock) return;
#endif

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
            _win.height = _open ? _expandedH
                                : CollapsedH + (_showHelp ? HelpOverlayHeight() : 0f);

            // The restore has to survive a throw. DrawWindow deliberately re-throws after logging,
            // and an escaping exception used to leave GUI.matrix scaled for the rest of the frame,
            // so every other IMGUI window in the game drew at this panel's scale.
            Matrix4x4 prevMatrix = GUI.matrix;
            try
            {
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
                _win = GUI.Window(WindowId, _win, DrawWindow, GUIContent.none, _winStyle);
            }
            finally
            {
                GUI.matrix = prevMatrix;
            }

            // Keep the window on-screen.
            _win.x = Mathf.Clamp(_win.x, -_win.width + OffscreenMargin, sw - OffscreenMargin);
            _win.y = Mathf.Clamp(_win.y, 0f, sh - CollapsedH);
        }

        private string _lastDrawError;

        private void DrawWindow(int id)
        {
            // Log-only guard. Both filter calls return false, so the exception is never caught here
            // and IMGUI sees it exactly as it would without the guard. Running them in the filter,
            // before the stack unwinds, restores the shared skin and logs which draw threw (full
            // stack, deduped by message so a per-frame throw does not flood the log).
            try
            {
                DrawWindowInner(id);
            }
            catch (System.Exception e) when (RestoreSharedSkin() || LogDrawError(e))
            {
                throw;
            }
        }

        private bool LogDrawError(System.Exception e)
        {
            if (e.Message != _lastDrawError)
            {
                _lastDrawError = e.Message;
                Bootstrap.Log.LogError($"[AutoTOT] HUD DrawWindow threw:\n{e}");
            }
            return false;
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
            DrawCommitRow(shooters, haveTarget && AnyChecked(shooters), haveTarget && AnyFireable(shooters));

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

    }
}
