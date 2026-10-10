using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial): panel content rendering, including the header, missile rows, the ENGAGEMENTS overview,
    /// fire actions and small draw helpers.
    /// </summary>
    internal sealed partial class Hud
    {
        private const float SelectionLabelW = 80f;   // TARGET / SHOOTERS label column
        private const float ClearButtonW = 70f;      // CLEAR, on both lists
        private const float AddUnitButtonW = 100f;   // + UNIT / ✓ HELD
        private const float AddFormationButtonW = 130f;  // + FORMATION / ✓ HELD
        private const float HelpButtonW = 76f;       // ? KEYS, in the title bar
        // Secondary commit-row buttons share one width, and three fit inside MinWindowW.
        private const float SecondaryButtonW = 122f;
        private const float SecondaryButtonH = 28f;
        // Salvo stepper buttons and count keep one width whatever the modifier prints, so the group does not jump.
        private const float StepButtonW = 42f, SalvoCountW = 42f;
        // The strike list gets at most this share of the window height (and at least StrikeListMinH), so the
        // shooter and strike lists stay usable together at any window size; it claims only the height its
        // content needs.
        private const float StrikeListShare = 0.5f;
        private const float StrikeListMinH = 2f * RowHeight;
        private const float StrikeOrderLineH = 18f;   // one condensed order line
        // Tick column width; the mark is drawn into it, so a row's text does not move when ticked.
        private const float MarkColumnW = 14f;

        // SHOOTERS then TARGET, in the order the player works: pick who shoots, then what at.
        private void DrawSelectionHeader(bool haveTarget)
        {
            // The name gets what the two buttons leave, on one line; wrapping pushed long names into the row above.
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUILayout.Label("SELECTED", _hdr, GUILayout.Width(SelectionLabelW));
            GUILayout.Label(_anchor != null ? UnitNaming.SafeName(_anchor) : "click one of your units",
                            _rowOneLine, GUILayout.ExpandWidth(true));

            // Two buttons, each naming what it adds and saying when it is already done.
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

            // With a roster, it replaces the current selection as the shooter list; say so, or clicking
            // another ship looks like the panel stopped tracking.
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

        /// <summary>One line stating what the commit buttons will do, or why they are greyed.</summary>
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
            // Sized to its text, so the collecting notice sits at the panel's right edge.
            GUILayout.Label(msg, _rowOneLine, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            // A strike still waiting on its flight estimates appears nowhere else in the panel; say so.
            if (Coordinator.CommitPending)
            {
                GUI.color = Accent;
                GUILayout.Label($"● preparing {Coordinator.PendingOrders} order(s)",
                                _rowOneLine, GUILayout.ExpandWidth(false));
            }
            // Collecting in-game orders is a mode, so it shows here whenever it is on.
            if (Coordinator.StrikeArmed)
            {
                GUI.color = Accent;
                GUILayout.Label($"● collecting your in-game orders too ({Coordinator.StrikeCount} held); {StrikeHint()} to stop",
                                _rowOneLine, GUILayout.ExpandWidth(false));
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
        }

        // The hotkey list as rows: a key column and a description column.
        private static readonly string[] HelpDescriptions =
        {
            "hide the panel",
            "auto-coordinate normal group orders",
            "collect the orders you issue in-game into the strike",
            "fire the staged strike",
            "hold while stepping a salvo: +/- 10 (Shift) or 5 (Ctrl)",
            // Not a key: the one title-bar light that reports rounds the player will not get, readable with
            // the panel collapsed.
            "a shooter is out of missile guidance channels; further orders for it are refused",
        };

        // One GUIContent reused for the header's width measurements, instead of one per label per pass.
        private static readonly GUIContent _measure = new GUIContent();

        private static float TextWidth(GUIStyle style, string text)
        {
            _measure.text = text;
            return style.CalcSize(_measure).x;
        }

        private static readonly GUIContent ScaleLabel = new GUIContent("Scale");

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

        /// <summary>Height the overlay needs, dividers included; one row per key, so it is exact.</summary>
        private float HelpOverlayHeight()
        {
            return HelpDescriptions.Length * HelpLineH + 2f * DividerH + 8f;
        }

        private const float HelpLineH = 20f;

        private const float DividerH = 3f;

        /// <summary>
        /// The title bar: always visible, draggable, with the collapse toggle and status. Drawn at explicit
        /// rects centred on the strip's mid-line, since the painted strip is taller than its layout row.
        /// </summary>
        private void DrawHeader(Rect strip)
        {
            // One mid-line for the bar; each item gets a full-height rect and the Middle* styles centre the glyphs.
            float y = strip.center.y - HeaderH * 0.5f;
            float left = strip.x + 6f, right = strip.xMax - 10f;

            var chevRect = new Rect(left, y, 26f, HeaderH);
            _chevRectWin = chevRect;
            if (GUI.Button(chevRect, _open ? "\u25be" : "\u25b8", _chev)) _open = !_open;

            // AUTO and the engagement count are laid out from the right edge inwards, independent of the title width.
            string autoText = Coordinator.Active ? "\u25cf AUTO" : "\u25cb AUTO";
            float autoW = TextWidth(_hdrCenterV, autoText) + 2f;
            var autoRect = new Rect(right - autoW, y, autoW, HeaderH);
            GUI.color = Coordinator.Active ? Accent : TextDim;
            GUI.Label(autoRect, autoText, _hdrCenterV);
            GUI.color = Color.white;
            right = autoRect.x - 8f;

            // Strike state, shaped like AUTO: collecting can be left running with the panel collapsed.
            int staged = _strike.Count + Coordinator.StrikeCount;
            if (Coordinator.StrikeArmed || staged > 0)
            {
                string strikeText = Coordinator.StrikeArmed
                    ? $"\u25cf STRIKE {staged}"
                    : $"\u25cb STRIKE {staged}";
                float strikeW = TextWidth(_hdrCenterV, strikeText) + 2f;
                GUI.color = Coordinator.StrikeArmed ? Warn : TextDim;
                GUI.Label(new Rect(right - strikeW, y, strikeW, HeaderH), strikeText, _hdrCenterV);
                GUI.color = Color.white;
                right -= strikeW + 8f;
            }

            // Guidance-channel trim: the one status reporting rounds asked for and not received, and its
            // triggers (AUTO, Alt+H) are used without opening the panel.
            if (Coordinator.AnyChannelCapped || AnyStagedChannelCap())
            {
                const string capText = "● CH CAP";
                float capW = TextWidth(_hdrCenterV, capText) + 2f;
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
                float liveW = TextWidth(_hdrCenterV, live) + 2f;
                GUI.color = TargetCol;
                GUI.Label(new Rect(right - liveW, y, liveW, HeaderH), live, _hdrCenterV);
                GUI.color = Color.white;
                right -= liveW + 8f;
            }

            // The hotkeys live behind this button, outlined so it reads as pressable; centred, then pushed
            // left if a long status would overlap it.
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

        // A held modifier grows the salvo step (read from Event.current during OnGUI); still clamped to the row range.
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
            // Rows start unticked, so + FORMATION then FIRE THIS TARGET fires only the rows the player picked, not every
            // in-range weapon in the formation, whose slowest would become the anchor and hold the rest.
            if (!_checked.ContainsKey(key)) _checked[key] = false;
            if (!_salvo.ContainsKey(key)) _salvo[key] = 1;

            bool haveTarget = _target != null && !_target.IsDestroyed;
            GUILayout.BeginHorizontal();

            string eta = "--", range = "";
            if (haveTarget)
            {
                // Out-of-range rows print "(out of range)" instead of an ETA, so they are not simulated: an
                // unreachable shot is where the integrator bails and the fallback estimator runs too.
                if (r.InRange)
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.UiEstimate);
                    float etaSec = FlightTime.EstimateForDisplay(ship, r.AmmoId, _target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.UiEstimate);
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.UiEstimateCalls);
                    // Not positive: no estimate yet (this frame's solve budget went to other rows) or none at all.
                    eta = etaSec > 0f ? FormatTime(etaSec) : "--";
                }
                float nm = GameUnits.NmBetween(ship, _target);
                range = $"{nm:0.0}nm";
            }

            // A row with no channels left is shown but cannot be picked, like an out-of-range one.
            bool selectable = r.InRange && r.Count > 0;

            GUI.enabled = selectable;
            
            // Clickable missile name, with the tick in its own column (like Sea Power menu items).
            bool isChecked = _checked[key] && selectable;

            GUI.color = selectable ? TextMain : OutOfRange;
            if (GUILayout.Button($"{r.AmmoId}  x{r.Count}", _menuItemMarked, GUILayout.Height(RowHeight)))
                _checked[key] = !isChecked;
            if (isChecked)
            {
                // Drawn over the row's rect, so the mark costs no width and toggling it moves nothing.
                Rect nameRect = GUILayoutUtility.GetLastRect();
                GUI.Label(new Rect(nameRect.x + _menuItem.padding.left, nameRect.y,
                                   MarkColumnW, nameRect.height), "\u2713", _rowCenter);
            }
            GUI.color = Color.white;
            
            // ETA and range labels
            GUI.color = selectable ? Accent : OutOfRange;
            GUILayout.Label($"ETA {eta}", _row, GUILayout.Width(95));
            GUI.color = selectable ? TextMain : OutOfRange;
            // Both range failures read as the range against the bound it broke, short enough for the fixed
            // 170 px label column.
            string why = r.TooClose ? " < min range"
                       : !r.InRange ? " > max range"
                       : r.Count <= 0 ? " (no channels left)"
                       : "";
            GUILayout.Label(range + why, _row, GUILayout.Width(170));
            GUI.color = Color.white;
            
            GUILayout.FlexibleSpace();
            // The steppers show their step size while a modifier is held; the key list is behind the ? in the
            // title bar.
            int step = SalvoStep();
            string minus = step > 1 ? $"–{step}" : "–", plus = step > 1 ? $"+{step}" : "+";
            if (GUILayout.Button(minus, _btnStep, GUILayout.Width(StepButtonW), GUILayout.Height(RowHeight)))
                _salvo[key] = Mathf.Max(1, _salvo[key] - step);
            GUILayout.Label($"{_salvo[key]}", _rowCenter, GUILayout.Width(SalvoCountW), GUILayout.Height(RowHeight));
            if (GUILayout.Button(plus, _btnStep, GUILayout.Width(StepButtonW), GUILayout.Height(RowHeight)))
                _salvo[key] = Mathf.Min(r.Count, _salvo[key] + step);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Warn when the salvo cannot fire before the launcher must reload: it still fires as one order,
            // arriving in waves that ENGAGEMENTS shows.
            if (r.InRange && _checked[key] &&
                LauncherFactsSource.WillNeedReload(ship, r.AmmoId, _salvo[key], out int ready, out int waves))
            {
                GUI.color = Warn;
                GUILayout.Label($"     ⚠ needs reload : {ready} ready, fires in {waves} waves", _row);
                GUI.color = Color.white;
            }

            // Shared missile group: shooters inside the ammo's join range feed one group, and overfilled
            // rounds never leave the rails. The player can fix this by repositioning.
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

            // Fire-control channel limit: a radio-command round that cannot join a group holds a channel for
            // its whole flight, so the picker clamps the row to what the ship can guide. These notes show
            // whether or not the row is ticked; the plain at-cap note needs a tick and tests `>=`, since the
            // + button clamps and `>` is never reached. Each is one short clause, because at the limit it
            // shows most of the time.
            if (r.ChannelCap < int.MaxValue)
            {
                int cap = r.ChannelCap;
                RowFacts rf = FactsFor(key);
                if (!rf.HaveChannel)
                {
                    rf.HaveChannel = true;
                    rf.ChannelTrimmed = Coordinator.IsChannelTrimmed(ship, r.AmmoId);
                    rf.ChannelCapped = Coordinator.IsChannelCapped(ship, r.AmmoId);
                }
                bool trimmed = rf.ChannelTrimmed;
                bool full = rf.ChannelCapped;
                int want = _salvo.TryGetValue(key, out int sv) ? sv : 0;
                bool atCap = _checked[key] && r.InRange && want >= r.Count && r.Count > 0;

                // Worst first; only the trimmed case says rounds were dropped.
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
                RowFacts rf = CheckedRowFacts(ship, r.AmmoId, key);

                // Beam-only launcher: the ship steers to put the target within 3 degrees of abeam, not merely
                // inside the arc, so it keeps turning after it could shoot and fires in bursts. The split
                // depends on time compression, so no number is given.
                if (rf.Manoeuvres)
                {
                    GUI.color = Warn;
                    GUILayout.Label($"     ⚠ manoeuvres to fire : launcher bears only abeam, ship steers into a " +
                                    $"{rf.WindowDeg:0}° window at 90° : salvo may split across turns", _row);
                    GUI.color = Color.white;
                }

                // Launcher contention: two targets on one box launcher cannot fire together, since the game
                // services the engage tasks one at a time.
                if (rf.Contended)
                {
                    GUI.color = Warn;
                    GUILayout.Label("     ⚠ launcher busy at another target : these rounds leave serially, " +
                                    "impacts will not sync", _row);
                    GUI.color = Color.white;
                }

                // Every guiding sensor this ammo can use has been shot away: the launcher holds the engage
                // task in ReadyUpWhileWaiting forever when hasFreeWeaponChannel() finds no operable free
                // sensor (WeaponSystemLauncher.cs:436-450).
                if (rf.SensorUnavailable)
                {
                    GUI.color = Warn;
                    GUILayout.Label("     ⚠ no guidance radar left : this order will not fire until one is repaired", _row);
                    GUI.color = Color.white;
                }

                // A submerged boat's (or descending aircraft's) delay is estimated. As anchor the error is
                // absorbed by its real launch; as a follower it lands on the arrival. The role is unknown
                // until commit, so warn whenever the delay is nonzero.
                float envelopeDelay = rf.EnvelopeDelay;
                if (envelopeDelay > 0f)
                {
                    // The delay covers a rising boat and a descending aircraft, so the text names the platform.
                    bool air = ship is Aircraft || ship is Helicopter;
                    GUI.color = Warn;
                    GUILayout.Label(air
                        ? $"     ⚠ ~{envelopeDelay:0}s estimated to reach launch altitude"
                        : $"     ⚠ ~{envelopeDelay:0}s estimated to reach launch depth", _row);
                    GUI.color = Color.white;
                }
            }
        }

        // Live overview of every coordinated salvo: target, rounds queued and in flight, and the impact countdown.
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
                GUILayout.Label(Coordinator.CommitPending
                    ? $"   preparing {Coordinator.PendingOrders} order(s)"
                    : "   none in progress", _row);
                GUI.color = Color.white;
                return;
            }

            float now = GameClock.SimNow();
            foreach (EngagementBoard.SalvoLine e in _salvos)
            {
                GUILayout.BeginHorizontal();
                GUI.color = TargetCol;
                // Rows sharing a strike id land together; say so, or a multi-target strike reads as coincidence.
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

        // One staged shooter: its name and every missile it fires at this target, small and dim; collected
        // orders are marked.
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

        // Shared by the grouping below and its height count, so both count the same lines. Never held across frames.
        private readonly List<ObjectBase> _orderShooterScratch = new List<ObjectBase>();
        private readonly System.Text.StringBuilder _orderLine = new System.Text.StringBuilder();

        /// <summary>
        /// The per-order breakdown under one target: one line per shooter listing every missile it fires
        /// there. Staged rows and in-game orders are grouped separately, so a line's in-game marker is true
        /// of the whole line.
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
        /// How many order lines the breakdown draws, grouped as DrawShooterGroup groups them, so the reserved
        /// height matches.
        /// </summary>
        private int StrikeOrderLineCount(List<ObjectBase> targets, List<Coordinator.Shot> collected)
        {
            int lines = 0;
            foreach (ObjectBase t in targets)
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
        /// Height of the strike section: its header row plus the scrolling list, which asks only for what its
        /// current contents need. The section is anchored to the bottom of the shared list budget and grows
        /// upward, so the commit row below stays put.
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
                           (_strikeDetail ? StrikeOrderLineCount(_strikeTargetScratch, collected) * StrikeOrderLineH : 0f) + 4f;
            return RowHeight +
                   Mathf.Min(wanted, Mathf.Max(StrikeListMinH, _listsAvailH * StrikeListShare));
        }

        // The staged strike, one removable group per target, directly above the commit row that fires it.
        private void DrawStagedStrike()
        {
            bool armed = Coordinator.StrikeArmed;
            if (_strike.Count == 0 && !armed) return;

            CollectStrikeTargets(_strikeTargetScratch);
            List<Coordinator.Shot> collected = CollectedOrders();
            int totalOrders = _strike.Count + collected.Count;

            DrawDivider();
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            // The chevron collapses the per-order breakdown, not the target lines.
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
                // One line, never wrapped: a second line would overlap the target line below.
                GUILayout.Label($"{orders} order(s)  ·  {rounds} round(s)", _rowOneLine,
                                GUILayout.ExpandWidth(false));
                GUI.color = Color.white;

                GUILayout.FlexibleSpace();
                ObjectBase removing = t;   // captured by the closure below, so it must not be the loop variable
                // Removes the whole group shown: staged rows and collected in-game orders alike.
                if (GUILayout.Button("REMOVE", _btnDanger, GUILayout.Width(ClearButtonW), GUILayout.Height(RowHeight)))
                {
                    _strike.RemoveAll(sh => sh.Target == removing);
                    Coordinator.RemoveStrikeIntents(removing);
                }
                GUILayout.EndHorizontal();

                // Which shooter fires which missile, one line apiece, so the player can see their order was caught.
                if (_strikeDetail) DrawStrikeOrderLines(t, collected);
            }

            GUILayout.EndScrollView();
            PopScrollSkin();
        }

        /// <summary>
        /// The commit row. Exactly one solid green button is on screen: the staged strike if anything is
        /// staged, else the target in front of the player. Direct fire stays available either way.
        /// </summary>
        /// <param name="canStage">A target and a checked row: + TARGET can stage something.</param>
        /// <param name="canFire">A target and a checked row not already staged at it: direct fire would fire
        /// something.</param>
        private void DrawCommitRow(List<ObjectBase> shooters, bool canStage, bool canFire)
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
                    // Cleared even if the fire throws, for the reason given in FireStrikeNow.
                    try { Coordinator.FireStrike(_strike); }
                    finally { _strike.Clear(); }
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
            GUI.enabled = canStage;
            if (GUILayout.Button("+ TARGET", _btnSecondary,
                                 GUILayout.Width(SecondaryButtonW), GUILayout.Height(SecondaryButtonH)))
                AddSelectionToStrike(shooters);
            GUI.enabled = canFire;

            // Demoted, not removed: firing the target in front of you stays one click away while a strike is staged.
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
                    // Count 0 means the guidance channels are spoken for at another target.
                    if (!r.InRange || r.Count <= 0) continue;
                    // Already staged at this target, so the strike fires it; firing it directly too would
                    // send it twice.
                    if (IsStagedHere(ship, r.AmmoId)) continue;
                    string key = Key(ship, r.AmmoId);
                    if (_checked.TryGetValue(key, out bool on) && on)
                    {
                        // Re-clamp to the current launcher cap; rounds may have been spent since the picker
                        // last clamped.
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
        /// The bottom strip: a divider and the panel-scale stepper, drawn at explicit rects on the window's
        /// bottom edge, like the title bar, so it stays put and its items centre on one mid-line.
        /// </summary>
        private void DrawFooter()
        {
            float y = _win.height - _winStyle.padding.bottom - FooterRowHeight;
            float right = _win.width - _winStyle.padding.right - ResizeGripClearance;

            GUI.color = new Color(Border.r, Border.g, Border.b, 0.5f);
            GUI.DrawTexture(new Rect(1f, y - 4f, _win.width - 2f, 1f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Laid out from the right edge inwards, styled like the salvo steppers.
            var plusRect  = new Rect(right - StepButtonW, y, StepButtonW, FooterRowHeight);
            var valueRect = new Rect(plusRect.x - ScaleValueW, y, ScaleValueW, FooterRowHeight);
            var minusRect = new Rect(valueRect.x - StepButtonW, y, StepButtonW, FooterRowHeight);

            GUIContent label = ScaleLabel;
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
