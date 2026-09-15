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
    /// honest while these fire is in Coordinator.Anchor.cs, and the budget of rounds a commit may
    /// order (guidance channels, magazine, reservations) is in Coordinator.Channels.cs.
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
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float cached,
                                         out float cacheAgeReal))
                {
                    // Already computed, possibly by a worker that finished since the last tick.
                    // No Begin/End here: nothing ran, so there is no duration to attribute.
                    CoordinatorProfiler.CountCachedHit();
                    flightNow = cached;
                    s.LastFlightEst = flightNow;
                    // Back-date the staleness stamp by the entry's own age. The cache is SHARED and
                    // its TTL is 0.5 s of REAL time, so a hit can be a value another caller stored
                    // half a second ago; stamping it `simNow` recorded geometry of arbitrary age as
                    // computed this instant, and at the game's 20x compression clamp that is up to
                    // 10 sim seconds of movement, with the 2 s far-item cadence on top. The item is
                    // then due again sooner, which is the whole effect: one extra refresh rather
                    // than a release decided on a stale number.
                    //
                    // Real age is converted with the current compression rather than tracked in sim
                    // time. Over a 0.5 s window the two differ only if the player changed
                    // compression inside it, and the cost of being wrong there is one refresh.
                    s.LastFlightEstSim = simNow - cacheAgeReal * Mathf.Max(GameTime.TimeCompression, 1f);
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
                    string reason = shooterGone ? "shooter destroyed" : "target destroyed";
                    _scheduled.RemoveAt(i);
                    // Every removal of an anchor goes through one exit. Before, only an anchor that
                    // had NOT released was handed over, and one that had released without launching
                    // took its followers' release condition with it. See RetireAnchor.
                    if (s.IsAnchor) RetireAnchor(s, simNow, reason);
                    NoteScheduleExit(s, simNow, reason);   // after the retire, see its summary
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
                //
                // A pooled follower on the anchor's own launch clock holds even before the anchor has
                // fired. The pooled prediction measures the pool's centre from the anchor's first
                // round, so that round has to be the clock's first, and a follower whose flight is
                // within a release tick of the anchor's could otherwise take the slot. The follower's
                // own slot comes after that round either way. See PoolSharedLaunchClock.
                bool onAnchorClock = it.Pooled && s.Anchor?.Item != null && s.Anchor.Item.Pooled &&
                                     it.PoolClock == s.Anchor.Item.PoolClock && it.Unit == s.Anchor.Item.Unit;
                // An order marked to fire when ready (MarkUnholdableOrders) arrives when its geometry
                // says whatever it waits for, so holding it for the anchor's first round only costs
                // the shot. Behind a submarine anchor that wait is the boat's whole ascent.
                if (!s.IsAnchor && !it.FireImmediately && s.Anchor != null && (s.Anchor.Fired || onAnchorClock) &&
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

                float releaseGate = it.ReleaseLead + it.StartupLead + groupDelay + lookahead;
                float flightNow = ResolveFlightEstimate(s, it, simNow, timeLeft, releaseGate);

                // Release early by: the ripple lead (centers the salvo on the coordinated impact),
                // the fixed startup offset (PreLaunchDelay + expected reaction) the engage cycle burns
                // before round 1 leaves the rail, PLUS the group-drag delay (a grouped salvo flies
                // slower than the solo estimate, so it must leave earlier to still arrive on time).
                // FireImmediately: this order's arrival does not follow its release, so waiting
                // gains nothing and costs the shot. See MarkUnholdableOrders in Coordinator.cs.
                if (it.FireImmediately || timeLeft <= EnrouteSeconds(it, flightNow, groupDelay) + lookahead)
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
                        // the impact-commitment line measures. No timing reads this.
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
                            $"[AutoTOT] {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} released {overshoot:0.0}s " +
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
                            $"[AutoTOT] launch {it.AmmoId} from {UnitNaming.SafeName(it.Unit)}" +
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
                    if (!Fire(it, s.IsAnchor ? s : null, s.ImpactAtSim))
                    {
                        // The game refused it. An anchor that cannot fire must hand the strike over
                        // rather than leave its followers waiting for a ripple that will never
                        // start; it is already out of _scheduled below, or still in it as an anchor,
                        // so retire it through the one exit. A follower has spent its release window
                        // and is simply gone, which the warning above states.
                        if (s.IsAnchor)
                        {
                            int at = _scheduled.IndexOf(s);
                            if (at >= 0) _scheduled.RemoveAt(at);
                            RetireAnchor(s, simNow, "the game refused the order");
                            NoteScheduleExit(s, simNow, "the game refused the order");
                        }
                    }
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
            if (!TryPrepareCommit(items)) return;

            foreach (Intent it in items)
            {
                // Per TARGET, not per strike: the game's formation-attack flag means "several
                // shooters are engaging this contact together", so counting the whole strike would
                // mislabel a one-shooter target inside a multi-target strike.
                int atTarget = 0;
                foreach (Intent o in items) if (o.Target == it.Target) atTarget++;
                it.IsFormation = atTarget > 1;
                PrepareIntent(it);
            }

            // The flight estimates, the anchor pick and the scheduling all happen once the solve
            // workers have answered. See Coordinator.Pending.cs.
            BeginCommit(items, planner: true, isStrike: false);
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
                Bootstrap.Log.LogError($"[AutoTOT] fire-now failed for {UnitNaming.SafeName(unit)}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }
        }

        /// <summary>
        /// Hand one order to the game. Returns false when the game did not take it, which the caller
        /// must act on. This used to catch
        /// the exception, log it, and then mark the engagement fired and register a launch
        /// expectation anyway, so the mod believed rounds were coming that nothing had accepted, and
        /// a follower had already been dropped from the schedule by its caller.
        ///
        /// Not retried here. An exception can be thrown after the game accepted the task, and a
        /// retry would then order the salvo twice; the honest move is to report the order as failed
        /// and let the player reissue it.
        /// </summary>
        private static bool Fire(Intent it, Scheduled sched, float impactAtRelease)
        {
            ObjectBase unit = it.Unit;
            ObjectBase target = it.Target;

            if (unit == null || unit.IsDestroyed) return false;
            if (target == null || target.IsDestroyed) return false;

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
                Bootstrap.Log.LogError($"[AutoTOT] launch failed for {UnitNaming.SafeName(unit)}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }

            if (!inserted)
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] dispatch-failed {it.AmmoId} from {UnitNaming.SafeName(unit)} -> " +
                    $"{UnitNaming.SafeName(target)}: the game refused the order, so its " +
                    $"{Mathf.Max(1, it.Shots)} round(s) are NOT coming and nothing has been " +
                    $"reserved or expected for them. Reissue the order if you still want it.");
                return false;
            }

            EngagementBoard.MarkFired(target);
            LaunchDiagnostics.RegisterExpectation(it, sched, impactAtRelease);
            // Hold this order's channels until its rounds actually appear.
            ReserveDispatched(it, GameClock.SimNow());

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] launched {UnitNaming.SafeName(unit)} -> {UnitNaming.SafeName(target)}");
            return true;
        }
    }
}
