using System.Collections.Generic;
using System.Diagnostics;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Per-missile and per-launcher tracing: engage-state sampling, the first-sighting card, stage
    /// changes, throttled telemetry and the round-based prediction score. Everything here is gated
    /// behind a diagnostic switch and exists to be read in the log, not acted on.
    ///
    /// Split out of LaunchDiagnostics.cs for size only.
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        private const float EngageStateSampleSim = 0.25f; // sim-time cadence for the state trace

        /// <summary>
        /// Trace the launcher's own state machine for the whole life of an order, logging each
        /// transition, how long the previous state held, and the shot count at that moment.
        ///
        /// This exists because the measured order-to-first-round time on a guidance-requiring mount
        /// is a repeatable 2.7 s that no modelled term accounts for: PreLaunchDelay, the reaction
        /// draw and the whole OODA stack are all zero on those launchers. The states in
        /// WeaponSystemLauncher.Update each carry a real duration, and this says WHICH one holds,
        /// rather than inferring it from a total.
        ///
        /// It used to stop at the first round, which made the second half of a ripple invisible.
        /// That is where the Kynda problem lives: it fires a linked fore/aft pair, turns the ship to
        /// unmask again, then fires the rest, and the gap in the middle is a ship turn rather than
        /// anything the launcher is doing. Covering the whole order is what makes that legible.
        ///
        /// Volume is bounded by logging transitions only, so a launcher sitting in one state is
        /// silent no matter how long it sits. Verbose only.
        /// </summary>
        private static readonly List<LauncherProbe.LauncherView> _probeScratch =
            new List<LauncherProbe.LauncherView>();

        private static void SampleEngageStates(float simNow)
        {
            if (!Coordinator.VerboseLog || _launchExpectations.Count == 0) return;

            _tracedThisTick.Clear();
            for (int i = 0; i < _launchExpectations.Count; i++)
            {
                LaunchExpectation e = _launchExpectations[i];
                if (e.Unit == null || e.Unit.IsDestroyed) continue;
                if (simNow - e.LastEngageSampleSim < EngageStateSampleSim) continue;
                e.LastEngageSampleSim = simNow;

                LauncherProbe.Collect(e.Unit, e.AmmoId, e.Target, _probeScratch);
                if (_probeScratch.Count == 0) continue;
                if (e.Launchers == null) e.Launchers = new List<LauncherTrace>(_probeScratch.Count);

                for (int k = 0; k < _probeScratch.Count; k++)
                {
                    LauncherProbe.LauncherView v = _probeScratch[k];
                    if (!_launcherTraces.TryGetValue(v.Id, out LauncherTrace t))
                    {
                        // First sighting: record the state without logging a transition into it,
                        // the same way the old code stayed silent until it had a previous state.
                        t = new LauncherTrace
                        {
                            Id = v.Id, SystemName = v.SystemName, State = v.State, SinceSim = simNow,
                            WarmupCount = v.State == OnRailWarmupState ? 1 : 0,
                        };
                        _launcherTraces[v.Id] = t;
                        e.Launchers.Add(t);
                        continue;
                    }
                    if (FindTrace(e.Launchers, v.Id) == null) e.Launchers.Add(t);
                    // A launcher serving two orders is read once per tick, by whichever order
                    // reached it first. The state it is in is the same either way.
                    if (!_tracedThisTick.Add(v.Id)) continue;
                    if (t.State == v.State) continue;

                    float held = simNow - t.SinceSim;
                    // D1: the observed side of the declared-versus-observed comparison. Captured on
                    // the way OUT of the state, which is the only moment its full duration is known.
                    if (t.State == OnRailWarmupState) t.LastWarmupHold = held;
                    if (v.State == OnRailWarmupState) t.WarmupCount++;

                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] engage-state {e.AmmoId} from {UnitNaming.SafeName(e.Unit)} " +
                        $"#{t.Id}[{t.SystemName}]: {t.State} held {held:0.00}s -> {v.State} " +
                        $"at t+{simNow - e.RegisteredSim:0.00}s, launched {e.Launched}/{e.Requested}" +
                        $"{(t.WarmupCount > 1 ? $", warmup #{t.WarmupCount}" : "")}{Unmasking(e)}");

                    // D11: if the state it just left is one the startup model does not cost, ask
                    // which declared field carries that duration. Verbose, once per system and
                    // state per mission. See LauncherTimingProbe.
                    LauncherTimingProbe.OnStateLeft(e.Unit, e.AmmoId, v.System, t.SystemName,
                                                    t.State, held);

                    t.State = v.State;
                    t.SinceSim = simNow;
                }
            }
        }

        /// <summary>The engage state whose duration this whole investigation turned on.</summary>
        private const string OnRailWarmupState = "OnRailWarmup";

        private static LauncherTrace FindTrace(List<LauncherTrace> traces, int id)
        {
            // Linear: a ship has single-digit launchers for one ammo, so this beats a dictionary
            // and sidesteps the question of whether BaseSystem overrides Equals.
            for (int i = 0; i < traces.Count; i++) if (traces[i].Id == id) return traces[i];
            return null;
        }

        /// <summary>
        /// Training context for the engage-state line: where the target sits relative to the ship's
        /// head, and whether the ship is currently steering to fix that.
        ///
        /// This is what tells an arc problem apart from a launcher problem. A Kynda's SS-N-3 mounts
        /// bear only through FiringArcs=-105,-75|75,105, two 30-degree windows abeam, so the ship
        /// must hold the target within 15 degrees of a beam to shoot at all. Its 8 rounds go out as
        /// linked fore/aft pairs with a long turn in the middle, and without the relative bearing
        /// there is no way to tell from a log whether it is still turning, has swung past the
        /// window, or is waiting on something else.
        ///
        /// Relative bearing rather than a real arc test, deliberately: targetIsInFiringArc wants an
        /// intercept point that does not exist before launch, and a bearing the reader can check
        /// against the ammo's own FiringArcs is both cheaper and harder to get subtly wrong.
        /// </summary>
        private static string Unmasking(LaunchExpectation e)
        {
            ObjectBase u = e.Unit, t = e.Target;
            if (u == null || t == null || t.IsDestroyed) return "";
            Vector3 to = t.transform.position - u.transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) return "";
            // Signed about world up: negative to port, positive to starboard, so it reads directly
            // against the FiringArcs pairs in the ship's ini.
            float rel = Vector3.SignedAngle(u.transform.forward, to, Vector3.up);
            return $", target rel brg {rel:+0;-0} deg, aligning {u.Aligning}";
        }

        /// <summary>
        /// Invokes <paramref name="visit"/> for every friendly missile currently tracked in flight
        /// along with its live intended target (both guaranteed non-null and alive). Used by the
        /// engagement overview to count in-flight rounds per target.
        /// </summary>
        internal static void ForEachInFlight(System.Action<WeaponBase, ObjectBase> visit)
        {
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed) continue;
                ObjectBase t = w.CurrentIntendedTargetObject;
                if (t == null || t.IsDestroyed) continue;
                visit(w, t);
            }
        }

        /// <summary>
        /// True while at least one tracked friendly round is still flying at this target. The
        /// single-target question behind <see cref="ForEachInFlight"/>, for the engagement board's
        /// prune, which runs on the coordinator tick and so cannot use the display scratch the
        /// visitor fills.
        /// </summary>
        internal static bool HasInFlightAt(ObjectBase target)
        {
            if (target == null) return false;
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed) continue;
                if (ReferenceEquals(w.CurrentIntendedTargetObject, target)) return true;
            }
            return false;
        }

        // One-time per-missile line at first sighting: nominal speeds + our kinematic estimate, so a
        // late group can be read against what the game's own solo sim predicted. Flight-model trace.
        private static void LogTrackInit(WeaponBase w, float est)
        {
            if (!Coordinator.TraceFlightModel) return;
            AmmunitionParameters ap = w?._ap;
            if (ap == null) return;
            bool grouped = ap._maxGroupSize > 1;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] track-init {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                $"nominal cruise {ap._maxVelocityInKnots:0}/loft {ap._maxLoftVelocityInKnots:0}/" +
                $"term {ap._terminalVelocityInKnots:0} kn, kinEst {est:0.0}s, grouped {grouped}");

            // Grounded-integrator per-phase breakdown ; pins WHICH loft phase (climb/cruise/descent)
            // the model gets wrong when its intercept time disagrees with the actual flown time.
            // peakAlt vs loftAlt = climb-height error; a slow VTerm + long DescentTime = descent error.
            // Hoisted out of the `if` so the integrator run can be timed. TryIntegratedPhaseDiag
            // calls IntegratedEndTimeCore directly rather than going through FlightTime.Estimate, so
            // without this the profiler misses it and the stage totals fall short of `model: loop`.
            bool phaseDiagOk = false;
            float intT = 0f;
            FlightTime.IntegratedPhases ph = default;
            if (w._launchPlatform != null && w.CurrentIntendedTargetObject != null)
            {
                CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                phaseDiagOk = FlightTime.TryIntegratedPhaseDiag(w._launchPlatform, ap._ammunitionFileName,
                    w.CurrentIntendedTargetObject, out intT, out ph);
                CoordinatorProfiler.End(CoordinatorProfiler.Stage.FlightEstimate);
                CoordinatorProfiler.CountEstimate(cacheHit: false);   // always a fresh sim, never cached
            }
            if (phaseDiagOk)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] int-phases {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                    $"intercept {intT:0.0}s, lofting {ph.Lofting}, loftAlt {ph.LoftAltTarget:0}u, " +
                    $"peakAlt {ph.PeakAltU:0}u, climb {ph.ClimbTime:0.0}s/cruise {ph.CruiseTime:0.0}s/" +
                    $"descent {ph.DescentTime:0.0}s, spd start {ph.VStart:0}/climbExit {ph.VClimbExit:0}/" +
                    $"cruiseExit {ph.VCruiseExit:0}/term {ph.VTerm:0}kn, " +
                    $"finalDist {ph.FinalDistU:0}u/termDist {ph.TermDistU:0}u");

                // The region model's phase boundaries, printed in the same units the `stage-obs`
                // line reports so the two can be diffed directly. finalDist should line up with the
                // real MaintainLoftAlt -> Maintain{SeaSkimming,FinalFlightAlt} transition, and
                // diveStart with the real -> TerminalApproach transition. These boundaries are
                // hand-derived from ini fields and are the most fragile part of the model (see
                // docs/plans/done/2026-09-02-waypoint-sim-port.md, the rejected Part L), so this is the evidence
                // that says whether they are right rather than merely tuned.
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] stage-model {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                    $"finalDist {ph.FinalDistU:0}u, termDist {ph.TermDistU:0}u, " +
                    $"diveStart {(ph.DiveStartU >= 0f ? ph.DiveStartU.ToString("0") + "u" : "never")}, " +
                    $"loftAlt {ph.LoftAltTarget:0}u, onsetDeg {ph.DescentOnsetDeg:0.0}°");
            }

        }

        // Fires once per transition of the game's OWN flight-stage machine
        // (Launch -> ToBearing -> MoveToLoftAlt -> MaintainLoftAlt -> ... -> TerminalApproach).
        //
        // The only direct ground truth for the boundaries the model derives from ini fields: where
        // the real missile leaves the loft is the model's finalDist, and where it enters
        // TerminalApproach is its diveStart. `track` prints the stage too, but samples every 15s,
        // which on a Mach-10 lofter brackets a transition to a ~43km window.
        //
        // Distances are reported three ways because `track` logs 3D SLANT and `sim-track` logs flat;
        // flat is the one comparable to the model's boundaries.
        //
        // Change-gated, so a full flight emits under a dozen lines. Flight-model trace + Coordinated only.
        private static void MaybeLogStageChange(WeaponBase w, ObjectBase tgt, FlightSample s, float simNow)
        {
            if (!Coordinator.TraceFlightModel) return;
            WeaponBase.FlightStage now = w._flightStage;
            if (now == s.LastStage) return;
            WeaponBase.FlightStage prev = s.LastStage;
            s.LastStage = now;

            float altU = w.transform != null ? w.transform.position.y : 0f;
            float slantM = GameUnits.MetersBetween(w, tgt);
            float slantU = slantM / GameUnits.MetersPerUnity;
            float altDelta = altU - (tgt.transform != null ? tgt.transform.position.y : 0f);
            float flatU = GameMath.FlatFromSlant(slantU, altDelta);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] stage-obs {(w._ap != null ? w._ap._ammunitionFileName : "?")}#{w.GetInstanceID()}: " +
                $"{prev} -> {now} at t+{simNow - s.LaunchTime:0.0}s, " +
                $"flat {flatU:0}u ({flatU * GameUnits.MetersPerUnity / 1000f:0.0}km), " +
                $"slant {slantM / 1000f:0.0}km, alt {altU:0.0}u, spd {w._velocityInKnots:0}kn");
        }

        // Throttled per-missile telemetry: actual vs nominal speed, group/leader state, in-group
        // commanded speed, flight stage, altitude, distance. Separates group-drag (leader spd ~0.6x
        // nominal) from a solo estimate error. Flight-model trace. Updates the sample's throttle stamp.
        private static void MaybeLogTelemetry(WeaponBase w, ObjectBase tgt, FlightSample s, float distM, float simNow)
        {
            if (!Coordinator.TraceFlightModel) return;
            float interval = TelemetryCadence.IntervalFor(simNow - s.LaunchTime);
            if (s.LastTelemetrySim >= 0f && (simNow - s.LastTelemetrySim) < interval) return;
            s.LastTelemetrySim = simNow;

            AmmunitionParameters ap = w._ap;
            float nominal = ap != null ? ap._maxVelocityInKnots : 0f;
            Missile m = w as Missile;
            string grp = m != null && m._inMissileGroup ? (m.GroupLeader ? "grpL" : "grp") : "solo";
            float vGrp = m != null ? m._inGroupVelocityInKnots : -1f;
            string stage = m != null ? m._flightStage.ToString() : "?";
            float altU = w.transform != null ? w.transform.position.y : 0f;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] track {(ap != null ? ap._ammunitionFileName : "?")}#{w.GetInstanceID()} -> {UnitNaming.SafeName(tgt)}: " +
                $"t+{simNow - s.LaunchTime:0.0}s spd {w._velocityInKnots:0}/{nominal:0}kn {grp} " +
                $"vGrp {vGrp:0} stage {stage} alt {altU:0.0} dist {distM / 1000f:0.0}km");
        }

        /// <summary>Ages (s after launch) at which the round-based prediction is sampled. The first
        /// is past boost, so the round is at cruise; the second is far enough later that drift is
        /// visible. A flight shorter than the late age simply never records one.</summary>
        private const float RoundPredEarlyAgeSim = 15f;

        private const float RoundPredLateAgeSim = 60f;

        /// <summary>
        /// D7: what the proposed fix would have predicted, captured from the round in the air.
        ///
        /// The fix under consideration replaces "re-estimate the flight from where the shooter is
        /// now" with "ask the round already in the air how long it has left". The open question was
        /// whether that stays usable when the TARGET moves, which cannot be tested by ordering an AI
        /// ship to manoeuvre. It does not need to be: scoring this prediction against the round's
        /// ACTUAL impact measures the same thing on whatever motion the target really does, on every
        /// coordinated round of every run, rather than on one contrived case.
        ///
        /// Range over current speed, deliberately crude, because that is exactly what the fix would
        /// use. If this is accurate to a few seconds the fix is sound; if it is not, the fix needs
        /// the game's own intercept solution instead and that is a different piece of work.
        /// </summary>
        private static void SampleRoundPrediction(WeaponBase w, FlightSample s, float distM, float simNow)
        {
            float age = simNow - s.LaunchTime;
            bool wantEarly = s.RoundPredEarlySim < 0f && age >= RoundPredEarlyAgeSim;
            bool wantLate = s.RoundPredLateSim < 0f && age >= RoundPredLateAgeSim;
            if (!wantEarly && !wantLate) return;

            float speedMs = w._velocityInKnots * GameUnits.KnotsToMs;
            if (speedMs <= 1f) return;
            float pred = simNow + distM / speedMs;
            if (wantEarly) s.RoundPredEarlySim = pred;
            if (wantLate) s.RoundPredLateSim = pred;
        }

        /// <summary>D7's verdict, appended to the impact line: the round-based prediction against
        /// the impact that actually happened.</summary>
        private static string RoundPredictionScore(FlightSample s)
        {
            if (s.RoundPredEarlySim < 0f && s.RoundPredLateSim < 0f) return "";
            string early = s.RoundPredEarlySim >= 0f
                ? $"t+{RoundPredEarlyAgeSim:0} {s.RoundPredEarlySim:0.0} " +
                  $"(err {s.LastSeenTime - s.RoundPredEarlySim:+0.0;-0.0}s)"
                : $"t+{RoundPredEarlyAgeSim:0} n/a";
            string late = s.RoundPredLateSim >= 0f
                ? $", t+{RoundPredLateAgeSim:0} {s.RoundPredLateSim:0.0} " +
                  $"(err {s.LastSeenTime - s.RoundPredLateSim:+0.0;-0.0}s)"
                : "";
            return $" | roundPred {early}{late}";
        }
    }
}
