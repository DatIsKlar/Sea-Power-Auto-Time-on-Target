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
                            $"past-due (est flight {flightNow:0.0}s > time-to-impact {timeLeft:0.0}s) ; " +
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
            _targetScratch.Clear();
            foreach (Shot s in shots)
            {
                if (s.Target == null) continue;
                // Per TARGET, not per strike: the game's formation-attack flag means "several
                // shooters are engaging this contact together", so counting the whole strike would
                // mislabel a one-shooter target inside a multi-target strike.
                int atTarget = 0;
                foreach (Shot o in shots) if (o.Target == s.Target) atTarget++;
                var it = new Intent
                {
                    Unit = s.Unit,
                    AmmoId = s.AmmoId,
                    Target = s.Target,
                    Shots = Mathf.Max(1, s.Salvo),
                    Priority = PlannerTaskPriority,
                    IsFormation = atTarget > 1,
                };
                PrepareIntent(it);
                items.Add(it);
                if (!_targetScratch.Contains(s.Target)) _targetScratch.Add(s.Target);
            }
            if (items.Count == 0) return;

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
            try
            {
                unit.InsertEngageTask(it.AmmoId, target, Vector3.zero, it.Shots, it.Priority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: it.IsFormation);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] launch failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }

            EngagementBoard.MarkFired(target);
            LaunchDiagnostics.RegisterExpectation(it, sched);

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] launched {unit.getUIDAndName()} -> {target.getUIDAndName()}");
        }
    }
}
