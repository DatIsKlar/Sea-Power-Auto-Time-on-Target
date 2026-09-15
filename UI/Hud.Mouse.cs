using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial): pointer handling, meaning window resize and the game's mouse-over-UI capture, so clicks
    /// and drags on the panel never reach the camera or world selection.
    /// </summary>
    internal sealed partial class Hud
    {
        private const float ResizeGripSize = 24f;   // hit area of the bottom-right resize grip
        private const float ResizeCursorInset = 8f; // cursor sits this far inside the edge while resizing
        private const float DragLatchMargin = 4f;   // px of slack when seeding the press-on-panel latch

        /// <summary>
        /// The cursor in the panel's GUI space: screen y flipped to GUI's top-down axis, then divided by the
        /// UI scale, since GUI.matrix scales the panel while Input reports screen pixels. Every Update-path
        /// pointer test uses this.
        /// </summary>
        private static Vector2 MouseInGui()
        {
            float s = EffectiveScale();
            return new Vector2(Input.mousePosition.x / s, (Screen.height - Input.mousePosition.y) / s);
        }

        // Frame-based resize from raw Input, since IMGUI drag events stop arriving once a fast cursor outruns
        // the window: grab the grip on mouse-down, then track the global cursor until release.
        private void HandleResizeInput()
        {
            if (!_open) { _resizing = false; return; }

            Vector2 m = MouseInGui();
            Rect grip = new Rect(_win.x + _win.width - ResizeGripSize, _win.y + _win.height - ResizeGripSize,
                                 ResizeGripSize, ResizeGripSize);

            if (Input.GetMouseButtonDown(0) && grip.Contains(m)) _resizing = true;
            if (!Input.GetMouseButton(0)) { _resizing = false; return; }

            if (_resizing)
            {
                _expandedW = Mathf.Max(MinWindowW, m.x - _win.x + ResizeCursorInset);
                _expandedH = Mathf.Max(MinWindowH, m.y - _win.y + ResizeCursorInset);
                _win.width = _expandedW;
                _win.height = _expandedH;
            }
        }

        // Frame-based drag too: GUI.DragWindow moves the panel only while OnGUI gets mouse events, which stop
        // while one of the game's menus is open, and the panel could then not be moved off what it covers.
        private void HandleDragInput()
        {
            if (_resizing) { _dragging = false; return; }

            Vector2 m = MouseInGui();

            if (Input.GetMouseButtonDown(0))
            {
                var bar = new Rect(_win.x, _win.y, _win.width, HeaderH);
                // Window-local, because that is the space the title bar's controls were laid out in.
                Vector2 local = m - new Vector2(_win.x, _win.y);
                bool onControl = _chevRectWin.Contains(local) || _helpRectWin.Contains(local);
                if (bar.Contains(m) && !onControl)
                {
                    _dragging = true;
                    _dragOffset = local;
                }
            }
            if (!Input.GetMouseButton(0)) { _dragging = false; return; }

            if (_dragging)
            {
                _win.x = m.x - _dragOffset.x;
                _win.y = m.y - _dragOffset.y;
            }
        }

        // Tell the game the mouse is over UI while the cursor is on the panel, latched through a held drag so
        // a fast move never briefly frees the camera.
        private void UpdateMouseCapture()
        {
            Vector2 m = MouseInGui();
            bool overNow = _win.Contains(m);

            // The latch is button-agnostic: left and right drags both move the camera (CameraBase
            // LeftMousePressed, FollowCamera RightMousePressed).
            bool anyDown = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            bool anyHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            if (anyDown)
            {
                // Seed the drag latch generously: a press on the panel had the cursor over it the frame
                // before, so honour _lastOverUi even if this frame's rect sample lags a fast entry. A few px
                // of margin covers an arrival with no prior hover frame.
                Rect grab = new Rect(_win.x - DragLatchMargin, _win.y - DragLatchMargin,
                                 _win.width + 2f * DragLatchMargin, _win.height + 2f * DragLatchMargin);
                _mouseDownOverUi = overNow || _lastOverUi || grab.Contains(m);
            }
            if (!anyHeld) _mouseDownOverUi = false;

            bool over = overNow || (anyHeld && _mouseDownOverUi) || _resizing || _dragging;

            // _isMouseOverUIWindow is one global flag the game also writes from its own edge-triggered UI
            // hit-testing (DM.cs, the test UIs), so it can be cleared mid-drag.
            if (over)
            {
                if (!_lastOverUi || anyDown)
                {
                    // Hover-enter, or a press while over: the full setter, which also fixes
                    // NoesisView.EnableMouse. Once per enter or click.
                    SetOverUi(true);
                }
                else if (anyHeld || _resizing)
                {
                    // Held-drag frames: pin the flag without the setter's per-frame FindObjectsByType,
                    // falling back to the setter if the private field cannot be resolved (renamed by a game update).
                    if (!PinOverUi()) SetOverUi(true);
                }
            }
            else if (_lastOverUi)
            {
                SetOverUi(false);   // release once when we leave; let the game manage it again
            }
        }

        // Cached reflection handle to MouseControlState._isMouseOverUIWindow, pinned every held-drag frame.
        private static System.Reflection.FieldInfo _fiOverUi;
        private static bool _fiLookedUp;

        // Force the game's over-UI flag true; it is the only field the camera gate reads
        // (MouseControlState.OnUpdate). False if the field cannot be resolved, so the caller can use the full setter.
        private static bool PinOverUi()
        {
            if (!Singleton<MouseControlState>.InstanceExists()) return true; // nothing to pin yet
            if (!_fiLookedUp)
            {
                _fiLookedUp = true;
                _fiOverUi = typeof(MouseControlState).GetField("_isMouseOverUIWindow",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_fiOverUi == null) return false;
            _fiOverUi.SetValue(Singleton<MouseControlState>.Instance, true);
            return true;
        }

        private void SetOverUi(bool over)
        {
            _lastOverUi = over;
            if (!Singleton<MouseControlState>.InstanceExists()) return;
            // InstanceExists can report true while the singleton is half torn down at mission end, so the
            // call can throw.
            try { Singleton<MouseControlState>.Instance.setMouseIsOverUIWindow(over); }
            // game state mid-teardown; nothing we can do, and nothing to leak
            catch { }
        }
    }
}
