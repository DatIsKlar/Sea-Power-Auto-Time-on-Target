using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) ; visual styling: the palette sampled from the game's own panels,
    /// texture helpers, and one-time GUIStyle construction. No layout or input logic here.
    /// </summary>
    internal sealed partial class Hud
    {
        // Palette sampled directly from the game's own panels (Screenshot 2026-08-23)
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

        private GUIStyle _winStyle, _title, _chev, _hdr, _row, _ship, _btn, _fire, _menuItem;
        // The four button roles the panel uses. _fire is primary (the single commit), _btnSecondary
        // is an outlined action that is real but not the headline, _btn is the flat ghost used for
        // in-list actions, and _btnDanger empties a list. One style per role, so a button's weight
        // reads its consequence.
        private GUIStyle _btnSecondary, _btnDanger;
        // Single-line variant of _row for the selection header. A long ship name in a wrapping
        // label grows the row taller than the layout reserved for it and the second line then
        // draws over the row above.
        private GUIStyle _rowOneLine;
        // Centred variants. The salvo count sits between two steppers and has to read as their
        // centre; the title-bar status labels have to sit on the same centre line as the button
        // between them, which a left-aligned label at a different font size does not.
        private GUIStyle _rowCenter, _hdrCenterV;
        // Condensed list line, for the per-order breakdown under a strike target.
        private GUIStyle _rowSmall;
        // In-list "remove". Same ghost role as _btn, at list-text size: it acts on one row and
        // should not out-weigh the row it acts on.
        private GUIStyle _btnSmall;
        // The title bar's help button: _btnSecondary sized to the full header height, with its
        // vertical padding dropped so the text centres in that box instead of riding high.
        private GUIStyle _btnHelp;
        // The salvo steppers, at body size so a row's -, count and missile name are one line of
        // text rather than a large control bolted onto a small label.
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
        /// Set every visual state of a button-shaped style at once.
        ///
        /// IMGUI keeps eight states (four, each with an `on` twin) and every style here sets all
        /// eight the same way: the `on` twin mirrors its plain state, and the text colour splits
        /// only between resting (normal, focused) and engaged (hover, active). Written out that was
        /// 16 assignment lines per style, four times over.
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

            // Scrollbar textures ; light gray thumb on dark track (Sea Power style)
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
                // Title-bar items carry no vertical margin. The default label margin made this
                // label claim HeaderH+8, so the whole bar grew and every fixed-height item in it
                // (the chevron, ? KEYS, AUTO) top-aligned instead of sharing a centre line.
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
            // Margin zeroed for the same reason as _btn: it sits between two steppers on a
            // fixed-height row and must not make that row taller than they are.
            _rowCenter  = new GUIStyle(_row) { alignment = TextAnchor.MiddleCenter,
                                               margin = new RectOffset(0, 0, 0, 0) };
            _rowSmall   = new GUIStyle(_row) { fontSize = FontSizeSmall, wordWrap = false,
                                               clipping = TextClipping.Clip,
                                               margin = new RectOffset(0, 0, 0, 0) };
            // Never wraps. These labels are drawn at a rect measured with CalcSize, and a fraction
            // of a pixel lost to the panel's scale matrix is enough to make a wrapping label break
            // "○ AUTO" onto two lines.
            _hdrCenterV = new GUIStyle(_hdr) { alignment = TextAnchor.MiddleCenter,
                                               wordWrap = false, clipping = TextClipping.Clip,
                                               margin = new RectOffset(0, 0, 0, 0) };

            _ship = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = FontSizeTitle };
            _ship.normal.textColor = TextMain;

            // Small square button for –/+ controls (centered, flat)
            _btn = new GUIStyle()
            {
                fontStyle = FontStyle.Bold, fontSize = FontSizeControl, alignment = TextAnchor.MiddleCenter,
                // No vertical margin. A 1px margin top and bottom makes a Height(RowHeight) button
                // claim RowHeight+2, which grows the row past every fixed-height item beside it;
                // those then top-align and the steppers sit a couple of pixels low.
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(1, 1, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            SetStates(_btn, _transparentTex, _btnHoverTex, _btnTex, _transparentTex, TextMain, TextMain);

            // Menu-item row style (for checkboxes and other interactive rows)
            // Flat text with hover highlight, checkmark prefix when active
            _menuItem = new GUIStyle()
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeBody, alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(8, 8, 2, 2),
            };
            SetStates(_menuItem, _transparentTex, _menuHoverTex, _menuHoverTex, _menuHoverTex,
                      TextMain, Color.white);
            
            // Scrollbar styles ; flat light gray thumb, dark track (Sea Power style).
            // These are LOCAL styles applied only around our own scroll view (see
            // DrawWindowInner), never to the shared GUI.skin.
            
            // Tracks and thumbs, vertical and horizontal: one flat texture, no border.
            FlatBar(_scrollTrack = new GUIStyle(GUI.skin.verticalScrollbar), _scrollTrackTex);
            FlatBar(_hScrollTrack = new GUIStyle(GUI.skin.horizontalScrollbar), _scrollTrackTex);
            FlatBar(_scrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb), _scrollThumbTex);
            FlatBar(_hScrollThumb = new GUIStyle(GUI.skin.horizontalScrollbarThumb), _scrollThumbTex);

            // NOTE: these styles are applied only around our own scroll view (see DrawWindowInner),
            // then restored ; we deliberately do NOT overwrite GUI.skin, which is process-global
            // and shared with the BepInEx console and every other IMGUI mod.

            // Fire button ; green background but flat, menu-item height
            _fire = new GUIStyle(_btn)
            {
                fontSize = FontSizeTitle,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 6, 6),
            };
            SetStates(_fire, _fireTex, _fireTex, _fireTex, _fireTex, Color.white, Color.white);

            // Secondary: a real action, outlined rather than filled. Framed() carries its border in
            // the texture, so unlike _btn this style needs a 1px GUIStyle border to stop the corners
            // being stretched away.
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

            _btnSmall = new GUIStyle(_btn) { fontStyle = FontStyle.Normal, fontSize = FontSizeBody };
            _btnHelp = new GUIStyle(_btnSecondary) { padding = new RectOffset(8, 8, 0, 0) };
            _btnStep = new GUIStyle(_btn) { fontSize = FontSizeBody };

            // Destructive: empties a list. Flat and dim at rest so it never competes with a commit
            // button, amber on hover so the consequence is stated before the click.
            _btnDanger = new GUIStyle(_btn)
            {
                fontStyle = FontStyle.Normal, fontSize = FontSizeSmall,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 4, 4),
            };
            SetStates(_btnDanger, _transparentTex, _menuHoverTex, _menuHoverTex, _transparentTex,
                      TextDim, Warn);
        }
    }
}
