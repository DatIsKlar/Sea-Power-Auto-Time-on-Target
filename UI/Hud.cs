using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// On-screen planner. Tracks your last-selected friendly ship (the anchor) and enemy (the target), holds
    /// a shooter roster built one hull or one formation at a time, lists each shooter's missiles with live
    /// flight times, and fires a hand-picked set as a coordinated strike. While the cursor is over the panel
    /// it sets the game's MouseControlState to UI, so clicks do not reach the camera (Hud.Mouse.cs).
    ///
    /// Partial files:
    ///   Hud.cs          lifecycle, selection tracking, window layout
    ///   Hud.Rows.cs     shooter and missile-row data, per-frame row caches, selection prune
    ///   Hud.Render.cs   panel content rendering
    ///   Hud.Actions.cs  strike arm and fire, adding the selection to the strike or the roster
    ///   Hud.Labels.cs   fog-of-war target names, hotkey hints, time formatting
    ///   Hud.Mouse.cs    drag, resize and mouse-over-UI capture
    ///   Hud.Styles.cs   palette, textures, GUIStyle construction, shared-skin push and pop
    /// </summary>
    internal sealed partial class Hud : MonoBehaviour
    {
        private bool _visible = true; // fully shown; Alt+G hides the panel entirely (even the tab)
        private bool _open = false;   // when visible: expanded vs. collapsed tab; toggled by the ▸ chevron
        private ObjectBase _anchor;      // last selected friendly unit
        private ObjectBase _target;      // last selected enemy unit (real object, for firing)
        private Vehicle _targetVehicle;  // the enemy contact, for fog-of-war-correct display
        // ENGAGEMENTS sits below the planning controls and can be folded away while a strike is built.
        private bool _engagementsOpen = true;
        // The hotkey list, behind the ? in the title bar.
        private bool _showHelp;

#if LIVETEST
        // The live test build's hooks into the panel. The Workshop build has no runner to call them.
        /// <summary>The live panel, so an automated run can stage it. Set in OnEnable.</summary>
        private static Hud _instance;

        /// <summary>Set by StagePanelFor: keeps the staged selection from being overwritten by TrackSelection.</summary>
        private bool _stagedLock;

        /// <summary>
        /// Put the panel into the state a player reaches by clicking (shooter, target, one ticked row and its
        /// salvo), so an automated run can screenshot a label. Sets the fields <see cref="TrackSelection"/>
        /// writes, since the game's selection is read-only; TrackSelection leaves them alone while nothing is
        /// selected. False if the panel is not up.
        /// </summary>
        internal static bool StagePanelFor(ObjectBase shooter, ObjectBase target, string ammoId, int salvo)
        {
            Hud h = _instance;
            if (h == null || shooter == null || target == null) return false;
            try
            {
                // The collapsed tab draws only a header, so open the panel.
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
        /// Stage a shot from several hulls at one target, as a player builds it: first shooter, + FORMATION
        /// or + UNIT, then tick each row and set its salvo. False if the panel is not up or the shots name
        /// more than one target.
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
        /// Press FIRE THIS TARGET as a click does: the same shooter list, enable test and FireSelected call,
        /// made from the runner's Update so it lands between OnGUI passes. Null when it fired, otherwise the
        /// reason the button would be disabled.
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

        // The shooter roster: added shooters, kept across selection changes so it can be built before a
        // target is picked. Empty = follow the current selection.
        private readonly List<ObjectBase> _group = new List<ObjectBase>();

        // Shots staged for a multi-target strike; each carries its own target, so the panel stays a
        // single-target editor.
        private readonly List<Coordinator.Shot> _strike = new List<Coordinator.Shot>();
        // The coordinator reset the staged list was built against; a mission end invalidates it.
        private int _strikeGeneration;

        // Per-frame cache for EngageRows, which is otherwise computed several times per ship per OnGUI pass.
        private readonly Dictionary<int, List<Row>> _rowCache = new Dictionary<int, List<Row>>();
        private int _rowCacheFrame = -1;
        private int _pruneCounter;

        // Reused shooter list, so OnGUI does not allocate one per call.
        private readonly List<ObjectBase> _shootersCache = new List<ObjectBase>();

        private Vector2 _scroll;
        // The strike list scrolls on its own, with a share of the window height (StrikeListShare), so it
        // cannot push the shooter list off the panel.
        private Vector2 _strikeScroll;
        // Collapsible per-order breakdown under each strike target.
        private bool _strikeDetail = true;
        // Frame-based title-bar drag (Hud.Mouse.cs). DrawHeader publishes the button rects, so a press on the
        // chevron or ? KEYS is a click, not a drag.
        private bool _dragging;
        private Vector2 _dragOffset;
        private Rect _chevRectWin, _helpRectWin;
        // Vertical budget for the two lists, measured on Repaint and used the next frame, so one frame's
        // Layout and Repaint passes see the same numbers, as IMGUI requires.
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
        // Room for the key list under the collapsed title bar, measured from the text (HelpOverlayHeight).
        private const float HeaderH = 30f;

        // Window geometry.
        private const float DefaultWindowW = 540f, DefaultWindowH = 520f;
        // Enforced while resizing; MinWindowW must fit the three secondary commit buttons in a row.
        private const float MinWindowW = 480f, MinWindowH = 320f;
        private const float InitialTopMargin = 40f;                  // first-paint placement
        private const float InitialSideMargin = 8f;
        private const float OffscreenMargin = 60f;                   // px the window always keeps on screen
        private const float AutoScaleRefHeight = 1280f;
        // Named here so Bootstrap's config ranges and the SetUiScaleMultiplier clamp share one definition.
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

        // Only inside a running mission; in the main menu the planner neither draws nor takes mouse input.
        private static bool InMission() => Globals._mainGameViewModel != null;

        // Uniform UI scale: 0 in config = auto (1x at 1080p, about 2x at 2160p). The Update-path input
        // handlers (Hud.Mouse.cs) divide screen pixels by it to reach the panel's GUI space.
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

            // With the master switch off, Update must also skip the hotkeys and mouse capture, or an
            // invisible panel could arm a strike and swallow a map click. The switch is startup-only, so this
            // costs one bool per frame.
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

            // A mission end clears the coordinator and every unit the staged strike points at; checked here
            // rather than in OnGUI, so the list is never drawn stale.
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
                // Unity reuses instance IDs, so a ship that threw in one mission could hide a healthy ship in
                // the next; the set only dedupes log lines within a mission.
                _rowErrorLogged.Clear();
            }

            // Prune destroyed ships on a frame cadence here, not in the draw, which runs only with the panel expanded.
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

            // Nothing is painted with the indicator off, so nothing may capture the mouse; the hotkeys stay live.
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
            // The coordinator needs the panel's staged picks for its intake budget; cleared in OnDisable.
            Coordinator.StagedRoundsProvider = StagedRoundsFor;
        }

        private void OnDisable()
        {
#if LIVETEST
            if (ReferenceEquals(_instance, this)) _instance = null;
#endif
            Coordinator.StagedRoundsProvider = null;
            // On mission end the game HUD is already tearing down: reset our flag unconditionally and call
            // into the game only while a mission is live.
            _lastOverUi = false;
            if (InMission()) SetOverUi(false);
        }

        private void OnDestroy()
        {
            // Destroy every texture we created, or the rest leak.
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

            // Everything below is in scaled GUI space (see EffectiveScale); sw and sh are the screen in that space.
            float s = EffectiveScale();
            float sw = Screen.width / s, sh = Screen.height / s;

            // Keep the panel's on-screen top-left fixed when the scale changes, since the matrix scales about
            // the screen origin.
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

            // The matrix restore must survive a throw, or every other IMGUI window would draw at this panel's scale.
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
            // OnGUI fires several events per frame, so the per-frame caches reset once per frame.
            int frame = Time.frameCount;
            if (_rowCacheFrame != frame)
            {
                _rowCache.Clear();
                EngagementBoard.CollectSalvos(_salvos);   // live engagement snapshot (header + list)
                _rowCacheFrame = frame;
            }

            // Title strip from the window's top edge to the bottom of its layout row; DrawHeader centres on
            // the same rect.
            Rect headerRow = GUILayoutUtility.GetRect(0f, HeaderH, GUILayout.ExpandWidth(true));
            var strip = new Rect(1f, 1f, _win.width - 2f, headerRow.yMax - 1f);
            GUI.DrawTexture(strip, _headerTex);
            DrawHeader(strip);
            if (!_open)
            {
                // The key list stays toggleable while the panel is collapsed.
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

            // Split the leftover height between the shooter list and the strike list, both with explicit
            // heights, so neither starves the other.
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

            // The staged list sits directly above the button that commits it.
            DrawStagedStrike();

            // Everything from here down is the chrome the budget above has to leave room for.
            Rect belowProbe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            DrawCommitRow(shooters, haveTarget && AnyChecked(shooters), haveTarget && AnyFireable(shooters));

            // Live post-launch state, below everything that builds the strike.
            DrawEngagements();

            // The footer is reserved in the flow but painted at the window's bottom edge (DrawFooter).
            GUILayoutUtility.GetRect(0f, FooterStripH, GUILayout.ExpandWidth(true));

            Rect endProbe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                _belowListsH = Mathf.Max(0f, endProbe.y - belowProbe.y);

            DrawFooter();
            DrawResizeGrip();
        }

    }
}
