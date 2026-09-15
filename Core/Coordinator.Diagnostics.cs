using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The coordinator's log lines and their text helpers; nothing here decides anything. Most are gated
    /// behind <see cref="Coordinator.VerboseLog"/> or <see cref="Coordinator.TraceFlightModel"/>.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// What a launch-state snapshot was built from, and what it produced against the live estimate. It
        /// feeds the shared impact, so a bad value must be visible. One line per round, since it is memoised.
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
        /// The recorded launch state in readable form. The rail heading's flat magnitude matters: an
        /// air-dropped round still pointing steeply down has a tiny horizontal component whose direction is
        /// nearly arbitrary, and the integrator flies that bearing through the initial phase.
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
        /// The shared impact prediction each time it moves, with its terms: `pred` and its movement; `moved`,
        /// shooter displacement since its first launch, which a launch-time and estimate mismatch scales
        /// with; and `live`, a flying round's range over speed, against the `est` used. Thresholded at
        /// <see cref="AnchorSlideLogSeconds"/>; the first prediction always prints as a baseline.
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
                $" ({terms.Source}, " +
                $"ready {ReadyRoundsText(it)})" +
                $" - centering {terms.Centering:0.0}s{(it.Pooled ? " (pooled)" : "")}" +
                $" + groupDelay {terms.GroupDelay:0.0}s" +
                $" | {ShooterDisplacement(a, it, simNow)}{LiveRoundFlight(a, it)}");
        }

        /// <summary>Rounds of this ammo still aboard the shooter.</summary>
        private static string ReadyRoundsText(Intent it)
        {
            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            return f.Valid ? f.ReadyRounds.ToString() : "?";
        }

        /// <summary>
        /// Fraction of the straight-line, top-speed flight time below which an estimate is broken rather than
        /// slow: a real shot is never faster than range over top speed.
        /// </summary>
        private const float ImplausibleEstimateFraction = 0.5f;

        /// <summary>Where a commit's flight estimate came from, for the implausible-estimate line.</summary>
        internal enum EstSource
        {
            Fresh,           // solved on this call, so ModelStats.LastTier still describes it
            AsyncCache,      // published by a solve worker
            DeferredCommit,  // resolved while the commit was pending, by either route
        }

        private static string Describe(EstSource src)
        {
            switch (src)
            {
                case EstSource.AsyncCache: return "async cache";
                case EstSource.DeferredCommit: return "deferred commit";
                default: return $"fresh solve, tier {ModelStats.LastTier}";
            }
        }

        /// <summary>
        /// Catch a collapsed flight estimate at commit, where the anchor role is decided: a collapsed order
        /// loses the anchor role or, if it anchors, puts the shared impact in the past. The bound is physical
        /// (range over top speed). Says whether the value came from the async cache or a fresh solve.
        /// Diagnostic only; nothing is substituted.
        /// </summary>
        private static void WarnOnImplausibleEstimate(Intent it, float flightEst, EstSource source)
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
                $"Source {Describe(source)}, " +
                $"kinematics {ap.Kinematics}, shooter {it.Unit._velocityInKnots:0}kn. " +
                $"This order will get a tiny enroute and lose the anchor role; if it WINS one, " +
                $"the shared impact goes with it.");
        }

        /// <summary>
        /// Warn when the shared impact prediction jumps backwards by a collapse-sized step or lands in the
        /// past, where every held order would release past due. Diagnostic only; the prediction is not clamped.
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
                $". est {terms.Est:0.0}s ({terms.Source}), lastRoundLaunch {terms.LastRoundLaunch:0.0}s, " +
                $"ready {ReadyRoundsText(it)}. Every held order will release past-due.");
        }

        /// <summary>
        /// Why an anchor that has launched nothing is still holding the strike: the
        /// <see cref="AnchorStillWorking"/> verdict and the signal behind it, the launcher state and both
        /// deadlines, every <see cref="LivenessLogIntervalSim"/> seconds. Diagnostic only.
        /// </summary>
        private static void LogAnchorLiveness(Scheduled a, Intent it, float simNow, int k, int n,
                                              float interval)
        {
            if (!VerboseLog) return;
            if (simNow - a.LastLivenessSim < LivenessLogIntervalSim) return;
            a.LastLivenessSim = simNow;

            // Two clocks: the hard cap always runs from the order; the stall window runs from the last round
            // away once the ripple has started, and from the order before that.
            float sinceFired = simNow - a.FiredAtSim;
            float held = k > 0 ? simNow - a.LaunchTimes[k - 1] : sinceFired;
            float stallWindow = k > 0
                ? Mathf.Max(StallCadenceMultiplier * interval, StallMinWindowSim)
                : NoLaunchStallSim;

            bool preparing = LauncherFactsSource.IsPreparingToFire(it.Unit, it.AmmoId);
            bool working = AnchorStillWorking(a, it, simNow);
            // Which arm answered, in AnchorStillWorking's order; the hard cap overrides both signals.
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

        /// <summary>Engage state of every launcher serving this order; returns a marker instead of throwing.</summary>
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
        /// Every move of the shared impact past <see cref="ImpactMoveLogSeconds"/> once a follower has
        /// released, with the number of followers already away, whose rounds cannot follow the move.
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

        /// <summary>How far the shooter has moved since its first observed launch, and how long ago; empty
        /// when nothing is recorded.</summary>
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
        /// Remaining flight of the newest round in the air, as range over current speed. Crude on purpose: it
        /// shows whether the game's live state could answer "how long until this one arrives".
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

        /// <summary>Platform class, so the anchor lines say what kind of shooter won the role.</summary>
        private static string PlatformTag(ObjectBase u)
        {
            if (u == null) return "?";
            if (u is Submarine) return "sub";
            if (u is Aircraft) return "air";
            if (u is Helicopter) return "helo";
            return "ship";
        }

        // ------------------------------------------------------------------------------------
        // Release-path diagnostics. Nothing below changes a timing decision.
        // ------------------------------------------------------------------------------------

        /// <summary>Cadence for the estimator census. Sim time, so a paused game is quiet and time
        /// compression does not multiply the output.</summary>
        private const float CensusIntervalSim = 30f;

        private static float _lastCensusSim = float.NegativeInfinity;

        /// <summary>
        /// The estimator pool's census: in flight, completed, superseded, publish age, faults. Verbose only;
        /// it runs whether or not anything is engaged, because the pool does.
        /// </summary>
        private static void DiagnoseEstimatorCensus(float simNow)
        {
            if (!VerboseLog) return;
            if (simNow - _lastCensusSim < CensusIntervalSim) return;
            _lastCensusSim = simNow;
            FlightTime.LogAsyncCensus();
        }

        /// <summary>
        /// Warn when a follower still holds on an anchor that has left _scheduled: only UpdateAnchorTracking
        /// can set the RippleDone that frees it, and that loop walks _scheduled. Ungated, once per follower.
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
                $"This order has been held {held:0.0}s and nothing can release it.");
        }

        /// <summary>
        /// Count the followers still pointing at an entry as it leaves _scheduled, and warn when a fired
        /// anchor that launched nothing leaves without RippleDone. Call after <see cref="RetireAnchor"/>,
        /// which sets RippleDone on every such exit, so the warning only fires for an exit that skipped it.
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

            // Ordinary exits must not warn: a completed ripple sets RippleDone, a held anchor is re-elected,
            // and a fired anchor that launched nothing is retired. What remains is the stranding case.
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
                $"them now.");
        }

        /// <summary>
        /// The centring used to finalize a stalled ripple, beside the one the observed launches give: the
        /// lead was fixed at commit from the full order, while the re-prediction uses the rounds that flew.
        /// `correction` is the difference. Grouped salvos centre at 0 either way; a pooled anchor uses the pool's lead.
        /// </summary>
        private static void LogStallCentering(Scheduled a, Intent it, int requested, int observed,
                                              float span, float interval, float onObserved)
        {
            if (!VerboseLog || it.Grouped) return;
            float used = it.ReleaseLead;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] stall-centering {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                $"requested {requested}, observed {observed}, interval {interval:0.00}s, " +
                $"observedSpan {span:0.0}s, {(it.Pooled ? "pooled lead" : "centering")} " +
                $"{onObserved:0.0}s (commit value was " +
                $"{used:0.0}s, correction {onObserved - used:+0.0;-0.0}s).");
        }

        /// <summary>
        /// What a strike asks of one shooter's magazine against what it can fire. The panel caps each row,
        /// but only the channel budget is shared across targets, so ammunition that is not channel-capped can
        /// be staged twice over. Called once per commit, after the guidance clamp and before PrepareIntent.
        /// </summary>
        private static readonly Dictionary<ShipAmmoKey, int> _stockScratch = new Dictionary<ShipAmmoKey, int>();
        private static readonly Dictionary<ShipAmmoKey, int> _stockTargets = new Dictionary<ShipAmmoKey, int>();

        private static void DiagnoseAggregateStock(List<Intent> items)
        {
            if (items == null || items.Count == 0) return;
            _stockScratch.Clear();
            _stockTargets.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it.Unit == null || it.AmmoId == null) continue;
                var key = new ShipAmmoKey(it.Unit, it.AmmoId);
                _stockScratch.TryGetValue(key, out int n);
                _stockScratch[key] = n + Mathf.Max(1, it.Shots);
                _stockTargets.TryGetValue(key, out int t);
                _stockTargets[key] = t + 1;
            }

            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it.Unit == null || it.AmmoId == null) continue;
                var key = new ShipAmmoKey(it.Unit, it.AmmoId);
                if (!_stockScratch.TryGetValue(key, out int requested)) continue;
                _stockScratch.Remove(key);   // one line per shooter and ammunition, not per order
                int targets = _stockTargets.TryGetValue(key, out int t) ? t : 1;
                int available = LauncherFactsSource.AvailableRounds(it.Unit, it.AmmoId);
                // Include rounds owed from earlier commits, same ammunition only.
                int outstanding = OutstandingRounds(it.Unit, it.AmmoId);
                requested += outstanding;

                string owed = outstanding > 0 ? $" (including {outstanding} still owed from earlier orders)" : "";
                if (requested > available)
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] stock-check {UnitNaming.SafeName(it.Unit)} {it.AmmoId}: requested " +
                        $"{requested} across {targets} target(s){owed}, launchers can supply " +
                        $"{available}. The strike is over-committed by {requested - available}.");
                else if (VerboseLog && (targets > 1 || outstanding > 0))
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] stock-check {UnitNaming.SafeName(it.Unit)} {it.AmmoId}: requested " +
                        $"{requested} across {targets} target(s){owed}, available {available}. OK.");
            }
        }

        /// <summary>
        /// Rounds of one ammunition this ship is committed to and has not yet launched: scheduled but
        /// unreleased, plus dispatched but unfinished. Exact ammunition match, since magazines are per ammunition.
        /// </summary>
        internal static int AmmoOutstanding(ObjectBase unit, string ammoId)
            => OutstandingRounds(unit, ammoId);

        private static int OutstandingRounds(ObjectBase unit, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _scheduled.Count; i++)
            {
                Scheduled s = _scheduled[i];
                if (s?.Item == null || s.Fired) continue;
                if (s.Item.Unit == unit && string.Equals(s.Item.AmmoId, ammoId, StringComparison.Ordinal))
                    n += Mathf.Max(1, s.Item.Shots);
            }
            n += ReservedRounds(unit, ammoId);
            // Committed orders still waiting on their estimates are a claim on the magazine too.
            n += PendingAmmoRounds(unit, ammoId);
            return n;
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
                    $"{UnitNaming.SafeName(it.Unit)}: {reason} before release.");
        }
    }
}
