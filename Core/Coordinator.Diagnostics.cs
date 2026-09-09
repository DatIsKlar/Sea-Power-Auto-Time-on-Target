using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The coordinator's log lines and the small text helpers that build them. Nothing here decides
    /// anything: every method is called for its side effect on the log, or returns a string.
    ///
    /// Split out of Coordinator.cs for size only. Most of these are gated behind
    /// <see cref="Coordinator.VerboseLog"/> or <see cref="Coordinator.TraceFlightModel"/>.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// D11 of docs/plans/open/anchor-liveness.md: what the launch-state snapshot was actually
        /// built from, and what it produced against the live estimate for the same shot.
        ///
        /// This estimator feeds the shared impact and had no trace of any kind. On 2026-09-07e it
        /// returned 1218.9s for a round that flew 676.1s and moved a whole strike 548s late, and the
        /// only way to see that was to invert the arithmetic of the anchored line afterwards.
        ///
        /// One line per round, since the value is memoised on the observation and computed once.
        /// </summary>
        private static void LogLaunchSnapshot(LaunchObservation o, Intent it, float est, float liveEst)
        {
            if (!VerboseLog) return;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] launch-snapshot {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}]: est {est:0.0}s vs live {liveEst:0.0}s " +
                $"({(liveEst > 0f ? est / liveEst : 0f):0.00}x) | {LaunchSnapshotText(o, it)}");
        }

        /// <summary>
        /// The recorded launch state in readable form. The rail heading's FLAT MAGNITUDE is the value
        /// under suspicion: it is taken from the round's forward vector at first sighting, and an
        /// air-dropped weapon's first stage is DropFromAircraft, so a round still pointing steeply
        /// down yields a tiny horizontal component whose direction is then almost arbitrary. The
        /// integrator flies that bearing for the whole initial flight phase before turning onto the
        /// target.
        /// </summary>
        private static string LaunchSnapshotText(LaunchObservation o, Intent it)
        {
            float railFlat = new Vector2(o.RailHeadingU.x, o.RailHeadingU.z).magnitude;
            string bearing = "none";
            if (railFlat > 1e-4f)
            {
                float az = Mathf.Atan2(o.RailHeadingU.x, o.RailHeadingU.z) * Mathf.Rad2Deg;
                if (az < 0f) az += 360f;
                bearing = $"{az:0}deg";
            }
            string rangeTxt = "n/a";
            try
            {
                if (it.Target != null && it.Target.transform != null)
                    rangeTxt = ((it.Target.transform.position - o.ShooterPosU).magnitude
                                * GameUnits.MetersPerUnity / 1000f).ToString("0.0") + "km";
            }
            catch { }
            return $"launched sim {o.Sim:0.0}, alt {o.ShooterPosU.y * GameUnits.MetersPerUnity:0}m, " +
                   $"spd {o.ShooterVelKn:0}kn, railHeading {bearing} (flat {railFlat:0.000}), " +
                   $"range at launch {rangeTxt}";
        }

        /// <summary>
        /// D1, D2 and D4 of docs/plans/open/aircraft-anchor-impact-slide.md: the shared impact
        /// prediction every time it MOVES, decomposed into the two terms that produce it.
        ///
        /// The defect this exists to measure is that `LastRoundLaunch` is an absolute timestamp from
        /// when a round left the rail, while `est` is the flight of a round launched from wherever
        /// the shooter is at the moment of the call. For a ship those describe the same place. For
        /// an aircraft closing on the target they do not, and the shared impact walks earlier by the
        /// distance flown, splitting a strike into shots timed against different impacts.
        ///
        /// Three quantities per line:
        ///   - `pred` and its movement since the last line, which is the slide itself (D1);
        ///   - `moved`, shooter displacement since its FIRST launch, which is what the error should
        ///     be proportional to if the diagnosis is right (D2);
        ///   - `live`, the range-over-speed remaining flight of a round actually in the air, against
        ///     the `est` being used in its place (D4). This is what decides whether the fix can ask
        ///     the game for the real remaining flight or has to freeze an estimate.
        ///
        /// Thresholded at <see cref="AnchorSlideLogSeconds"/> because the prediction re-runs on a
        /// 0.5s TTL. The first prediction always prints, so a ship anchor that never moves still
        /// produces the baseline the control run needs.
        /// </summary>
        private static void LogAnchorSlide(Scheduled a, Intent it, float pred,
                                           AnchorPredictTerms terms, float simNow)
        {
            if (!VerboseLog || !terms.Valid) return;

            bool first = float.IsNaN(a.LastLoggedPred);
            float delta = first ? 0f : pred - a.LastLoggedPred;
            if (!first && Mathf.Abs(delta) < AnchorSlideLogSeconds) return;

            if (first) a.FirstPred = pred;
            a.LastLoggedPred = pred;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchor-slide {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}]: pred {pred:0.0}" +
                (first ? " (first)" : $" ({delta:+0.0;-0.0}s, {pred - a.FirstPred:+0.0;-0.0}s total)") +
                $" | lastRoundLaunch {terms.LastRoundLaunch:0.0}s + est {terms.Est:0.0}s" +
                $" ({(terms.FromLaunchState ? "launch state" : terms.Tier.ToString())}, " +
                $"ready {ReadyRoundsText(it)})" +
                $" - centering {terms.Centering:0.0}s + groupDelay {terms.GroupDelay:0.0}s" +
                $" | {ShooterDisplacement(a, it, simNow)}{LiveRoundFlight(a, it)}");
        }

        /// <summary>
        /// D6: rounds of this ammo still on the shooter. The 2026-09-06 run's estimate collapsed to
        /// 0.2s in the same frame the aircraft fired its LAST round, so whether the shooter still
        /// has one aboard is the first thing to rule in or out.
        /// </summary>
        private static string ReadyRoundsText(Intent it)
        {
            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            return f.Valid ? f.ReadyRounds.ToString() : "?";
        }

        /// <summary>
        /// Fraction of the straight-line, top-speed flight time below which an estimate is not
        /// slow-but-wrong, it is broken. Half is deliberately generous: a real shot is always SLOWER
        /// than range over max speed, never faster, so anything under half is impossible rather than
        /// merely surprising.
        /// </summary>
        private const float ImplausibleEstimateFraction = 0.5f;

        /// <summary>
        /// D8: catch a collapsed flight estimate at COMMIT, where the anchor role is decided.
        ///
        /// D6 watches the anchor's live prediction and so cannot see this: an order whose estimate
        /// collapses at commit gets a tiny `enroute` and LOSES the anchor role, which is how the
        /// 2026-09-06 second mission escaped with good coordination while the F/A-18's own Harpoons
        /// went out 238.7s past-due. The same 0.2s value reaching an order that DOES anchor set the
        /// shared impact to a time in the past and destroyed the strike.
        ///
        /// A shot cannot fly faster than the round's own top speed in a straight line, so comparing
        /// against that is a bound the physics guarantees rather than a tuned threshold. Reports
        /// whether the number came from the async cache or a fresh solve, which is the first fork in
        /// diagnosing it: a cached value means a worker published garbage under a live key, a fresh
        /// one means the integrator returned it on this thread, this frame.
        ///
        /// Diagnostic only. It does NOT substitute a corrected value, for the same reason
        /// WarnOnImpactAnomaly does not clamp.
        /// </summary>
        private static void WarnOnImplausibleEstimate(Intent it, float flightEst, bool fromCache)
        {
            if (it.Unit == null || it.Target == null) return;
            AmmunitionParameters ap;
            try { ap = it.Unit.getAmmunitionByName(it.AmmoId)?._ap; } catch { return; }
            if (ap == null || ap._maxVelocityInKnots <= 0f) return;

            float rangeM = GameUnits.MetersBetween(it.Unit, it.Target);
            float floorS = rangeM / (ap._maxVelocityInKnots * GameUnits.KnotsToMs);
            if (flightEst >= floorS * ImplausibleEstimateFraction) return;

            Bootstrap.Log.LogWarning(
                $"[AutoTOT] flight-est-implausible {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}] -> {UnitNaming.SafeName(it.Target)}: " +
                $"est {flightEst:0.00}s against a straight-line floor of {floorS:0.0}s " +
                $"({rangeM / 1000f:0.0}km at {ap._maxVelocityInKnots:0}kn). " +
                $"Source {(fromCache ? "async cache" : "fresh solve")}, tier {ModelStats.LastTier}, " +
                $"kinematics {ap.Kinematics}, shooter {it.Unit._velocityInKnots:0}kn. " +
                $"This order will get a tiny enroute and lose the anchor role; if it WINS one, " +
                $"the shared impact goes with it.");
        }

        /// <summary>
        /// D6: a loud line when the shared impact prediction does something it should never do.
        /// Two cases, both seen in the 2026-09-06 run and neither currently detected anywhere:
        ///
        ///   - the estimate collapses, so the impact jumps backwards by hundreds of seconds;
        ///   - the impact lands in the PAST, at which point every held order dumps at once with
        ///     "released past-due" and the strike has no coordination left at all.
        ///
        /// Diagnostic only. It does NOT clamp the prediction, deliberately: the point of this run is
        /// to see the raw behaviour and learn which guard is the right one. A guard added now would
        /// hide the evidence needed to choose it.
        /// </summary>
        private static void WarnOnImpactAnomaly(Scheduled a, Intent it, float pred,
                                                AnchorPredictTerms terms, float simNow)
        {
            if (!terms.Valid) return;
            bool backwards = !float.IsNaN(a.LastLoggedPred) &&
                             (a.LastLoggedPred - pred) > ImpactCollapseSeconds;
            bool inThePast = pred < simNow;
            if (!backwards && !inThePast) return;
            if (a.LoggedImpactAnomaly) return;   // once per anchor; the follow-on spam is derivative
            a.LoggedImpactAnomaly = true;

            Bootstrap.Log.LogWarning(
                $"[AutoTOT] anchor-impact-anomaly {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}]: impact {pred:0.0} at sim {simNow:0.0}" +
                (inThePast ? " IS IN THE PAST" : "") +
                (backwards ? $", jumped back {a.LastLoggedPred - pred:0.0}s" : "") +
                $". est {terms.Est:0.0}s ({terms.Tier}), lastRoundLaunch {terms.LastRoundLaunch:0.0}s, " +
                $"ready {ReadyRoundsText(it)}. Every held order will release past-due.");
        }

        /// <summary>
        /// D1 of docs/plans/open/anchor-liveness.md: why an anchor that has launched NOTHING is still
        /// holding the strike.
        ///
        /// The 2026-09-07 run 3 left one link in the chain inferred rather than observed. A circling
        /// F/A-18 held the anchor role for 296 s and fired none of its 4 ready rounds, and the
        /// existing traces say the launch envelope was not the blocker (`envelope-track` reported
        /// `aboveGate False` for all 60 samples) and that its hardpoint reached
        /// `EngagementNotAllowed` at t+6.9 s and logged no transition after. On those readings
        /// <see cref="AnchorStillWorking"/> should have gone false at about
        /// t+30 s, when the post-unblock grace expired, and the no-launch stall should have fired at
        /// <see cref="NoLaunchStallSim"/> = 120 s. It did not: the ripple ran to the
        /// <see cref="NoLaunchMaxHoldSim"/> = 300 s hard cap. Something kept the verdict true and no
        /// line records what.
        ///
        /// So this reports the verdict AND which signal produced it, next to the launcher's own state
        /// and the two deadlines, once every <see cref="LivenessLogIntervalSim"/> seconds. Reading
        /// `working True (preparing)` against a launcher state of `EngagementNotAllowed` would mean
        /// the launcher-intent read and the state the transition logger sees disagree, which is the
        /// leading hypothesis and is not something the current traces can show.
        ///
        /// Diagnostics only. No timing decision reads any of this.
        /// </summary>
        private static void LogAnchorLiveness(Scheduled a, Intent it, float simNow, int k, int n,
                                              float interval)
        {
            if (!VerboseLog) return;
            if (simNow - a.LastLivenessSim < LivenessLogIntervalSim) return;
            a.LastLivenessSim = simNow;

            // Two clocks, and they are not interchangeable. The hard cap in AnchorStillWorking
            // always runs from the ORDER; the stall window runs from the last round away once the
            // ripple has started, and from the order before that. Printing one countdown against
            // the other clock is how a diagnostic ends up lying about which deadline is close.
            float sinceFired = simNow - a.FiredAtSim;
            float held = k > 0 ? simNow - a.LaunchTimes[k - 1] : sinceFired;
            float stallWindow = k > 0
                ? Mathf.Max(StallCadenceMultiplier * interval, StallMinWindowSim)
                : NoLaunchStallSim;

            bool preparing = LauncherFactsSource.IsPreparingToFire(it.Unit, it.AmmoId);
            bool working = AnchorStillWorking(a, it, simNow);
            // Which arm answered, in the order AnchorStillWorking tests them. The hard cap is
            // tested first there, so it overrides both signals.
            string why = sinceFired > NoLaunchMaxHoldSim ? "hard-cap"
                       : preparing ? "preparing"
                       : a.LastAscending ? "progress-or-grace"
                       : "none";

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchor-liveness {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}]: {(k > 0 ? "since round " + k : "t")}+{held:0}s " +
                $"(order t+{sinceFired:0}s), launched {k}/{n}, " +
                $"ready {ReadyRoundsText(it)} | working {working} ({why}) | " +
                $"{LauncherStateText(it)} | stall in {Mathf.Max(0f, stallWindow - held):0}s, " +
                $"hard cap in {Mathf.Max(0f, NoLaunchMaxHoldSim - sinceFired):0}s | " +
                $"impact still sim {a.ImpactAtSim:0.0}, {(a.Followers?.Count ?? 0)} follower(s) " +
                $"({ReleasedFollowerCount(a)} already released)");
        }

        /// <summary>Current engage state of every launcher serving this order, for D1. Falls back to
        /// a short marker rather than throwing: this is a diagnostic and must never be the reason a
        /// tick fails.</summary>
        private static string LauncherStateText(Intent it)
        {
            try
            {
                var views = new List<LauncherProbe.LauncherView>();
                LauncherProbe.Collect(it.Unit, it.AmmoId, it.Target, views);
                return views.Count == 0 ? "no launcher" : LauncherProbe.Describe(views);
            }
            catch { return "launcher n/a"; }
        }

        /// <summary>
        /// D2 of docs/plans/open/anchor-liveness.md: every move of the shared impact, with the number
        /// of followers that have ALREADY released against the old value.
        ///
        /// A round in the air has spent its lead, so a later move of the shared impact is unfixable
        /// for it. This is the number that decides whether the shared impact should be committed once
        /// the first follower releases: run 2 of the 2026-09-07 session moved it 34.6 s earlier at
        /// stall finalisation with four rounds already away, and they ended 9.3 to 12.6 km short.
        ///
        /// Only reports moves past <see cref="ImpactMoveLogSeconds"/>, and only once a follower has
        /// actually released. Before that a move costs nothing and the tracking trace already covers
        /// it.
        /// </summary>
        private static void LogImpactMove(Scheduled a, Intent it, float pred, float simNow, string phase)
        {
            if (!VerboseLog) return;
            int released = ReleasedFollowerCount(a);
            if (released == 0) { a.LastCommittedImpact = pred; return; }
            if (float.IsNaN(a.LastCommittedImpact)) { a.LastCommittedImpact = pred; return; }

            float delta = pred - a.LastCommittedImpact;
            if (Mathf.Abs(delta) < ImpactMoveLogSeconds) return;
            a.LastCommittedImpact = pred;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] impact-move {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}] ({phase}): impact {delta:+0.0;-0.0}s to sim {pred:0.0} " +
                $"at sim {simNow:0.0}, {released} of {(a.Followers?.Count ?? 0)} follower(s) " +
                $"ALREADY RELEASED against the old value" +
                (delta < 0f ? " and cannot make the new one up" : ""));
        }

        /// <summary>Followers of this anchor that have already released, so a later move of the
        /// shared impact can no longer reach them.</summary>
        private static int ReleasedFollowerCount(Scheduled a)
        {
            if (a.Followers == null) return 0;
            int n = 0;
            for (int j = 0; j < a.Followers.Count; j++)
                if (a.Followers[j].Fired) n++;
            return n;
        }

        /// <summary>D2: how far the shooter has moved since its first observed launch, and how long
        /// ago that was. Empty when nothing has been recorded yet.</summary>
        private static string ShooterDisplacement(Scheduled a, Intent it, float simNow)
        {
            if (a.LaunchObs.Count == 0 || it.Unit == null) return "moved n/a";
            try
            {
                LaunchObservation first = a.LaunchObs[0];
                float km = (it.Unit.transform.position - first.ShooterPosU).magnitude
                           * GameUnits.MetersPerUnity / 1000f;
                return $"moved {km:0.0}km in the {simNow - first.Sim:0.0}s since launch 1";
            }
            catch { return "moved err"; }
        }

        /// <summary>
        /// D4: the remaining flight of a round actually in the air, as range over its current speed.
        /// Deliberately crude, and deliberately measured from the ROUND rather than from the
        /// shooter: the question is only whether the game can answer "how long until this one
        /// arrives" well enough to replace the re-estimate. The newest live round is used, since it
        /// is the one whose arrival the prediction is nominally about.
        /// </summary>
        private static string LiveRoundFlight(Scheduled a, Intent it)
        {
            if (it.Target == null || it.Target.IsDestroyed) return "";
            for (int i = a.LaunchObs.Count - 1; i >= 0; i--)
            {
                WeaponBase w = a.LaunchObs[i].Round;
                if (w == null || w.IsDestroyed) continue;
                try
                {
                    float speedMs = w._velocityInKnots * GameUnits.KnotsToMs;
                    if (speedMs <= 1f) continue;
                    float distM = GameUnits.MetersBetween(w, it.Target);
                    return $" | live round #{i + 1} {distM / 1000f:0.0}km at {w._velocityInKnots:0}kn, " +
                           $"remaining {distM / speedMs:0.0}s";
                }
                catch { return " | live round err"; }
            }
            return " | no live round";
        }

        /// <summary>
        /// D5: platform class, so the anchor lines say what KIND of shooter won the role. The whole
        /// question is whether the anchor moves between its launch and the ripple finalising, and
        /// that is a property of the platform, not of the weapon.
        /// </summary>
        private static string PlatformTag(ObjectBase u)
        {
            if (u == null) return "?";
            if (u is Submarine) return "sub";
            if (u is Aircraft) return "air";
            if (u is Helicopter) return "helo";
            return "ship";
        }

        // ------------------------------------------------------------------------------------
        // Phase 1 diagnostics, docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md. Each of these exists to
        // prove or kill one finding of that audit before its fix is written. All six were found by
        // reading the source, so none has ever been seen in a log; that is what they are here to
        // settle. Nothing below changes a timing decision.
        // ------------------------------------------------------------------------------------

        /// <summary>Cadence for the two per-tick census lines. Sim time, so a paused game is quiet
        /// and time compression does not multiply the output.</summary>
        private const float CensusIntervalSim = 30f;

        private static float _lastDisabledLogSim = float.NegativeInfinity;
        private static int _lastDisabledScheduled = -1;
        private static float _lastCensusSim = float.NegativeInfinity;
        private static int _lastCensusRows = -1;

        /// <summary>
        /// D2: what is still live while the master switch is off. `Enabled=false` stops intake
        /// (Coordinator.TryIntercept) and drawing (Hud.OnGUI) but not this tick, so an order that was
        /// already scheduled can still reach the launcher after the player has switched the mod off.
        ///
        /// Ungated and at WARNING when work is actually held, because that is the case the finding is
        /// about and a normal run must not be able to hide it. Silent when the state is empty, which
        /// is the expected result and the one that kills the finding.
        /// </summary>
        private static void DiagnoseDisabledWork(float simNow)
        {
            if (Enabled)
            {
                _lastDisabledScheduled = -1;
                return;
            }

            int held = _scheduled.Count;
            int open = _openBatches.Count;
            bool armed = _strikeBatch != null;
            if (held == 0 && open == 0 && !armed)
            {
                _lastDisabledScheduled = 0;
                return;
            }

            // On the first tick after the switch goes off, and then only when the count moves or the
            // cadence expires. A held order can sit for minutes and one line a minute is enough to
            // show it is still there.
            bool changed = held != _lastDisabledScheduled;
            if (!changed && simNow - _lastDisabledLogSim < CensusIntervalSim) return;
            _lastDisabledScheduled = held;
            _lastDisabledLogSim = simNow;

            Bootstrap.Log.LogWarning(
                $"[AutoTOT] disabled-work: Enabled=false but the coordinator is still ticking with " +
                $"{held} scheduled order(s), {open} open batch(es), strikeArmed={armed}. " +
                $"These can still dispatch. D2 of the beta-release audit.");
        }

        /// <summary>
        /// D10: whether the engagement board is being pruned. Its only prune path is CollectSalvos,
        /// which the HUD calls from inside OnGUI, so hiding the panel or disabling the indicator
        /// stops the cleanup. `panelDrawn` says whether the panel painted since the last census, so
        /// a rising row count beside `panelDrawn=False` is the finding, and a falling one kills it.
        /// </summary>
        private static void DiagnoseBoardCensus(float simNow)
        {
            if (!VerboseLog) return;
            if (simNow - _lastCensusSim < CensusIntervalSim) return;
            _lastCensusSim = simNow;

            // Same cadence, and the same kind of question, so it rides along rather than keeping
            // its own timer. Before the quiet-board return: the estimator census is about the solve
            // pool, which runs whether or not anything is being engaged.
            FlightTime.LogAsyncCensus();

            EngagementBoard.Census(simNow, out int rows, out int fired, out float oldest);
            bool panelDrawn = Hud.DrewSinceLastCensus();
            // Quiet when there is nothing to say: an empty board that stays empty is not a finding.
            if (rows == 0 && _lastCensusRows <= 0) { _lastCensusRows = rows; return; }
            _lastCensusRows = rows;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] board-census {rows} row(s), {fired} fired, oldest fired {oldest:0.0}s ago, " +
                $"panelDrawn={panelDrawn}, scheduled {_scheduled.Count}. D10 of the beta-release audit.");
        }

        /// <summary>
        /// D1: a follower still holding on an anchor that has left _scheduled. The hold in
        /// ReleaseDueLaunches waits for the anchor's first launch and is bounded by RippleDone, which
        /// only UpdateAnchorTracking can set, and that loop walks _scheduled. An anchor removed from
        /// it after being marked Fired therefore cannot ever release its followers.
        ///
        /// Ungated and at WARNING: this states an invariant that should never hold, and a run that
        /// hits it must not have to be re-flown with verbose on to see it. Once per follower.
        /// </summary>
        private static void DiagnoseOrphanedFollower(Scheduled s, float simNow)
        {
            if (s.LoggedOrphan) return;
            Scheduled a = s.Anchor;
            if (a == null || a.LeftScheduleSim < 0f) return;
            s.LoggedOrphan = true;

            Intent it = s.Item;
            Intent ai = a.Item;
            float held = s.ScheduledAtSim >= 0f ? simNow - s.ScheduledAtSim : -1f;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] anchor-orphaned {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} -> " +
                $"{UnitNaming.SafeName(it.Target)}: its anchor {ai?.AmmoId} from " +
                $"{UnitNaming.SafeName(ai?.Unit)} left the schedule {simNow - a.LeftScheduleSim:0.0}s ago " +
                $"with {a.LaunchTimes.Count} launch(es) observed and RippleDone={a.RippleDone}. " +
                $"This order has been held {held:0.0}s and nothing can release it. " +
                $"D1 of the beta-release audit.");
        }

        /// <summary>
        /// D1, removal side: how many followers were still pointing at an entry as it left
        /// _scheduled. Zero is the ordinary case and says nothing; a nonzero count on an anchor that
        /// has fired without launching is the exact shape the finding predicts.
        /// </summary>
        private static void NoteScheduleExit(Scheduled s, float simNow, string reason)
        {
            s.LeftScheduleSim = simNow;
            int followers = s.Followers?.Count ?? 0;
            if (!s.IsAnchor || followers == 0) return;

            int waiting = 0;
            for (int i = 0; i < s.Followers.Count; i++)
                if (!s.Followers[i].Fired) waiting++;
            if (waiting == 0) return;

            // Three of the four exits are recoverable and must not warn, or every ordinary strike
            // would print one. A ripple that COMPLETES sets RippleDone, which frees the hold; an
            // anchor that dies BEFORE releasing is re-elected by PromoteNewAnchor, which re-points
            // every survivor; an abandoned anchor sets RippleDone and goes through
            // PromoteAbandonedAnchor. What is left is the shape the finding is about: the anchor has
            // dispatched, no round was ever seen, and it leaves without RippleDone, so nothing can
            // set it afterwards and nothing can release the followers.
            bool stranding = s.Fired && !s.RippleDone && s.LaunchTimes.Count == 0;
            if (!stranding)
            {
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchor-exit {s.Item?.AmmoId} from {UnitNaming.SafeName(s.Item?.Unit)} " +
                        $"({reason}): {waiting} of {followers} follower(s) still held, fired={s.Fired}, " +
                        $"launches {s.LaunchTimes.Count}, RippleDone={s.RippleDone}.");
                return;
            }

            Bootstrap.Log.LogWarning(
                $"[AutoTOT] anchor-exit {s.Item?.AmmoId} from {UnitNaming.SafeName(s.Item?.Unit)} " +
                $"({reason}): left the schedule after dispatch with NO launch observed and " +
                $"RippleDone=false, holding {waiting} of {followers} follower(s). Nothing can release " +
                $"them now. D1 of the beta-release audit.");
        }

        /// <summary>
        /// D6: the centering term used to finalize a stalled ripple, beside the one the observed
        /// launches would have produced. Independent salvos release half a ripple span early and the
        /// prediction subtracts that same span back out, but the span was fixed at commit from the
        /// FULL order while the stall re-predicts on the rounds that actually flew.
        ///
        /// `delta` is the error a fix would remove, stated before anything is changed. Grouped
        /// salvos are excluded: their centering is 0 either way.
        /// </summary>
        private static void LogStallCentering(Scheduled a, Intent it, int requested, int observed,
                                              float span, float interval)
        {
            if (!VerboseLog || it.Grouped) return;
            float used = it.ReleaseLead;
            float onObserved = observed > 1 ? span * 0.5f : 0f;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] stall-centering {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                $"requested {requested}, observed {observed}, interval {interval:0.00}s, " +
                $"observedSpan {span:0.0}s, centeringUsed {used:0.0}s, " +
                $"centeringIfObserved {onObserved:0.0}s, delta {used - onObserved:+0.0;-0.0}s " +
                $"(positive => followers released that much early). D6 of the beta-release audit.");
        }

        /// <summary>
        /// D7: a shooter that was inside its launch envelope at commit and has since left it. Only
        /// EnvelopeTracked orders are refreshed, and that flag is set from a commit-time delay
        /// greater than zero, so a platform that was ready is never asked again.
        ///
        /// The value is read for the log and thrown away: nothing here feeds StartupLead, so release
        /// timing is identical with the diagnostic on or off. One line per order.
        /// </summary>
        private static void DiagnoseUntrackedEnvelope(Scheduled s, Intent it, float simNow)
        {
            if (!VerboseLog || s.Fired || it.EnvelopeTracked || s.LoggedUntrackedEnvelope) return;
            if (!(it.Unit is Aircraft) && !(it.Unit is Submarine)) return;
            if (simNow - s.LastUntrackedEnvelopeSim < EnvelopeRefreshSim) return;
            s.LastUntrackedEnvelopeSim = simNow;

            float now = LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId);
            if (now <= 0f) return;
            s.LoggedUntrackedEnvelope = true;

            float held = s.ScheduledAtSim >= 0f ? simNow - s.ScheduledAtSim : -1f;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] envelope-untracked {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} " +
                $"[{PlatformTag(it.Unit)}]: 0.0s at commit so it is not tracked, now {now:0.0}s " +
                $"after {held:0.0}s held. Its startupLead is {it.StartupLead:0.0}s and will not be " +
                $"updated. D7 of the beta-release audit.");
        }

        /// <summary>
        /// D9: what a whole strike asks of one shooter's magazine, against what that shooter can
        /// actually fire. The panel caps each ROW at the launcher-available count, but only the
        /// guidance-channel budget is shared across targets, so ammunition that is not channel-capped
        /// can be staged twice over.
        ///
        /// Called once per commit, after the guidance clamp and before PrepareIntent, so the counts
        /// are the ones that will actually be dispatched.
        /// </summary>
        private static readonly Dictionary<string, int> _stockScratch = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _stockTargets = new Dictionary<string, int>();

        private static void DiagnoseAggregateStock(List<Intent> items)
        {
            if (items == null || items.Count == 0) return;
            _stockScratch.Clear();
            _stockTargets.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it.Unit == null || it.AmmoId == null) continue;
                string key = it.Unit.GetInstanceID() + "|" + it.AmmoId;
                _stockScratch.TryGetValue(key, out int n);
                _stockScratch[key] = n + Mathf.Max(1, it.Shots);
                _stockTargets.TryGetValue(key, out int t);
                _stockTargets[key] = t + 1;
            }

            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it.Unit == null || it.AmmoId == null) continue;
                string key = it.Unit.GetInstanceID() + "|" + it.AmmoId;
                if (!_stockScratch.TryGetValue(key, out int requested)) continue;
                _stockScratch.Remove(key);   // one line per shooter and ammunition, not per order
                int targets = _stockTargets.TryGetValue(key, out int t) ? t : 1;
                int available = LauncherFactsSource.AvailableRounds(it.Unit, it.AmmoId);

                if (requested > available)
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] stock-check {it.Unit.getUIDAndName()} {it.AmmoId}: requested " +
                        $"{requested} across {targets} target(s), launchers can supply {available}. " +
                        $"The strike is over-committed by {requested - available}. " +
                        $"D9 of the beta-release audit.");
                else if (VerboseLog && targets > 1)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] stock-check {it.Unit.getUIDAndName()} {it.AmmoId}: requested " +
                        $"{requested} across {targets} target(s), available {available}. OK.");
            }
        }

        /// <summary>Verbose log for a held item dropped because its shooter or target is gone.</summary>
        private static void LogDroppedItem(Scheduled s)
        {
            if (!VerboseLog) return;
            Intent it = s.Item;
            bool targetGone = it.Target == null || it.Target.IsDestroyed;
            string reason = targetGone ? "target already destroyed" : "shooter gone";
            if (s.Fired)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor {it.AmmoId} lost after launch ({reason}); " +
                    $"held orders keep the last predicted impact.");
            else
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] dropped held {it.AmmoId} from " +
                    $"{(it.Unit != null ? it.Unit.getUIDAndName() : "?")}: {reason} before release.");
        }
    }
}
