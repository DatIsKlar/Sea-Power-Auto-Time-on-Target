using System;
using System.Collections.Generic;
using System.IO;
using SeaPower;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Unattended in-game test driver. When <c>BepInEx/AutoTOT-livetest/plan.ini</c> exists at
    /// startup it is consumed (renamed to <c>plan.running</c>) and this component walks the game
    /// from the main menu into the named mission, fires the listed shots through the coordinator,
    /// waits for every round to land or die, writes a result file and quits the process. Nothing
    /// is clicked: every step calls the same method the game's own button would. Without the plan
    /// file the component does nothing at all.
    ///
    /// The driver is <c>tools/livetest/run.sh</c>, which writes the plan, launches Steam, waits for
    /// <c>results/DONE</c> and collects the log. See docs/plans/open/live-test-runner.md.
    ///
    /// Every state has a wall-clock timeout so a stuck run still produces a result and exits.
    /// </summary>
    internal sealed partial class LiveTestRunner : MonoBehaviour
    {
        private enum State { Idle, Menu, Load, Start, Arm, Fire, Watch, Settle, Finish, Done }

        // Panel mode: whether the panel has been staged, and when the hold started.
        private bool _panelStaged;
        private float _panelHeldFrom;

        private State _state = State.Idle;
        private float _stateEntered;          // Time.realtimeSinceStartup at entry
        private float _menuSeenAt = -1f;      // main menu first observed (settle timer)
        private float _startedAt = -1f;       // mission running (post-briefing) real time
        private float _firedSim = -1f;        // sim clock at FireCoordinated
        private float _firedReal = -1f;
        private bool _sawActivity;            // queue/pending/in-flight observed after firing
        private int _expectedRounds;          // salvo total ordered, so Watch can end on the count
        private string _stuckReason;

        private Plan _plan;
        private readonly List<Coordinator.Shot> _shots = new List<Coordinator.Shot>();
        private readonly List<ObjectBase> _targets = new List<ObjectBase>();
        private readonly List<string> _notes = new List<string>();

        // Wall-clock budgets per state, seconds. Proton cold start plus shader warm-up can hold
        // the menu for minutes; loading a mission likewise.
        private const float MenuTimeout = 900f;
        private const float LoadTimeout = 900f;
        private const float MenuSettle = 3f;
        private const float StartSettle = 1f;
        private const float CommitGrace = 60f;   // empty queue after firing = commit dropped all
        private const float DoneSettle = 10f;    // retargeted rounds still closing

        private static LiveTestRunner _active;

        private void Awake()
        {
            try
            {
                if (!ConsumePlanFile()) { enabled = false; return; }
                _active = this;
                // Unattended runs are silent. In-process only: nothing is saved to the game's options.
                AudioListener.volume = 0f;
                Bootstrap.Log.LogInfo($"[AutoTOT] livetest: plan '{_plan.Name}' consumed, mission '{_plan.Mission}', {_plan.Shots.Count} shot(s)");
                Enter(State.Menu);
            }
            catch (Exception e)
            {
                ModLog.Warn("livetest: startup failed", e);
                enabled = false;
            }
        }

        private void Update()
        {
            if (_quitAt >= 0f)
            {
                if (Time.realtimeSinceStartup >= _quitAt) { _quitAt = -1f; FlushDiskLog(); Application.Quit(); }
                return;
            }
            try { Step(); }
            catch (Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] livetest: exception in {_state}:\n{e}");
                Fail($"EXCEPTION in {_state}: {e.GetType().Name}: {e.Message}");
            }
        }

        private void Enter(State s)
        {
            _state = s;
            _stateEntered = Time.realtimeSinceStartup;
            Bootstrap.Log.LogInfo($"[AutoTOT] livetest: -> {s}");
        }

        private float InState => Time.realtimeSinceStartup - _stateEntered;

        private void Step()
        {
            if (_state >= State.Arm && _state <= State.Watch) TickManualOrders();
            switch (_state)
            {
                case State.Idle:
                case State.Done:
                    return;


                case State.Menu:
                    if (InState > MenuTimeout) { Fail("STUCK in Menu: main menu never appeared"); return; }
                    if (!Singleton<MenuSystemViewModel>.InstanceExists()) return;
                    object win = Singleton<MenuSystemViewModel>.Instance.CurrentWindow;
                    if (win == null || win.GetType().Name != "MainMenuView") { _menuSeenAt = -1f; return; }
                    if (_menuSeenAt < 0f) _menuSeenAt = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - _menuSeenAt < MenuSettle) return;
                    Enter(State.Load);
                    return;

                case State.Load:
                {
                    // Same two statements as ScenarioMenuViewModel's Play command.
                    string path = Singleton<FileManager>.Instance.GetFile("missions/" + _plan.Mission);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        Fail($"REJECTED mission not found: missions/{_plan.Mission}");
                        return;
                    }
                    Globals.currentMissionFilePath = path;
                    MissionManager.DoLoad();
                    Enter(State.Start);
                    return;
                }

                case State.Start:
                {
                    if (InState > LoadTimeout) { Fail("STUCK in Start: mission never finished loading"); return; }
                    if (!Singleton<SceneCreator>.InstanceExists() || !Singleton<GameMain>.InstanceExists()) return;
                    SceneCreator sc = Singleton<SceneCreator>.Instance;
                    if (!sc.IsLoadingDone || !Singleton<GameMain>.Instance._initialized) return;

                    if (_startedAt < 0f)
                    {
                        // The briefing pauses the game only when it has content. Its Play button's
                        // body, verbatim (MissionBriefViewModel.PlayCommand).
                        if (sc._pauseForBriefing)
                        {
                            if (Singleton<InputHandler>.InstanceExists())
                            {
                                Singleton<InputHandler>.Instance.IsInMessagePauseMode = false;
                                Singleton<InputHandler>.Instance.IsInPauseMode.Value = false;
                            }
                            sc._pauseForBriefing = false;
                            CancelAction.UnPause();
                            _notes.Add("briefing dismissed");
                        }
                        _startedAt = Time.realtimeSinceStartup;
                        return;
                    }
                    if (Time.realtimeSinceStartup - _startedAt < StartSettle) return;

                    // UnPause() reset compression, so set it only now.
                    ApplyCompression();
                    // VerboseLog is forced: LaunchDiagnostics only registers a launch expectation
                    // for every order when it is on, and Watch reads those. The rest is per plan.
                    Coordinator.VerboseLog = true;
                    Coordinator.TraceFlightModel = _plan.TraceFlightModel;
                    VerticalProfiler.Enabled = _plan.VerticalProfile;
                    AlignmentProbe.Enabled = _plan.AlignmentTrace;
                    AlignmentProbe.UidCollisionFix = _plan.AlignmentUidFix;
                    Coordinator.ProfilingEnabled = _plan.Profiling;
                    FlightTime.VerifySolve = _plan.VerifySolve;
                    Bootstrap.Log.LogInfo($"[AutoTOT] livetest gates: verbose on, trace {_plan.TraceFlightModel}, " +
                                          $"vertical {_plan.VerticalProfile}, alignment {_plan.AlignmentTrace}, uidfix {_plan.AlignmentUidFix}, profiling {_plan.Profiling}, verifySolve {_plan.VerifySolve}");
                    AudioListener.volume = 0f;   // again, in case the options manager reset it on load
                    Enter(State.Arm);
                    return;
                }

                case State.Arm:
                {
                    if (_shots.Count == 0 && !ResolveShots()) return;   // ResolveShots failed the run
                    List<string> waiting = UncontactedTargets();
                    if (waiting.Count > 0)
                    {
                        if (InState > _plan.WaitForContactSec)
                            Fail("STUCK waiting for contact: " + string.Join(", ", waiting));
                        return;
                    }
                    // Panel fire: build the shot in the planner as a player does, and keep it on
                    // screen from contact on, so the panel's per-frame work over the whole roster runs
                    // through the hold, the release and the flight, as it does while a player waits.
                    if (_plan.PanelFire && _plan.PanelHoldSec <= 0f && !_panelStaged)
                    {
                        _panelStaged = Hud.StagePanelForShots(_shots, wholeFormation: true);
                        if (!_panelStaged) { Fail("panel fire: staging failed, the panel is not up"); return; }
                        Bootstrap.Log.LogInfo("[AutoTOT] livetest: panel staged for FIRE THIS TARGET, whole formation rostered");
                        _panelHeldFrom = Time.realtimeSinceStartup;
                    }
                    if (!HoldSatisfied()) return;
                    // Panel mode: stage the first shot on screen and hold, instead of firing. The
                    // warnings this photographs are drawn per weapon row and in the strike section,
                    // so they need a shooter picked, a target picked and a row ticked, which is
                    // exactly what StagePanelFor sets. Nothing is fired and nothing is captured.
                    if (_plan.PanelHoldSec > 0f)
                    {
                        if (!_panelStaged)
                        {
                            Coordinator.Shot s0 = _shots[0];
                            _panelStaged = Hud.StagePanelFor(s0.Unit, s0.Target, s0.AmmoId, s0.Salvo);
                            Bootstrap.Log.LogInfo(_panelStaged
                                ? $"[AutoTOT] livetest: panel staged, holding {_plan.PanelHoldSec:0}s for a screenshot"
                                : "[AutoTOT] livetest: panel staging FAILED, the panel is not up");
                            _panelHeldFrom = Time.realtimeSinceStartup;
                        }
                        if (Time.realtimeSinceStartup - _panelHeldFrom < _plan.PanelHoldSec) return;
                        _notes.Add(_panelStaged ? "panel held for screenshot" : "panel staging failed");
                        Enter(State.Finish);
                        return;
                    }
                    if (_plan.PanelFire && _panelStaged)
                    {
                        // A few frames for the rows the panel draws to be built before the press.
                        const float PanelFireSettleSec = 3f;
                        if (Time.realtimeSinceStartup - _panelHeldFrom < PanelFireSettleSec) return;
                    }
                    Enter(State.Fire);
                    return;
                }

                case State.Fire:
                    _firedSim = GameClock.SimNow();
                    _firedReal = Time.realtimeSinceStartup;
                    Bootstrap.Log.LogInfo($"[AutoTOT] livetest: firing {_shots.Count} shot(s) at sim {_firedSim:0.0}" +
                                          (_plan.FireNow ? " as direct FIRE NOW orders" : _plan.PanelFire ? " through the panel's FIRE THIS TARGET" : ""));
                    if (_plan.FireNow)
                    {
                        // Direct orders, the panel's FIRE NOW path without its range filter: nothing is
                        // coordinated or held, and an order inside minimum range reaches the game as is.
                        foreach (Coordinator.Shot shot in _shots)
                            Coordinator.FireNow(shot.Unit, shot.AmmoId, shot.Target, shot.Salvo);
                        _notes.Add("fired as direct orders through Coordinator.FireNow, nothing coordinated");
                    }
                    else if (_plan.PanelFire && _panelStaged)
                    {
                        string why = Hud.PressFireThisTarget();
                        if (why != null) { Fail("panel fire: FIRE THIS TARGET not pressed, " + why); return; }
                        _notes.Add("fired through the panel's FIRE THIS TARGET, whole formation rostered");
                    }
                    else
                        Coordinator.FireCoordinated(new List<Coordinator.Shot>(_shots));
            _notes.Add($"expected rounds {_expectedRounds}");
                    Enter(State.Watch);
                    return;

                case State.Watch:
                {
                    // Mission events (a civilian hit, a scripted message) pause the game or drop
                    // compression mid-flight. Unattended, nobody clicks them away, so do it here.
                    ApplyCompression();
                    float simElapsed = GameClock.SimNow() - _firedSim;
                    if (_plan.FireNow)
                    {
                        // Direct orders register no launch expectation, so nothing below can see them.
                        // The run is an observation window: the game's own logs are the result.
                        if (simElapsed > _plan.TimeoutSec)
                        {
                            _notes.Add($"observation window of {_plan.TimeoutSec:0}s sim elapsed");
                            Enter(State.Finish);
                        }
                        return;
                    }
                    if (simElapsed > _plan.TimeoutSec)
                    {
                        Fail($"STUCK in Watch: {_plan.TimeoutSec:0}s of sim time elapsed with rounds unresolved");
                        return;
                    }
                    // Every ordered round is accounted for: nothing can still be coming.
                    if (_sawActivity && _rounds.Count >= _expectedRounds) { Enter(State.Settle); return; }

                    // NOT HasInFlightAt(target). A seeker that switches to a sibling in the
                    // target's formation clears the per-target flag while the round is still 25 km
                    // out, and the first automated run ended exactly there: the runner quit at
                    // t+281s with the missile in TerminalApproach, so nothing was ever recorded.
                    bool anyActive = LaunchDiagnostics.AnyCoordinatedInFlight();
                    if (!anyActive)
                    {
                        foreach (ObjectBase t in _targets)
                        {
                            if (t == null || t.IsDestroyed) continue;
                            if (HasScheduled(t) || LaunchDiagnostics.HasPendingLaunch(t))
                            {
                                anyActive = true;
                                break;
                            }
                        }
                    }
                    if (anyActive) { _sawActivity = true; return; }
                    if (!_sawActivity)
                    {
                        // The commit is deferred to the solve workers, so an empty queue right
                        // after FireCoordinated is normal. Staying empty is not.
                        if (Time.realtimeSinceStartup - _firedReal < CommitGrace) return;
                        Fail("REJECTED by commit: nothing was scheduled, launched or seen in flight " +
                             "(RevalidateCommit logs the reason)");
                        return;
                    }
                    Enter(State.Settle);
                    return;
                }

                case State.Settle:
                    if (InState < DoneSettle) return;
                    Enter(State.Finish);
                    return;

                case State.Finish:
                    Finish(_stuckReason ?? "DONE");
                    return;
            }
        }

        private bool[] _manualDone;

        /// <summary>
        /// ManualAtInbound lines. Once a missile threatens the named ship and is inside its SAM's range,
        /// issue the SAM order the way the game's own attack order does (AttackTask.OnExecute calls
        /// InsertEngageTask with autoAttack false and GetEngagePriority), without the coordinator's
        /// bypass, so the Harmony prefix sees a player order. Each line is issued once.
        /// </summary>
        private void TickManualOrders()
        {
            if (_plan == null || _plan.Manual.Count == 0) return;
            if (_manualDone == null) _manualDone = new bool[_plan.Manual.Count];
            var byKey = Singleton<SceneCreator>.Instance.ObjectsByPvKey;
            for (int i = 0; i < _plan.Manual.Count; i++)
            {
                if (_manualDone[i]) continue;
                Plan.ManualLine m = _plan.Manual[i];
                if (!byKey.TryGetValue(m.Shooter, out ObjectBase ship) || ship == null || ship.IsDestroyed)
                {
                    _manualDone[i] = true;
                    _notes.Add($"manual {m.Shooter}: shooter not found or destroyed");
                    continue;
                }
                Ammunition ammo = ship.getAmmunitionByName(m.Ammo);
                if (ammo == null)
                {
                    _manualDone[i] = true;
                    _notes.Add($"manual {m.Shooter}: no {m.Ammo} aboard");
                    continue;
                }
                Missile threat = ship.ThreateningMissile;
                if (threat == null || threat.IsDestroyed) continue;
                float dist = (threat.transform.position - ship.transform.position).magnitude;
                if (dist > ship.GetMaxRangeForAmmo(ammo, threat)) continue;

                _manualDone[i] = true;
                string what = $"manual {m.Ammo} x{m.Salvo} from {UnitNaming.SafeName(ship)} at " +
                              $"{UnitNaming.SafeName(threat)}, {dist * GameUnits.UnityToNm:0.0} nm, sim " +
                              $"{GameClock.SimNow():0.0}, auto mode {(Coordinator.Active ? "ON" : "off")}";
                Bootstrap.Log.LogInfo("[AutoTOT] livetest: " + what);
                _notes.Add(what);
                ship.InsertEngageTask(m.Ammo, threat, Vector3.zero, Mathf.Max(1, m.Salvo),
                                      ship.GetEngagePriority(threat), autoAttack: false,
                                      markAsReturned: false, isFormationAttack: false);
            }
        }

        private float _lastCompressionFix = -1f;

        /// <summary>
        /// Keep the game running at the plan's compression: clear a message pause the way the
        /// briefing's Play button does, then set the rate. Idempotent; logs when it had to act.
        /// </summary>
        private void ApplyCompression()
        {
            bool acted = false;
            if (Singleton<InputHandler>.InstanceExists())
            {
                InputHandler ih = Singleton<InputHandler>.Instance;
                if (ih.IsInMessagePauseMode || ih.IsInPauseMode.Value)
                {
                    ih.IsInMessagePauseMode = false;
                    ih.IsInPauseMode.Value = false;
                    acted = true;
                }
            }
            if (GameTime.IsPaused()) { CancelAction.UnPause(); acted = true; }
            float want = Mathf.Max(1f, _plan.Compression);
            if (Mathf.Abs(GameTime.TimeCompression - want) > 0.01f)
            {
                GameTime.SetTimeCompression(want);
                acted = true;
            }
            if (acted && Time.realtimeSinceStartup - _lastCompressionFix > 5f)
            {
                _lastCompressionFix = Time.realtimeSinceStartup;
                Bootstrap.Log.LogInfo($"[AutoTOT] livetest: re-applied {want:0}x compression / unpaused");
            }
        }

        private static bool HasScheduled(ObjectBase t)
        {
            IReadOnlyList<Coordinator.Scheduled> items = Coordinator.ScheduledItems;
            for (int i = 0; i < items.Count; i++)
                if (ReferenceEquals(items[i].Item?.Target, t)) return true;
            return false;
        }

        /// <summary>
        /// Turn the plan's shot lines into coordinator shots. Every check the planner panel makes
        /// before it offers a row (ammo present and a missile, matches the target, inside the
        /// launch band) is repeated here, so a bad plan is REJECTED at once instead of holding
        /// the ship forever. Returns false after failing the run.
        /// </summary>
        private bool ResolveShots()
        {
            var byKey = Singleton<SceneCreator>.Instance.ObjectsByPvKey;

            // Inventory pass first, over EVERY line. Resolution stops at the first bad shot, so a
            // probe plan (one deliberately unknown ammo name) would otherwise report only its first
            // shooter. Dumping all of them up front makes one 60 s probe answer the whole mission.
            foreach (Plan.ShotLine probe in _plan.Shots)
            {
                if (byKey.TryGetValue(probe.Shooter, out ObjectBase pu) && pu != null && !pu.IsDestroyed &&
                    byKey.TryGetValue(probe.Target, out ObjectBase pt) && pt != null && !pt.IsDestroyed)
                    LogInventory(pu, pt);
                else
                    Bootstrap.Log.LogInfo($"[AutoTOT] livetest inventory {probe.Shooter} vs {probe.Target}: " +
                                          "one of the two keys does not resolve in this mission");
            }

            foreach (Plan.ShotLine line in _plan.Shots)
            {
                if (!byKey.TryGetValue(line.Shooter, out ObjectBase unit) || unit == null)
                { Fail($"REJECTED unknown shooter key '{line.Shooter}'"); return false; }
                if (!byKey.TryGetValue(line.Target, out ObjectBase target) || target == null)
                { Fail($"REJECTED unknown target key '{line.Target}'"); return false; }
                if (unit.IsDestroyed) { Fail($"REJECTED shooter {line.Shooter} is destroyed"); return false; }
                if (target.IsDestroyed) { Fail($"REJECTED target {line.Target} is destroyed"); return false; }

                string ammoId = line.Ammo;
                string reason;
                if (ammoId == "*")
                {
                    ammoId = null;
                    var why = new List<string>();
                    foreach (KeyValuePair<string, int> kv in unit.AmmunitionAmountDictionary)
                    {
                        if (kv.Value <= 0) continue;
                        if (Check(unit, kv.Key, target, out string r)) { ammoId = kv.Key; break; }
                        if (r != "not a missile") why.Add($"{kv.Key}: {r}");
                    }
                    if (ammoId == null)
                    {
                        float nm = (target.transform.position - unit.transform.position).magnitude * GameUnits.UnityToNm;
                        Fail($"REJECTED no usable missile on {line.Shooter} for {line.Target} at {nm:0} nm " +
                             $"({(why.Count > 0 ? string.Join("; ", why) : "no missiles aboard")})");
                        return false;
                    }
                }
                else if (!Check(unit, ammoId, target, out reason))
                {
                    Fail($"REJECTED {reason}: {line.Shooter} {ammoId} -> {line.Target}");
                    return false;
                }

                int salvo = Math.Max(1, line.Salvo);
                _expectedRounds += salvo;
                _shots.Add(new Coordinator.Shot { Unit = unit, AmmoId = ammoId, Target = target, Salvo = salvo });
                if (!_targets.Contains(target)) _targets.Add(target);
                _notes.Add($"shot {UnitNaming.SafeName(unit)} {ammoId} x{line.Salvo} -> {UnitNaming.SafeName(target)}");
            }
            return true;
        }

        /// <summary>
        /// Every missile aboard this shooter and why each one can or cannot be used against this
        /// target. Plans pin ammunition by name, and the names of modded units are not in the
        /// game's own files, so without this a plan author is guessing: the first automated run of
        /// `teest` asked for `*` and silently got surface-to-air rounds rather than the anti-ship
        /// weapons the scenario exists to test.
        /// </summary>
        private static void LogInventory(ObjectBase unit, ObjectBase target)
        {
            var sb = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> kv in unit.AmmunitionAmountDictionary)
            {
                Ammunition a = unit.getAmmunitionByName(kv.Key);
                if (a?._ap == null || a._ap._type != Ammunition.Type.Missile) continue;
                bool ok = Check(unit, kv.Key, target, out string why);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(" x").Append(kv.Value).Append(ok ? " USABLE" : " (" + why + ")");
            }
            Bootstrap.Log.LogInfo($"[AutoTOT] livetest inventory {UnitNaming.SafeName(unit)} vs " +
                                  $"{UnitNaming.SafeName(target)}: {(sb.Length > 0 ? sb.ToString() : "no missiles")}");
        }

        /// <summary>The panel's row filter, in the same order (Hud.Rows.EngageRows).</summary>
        private static bool Check(ObjectBase unit, string ammoId, ObjectBase target, out string reason)
        {
            reason = null;
            if (!unit.AmmunitionAmountDictionary.TryGetValue(ammoId, out int n) || n <= 0)
            { reason = "no such ammunition aboard"; return false; }
            Ammunition a = unit.getAmmunitionByName(ammoId);
            if (a?._ap == null || a._ap._type != Ammunition.Type.Missile)
            { reason = "not a missile"; return false; }
            if (!unit.DoesAmmoMatchTarget(a._ap, target, out _))
            { reason = "ammunition does not match target"; return false; }
            float dist = (target.transform.position - unit.transform.position).magnitude;
            if (dist > unit.GetMaxRangeForAmmo(a, target))
            { reason = "out of range"; return false; }
            if (!(_active?._plan?.AllowInsideMinRange ?? false) &&
                LaunchEnvelope.IsInsideMinimumRange(unit, a._ap, target, out float rangeNm, out float minNm))
            { reason = $"inside min range ({rangeNm:0.0} nm < {minNm:0.0} nm)"; return false; }
            return true;
        }

        private float _lastHoldLog = -1f;

        /// <summary>
        /// Delay firing until the plan says the geometry is right. Two forms, both optional:
        /// `HoldUntilSec` waits a fixed span of SIM time from mission start, and
        /// `HoldUntilRangeNm` waits for one named shooter to close inside a range of its target.
        ///
        /// The range form exists because who anchors a strike is decided by enroute time, and a
        /// closing aircraft crosses over. A Hornet 70 nm out has a far longer Harpoon flight than
        /// the ship beside it and is elected anchor; let it run in to 30 nm and the same mission
        /// tests it as a FOLLOWER instead, with no edit to the mission file. Ranges are checked
        /// against the shot's own target, and a shooter named in no shot line is ignored.
        /// </summary>
        private bool HoldSatisfied()
        {
            if (_plan.HoldUntilSec > 0f && GameClock.SimNow() < _plan.HoldUntilSec)
            {
                LogHold($"sim {GameClock.SimNow():0}s of {_plan.HoldUntilSec:0}s");
                return false;
            }
            if (string.IsNullOrEmpty(_plan.HoldShooter) || _plan.HoldRangeNm <= 0f) return true;

            for (int i = 0; i < _shots.Count; i++)
            {
                ObjectBase u = _shots[i].Unit, t = _shots[i].Target;
                if (u == null || t == null || u.IsDestroyed || t.IsDestroyed) continue;
                if ((u._sectionName ?? "") != _plan.HoldShooter) continue;
                float nm = (t.transform.position - u.transform.position).magnitude * GameUnits.UnityToNm;
                if (nm > _plan.HoldRangeNm)
                {
                    LogHold($"{_plan.HoldShooter} at {nm:0.0} nm, waiting for {_plan.HoldRangeNm:0.0} nm");
                    return false;
                }
            }
            return true;
        }

        private void LogHold(string what)
        {
            // Once every 10 s of wall clock: a hold can legitimately last minutes of sim time.
            if (Time.realtimeSinceStartup - _lastHoldLog < 10f) return;
            _lastHoldLog = Time.realtimeSinceStartup;
            Bootstrap.Log.LogInfo($"[AutoTOT] livetest: holding fire, {what}");
        }

        private List<string> UncontactedTargets()
        {
            var list = new List<string>();
            PlottingTable pt = Globals._playerTaskforce?.PlottingTable;
            foreach (ObjectBase t in _targets)
            {
                Vehicle v = null;
                try { v = pt?.VehicleForObject(t); } catch { v = null; }
                if (v == null || v.KnownUnityPosition == null) list.Add(t._sectionName ?? UnitNaming.SafeName(t));
            }
            return list;
        }

        private void Fail(string reason)
        {
            if (_state == State.Done) return;
            Bootstrap.Log.LogWarning("[AutoTOT] livetest: " + reason);
            _stuckReason = reason;
            Finish(reason);
        }

        /// <summary>Called from LaunchDiagnostics.RetireDeadTrackers for every round it retires.</summary>
        internal static void OnRoundRetired(LaunchDiagnostics.FlightSample s, float flightTime)
        {
            LiveTestRunner r = _active;
            if (r == null || r._state == State.Done || r._state == State.Idle) return;
            try { r.Record(s, flightTime); }
            catch (Exception e) { ModLog.Warn("livetest: record failed", e); }
        }
    }
}
