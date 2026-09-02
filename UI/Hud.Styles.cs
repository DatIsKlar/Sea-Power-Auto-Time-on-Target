using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) — visual styling: the palette sampled from the game's own panels,
    /// texture helpers, and one-time GUIStyle construction. No layout or input logic here.
    /// </summary>
    internal sealed partial class Hud
    {
        // ---- Palette sampled directly from the game's own panels (Screenshot 2026-08-23) ----
        // Panel body RGB(43,45,49); title bar RGB(30,31,34) (darker).
        private static readonly Color Panel     = new Color(0.169f, 0.176f, 0.192f, 0.97f);  // (43,45,49)
        private static readonly Color HeaderBg  = new Color(0.118f, 0.122f, 0.133f, 1f);     // (30,31,34)
        private static readonly Color Border    = new Color(0.235f, 0.247f, 0.267f, 1f);     // faint frame (~60)
        private static readonly Color BtnBg     = new Color(0.216f, 0.227f, 0.247f, 1f);     // (55,58,63)
        private static readonly Color BtnHover  = new Color(0.298f, 0.314f, 0.337f, 1f);     // (76,80,86)
        private static readonly Color TextMain  = new Color(0.78f, 0.80f, 0.82f, 1f);        // light gray
        // Functional colors taken from the game's OWN rich-text status hex tags (Seapower-Scripts).
        private static readonly Color TextDim   = new Color(0.592f, 0.596f, 0.600f, 1f);     // #979899 unavailable/dim
        private static readonly Color Accent    = new Color(0.427f, 0.714f, 0.929f, 1f);     // #6db6ed info/friendly
        private static readonly Color OutOfRange = new Color(1.000f, 0.749f, 0.000f, 1f);    // #ffbf00 warning amber
        private static readonly Color TargetCol  = new Color(0.808f, 0.067f, 0.141f, 1f);    // #ce1124 hostile red
        private static readonly Color FireGreen  = new Color(0.110f, 0.612f, 0.243f, 1f);    // #1c9c3e available/go
        private static readonly Color Warn       = OutOfRange;                              // same amber, semantic alias
        private static readonly Color TargetMissing = new Color(0.85f, 0.45f, 0.45f, 1f);   // muted red, no target selected
        private static readonly Color MenuHover  = new Color(0.25f, 0.26f, 0.28f, 1f);      // subtle row highlight

        // Font sizes used by the styles below.
        private const int FontSizeBody = 14, FontSizeTitle = 15, FontSizeControl = 16, FontSizeSmall = 12;

        private GUIStyle _winStyle, _title, _chev, _hdr, _row, _ship, _btn, _fire, _fireNow, _menuItem;
        private GUIStyle _scrollThumb, _scrollTrack, _hScrollThumb, _hScrollTrack;
        private Texture2D _panelTex, _headerTex, _fireTex, _btnTex, _btnHoverTex;
        private Texture2D _scrollThumbTex, _scrollTrackTex, _menuHoverTex, _transparentTex;

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
            _scrollTrackTex = Solid(new Color(Panel.r, Panel.g, Panel.b, 0.5f));
            _scrollThumbTex = Solid(new Color(0.706f, 0.706f, 0.706f, 1f));
            
            // Menu-item hover highlight (subtle lighter background)
            _menuHoverTex = Solid(MenuHover);
            
            // Transparent texture for "no background" states
            _transparentTex = Solid(new Color(0, 0, 0, 0));

            _winStyle = new GUIStyle(GUI.skin.window);
            _winStyle.normal.background = _winStyle.onNormal.background = _panelTex;
            _winStyle.border = new RectOffset(1, 1, 1, 1);
            _winStyle.padding = new RectOffset(10, 10, 4, 10);
            _winStyle.margin = new RectOffset(0, 0, 0, 0);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeTitle, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 4, 0, 0),
            };
            _title.normal.textColor = TextMain;

            _chev = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold, fontSize = FontSizeControl, alignment = TextAnchor.MiddleCenter,
            };
            _chev.normal.textColor = TextDim;
            _chev.hover.textColor = TextMain;

            _hdr = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Normal, fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft };
            _hdr.normal.textColor = TextDim;

            _row = new GUIStyle(GUI.skin.label) { fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft };
            _row.normal.textColor = TextMain;

            _ship = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = FontSizeTitle };
            _ship.normal.textColor = TextMain;

            // Small square button for –/+ controls (centered, flat)
            _btn = new GUIStyle()
            {
                fontStyle = FontStyle.Bold, fontSize = FontSizeControl, alignment = TextAnchor.MiddleCenter,
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
                fontStyle = FontStyle.Normal, fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft,
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
            
            // Scrollbar styles — flat light gray thumb, dark track (Sea Power style).
            // These are LOCAL styles applied only around our own scroll view (see
            // DrawWindowInner), never to the shared GUI.skin.
            
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

            // NOTE: these styles are applied only around our own scroll view (see DrawWindowInner),
            // then restored — we deliberately do NOT overwrite GUI.skin, which is process-global
            // and shared with the BepInEx console and every other IMGUI mod.

            // Fire button — green background but flat, menu-item height
            _fire = new GUIStyle(_btn)
            {
                fontSize = FontSizeTitle,
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
                fontSize = FontSizeSmall,
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
