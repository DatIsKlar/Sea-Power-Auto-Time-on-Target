using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) — pointer handling: window resize and the game's mouse-over-UI capture,
    /// so clicks/drags on the panel never leak into the camera or world selection.
    /// </summary>
    internal sealed partial class Hud
    {
        private const float ResizeGripSize = 24f;   // hit area of the bottom-right resize grip
        private const float ResizeCursorInset = 8f; // cursor sits this far inside the edge while resizing
        private const float DragLatchMargin = 4f;   // px of slack when seeding the press-on-panel latch

        // Frame-based resize: driven by raw Input every frame (not IMGUI drag events, which stop
        // being delivered to the window the moment a fast cursor outruns its rect). Grab the grip
        // on mouse-down, then track the global cursor until release — smooth at any speed.
        private void HandleResizeInput()
        {
            if (!_open) { _resizing = false; return; }

            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
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

        // Tell the game the mouse is over UI while the cursor is on our panel.
        // Latch through a held drag so fast window moves never briefly unblock the camera.
        private void UpdateMouseCapture()
        {
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overNow = _win.Contains(m);

            // Latch is button-agnostic: BOTH left and right drags rotate/pan the camera
            // (CameraBase LeftMousePressed, FollowCamera RightMousePressed), so a fast
            // right-drag over the panel must capture too.
            bool anyDown = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            bool anyHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            if (anyDown)
            {
                // Seed the held-drag latch generously: to press ON the panel the cursor was
                // over it the frame before, so honour _lastOverUi even if this frame's rect
                // sample glitches (a fast entry outruns the IMGUI-updated _win). A few px of
                // margin covers a same-frame arrival with no prior hover frame.
                Rect grab = new Rect(_win.x - DragLatchMargin, _win.y - DragLatchMargin,
                                 _win.width + 2f * DragLatchMargin, _win.height + 2f * DragLatchMargin);
                _mouseDownOverUi = overNow || _lastOverUi || grab.Contains(m);
            }
            if (!anyHeld) _mouseDownOverUi = false;

            bool over = overNow || (anyHeld && _mouseDownOverUi) || _resizing;

            // _isMouseOverUIWindow is a SINGLE global flag the game also writes from its own
            // edge-triggered UI hit-testing (DM.cs, the test UIs), so it can be cleared mid-drag.
            if (over)
            {
                if (!_lastOverUi || anyDown)
                {
                    // Hover-enter, or any mouse press while over: full setter (also fixes
                    // NoesisView.EnableMouse). Rare — once per enter / per click.
                    SetOverUi(true);
                }
                else if (anyHeld || _resizing)
                {
                    // The many held-drag frames: cheaply pin the flag true without the setter's
                    // per-frame FindObjectsByType. Fall back to the full setter only if the
                    // private field can't be resolved (e.g. renamed by a game update).
                    if (!PinOverUi()) SetOverUi(true);
                }
            }
            else if (_lastOverUi)
            {
                SetOverUi(false);   // release once when we leave; let the game manage it again
            }
        }

        // Cached reflection handle to MouseControlState._isMouseOverUIWindow so we can pin it
        // every held-drag frame WITHOUT the public setter's per-frame FindObjectsByType.
        private static System.Reflection.FieldInfo _fiOverUi;
        private static bool _fiLookedUp;

        // Cheaply force the game's over-UI flag true (only field the camera gate reads, see
        // MouseControlState.OnUpdate). Returns false if the field can't be resolved so the
        // caller can fall back to the full setter.
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
            // InstanceExists can still report true while the singleton's internals are half torn
            // down on mission end, so the game call itself can NRE — swallow it defensively.
            try { Singleton<MouseControlState>.Instance.setMouseIsOverUIWindow(over); }
            catch { /* game state mid-teardown; nothing we can do, and nothing to leak */ }
        }
    }
}
