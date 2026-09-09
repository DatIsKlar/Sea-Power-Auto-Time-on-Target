using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) ; panel content rendering: header, missile rows, ENGAGEMENTS overview,
    /// fire actions, and small draw helpers. Data comes from the core partial; styling from
    /// the styles partial.
    /// </summary>
    internal sealed partial class Hud
    {
        private const float SelectionLabelW = 80f;   // TARGET / SHOOTERS label column
        private const float ClearButtonW = 70f;      // CLEAR, on both lists
        private const float AddUnitButtonW = 100f;   // + UNIT / ✓ HELD
        private const float AddFormationButtonW = 130f;  // + FORMATION / ✓ HELD
        private const float HelpButtonW = 76f;       // ? KEYS, in the title bar
        // Secondary commit-row buttons. They share a width so the row reads as one set of
        // alternatives to the primary above it, and three of them fit inside MinWindowW.
        private const float SecondaryButtonW = 122f;
        private const float SecondaryButtonH = 28f;
        // Salvo stepper. Both buttons and the count keep ONE width whatever the held modifier
        // prints on them, otherwise the whole group jumps sideways the moment Shift goes down.
        private const float StepButtonW = 42f, SalvoCountW = 42f;
        // The strike list gets at most this share of the window height, and never less than
        // StrikeListMinH. Expressing it as a fraction is what keeps the shooter list and the strike
        // list usable together at any window size; a fixed pixel height would starve one of them at
        // the extremes. It only claims the height its own content needs, so a one-order strike
        // stays a one-line list.
        private const float StrikeListShare = 0.5f;
        private const float StrikeListMinH = 2f * RowHeight;
        private const float StrikeOrderLineH = 18f;   // one condensed order line
        // Width of the tick column on a tickable row. The mark is drawn into it, never prefixed
        // to the label, so a row's text sits at the same place whether it is ticked or not.
        private const float MarkColumnW = 14f;

        // SHOOTERS then TARGET, in the order the player works: pick who shoots, then what at.
        private void DrawSelectionHeader(bool haveTarget)
        {
            // The name gets whatever the two buttons leave, on ONE line. Wrapping here pushed a
            // long ship name up into the row above.
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUILayout.Label("SELECTED", _hdr, GUILayout.Width(SelectionLabelW));
            GUILayout.Label(_anchor != null ? UnitNaming.SafeName(_anchor) : "click one of your units",
                            _rowOneLine, GUILayout.ExpandWidth(true));

            // Two buttons rather than one button and a mode. Each names the noun it adds, and each
            // says when its work is already done, so clicking a ship that is already held gives an
            // answer instead of looking like nothing happened.
            bool haveAnchor = _anchor != null && !_anchor.IsDestroyed;
            bool shipHeld = haveAnchor && _group.Contains(_anchor);
            GUI.enabled = haveAnchor && !shipHeld;
            if (GUILayout.Button(shipHeld ? "✓ HELD" : "+ UNIT", _btnSecondary,
                                 GUILayout.Width(AddUnitButtonW), GUILayout.Height(RowHeight)))
                AddSelectionToGroup(wholeFormation: false);

            bool haveFormation = haveAnchor && _anchor.Formation != null;
            bool formationHeld = haveFormation && FormationFullyHeld(_anchor);
            GUI.enabled = haveFormation && !formationHeld;
            if (GUILayout.Button(formationHeld ? "✓ HELD" : "+ FORMATION", _btnSecondary,
                                 GUILayout.Width(AddFormationButtonW), GUILayout.Height(RowHeight)))
                AddSelectionToGroup(wholeFormation: true);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // The shooter roster, when there is one. It replaces the current selection as the
            // shooter list, so say so plainly: otherwise clicking another ship and seeing the list
            // not change reads as the panel having stopped tracking the selection.
            if (_group.Count > 0)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
                GUILayout.Label($"SHOOTERS ({_group.Count})", _hdr, GUILayout.Width(SelectionLabelW + 40f));
                GUI.color = Accent;
                GUILayout.Label("from any formation; click a unit anywhere, then + UNIT",
                                _rowOneLine, GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                if (GUILayout.Button("CLEAR", _btnDanger, GUILayout.Width(ClearButtonW), GUILayout.Height(RowHeight)))
                    _group.Clear();
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUILayout.Label("TARGET", _hdr, GUILayout.Width(SelectionLabelW));
            GUI.color = haveTarget ? TargetCol : TargetMissing;
            GUILayout.Label(haveTarget ? TargetLabel() : "click an enemy contact to set target", _rowOneLine);
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// One line stating what the commit buttons will do, or why they are greyed. A disabled
        /// button that gives no reason is the panel refusing to say which of two preconditions is
        /// missing, and both are one click away from being met.
        /// </summary>
        private void DrawStatusLine(List<ObjectBase> shooters, bool haveTarget)
        {
            int ships = 0, rounds = 0;
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                {
                    if (!r.InRange || r.Count <= 0) continue;
                    string key = Key(ship, r.AmmoId);
                    if (!_checked.TryGetValue(key, out bool on) || !on) continue;
                    rounds += Mathf.Min(_salvo.TryGetValue(key, out int sv) ? sv : 1, r.Count);
                    ships++;
                }

            string msg;
            Color col;
            if (!haveTarget)      { msg = "select an enemy contact to fire"; col = TargetMissing; }
            else if (rounds == 0) { msg = "tick at least one missile"; col = Warn; }
            else
            {
                msg = $"{ships} order(s)  ·  {rounds} round(s) ready for {TargetLabel()}";
                col = Accent;
            }

            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUI.color = col;
            // Sized to its text, not expanded: an expanding label takes the whole row and leaves
            // the collecting notice parked wherever that label happens to end, instead of on the
            // panel's right edge where the flexible space below puts it.
            GUILayout.Label(msg, _rowOneLine, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            // Collecting in-game orders is a mode, not a hint, so it belongs on the status line
            // whenever it is on and nowhere at all when it is off.
            if (Coordinator.StrikeArmed)
            {
                GUI.color = Accent;
                GUILayout.Label($"● collecting your in-game orders too ({Coordinator.StrikeCount} held); {StrikeHint()} to stop",
                                _rowOneLine, GUILayout.ExpandWidth(false));
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
        }

        // The hotkey list, as rows rather than one run-on line: a key column and a description
        // column. Run together, the keys and the phrases they belong to wrapped into each other and
        // there was no telling which description went with which key.
        private static readonly string[] HelpDescriptions =
        {
            "hide the panel",
            "auto-coordinate normal group orders",
            "collect the orders you issue in-game into the strike",
            "fire the staged strike",
            "hold while stepping a salvo: +/- 10 (Shift) or 5 (Ctrl)",
            // Not a key. It earns a line here because it is the one title-bar light that reports
            // rounds the player will not get, and it is readable with the panel collapsed, which is
            // exactly when its meaning is least guessable.
            "a shooter is out of missile guidance channels; further orders for it are refused",
        };

        private static string[] HelpKeys() => new[]
        {
            HideHint(), ToggleHint(), StrikeHint(), FireHint(), "Shift / Ctrl", "● CH CAP",
        };

        private const float HelpKeyColumnW = 96f;   // widest configured combo plus air

        // The hotkey list, shown only while the ? in the title bar is toggled on.
        private void DrawHelpOverlay()
        {
            DrawDivider();
            GUILayout.Space(2f);
            string[] keys = HelpKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(HelpLineH));
                GUI.color = TextMain;
                GUILayout.Label(keys[i], _rowSmall, GUILayout.Width(HelpKeyColumnW));
                GUI.color = TextDim;
                GUILayout.Label(HelpDescriptions[i], _rowSmall, GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(2f);
            DrawDivider();
        }

        /// <summary>
        /// Height the overlay needs, dividers included. One row per key, so this is exact and does
        /// not depend on how the text happens to wrap.
        /// </summary>
        private float HelpOverlayHeight()
        {
            EnsureStyles();
            return HelpDescriptions.Length * HelpLineH + 2f * DividerH + 8f;
        }

        private const float HelpLineH = 20f;

        private const float DividerH = 3f;

        /// <summary>
        /// The title bar ; always visible, draggable, carries the collapse toggle and auto status.
        ///
        /// Drawn at explicit rects rather than with auto-layout. The painted strip runs to the
        /// window's top edge, so it is taller than the layout row that sits inside the window
        /// padding, and anything centred in that row is centred on the wrong box. Every item here
        /// is instead centred on the strip's own mid-line, which is what the eye measures against.
        /// </summary>
        private void DrawHeader(Rect strip)
        {
            // One mid-line for the whole bar. Each item gets a full-height rect on it, and the
            // styles' Middle* alignment then centres the glyphs inside those rects.
            float y = strip.center.y - HeaderH * 0.5f;
            float left = strip.x + 6f, right = strip.xMax - 10f;

            var chevRect = new Rect(left, y, 26f, HeaderH);
            _chevRectWin = chevRect;
            if (GUI.Button(chevRect, _open ? "\u25be" : "\u25b8", _chev)) _open = !_open;

            // AUTO, and the live engagement count when there is one, are measured and laid out from
            // the right edge inwards so neither depends on how wide the title happens to be.
            string autoText = Coordinator.Active ? "\u25cf AUTO" : "\u25cb AUTO";
            float autoW = _hdrCenterV.CalcSize(new GUIContent(autoText)).x + 2f;
            var autoRect = new Rect(right - autoW, y, autoW, HeaderH);
            GUI.color = Coordinator.Active ? Accent : TextDim;
            GUI.Label(autoRect, autoText, _hdrCenterV);
            GUI.color = Color.white;
            right = autoRect.x - 8f;

            // Strike state, in the same shape as AUTO and next to it. Collecting orders is a mode
            // the player can leave running with the panel collapsed, and a mode with no indicator
            // is a mode you forget you are in.
            int staged = _strike.Count + Coordinator.StrikeCount;
            if (Coordinator.StrikeArmed || staged > 0)
            {
                string strikeText = Coordinator.StrikeArmed
                    ? $"\u25cf STRIKE {staged}"
                    : $"\u25cb STRIKE {staged}";
                float strikeW = _hdrCenterV.CalcSize(new GUIContent(strikeText)).x + 2f;
                GUI.color = Coordinator.StrikeArmed ? Warn : TextDim;
                GUI.Label(new Rect(right - strikeW, y, strikeW, HeaderH), strikeText, _hdrCenterV);
                GUI.color = Color.white;
                right -= strikeW + 8f;
            }

            // Guidance-channel trim, in the same shape as the two above. This is the one status here
            // that reports rounds the player asked for and did NOT get, and the paths that trigger it
            // (auto-coordination and Alt+H collection) are exactly the ones a player uses without
            // ever opening the panel, so a panel-only warning would never reach them.
            if (Coordinator.AnyChannelCapped || AnyStagedChannelCap())
            {
                const string capText = "● CH CAP";
                float capW = _hdrCenterV.CalcSize(new GUIContent(capText)).x + 2f;
                GUI.color = Warn;
                GUI.Label(new Rect(right - capW, y, capW, HeaderH), capText, _hdrCenterV);
                GUI.color = Color.white;
                right -= capW + 8f;
            }

            int rounds = 0, tgts = _salvos.Count;
            foreach (var e in _salvos) rounds += e.Queued + e.InFlight;
            if (tgts > 0)
            {
                string live = $"\u25cf {tgts} tgt / {rounds} msl";
                float liveW = _hdrCenterV.CalcSize(new GUIContent(live)).x + 2f;
                GUI.color = TargetCol;
                GUI.Label(new Rect(right - liveW, y, liveW, HeaderH), live, _hdrCenterV);
                GUI.color = Color.white;
                right -= liveW + 8f;
            }

            // The hotkeys live behind this, so they are findable without being repeated on a hint
            // line every frame. Outlined rather than bare: a glyph drawn in a label style reads as
            // part of the title, not as something to press. Centred in the bar, then pushed left if
            // a long status on the right would otherwise overlap it.
            float helpX = Mathf.Min(strip.center.x - HelpButtonW * 0.5f, right - HelpButtonW);
            var helpRect = new Rect(helpX, y + 2f, HelpButtonW, HeaderH - 4f);
            _helpRectWin = helpRect;
            GUI.color = _showHelp ? Accent : Color.white;
            if (GUI.Button(helpRect, "? KEYS", _btnHelp)) _showHelp = !_showHelp;
            GUI.color = Color.white;

            // The title takes what is left between the chevron and whatever is nearest on its right.
            float titleX = chevRect.xMax + 4f;
            GUI.Label(new Rect(titleX, y, Mathf.Max(0f, helpRect.x - 8f - titleX), HeaderH),
                      "TIME-ON-TARGET", _title);
        }

        // Held modifier grows the salvo step so large launchers fill faster.
        // Read from Event.current (live during OnGUI). Result is still clamped to the row range.
        private static int SalvoStep()
        {
            var e = Event.current;
            if (e != null && e.shift)   return 10;
            if (e != null && e.control) return 5;
            return 1;
        }

        private void DrawMissileRow(ObjectBase ship, Row r)
        {
            string key = Key(ship, r.AmmoId);
            if (!_checked.ContainsKey(key)) _checked[key] = true;
            if (!_salvo.ContainsKey(key)) _salvo[key] = 1;

            bool haveTarget = _target != null && !_target.IsDestroyed;
            GUILayout.BeginHorizontal();

            string eta = "--", range = "";
            if (haveTarget)
            {
                // Out-of-range rows print "(out of range)" instead of the ETA below, so simulating
                // one is wasted work -- and it is the worst kind, because an unreachable shot is
                // exactly where the integrator bails and the call then also pays for WaypointSim.
                if (r.InRange)
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.UiEstimate);
                    float etaSec = FlightTime.EstimateForDisplay(ship, r.AmmoId, _target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.UiEstimate);
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.UiEstimateCalls);
                    eta = FormatTime(etaSec);
                }
                float nm = GameUnits.NmBetween(ship, _target);
                range = $"{nm:0.0}nm";
            }

            // A row with no channels left is shown but cannot be picked, exactly like an
            // out-of-range one. Both are "visible so it can explain itself, inert so it cannot be
            // staged".
            bool selectable = r.InRange && r.Count > 0;

            GUI.enabled = selectable;
            
            // Clickable missile name, with the tick in its own column (like Sea Power menu items).
            bool isChecked = _checked[key] && selectable;

            GUI.color = selectable ? TextMain : OutOfRange;
            if (GUILayout.Button($"{r.AmmoId}  x{r.Count}", _menuItemMarked, GUILayout.Height(RowHeight)))
                _checked[key] = !isChecked;
            if (isChecked)
            {
                // Drawn over the row's own rect rather than laid out, so the mark costs the row no
                // width and toggling it cannot move anything.
                Rect nameRect = GUILayoutUtility.GetLastRect();
                GUI.Label(new Rect(nameRect.x + _menuItem.padding.left, nameRect.y,
                                   MarkColumnW, nameRect.height), "\u2713", _rowCenter);
            }
            GUI.color = Color.white;
            
            // ETA and range labels
            GUI.color = selectable ? Accent : OutOfRange;
            GUILayout.Label($"ETA {eta}", _row, GUILayout.Width(95));
            GUI.color = selectable ? TextMain : OutOfRange;
            string why = !r.InRange ? " (out of range)"
                       : r.Count <= 0 ? " (no channels left)"
                       : "";
            GUILayout.Label(range + why, _row, GUILayout.Width(170));
            GUI.color = Color.white;
            
            GUILayout.FlexibleSpace();
            // The steppers state their own step size while a modifier is held, which is the moment
            // it matters. The full key list lives behind the ? in the title bar.
            int step = SalvoStep();
            string minus = step > 1 ? $"–{step}" : "–", plus = step > 1 ? $"+{step}" : "+";
            if (GUILayout.Button(minus, _btnStep, GUILayout.Width(StepButtonW), GUILayout.Height(RowHeight)))
                _salvo[key] = Mathf.Max(1, _salvo[key] - step);
            GUILayout.Label($"{_salvo[key]}", _rowCenter, GUILayout.Width(SalvoCountW), GUILayout.Height(RowHeight));
            if (GUILayout.Button(plus, _btnStep, GUILayout.Width(StepButtonW), GUILayout.Height(RowHeight)))
                _salvo[key] = Mathf.Min(r.Count, _salvo[key] + step);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Warn when the chosen salvo can't fire before the launcher must reload.
            // The strike still fires as one order (the game paces the reload); it just arrives in
            // waves, which the ENGAGEMENTS overview then shows split out.
            if (r.InRange && _checked[key] &&
                LauncherFactsSource.WillNeedReload(ship, r.AmmoId, _salvo[key], out int ready, out int waves))
            {
                GUI.color = Warn;
                GUILayout.Label($"     ⚠ needs reload : {ready} ready, fires in {waves} waves", _row);
                GUI.color = Color.white;
            }

            // Shared missile group. Grouped ammo is not limited by fire-control channels but by the
            // group itself, and shooters inside the ammo's join range feed ONE group between them.
            // Overfill it and the surplus rounds never leave the rails, so this is the one warning
            // the player can act on by repositioning rather than by firing less.
            if (r.InRange && _checked[key] && _groupShare.TryGetValue(key, out GroupShare gs))
            {
                GUI.color = Warn;
                GUILayout.Label(
                    $"     ⚠ shared group : {gs.Shooters} shooters within {gs.JoinNm:0} nm staged " +
                    $"{gs.Combined}, group holds {gs.Cap} : {gs.Combined - gs.Cap} will not launch", _row);
                GUILayout.Label(
                    $"        separate them by more than {gs.JoinNm:0} nm and each fires a full salvo", _row);
                GUI.color = Color.white;
            }

            // Fire-control channel limit. A radio-command round that cannot join a group holds one
            // channel for its whole flight, so the salvo picker clamps the row to what the boat can
            // guide at once (Hud.cs, CachedEngageRows). A limit the player is nowhere near is noise
            // on every row of every ship, so this fires only when the player is AT the cap.
            //
            // It used to test `_salvo[key] > cap`, which is unreachable: the + button clamps to
            // r.Count and r.Count is already Min(available, cap), so the salvo can never exceed the
            // cap and this warning never once appeared. The slider simply stopped at the cap and
            // said nothing. `>=` is the condition that makes a binding limit visible, which is the
            // whole point of the line.
            // Outside the `_checked` block below, deliberately. Committing a row clears its tick,
            // so gating on it hid this warning from the staged strike it actually applies to, which
            // is the moment the player most needs it.
            //
            // Kept to one short clause. This fires whenever the player is at the limit, which on a
            // 4-channel ship is most of the time, so a long explanation becomes wallpaper and stops
            // being read at all.
            if (r.ChannelCap < int.MaxValue)
            {
                int cap = r.ChannelCap;
                bool trimmed = Coordinator.IsChannelTrimmed(ship, r.AmmoId);
                bool full = Coordinator.IsChannelCapped(ship, r.AmmoId);
                int want = _salvo.TryGetValue(key, out int sv) ? sv : 0;
                bool atCap = _checked[key] && r.InRange && want >= r.Count && r.Count > 0;

                // Ordered by how much the player loses, worst first. Only the trimmed case says
                // rounds were dropped; a ship merely sitting at its limit has lost nothing yet.
                string note = null;
                if (trimmed)                      note = $"over {cap} guidance channels : the extra rounds will not fire";
                else if (r.Count <= 0)            note = $"all {cap} guidance channels used at other targets";
                else if (full)                    note = $"all {cap} guidance channels in use : further orders are refused";
                else if (r.ChannelsElsewhere > 0) note = $"only {cap} guidance channels, {r.ChannelsElsewhere} used elsewhere";
                else if (atCap)                   note = $"only {cap} guidance channels";

                if (note != null)
                {
                    GUI.color = Warn;
                    GUILayout.Label($"     ⚠ {note}", _row);
                    GUI.color = Color.white;
                }
            }

            if (r.InRange && _checked[key])
            {
                // Beam-only launcher. The ship steers to put the target within 3 degrees of exactly
                // abeam, not merely inside the launcher's arc, so it keeps manoeuvring after it
                // could already shoot and its salvo comes out in bursts around the turns. Nothing
                // this mod schedules changes that, and how far the salvo splits depends on the
                // player's time compression, so the warning states the effect and no number.
                if (LauncherFactsSource.WillManoeuvreToFire(ship, r.AmmoId, out float windowDeg))
                {
                    GUI.color = Warn;
                    GUILayout.Label($"     ⚠ manoeuvres to fire : launcher bears only abeam, ship steers into a " +
                                    $"{windowDeg:0}° window at 90° : salvo may split across turns", _row);
                    GUI.color = Color.white;
                }

                // Launcher contention. The strike path makes this easy to do by accident: two
                // targets picked off the same box launcher cannot leave together, whatever this mod
                // schedules, because the game services those engage tasks one at a time.
                if (Coordinator.IsContended(ship, r.AmmoId))
                {
                    GUI.color = Warn;
                    GUILayout.Label("     ⚠ launcher busy at another target : these rounds leave serially, " +
                                    "impacts will not sync", _row);
                    GUI.color = Color.white;
                }

                // The Run A deadlock: the launcher raises a submerged boat only to the weapon's own
                // depth ceiling, which can still be below the depth its guidance radar mast needs.
                // The boat then sits there and never fires. We warn rather than command a depth,
                // because surfacing a submarine is the player's call, not the mod's.
                if (LauncherFactsSource.GuidanceSensorUnavailable(ship, r.AmmoId))
                {
                    GUI.color = Warn;
                    GUILayout.Label("     ⚠ guidance radar unavailable : come to periscope depth or this order will not fire", _row);
                    GUI.color = Color.white;
                }

                // A submerged boat's order-to-first-round delay is ESTIMATED, not measured, and the
                // observed ascent rates span 3x. Where the boat ends up as the batch anchor the
                // error is absorbed (its real launch rewrites the shared impact); where it ends up a
                // follower it is not, and lands on the arrival time. The role is not known until
                // commit, so warn on the condition the picker can see: the delay is nonzero. At
                // launch depth it is zero and the timing is exact.
                float envelopeDelay = LaunchEnvelope.TimeToReady(ship, r.AmmoId);
                if (envelopeDelay > 0f)
                {
                    // The same delay covers a boat that must rise and an aircraft that must descend,
                    // so the text has to name the platform. It said "submerged" for both, which told
                    // a pilot their aircraft was under water.
                    bool air = ship is Aircraft || ship is Helicopter;
                    GUI.color = Warn;
                    GUILayout.Label(air
                        ? $"     ⚠ outside launch altitude : ~{envelopeDelay:0}s to descend into the " +
                          "launch band, timing estimated; fly in the band for exact coordination"
                        : $"     ⚠ submerged : ~{envelopeDelay:0}s to reach launch depth, timing " +
                          "estimated; come to launch depth for exact coordination", _row);
                    GUI.color = Color.white;
                }
            }
        }

        // Live overview of every salvo we're coordinating: target + rounds queued / in flight,
        // and the synced impact countdown for shots still being held.
        private void DrawEngagements()
        {
            DrawDivider();
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            if (GUILayout.Button(_engagementsOpen ? "▾" : "▸", _chev, GUILayout.Width(20), GUILayout.Height(RowHeight)))
                _engagementsOpen = !_engagementsOpen;
            GUILayout.Label(_salvos.Count > 0 ? $"ENGAGEMENTS ({_salvos.Count})" : "ENGAGEMENTS", _hdr);
            GUILayout.EndHorizontal();
            if (!_engagementsOpen) return;

            if (_salvos.Count == 0)
            {
                GUI.color = TextDim;
                GUILayout.Label("   none in progress", _row);
                GUI.color = Color.white;
                return;
            }

            float now = GameClock.SimNow();
            foreach (EngagementBoard.SalvoLine e in _salvos)
            {
                GUILayout.BeginHorizontal();
                GUI.color = TargetCol;
                // Rows sharing a strike id were scheduled together and land together; say so, or a
                // multi-target strike reads as several engagements that coincide by accident.
                string label = FoggedLabel(e.Target);
                if (e.StrikeId > 0 && SharesStrike(e.StrikeId)) label = "◆ " + label;
                GUILayout.Label(label, _row, GUILayout.Width(210));

                GUI.color = TextMain;
                string status = "";
                if (e.Queued > 0) status += $"{e.Queued} queued";
                if (e.InFlight > 0) status += (status.Length > 0 ? "  ·  " : "") + $"{e.InFlight} in flight";
                if (e.AnchorTotal > 0) status += (status.Length > 0 ? "  ·  " : "") + $"anchoring {e.AnchorLaunched}/{e.AnchorTotal}";
                GUILayout.Label(status, _row, GUILayout.Width(170));

                GUILayout.FlexibleSpace();
                if (e.ImpactSim > 0f)
                {
                    GUI.color = Accent;
                    if (e.Waves > 1 && e.WaveGap > 0f)
                    {
                        // Reload-separated waves: show each wave's arrival (wave k = base + k*gap).
                        string arrival = $"wave 1 {FormatTime(Mathf.Max(0f, e.ImpactSim - now))}";
                        arrival += $"  ·  wave {e.Waves} {FormatTime(Mathf.Max(0f, e.ImpactSim + (e.Waves - 1) * e.WaveGap - now))}";
                        GUILayout.Label(arrival, _row);
                    }
                    else
                    {
                        string arrival = $"arrival {FormatTime(Mathf.Max(0f, e.ImpactSim - now))}";
                        if (e.ImpactSpread > MinSpreadToDisplay)
                            arrival += $" ±{e.ImpactSpread:0.0}s";
                        GUILayout.Label(arrival, _row);
                    }
                }
                else if (e.InFlight > 0)
                {
                    GUI.color = TextDim;
                    GUILayout.Label("inbound", _row);
                }
                GUI.color = Color.white;
                GUILayout.EndHorizontal();
            }
        }

        // One staged shooter: its name and every missile it fires at this target, small and dim so
        // a long strike stays scannable. Collected orders carry a marker naming where they came from.
        private void DrawStrikeOrderLine(ObjectBase unit, string weapons, bool fromGame)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(StrikeOrderLineH));
            GUI.color = TextDim;
            GUILayout.Space(16f);
            GUILayout.Label($"{UnitNaming.SafeName(unit)}  ·  {weapons}",
                            _rowSmall, GUILayout.ExpandWidth(false));
            if (fromGame)
            {
                GUI.color = Accent;
                GUILayout.Label("  in-game order", _rowSmall, GUILayout.ExpandWidth(false));
            }
            GUI.color = Color.white;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // Reused by the order-line grouping below and by the height it asks for, so both always
        // count the same lines. Never held across frames.
        private readonly List<ObjectBase> _orderShooterScratch = new List<ObjectBase>();
        private readonly System.Text.StringBuilder _orderLine = new System.Text.StringBuilder();

        /// <summary>
        /// The per-order breakdown under one target: ONE line per shooter, listing every missile
        /// that ship fires at it, rather than one line per missile. A ship with three weapon types
        /// staged used to cost three lines, which pushed the rest of the strike out of view for no
        /// extra information. Staged rows and in-game orders are grouped separately, so the
        /// "in-game order" marker on a line is true of everything on that line.
        /// </summary>
        private void DrawStrikeOrderLines(ObjectBase target, List<Coordinator.Shot> collected)
        {
            DrawShooterGroup(_strike, target, fromGame: false);
            DrawShooterGroup(collected, target, fromGame: true);
        }

        private void DrawShooterGroup(List<Coordinator.Shot> shots, ObjectBase target, bool fromGame)
        {
            _orderShooterScratch.Clear();
            foreach (Coordinator.Shot sh in shots)
                if (sh.Target == target && !_orderShooterScratch.Contains(sh.Unit))
                    _orderShooterScratch.Add(sh.Unit);

            foreach (ObjectBase unit in _orderShooterScratch)
            {
                _orderLine.Length = 0;
                foreach (Coordinator.Shot sh in shots)
                {
                    if (sh.Target != target || sh.Unit != unit) continue;
                    if (_orderLine.Length > 0) _orderLine.Append(",  ");
                    _orderLine.Append(sh.AmmoId).Append(" x").Append(Mathf.Max(1, sh.Salvo));
                }
                DrawStrikeOrderLine(unit, _orderLine.ToString(), fromGame);
            }
        }

        /// <summary>
        /// How many order lines the breakdown will draw: one per shooter per target, counted the
        /// same way DrawShooterGroup groups them, so the height reserved matches what is drawn.
        /// </summary>
        private int StrikeOrderLineCount(List<Coordinator.Shot> collected)
        {
            int lines = 0;
            foreach (ObjectBase t in _strikeTargetScratch)
            {
                lines += DistinctShooters(_strike, t);
                lines += DistinctShooters(collected, t);
            }
            return lines;
        }

        private int DistinctShooters(List<Coordinator.Shot> shots, ObjectBase target)
        {
            _orderShooterScratch.Clear();
            foreach (Coordinator.Shot sh in shots)
                if (sh.Target == target && !_orderShooterScratch.Contains(sh.Unit))
                    _orderShooterScratch.Add(sh.Unit);
            return _orderShooterScratch.Count;
        }

        /// <summary>True if another listed engagement shares this strike id.</summary>
        private bool SharesStrike(int strikeId)
        {
            int n = 0;
            foreach (EngagementBoard.SalvoLine e in _salvos)
                if (e.StrikeId == strikeId && ++n > 1) return true;
            return false;
        }

        private readonly List<ObjectBase> _strikeTargetScratch = new List<ObjectBase>();

        /// <summary>
        /// Height the whole strike section will occupy: its header row plus the scrolling list.
        /// The shooter list above is sized as the rest of the budget, so this is what keeps the two
        /// in proportion at any window size.
        ///
        /// The list asks only for the height its CURRENT contents need, so collapsing the breakdown
        /// hands the slack to the shooter list above rather than leaving an empty box. The commit
        /// row below stays put either way, because the two lists share one fixed budget: the strike
        /// section is anchored to the bottom of that budget and grows upward into it.
        /// </summary>
        private float StrikeSectionHeight()
        {
            if (_strike.Count == 0 && !Coordinator.StrikeArmed) return 0f;
            CollectStrikeTargets(_strikeTargetScratch);
            List<Coordinator.Shot> collected = CollectedOrders();
            int totalOrders = _strike.Count + collected.Count;
            if (totalOrders == 0) return RowHeight * 2f;   // header plus the "nothing staged" line

            // The breakdown draws a line per shooter, not per order, so it is counted that way too.
            float wanted = _strikeTargetScratch.Count * RowHeight +
                           (_strikeDetail ? StrikeOrderLineCount(collected) * StrikeOrderLineH : 0f) + 4f;
            return RowHeight + Mathf.Min(wanted, Mathf.Max(StrikeListMinH, _listsAvailH * StrikeListShare));
        }

        // The staged multi-target strike: one group per target, each removable, directly above the
        // commit row that fires them. The list and the button that acts on it belong together.
        private void DrawStagedStrike()
        {
            bool armed = Coordinator.StrikeArmed;
            if (_strike.Count == 0 && !armed) return;

            CollectStrikeTargets(_strikeTargetScratch);
            List<Coordinator.Shot> collected = CollectedOrders();
            int totalOrders = _strike.Count + collected.Count;

            DrawDivider();
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            // The chevron collapses the per-order breakdown, not the list: the target lines and
            // their counts stay, so the strike is still readable at a glance while collapsed.
            if (GUILayout.Button(_strikeDetail ? "▾" : "▸", _chev,
                                 GUILayout.Width(20), GUILayout.Height(RowHeight)))
                _strikeDetail = !_strikeDetail;
            GUILayout.Label(totalOrders > 0
                ? $"STRIKE  ·  {_strikeTargetScratch.Count} target(s), {totalOrders} order(s)"
                : "STRIKE", _hdr);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("CLEAR", _btnDanger, GUILayout.Width(ClearButtonW), GUILayout.Height(RowHeight)))
            {
                _strike.Clear();
                Coordinator.CancelStrike();
            }
            GUILayout.EndHorizontal();

            if (totalOrders == 0)
            {
                GUI.color = TextDim;
                GUILayout.Label("   nothing staged yet; pick missiles and press + TARGET, " +
                                "or order a strike in the game and it is collected here", _row);
                GUI.color = Color.white;
                return;
            }

            // Height the list would like: one row per target, plus its orders when expanded. Capped
            // at a share of the window, so beyond that the list scrolls instead of growing.
            float listH = StrikeSectionHeight() - RowHeight;

            PushScrollSkin();
            _strikeScroll = GUILayout.BeginScrollView(_strikeScroll, GUILayout.Height(listH));

            foreach (ObjectBase t in _strikeTargetScratch)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
                GUI.color = TargetCol;
                GUILayout.Label(FoggedLabel(t), _rowOneLine, GUILayout.Width(210));
                GUI.color = TextMain;

                int orders = 0, rounds = 0;
                foreach (Coordinator.Shot sh in _strike)
                    if (sh.Target == t) { orders++; rounds += Mathf.Max(1, sh.Salvo); }
                foreach (Coordinator.Shot sh in collected)
                    if (sh.Target == t) { orders++; rounds += Mathf.Max(1, sh.Salvo); }
                // One line, never wrapped: at RowHeight a second line would be drawn below the
                // row's own box and overlap the target line under it.
                GUILayout.Label($"{orders} order(s)  ·  {rounds} round(s)", _rowOneLine,
                                GUILayout.ExpandWidth(false));
                GUI.color = Color.white;

                GUILayout.FlexibleSpace();
                ObjectBase removing = t;   // captured by the closure below, so it must not be the loop variable
                // Only the panel's own rows can be taken back out; an order the game has already
                // accepted is held by the coordinator, and CLEAR is the way to drop those.
                // Removes the whole group the player is looking at, staged rows and collected
                // in-game orders alike. Anything else makes the button lie about what it acts on.
                if (GUILayout.Button("REMOVE", _btnDanger, GUILayout.Width(ClearButtonW), GUILayout.Height(RowHeight)))
                {
                    _strike.RemoveAll(sh => sh.Target == removing);
                    Coordinator.RemoveStrikeIntents(removing);
                }
                GUILayout.EndHorizontal();

                // What each order actually fires, one condensed line apiece. The count above says
                // how much is committed; these say which shooter and which missile, which is what
                // tells the player the order they just gave is the one that got caught.
                if (_strikeDetail) DrawStrikeOrderLines(t, collected);
            }

            GUILayout.EndScrollView();
            PopScrollSkin();
        }

        /// <summary>
        /// The commit row. Exactly ONE solid green button is ever on screen, and it is whichever
        /// commit the player is currently building: the staged strike if anything is staged, the
        /// target in front of them otherwise. Direct fire stays available either way, because a
        /// pop-up threat is a legitimate reason to shoot without disturbing the plan; it just stops
        /// being the headline action.
        /// </summary>
        private void DrawCommitRow(List<ObjectBase> shooters, bool canFire)
        {
            CollectStrikeTargets(_strikeTargetScratch);
            bool strikeIsPrimary = _strike.Count + Coordinator.StrikeCount > 0;

            GUILayout.Space(4);

            // The primary, first and alone on its row.
            GUILayout.BeginHorizontal();
            if (strikeIsPrimary)
            {
                // Both halves are listed above as one strike, so the button counts them as one too.
                string held = Coordinator.StrikeCount > 0 ? $" ({Coordinator.StrikeCount} in-game)" : "";
                if (GUILayout.Button(
                        $"FIRE STRIKE ({FireHint()})  ·  {_strikeTargetScratch.Count} target(s), " +
                        $"{_strike.Count + Coordinator.StrikeCount} order(s){held}",
                        _fire, GUILayout.Height(FireButtonHeight)))
                {
                    Coordinator.FireStrike(_strike);
                    _strike.Clear();
                }
            }
            else
            {
                GUI.enabled = canFire;
                if (GUILayout.Button("FIRE THIS TARGET", _fire, GUILayout.Height(FireButtonHeight)))
                    FireSelected(shooters, coordinated: true);
                GUI.enabled = true;
            }
            GUILayout.Space(ResizeGripClearance);
            GUILayout.EndHorizontal();

            // The alternatives, outlined, on the row below.
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUI.enabled = canFire;
            if (GUILayout.Button("+ TARGET", _btnSecondary,
                                 GUILayout.Width(SecondaryButtonW), GUILayout.Height(SecondaryButtonH)))
                AddSelectionToStrike(shooters);

            // Demoted, not removed: firing the target in front of you must stay one click away even
            // while a strike is staged.
            if (strikeIsPrimary &&
                GUILayout.Button("FIRE THIS TARGET", _btnSecondary,
                                 GUILayout.Width(SecondaryButtonW + 30f), GUILayout.Height(SecondaryButtonH)))
                FireSelected(shooters, coordinated: true);

            if (GUILayout.Button("FIRE NOW (no sync)", _btnSecondary,
                                 GUILayout.Width(SecondaryButtonW + 30f), GUILayout.Height(SecondaryButtonH)))
                FireSelected(shooters, coordinated: false);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Space(ResizeGripClearance);
            GUILayout.EndHorizontal();
        }

        // Visual only ; the actual resize is driven from Update/HandleResizeInput.
        private void DrawResizeGrip()
        {
            GUI.color = _resizing ? TextMain : TextDim;
            GUI.Label(new Rect(_win.width - 20, _win.height - 20, 18, 18), "◢");
            GUI.color = Color.white;
        }

        private void FireSelected(List<ObjectBase> shooters, bool coordinated)
        {
            if (_target == null) return;
            var shots = new List<Coordinator.Shot>();
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                {
                    // Count 0 means the guidance channels are already spoken for at another
                    // target, so there is nothing to fire here even if the row is still ticked.
                    if (!r.InRange || r.Count <= 0) continue;
                    string key = Key(ship, r.AmmoId);
                    if (_checked.TryGetValue(key, out bool on) && on)
                    {
                        // Re-clamp to the CURRENT launcher cap: rounds may have been spent on
                        // another strike since the +/- buttons last clamped this picker.
                        int salvo = Mathf.Min(_salvo.TryGetValue(key, out int sv) ? sv : 1, r.Count);
                        shots.Add(new Coordinator.Shot
                        {
                            Unit = ship, AmmoId = r.AmmoId, Salvo = salvo, Target = _target,
                        });
                    }
                }
            if (shots.Count == 0) return;

            if (coordinated) Coordinator.FireCoordinated(shots);
            else foreach (var s in shots) Coordinator.FireNow(s.Unit, s.AmmoId, _target, s.Salvo);
        }

        private const float ScaleValueW = 36f;   // "1.0×", wide enough for every step

        /// <summary>
        /// The bottom strip: a divider and the panel-scale stepper, drawn at explicit rects on the
        /// window's bottom edge rather than in the layout flow.
        ///
        /// Same reasoning as the title bar. The flow puts a row wherever the content above it
        /// happens to end, so this row moved with the strike list and eventually sat on the border;
        /// and a flow row is only as tall as its tallest item, so the label beside the steppers had
        /// no row to be centred in. Here every item gets a full-height rect on ONE mid-line, and the
        /// strip keeps a fixed clearance from the resize grip in the corner.
        /// </summary>
        private void DrawFooter()
        {
            float y = _win.height - _winStyle.padding.bottom - FooterRowHeight;
            float right = _win.width - _winStyle.padding.right - ResizeGripClearance;

            GUI.color = new Color(Border.r, Border.g, Border.b, 0.5f);
            GUI.DrawTexture(new Rect(1f, y - 4f, _win.width - 2f, 1f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Measured from the right edge inwards, so the group stays put whatever the panel width.
            // Same style, size and centred read-out as the salvo steppers: both are a value between
            // two steps, so they should not be two different-looking controls.
            var plusRect  = new Rect(right - StepButtonW, y, StepButtonW, FooterRowHeight);
            var valueRect = new Rect(plusRect.x - ScaleValueW, y, ScaleValueW, FooterRowHeight);
            var minusRect = new Rect(valueRect.x - StepButtonW, y, StepButtonW, FooterRowHeight);

            var label = new GUIContent("Scale");
            float labelW = _hdrCenterV.CalcSize(label).x + 2f;
            var labelRect = new Rect(minusRect.x - labelW - 6f, y, labelW, FooterRowHeight);

            GUI.color = TextDim;
            GUI.Label(labelRect, label, _hdrCenterV);
            GUI.color = Color.white;
            if (GUI.Button(minusRect, "–", _btnStep))
                Bootstrap.SetUiScaleMultiplier(Bootstrap.UiScaleMultiplier - UiScaleStep);
            GUI.Label(valueRect, $"{Bootstrap.UiScaleMultiplier:0.0}×", _rowCenter);
            if (GUI.Button(plusRect, "+", _btnStep))
                Bootstrap.SetUiScaleMultiplier(Bootstrap.UiScaleMultiplier + UiScaleStep);
        }

        private void DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(1, DividerH);
            GUI.color = new Color(Border.r, Border.g, Border.b, 0.5f);
            GUI.DrawTexture(new Rect(r.x, r.y + 1, r.width, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
