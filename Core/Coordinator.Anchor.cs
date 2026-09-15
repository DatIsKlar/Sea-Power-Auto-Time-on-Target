using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Observation anchoring: watch what the anchor actually launches and move the shared impact to match,
    /// rather than trusting the commit estimate. The release half is in Coordinator.Release.cs.
    /// </summary>
    internal static partial class Coordinator
    {
        /// <summary>
        /// The shared impact extrapolated from the anchor's launches: the last round's launch time (observed,
        /// or projected from the measured cadence) plus its flight estimate, minus the centring lead for
        /// non-grouped salvos. Returns the entry's current impact until a launch and a valid estimate exist.
        ///
        /// Rule: <b>pair a launch time with a flight estimate valid at that launch time.</b> A round already
        /// away uses an estimate integrated from its recorded <see cref="FlightTime.LaunchState"/>; a round
        /// still to come uses a launch time extrapolated from the cadence (clamped to now) and an estimate
        /// from the shooter's current position. Ships and aircraft run the same code, so the impact cannot step.
        /// </summary>
        /// <param name="centeringOverride">
        /// Centring to use instead of the commit-time <see cref="Intent.ReleaseLead"/>; negative means use
        /// the commit value, which is every caller but the stall finalizer.
        /// </param>
        private static float PredictAnchorImpact(Scheduled a, Intent it, int k, int n, float interval,
                                                 float simNow, out AnchorPredictTerms terms,
                                                 float centeringOverride = -1f)
        {
            terms = default;
            // Nothing launched yet: the answer is the current impact, so no estimate is taken.
            if (k <= 0) return a.ImpactAtSim;

            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictFlight);
            float est = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictFlight);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictFlightCalls);
            // Read now: a later solve overwrites the tier, and a cache hit runs no tier at all.
            bool estCached = FlightTime.WasLastCallCacheHit;
            ModelStats.Tier estTier = ModelStats.LastTier;
            
            if (est <= FlightTime.MinValidSeconds) return a.ImpactAtSim;

            float lastLaunch = a.LaunchTimes[k - 1];
            bool allAway = k >= n;
            // Only the extrapolated part is clamped; an observed timestamp is a measurement.
            float lastRoundLaunch = allAway
                ? lastLaunch
                : Mathf.Max(lastLaunch + interval * (n - k), simNow);
            float span = lastRoundLaunch - a.LaunchTimes[0];
            
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.PredictGroupDelay);
            float groupDelay = GroupDelay(it, span);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.PredictGroupDelay);
            CoordinatorProfiler.Count(CoordinatorProfiler.Counter.PredictGroupDelayCalls);

            // The last round's launch state is a measurement only once it is away; a round still to come has none.
            float snapshot = allAway ? LaunchStateEstimate(a, it, k - 1, est) : -1f;
            bool fromLaunchState = snapshot > FlightTime.MinValidSeconds;
            float flight = fromLaunchState ? snapshot : est;
            // An independent salvo releases half a ripple early and the prediction subtracts that half back.
            // ReleaseLead describes the ordered salvo; a stalled ripple passes what the launches actually
            // spanned. A pooled anchor's rounds are not one contiguous ripple, so its centre is measured from
            // the pool's first round, which is the anchor's: impact = first launch + pooled lead + PoolDelta
            // + flight, written as a centring so the anchor-slide line still adds up. See PoolSharedLaunchClock.
            float lead = centeringOverride >= 0f ? centeringOverride : it.ReleaseLead;
            float centering = it.Grouped ? 0f
                            : it.Pooled ? span - lead - it.PoolDelta
                            : lead;

            terms = new AnchorPredictTerms
            {
                Valid = true,
                Est = flight,
                Tier = estTier,
                Cached = estCached,
                LastRoundLaunch = lastRoundLaunch,
                Centering = centering,
                GroupDelay = groupDelay,
                FromLaunchState = fromLaunchState,
            };
            return lastRoundLaunch + flight - centering + groupDelay;
        }

        /// <summary>
        /// Flight estimate for a launched round, integrated from its shooter's state at launch; -1 when none
        /// was recorded. Memoised on the observation, since it is a constant of the launch.
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

            // No plausibility ceiling, on purpose: the live estimate comes from where the shooter has since
            // got to, so correct and wrong snapshots both differ from it by large, overlapping ratios.
            LogLaunchSnapshot(o, it, est, liveEst);

            o.Est = est > FlightTime.MinValidSeconds ? est : -1f;
            a.LaunchObs[index] = o;
            return o.Est;
        }

        /// <summary>
        /// Launcher-intent form for callers without a <see cref="Scheduled"/> (the shortfall accounting): a
        /// launcher mid cycle, or a submarine still below its launch ceiling. It cannot tell a climbing boat
        /// from a parked one, so callers need their own absolute bound.
        /// </summary>
        internal static bool ShooterStillWorking(ObjectBase unit, string ammoId)
        {
            if (unit == null || ammoId == null) return false;
            if (LauncherFactsSource.IsPreparingToFire(unit, ammoId)) return true;
            return SubmarineFacts.TrySnapshot(unit, ammoId, out SubmarineFacts.Snapshot s) && s.LaunchBlocked;
        }

        /// <summary>
        /// True while a shooter that has launched nothing is still visibly working toward its first round, so
        /// the no-launch stall holds past <see cref="NoLaunchStallSim"/>, up to
        /// <see cref="NoLaunchMaxHoldSim"/>. Signals, in order: the launcher is mid cycle
        /// (<see cref="LauncherFactsSource.IsPreparingToFire"/>); a depth-blocked submarine has risen since
        /// the last sample; or it cleared the block within <see cref="PostUnblockGraceSim"/>, covering the
        /// handoff between the first two. An idle launcher or a boat parked below its ceiling does not count,
        /// so a stuck order still ends. Named apart from
        /// <see cref="ShooterStillWorking(ObjectBase, string)"/> because the two answer different questions.
        /// </summary>
        internal static bool AnchorStillWorking(Scheduled a, Intent it, float simNow)
        {
            if (a.FiredAtSim >= 0f && (simNow - a.FiredAtSim) > NoLaunchMaxHoldSim) return false;
            if (LauncherFactsSource.IsPreparingToFire(it.Unit, it.AmmoId)) return true;
            // Maintained every tick by SampleShooterProgress from the moment the anchor fires.
            return a.LastAscending;
        }

        /// <summary>
        /// Sample the shooter's ascent every <see cref="DepthSampleIntervalSim"/>, maintaining the verdict
        /// <see cref="AnchorStillWorking"/> returns and emitting envelope-track. Self-gating.
        /// </summary>
        private static void SampleShooterProgress(Scheduled a, Intent it, float simNow,
                                                  bool maintainVerdict = true)
        {
            if (it.Unit == null || it.Unit.IsDestroyed) return;
            if (simNow - a.LastDepthSim < DepthSampleIntervalSim) return;

            if (!SubmarineFacts.TrySnapshot(it.Unit, it.AmmoId, out SubmarineFacts.Snapshot s))
            {
                if (SampleAircraftProgress(a, it, simNow, maintainVerdict)) return;
                // Not a readable submarine or aircraft: nothing to hold on; the launcher-intent test still applies.
                a.LastDepthSim = simNow;
                if (maintainVerdict) a.LastAscending = false;
                return;
            }

            float prev = a.LastDepthFt;
            // envelope-track reads against envelope-sim. The rate spans the sample interval, so the first
            // sample has none.
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
                // In the launch envelope but not yet seen cycling: hold for PostUnblockGraceSim over the handoff.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return;
            }

            // Back below the ceiling: the grace starts again.
            a.UnblockedAtSim = float.NegativeInfinity;
            // First sample: nothing to compare against, so allow one more interval.
            a.LastAscending = float.IsNaN(prev) || (prev - s.DepthFt) >= AscentProgressFeet;
        }

        /// <summary>
        /// Aircraft half of <see cref="SampleShooterProgress"/>. <see cref="Scheduled.LastDepthFt"/> holds
        /// the altitude here: depth and altitude both decrease toward the launch envelope, so the progress
        /// test is the same. False when the shooter is not a readable aircraft.
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
                    // Roll, which the WaitingForRoll state waits on, and off-bore, which says whether the
                    // nose is near the target.
                    $" | roll {s.RollDeg:+0.0;-0.0}deg hdg {s.HeadingDeg:0}deg" +
                    (float.IsNaN(s.OffBoreDeg) ? "" :
                        $" offBore {s.OffBoreDeg:+0;-0}deg rng {s.RangeKm:0.0}km") +
                    // The game's own terms: EngageSurfaceContact refuses on `!_directPoint && PitchAngle <
                    // -10`, plus a dive-attack pitch-rate gate re-enabled at `|PitchError| < 3`. Printed
                    // beside our pitch so the sign conventions can be compared.
                    (s.HasMotion
                        ? $" | mcPitch {s.McPitchAngle:+0.0;-0.0} rate {s.McPitchRate:+0.0;-0.0}" +
                          $" err {s.McPitchError:+0.0;-0.0} directPoint {s.McDirectPoint}"
                        : " | mc n/a") +
                    // Whether the climb is commanded, and whether the attack state machine drives it.
                    (float.IsNaN(s.DesiredAltFt) ? ""
                        : $" | cmdAlt {s.DesiredAltFt:0}ft diveAttack {s.InDiveAttack}"));
            }
            a.LastDepthFt = s.AltFt;
            a.LastDepthSim = simNow;

            if (!maintainVerdict) return true;

            if (!s.AboveGate)
            {
                // Inside the launch band: hold for the same grace while the launcher starts its cycle.
                if (float.IsNegativeInfinity(a.UnblockedAtSim)) a.UnblockedAtSim = simNow;
                a.LastAscending = (simNow - a.UnblockedAtSim) < PostUnblockGraceSim;
                return true;
            }

            a.UnblockedAtSim = float.NegativeInfinity;
            a.LastAscending = float.IsNaN(prev) || (prev - s.AltFt) >= AscentProgressFeet;
            return true;
        }

        /// <summary>
        /// Observation anchoring. The anchor (longest enroute) releases first; its actual launches give the
        /// live cadence and rewrite the shared impact every held order releases against. A grouped salvo
        /// lands when its last round's solo flight ends (MissileGroup.cs:106-141, Missile.cs:839-842), so the
        /// last launch predicts the group. Cadence is measured, since no ini declares it.
        ///
        ///   interval  = (lastLaunch - firstLaunch) / (k - 1)   once k >= 2, else the ini interval
        ///   lastRound = lastLaunch + interval * (n - k)         while k < n, else lastLaunch
        ///   impact    = lastRound + liveEstimate - centring     (ReleaseLead for independent salvos, 0 grouped)
        ///
        /// Finalizes when the first wave has launched (k >= n) or launches stall.
        /// </summary>
        private static void UpdateAnchorTracking(float simNow)
        {
            if (_scheduled.Count == 0) return;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled a = _scheduled[i];
                if (!a.IsAnchor || !a.Fired || a.RippleDone) continue;
                Intent it = a.Item;
                // Dead shooter: ReleaseDueLaunches drops the entry; held orders keep the last prediction.
                if (it.Unit == null || it.Unit.IsDestroyed) continue;
                // Dead anchor target after it fired. With rounds away its ripple is the clock, so the strike
                // locks on the last prediction; with nothing away, RetireAnchor re-anchors the survivors instead.
                if (it.Target == null || it.Target.IsDestroyed)
                {
                    _scheduled.RemoveAt(i);
                    if (a.LaunchTimes.Count > 0)
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] anchor target lost after launch ({it.AmmoId} from " +
                            $"{UnitNaming.SafeName(it.Unit)}); impact locked at sim {a.ImpactAtSim:0.0} " +
                            $"for the {(a.Followers?.Count ?? 0)} order(s) still held.");
                    RetireAnchor(a, simNow, "anchor target destroyed after launch");
                    NoteScheduleExit(a, simNow, "anchor target destroyed after launch");
                    continue;
                }

                int k = a.LaunchTimes.Count;
                int n = Mathf.Max(1, a.AnchorShots);

                // Sampling runs from the moment the anchor fires until the ripple is done, so envelope-track
                // covers the whole climb and the gaps between rounds. envelope-residual: observed
                // order-to-first-round against the lead the gate used (refreshed while held, frozen at
                // release), scored once at the first launch. Positive = slower than predicted.
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

                // After `interval`, since the liveness line prints the stall window derived from it. Past the
                // first round this sampling only feeds the trace, so it runs only with verbose logging;
                // before it, it maintains the no-launch stall verdict.
                if (a.FiredAtSim >= 0f && (k == 0 || VerboseLog))
                {
                    // maintainVerdict is false once the ripple has started: AnchorStillWorking reads the
                    // verdict in the mid-ripple stall arm, so sampling it then would change when a stall is declared.
                    SampleShooterProgress(a, it, simNow, maintainVerdict: k == 0);
                    LogAnchorLiveness(a, it, simNow, k, n, interval);
                }

                // Reuse the prediction while (k, interval) holds and the entry is within its sim TTL; on
                // expiry re-run, so the live estimate tracks shooter and target motion.
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
                    // Only on a fresh compute: a cache hit repeats the last number.
                    WarnOnImpactAnomaly(a, it, pred, terms, simNow);
                    LogAnchorSlide(a, it, pred, terms, simNow);
                }
                // Held orders follow the live prediction, through the anchor's follower list.
                if (a.Followers != null) LogImpactMove(a, it, pred, simNow, "tracking");
                PublishImpact(a, pred);

                bool complete = k >= n;
                // Stall = launches stopped, or nothing launched for a long while. Both arms consult
                // AnchorStillWorking: a launcher mid cycle outranks the timer (a turning ship's gap between
                // pairs can exceed StallMinWindowSim), and it self-limits at NoLaunchMaxHoldSim.
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
                    FinalizeRipple(a, it, k, n, interval, pred, complete, stalled, simNow);
                }
                // Submarines re-log on a sim cadence even when k is unchanged, to show whether a boat at 0/n is rising.
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
        /// Finish an anchor's ripple: re-predict a stalled one on the rounds that flew, abandon one that
        /// launched nothing, and log the impact. The caller has marked it RippleDone and removed it from _scheduled.
        /// </summary>
        private static void FinalizeRipple(Scheduled a, Intent it, int k, int n, float interval,
                                           float pred, bool complete, bool stalled, float simNow)
        {
            float span = (k > 1) ? a.LaunchTimes[k - 1] - a.LaunchTimes[0] : 0f;

            // A stalled ripple is finalized on the rounds that actually flew (n = k), so every round is away
            // and the live-round path answers.
            if (stalled && !complete && k > 0)
            {
                // Centring from what flew: k == 1 centres on the single arrival. A pooled anchor keeps the
                // pool's centre, less half an interval per round that never left.
                float observedCentering = it.Pooled
                    ? Mathf.Max(0f, it.ReleaseLead - (n - k) * 0.5f * a.IniInterval)
                    : (k > 1) ? span * 0.5f : 0f;
                // Logged before the re-prediction, so it reports the terms about to be used.
                LogStallCentering(a, it, n, k, span, interval, observedCentering);
                float onObserved = PredictAnchorImpact(a, it, k, k, interval, simNow,
                                                       out AnchorPredictTerms stallTerms,
                                                       observedCentering);
                if (stallTerms.Valid) pred = onObserved;
                LogImpactMove(a, it, pred, simNow, "stall-finalize");
                PublishImpact(a, pred);
            }

            // Nothing launched, so the board prediction is for an impact that will never happen;
            // AbandonAnchor re-anchors the followers on the longest-enroute survivor.
            if (k == 0) AbandonAnchor(a, simNow, "nothing launched within the stall window");
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] anchored {UnitNaming.SafeName(it.Target)}: {k}/{n} launched over {span:0.0}s " +
                $"(cadence {interval:0.0}s), impact set to sim {pred:0.0}" +
                // The total slide over the ripple's life; a ship anchor should read about 0 s.
                (VerboseLog && !float.IsNaN(a.FirstPred)
                    ? $" (slid {pred - a.FirstPred:+0.0;-0.0}s from {a.FirstPred:0.0}, " +
                      $"anchor {PlatformTag(it.Unit)}, {ShooterDisplacement(a, it, simNow)})"
                    : "") +
                (stalled && !complete ? ", ripple stalled, anchored on launches observed" : "") +
                (stalled && k == 0 ? " (NOTHING launched)" : "") +
                SubmarineFacts.Describe(it.Unit, it.AmmoId));

            // The group-drag model's terms for the ripple that flew: distance covered during the span,
            // forming time, and the resulting groupDelay. Watch that `candidate` tracks the observed residual.
            if (VerboseLog && it.Grouped && span > 0f &&
                FlightTime.GroupFormingTauDiag(it.Unit, it.AmmoId, it.Target, span,
                    out float pSpan, out float tauForm, out float candidate))
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] group-tau {it.AmmoId}: span {span:0.0}s, Pspan {pSpan:0}, " +
                    $"2.5Pspan {2.5f * pSpan:0}, tauForm {tauForm:0.0}s, candidate {candidate:0.0}s");
            }
        }

        /// <summary>
        /// One exit for an anchor that will never finish its ripple (shooter or target destroyed, dispatch
        /// refused, no-launch stall). Not yet released: <see cref="PromoteAnchor"/> hands the role over and
        /// the impact stands. Released with nothing observed: <see cref="AbandonAnchor"/> re-anchors on the
        /// longest-enroute survivor, retimed from now, since the old impact came from a shot that never
        /// existed. Released with rounds away: the last prediction is locked. Idempotent.
        /// </summary>
        private static void RetireAnchor(Scheduled lost, float simNow, string reason)
        {
            if (lost == null || !lost.IsAnchor || lost.RippleDone) return;

            if (!lost.Fired)
            {
                PromoteAnchor(lost, simNow, retime: false);
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

            AbandonAnchor(lost, simNow, reason);
        }

        /// <summary>
        /// A released anchor that launched nothing: hand the strike to the longest-enroute survivor with an
        /// impact it can make, or, with no survivor, drop the unfired board rows. A fired row is left for the
        /// prune, since a follower's rounds may still be in flight at its target.
        /// </summary>
        private static void AbandonAnchor(Scheduled lost, float simNow, string reason)
        {
            bool rehomed = PromoteAnchor(lost, simNow, retime: true);
            if (!rehomed)
            {
                if (lost.BoardTargets != null)
                    for (int j = 0; j < lost.BoardTargets.Count; j++)
                        EngagementBoard.Drop(lost.BoardTargets[j]);
                else if (lost.Item?.Target != null)
                    EngagementBoard.Drop(lost.Item.Target);
            }

            Intent it = lost.Item;
            Bootstrap.Log.LogWarning(
                $"[AutoTOT] anchor retired {it?.AmmoId} from {UnitNaming.SafeName(it?.Unit)} -> " +
                $"{UnitNaming.SafeName(it?.Target)} ({reason}) having launched nothing: " +
                (rehomed
                    ? "strike re-anchored on the longest-enroute survivor and retimed."
                    : "no survivor to re-anchor on, so the strike ends with it.") +
                SubmarineFacts.Describe(it?.Unit, it?.AmmoId));
        }

        /// <summary>
        /// Hand the anchor's prediction to every held order and every strike target's board row, so rows of
        /// other targets do not freeze.
        /// </summary>
        private static void PublishImpact(Scheduled a, float impactSim)
        {
            if (a.Followers != null)
                for (int j = 0; j < a.Followers.Count; j++)
                    a.Followers[j].ImpactAtSim = impactSim;
            PublishBoardImpact(a, impactSim);
        }

        /// <summary>
        /// Write the shared impact to every strike target's board row, or the anchor's target alone. A
        /// destroyed target is skipped, since <see cref="EngagementBoard.UpdateImpact"/> would re-create its
        /// pruned row every tick.
        /// </summary>
        private static void PublishBoardImpact(Scheduled a, float impactSim)
        {
            if (a.BoardTargets != null)
            {
                for (int j = 0; j < a.BoardTargets.Count; j++)
                {
                    ObjectBase t = a.BoardTargets[j];
                    if (t != null && !t.IsDestroyed) EngagementBoard.UpdateImpact(t, impactSim);
                }
            }
            else if (a.Item?.Target != null && !a.Item.Target.IsDestroyed)
            {
                EngagementBoard.UpdateImpact(a.Item.Target, impactSim);
            }
        }

        /// <summary>
        /// Re-elect an anchor when the sitting one leaves: the longest-enroute survivor, as
        /// <see cref="Schedule"/> would have chosen. False when nothing can take over.
        ///
        /// <paramref name="retime"/> false (held): the anchor died before releasing. The impact stands, since
        /// it came from the largest enroute and every survivor can still make it. True (retimed): the anchor
        /// released and launched nothing, so its impact came from a shot that never existed; it is recomputed
        /// from the new anchor's enroute, from now.
        ///
        /// A follower that has already released is never a candidate: its round is away and it has left
        /// <c>_scheduled</c>, so nothing could observe its ripple.
        /// </summary>
        private static bool PromoteAnchor(Scheduled lost, float simNow, bool retime)
        {
            List<Scheduled> followers = lost.Followers;
            if (followers == null || followers.Count == 0) return false;

            Scheduled best = null;
            float bestEnroute = float.NegativeInfinity;
            var survivors = new List<Scheduled>(followers.Count);
            foreach (Scheduled f in followers)
            {
                Intent fi = f.Item;
                if (f.Fired) continue;
                if (fi.Unit == null || fi.Unit.IsDestroyed || fi.Target == null || fi.Target.IsDestroyed)
                    continue;   // its own drop pass will deal with it
                survivors.Add(f);
                // Reuse the estimate the release path holds; a fresh sim per survivor is not worth it to sort a list.
                float flightEst = f.LastFlightEst >= 0f
                    ? f.LastFlightEst
                    : FlightTime.Estimate(fi.Unit, fi.AmmoId, fi.Target);
                // A retimed impact is built from this estimate, so it must be real; a held one only needs the order.
                if (retime && flightEst <= FlightTime.MinValidSeconds) continue;
                float enroute = EnrouteSeconds(fi, flightEst, GroupDelay(fi, fi.ReleaseLead));
                if (enroute > bestEnroute) { bestEnroute = enroute; best = f; }
            }
            if (best == null || (retime && bestEnroute <= 0f)) return false;

            best.IsAnchor = true;
            best.AnchorShots = Mathf.Max(1, best.Item.AnchorShots);
            best.IniInterval = LauncherFactsSource.Get(best.Item.Unit, best.Item.AmmoId).ShotIntervalOrFallback;
            best.Anchor = null;
            best.HasPredict = false;
            best.LoggedAnchorWait = false;
            // Retimed: the longest remaining shot, from now, which every survivor can meet.
            if (retime) best.ImpactAtSim = simNow + bestEnroute;

            var newFollowers = new List<Scheduled>(survivors.Count);
            foreach (Scheduled f in survivors)
            {
                if (f == best) continue;
                f.Anchor = best;
                f.LoggedAnchorWait = false;
                if (retime) f.ImpactAtSim = best.ImpactAtSim;
                newFollowers.Add(f);
            }
            best.Followers = newFollowers;

            // Carry the board rows over, minus any whose target is gone.
            var boards = new List<ObjectBase>();
            if (lost.BoardTargets != null)
                foreach (ObjectBase t in lost.BoardTargets)
                    if (t != null && !t.IsDestroyed) boards.Add(t);
            best.BoardTargets = boards;
            if (retime) PublishBoardImpact(best, best.ImpactAtSim);

            if (retime)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor re-elected after abandonment: {lost.Item.AmmoId} from " +
                    $"{UnitNaming.SafeName(lost.Item.Unit)} launched nothing; " +
                    $"{best.Item.AmmoId} from {UnitNaming.SafeName(best.Item.Unit)} -> " +
                    $"{UnitNaming.SafeName(best.Item.Target)} takes over ({bestEnroute:0.0}s " +
                    $"enroute) for {newFollowers.Count} follower(s), impact RETIMED from sim " +
                    $"{lost.ImpactAtSim:0.0} to sim {best.ImpactAtSim:0.0}.");
            else
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] anchor re-elected before release: {lost.Item.AmmoId} from " +
                    $"{UnitNaming.SafeName(lost.Item.Unit)} is gone; " +
                    $"{best.Item.AmmoId} from {UnitNaming.SafeName(best.Item.Unit)} -> " +
                    $"{UnitNaming.SafeName(best.Item.Target)} takes over " +
                    $"({bestEnroute:0.0}s enroute) for {newFollowers.Count} follower(s), " +
                    $"impact held at sim {best.ImpactAtSim:0.0}.");
            return true;
        }
    }
}
