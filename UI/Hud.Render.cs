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
        private const float AddShipButtonW = 100f;   // + SHIP / ✓ HELD
        private const float AddFormationButtonW = 130f;  // + FORMATION / ✓ HELD
        private const float HelpButtonW = 76f;       // ? KEYS, in the title bar
        // Secondary commit-row buttons. They share a width so the row reads as one set of
        // alternatives to the primary above it, and three of them fit inside MinWindowW.
        private const float SecondaryButtonW = 122f;
        private const float SecondaryButtonH = 28f;
        // Checkbox prefixes. The unchecked one is spaces of matching width so the label does not
        // shift sideways when a row is toggled.
        private const string CheckedPrefix = "\u2713 ", UncheckedPrefix = "  ";

        // SHOOTERS then TARGET, in the order the player works: pick who shoots, then what at.
        private void DrawSelectionHeader(bool haveTarget)
        {
            // The name gets whatever the two buttons leave, on ONE line. Wrapping here pushed a
            // long ship name up into the row above.
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUILayout.Label("SHOOTERS", _hdr, GUILayout.Width(SelectionLabelW));
            GUILayout.Label(_anchor != null ? UnitNaming.SafeName(_anchor) : "click one of your ships",
                            _rowOneLine, GUILayout.ExpandWidth(true));

            // Two buttons rather than one button and a mode. Each names the noun it adds, and each
            // says when its work is already done, so clicking a ship that is already held gives an
            // answer instead of looking like nothing happened.
            bool haveAnchor = _anchor != null && !_anchor.IsDestroyed;
            bool shipHeld = haveAnchor && _group.Contains(_anchor);
            GUI.enabled = haveAnchor && !shipHeld;
            if (GUILayout.Button(shipHeld ? "✓ HELD" : "+ SHIP", _btn,
                                 GUILayout.Width(AddShipButtonW), GUILayout.Height(RowHeight)))
                AddSelectionToGroup(wholeFormation: false);

            bool haveFormation = haveAnchor && _anchor.Formation != null;
            bool formationHeld = haveFormation && FormationFullyHeld(_anchor);
            GUI.enabled = haveFormation && !formationHeld;
            if (GUILayout.Button(formationHeld ? "✓ HELD" : "+ FORMATION", _btn,
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
                GUILayout.Label("from any formation; click a ship anywhere, then + SHIP",
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
                    if (!r.InRange) continue;
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
            GUILayout.Label(msg, _rowOneLine, GUILayout.ExpandWidth(true));
            // Collecting in-game orders is a mode, not a hint, so it belongs on the status line
            // whenever it is on and nowhere at all when it is off.
            if (Coordinator.StrikeArmed)
            {
                GUI.color = Accent;
                GUILayout.Label($"● collecting your in-game orders too ({Coordinator.StrikeCount} held); {StrikeHint()} to stop",
                                _rowOneLine);
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
        }

        // The hotkey list, shown only while the ? in the title bar is toggled on.
        private void DrawHelpOverlay()
        {
            DrawDivider();
            GUI.color = TextDim;
            GUILayout.Label($"{HideHint()}  hide the panel        " +
                            $"{ToggleHint()}  auto-coordinate normal group orders        " +
                            $"{StrikeHint()}  collect in-game orders into the strike", _hdr);
            GUILayout.Label("salvo  ·  Shift steps by 10, Ctrl by 5", _hdr);
            GUI.color = Color.white;
            DrawDivider();
        }

        // The title bar ; always visible, draggable, carries the collapse toggle and auto status.
        private void DrawHeader()
        {
            GUILayout.BeginHorizontal(GUILayout.Height(HeaderH));
            if (GUILayout.Button(_open ? "▾" : "▸", _chev, GUILayout.Width(26), GUILayout.Height(HeaderH)))
                _open = !_open;
            GUILayout.Label("TIME-ON-TARGET", _title, GUILayout.Height(HeaderH));
            GUILayout.FlexibleSpace();
            // The hotkeys live behind this, so they are findable without being repeated on a hint
            // line every frame. Outlined rather than bare: a glyph drawn in a label style reads as
            // part of the title, not as something to press.
            GUI.color = _showHelp ? Accent : Color.white;
            if (GUILayout.Button("? KEYS", _btnSecondary,
                                 GUILayout.Width(HelpButtonW), GUILayout.Height(HeaderH - 6f)))
                _showHelp = !_showHelp;
            GUI.color = Color.white;
            GUILayout.FlexibleSpace();
            // Live engagement count ; visible even while minimized.
            int rounds = 0, tgts = _salvos.Count;
            foreach (var e in _salvos) rounds += e.Queued + e.InFlight;
            if (tgts > 0)
            {
                GUI.color = TargetCol;
                GUILayout.Label($"● {tgts} tgt / {rounds} msl", _hdr, GUILayout.Height(HeaderH));
                GUILayout.Space(8);
            }
            GUI.color = Coordinator.Active ? Accent : TextDim;
            GUILayout.Label(Coordinator.Active ? "● AUTO" : "○ AUTO", _hdr, GUILayout.Height(HeaderH));
            GUI.color = Color.white;
            GUILayout.Space(4);
            GUILayout.EndHorizontal();
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

            GUI.enabled = r.InRange;                       // out-of-range rows can't be picked
            
            // Clickable missile name with checkmark prefix (like Sea Power menu items)
            bool isChecked = _checked[key] && r.InRange;
            string missileLabel = (isChecked ? CheckedPrefix : UncheckedPrefix) + $"{r.AmmoId}  x{r.Count}";
            
            // Set color based on range
            GUI.color = r.InRange ? TextMain : OutOfRange;
            if (GUILayout.Button(missileLabel, _menuItem, GUILayout.Height(RowHeight)))
                _checked[key] = !isChecked;
            GUI.color = Color.white;
            
            // ETA and range labels
            GUI.color = r.InRange ? Accent : OutOfRange;
            GUILayout.Label($"ETA {eta}", _row, GUILayout.Width(95));
            GUI.color = r.InRange ? TextMain : OutOfRange;
            GUILayout.Label(r.InRange ? range : range + " (out of range)", _row, GUILayout.Width(170));
            GUI.color = Color.white;
            
            GUILayout.FlexibleSpace();
            // The steppers state their own step size while a modifier is held, which is the moment
            // it matters. The full key list lives behind the ? in the title bar.
            int step = SalvoStep();
            string minus = step > 1 ? $"–{step}" : "–", plus = step > 1 ? $"+{step}" : "+";
            float stepW = step > 1 ? 44f : 30f;
            if (GUILayout.Button(minus, _btn, GUILayout.Width(stepW))) _salvo[key] = Mathf.Max(1, _salvo[key] - step);
            GUILayout.Label($"{_salvo[key]}", _row, GUILayout.Width(28));
            if (GUILayout.Button(plus, _btn, GUILayout.Width(stepW))) _salvo[key] = Mathf.Min(r.Count, _salvo[key] + step);
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
            // channel for its whole flight, so the salvo above is already clamped to what the boat
            // can guide at once. Say so, otherwise the shorter maximum looks like a bug.
            if (r.InRange && _checked[key])
            {
                int cap = LauncherFactsSource.GuidanceChannelCap(ship, r.AmmoId);
                if (cap < int.MaxValue)
                {
                    GUI.color = Warn;
                    GUILayout.Label($"     ⚠ {cap} guidance channel(s) : salvo capped, rounds are guided one per channel", _row);
                    GUI.color = Color.white;
                }

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
                float subDelay = LaunchEnvelope.TimeToReady(ship, r.AmmoId);
                if (subDelay > 0f)
                {
                    GUI.color = Warn;
                    GUILayout.Label($"     ⚠ submerged : ~{subDelay:0}s to reach launch depth, timing estimated; " +
                                    "come to launch depth for exact coordination", _row);
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

        /// <summary>True if another listed engagement shares this strike id.</summary>
        private bool SharesStrike(int strikeId)
        {
            int n = 0;
            foreach (EngagementBoard.SalvoLine e in _salvos)
                if (e.StrikeId == strikeId && ++n > 1) return true;
            return false;
        }

        private readonly List<ObjectBase> _strikeTargetScratch = new List<ObjectBase>();

        // The staged multi-target strike: one group per target, each removable, directly above the
        // commit row that fires them. The list and the button that acts on it belong together.
        private void DrawStagedStrike()
        {
            bool armed = Coordinator.StrikeArmed;
            if (_strike.Count == 0 && !armed) return;

            CollectStrikeTargets(_strikeTargetScratch);

            DrawDivider();
            GUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            GUILayout.Label(_strike.Count > 0
                ? $"STRIKE  ·  {_strikeTargetScratch.Count} target(s), {_strike.Count} order(s)"
                : "STRIKE", _hdr);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("CLEAR", _btnDanger, GUILayout.Width(ClearButtonW), GUILayout.Height(RowHeight)))
            {
                _strike.Clear();
                Coordinator.CancelStrike();
            }
            GUILayout.EndHorizontal();

            if (_strike.Count == 0)
            {
                GUI.color = TextDim;
                GUILayout.Label("   nothing staged from the panel yet; pick missiles and press + TARGET", _row);
                GUI.color = Color.white;
                return;
            }

            foreach (ObjectBase t in _strikeTargetScratch)
            {
                GUILayout.BeginHorizontal();
                GUI.color = TargetCol;
                GUILayout.Label(FoggedLabel(t), _rowOneLine, GUILayout.Width(210));
                GUI.color = TextMain;

                int ships = 0, rounds = 0;
                foreach (Coordinator.Shot sh in _strike)
                    if (sh.Target == t) { ships++; rounds += Mathf.Max(1, sh.Salvo); }
                GUILayout.Label($"{ships} order(s)  ·  {rounds} round(s)", _row);
                GUI.color = Color.white;

                GUILayout.FlexibleSpace();
                ObjectBase removing = t;   // captured by the closure below, so it must not be the loop variable
                if (GUILayout.Button("remove", _btn, GUILayout.Width(70)))
                    _strike.RemoveAll(sh => sh.Target == removing);
                GUILayout.EndHorizontal();
            }
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
                string held = Coordinator.StrikeCount > 0 ? $" + {Coordinator.StrikeCount} in-game" : "";
                if (GUILayout.Button(
                        $"FIRE STRIKE  ·  {_strikeTargetScratch.Count} target(s), {_strike.Count} order(s){held}",
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
                    if (!r.InRange) continue;
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

        // Draw a menu-item style checkbox row (checkmark prefix + hover highlight, like Sea Power
        // context menu). Always renders with _menuItem: the row IS the button, so a caller-supplied
        // label style would have nothing to apply to.
        private bool DrawCheckbox(bool value, string label, float width = 0f)
        {
            string displayText = (value ? CheckedPrefix : UncheckedPrefix) + label;
            bool clicked = width > 0f
                ? GUILayout.Button(displayText, _menuItem, GUILayout.Height(RowHeight), GUILayout.Width(width))
                : GUILayout.Button(displayText, _menuItem, GUILayout.Height(RowHeight));
            if (clicked) value = !value;
            return value;
        }

        // On-panel size control: –/+ buttons adjusting the UI scale multiplier in 0.1 steps.
        // Persists to config via Bootstrap so it survives restarts and stays in sync with the .cfg.
        private void DrawScaleControl()
        {
            GUI.color = TextDim;
            GUILayout.Label("Scale", _hdr, GUILayout.Height(RowHeight));
            GUI.color = Color.white;
            if (GUILayout.Button("–", _btn, GUILayout.Width(30)))
                Bootstrap.SetUiScaleMultiplier(Bootstrap.UiScaleMultiplier - UiScaleStep);
            GUILayout.Label($"{Bootstrap.UiScaleMultiplier:0.0}×", _row, GUILayout.Width(36));
            if (GUILayout.Button("+", _btn, GUILayout.Width(30)))
                Bootstrap.SetUiScaleMultiplier(Bootstrap.UiScaleMultiplier + UiScaleStep);
        }

        private void DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(1, 3);
            GUI.color = new Color(Border.r, Border.g, Border.b, 0.5f);
            GUI.DrawTexture(new Rect(r.x, r.y + 1, r.width, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
