using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Shot-count accounting: what one coordinated order asked for, what actually left the rail,
    /// and the shortfall line when those disagree. This is the half that feeds observation
    /// anchoring back to the coordinator.
    ///
    /// Split out of LaunchDiagnostics.cs for size only.
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        // Per-ship shot-count accounting
        // Records, per coordinated order, how many missiles were requested vs. how many actually
        // left the rail. Sim-time deadlined so time compression / pause don't produce false
        // shortfalls.
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
            public float RegisteredSim;       // when the order was issued, the reference point for an
                                              // order that has not launched anything yet
            public float EnvelopeLead;        // predicted order-to-first-round transit into the launch
                                              // envelope (submarine ascent, aircraft descent); 0 = none
            public float StartupLead;         // full predicted order-to-first-round offset, launcher
                                              // startup plus EnvelopeLead
            public float LastEngageSampleSim = float.NegativeInfinity;

            // D0: one trace per LAUNCHER OBJECT, not one merged string per ship and ammo.
            // SubmarineFacts.EngageStates joins the distinct states of every launcher serving
            // the ammo with "+", so on a two-launcher ship "held 5.00s" only ever meant "the
            // composite string did not change", which is not the same statement as one launcher
            // holding one state. Every quantity these diagnostics measure belongs to one object.
            //
            // These are REFERENCES into _launcherTraces, shared with any other order the same
            // launcher is serving. Per-order copies logged every transition twice, once per order,
            // and each copy's warm-up counter included the other order's cycles.
            public List<LauncherTrace> Launchers;

            // D4: the candidate pool at the moment the order was issued, kept so the round-away
            // line can say whether the order had a free launcher to go to or had to wait for one.
            public int UsableAtRegister = -1;
            public int LaunchersAtRegister = -1;
        }

        private static readonly List<LaunchExpectation> _launchExpectations = new List<LaunchExpectation>();

        // Last observed launch per (ship, ammo file), updated for EVERY sighting including rounds that
        // match no open order. A ship working through a queue of engage tasks is not stalled, and an
        // order sitting behind others in that queue must not be reported short while rounds are still
        // leaving. Keyed by instance id and ammo file rather than by ObjectBase, so a destroyed
        // shooter cannot keep the entry alive.
        private static readonly Dictionary<ShipAmmoKey, float> _shipLastLaunchSim =
            new Dictionary<ShipAmmoKey, float>();

        /// <summary>
        /// (firing ship, ammo file) identity for <see cref="_shipLastLaunchSim"/>. A struct rather
        /// than a concatenated string because FinalizeExpectations looks this up once per open order
        /// per tick, and a string key allocates on every one of those. Same pattern as
        /// <c>FlightTime.TofKey</c> and <c>LauncherFactsSource.FactsKey</c>. Keyed on the instance
        /// id, not the ObjectBase, so a destroyed shooter cannot keep the entry alive.
        /// </summary>
        private struct ShipAmmoKey : System.IEquatable<ShipAmmoKey>
        {
            public int UnitId;
            public string AmmoFile;
            public ShipAmmoKey(ObjectBase unit, string ammoFile)
            {
                UnitId = unit != null ? unit.GetInstanceID() : 0;
                AmmoFile = ammoFile;
            }
            public bool Equals(ShipAmmoKey o) => UnitId == o.UnitId && AmmoFile == o.AmmoFile;
            public override bool Equals(object obj) => obj is ShipAmmoKey k && Equals(k);
            public override int GetHashCode()
            { unchecked { return (UnitId * 397) ^ (AmmoFile?.GetHashCode() ?? 0); } }
        }

        // Missiles seen leaving that matched no open order. Non-zero means orders are being credited
        // to the wrong place, or expectations are being retired too early.
        internal static int UncreditedLaunches;

        private const float ExpectationMarginSim = 10f; // slack (sim s) beyond the computed ripple time

        private const float HitRangeM = 500f;           // closer than this when a missile vanishes => counted as a HIT

        /// <summary>
        /// D1: the declared warm-up against the one this launcher actually sat through, plus the
        /// state each launcher was in when the round left.
        ///
        /// D1 is the kill criterion for the whole implementation plan. If declared and observed
        /// disagree, the 5.00 s comes from somewhere other than the INI field and every proposed
        /// change built on it is void. Reported per launcher object, because a merged string cannot
        /// attribute a hold to the launcher that paid it.
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

            // D4: what the order had to work with when it was issued. "0 usable" means it could not
            // be dispatched at all that tick and waited for a launcher to free up.
            if (e.LaunchersAtRegister >= 0)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] dispatch-pool {e.AmmoId} from {UnitNaming.SafeName(e.Unit)}: " +
                    $"at order time {e.UsableAtRegister}/{e.LaunchersAtRegister} launcher(s) usable, " +
                    $"round away at t+{observed:0.00}s");
        }

        /// <summary>
        /// True while an order issued at this target still has rounds that have not been seen
        /// leaving. A launcher can take a long time between the order and its first round: a
        /// submerged boat ordered up from 400 ft took 111 s. During that window the target has a
        /// fired row, nothing queued and nothing in flight, which is exactly what the engagement
        /// board's grace prune treats as finished. Pruning there is wrong twice over: the round
        /// vanishes from the HUD while it is still coming, and it is stamped uncoordinated at first
        /// sighting, which silently drops every per-missile diagnostic for it.
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

        // Record what one coordinated order asked for, so Tick can tally how many of its missiles
        // actually launch. Called from Coordinator.Fire after the order is issued. Anchors are
        // registered even as single shots ; their observed launches finalize the batch impact.
        internal static void RegisterExpectation(Coordinator.Intent it, Coordinator.Scheduled sched)
        {
            if (it.Unit == null || it.Target == null) return;
            int shots = Mathf.Max(1, it.Shots);
            bool isAnchor = sched != null && sched.IsAnchor;
            // A single shot cannot come up "short", so it is normally not worth an expectation. The
            // exception is a shot that must first climb or dive into its launch envelope: the
            // follower case is precisely where nothing corrects the envelope estimate, so it is the
            // case that most needs scoring, and it was silent because envelope-residual is emitted
            // from the anchor-tracking loop only.
            // Under VerboseLog every order is registered, so the order-to-first-round residual
            // below can be scored on ordinary launchers too. That is research instrumentation and
            // costs a list entry per order, so normal play keeps the original rule.
            if (shots <= 1 && !isAnchor && it.EnvelopeLead <= 0f && !Coordinator.VerboseLog) return;

            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            string ammoFile = it.Unit.getAmmunitionByName(it.AmmoId)?._ap?._ammunitionFileName;
            if (string.IsNullOrEmpty(ammoFile)) return; // can't attribute missiles without it

            float interval = (f.Valid && f.ShotInterval > 0f) ? f.ShotInterval : LauncherFactsSource.FallbackShotInterval;
            float reload = f.Valid ? f.ReloadGap : 0f;
            float ripple = (shots - 1) * interval + Mathf.Max(0, it.Waves - 1) * reload;
            // Reload waves after wave 1 launch AnchorShots*interval + reload later each; allow for
            // them up front so the adaptive deadline below doesn't flag them as a shortfall.
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
            });

            LogDispatchState(_launchExpectations[_launchExpectations.Count - 1], it);
        }

        /// <summary>
        /// D2 and D4: what the launchers looked like at the instant the order was issued.
        ///
        /// D2, the pre-warm question, is the one that decides whether the fix is a win at all. The
        /// game pre-warms a launcher through ReadyUpWhileWaiting while it idles aimed at the target,
        /// so a launcher can arrive at the engage task with its warm-up already spent. Adding the
        /// declared warm-up unconditionally would then release EARLY, which is a different error
        /// rather than a smaller one. `onRail` here is that answer, per launcher object.
        ///
        /// D4 is the pool: how many launchers serve this ammo and how many pass the game's own
        /// candidate filter right now. A busy launcher is filtered OUT rather than queued behind, so
        /// a second order runs in PARALLEL when another launcher is free and serialises only when
        /// none is. "0 usable" is the wait-for-a-free-launcher case and is worth seeing directly.
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
        /// Renders a declared INI value, keeping "absent" distinct from "zero". The difference is
        /// load-bearing: 0 means the weapon declares no warm-up, absent means this launcher position
        /// is not in the ammo's table at all and the game skips the gate entirely.
        /// </summary>
        private static string Declared(float v) => v < 0f ? "none" : $"{v:0.00}s";

        // Credit a just-launched missile to the first still-open order that matches its firing
        // ship, ammo, and intended target.
        private static void CreditLaunch(WeaponBase w, ObjectBase tgt)
        {
            if (w == null) return;
            ObjectBase platform = w._launchPlatform;
            string ammoFile = w._ap != null ? w._ap._ammunitionFileName : null;
            if (platform == null || ammoFile == null) return;

            // The empty-list case is the one that matters for D5 and it used to return before any of
            // this. When an order is the LAST one open, its retirement empties the list, so a round
            // arriving afterwards was dropped at the first line and never reported. That is exactly
            // the 2026-09-07d run: two rounds flew after the F/A-18's order retired and
            // launch-uncredited printed nothing.
            //
            // Everything below the guard stays behind the original condition, deliberately. The
            // _shipLastLaunchSim stamp feeds the deadline calculation, so recording it on a path that
            // never recorded it before would be a behaviour change in a diagnostics-only build.
            if (_launchExpectations.Count == 0)
            {
                WarnUncredited(platform, ammoFile, tgt, w);
                return;
            }

            float launchStamp = GameClock.LaunchStamp(w);   // one reflection read, reused

            // Stamp SHIP activity first, and unconditionally. This round proves the launcher is still
            // working even if it belongs to no order we are tracking, which is what keeps a queued
            // order from being reported short.
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
                        // The shooter's full state at the instant this round left the rail. Not
                        // verbose-gated: PredictAnchorImpact re-runs the integrator from this
                        // snapshot rather than from the platform's current position, so the record
                        // feeds a timing decision and not only the diagnostics. Captured here
                        // because this is the one moment the launch state exists.
                        Vector3 railHeading;
                        if (w.transform == null
                            || !GameMath.TryFlatDirection(w.transform.forward, out railHeading))
                            railHeading = Vector3.zero;
                        e.Linked.LaunchObs.Add(new Coordinator.LaunchObservation
                        {
                            Sim = launchStamp,
                            ShooterPosU = platform.transform != null
                                ? platform.transform.position : Vector3.zero,
                            ShooterVelKn = platform._velocityInKnots,
                            // The round's own heading on the rail IS the launch bearing, and it is
                            // the geometry the off-boresight defect turns on. Read from the round
                            // rather than the launcher because the round is in hand here.
                            RailHeadingU = railHeading,
                            Round = w,
                        });
                    }
                    return;
                }
            }

            // No expectation matched. That is NORMAL for most rounds: expectations only exist for
            // anchors and multi-shot orders, so a salvo of single-shot orders has nothing to match
            // against and counting those made the figure meaningless (74 on a clean run).
            // Only a launch from a ship that DOES have an open order for this ammo, but at another
            // target, indicates real misattribution. That is the case worth counting.
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
        /// D3 of docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md: the guiding sensor's live occupancy as
        /// each round of a channel-limited order leaves the rail, beside how many of that order's
        /// rounds are still to come.
        ///
        /// The reservation the coordinator holds is released the moment an order is DISPATCHED, but
        /// the sensor channels it needs fill one by one as the rounds LAUNCH. This line is the launch
        /// side of that comparison; the intake side is logged by the coordinator when it decides
        /// whether a new order fits. Silent for ammunition that is not channel-limited.
        /// </summary>
        private static void LogChannelOccupancy(ObjectBase platform, string ammoFile,
                                                LaunchExpectation e, float launchStamp)
        {
            if (!Coordinator.VerboseLog) return;
            string occ = LauncherFactsSource.GuidanceOccupancyText(platform, ammoFile);
            if (string.IsNullOrEmpty(occ)) return;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] channels-launch {ammoFile} from {UnitNaming.SafeName(platform)} at sim " +
                $"{launchStamp:0.0}: round {e.Launched} of {e.Requested} away, {occ}. " +
                $"D3 of the beta-release audit.");
        }

        /// <summary>
        /// D5 of docs/plans/open/anchor-liveness.md. A round that belongs to a coordinated target and
        /// matches NOTHING used to be dropped in silence, and that silence is the whole defect: the
        /// F/A-18's expectation timed out at about t+45s, its rounds left at t+55 to t+60, and with
        /// no record to credit them to, the anchor reported "NOTHING launched" for the rest of the
        /// engagement while both missiles were in the air. The shared impact was then never
        /// corrected, because every correction path is gated on having observed a launch.
        ///
        /// A warning rather than an info line because it is never benign: a round exists that the
        /// coordinator does not know about, and the strike is being timed without it.
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
        /// Fix E of docs/plans/open/anchor-liveness.md: credit a round that left AFTER its order's
        /// window closed.
        ///
        /// An order's deadline is <c>max(4 x cadence, 30s)</c>, which for a 1.0s modelled cadence is
        /// the 30s floor. A strike aircraft at 37,000ft takes 55 to 60s to get a round away, because
        /// the engagement triggers a climb and the game refuses the shot while its pitch exceeds 10
        /// degrees. The order therefore retires before the round exists, and the round was then
        /// dropped in silence. Every path that can correct the shared impact is gated on having
        /// observed a launch, so the strike stayed timed against a value no round would make. Five
        /// such rounds were recorded across the 2026-09-07 sessions.
        ///
        /// The bound is a STATE, not a time: credit only while the linked anchor is still tracking
        /// its ripple. Once it has finalised, its impact is published and the followers are timed
        /// against it, so a late arrival cannot be folded in without moving a value other rounds have
        /// already committed to. That is the discontinuity the impact-slide plan exists to prevent.
        /// </summary>
        private static bool TryCreditLate(RetiredExpectation r, ObjectBase platform, string ammoFile,
                                          ObjectBase tgt, WeaponBase w)
        {
            Coordinator.Scheduled a = r.Linked;
            // Once the anchor has finalised the record can never credit anything again, and it pins
            // a Scheduled (and through it every WeaponBase in LaunchObs). Drop it here rather than
            // waiting for the 64-entry cap or a mission reset.
            if (a == null || !a.IsAnchor || a.RippleDone)
            {
                if (a != null && a.RippleDone)
                    _retiredExpectations.Remove(new ShipAmmoKey(platform, ammoFile));
                return false;
            }
            if (r.Target != tgt) return false;
            if (a.LaunchTimes.Count >= Mathf.Max(1, a.AnchorShots)) return false;

            // Deliberately NOT stamping _shipLastLaunchSim here. That value feeds the deadline
            // calculation for orders still open, and this path never wrote it before; adding a write
            // would extend other orders' windows as a side effect of a bookkeeping fix.
            float launchStamp = GameClock.LaunchStamp(w);
            a.LaunchTimes.Add(launchStamp);
            Vector3 railHeading;
            if (w.transform == null || !GameMath.TryFlatDirection(w.transform.forward, out railHeading))
                railHeading = Vector3.zero;
            a.LaunchObs.Add(new Coordinator.LaunchObservation
            {
                Sim = launchStamp,
                ShooterPosU = platform.transform != null ? platform.transform.position : Vector3.zero,
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
        /// a late round missed its window. D5 of docs/plans/open/anchor-liveness.md.</summary>
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

        /// <summary>Entries kept before the retired-order map is dropped wholesale. One per ship and
        /// ammo, so this is only reached by a very long mission.</summary>
        private const int RetiredExpectationCap = 64;

        /// <summary>
        /// Order-to-first-round residual for a FOLLOWER: what the launcher actually took against
        /// what was predicted at commit. Two flavours, one per model: <c>envelope-residual</c> when
        /// the platform had to climb or dive into its launch envelope, <c>startup-residual</c> for
        /// an ordinary mount, whose whole prediction is <see cref="Facts.StartupDelay"/>. Positive means the platform took longer than
        /// predicted. Anchors are scored in the anchor-tracking loop instead, which also has the
        /// per-step envelope-track trace; a follower is out of that loop the moment it releases, so
        /// this is the only place its model can be read against reality. It is also the case that
        /// matters most: an anchor's real launch rewrites the shared impact, a follower's does not,
        /// so a follower's envelope error lands directly on the arrival time.
        /// </summary>
        private static void ScoreEnvelopeLead(LaunchExpectation e, float launchStamp)
        {
            if (!Coordinator.VerboseLog) return;
            if (e.Linked != null && e.Linked.IsAnchor) return;   // the anchor path already logs it

            float observed = launchStamp - e.RegisteredSim;
            LogRoundAway(e, launchStamp, observed);
            if (e.EnvelopeLead > 0f)
            {
                // EnvelopeLead is the transit alone now; the launcher cycle it used to carry moved
                // into StartupDelay, so it is reported here as the separate term it became.
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-residual {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} (follower): " +
                    $"observed {observed:0.0}s, predicted {e.StartupLead:0.0}s " +
                    $"(transit {e.EnvelopeLead:0.0}s " +
                    $"+ launcher cycle {LauncherFactsSource.LauncherCycleSeconds(e.Unit, e.AmmoId):0.0}s), " +
                    $"residual {observed - e.StartupLead:+0.0;-0.0}s");
                return;
            }

            // Ordinary launcher, no envelope transit. PreLaunchDelay and the reaction draw are 0 on
            // several mounts, so before the launcher cycle was modelled this line reported seconds
            // observed against a predicted 0.0s. It lands on a follower's arrival in full, so it is
            // worth the same scrutiny the envelope models got. Printed even when predicted is 0.0:
            // a zero that logs nothing cannot be told from a model that declined to run.
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] startup-residual {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} (follower): " +
                $"observed {observed:0.0}s, predicted {e.StartupLead:0.0}s " +
                $"(launcher cycle {LauncherFactsSource.LauncherCycleSeconds(e.Unit, e.AmmoId):0.0}s), " +
                $"residual {observed - e.StartupLead:+0.0;-0.0}s");
        }

        // Close out orders whose ripple window has elapsed. A shortfall against a live target is a
        // real anomaly (WARN); a shortfall when the target died mid-salvo is expected (info).
        private static void FinalizeExpectations(float simNow)
        {
            if (_launchExpectations.Count == 0) return;
            for (int i = _launchExpectations.Count - 1; i >= 0; i--)
            {
                LaunchExpectation e = _launchExpectations[i];

                // Adaptive deadline: every observed launch extends the window, because a launcher's
                // REALIZED cadence (hatch cycles, task reassignment) is often far slower than its INI
                // pace ; a fixed deadline fired mid-ripple and logged false SHORTFALLs. Only a
                // true stall (no launch for max(4x measured cadence, 30s), plus any reload-wave
                // tail) counts as a shortfall now.
                if (e.Launched < e.Requested)
                {
                    float interval = e.IniInterval;
                    List<float> lt = e.Linked?.LaunchTimes;
                    if (lt != null && lt.Count >= 2)
                        interval = (lt[lt.Count - 1] - lt[0]) / (lt.Count - 1);
                    if (interval <= 0f) interval = LauncherFactsSource.FallbackShotInterval;

                    // Reference point: this order's own last launch if it has started, otherwise the
                    // SHIP's last launch of this ammo, otherwise when the order was issued. A ship
                    // still cycling its launcher is not stalled, and an order queued behind others is
                    // not short -- it has not had its turn. Before this, a one-shot order got 10s and
                    // no extension at all, so a ship handed several targets reported false shortfalls
                    // for the tail of its queue while the rounds were still going out.
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

                    // A shooter may be legitimately mid launch cycle or, for a submarine, still
                    // rising to its launch depth. Both routinely outlast the window above: an Oscar
                    // ordered up from 350ft needed ~70s of ascent and then over a minute opening 24
                    // hatches, and reported a SHORTFALL of 0/24 while doing exactly what it was told.
                    // Hold the deadline off while that is true, bounded absolutely from the order so
                    // a boat parked at a depth it will not leave still reports.
                    //
                    // This applies mid-ripple too, not only before the first round. A Kynda's gap
                    // between SS-N-3b pairs is far longer than StallMinWindowSim, so a launched > 0
                    // order was closed out at 2/8 and the remaining 6 rounds, already on their way,
                    // were never credited.
                    if (e.Unit != null && !e.Unit.IsDestroyed &&
                        simNow - e.RegisteredSim < Coordinator.NoLaunchMaxHoldSim &&
                        Coordinator.ShooterStillWorking(e.Unit, e.AmmoId))
                        adaptive = simNow + Mathf.Max(interval, Coordinator.StallMinWindowSim);

                    if (adaptive > e.DeadlineSim) e.DeadlineSim = adaptive;
                }

                bool done = e.Launched >= e.Requested;
                if (!done && simNow < e.DeadlineSim) continue;

                // D5 of docs/plans/open/anchor-liveness.md: remember that this order existed, so a
                // round arriving after its window closed can be reported against it rather than
                // vanishing. Keyed by ship and ammo, which is all a late round can be matched on.
                _retiredExpectations[new ShipAmmoKey(e.Unit, e.AmmoFile)] =
                    new RetiredExpectation { Sim = simNow, Launched = e.Launched,
                                             Requested = e.Requested, Target = e.Target,
                                             AmmoId = e.AmmoId, Linked = e.Linked };
                _launchExpectations.RemoveAt(i);
                // Traces outlive one order deliberately, because a launcher's cycle carries over to
                // the next order queued behind it. They are only worth keeping while some order is
                // open, so the last one out drops them all rather than each order trying to work out
                // whether a launcher it shared is still wanted.
                if (_launchExpectations.Count == 0) _launcherTraces.Clear();
                // Bounded the same way the traces are: one entry per ship and ammo is small, but it
                // holds a target reference, so it must not outlive the orders it describes by much.
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
                    // Unconditional, not VerboseLog-gated: SHORTFALL is the line users send in, and
                    // for a submarine shooter the depth state is the whole explanation.
                    (shooterGone ? "" : SubmarineFacts.Describe(e.Unit, e.AmmoId)) +
                    // Likewise for grouped ammo: "ready N, reserve 0" with rounds still aboard reads
                    // as a mod bug until you can see that the group those rounds would join is full.
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
