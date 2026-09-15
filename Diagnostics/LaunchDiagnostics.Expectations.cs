using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Shot-count accounting: what one coordinated order asked for, what left the rail, and the shortfall
    /// line when they differ. Feeds observation anchoring back to the coordinator.
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        // Per coordinated order: missiles requested against missiles launched, deadlined in sim time so
        // compression and pause cannot produce false shortfalls.
        private sealed class LaunchExpectation
        {
            public ObjectBase Unit;
            public string AmmoId;      // order id (for facts / logging)
            public string AmmoFile;    // resolved _ammunitionFileName, for matching airborne missiles
            public ObjectBase Target;
            public int Requested;
            public int Launched;
            public float DeadlineSim;  // sim time by which the salvo should have fully rippled
            public Coordinator.Scheduled Linked;   // scheduled entry when this order is a batch anchor
            public float IniInterval;  // a-priori per-round interval (adaptive-deadline fallback)
            public float WaveTailSim;  // expected span+gaps of reload waves after wave 1
            public float LastLaunchSim = -1f; // most recent observed launch (sim s)
            public float RegisteredSim;       // when the order was issued; the reference before any launch
            public float EnvelopeLead;        // predicted transit into the launch envelope; 0 = none
            public float StartupLead;         // predicted order-to-first-round offset, including EnvelopeLead
            // Copied rather than read through Linked, which is held only for an anchor: the release lead
            // matters most for a follower. ImpactAtRegister is the impact the order was released against,
            // handed down from the release loop, since Fire is passed null for a follower.
            public float ReleaseLeadAtRegister = -1f;
            public float ImpactAtRegister = -1f;
            public float LastEngageSampleSim = float.NegativeInfinity;

            // One trace per launcher object, since a merged per-ship state string cannot attribute a hold to
            // the launcher that paid it. References into _launcherTraces, shared with any other order on the
            // same launcher.
            public List<LauncherTrace> Launchers;

            // The candidate pool when the order was issued, so the round-away line can say whether it waited
            // for a free launcher.
            public int UsableAtRegister = -1;
            public int LaunchersAtRegister = -1;
        }

        private static readonly List<LaunchExpectation> _launchExpectations = new List<LaunchExpectation>();

        // Last launch per (ship, ammo), updated for every sighting, so an order queued behind others is not
        // reported short while rounds are still leaving. Keyed by instance id, so a destroyed ship cannot
        // keep it alive.
        private static readonly Dictionary<ShipAmmoKey, float> _shipLastLaunchSim =
            new Dictionary<ShipAmmoKey, float>();

        // Launches that matched no open order; non-zero means misattribution or early retirement.
        internal static int UncreditedLaunches;

        private const float ExpectationMarginSim = 10f; // slack (sim s) beyond the computed ripple time

        // A missile that disappears within this range of its target is reported ARRIVED. Not proof of a hit:
        // it may have been shot down on final approach.
        internal const float HitRangeM = 500f;

        /// <summary>
        /// The declared warm-up against the one each launcher actually sat through, and each launcher's state
        /// as the round left. Per launcher object, so a hold is attributed to the launcher that paid it.
        /// </summary>
        private static void LogRoundAway(LaunchExpectation e, float launchStamp, float observed)
        {
            if (e.Launchers == null || e.Launchers.Count == 0) return;

            float declared = LauncherProbe.DeclaredOnRailWarmup(e.Unit, e.AmmoId);
            for (int i = 0; i < e.Launchers.Count; i++)
            {
                LauncherTrace t = e.Launchers[i];
                string warmup = t.LastWarmupHold >= 0f
                    ? $", onRailWarmup declared {Declared(declared)} observed {t.LastWarmupHold:0.00}s " +
                      $"residual {t.LastWarmupHold - Mathf.Max(0f, declared):+0.00;-0.00}s"
                    : $", onRailWarmup declared {Declared(declared)} never entered";
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] engage-state {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} " +
                    $"#{t.Id}[{t.SystemName}]: {t.State} held {launchStamp - t.SinceSim:0.00}s " +
                    $"-> round away at t+{observed:0.00}s (warmups {t.WarmupCount}){warmup}");
            }

            // What the order had to work with when issued; "0 usable" means it waited for a launcher to free up.
            if (e.LaunchersAtRegister >= 0)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] dispatch-pool {e.AmmoId} from {UnitNaming.SafeName(e.Unit)}: " +
                    $"at order time {e.UsableAtRegister}/{e.LaunchersAtRegister} launcher(s) usable, " +
                    $"round away at t+{observed:0.00}s");
        }

        /// <summary>
        /// True while an order at this target still has rounds not yet seen leaving. A launcher can take
        /// minutes to its first round (a submerged boat must rise first), and the board's grace prune would
        /// otherwise treat the target as finished and stamp the round uncoordinated.
        /// </summary>
        internal static bool HasPendingLaunch(ObjectBase target)
        {
            if (target == null) return false;
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Target == target && e.Launched < e.Requested) return true;
            }
            return false;
        }

        // Record what one coordinated order asked for, so Tick can tally its launches. Called from
        // Coordinator.Fire. Anchors register even as single shots, since their launches finalize the impact.
        internal static void RegisterExpectation(Coordinator.Intent it, Coordinator.Scheduled sched,
                                                 float impactAtRelease = -1f)
        {
            if (it.Unit == null || it.Target == null) return;
            int shots = Mathf.Max(1, it.Shots);
            bool isAnchor = sched != null && sched.IsAnchor;
            // A single shot cannot come up short, so it normally gets no expectation, except one that must
            // reach its launch envelope first: the follower case envelope-residual needs. Under VerboseLog
            // every order registers, so startup-residual can be scored on ordinary launchers too.
            if (shots <= 1 && !isAnchor && it.EnvelopeLead <= 0f && !Coordinator.VerboseLog) return;

            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            string ammoFile = it.Unit.getAmmunitionByName(it.AmmoId)?._ap?._ammunitionFileName;
            if (string.IsNullOrEmpty(ammoFile)) return; // can't attribute missiles without it

            float interval = f.ShotIntervalOrFallback;
            float reload = f.Valid ? f.ReloadGap : 0f;
            float ripple = (shots - 1) * interval + Mathf.Max(0, it.Waves - 1) * reload;
            // Reload waves launch later; allow for them so the adaptive deadline does not flag them.
            float waveTail = it.Waves > 1
                ? (it.Waves - 1) * (Mathf.Max(1, it.AnchorShots) * interval + reload)
                : 0f;

            _launchExpectations.Add(new LaunchExpectation
            {
                Unit = it.Unit,
                AmmoId = it.AmmoId,
                AmmoFile = ammoFile,
                Target = it.Target,
                Requested = shots,
                Launched = 0,
                RegisteredSim = GameClock.SimNow(),
                DeadlineSim = GameClock.SimNow() + ripple + waveTail + ExpectationMarginSim,
                Linked = isAnchor ? sched : null,
                IniInterval = interval,
                WaveTailSim = waveTail,
                EnvelopeLead = it.EnvelopeLead,
                StartupLead = it.StartupLead,
                ReleaseLeadAtRegister = it.ReleaseLead,
                ImpactAtRegister = impactAtRelease,
            });

            LogDispatchState(_launchExpectations[_launchExpectations.Count - 1], it);
        }

        /// <summary>
        /// The launchers at the instant the order was issued. `onRail` shows whether the game pre-warmed a
        /// launcher (ReadyUpWhileWaiting), spending its warm-up early; the pool shows how many launchers
        /// serve the ammo and how many pass the game's candidate filter. A busy launcher is filtered out, so
        /// a second order runs in parallel when another launcher is free and waits only when none is.
        /// </summary>
        private static void LogDispatchState(LaunchExpectation e, Coordinator.Intent it)
        {
            if (!Coordinator.VerboseLog) return;

            LauncherProbe.Collect(e.Unit, e.AmmoId, e.Target, _probeScratch);
            e.LaunchersAtRegister = _probeScratch.Count;
            int usable = 0;
            for (int i = 0; i < _probeScratch.Count; i++) if (_probeScratch[i].Usable) usable++;
            e.UsableAtRegister = usable;

            float declaredWarmup = LauncherProbe.DeclaredOnRailWarmup(e.Unit, e.AmmoId);
            float sharedInterval = LauncherProbe.DeclaredSharedInterval(e.Unit, e.AmmoId);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] dispatch {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} -> {UnitNaming.SafeName(e.Target)}: " +
                $"declared onRailWarmup {Declared(declaredWarmup)}, " +
                $"sharedLaunchInterval {Declared(sharedInterval)}, " +
                $"predicted startupLead {it.StartupLead:0.00}s | " +
                $"{LauncherProbe.Describe(_probeScratch)}");
        }

        /// <summary>
        /// A declared ini value, keeping absent distinct from zero: 0 declares no warm-up, absent means the
        /// launcher position is not in the ammo's table and the game skips the gate.
        /// </summary>
        private static string Declared(float v) => v < 0f ? "none" : $"{v:0.00}s";

        // Credit a just-launched missile to the first open order matching its ship, ammo and intended target.
        private static void CreditLaunch(WeaponBase w, ObjectBase tgt)
        {
            if (w == null) return;
            ObjectBase platform = w._launchPlatform;
            string ammoFile = w._ap != null ? w._ap._ammunitionFileName : null;
            if (platform == null || ammoFile == null) return;

            // Release one round of the order's guidance reservation. Above the empty-list return on purpose:
            // a single-shot order has no expectation in normal play, so crediting below it would leave the
            // reservation to its timeout and the ship refusing its next order.
            Coordinator.CreditReservedLaunch(platform, ammoFile);

            // With the list empty, a round arriving after the last order retired must still be reported.
            // Everything below the guard keeps its original condition, since _shipLastLaunchSim feeds the deadlines.
            if (_launchExpectations.Count == 0)
            {
                WarnUncredited(platform, ammoFile, tgt, w);
                return;
            }

            float launchStamp = GameClock.LaunchStampOr(w, GameClock.SimNow());   // one read, reused

            // Stamp ship activity first and unconditionally: a round from an untracked order still shows the
            // launcher working, which keeps a queued order from being reported short.
            _shipLastLaunchSim[new ShipAmmoKey(platform, ammoFile)] = launchStamp;

            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Launched < e.Requested && e.Unit == platform && e.Target == tgt && e.AmmoFile == ammoFile)
                {
                    e.Launched++;
                    e.LastLaunchSim = launchStamp;
                    if (e.Launched == 1) ScoreEnvelopeLead(e, launchStamp);
                    LogChannelOccupancy(platform, ammoFile, e, launchStamp);
                    // Feed the batch anchor's live impact prediction (observation anchoring).
                    if (e.Linked != null && e.Linked.IsAnchor && !e.Linked.RippleDone)
                    {
                        e.Linked.LaunchTimes.Add(launchStamp);
                        // The shooter's state as this round left the rail. Not verbose-gated:
                        // PredictAnchorImpact integrates from it, so it feeds timing.
                        Vector3 railHeading;
                        if (w.transform == null
                            || !GameMath.TryFlatDirection(w.transform.forward, out railHeading))
                            railHeading = Vector3.zero;
                        e.Linked.LaunchObs.Add(new Coordinator.LaunchObservation
                        {
                            Sim = launchStamp,
                            // The round's own position, its launcher cell; the anchor prediction integrates from here.
                            ShooterPosU = w.transform != null ? w.transform.position
                                        : platform.transform != null ? platform.transform.position
                                        : Vector3.zero,
                            ShooterVelKn = platform._velocityInKnots,
                            // The round's own heading on the rail is the launch bearing.
                            RailHeadingU = railHeading,
                            Round = w,
                        });
                    }
                    return;
                }
            }

            // No match is normal: expectations exist only for anchors and multi-shot orders. Only a launch
            // from a ship with an open order for this ammo at another target counts as misattribution.
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Launched < e.Requested && e.Unit == platform && e.AmmoFile == ammoFile)
                {
                    UncreditedLaunches++;
                    return;
                }
            }

            WarnUncredited(platform, ammoFile, tgt, w);
        }

        /// <summary>
        /// The guiding sensor's live occupancy as each round of a channel-limited order leaves, beside the
        /// rounds still to come: the launch side of the channel check, whose intake side the coordinator
        /// logs. Silent for ammunition that is not channel-limited.
        /// </summary>
        private static void LogChannelOccupancy(ObjectBase platform, string ammoFile,
                                                LaunchExpectation e, float launchStamp)
        {
            if (!Coordinator.VerboseLog) return;
            string occ = LauncherFactsSource.GuidanceOccupancyText(platform, ammoFile);
            if (string.IsNullOrEmpty(occ)) return;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] channels-launch {ammoFile} from {UnitNaming.SafeName(platform)} at sim " +
                $"{launchStamp:0.0}: round {e.Launched} of {e.Requested} away, {occ}.");
        }

        /// <summary>
        /// Warn about a round at a coordinated target that matched no order: the coordinator does not know it
        /// exists, so the shared impact is being timed without it.
        /// </summary>
        private static void WarnUncredited(ObjectBase platform, string ammoFile, ObjectBase tgt,
                                           WeaponBase w)
        {
            if (tgt == null || !EngagementBoard.IsCoordinated(tgt)) return;
            bool haveRetired = _retiredExpectations.TryGetValue(new ShipAmmoKey(platform, ammoFile),
                                                                out RetiredExpectation r);
            if (haveRetired && TryCreditLate(r, platform, ammoFile, tgt, w)) return;

            if (!Coordinator.VerboseLog) return;
            float launchStamp = GameClock.LaunchStamp(w);
            string late = haveRetired
                ? $"its order retired {launchStamp - r.Sim:0.0}s earlier at {r.Launched}/{r.Requested}"
                : "no order for this ship and ammo was ever registered";
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] launch-uncredited {ammoFile} from {UnitNaming.SafeName(platform)} -> {UnitNaming.SafeName(tgt)} " +
                $"at sim {launchStamp:0.0}: this round matched no open order, so nothing counts it. " +
                $"{late}. The anchor will under-report its ripple and the shared impact will not be " +
                $"corrected for this round.");
        }

        /// <summary>
        /// Credit a round that left after its order's window closed (an aircraft may need a climb before the
        /// game allows the shot). Bounded by state, not time: only while the linked anchor still tracks its
        /// ripple, since a finalized impact may already have followers timed against it.
        /// </summary>
        private static bool TryCreditLate(RetiredExpectation r, ObjectBase platform, string ammoFile,
                                          ObjectBase tgt, WeaponBase w)
        {
            Coordinator.Scheduled a = r.Linked;
            // Once the anchor has finalized, this record can never credit anything and it pins a Scheduled; drop it.
            if (a == null || !a.IsAnchor || a.RippleDone)
            {
                if (a != null && a.RippleDone)
                    _retiredExpectations.Remove(new ShipAmmoKey(platform, ammoFile));
                return false;
            }
            if (r.Target != tgt) return false;
            if (a.LaunchTimes.Count >= Mathf.Max(1, a.AnchorShots)) return false;

            // _shipLastLaunchSim is deliberately not stamped here, since that would extend other orders' deadlines.
            float launchStamp = GameClock.LaunchStampOr(w, GameClock.SimNow());
            a.LaunchTimes.Add(launchStamp);
            Vector3 railHeading;
            if (w.transform == null || !GameMath.TryFlatDirection(w.transform.forward, out railHeading))
                railHeading = Vector3.zero;
            a.LaunchObs.Add(new Coordinator.LaunchObservation
            {
                Sim = launchStamp,
                // The round's own position, its launcher cell; see the other LaunchObservation above.
                ShooterPosU = w.transform != null ? w.transform.position
                            : platform.transform != null ? platform.transform.position : Vector3.zero,
                ShooterVelKn = platform._velocityInKnots,
                RailHeadingU = railHeading,
                Round = w,
            });

            if (Coordinator.VerboseLog)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] launch-credited-late {r.AmmoId} from {UnitNaming.SafeName(platform)} -> " +
                    $"{UnitNaming.SafeName(tgt)} at sim {launchStamp:0.0}: arrived {launchStamp - r.Sim:0.0}s " +
                    $"after its order retired at {r.Launched}/{r.Requested}, and the anchor is still " +
                    $"tracking, so it counts. Ripple now {a.LaunchTimes.Count}/" +
                    $"{Mathf.Max(1, a.AnchorShots)}.");
            return true;
        }

        /// <summary>An order that closed, kept only so <c>launch-uncredited</c> can say how narrowly
        /// a late round missed its window.</summary>
        private struct RetiredExpectation
        {
            public float Sim;
            public int Launched;
            public int Requested;
            public ObjectBase Target;
            public string AmmoId;
            public Coordinator.Scheduled Linked;   // the anchor entry, for late crediting (fix E)
        }

        private static readonly Dictionary<ShipAmmoKey, RetiredExpectation> _retiredExpectations =
            new Dictionary<ShipAmmoKey, RetiredExpectation>();

        /// <summary>Retired-order entries kept before the map is cleared; one per ship and ammo.</summary>
        private const int RetiredExpectationCap = 64;

        /// <summary>
        /// The delay terms an order was released on, for the corpus writer, so the delay model can be scored
        /// offline. Matched as <see cref="CreditLaunch"/> matches: unit, target, ammunition file, first open
        /// order. False when nothing matches, which is normal for an uncoordinated round.
        /// </summary>
        internal static bool TryGetDelayTerms(ObjectBase unit, string ammoFile, ObjectBase target,
                                              out float registeredSim, out float startupLead,
                                              out float envelopeLead, out float releaseLead,
                                              out float impactAtSim, out bool isAnchor)
        {
            registeredSim = -1f; startupLead = -1f; envelopeLead = -1f;
            releaseLead = -1f; impactAtSim = -1f; isAnchor = false;
            if (unit == null || ammoFile == null) return false;
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Unit != unit || e.Target != target || e.AmmoFile != ammoFile) continue;
                registeredSim = e.RegisteredSim;
                startupLead = e.StartupLead;
                envelopeLead = e.EnvelopeLead;
                releaseLead = e.ReleaseLeadAtRegister;
                impactAtSim = e.ImpactAtRegister;
                isAnchor = e.Linked != null && e.Linked.IsAnchor;
                // An anchor's impact at launch is its live prediction rather than its scheduled one; prefer that.
                if (e.Linked != null) impactAtSim = e.Linked.ImpactAtSim;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Order-to-first-round residual for a follower: what the launcher took against the prediction.
        /// <c>envelope-residual</c> when the platform had to reach its launch envelope,
        /// <c>startup-residual</c> for an ordinary mount (<see cref="Facts.StartupDelay"/>). Positive =
        /// slower than predicted. Anchors are scored in the anchor-tracking loop; a follower's error lands
        /// directly on its arrival.
        /// </summary>
        private static void ScoreEnvelopeLead(LaunchExpectation e, float launchStamp)
        {
            if (!Coordinator.VerboseLog) return;
            if (e.Linked != null && e.Linked.IsAnchor) return;   // the anchor path already logs it

            float observed = launchStamp - e.RegisteredSim;
            LogRoundAway(e, launchStamp, observed);
            if (e.EnvelopeLead > 0f)
            {
                // EnvelopeLead is the transit alone; the launcher cycle is in StartupDelay and reported separately.
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-residual {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} (follower): " +
                    $"observed {observed:0.0}s, predicted {e.StartupLead:0.0}s " +
                    $"(transit {e.EnvelopeLead:0.0}s " +
                    $"+ launcher cycle {LauncherFactsSource.LauncherCycleSeconds(e.Unit, e.AmmoId):0.0}s), " +
                    $"residual {observed - e.StartupLead:+0.0;-0.0}s");
                return;
            }

            // Ordinary launcher. Printed even when the prediction is 0.0, so a zero is not mistaken for a
            // model that did not run.
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] startup-residual {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} (follower): " +
                $"observed {observed:0.0}s, predicted {e.StartupLead:0.0}s " +
                $"(launcher cycle {LauncherFactsSource.LauncherCycleSeconds(e.Unit, e.AmmoId):0.0}s), " +
                $"residual {observed - e.StartupLead:+0.0;-0.0}s");
        }

        // Close out orders whose window has passed. A shortfall against a live target is a warning; one whose
        // target died mid-salvo is info.
        private static void FinalizeExpectations(float simNow)
        {
            if (_launchExpectations.Count == 0) return;
            for (int i = _launchExpectations.Count - 1; i >= 0; i--)
            {
                LaunchExpectation e = _launchExpectations[i];

                // Adaptive deadline: every launch extends the window, since realized cadence is often far
                // slower than the ini pace. Only no launch for max(4x measured cadence, 30 s), plus any
                // reload-wave tail, is a shortfall.
                if (e.Launched < e.Requested)
                {
                    float interval = e.IniInterval;
                    List<float> lt = e.Linked?.LaunchTimes;
                    if (lt != null && lt.Count >= 2)
                        interval = (lt[lt.Count - 1] - lt[0]) / (lt.Count - 1);
                    if (interval <= 0f) interval = LauncherFactsSource.FallbackShotInterval;

                    // Reference point: this order's last launch, else the ship's last launch of this ammo,
                    // else the order time. A ship still cycling its launcher is not stalled, and an order
                    // queued behind others is not short.
                    float since = e.LastLaunchSim;
                    if (since < 0f)
                    {
                        if (!_shipLastLaunchSim.TryGetValue(new ShipAmmoKey(e.Unit, e.AmmoFile), out since))
                            since = e.RegisteredSim;
                        else if (since < e.RegisteredSim) since = e.RegisteredSim;
                    }
                    float adaptive = since
                    + Mathf.Max(Coordinator.StallCadenceMultiplier * interval, Coordinator.StallMinWindowSim)
                    + e.WaveTailSim;

                    // A shooter mid launch cycle, or a submarine still rising, can outlast the window,
                    // mid-ripple as well as before the first round. Hold the deadline while that is true,
                    // bounded from the order so a parked boat still reports.
                    if (e.Unit != null && !e.Unit.IsDestroyed &&
                        simNow - e.RegisteredSim < Coordinator.NoLaunchMaxHoldSim &&
                        Coordinator.ShooterStillWorking(e.Unit, e.AmmoId))
                        adaptive = simNow + Mathf.Max(interval, Coordinator.StallMinWindowSim);

                    if (adaptive > e.DeadlineSim) e.DeadlineSim = adaptive;
                }

                bool done = e.Launched >= e.Requested;
                if (!done && simNow < e.DeadlineSim) continue;

                // Remember the order, keyed by ship and ammo, so a round arriving after its window can be reported.
                _retiredExpectations[new ShipAmmoKey(e.Unit, e.AmmoFile)] =
                    new RetiredExpectation { Sim = simNow, Launched = e.Launched,
                                             Requested = e.Requested, Target = e.Target,
                                             AmmoId = e.AmmoId, Linked = e.Linked };
                _launchExpectations.RemoveAt(i);
                // Traces carry over to the next order on a launcher, but are kept only while some order is open.
                if (_launchExpectations.Count == 0) _launcherTraces.Clear();
                // Bounded like the traces: each entry holds a target reference.
                if (_retiredExpectations.Count > RetiredExpectationCap) _retiredExpectations.Clear();
                if (e.Launched >= e.Requested)
                {
                    if (Coordinator.VerboseLog)
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] order complete {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} -> {UnitNaming.SafeName(e.Target)}: " +
                            $"launched {e.Launched}/{e.Requested}.");
                    continue;
                }

                bool targetGone = e.Target == null || e.Target.IsDestroyed;
                bool shooterGone = e.Unit == null || e.Unit.IsDestroyed;
                int ready = 0, reserve = 0, inv = 0;
                if (!shooterGone)
                {
                    LauncherFactsSource.Facts f = LauncherFactsSource.Get(e.Unit, e.AmmoId);
                    ready = f.ReadyRounds; reserve = f.Reserve;
                    e.Unit.AmmunitionAmountDictionary.TryGetValue(e.AmmoId, out inv);
                }
                string quiet = "never";
                if (_shipLastLaunchSim.TryGetValue(new ShipAmmoKey(e.Unit, e.AmmoFile), out float shipLast))
                    quiet = $"{simNow - shipLast:0.0}s ago";
                string detail =
                    $"launched {e.Launched}/{e.Requested}, ready {ready}, reserve {reserve}, inventory {inv}, " +
                    $"ship last fired this ammo {quiet}, targetGone {targetGone}, shooterGone {shooterGone}" +
                    // Unconditional: SHORTFALL is the line users send in, and a submarine's depth state is
                    // the explanation.
                    (shooterGone ? "" : SubmarineFacts.Describe(e.Unit, e.AmmoId)) +
                    // Likewise for grouped ammo: rounds still aboard with "ready N" are explained by a full group.
                    (shooterGone ? "" : LauncherFactsSource.DescribeGroups(e.Unit, e.AmmoId));

                if (targetGone || shooterGone)
                {
                    if (Coordinator.VerboseLog)
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] order ended early {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} -> {UnitNaming.SafeName(e.Target)}: {detail}");
                }
                else
                {
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] SHORTFALL {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} -> {UnitNaming.SafeName(e.Target)}: {detail}");
                }
            }
        }
    }
}
