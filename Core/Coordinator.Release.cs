using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The release half of the pipeline: deciding when each held order is due, resolving the flight
    /// estimate it is due against, and putting the shot away.
    ///
    /// Split out of Coordinator.cs for size only. The anchoring that keeps the shared impact time
    /// honest while these fire is in Coordinator.Anchor.cs.
    /// </summary>
    internal static partial class Coordinator
    {
        private static float _lastReleaseSimNow = -1f;

        /// <summary>
        /// Flight time for one scheduled item, refreshed or reused. Mutates the item's cached
        /// estimate and the per-frame fresh-sim budget, which is why it is not a pure function.
        /// </summary>
        private static float ResolveFlightEstimate(Scheduled s, Intent it, float simNow,
                                                   float timeLeft, float releaseGate)
        {
            // Two invariants bind here.
            //
            // 1. Release must use the SAME estimator the commit path scheduled the impact against
            //    (FlightTime.Estimate, whose real-0.5 s TTL cache both paths share). Any cheaper
            //    substitute biases one path against the other, slack goes negative, and the whole
            //    batch dumps on one tick with no stagger.
            // 2. Staleness is measured in SIM time, not real time, or slow ticks expire the cache
            //    every frame and force a full recompute, which is the feedback loop that made them
            //    slow.
            //
            // Proximity gate: far items reuse a prior Estimate on a long cadence (FlightRefreshSim),
            // near-release items on a short one (FlightRefreshNearSim). The reused value is always a
            // real Estimate output, so it cannot diverge from the commit path.
            // See docs/plans/done/2026-08-30-performance-analysis.md and
            // docs/plans/reference/estimator-cost.md.
            bool nearRelease = s.LastFlightEst >= 0f &&
                               timeLeft <= s.LastFlightEst + releaseGate + FlightGateMargin;
            float refreshCadence = nearRelease ? FlightRefreshNearSim : FlightRefreshSim;
            bool due = s.LastFlightEst < 0f || simNow - s.LastFlightEstSim >= refreshCadence;
            // Per-frame ceiling on fresh sims: a synchronized wave bunches many due refreshes into
            // one frame. An item with NO estimate always computes; correctness before budget.
            //
            // The budget is charged AFTER the call and only when it missed the cache. Charging it
            // before meant a cache hit spent a slot, and hits are the large majority: one measured
            // window served 454 hits against 123 misses, the hits costing 0.1 ms in total. A ceiling
            // of 12 therefore admitted about 2 real sims per frame and turned away roughly 64 items,
            // so the release path ran on estimates 8 to 12 s old with the budget mostly spent on
            // work that was free. Only a miss consumes the resource this exists to bound.
            bool budgeted = s.LastFlightEst < 0f || _flightEstimatesThisFrame < MaxFlightEstimatesPerFrame;
            float flightNow;
            if (due && budgeted)
            {
                // Three ways to answer, cheapest first.
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float cached))
                {
                    // Already computed, possibly by a worker that finished since the last tick.
                    // No Begin/End here: nothing ran, so there is no duration to attribute.
                    CoordinatorProfiler.CountCachedHit();
                    flightNow = cached;
                    s.LastFlightEst = flightNow;
                    s.LastFlightEstSim = simNow;
                }
                else if (s.LastFlightEst >= 0f &&
                         FlightTime.RequestRefresh(it.Unit, it.AmmoId, it.Target))
                {
                    // A refresh of a value we already hold: queue it and keep the previous number
                    // for now. LastFlightEstSim is deliberately NOT advanced, so the item stays due
                    // and adopts the fresh value the moment it lands. RequestRefresh dedupes by key,
                    // so staying due does not re-queue it.
                    flightNow = s.LastFlightEst;
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.FlightQueued);
                }
                else
                {
                    // No usable previous value (the anchor releases almost immediately after being
                    // scheduled, so it must be answered now), or the queue declined the work.
                    // Correctness before budget, as before.
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                    flightNow = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                    bool cacheHit = FlightTime.WasLastCallCacheHit;
                    CoordinatorProfiler.CountEstimate(cacheHit);
                    if (!cacheHit) _flightEstimatesThisFrame++;
                    s.LastFlightEst = flightNow;
                    s.LastFlightEstSim = simNow;
                }
            }
            else
            {
                flightNow = s.LastFlightEst;
                CoordinatorProfiler.Count(due ? CoordinatorProfiler.Counter.FlightBudgetSkipped
                                   : CoordinatorProfiler.Counter.FlightDeferred);
            }
            return flightNow;
        }

        private static void ReleaseDueLaunches(float simNow)
        {
            if (_scheduled.Count == 0) { _lastReleaseSimNow = simNow; return; }

            // Half-a-frame lookahead: releases evaluate "time left <= flight time" with a flight
            // time estimated THIS frame, but the missile actually launches a fraction of a sim
            // step later. The tiny lead absorbs shooter/target motion during the stagger and
            // corrects time-compression's late-bias. simStep is measured in SIM time so pause
            // (simStep=0) adds no lookahead.
            float simStep = (_lastReleaseSimNow >= 0f) ? Mathf.Max(0f, simNow - _lastReleaseSimNow) : 0f;
            float lookahead = LookaheadFraction * simStep;
            _lastReleaseSimNow = simNow;
            _flightEstimatesThisFrame = 0;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled s = _scheduled[i];
                Intent it = s.Item;

                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                {
                    bool shooterGone = it.Unit == null || it.Unit.IsDestroyed;
                    NoteScheduleExit(s, simNow, shooterGone ? "shooter destroyed" : "target destroyed");
                    _scheduled.RemoveAt(i);
                    if (s.IsAnchor && !s.Fired) PromoteNewAnchor(s, simNow);
                    // Never fires, so the target may never get a fired row, which is what the board's
                    // prune keys off. Drop it now or it leaks until the next mission Reset().
                    DropImpactDataIfUnscheduled(it.Target);
                    LogDroppedItem(s);
                    continue;
                }

                float timeLeft = s.ImpactAtSim - simNow;

                // Use cached GroupDelay if available (independent of the flight estimate; the
                // proximity gate below needs it, so resolve it first).
                if (s.GroupDelayCached < 0f)
                {
                    CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.GroupDelay);
                    s.GroupDelayCached = GroupDelay(it, it.ReleaseLead);
                    CoordinatorProfiler.End(CoordinatorProfiler.Stage.GroupDelay);
                }
                float groupDelay = s.GroupDelayCached;
                CoordinatorProfiler.Count(CoordinatorProfiler.Counter.GroupDelayCalls);

                // A follower never fires before its anchor has actually put a round up. The anchor is
                // by definition the longest-enroute shot of the batch, so a follower going first can
                // only ever be too early. Normally a no-op (the anchor launches within seconds of its
                // own release); it earns its keep when the anchor is a submarine whose ascent ran
                // longer than the estimate folded into StartupLead. Bounded by RippleDone: when the
                // anchor completes, or is abandoned at NoLaunchMaxHoldSim, followers free next tick.
                if (!s.IsAnchor && s.Anchor != null && s.Anchor.Fired &&
                    !s.Anchor.RippleDone && s.Anchor.LaunchTimes.Count == 0)
                {
                    // D1: the hold is bounded by RippleDone, which only UpdateAnchorTracking sets,
                    // and that loop walks _scheduled. An anchor that has left it can no longer reach
                    // this follower. Says so once rather than holding in silence.
                    DiagnoseOrphanedFollower(s, simNow);
                    if (VerboseLog && !s.LoggedAnchorWait)
                    {
                        s.LoggedAnchorWait = true;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] holding {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                            $"anchor {s.Anchor.Item?.AmmoId} from {UnitNaming.SafeName(s.Anchor.Item?.Unit)} " +
                            $"has launched nothing yet.");
                    }
                    continue;
                }

                RefreshEnvelopeLead(s, it, simNow);
                DiagnoseUntrackedEnvelope(s, it, simNow);

                float releaseGate = it.ReleaseLead + it.StartupLead + groupDelay + lookahead;
                float flightNow = ResolveFlightEstimate(s, it, simNow, timeLeft, releaseGate);

                // Release early by: the ripple lead (centers the salvo on the coordinated impact),
                // the fixed startup offset (PreLaunchDelay + expected reaction) the engage cycle burns
                // before round 1 leaves the rail, PLUS the group-drag delay (a grouped salvo flies
                // slower than the solo estimate, so it must leave earlier to still arrive on time).
                if (timeLeft <= flightNow + it.ReleaseLead + it.StartupLead + groupDelay + lookahead)
                {
                    if (s.IsAnchor)
                    {
                        // The anchor stays in _scheduled after firing: UpdateAnchorTracking observes
                        // its launch ripple and finalizes the batch impact from it.
                        if (s.Fired) continue;
                        s.Fired = true;
                        s.FiredAtSim = simNow;
                    }
                    else
                    {
                        // Marked before removal purely so the anchor's follower list can still tell
                        // which of its orders are already in the air. A released round has spent its
                        // lead and a later move of the shared impact cannot reach it, which is what
                        // D2 of docs/plans/open/anchor-liveness.md measures. No timing reads this.
                        s.Fired = true;
                        _scheduled.RemoveAt(i);
                    }
                    // Regression guardrail for invariant 1 in ResolveFlightEstimate: large positive
                    // overshoot means the item cannot make its scheduled impact and the batch has
                    // collapsed onto one tick with no stagger. Once per item, independent of
                    // VerboseLog.
                    float overshoot = flightNow - timeLeft;
                    if (overshoot > SlackWarnSeconds)
                        Bootstrap.Log.LogWarning(
                            $"[AutoTOT] {it.AmmoId} from {it.Unit.getUIDAndName()} released {overshoot:0.0}s " +
                            $"past-due (est flight {flightNow:0.0}s > time-to-impact {timeLeft:0.0}s): " +
                            $"launch stagger lost; commit/release flight estimates likely diverged.");
                    if (VerboseLog)
                    {
                        AmmunitionParameters ap = it.Unit.getAmmunitionByName(it.AmmoId)?._ap;
                        // Verbose-only, but it runs the model, so it is timed like any other sim.
                        CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                        float kinematicEst = (ap != null) ? FlightTime.Kinematic(it.Unit, ap, it.Target) : -1f;
                        CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                        CoordinatorProfiler.CountEstimate(FlightTime.WasLastCallCacheHit);
                        string src = (kinematicEst > FlightTime.MinValidSeconds) ? "kinematic" : "straight-line fallback";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] launch {it.AmmoId} from {it.Unit.getUIDAndName()}" +
                            $"{(s.IsAnchor ? " (anchor)" : "")}: " +
                            $"est flight {flightNow:0.0}s ({src}), " +
                            $"releaseLead {it.ReleaseLead:0.0}s, startupLead {it.StartupLead:0.0}s, " +
                            $"groupDelay {groupDelay:0.0}s, " +
                            $"impactAt {s.ImpactAtSim:0.0}, now {simNow:0.0}, " +
                            $"simStep {simStep:0.0}s, overshoot {overshoot:0.0}s" +
                            SubmarineFacts.Describe(it.Unit, it.AmmoId));
                    }
                    // Age of the estimate this release used: what the per-frame sim cap costs in
                    // freshness. 0 = recomputed this frame, -1 = no estimate was ever taken.
                    CoordinatorProfiler.ReleaseStaleness(s.LastFlightEst >= 0f ? simNow - s.LastFlightEstSim : -1f);
                    Fire(it, s.IsAnchor ? s : null);
                }
            }
        }

        /// <summary>
        /// Re-read a held order's launch-envelope transit from the platform's CURRENT state.
        ///
        /// <see cref="PrepareIntent"/> computes <see cref="Intent.EnvelopeLead"/> once, at commit,
        /// and the release can be minutes later. A boat that was still settling at 430 ft when the
        /// strike was planned, and sat at 420 ft by the time its gate opened, was released against a
        /// 120.2 s ascent it no longer had to make: it launched 4.4 s early and its round arrived
        /// 6.0 s ahead of the rest. Scored against the depth it actually left from, the same model
        /// was off by 1.4 s, in line with every other run. The model was right and the input was
        /// stale.
        ///
        /// Refreshing is safe for the release gate in a way it would not be for the flight estimate:
        /// the shared impact time is already fixed, and this only answers "how long does this
        /// platform need FROM NOW", which is exactly the question the gate asks.
        /// </summary>
        private static void RefreshEnvelopeLead(Scheduled s, Intent it, float simNow)
        {
            if (!it.EnvelopeTracked || s.Fired) return;
            if (simNow - s.LastEnvelopeSim < EnvelopeRefreshSim) return;
            s.LastEnvelopeSim = simNow;

            float fresh = LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId);
            float delta = fresh - it.EnvelopeLead;
            if (Mathf.Approximately(delta, 0f)) return;

            it.EnvelopeLead = fresh;
            it.StartupLead = Mathf.Max(0f, it.StartupLead + delta);

            if (VerboseLog && Mathf.Abs(delta) >= EnvelopeDriftLogSeconds)
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] envelope-refresh {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}: " +
                    $"lead now {fresh:0.0}s ({delta:+0.0;-0.0}s vs the value at commit), " +
                    $"startupLead {it.StartupLead:0.0}s.");
        }

        /// <summary>
        /// Fire a hand-picked set of missile shots, staggered so they all arrive together. The shots
        /// need not share a target: one anchor is elected across the whole set and every other shot
        /// follows it, so a strike spanning several targets lands on one time-on-target.
        /// </summary>
        internal static void FireCoordinated(List<Shot> shots)
        {
            if (shots == null || shots.Count == 0) return;

            var items = new List<Intent>(shots.Count);
            foreach (Shot s in shots)
            {
                if (s.Target == null) continue;
                items.Add(new Intent
                {
                    Unit = s.Unit,
                    AmmoId = s.AmmoId,
                    Target = s.Target,
                    Shots = Mathf.Max(1, s.Salvo),
                    Priority = PlannerTaskPriority,
                });
            }
            if (items.Count == 0) return;

            // Before the formation flags and PrepareIntent below, both of which are derived from the
            // shot counts and from which orders survive.
            ClampToGuidanceChannels(items);
            if (items.Count == 0) return;
            DiagnoseAggregateStock(items);

            _targetScratch.Clear();
            foreach (Intent it in items)
            {
                // Per TARGET, not per strike: the game's formation-attack flag means "several
                // shooters are engaging this contact together", so counting the whole strike would
                // mislabel a one-shooter target inside a multi-target strike.
                int atTarget = 0;
                foreach (Intent o in items) if (o.Target == it.Target) atTarget++;
                it.IsFormation = atTarget > 1;
                PrepareIntent(it);
                if (!_targetScratch.Contains(it.Target)) _targetScratch.Add(it.Target);
            }

            Intent anchor = PickAnchor(items, out float maxEnroute);

            Schedule(items, GameClock.SimNow() + maxEnroute, anchor);

            string where = _targetScratch.Count == 1
                ? _targetScratch[0].getUIDAndName()
                : $"{_targetScratch.Count} targets";
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] planner firing {items.Count} order(s) at {where}: " +
                $"longest enroute {maxEnroute:0.0}s, anchor {anchor?.AmmoId} -> " +
                $"{UnitNaming.SafeName(anchor?.Target)}, impacts synced.");
            WarnOnLauncherContention(items);
        }

        /// <summary>
        /// Fire the planner's staged strike. Any orders the player also issued in-game while the
        /// strike was armed are already sitting in the armed batch, so the staged picks are folded
        /// into it and the whole lot commits as ONE strike with one anchor. With nothing armed this
        /// is just a coordinated multi-target fire of the staged picks.
        /// </summary>
        internal static void FireStrike(List<Shot> staged)
        {
            if (_strikeBatch == null) { FireCoordinated(staged); return; }

            int firstAdded = _strikeBatch.Items.Count;
            if (staged != null)
            {
                foreach (Shot s in staged)
                {
                    if (s.Unit == null || s.Target == null) continue;
                    _strikeBatch.Items.Add(new Intent
                    {
                        Unit = s.Unit,
                        AmmoId = s.AmmoId,
                        Target = s.Target,
                        Shots = Mathf.Max(1, s.Salvo),
                        Priority = PlannerTaskPriority,
                    });
                }
                // Formation-attack flag, per target and over the WHOLE batch: an intercepted order
                // and a staged pick at the same contact are one formation attack on it. Intercepted
                // items keep the flag the game itself passed.
                List<Intent> all = _strikeBatch.Items;
                for (int i = firstAdded; i < all.Count; i++)
                {
                    int atTarget = 0;
                    for (int j = 0; j < all.Count; j++)
                        if (all[j].Target == all[i].Target) atTarget++;
                    all[i].IsFormation = atTarget > 1;
                }
            }
            ExecuteStrike();
        }

        // Shooter+ammo pairs already handled by the current clamp pass, so each group is costed once.
        private static readonly HashSet<string> _clampSeen = new HashSet<string>();
        // Intents the clamp reduced to nothing, removed after the walk rather than during it.
        private static readonly List<Intent> _clampDropped = new List<Intent>();

        /// <summary>
        /// Trim every shooter's salvo to the number of rounds its guiding sensor can actually
        /// control, across the WHOLE commit rather than one target at a time.
        ///
        /// <para><b>Why this clamps instead of warning.</b> The surplus rounds are not held back by
        /// the game: they launch, fail to win a channel in Missile.CheckRadioConnection, set
        /// ConnectionLost and are logged "no weapon channel". A radio-command round in that state is
        /// refused its run-in by Missile.RunInPermitted, so it flies on and self-destructs. The
        /// magazine is spent either way, so ordering them can only waste rounds, never deliver
        /// them.</para>
        ///
        /// <para><b>Why here.</b> This is the only point that sees the whole wave, and it covers the
        /// planner path and the intercepted-order path alike. The panel's own per-row cap cannot do
        /// it: it is applied per target, and orders issued through the game's own interface never
        /// reach the panel at all. It must also run BEFORE PrepareIntent, because anchor selection
        /// and the shared impact time are computed from the shot counts.</para>
        ///
        /// <para>Only ungroupable launcher-guided ammo is capped at all (see
        /// LauncherFactsSource.ComputeGuidance); everything that can form a missile group shares one
        /// channel and is bounded by the group instead.</para>
        /// </summary>
        /// <summary>
        /// One dispatched order whose rounds have not all left the rail yet.
        ///
        /// The channel budget is read as "sensor channels minus weapons the sensor is guiding", and
        /// a weapon only joins that list when it LAUNCHES. An order that AutoTOT has handed to the
        /// game therefore occupies nothing measurable until its ripple runs, which on a submarine
        /// can be half a minute later. Between those two moments the rounds were invisible to both
        /// halves of the accounting, and a second order could be accepted against channels the first
        /// one was already going to need. Measured on 2026-09-09: a Prj.675 with a 4-channel
        /// Front_Door took a 4-round order and then a 2-round order 26 s later, and two of the four
        /// rounds that flew ended 2.4 km and 4.8 km short with the target still afloat.
        /// </summary>
        private sealed class Reservation
        {
            public ObjectBase Unit;
            public string AmmoId;      // the order's ammunition id, for the sensor-sharing lookup
            public string AmmoFile;    // AmmunitionParameters._ammunitionFileName, what a LAUNCHED
                                       // round reports; the two are not always the same string
            public int Shots;
            public int Launched;
            public float DispatchedSim;
            public float ExpirySim;    // when the unlaunched remainder stops being reserved
        }

        private static readonly List<Reservation> _reservations = new List<Reservation>();

        /// <summary>How long past its expected ripple a dispatched order keeps reserving the rounds
        /// that have not appeared. Long enough to cover a launcher working through a slow cycle,
        /// bounded so a dead order cannot hold a ship's channels for the rest of the mission.</summary>
        private const float ReservationGraceSim = 120f;

        /// <summary>
        /// Reserve the rounds of a dispatched order until they are seen leaving. Only
        /// channel-limited ammunition is tracked: everything that can form a missile group is
        /// bounded by the group instead, so reserving it would refuse orders for no reason.
        /// </summary>
        private static void ReserveDispatched(Intent it, float simNow)
        {
            if (it?.Unit == null || it.AmmoId == null) return;
            if (LauncherFactsSource.GuidanceChannelCap(it.Unit, it.AmmoId) == int.MaxValue) return;

            int shots = Mathf.Max(1, it.Shots);
            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            float interval = (f.Valid && f.ShotInterval > 0f) ? f.ShotInterval
                                                             : LauncherFactsSource.FallbackShotInterval;
            float ripple = (shots - 1) * interval + Mathf.Max(0, it.Waves - 1) * (f.Valid ? f.ReloadGap : 0f);

            string ammoFile = it.Unit.getAmmunitionByName(it.AmmoId)?._ap?._ammunitionFileName;

            _reservations.Add(new Reservation
            {
                Unit = it.Unit,
                AmmoId = it.AmmoId,
                AmmoFile = string.IsNullOrEmpty(ammoFile) ? it.AmmoId : ammoFile,
                Shots = shots,
                Launched = 0,
                DispatchedSim = simNow,
                ExpirySim = simNow + it.StartupLead + ripple + ReservationGraceSim,
            });
        }

        /// <summary>
        /// Credit one observed launch against the oldest matching reservation. Called from the launch
        /// tracker's first-sighting path, which sees every round the game puts up, so this does not
        /// depend on the diagnostic expectation list and its verbose-only membership rule.
        /// </summary>
        internal static void CreditReservedLaunch(ObjectBase unit, string ammoFile)
        {
            if (unit == null || ammoFile == null) return;
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit || r.Launched >= r.Shots) continue;
                if (!string.Equals(r.AmmoFile, ammoFile, StringComparison.Ordinal) &&
                    !string.Equals(r.AmmoId, ammoFile, StringComparison.Ordinal)) continue;
                r.Launched++;
                if (r.Launched >= r.Shots) _reservations.RemoveAt(i);
                return;
            }
        }

        /// <summary>
        /// Drop reservations whose rounds have arrived, whose shooter is gone, or which have waited
        /// past their grace. Called once per tick.
        ///
        /// An expiry is worth a line: it means rounds were ordered, the channels were held for them,
        /// and they never flew. That is the shortfall case seen from the guidance side.
        /// </summary>
        private static void PruneReservations(float simNow)
        {
            for (int i = _reservations.Count - 1; i >= 0; i--)
            {
                Reservation r = _reservations[i];
                if (r.Unit == null || r.Unit.IsDestroyed) { _reservations.RemoveAt(i); continue; }
                if (simNow < r.ExpirySim) continue;

                _reservations.RemoveAt(i);
                if (VerboseLog && r.Launched < r.Shots)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] reservation-expired {r.AmmoId} from {UnitNaming.SafeName(r.Unit)}: " +
                        $"{r.Shots - r.Launched} of {r.Shots} round(s) never left in " +
                        $"{simNow - r.DispatchedSim:0.0}s; their guidance channels are free again.");
            }
        }

        /// <summary>
        /// Rounds of exactly this ammunition still owed by dispatched orders. The magazine question,
        /// as opposed to the channel question below.
        /// </summary>
        private static int ReservedRounds(ObjectBase unit, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit) continue;
                if (!string.Equals(r.AmmoId, ammoId, StringComparison.Ordinal)) continue;
                n += Mathf.Max(0, r.Shots - r.Launched);
            }
            return n;
        }

        /// <summary>
        /// Rounds this ship has spoken for that are not yet occupying a channel: orders committed and
        /// not released, plus orders dispatched whose rounds have not all launched.
        ///
        /// Counted across every ammunition that SHARES a guiding sensor with
        /// <paramref name="ammoId"/>, because the budget belongs to the sensor rather than to the
        /// ammunition. Rounds already in the air are deliberately absent: they appear in the sensor's
        /// own weapon list, which the cap subtracts, and counting them here would charge them twice.
        /// </summary>
        private static int CommittedUnfired(ObjectBase unit, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _scheduled.Count; i++)
            {
                Scheduled s = _scheduled[i];
                if (s == null || s.Fired || s.Item == null) continue;
                if (s.Item.Unit == unit &&
                    LauncherFactsSource.SharesGuidanceSensor(unit, s.Item.AmmoId, ammoId))
                    n += Mathf.Max(1, s.Item.Shots);
            }
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit) continue;
                if (!LauncherFactsSource.SharesGuidanceSensor(unit, r.AmmoId, ammoId)) continue;
                n += Mathf.Max(0, r.Shots - r.Launched);
            }
            return n;
        }

        /// <summary>
        /// Rounds of <paramref name="ammoId"/> this ship could still guide if an order arrived now:
        /// its channel budget less what is already committed and unfired, less what
        /// <paramref name="batch"/> is already holding for it. int.MaxValue when the ammunition is
        /// not channel-limited, which is everything that can form a missile group.
        /// </summary>
        private static int BatchRounds(Batch b, ObjectBase unit, string ammoId)
        {
            int n = 0;
            if (b == null) return 0;
            for (int i = 0; i < b.Items.Count; i++)
            {
                Intent it = b.Items[i];
                if (it != null && it.Unit == unit &&
                    LauncherFactsSource.SharesGuidanceSensor(unit, it.AmmoId, ammoId))
                    n += Mathf.Max(1, it.Shots);
            }
            return n;
        }

        /// <summary>
        /// Every round of <paramref name="ammoId"/> this ship is already spoken for by the
        /// coordinator: committed and unfired, plus anything in the armed strike batch or in an open
        /// per-target batch. A batch is either the strike batch or in _openBatches, never both, so
        /// these three pools do not overlap.
        /// </summary>
        internal static int HeldRounds(ObjectBase unit, string ammoId)
        {
            int n = CommittedUnfired(unit, ammoId);
            n += BatchRounds(_strikeBatch, unit, ammoId);
            foreach (KeyValuePair<ObjectBase, Batch> kv in _openBatches)
                n += BatchRounds(kv.Value, unit, ammoId);
            return n;
        }

        /// <summary>
        /// Rounds the PLANNER PANEL has staged but not yet handed over, supplied by the Hud because
        /// the coordinator cannot see them: a staged pick lives in the panel until FireStrike.
        ///
        /// <para>Without this the two halves of the budget were blind to each other. Orders taken in
        /// through Alt+H did not stop the panel offering more, and picks staged in the panel did not
        /// stop Alt+H accepting more, so either order of operations could exceed the channels the
        /// other half was carefully protecting.</para>
        /// </summary>
        internal static System.Func<ObjectBase, string, int> StagedRoundsProvider;

        private static int StagedRounds(ObjectBase unit, string ammoId)
        {
            System.Func<ObjectBase, string, int> f = StagedRoundsProvider;
            if (f == null) return 0;
            try { return f(unit, ammoId); }
            catch { return 0; }   // a UI fault must never block firing
        }

        /// <summary>
        /// Rounds of <paramref name="ammoId"/> this ship could still guide if an order arrived now.
        /// int.MaxValue when the ammunition is not channel-limited, which is everything that can
        /// form a missile group.
        /// </summary>
        private static int GuidanceRoom(ObjectBase unit, string ammoId)
        {
            int cap = LauncherFactsSource.GuidanceChannelCap(unit, ammoId);
            if (cap == int.MaxValue) return int.MaxValue;
            return Mathf.Max(0, cap - HeldRounds(unit, ammoId) - StagedRounds(unit, ammoId));
        }

        /// <summary>
        /// Flag the shooters an armed batch has already pushed past their guidance-channel budget,
        /// WITHOUT trimming anything. Called as each order is collected, so the indicator and the
        /// panel can say a trim is coming while the strike is still being built and the player can
        /// still do something about it.
        ///
        /// <para>The clamp itself cannot serve this purpose: it runs at commit, which is the same
        /// instant the rounds leave. A player with the panel collapsed then learns what happened
        /// only from a log line, after the fact.</para>
        /// </summary>
        internal static void MarkChannelPressure(List<Intent> items)
        {
            _channelCappedShooters.Clear();
            if (items == null || items.Count == 0) return;
            _clampSeen.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                Intent lead = items[i];
                if (lead?.Unit == null || lead.AmmoId == null) continue;
                string key = lead.Unit.GetInstanceID() + "/" + lead.AmmoId;
                if (!_clampSeen.Add(key)) continue;

                int cap = LauncherFactsSource.GuidanceChannelCap(lead.Unit, lead.AmmoId);
                if (cap == int.MaxValue) continue;

                // HeldRounds already covers `items`, since the caller's list is one of the batches
                // it walks. Adding the list again here would double-count it.
                int requested = HeldRounds(lead.Unit, lead.AmmoId) + StagedRounds(lead.Unit, lead.AmmoId);
                // >= not >: the point of the light is "this ship's channels are spoken for", and a
                // ship holding exactly its cap is the case the player most needs to see, since the
                // next order they try will be refused outright. Requiring a strict overrun made it
                // unreachable on the intercept path, which now refuses the overrun before it can be
                // counted at all.
                if (requested >= cap) _channelCappedShooters.Add(key);
            }
        }

        /// <summary>
        /// True when an earlier item in this commit shares <paramref name="lead"/>'s guiding sensor
        /// on the same ship, so its pass already covered these rounds.
        /// </summary>
        private static bool PoolAlreadyClamped(List<Intent> items, int index, Intent lead)
        {
            for (int j = 0; j < index; j++)
            {
                Intent prior = items[j];
                if (prior?.Unit != lead.Unit || prior.AmmoId == null) continue;
                if (string.Equals(prior.AmmoId, lead.AmmoId, StringComparison.Ordinal)) continue;
                if (LauncherFactsSource.SharesGuidanceSensor(lead.Unit, prior.AmmoId, lead.AmmoId))
                    return true;
            }
            return false;
        }

        private static void ClampToGuidanceChannels(List<Intent> items)
        {
            _channelCappedShooters.Clear();
            _channelTrimmedShooters.Clear();
            if (items == null || items.Count == 0) return;

            _clampSeen.Clear();
            _clampDropped.Clear();

            // Indexed, not foreach: the group walk below reads the same list.
            for (int i = 0; i < items.Count; i++)
            {
                Intent lead = items[i];
                if (lead?.Unit == null || lead.AmmoId == null) continue;
                string key = lead.Unit.GetInstanceID() + "/" + lead.AmmoId;
                if (!_clampSeen.Add(key)) continue;
                // One pass per CHANNEL POOL, not per ammunition. Two radio-command types served by
                // one radar draw on one set of channels, and budgeting them separately handed the
                // same pool out twice. An earlier item in this same commit that shares the pool has
                // already trimmed everything in it, this one included.
                if (PoolAlreadyClamped(items, i, lead)) continue;

                int cap = LauncherFactsSource.GuidanceChannelCap(lead.Unit, lead.AmmoId);
                if (cap == int.MaxValue) { _channelCapWarned.Remove(key); continue; }

                int committed = CommittedUnfired(lead.Unit, lead.AmmoId);

                int budget = Mathf.Max(0, cap - committed);
                int requested = 0;
                for (int j = 0; j < items.Count; j++)
                {
                    Intent it = items[j];
                    if (it != null && it.Unit == lead.Unit &&
                        LauncherFactsSource.SharesGuidanceSensor(lead.Unit, it.AmmoId, lead.AmmoId))
                        requested += Mathf.Max(1, it.Shots);
                }
                // D3, commit side. The intake line only covers orders issued through the game's own
                // interface; everything staged in the planner arrives here instead, which is the
                // path the 2026-09-09 over-commit came in on.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] channels-commit {lead.AmmoId} from {lead.Unit.getUIDAndName()}: " +
                        $"cap {cap}, committed-unlaunched {committed}, budget {budget}, requested " +
                        $"{requested} | live {LauncherFactsSource.GuidanceOccupancyText(lead.Unit, lead.AmmoId)}");

                if (requested <= budget) { _channelCapWarned.Remove(key); continue; }

                // Trim in list order, so the targets staged first keep their full salvo and the ones
                // added last absorb the cut. Deterministic, and it matches the order the player
                // built the strike in.
                int left = budget;
                for (int j = 0; j < items.Count; j++)
                {
                    Intent it = items[j];
                    if (it == null || it.Unit != lead.Unit ||
                        !LauncherFactsSource.SharesGuidanceSensor(lead.Unit, it.AmmoId, lead.AmmoId))
                        continue;
                    int give = Mathf.Min(Mathf.Max(1, it.Shots), left);
                    it.Shots = give;
                    left -= give;
                    if (give <= 0) _clampDropped.Add(it);
                }

                _channelCappedShooters.Add(key);
                _channelTrimmedShooters.Add(key);
                if (_channelCapWarned.Add(key))
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] guidance channels: {lead.Unit.getUIDAndName()} can control " +
                        $"{cap} {lead.AmmoId} round(s) at once" +
                        (committed > 0 ? $" and has {committed} already committed" : "") +
                        $", but {requested} were ordered. Trimmed to {budget}; the targets added " +
                        $"last lose their rounds. Ordering more would not deliver them: the extra " +
                        $"rounds launch, lose guidance for want of a channel and self-destruct. " +
                        $"Spread the shots across more shooters.");
            }

            for (int i = 0; i < _clampDropped.Count; i++) items.Remove(_clampDropped[i]);
            _clampDropped.Clear();
        }

        /// <summary>
        /// Warn when a shooter is being committed to more targets than its launcher can service at
        /// once. The game queues every engage task (ObjectBase.InsertEngageTask appends and re-sorts;
        /// nothing is replaced), but HandleEngageTasks will only execute a task whose weapon system
        /// is actually free, so a launcher already engaging one target cannot start another. On a
        /// non-per-container launcher, a box mount, those orders therefore leave SERIALLY and their
        /// impacts cannot be synchronised no matter what this mod schedules.
        ///
        /// Diagnostic only. The shot the player asked for is still fired.
        /// </summary>
        private static void WarnOnLauncherContention(List<Intent> items)
        {
            _contendedShooters.Clear();
            foreach (Intent it in items)
            {
                if (it.Unit == null) continue;

                // Orders already open on this shooter for this ammo, excluding the ones just added.
                int open = 0;
                for (int i = 0; i < _scheduled.Count; i++)
                {
                    Intent other = _scheduled[i].Item;
                    if (other == null || other == it) continue;
                    if (other.Unit == it.Unit && other.AmmoId == it.AmmoId && other.Target != it.Target)
                        open++;
                }
                string key = it.Unit.GetInstanceID() + "/" + it.AmmoId;
                if (open == 0) { _contentionWarned.Remove(key); continue; }

                LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
                bool parallel = f.Valid && f.PerContainer;
                if (parallel) continue;   // VLS cells cycle independently; no serialisation to warn about

                // The HUD marks these rows. Recorded before the once-per-shooter log gate below, so
                // the panel keeps showing the condition after the log has said its piece.
                _contendedShooters.Add(key);

                // Once per shooter and ammo while the contention lasts; re-warning as the player adds
                // targets says nothing new.
                if (_contentionWarned.Contains(key)) continue;
                _contentionWarned.Add(key);

                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] launcher contention: {it.Unit.getUIDAndName()} now has {open + 1} open " +
                    $"{it.AmmoId} order(s) at different targets on a non-parallel launcher. The game " +
                    $"services these one at a time, so rounds will leave serially and time-on-target " +
                    $"across these targets will not hold. Spread the shots across more shooters.");
            }
        }

        /// <summary>Fire one shot immediately, uncoordinated (used by the planner's "Fire now").</summary>
        internal static void FireNow(ObjectBase unit, string ammoId, ObjectBase target, int salvo)
        {
            if (unit == null || unit.IsDestroyed || target == null || target.IsDestroyed) return;
            InsertEngageTask_Patch.Bypass = true;
            try
            {
                unit.InsertEngageTask(ammoId, target, Vector3.zero, Mathf.Max(1, salvo), PlannerTaskPriority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: false);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] fire-now failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }
        }

        private static void Fire(Intent it, Scheduled sched)
        {
            ObjectBase unit = it.Unit;
            ObjectBase target = it.Target;

            if (unit == null || unit.IsDestroyed) return;
            if (target == null || target.IsDestroyed) return;

            InsertEngageTask_Patch.Bypass = true;
            bool inserted = false;
            try
            {
                unit.InsertEngageTask(it.AmmoId, target, Vector3.zero, it.Shots, it.Priority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: it.IsFormation);
                inserted = true;
            }
            catch (System.Exception e)
            {
                // D4 of the beta-release audit. The order is still marked fired below, and a
                // follower has already been removed from _scheduled by its caller, so the mod
                // believes rounds are coming that the game never accepted. The behaviour is
                // deliberately unchanged for now: the diagnostic states the consequence, and the
                // fix waits until a run shows this can happen at all. Zero instances in the 17
                // logs held in Users's Logs as of 2026-09-09.
                Bootstrap.Log.LogError($"[AutoTOT] launch failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }

            if (!inserted)
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] dispatch-failed {it.AmmoId} from {unit.getUIDAndName()} -> " +
                    $"{target.getUIDAndName()}: InsertEngageTask threw, but the order is being marked " +
                    $"fired and {Mathf.Max(1, it.Shots)} round(s) expected anyway " +
                    $"({(sched != null && sched.IsAnchor ? "ANCHOR, its followers are timed off launches that may never come" : "follower, already removed from the schedule")}). " +
                    $"D4 of the beta-release audit.");

            EngagementBoard.MarkFired(target);
            LaunchDiagnostics.RegisterExpectation(it, sched);
            // Hold this order's channels until its rounds actually appear. Only on a dispatch the
            // game accepted: an order it refused occupies nothing.
            if (inserted) ReserveDispatched(it, GameClock.SimNow());

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] launched {unit.getUIDAndName()} -> {target.getUIDAndName()}");
        }
    }
}
