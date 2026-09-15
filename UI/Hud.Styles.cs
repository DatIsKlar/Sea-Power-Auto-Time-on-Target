using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial): visual styling, meaning the palette sampled from the game's own panels, texture helpers
    /// and one-time GUIStyle construction. No layout or input logic.
    /// </summary>
    internal sealed partial class Hud
    {
        // Palette sampled from the game's own panels: body RGB(43,45,49), title bar RGB(30,31,34).
        private static readonly Color Panel     = new Color(0.169f, 0.176f, 0.192f, 0.97f);  // (43,45,49)
        private static readonly Color HeaderBg  = new Color(0.118f, 0.122f, 0.133f, 1f);     // (30,31,34)
        private static readonly Color Border    = new Color(0.235f, 0.247f, 0.267f, 1f);     // faint frame (~60)
        private static readonly Color BtnBg     = new Color(0.216f, 0.227f, 0.247f, 1f);     // (55,58,63)
        private static readonly Color BtnHover  = new Color(0.298f, 0.314f, 0.337f, 1f);     // (76,80,86)
        private static readonly Color TextMain  = new Color(0.78f, 0.80f, 0.82f, 1f);        // light gray
        // Functional colours from the game's own rich-text status hex tags (Seapower-Scripts).
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

        private GUIStyle _winStyle, _title, _chev, _hdr, _row, _ship, _btn, _fire, _menuItem;
        // _menuItem with the tick column reserved in its left padding, for rows that can be ticked.
        private GUIStyle _menuItemMarked;
        // The four button roles: _fire is the primary (the single commit), _btnSecondary an outlined
        // secondary action, _btn the flat ghost for in-list actions, _btnDanger empties a list. A button's
        // weight shows its consequence.
        private GUIStyle _btnSecondary, _btnDanger;
        // Single-line _row for the selection header: a wrapping ship name grows the row past its layout
        // height and draws over the row above.
        private GUIStyle _rowOneLine;
        // Centred variants: the salvo count must read as the centre of its steppers, and the title-bar status
        // labels share the centre line of the button between them.
        private GUIStyle _rowCenter, _hdrCenterV;
        // Condensed list line, for the per-order breakdown under a strike target.
        private GUIStyle _rowSmall;
        // The title bar's help button: _btnSecondary at full header height, vertical padding dropped so the
        // text centres.
        private GUIStyle _btnHelp;
        // The salvo steppers, at body size so "–", count and missile name read as one line of text.
        private GUIStyle _btnStep;
        private GUIStyle _scrollThumb, _scrollTrack, _hScrollThumb, _hScrollTrack;
        private Texture2D _panelTex, _headerTex, _fireTex, _btnTex, _btnHoverTex;
        private Texture2D _scrollThumbTex, _scrollTrackTex, _menuHoverTex, _transparentTex;
        private Texture2D _outlineTex, _outlineHoverTex;

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

        /// <summary>
        /// Set all eight IMGUI states of a button-shaped style at once: each `on` twin mirrors its plain
        /// state, and the text colour splits only between resting (normal, focused) and engaged (hover, active).
        /// </summary>
        private static void SetStates(GUIStyle st, Texture2D normal, Texture2D hover,
                                      Texture2D active, Texture2D focused,
                                      Color restText, Color engagedText)
        {
            st.normal.background = st.onNormal.background = normal;
            st.hover.background = st.onHover.background = hover;
            st.active.background = st.onActive.background = active;
            st.focused.background = st.onFocused.background = focused;
            st.normal.textColor = st.onNormal.textColor = restText;
            st.focused.textColor = st.onFocused.textColor = restText;
            st.hover.textColor = st.onHover.textColor = engagedText;
            st.active.textColor = st.onActive.textColor = engagedText;
        }

        /// <summary>One flat texture across a scrollbar part's three states, with no border.</summary>
        private static void FlatBar(GUIStyle st, Texture2D tex)
        {
            st.normal.background = st.hover.background = st.active.background = tex;
            st.border = new RectOffset(0, 0, 0, 0);
        }

        private void EnsureStyles()
        {
            if (_winStyle != null) return;

            _panelTex    = Framed(Panel, Border);
            _headerTex   = Solid(HeaderBg);
            _fireTex     = Solid(FireGreen);
            _btnTex      = Solid(BtnBg);
            _btnHoverTex = Solid(BtnHover);

            // Scrollbar textures: light grey thumb on a dark track, as in the game's panels.
            _scrollTrackTex = Solid(new Color(Panel.r, Panel.g, Panel.b, 0.5f));
            _scrollThumbTex = Solid(new Color(0.706f, 0.706f, 0.706f, 1f));
            
            // Menu-item hover highlight, a slightly lighter background.
            _menuHoverTex = Solid(MenuHover);
            
            // Transparent texture for "no background" states.
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
                // No vertical margin: the default label margin made this label claim HeaderH+8, growing the
                // bar so its fixed-height items (the chevron, ? KEYS, AUTO) top-aligned instead of sharing a
                // centre line.
                margin = new RectOffset(0, 0, 0, 0),
            };
            _title.normal.textColor = TextMain;

            _chev = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold, fontSize = FontSizeControl, alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 0, 0),
            };
            _chev.normal.textColor = TextDim;
            _chev.hover.textColor = TextMain;

            _hdr = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Normal, fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft };
            _hdr.normal.textColor = TextDim;

            _row = new GUIStyle(GUI.skin.label) { fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft };
            _row.normal.textColor = TextMain;

            _rowOneLine = new GUIStyle(_row) { wordWrap = false, clipping = TextClipping.Clip };
            // Margin zeroed as for _btn: between two steppers on a fixed-height row, it must not make the row taller.
            _rowCenter  = new GUIStyle(_row) { alignment = TextAnchor.MiddleCenter,
                                               margin = new RectOffset(0, 0, 0, 0) };
            _rowSmall   = new GUIStyle(_row) { fontSize = FontSizeSmall, wordWrap = false,
                                               clipping = TextClipping.Clip,
                                               margin = new RectOffset(0, 0, 0, 0) };
            // Never wraps: these labels are drawn at a CalcSize-measured rect, and a fraction of a pixel lost
            // to the scale matrix is enough to break "○ AUTO" onto two lines.
            _hdrCenterV = new GUIStyle(_hdr) { alignment = TextAnchor.MiddleCenter,
                                               wordWrap = false, clipping = TextClipping.Clip,
                                               margin = new RectOffset(0, 0, 0, 0) };

            _ship = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = FontSizeTitle };
            _ship.normal.textColor = TextMain;

            // Small square button for the – and + controls, centred and flat.
            _btn = new GUIStyle()
            {
                fontStyle = FontStyle.Bold, fontSize = FontSizeControl, alignment = TextAnchor.MiddleCenter,
                // No vertical margin: a 1px margin makes a Height(RowHeight) button claim RowHeight+2,
                // growing the row so the items beside it top-align.
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(1, 1, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            SetStates(_btn, _transparentTex, _btnHoverTex, _btnTex, _transparentTex, TextMain, TextMain);

            // Menu-item row style for checkboxes and other interactive rows: flat text with a hover highlight.
            _menuItem = new GUIStyle()
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(8, 8, 2, 2),
            };
            SetStates(_menuItem, _transparentTex, _menuHoverTex, _menuHoverTex, _menuHoverTex,
                      TextMain, Color.white);

            // A tickable row reserves the mark's width in its padding and the mark is drawn into that column;
            // a text prefix moved the label when ticked, since the glyph is wider and taller than the spaces
            // it replaced.
            _menuItemMarked = new GUIStyle(_menuItem)
            {
                padding = new RectOffset(_menuItem.padding.left + (int)MarkColumnW,
                                         _menuItem.padding.right,
                                         _menuItem.padding.top, _menuItem.padding.bottom),
            };
            
            // Scrollbar styles: flat light grey thumb on a dark track. Applied only around the panel's own
            // scroll views (see DrawWindowInner) and then restored, never written to the shared GUI.skin,
            // which the BepInEx console and every other IMGUI mod also use.
            
            // Tracks and thumbs, vertical and horizontal: one flat texture, no border.
            FlatBar(_scrollTrack = new GUIStyle(GUI.skin.verticalScrollbar), _scrollTrackTex);
            FlatBar(_hScrollTrack = new GUIStyle(GUI.skin.horizontalScrollbar), _scrollTrackTex);
            FlatBar(_scrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb), _scrollThumbTex);
            FlatBar(_hScrollThumb = new GUIStyle(GUI.skin.horizontalScrollbarThumb), _scrollThumbTex);

            // Fire button: green background, flat, menu-item height.
            _fire = new GUIStyle(_btn)
            {
                fontSize = FontSizeTitle,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 6, 6),
            };
            SetStates(_fire, _fireTex, _fireTex, _fireTex, _fireTex, Color.white, Color.white);

            // Secondary: a real action, outlined rather than filled. Framed() carries its border in the
            // texture, so this style needs a 1px GUIStyle border or the corners stretch away.
            _outlineTex      = Framed(new Color(0f, 0f, 0f, 0f), Border);
            _outlineHoverTex = Framed(BtnHover, Border);
            _btnSecondary = new GUIStyle(_btn)
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeSmall,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(8, 8, 6, 6),
            };
            SetStates(_btnSecondary, _outlineTex, _outlineHoverTex, _outlineHoverTex, _outlineTex,
                      TextMain, Color.white);

            _btnHelp = new GUIStyle(_btnSecondary) { padding = new RectOffset(8, 8, 0, 0) };
            // Normal weight, not _btn's bold, so "– 4 +" reads as one line of text around a plain count
            // label. The same style at any step size, so "–5" and "–10" are the same control, only wider.
            _btnStep = new GUIStyle(_btn) { fontStyle = FontStyle.Normal, fontSize = FontSizeBody };

            // Destructive: empties a list. Flat and dim at rest so it never competes with a commit button,
            // amber on hover.
            _btnDanger = new GUIStyle(_btn)
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeSmall,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 4, 4),
            };
            SetStates(_btnDanger, _transparentTex, _menuHoverTex, _menuHoverTex, _transparentTex,
                      TextDim, Warn);
        }

        // The shared skin's scrollbar styles, saved while the panel's own are pushed around its two scroll lists.
        private GUIStyle _prevVBar, _prevVThumb, _prevHBar, _prevHThumb;

        /// <summary>
        /// Put the shared skin back if a draw threw between the push and the pop, then decline the exception.
        /// An exception filter, so the restore runs before the stack unwinds; otherwise every other IMGUI
        /// window keeps this panel's scrollbars for the rest of the session.
        /// </summary>
        private bool RestoreSharedSkin()
        {
            PopScrollSkin();
            return false;
        }

        private void PushScrollSkin()
        {
            _prevVBar = GUI.skin.verticalScrollbar; _prevVThumb = GUI.skin.verticalScrollbarThumb;
            _prevHBar = GUI.skin.horizontalScrollbar; _prevHThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.verticalScrollbar = _scrollTrack;
            GUI.skin.verticalScrollbarThumb = _scrollThumb;
            GUI.skin.horizontalScrollbar = _hScrollTrack;
            GUI.skin.horizontalScrollbarThumb = _hScrollThumb;
        }

        /// <summary>
        /// Undo <see cref="PushScrollSkin"/>. The saved styles are cleared once restored, so
        /// <see cref="RestoreSharedSkin"/> acts only while a push is outstanding.
        /// </summary>
        private void PopScrollSkin()
        {
            if (_prevVBar == null) return;   // nothing pushed, or already restored
            GUI.skin.verticalScrollbar = _prevVBar;
            GUI.skin.verticalScrollbarThumb = _prevVThumb;
            GUI.skin.horizontalScrollbar = _prevHBar;
            GUI.skin.horizontalScrollbarThumb = _prevHThumb;
            _prevVBar = _prevVThumb = _prevHBar = _prevHThumb = null;
        }
}
}
