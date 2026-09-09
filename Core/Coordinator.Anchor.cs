using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Observation anchoring: watching what a shooter actually launches and moving the shared
    /// impact time to match, rather than trusting the estimate made at commit.
    ///
    /// Split out of Coordinator.cs for size only. See docs/ARCHITECTURE.md for the pipeline this
    /// sits in; the release half that consumes these verdicts is in Coordinator.Release.cs.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// Live impact prediction for a firing anchor from its observed launch ripple: the last
        /// round's projected launch time (last observed launch + measured cadence x rounds still
        /// to come) plus a live lone-flight estimate at the CURRENT geometry (the last round
        /// launches from wherever the ship is now), minus the ripple-centering lead for
        /// non-grouped salvos. Returns the entry's current impact time until at least one launch
        /// has been observed and a valid estimate exists.
        /// </summary>
        private static float PredictAnchorImpact(Scheduled a, Intent it, int k, int n, float interval)
            => PredictAnchorImpact(a, it, k, n, interval, GameClock.SimNow(), out _);

        /// <summary>
        /// The shared impact time, extrapolated from the anchor's observed launches.
        ///
        /// One rule, from docs/plans/open/aircraft-anchor-impact-slide.md: <b>pair a launch time with
        /// a flight estimate valid AT that launch time.</b> Every symptom that plan chased is a
        /// violation of it, because <c>lastRoundLaunch</c> is an absolute timestamp while
        /// <c>FlightTime.Estimate</c> answers for a shot leaving from wherever the shooter is NOW.
        /// For a ship the two describe the same place. For an aircraft closing at 540kn the shared
        /// impact walked earlier at 3.6s per km, measured at 170.9s over 48.2km, and the strike split
        /// into shots timed against different impacts.
        ///
        /// Both branches below obey the rule, and they are the only two cases there are:
        ///   - a round already away pairs its real timestamp with an estimate integrated from its
        ///     recorded launch state (<see cref="FlightTime.LaunchState"/>);
        ///   - a round still to come pairs a launch time extrapolated from the measured cadence,
        ///     clamped to now because nothing can launch in the past, with an estimate from the
        ///     shooter's current position, which is the best guess available for where it will be.
        ///
        /// No threshold and no estimator switch, so ships and aircraft run identical code and the
        /// shared impact cannot step mid-strike.
        /// </summary>
        /// <param name="centeringOverride">
        /// Centering to use instead of the commit-time <see cref="Intent.ReleaseLead"/>. Negative
        /// means "use the commit value", which is every caller but the stall finalizer. See the note
        /// on centering below.
        /// </param>
        private static float PredictAnchorImpact(Scheduled a, Intent it, int k, int n, float interval,
                                                 float simNow, out AnchorPredictTerms terms,
                                                 float centeringOverride = -1f)
        {
            terms = default;
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictFlight);
            float est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictFlight);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictFlightCalls);
            
            if (k <= 0 || est <= FlightTime.MinValidSeconds) return a.ImpactAtSim;

            float lastLaunch = a.LaunchTimes[k - 1];
            bool allAway = k >= n;
            // Correction 1. Only the extrapolated part is clamped: a round already on the rail keeps
            // its real timestamp, which is a measurement and not a prediction.
            float lastRoundLaunch = allAway
                ? lastLaunch
                : Mathf.Max(lastLaunch + interval * (n - k), simNow);
            float span = lastRoundLaunch - a.LaunchTimes[0];
            
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictGroupDelay);
            float groupDelay = GroupDelay(it, span);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictGroupDelay);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictGroupDelayCalls);

            // The last round is on the rail, so its launch state is a measurement. Only then, and
            // only because a round still to come has no launch state to read: for that one the
            // shooter's current position remains the best available guess at where it will fire.
            float snapshot = allAway ? LaunchStateEstimate(a, it, k - 1, est) : -1f;
            bool fromLaunchState = snapshot > FlightTime.MinValidSeconds;
            float flight = fromLaunchState ? snapshot : est;
            // An independent salvo is released half a ripple span early so its spread of arrivals
            // straddles the time on target, and the prediction subtracts that same half-span back
            // out. ReleaseLead states it for the salvo that was ORDERED. When a ripple stalls, the
            // salvo that flew is shorter, and the finalizer passes what the observed launches
            // actually spanned. Measured on 2026-09-09 before this existed: an order of 4 that
            // launched 3 subtracted 22.5s where the launches spanned 10.0s, so the shared impact
            // was set 17.5s early and every follower released against it.
            float centering = it.Grouped ? 0f
                            : (centeringOverride >= 0f ? centeringOverride : it.ReleaseLead);

            terms = new AnchorPredictTerms
            {
                Valid = true,
                Est = flight,
                Tier = ModelStats.LastTier,
                LastRoundLaunch = lastRoundLaunch,
                Centering = centering,
                GroupDelay = groupDelay,
                FromLaunchState = fromLaunchState,
            };
            return lastRoundLaunch + flight - centering + groupDelay;
        }

        /// <summary>
        /// Flight estimate for an already-launched round, integrated from the state its shooter was
        /// in when it left the rail. -1 when no launch state was recorded for that round.
        ///
        /// Memoised on the observation. The answer is a constant of the launch, so it is integrated
        /// once per round rather than on every 0.5s prediction refresh; without that this would be
        /// the most expensive thing on the coordinator's tick.
        /// </summary>
        private static float LaunchStateEstimate(Scheduled a, Intent it, int index, float liveEst)
        {
            if (index < 0 || index >= a.LaunchObs.Count || it.Unit == null || it.Target == null)
                return -1f;
            LaunchObservation o = a.LaunchObs[index];
            if (o.Est != 0f) return o.Est;
            if (o.ShooterPosU == Vector3.zero) return -1f;

            var launch = new FlightTime.LaunchState(o.ShooterPosU, o.ShooterVelKn, o.RailHeadingU);
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictFlight);
            float est = FlightTime.EstimateFromLaunch(it.Unit, it.AmmoId, it.Target, launch);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictFlight);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictFlightCalls);

            // NO CEILING, and the reason is recorded because it looks like an obvious guard.
            // A ratio test against the live estimate was built and then removed: on 2026-09-07b the
            // snapshot read 697.7s against a live 444.6s, a ratio of 1.57, and that snapshot was
            // GOOD (the round flew 703.5s). The bad one on 2026-09-07e was 1218.9s against 545.5s,
            // a ratio of 2.23. There is no defensible line between 1.57 and 2.23, and a guard set
            // anywhere in that gap would reject a correct answer as readily as a wrong one. The
            // ratio is large in both cases for the same legitimate reason: the live estimate is
            // taken from where the shooter has since got to, which is the very error the snapshot
            // exists to remove. See docs/plans/open/anchor-liveness.md.
            LogLaunchSnapshot(o, it, est, liveEst);

            o.Est = est > FlightTime.MinValidSeconds ? est : -1f;
            a.LaunchObs[index] = o;
            return o.Est;
        }

        /// <summary>
        /// Observation anchoring. The batch anchor (longest enroute incl. span) is released first;
        /// its ACTUAL launches are watched here to extrapolate the launcher's live cadence and
        /// rewrite the shared impact time every held order releases against.
        ///
        /// WHY the anchor's last launch predicts the group's impact: a grouped salvo's convergent
        /// impact lands when its LAST round's solo flight ends (MissileGroup.cs:106-141 ;
        /// AdjustMembersVelocities applies symmetric ±40% speed clamps, so the farthest trailer
        /// flies at exactly solo speed until it closes up; then the group cashes in together,
        /// Missile.cs:839-842). Cadence is MEASURED, not read: no INI declares it, and the realized
        /// value comes from per-cell hatch animations and engage-task reassignment.
        ///
        /// The prediction updated every tick (k = launches observed, n = anchor shots):
        ///
        ///   interval  = (lastLaunch - firstLaunch) / (k-1)          once k >= 2,
        ///               else the INI interval (a-priori seed)
        ///   lastRound = lastLaunch + interval * (n - k)               while k < n,
        ///               else lastLaunch (ripple complete)
        ///   impact    = lastRound + liveEstimate - centering,
        ///               where centering = ReleaseLead for independent salvos (their arrivals are
        ///               centered on the trailing edge minus the lead) and 0 for grouped salvos
        ///               (they land tight at the trailing edge).
        ///
        /// Finalizes when the first wave has fully launched (k >= n), or when launches stall
        /// (shortfall / gating) ; held orders keep whatever prediction was last written.
        /// </summary>
        /// <summary>
        /// Launcher-intent-only form for callers with no <see cref="Scheduled"/> to carry the depth
        /// samples (the shortfall accounting in <see cref="LaunchDiagnostics"/>). Covers a launcher
        /// mid cycle and a submarine still held below its launch ceiling; it cannot tell a boat that
        /// is climbing from one that is parked, so callers must impose their own absolute bound.
        /// </summary>
        internal static bool ShooterStillWorking(ObjectBase unit, string ammoId)
        {
            if (unit == null || ammoId == null) return false;
            if (LauncherFactsSource.IsPreparingToFire(unit, ammoId)) return true;
            return SubmarineFacts.TrySnapshot(unit, ammoId, out SubmarineFacts.Snapshot s) && s.LaunchBlocked;
        }

        /// <summary>
        /// True while a shooter that has launched nothing is still visibly working toward its first
        /// round, so the no-launch stall timer should hold past <see cref="NoLaunchStallSim"/>
        /// (bounded by <see cref="NoLaunchMaxHoldSim"/>). Three signals, in order:
        ///
        /// 1. The launcher reports it is mid launch cycle
        ///    (<see cref="LauncherFactsSource.IsPreparingToFire"/>). This is the game stating its own
        ///    intent and is the primary test.
        /// 2. A submarine is depth-blocked and has come shallower since the last sample, which covers
        ///    the ascent before the launcher starts its cycle at all.
        /// 3. It cleared the block within the last <see cref="PostUnblockGraceSim"/> seconds, which
        ///    covers the handoff between the two: 1 and 2 are read from different places and do not
        ///    overlap, so without this an order can die in the gap.
        ///
        /// Neither an idle launcher nor a boat parked at a depth it will not leave counts, which is
        /// what lets a genuinely stuck order terminate: a radio-command weapon whose guidance radar
        /// needs periscope depth sits in LauncherTooLow at the weapon's own ceiling indefinitely,
        /// never clearing the block, so signal 3 never arms for it either.
        /// </summary>
        /// <summary>
        /// The full verdict used by the anchor-tracking loop: the launcher-intent and depth tests
        /// of <see cref="ShooterStillWorking(ObjectBase, string)"/>, plus the hard cap, the
        /// depth-sampling verdict and the post-unblock grace. Named apart from that overload
        /// because the two answer different questions and picking the wrong one by overload
        /// resolution is silent.
        /// </summary>
        internal static bool AnchorStillWorking(Scheduled a, Intent it, float simNow)
        {
            if (a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchMaxHoldSim) return false;
            if (LauncherFactsSource.IsPreparingToFire(it.Unit, it.AmmoId)) return true;
            // The depth verdict is maintained by SampleShooterProgress, which UpdateAnchorTracking
            // runs every tick from the moment the anchor fires. This used to sample inline, but the
            // only caller sits behind a `> NoLaunchStallSim` short-circuit, so the boat was first
            // read 120s into an ascent that was over by 135s: envelope-track produced three lines
            // at the very end and could not be compared against envelope-sim at all.
            return a.LastAscending;
        }

        /// <summary>
        /// Samples the shooter's ascent on <see cref="DepthSampleIntervalSim"/>, maintaining the
        /// verdict <see cref="AnchorStillWorking"/> returns and emitting
        /// the <c>envelope-track</c> trace. Called every tick for an anchor that has launched
        /// nothing; self-gating, so calling it more often costs one float compare.
        /// </summary>
        private static void SampleShooterProgress(Scheduled a, Intent it, float simNow,
                                                  bool maintainVerdict = true)
        {
            if (it.Unit == null || it.Unit.IsDestroyed) return;
            if (simNow - a.LastDepthSim < DepthSampleIntervalSim) return;

            if (!SubmarineFacts.TrySnapshot(it.Unit, it.AmmoId, out SubmarineFacts.Snapshot s))
            {
                if (SampleAircraftProgress(a, it, simNow, maintainVerdict)) return;
                // Not a submarine or an aircraft, or unreadable: nothing to hold on. The
                // launcher-intent test in ShooterStillWorking still applies.
                a.LastDepthSim = simNow;
                if (maintainVerdict) a.LastAscending = false;
                return;
            }

            float prev = a.LastDepthFt;
            // envelope-track: reads line-for-line against envelope-sim. Rate is measured over the
            // sample interval, so the first sample (no previous depth) reports none.
            if (VerboseLog && a.FiredAtSim >= 0f)
            {
                float rate = float.IsNaN(prev) ? float.NaN
                           : (prev - s.DepthFt) / Mathf.Max(0.001f, simNow - a.LastDepthSim);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-track {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                    $"t+{simNow - a.FiredAtSim:0}s {s.DepthFt:0}ft p{s.PitchDeg:0.0} " +
                    $"r{(float.IsNaN(rate) ? 0f : rate):0.00}ft/s spd {s.SpeedKn:0.0}kn " +
                    $"cmd {(float.IsNaN(s.CmdSpeedKn) ? 0f : s.CmdSpeedKn):0.0}kn " +
                    $"tanks {s.BallastRateFtPerS:0.00}ft/s blocked {s.LaunchBlocked} engage {s.EngageState}");
            }
            a.LastDepthFt = s.DepthFt;
            a.LastDepthSim = simNow;

            if (!maintainVerdict) return;

            if (!s.LaunchBlocked)
            {
                // In the launch envelope. The launcher has not been seen cycling yet (that test runs
                // first and wins outright), so hold for PostUnblockGraceSim to cover the handoff
                // rather than dropping the order in the gap between the two reads.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return;
            }

            // Slipped back below the ceiling: the grace is about the handoff, so it starts again.
            a.UnblockedAtSim = float.NegativeInfinity;
            // First sample: nothing to compare against, so allow one more interval.
            a.LastAscending = float.IsNaN(prev) || (prev - s.DepthFt) >= AscentProgressFeet;
        }

        /// <summary>
        /// Aircraft half of <see cref="SampleShooterProgress"/>: emits envelope-track and maintains
        /// the same progress verdict. False when the shooter is not a readable aircraft.
        ///
        /// <see cref="Scheduled.LastDepthFt"/> carries the altitude here rather than a depth. The two
        /// share a convention that makes the progress test identical: both quantities DECREASE as
        /// the shooter approaches its launch envelope, a boat rising and an aircraft descending.
        /// </summary>
        private static bool SampleAircraftProgress(Scheduled a, Intent it, float simNow,
                                                   bool maintainVerdict)
        {
            if (!LaunchEnvelope.TrySnapshotAircraft(it.Unit, it.AmmoId, out LaunchEnvelope.AirSnapshot s))
                return false;
            LaunchEnvelope.AddTargetGeometry(ref s, it.Unit, it.Target);

            float prev = a.LastDepthFt;
            if (VerboseLog && a.FiredAtSim >= 0f)
            {
                float rate = float.IsNaN(prev) ? float.NaN
                           : (prev - s.AltFt) / Mathf.Max(0.001f, simNow - a.LastDepthSim);
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-track {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                    $"t+{simNow - a.FiredAtSim:0}s {s.AltFt:0}ft p{s.PitchDeg:0.0} " +
                    $"r{(float.IsNaN(rate) ? 0f : rate):0.00}ft/s tas {s.TasKn:0}kn " +
                    $"mach {s.Mach:0.000} (cmd {(float.IsNaN(s.CmdMach) ? 0f : s.CmdMach):0.000}) " +
                    $"gate {s.GateFt:0}ft aboveGate {s.AboveGate}" +
                    // D4: roll is the quantity the launcher's WaitingForRoll state is named after and
                    // nothing recorded it; off-bore says whether the nose is anywhere near the target.
                    $" | roll {s.RollDeg:+0.0;-0.0}deg hdg {s.HeadingDeg:0}deg" +
                    (float.IsNaN(s.OffBoreDeg) ? "" :
                        $" offBore {s.OffBoreDeg:+0;-0}deg rng {s.RangeKm:0.0}km") +
                    // D10: the game's own terms. EngageSurfaceContact refuses the shot on
                    // `!_directPoint && PitchAngle < -10`, plus a dive-attack pitch-RATE gate
                    // re-enabled at `|PitchError| < 3`. Printed next to our own pitch above so the
                    // two sign conventions can be compared directly rather than assumed.
                    (s.HasMotion
                        ? $" | mcPitch {s.McPitchAngle:+0.0;-0.0} rate {s.McPitchRate:+0.0;-0.0}" +
                          $" err {s.McPitchError:+0.0;-0.0} directPoint {s.McDirectPoint}"
                        : " | mc n/a") +
                    // D12: is the climb commanded, and is the attack state machine driving it?
                    (float.IsNaN(s.DesiredAltFt) ? ""
                        : $" | cmdAlt {s.DesiredAltFt:0}ft diveAttack {s.InDiveAttack}"));
            }
            a.LastDepthFt = s.AltFt;
            a.LastDepthSim = simNow;

            if (!maintainVerdict) return true;

            if (!s.AboveGate)
            {
                // Inside the launch band. Same handoff problem as the submarine clearing its ceiling:
                // the launcher needs a moment to start its cycle, and that read is cached in real
                // seconds. Hold for the same grace rather than dropping the order in the gap.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return true;
            }

            a.UnblockedAtSim = float.NegativeInfinity;
            a.LastAscending = float.IsNaN(prev) || (prev - s.AltFt) >= AscentProgressFeet;
            return true;
        }

        private static void UpdateAnchorTracking(float simNow)
        {
            if (_scheduled.Count == 0) return;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled a = _scheduled[i];
                if (!a.IsAnchor || !a.Fired || a.RippleDone) continue;
                Intent it = a.Item;
                // Dead shooter: ReleaseDueLaunches drops the entry; held orders keep the last
                // prediction already written into their ImpactAtSim.
                if (it.Unit == null || it.Unit.IsDestroyed) continue;
                // Dead anchor TARGET, post-launch. Re-election is impossible once the anchor has
                // fired, because its ripple IS the clock. With one target that only affects the
                // anchor's own shot; with several it would strand every surviving target on a
                // prediction that stops being updated while its followers keep waiting. So lock the
                // strike on the last prediction now and say so, rather than letting it drift.
                if (it.Target == null || it.Target.IsDestroyed)
                {
                    a.RippleDone = true;
                    NoteScheduleExit(a, simNow, "anchor target destroyed after launch");
                    _scheduled.RemoveAt(i);
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchor target lost after launch ({it.AmmoId} from " +
                        $"{UnitNaming.SafeName(it.Unit)}); impact locked at sim {a.ImpactAtSim:0.0} " +
                        $"for the {(a.Followers?.Count ?? 0)} order(s) still held.");
                    continue;
                }

                int k = a.LaunchTimes.Count;
                int n = Mathf.Max(1, a.AnchorShots);

                // Ascent sampling runs from the moment the anchor fires, not from the stall check,
                // so envelope-track covers the whole climb and lines up with envelope-sim.
                // D4 of docs/plans/open/anchor-liveness.md: both traces used to stop the moment the
                // first round left (`k == 0`), so the gap between rounds was a total blind spot. The
                // 2026-09-07c straight-line run put 49.5s and a full climb-and-descent in that gap
                // and produced zero lines describing it. They now run until the ripple is done.
                // asc-residual: the one number that says whether the ascent model is good.
                // Observed order-to-first-round against what was predicted at commit, scored once
                // when the first launch lands. Positive = the boat took longer than predicted.
                if (VerboseLog && k >= 1 && !a.LoggedEnvelopeResidual && it.EnvelopeLead > 0f && a.FiredAtSim >= 0f)
                {
                    a.LoggedEnvelopeResidual = true;
                    float observed = a.LaunchTimes[0] - a.FiredAtSim;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] envelope-residual {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                        $"observed {observed:0.0}s, predicted {it.StartupLead:0.0}s " +
                        $"(transit {it.EnvelopeLead:0.0}s " +
                        $"+ launcher cycle {LauncherFactsSource.LauncherCycleSeconds(it.Unit, it.AmmoId):0.0}s), " +
                        $"residual {observed - it.StartupLead:+0.0;-0.0}s");
                }

                // Live cadence: measured once 2+ launches are in; the INI interval seeds k<=1.
                float interval = a.IniInterval;
                if (k >= 2) interval = (a.LaunchTimes[k - 1] - a.LaunchTimes[0]) / (k - 1);
                if (interval <= 0f) interval = a.IniInterval > 0f ? a.IniInterval : LauncherFactsSource.FallbackShotInterval;

                // Placed after `interval` because the liveness line prints the stall window, which
                // is derived from it. Runs for the whole order now, not only while nothing has
                // launched: the gap BETWEEN rounds was the blind spot, and on the 2026-09-07c
                // straight-line run it held 49.5s and a full climb-and-descent with no trace at all.
                // Past the first round this sampling exists ONLY to feed the trace, so it does not
                // run at all with verbose logging off. Before the first round it is load-bearing:
                // it maintains the verdict the no-launch stall reads.
                if (a.FiredAtSim >= 0f && (k == 0 || VerboseLog))
                {
                    // `maintainVerdict` stays false once the ripple has started, which keeps this a
                    // DIAGNOSTICS-ONLY change. SampleShooterProgress writes LastAscending, and
                    // AnchorStillWorking reads it in the mid-ripple stall arm, so sampling it for
                    // k > 0 as well would alter when a stalled ripple is declared. That is a real
                    // behaviour change, it is not what this DLL is for, and it would confound the
                    // very run these traces exist to observe.
                    SampleShooterProgress(a, it, simNow, maintainVerdict: k == 0);
                    LogAnchorLiveness(a, it, simNow, k, n, interval);
                }

                // Prediction cache: reuse while the ripple state (k, interval) is unchanged AND
                // the entry is fresh within the sim TTL. An expired entry re-runs
                // PredictAnchorImpact so the live flight estimate tracks shooter/target motion
                // between launches. (An earlier int-mixed key collided once cadence >= 10 s and
                // had no TTL at all, freezing the prediction between launches.)
                PredictKey cacheKey = new PredictKey { Launches = k, IntervalMilli = Mathf.RoundToInt(interval * PredictKeyMilliScale) };
                float pred;
                if (a.HasPredict && a.Predict.Key.Equals(cacheKey) &&
                    (simNow - a.Predict.StampSim) < PredictCacheTtlSim)
                {
                    pred = a.Predict.Value;
                }
                else
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.AnchorPredict);
                    pred = PredictAnchorImpact(a, it, k, n, interval, simNow, out AnchorPredictTerms terms);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.AnchorPredict);
                    a.Predict = new PredictCacheEntry { Key = cacheKey, StampSim = simNow, Value = pred };
                    a.HasPredict = true;
                    // D1/D2/D4. Only on the fresh-compute path: a cache hit returns the same number
                    // it returned last time, so there is nothing to report and the terms would be
                    // stale anyway.
                    WarnOnImpactAnomaly(a, it, pred, terms, simNow);
                    LogAnchorSlide(a, it, pred, terms, simNow);
                }
                // Held orders in this batch follow the live prediction; their release condition is
                // re-evaluated against it every tick. Uses anchor->followers index for O(followers).
                if (a.Followers != null)
                {
                    LogImpactMove(a, it, pred, simNow, "tracking");
                    for (int j = 0; j < a.Followers.Count; j++)
                        a.Followers[j].ImpactAtSim = pred;
                }
                // Every target in the strike, not just the anchor's: the followers aimed elsewhere
                // are being retimed above, so their board rows have to follow or they show a frozen
                // impact and a drifting ETA for a shot that is in fact still on schedule.
                if (a.BoardTargets != null)
                {
                    for (int j = 0; j < a.BoardTargets.Count; j++)
                        EngagementBoard.UpdateImpact(a.BoardTargets[j], pred);
                }
                else
                {
                    EngagementBoard.UpdateImpact(it.Target, pred);
                }

                bool complete = k >= n;
                // Stall = launches stopped (gated/short), or NOTHING launched for a long while
                // (launcher inoperable, guidance wait, ship still turning into its firing arc).
                //
                // Both arms consult AnchorStillWorking. The mid-ripple arm used not to, and a Kynda
                // reported 2/8 while its remaining 6 rounds were still on the way: its real gap
                // between pairs is far longer than StallMinWindowSim, so the timer expired mid-salvo
                // and the batch impact was then anchored on a 2-round sample. The launcher stating
                // it is mid cycle outranks any timer. AnchorStillWorking self-limits at
                // NoLaunchMaxHoldSim from the first round, so a launcher that truly dies still ends.
                bool stalled = k > 0
                    ? (simNow - a.LaunchTimes[k - 1]) > Mathf.Max(StallCadenceMultiplier * interval, StallMinWindowSim)
                      && !AnchorStillWorking(a, it, simNow)
                    : a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchStallSim
                      && !AnchorStillWorking(a, it, simNow);
                if (complete || stalled)
                {
                    a.RippleDone = true;
                    NoteScheduleExit(a, simNow, complete ? "ripple complete" : "ripple stalled");
                    _scheduled.RemoveAt(i);   // its caches are fields, so they go with it
                    float span = (k > 1) ? a.LaunchTimes[k - 1] - a.LaunchTimes[0] : 0f;

                    // A stalled ripple is finalized on the rounds that ACTUALLY flew, which is what
                    // the log line has always claimed. It used to keep a prediction extrapolated
                    // over the rounds that never left, so an order that fired 1 of 2 had its impact
                    // set from a modelled second launch that never happened. Re-predicting with
                    // n = k also lets the live-round path answer, since every round is now away by
                    // definition. See docs/plans/open/aircraft-anchor-impact-slide.md.
                    if (stalled && !complete && k > 0)
                    {
                        // D6 of the beta-release audit, before the re-prediction so the terms
                        // reported are the ones it is about to use.
                        LogStallCentering(a, it, n, k, span, interval);
                        // Centering from what flew, not from what was ordered. k == 1 spans nothing,
                        // so it centers on the single arrival, which is the same answer a one-round
                        // order would have produced at commit.
                        float observedCentering = (k > 1) ? span * 0.5f : 0f;
                        float onObserved = PredictAnchorImpact(a, it, k, k, interval, simNow,
                                                               out AnchorPredictTerms stallTerms,
                                                               observedCentering);
                        if (stallTerms.Valid) pred = onObserved;
                        LogImpactMove(a, it, pred, simNow, "stall-finalize");
                        if (a.Followers != null)
                            for (int j = 0; j < a.Followers.Count; j++)
                                a.Followers[j].ImpactAtSim = pred;
                        if (a.BoardTargets != null)
                            for (int j = 0; j < a.BoardTargets.Count; j++)
                                EngagementBoard.UpdateImpact(a.BoardTargets[j], pred);
                        else
                            EngagementBoard.UpdateImpact(it.Target, pred);
                    }

                    // Nothing launched: there is no shot to anchor on, so the prediction written to
                    // the board every tick above is for an impact that will never happen.
                    //
                    // This used to drop one board row and warn, and nothing else. The followers were
                    // freed (RippleDone releases the hold) but kept the phantom ImpactAtSim, so they
                    // released against a time no round was going to make, having already burned their
                    // own release windows waiting out the anchor's full hold. That is the 65s to 92s
                    // past-due dump seen in three separate 2026-09-07 logs. Two more gaps sat beside
                    // it: only `it.Target` was dropped rather than every row in `BoardTargets`, and
                    // PromoteNewAnchor could never run here, because its call site requires the
                    // shooter or target to be DESTROYED and is additionally guarded by `!s.Fired`,
                    // while this anchor is alive and has fired its order.
                    //
                    // So: hand the strike to the longest-enroute survivor and give it an impact it
                    // can actually make. Only drop the rows when there is no survivor to take over.
                    // See docs/plans/open/anchor-liveness.md.
                    if (k == 0)
                    {
                        bool rehomed = PromoteAbandonedAnchor(a, simNow);
                        if (!rehomed)
                        {
                            if (a.BoardTargets != null)
                                for (int j = 0; j < a.BoardTargets.Count; j++)
                                    EngagementBoard.Drop(a.BoardTargets[j]);
                            else
                                EngagementBoard.Drop(it.Target);
                        }
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] order abandoned {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} -> " +
                            $"{UnitNaming.SafeName(it.Target)}: nothing launched, " +
                            (rehomed
                                ? "strike re-anchored on the longest-enroute survivor."
                                : "no survivor to re-anchor on, engagement dropped so other " +
                                  "shooters are not held against it.") +
                            SubmarineFacts.Describe(it.Unit, it.AmmoId));
                    }
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchored {UnitNaming.SafeName(it.Target)}: {k}/{n} launched over {span:0.0}s " +
                        $"(cadence {interval:0.0}s), impact set to sim {pred:0.0}" +
                        // D1's endpoint. The per-move trace is thresholded, so the total is stated
                        // here explicitly rather than left to be diffed out of the first and last
                        // anchor-slide lines. A ship anchor should read about 0.0s.
                        (VerboseLog && !float.IsNaN(a.FirstPred)
                            ? $" (slid {pred - a.FirstPred:+0.0;-0.0}s from {a.FirstPred:0.0}, " +
                              $"anchor {PlatformTag(it.Unit)}, {ShooterDisplacement(a, it, simNow)})"
                            : "") +
                        (stalled && !complete ? ", ripple stalled, anchored on launches observed" : "") +
                        (stalled && k == 0 ? " (NOTHING launched)" : "") +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));

                    // Diagnostic: the range-aware τ_form model's internals (now the LIVE model, so
                    // `candidate` == `applied groupDelay`). Kept for sanity-checking modded/untested
                    // missiles ; watch that `candidate` tracks the observed residual.
                    if (VerboseLog && it.Grouped && span > 0f &&
                        FlightTime.GroupFormingTauDiag(it.Unit, it.AmmoId, it.Target, span,
                            out float pSpan, out float tauForm, out float candidate))
                    {
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] group-tau {it.AmmoId}: span {span:0.0}s, Pspan {pSpan:0}, " +
                            $"2.5Pspan {2.5f * pSpan:0}, tauForm {tauForm:0.0}s, candidate {candidate:0.0}s " +
                            $"(applied groupDelay {GroupDelay(it, span):0.0}s)");
                    }
                }
                // Submarines re-log on a sim cadence even when k has not moved: a boat holding at
                // 0/8 while it comes up to launch depth is otherwise one line and no trace at all,
                // and "rising slowly" vs "not rising and never will" is the whole question.
                else if (VerboseLog && (k != a.LastLoggedLaunches ||
                         (SubmarineFacts.IsSubmarine(it.Unit) &&
                          simNow - a.LastSubLogSim >= TelemetryCadence.SampleIntervalSim)))
                {
                    a.LastLoggedLaunches = k;
                    a.LastSubLogSim = simNow;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchoring {UnitNaming.SafeName(it.Target)}: {k}/{n} launched, " +
                        $"cadence {interval:0.0}s, impact predicted sim {pred:0.0}" +
                        SubmarineFacts.Describe(it.Unit, it.AmmoId));
                }
            }
        }

        /// <summary>
        /// One exit for an anchor that will never finish its ripple, whatever removed it: a
        /// destroyed shooter, a destroyed target, or a dispatch the game refused.
        ///
        /// Three cases, and the middle one is finding 1 of
        /// docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
        ///
        /// <list type="bullet">
        /// <item>Not yet released. Nothing has changed for the survivors, so the impact stands and
        /// <see cref="PromoteNewAnchor"/> hands the role over with it.</item>
        /// <item>Released, no round observed. This was the unrecoverable one. The entry left
        /// <c>_scheduled</c> with <c>Fired</c> set and <c>RippleDone</c> false, and only
        /// UpdateAnchorTracking sets <c>RippleDone</c>, which walks <c>_scheduled</c>. So the
        /// followers held on an anchor that no loop could reach any more, with no timeout, for the
        /// rest of the mission. They are freed here and the strike is re-anchored on the
        /// longest-enroute survivor, retimed from now, because the old impact came from a shot that
        /// never existed.</item>
        /// <item>Released with rounds away. The last prediction was built from real launches, so it
        /// is the best answer available and it is locked. Followers free on RippleDone.</item>
        /// </list>
        ///
        /// Idempotent: an anchor already marked RippleDone is left alone.
        /// </summary>
        private static void RetireAnchor(Scheduled lost, float simNow, string reason)
        {
            if (lost == null || !lost.IsAnchor || lost.RippleDone) return;

            if (!lost.Fired)
            {
                PromoteNewAnchor(lost, simNow);
                return;
            }

            lost.RippleDone = true;   // whatever follows, no follower may wait on this entry again

            if (lost.LaunchTimes.Count > 0)
            {
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] anchor retired {lost.Item?.AmmoId} from " +
                        $"{UnitNaming.SafeName(lost.Item?.Unit)} ({reason}) with " +
                        $"{lost.LaunchTimes.Count} round(s) away; impact locked at sim " +
                        $"{lost.ImpactAtSim:0.0} for the orders still held.");
                return;
            }

            bool rehomed = PromoteAbandonedAnchor(lost, simNow);
            if (!rehomed)
            {
                if (lost.BoardTargets != null)
                    for (int j = 0; j < lost.BoardTargets.Count; j++)
                        EngagementBoard.Drop(lost.BoardTargets[j]);
                else if (lost.Item?.Target != null)
                    EngagementBoard.Drop(lost.Item.Target);
            }

            Bootstrap.Log.LogWarning(
                $"[AutoTOT] anchor retired {lost.Item?.AmmoId} from " +
                $"{UnitNaming.SafeName(lost.Item?.Unit)} ({reason}) having launched nothing: " +
                (rehomed
                    ? "strike re-anchored on the longest-enroute survivor and retimed."
                    : "no survivor to re-anchor on, engagement dropped so other shooters are not " +
                      "held against it."));
        }

        /// <summary>
        /// Re-elect an anchor after the sitting one is abandoned having launched NOTHING, and give
        /// the strike an impact time the survivors can still make.
        ///
        /// Distinct from <see cref="PromoteNewAnchor"/> in the one respect that matters. There the
        /// impact is deliberately held, because it was derived from the lost anchor's enroute time,
        /// which was the largest, so every survivor could still make it. Here that reasoning fails:
        /// the impact came from a shot that never existed, and the survivors have been holding
        /// station against it for the anchor's entire no-launch window, which on the 2026-09-07 runs
        /// was the full 300 s hard cap. By the time they were freed they were already 65 s to 92 s
        /// past their own release points and were dumped as a batch that could not arrive together.
        ///
        /// So the impact is recomputed from the new anchor's enroute time at the current moment,
        /// which is by construction the longest remaining shot and therefore a time every survivor
        /// can still meet.
        ///
        /// False when nothing can take over, which is the caller's signal to drop the board rows.
        /// </summary>
        private static bool PromoteAbandonedAnchor(Scheduled lost, float simNow)
        {
            List<Scheduled> followers = lost.Followers;
            if (followers == null || followers.Count == 0) return false;

            Scheduled best = null;
            float bestEnroute = float.NegativeInfinity;
            var survivors = new List<Scheduled>(followers.Count);
            foreach (Scheduled f in followers)
            {
                Intent fi = f.Item;
                // A follower that has already released is beyond help: its round is in the air and
                // no impact time can reach it. It is also not a candidate to anchor anything.
                if (f.Fired) continue;
                if (fi.Unit == null || fi.Unit.IsDestroyed || fi.Target == null || fi.Target.IsDestroyed)
                    continue;
                survivors.Add(f);
                float flightEst = f.LastFlightEst >= 0f
                    ? f.LastFlightEst
                    : FlightTime.Estimate(fi.Unit, fi.AmmoId, fi.Target);
                if (flightEst <= FlightTime.MinValidSeconds) continue;
                float enroute = flightEst + fi.ReleaseLead + fi.StartupLead + GroupDelay(fi, fi.ReleaseLead);
                if (enroute > bestEnroute) { bestEnroute = enroute; best = f; }
            }
            if (best == null || bestEnroute <= 0f) return false;

            // The new shared impact: the longest remaining shot, timed from now. Every survivor can
            // meet it by definition, which is exactly what the phantom could not offer.
            float pred = simNow + bestEnroute;

            best.IsAnchor = true;
            best.AnchorShots = Mathf.Max(1, best.Item.AnchorShots);
            LauncherFactsSource.Facts bf = LauncherFactsSource.Get(best.Item.Unit, best.Item.AmmoId);
            best.IniInterval = (bf.Valid && bf.ShotInterval > 0f) ? bf.ShotInterval : LauncherFactsSource.FallbackShotInterval;
            best.Anchor = null;
            best.HasPredict = false;
            best.LoggedAnchorWait = false;
            best.ImpactAtSim = pred;

            var newFollowers = new List<Scheduled>(survivors.Count);
            foreach (Scheduled f in survivors)
            {
                if (f == best) continue;
                f.Anchor = best;
                f.LoggedAnchorWait = false;
                f.ImpactAtSim = pred;
                newFollowers.Add(f);
            }
            best.Followers = newFollowers;

            var boards = new List<ObjectBase>();
            if (lost.BoardTargets != null)
                foreach (ObjectBase t in lost.BoardTargets)
                    if (t != null && !t.IsDestroyed) boards.Add(t);
            best.BoardTargets = boards;
            for (int j = 0; j < boards.Count; j++) EngagementBoard.UpdateImpact(boards[j], pred);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchor re-elected after abandonment: {lost.Item.AmmoId} from " +
                $"{UnitNaming.SafeName(lost.Item.Unit)} launched nothing; " +
                $"{best.Item.AmmoId} from {UnitNaming.SafeName(best.Item.Unit)} -> " +
                $"{UnitNaming.SafeName(best.Item.Target)} takes over ({bestEnroute:0.0}s " +
                $"enroute) for {newFollowers.Count} follower(s), impact RETIMED from sim " +
                $"{lost.ImpactAtSim:0.0} to sim {pred:0.0}.");
            return true;
        }

        /// <summary>
        /// Re-elect an anchor after the sitting one is dropped BEFORE it released anything (its
        /// shooter or its target died while the strike was still being held).
        ///
        /// With one target per strike this never mattered: losing the anchor's target lost the only
        /// engagement. A multi-target strike is different. The other targets' orders are still live,
        /// still held, and still keyed to an anchor that will now never launch, so without this they
        /// coast forever on the impact time fixed at commit. The longest-enroute survivor takes over,
        /// exactly as <see cref="Schedule"/> would have chosen it had the loser never been there.
        ///
        /// The shared impact time is deliberately NOT recomputed. It was set from the lost anchor's
        /// enroute time, which was the largest, so every survivor can still make it; re-deriving it
        /// would pull the whole strike earlier and could put a shot past due.
        /// </summary>
        private static void PromoteNewAnchor(Scheduled lost, float simNow)
        {
            List<Scheduled> followers = lost.Followers;
            if (followers == null || followers.Count == 0) return;

            Scheduled best = null;
            float bestEnroute = float.NegativeInfinity;
            var survivors = new List<Scheduled>(followers.Count);
            foreach (Scheduled f in followers)
            {
                Intent fi = f.Item;
                if (fi.Unit == null || fi.Unit.IsDestroyed || fi.Target == null || fi.Target.IsDestroyed)
                    continue;   // its own drop pass will deal with it
                survivors.Add(f);
                // Reuse the estimate the release path already holds where there is one; this runs in
                // a frame that is already doing removal work, and a fresh sim per survivor is not
                // worth it to order a list.
                float flightEst = f.LastFlightEst >= 0f
                    ? f.LastFlightEst
                    : FlightTime.Estimate(fi.Unit, fi.AmmoId, fi.Target);
                float enroute = flightEst + fi.ReleaseLead + fi.StartupLead + GroupDelay(fi, fi.ReleaseLead);
                if (enroute > bestEnroute) { bestEnroute = enroute; best = f; }
            }
            if (best == null) return;

            best.IsAnchor = true;
            best.AnchorShots = Mathf.Max(1, best.Item.AnchorShots);
            LauncherFactsSource.Facts f2 = LauncherFactsSource.Get(best.Item.Unit, best.Item.AmmoId);
            best.IniInterval = (f2.Valid && f2.ShotInterval > 0f) ? f2.ShotInterval : LauncherFactsSource.FallbackShotInterval;
            best.Anchor = null;
            best.HasPredict = false;
            best.LoggedAnchorWait = false;

            var newFollowers = new List<Scheduled>(survivors.Count);
            foreach (Scheduled f in survivors)
            {
                if (f == best) continue;
                f.Anchor = best;
                f.LoggedAnchorWait = false;
                newFollowers.Add(f);
            }
            best.Followers = newFollowers;

            // Carry the board rows over, minus the one whose target is gone.
            var boards = new List<ObjectBase>();
            if (lost.BoardTargets != null)
                foreach (ObjectBase t in lost.BoardTargets)
                    if (t != null && !t.IsDestroyed) boards.Add(t);
            best.BoardTargets = boards;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchor re-elected before release: {lost.Item.AmmoId} from " +
                $"{UnitNaming.SafeName(lost.Item.Unit)} is gone; " +
                $"{best.Item.AmmoId} from {UnitNaming.SafeName(best.Item.Unit)} -> " +
                $"{UnitNaming.SafeName(best.Item.Target)} takes over " +
                $"({bestEnroute:0.0}s enroute) for {newFollowers.Count} follower(s), " +
                $"impact held at sim {best.ImpactAtSim:0.0}.");
        }
    }
}
