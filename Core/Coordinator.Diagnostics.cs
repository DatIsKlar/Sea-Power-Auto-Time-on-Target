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
