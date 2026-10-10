using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The release half of the pipeline: when each held order is due, the flight estimate it is due
    /// against, and firing it. Anchoring is in Coordinator.Anchor.cs, the round budget in Coordinator.Channels.cs.
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
            // Two invariants:
            // 1. Release uses the estimator the commit scheduled against (FlightTime.Estimate, shared cache). A
            //    cheaper substitute biases one path against the other and the batch fires unstaggered.
            // 2. Staleness is measured in sim time; real time would expire the cache on every slow frame.
            // Far items reuse their estimate every FlightRefreshSim, near-release items every FlightRefreshNearSim.
            bool nearRelease = s.LastFlightEst >= 0f &&
                               timeLeft <= s.LastFlightEst + releaseGate + FlightGateMargin;
            float refreshCadence = nearRelease ? FlightRefreshNearSim : FlightRefreshSim;
            bool due = s.LastFlightEst < 0f || simNow - s.LastFlightEstSim >= refreshCadence;
            // Per-frame ceiling on fresh sims; an item with no estimate always computes. Charged after the
            // call and only on a cache miss, since hits cost nothing.
            bool budgeted = s.LastFlightEst < 0f || _flightEstimatesThisFrame < MaxFlightEstimatesPerFrame;
            float flightNow;
            if (due && budgeted)
            {
                // Three ways to answer, cheapest first.
                if (FlightTime.TryCached(it.Unit, it.AmmoId, it.Target, out float cached,
                                         out float cacheAgeReal))
                {
                    // Already computed, possibly by a worker since the last tick; nothing ran, so nothing is timed.
                    CoordinatorProfiler.CountCachedHit();
                    flightNow = cached;
                    s.LastFlightEst = flightNow;
                    // The cache is shared with a 0.5 s real-time TTL: back-date the stamp by the entry's age
                    // (at current compression) so a stale hit is refreshed sooner.
                    s.LastFlightEstSim = simNow - cacheAgeReal * Mathf.Max(GameTime.TimeCompression, 1f);
                }
                else if (s.LastFlightEst >= 0f &&
                         FlightTime.RequestRefresh(it.Unit, it.AmmoId, it.Target))
                {
                    // Refreshing a value we hold: queue it and keep the old number. LastFlightEstSim is not
                    // advanced, so the item stays due and adopts the fresh value when it lands;
                    // RequestRefresh dedupes by key.
                    flightNow = s.LastFlightEst;
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.FlightQueued);
                }
                else
                {
                    // No previous value (an anchor releases almost at once), or the queue declined: solve now.
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

        // Release faults already logged this mission, keyed on type and message.
        private static readonly HashSet<string> _releaseFaultsLogged = new HashSet<string>();

        /// <summary>Most frames of sim time one tick's release lookahead may cover; see ReleaseDueLaunches.</summary>
        private const float LookaheadMaxFrames = 2f;

        private static void ReleaseDueLaunches(float simNow)
        {
            if (_scheduled.Count == 0) { _lastReleaseSimNow = simNow; return; }

            // Half-a-step lookahead: the missile launches a fraction of a sim step after this decision.
            // Measured in sim time, so a paused game adds none. Capped at a couple of frames' worth: after a gap
            // with no tick (a menu that drops the view model while the sim runs on), the whole gap would become
            // lookahead and release every due follower early by half of it.
            float simStep = (_lastReleaseSimNow >= 0f) ? Mathf.Max(0f, simNow - _lastReleaseSimNow) : 0f;
            float frameSim = Time.unscaledDeltaTime * Mathf.Max(GameTime.TimeCompression, 1f);
            if (frameSim > 0f) simStep = Mathf.Min(simStep, LookaheadMaxFrames * frameSim);
            float lookahead = LookaheadFraction * simStep;
            _lastReleaseSimNow = simNow;
            _flightEstimatesThisFrame = 0;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled s = _scheduled[i];
                Intent it = s.Item;
                // Each order on its own guard: one that throws on every tick must not hold every order after it.
                try
                {
                    if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                    {
                        bool shooterGone = it.Unit == null || it.Unit.IsDestroyed;
                        string reason = shooterGone ? "shooter destroyed" : "target destroyed";
                        _scheduled.RemoveAt(i);
                        // Every anchor removal goes through RetireAnchor, released or not.
                        if (s.IsAnchor) RetireAnchor(s, simNow, reason);
                        NoteScheduleExit(s, simNow, reason);   // after the retire, see its summary
                        // It never fires, so no fired row will prune its target from the board; drop it now.
                        DropImpactDataIfUnscheduled(it.Target);
                        LogDroppedItem(s);
                        continue;
                    }

                    // A fired anchor stays only so UpdateAnchorTracking can watch its ripple; refreshing its flight
                    // estimate here would only spend the per-frame solve budget the held orders need.
                    if (s.IsAnchor && s.Fired) continue;

                    float timeLeft = s.ImpactAtSim - simNow;

                    // GroupDelay does not depend on the flight estimate, and the proximity gate below needs it.
                    if (s.GroupDelayCached < 0f)
                    {
                        CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.GroupDelay);
                        s.GroupDelayCached = GroupDelay(it, it.ReleaseLead);
                        CoordinatorProfiler.End(CoordinatorProfiler.Stage.GroupDelay);
                    }
                    float groupDelay = s.GroupDelayCached;
                    CoordinatorProfiler.Count(CoordinatorProfiler.Counter.GroupDelayCalls);

                    // A follower never fires before its anchor has put a round up: the anchor is the longest
                    // enroute, so going first can only be early. Bounded by RippleDone (completion or
                    // NoLaunchMaxHoldSim). A pooled follower on the anchor's own launch clock holds even before
                    // the anchor fires, so the anchor's round is the clock's first; see PoolSharedLaunchClock.
                    bool onAnchorClock = it.Pooled && s.Anchor?.Item != null && s.Anchor.Item.Pooled &&
                                         it.PoolClock == s.Anchor.Item.PoolClock && it.Unit == s.Anchor.Item.Unit;
                    // An order that fires when ready (MarkUnholdableOrders) gains nothing by waiting for the anchor.
                    if (!s.IsAnchor && !it.FireImmediately && s.Anchor != null && (s.Anchor.Fired || onAnchorClock) &&
                        !s.Anchor.RippleDone && s.Anchor.LaunchTimes.Count == 0)
                    {
                        // Reported once if the anchor has left _scheduled, since nothing can then release this follower.
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

                    // Release when time left <= flight + ripple lead + startup lead + group-drag delay
                    // (EnrouteSeconds), or at once for an order whose arrival does not follow its release.
                    if (it.FireImmediately || timeLeft <= EnrouteSeconds(it, flightNow, groupDelay) + lookahead)
                    {
                        if (s.IsAnchor)
                        {
                            // The anchor stays in _scheduled after firing, so UpdateAnchorTracking can observe its ripple.
                            if (s.Fired) continue;
                            s.Fired = true;
                            s.FiredAtSim = simNow;
                        }
                        else
                        {
                            // Marked before removal so the anchor's follower list knows which orders are away. No
                            // timing reads it.
                            s.Fired = true;
                            _scheduled.RemoveAt(i);
                        }
                        // Guardrail for invariant 1 in ResolveFlightEstimate: a large overshoot means the batch
                        // collapsed onto one tick. Against the whole enroute time the gate used, leads included, so
                        // an order with a long startup lead (a submarine's ascent) is not under-reported. Logged
                        // once per item, ungated.
                        float enroute = EnrouteSeconds(it, flightNow, groupDelay);
                        float overshoot = enroute - timeLeft;
                        if (overshoot > SlackWarnSeconds && !it.FireImmediately)
                            Bootstrap.Log.LogWarning(
                                $"[AutoTOT] {it.AmmoId} from {UnitNaming.SafeName(it.Unit)} released {overshoot:0.0}s " +
                                $"past-due (enroute {enroute:0.0}s, est flight {flightNow:0.0}s, > time-to-impact " +
                                $"{timeLeft:0.0}s): launch stagger lost; commit/release flight estimates likely diverged.");
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
                        // Age of the estimate this release used: 0 = this frame, -1 = never estimated.
                        CoordinatorProfiler.ReleaseStaleness(s.LastFlightEst >= 0f ? simNow - s.LastFlightEstSim : -1f);
                        if (!Fire(it, s.IsAnchor ? s : null, s.ImpactAtSim))
                        {
                            // The game refused it. An anchor retires through the one exit so its followers are
                            // handed over; a follower is simply gone, as the warning says.
                            if (s.IsAnchor)
                            {
                                int at = _scheduled.IndexOf(s);
                                if (at >= 0) _scheduled.RemoveAt(at);
                                RetireAnchor(s, simNow, "the game refused the order");
                                NoteScheduleExit(s, simNow, "the game refused the order");
                            }
                            else
                            {
                                // It never fired, so no fired row will prune its target's board row either.
                                DropImpactDataIfUnscheduled(it.Target);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    if (_releaseFaultsLogged.Count < MaxStageFaultMessages &&
                        _releaseFaultsLogged.Add(e.GetType().Name + "|" + e.Message))
                        Bootstrap.Log.LogError(
                            $"[AutoTOT] release of {it?.AmmoId} from {UnitNaming.SafeName(it?.Unit)} faulted; " +
                            $"the other held orders still release. Reported once per message:\n{e}");
                }
            }
        }

        /// <summary>
        /// Re-read a held order's launch-envelope transit from the platform's current state: the commit-time
        /// value can be minutes old, and a boat that has already risen would release early. Safe for the
        /// gate, because the shared impact is fixed and the gate only asks how long the platform needs from now.
        /// </summary>
        private static void RefreshEnvelopeLead(Scheduled s, Intent it, float simNow)
        {
            if (!it.EnvelopeTracked || s.Fired) return;
            if (simNow - s.LastEnvelopeSim < EnvelopeRefreshSim) return;
            s.LastEnvelopeSim = simNow;

            float fresh = FiniteOrZero(LaunchEnvelope.TimeToReady(it.Unit, it.AmmoId));
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
        /// Fire a hand-picked set of shots so they arrive together. One anchor is elected across the whole
        /// set, so shots at several targets land on one time on target.
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

            // Before the formation flags and PrepareIntent, which depend on the surviving shot counts.
            if (!TryPrepareCommit(items)) return;

            foreach (Intent it in items)
            {
                // Per target: the game's formation-attack flag means several shooters engage this contact together.
                int atTarget = 0;
                foreach (Intent o in items) if (o.Target == it.Target) atTarget++;
                it.IsFormation = atTarget > 1;
                PrepareIntent(it);
            }

            // Estimates, anchor and scheduling complete once the workers answer (Coordinator.Pending.cs).
            BeginCommit(items, planner: true, isStrike: false);
        }

        /// <summary>
        /// Fire the planner's staged strike. With a strike armed, the picks join the orders already collected
        /// and commit as one strike with one anchor; otherwise this is a coordinated fire of the picks.
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
                // Formation flag per target over the whole batch; intercepted orders keep the flag the game passed.
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
        /// Hand one order to the game. False when the game did not take it, which the caller must act on. Not
        /// retried: the game may have accepted the task before throwing, and a retry would fire it twice.
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
