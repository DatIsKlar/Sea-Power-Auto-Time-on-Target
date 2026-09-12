using System.Collections.Generic;
using System.Reflection;
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

        // Reused by every launcher probe on the tick; main thread only.
        private static readonly List<LauncherProbe.LauncherView> _probeScratch =
            new List<LauncherProbe.LauncherView>();

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

            // Recorded as well as logged: the log rotates, the corpus does not.
            if (s.StageChanges.Count < 32)
                s.StageChanges.Add($"{now}@t+{simNow - s.LaunchTime:0.0}s");

            float altU = w.transform != null ? w.transform.position.y : 0f;
            float slantM = GameUnits.MetersBetween(w, tgt);
            float slantU = slantM / GameUnits.MetersPerUnity;
            float altDelta = altU - (tgt.transform != null ? tgt.transform.position.y : 0f);
            float flatU = GameMath.FlatFromSlant(slantU, altDelta);

            if (s.StageEvents.Count < 32)
                s.StageEvents.Add(
                    $"{simNow - s.LaunchTime:0.000}:{prev}>{now}:{flatU:0.000}:" +
                    $"{slantM:0.000}:{altU:0.000}:{w._velocityInKnots:0.000}");

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

        private const int MaxFlightTrackSamples = 256;

        /// <summary>
        /// Persist the live trajectory and the target state that produced it. Fields are
        /// t:px:py:pz:speed:pitch:heading:stage:flatM:slantM:tx:ty:tz:targetCourse:
        /// targetSpeed:groupActive:groupLeader:groupSpeed. Positions and altitude are Unity units;
        /// distances are metres. The cadence is intentionally shared with sim-track/track.
        /// </summary>
        private static void MaybeRecordFlightTrack(WeaponBase w, ObjectBase tgt, FlightSample s,
                                                   float simNow)
        {
            if (s.FlightTrack.Count >= MaxFlightTrackSamples ||
                simNow < s.NextFlightTrackSample || w.transform == null ||
                tgt == null || tgt.transform == null)
                return;

            float age = simNow - s.LaunchTime;
            s.NextFlightTrackSample = simNow + TelemetryCadence.IntervalFor(age);

            Vector3 pos = w.transform.position;
            Vector3 targetPos = tgt.transform.position;
            Vector3 forward = w.transform.forward;
            float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float slantM = (targetPos - pos).magnitude * GameUnits.MetersPerUnity;
            float slantU = slantM / GameUnits.MetersPerUnity;
            float flatU = GameMath.FlatFromSlant(slantU, pos.y - targetPos.y);
            float flatM = flatU * GameUnits.MetersPerUnity;
            Missile m = w as Missile;
            bool groupActive = m != null && m._inMissileGroup;
            bool groupLeader = m != null && m.GroupLeader;
            float groupSpeed = m != null ? m._inGroupVelocityInKnots : -1f;

            s.FlightTrack.Add(
                $"{age:0.000}:{pos.x:0.000}:{pos.y:0.000}:{pos.z:0.000}:" +
                $"{w._velocityInKnots:0.000}:{pitch:0.000}:{heading:0.000}:{w._flightStage}:" +
                $"{flatM:0.000}:{slantM:0.000}:{targetPos.x:0.000}:{targetPos.y:0.000}:" +
                $"{targetPos.z:0.000}:{tgt.getHeading():0.000}:{tgt._velocityInKnots:0.000}:" +
                $"{(groupActive ? 1 : 0)}:{(groupLeader ? 1 : 0)}:{groupSpeed:0.000}");
        }

        /// <summary>Record discrete formation-state changes; group velocity itself is sampled in
        /// FlightTrack because it can vary continuously and would otherwise create a line per tick.</summary>
        private static void MaybeRecordGroupChange(WeaponBase w, FlightSample s, float simNow)
        {
            Missile m = w as Missile;
            if (m == null) return;
            bool active = m._inMissileGroup;
            bool leader = m.GroupLeader;
            if (active == s.LastGroupActive && leader == s.LastGroupLeader) return;
            s.LastGroupActive = active;
            s.LastGroupLeader = leader;
            if (s.GroupChanges.Count < 32)
                s.GroupChanges.Add(
                    $"{simNow - s.LaunchTime:0.000}:{(active ? 1 : 0)}:{(leader ? 1 : 0)}:" +
                    $"{m._inGroupVelocityInKnots:0.000}");
        }

        internal const string GuardTrackFields =
            "t:stage:dt:wpCount:wpType:pitchMode:done:allowExceed:climbAngle:descentAngle:" +
            "endAlt:endHeightType:boundary:pctDone:totalDist:ctxFlatV:launchArcDone:" +
            "pos:vy:pitch:desired:future:lastWp:intercept:" +
            "rate:num2:num3:num4:num6:num7:num8:x:desiredNeFuture:fired:tgtType:inGroup:why";

        // Dense through the climb, the apex and the first step-down of every round in the corpus
        // (SS-N-19's apex is near t+80), sparse after. Transitions are written at any age.
        private const float GuardDenseEndSim = 120f;
        private const float GuardDenseIntervalSim = 0.5f;
        private const float GuardSparseIntervalSim = 5f;
        private const int GuardTrackCap = 600;

        private static readonly FieldInfo WpPercentDone =
            typeof(Waypoint).GetField("_percentageDone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WpTotalDist =
            typeof(Waypoint).GetField("_totalDist", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WpContext =
            typeof(Waypoint).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CtxFlatVelocity =
            typeof(Waypoint.Context).GetField("FlatVelocity", BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo MissileLaunchArcDone =
            typeof(Missile).GetField("_launchArcComplete", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Recompute Missile.SetMissileCourse's surface-target pre-apex guard (Missile.cs:2559-2581)
        /// from the live waypoint, term for term, and record it with everything that fed it.
        ///
        /// Recorded from launch on. The earlier recorder only covered t+35..65, and SS-N-19's
        /// unexplained nose-down from 45 to 4 degrees happens at t+15..27, before its window opens.
        /// The discrete state (waypoint, stage, the guard's three tests and its result) is keyed
        /// every frame; when the key changes, the previous frame's line is written as well, so
        /// both sides of each transition survive.
        /// </summary>
        private static void RecordGuardTrack(WeaponBase w, FlightSample s, float simNow)
        {
            if (s.GuardTrack.Count >= GuardTrackCap) return;
            Missile m = w as Missile;
            if (m == null || m.transform == null || m._ap == null) return;

            // Two frames per round, swapped every tick, so capturing allocates nothing once warm.
            GuardFrame f = s.GuardNext ?? new GuardFrame();
            float age = simNow - s.LaunchTime;
            Vector3 pos = m.transform.position;
            Vector3 fwd = m.transform.forward;
            f.Age = age;
            f.Stage = m._flightStage;
            f.Dt = m._dt;
            f.Pos = pos;
            f.Pitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            f.Vy = m._velocityVecInUnity.y;
            f.HasTarget = m.CurrentTarget != null;
            if (f.HasTarget) f.TgtType = m.CurrentTarget._type;
            f.InGroup = m._inMissileGroup;
            f.Intercept = m._targetInterceptPosition.ToUnity();
            f.WpCount = m.Waypoints != null ? m.Waypoints.Count : 0;

            if (f.WpCount > 0)
            {
                Waypoint wp = m.Waypoints[0];
                Waypoint.Settings set = wp.WpSettings;
                Vector3 goal = wp.WpStatus.DesiredPosition.ToUnity();
                Vector3 future = wp.WpStatus.FuturePosition.ToUnity();
                bool kappa = set.PitchMode == Waypoint.ToHeightType.KappaLoft;

                // The guard, as the game writes it. `rate` is the raw ini value, or the ToBearing
                // override in that state: no G-derate and no banking roll addend.
                float rate = m._ap._maxTurnRateDegrees;
                if (m._flightStage == WeaponBase.FlightStage.ToBearing &&
                    m._ap._toBearingTurnRateDegrees > 0f)
                    rate = m._ap._toBearingTurnRateDegrees;
                float num2 = future.y - pos.y;
                Vector3 path = (future - goal).normalized;
                float num3 = Mathf.Abs(Mathf.Asin(path.y) - Mathf.Asin(fwd.y)) * 57.29578f;
                Vector3 toGoal = goal - pos;
                float num4 = Vector3.Angle(new Vector3(fwd.x, 0f, fwd.z).normalized,
                                           new Vector3(toGoal.x, 0f, toGoal.z).normalized);
                float num5 = Mathf.Sqrt(num4 * num4 + num3 * num3);
                float num6 = num5 > 0.001f ? num3 / num5 : 1f;
                float num7 = rate * num6;
                float num8 = num2 / f.Vy;
                float x = num3 / num8;
                f.Distinct = goal != future;   // Unity's approximate Vector3 inequality
                f.RateTest = Mathf.Abs(x) > num7;
                f.TimeTest = num8 > 0.1f;
                f.Fired = !kappa && f.RateTest && f.TimeTest && f.Distinct;

                float pct = float.NaN, total = float.NaN, ctxV = float.NaN;
                try
                {
                    if (WpPercentDone != null) pct = (float)(double)WpPercentDone.GetValue(wp);
                    if (WpTotalDist != null) total = (float)(double)WpTotalDist.GetValue(wp);
                    if (WpContext != null && CtxFlatVelocity != null)
                        ctxV = (float)CtxFlatVelocity.GetValue(WpContext.GetValue(wp));
                }
                catch { }
                string arc = "";
                try
                {
                    if (MissileLaunchArcDone != null)
                        arc = (bool)MissileLaunchArcDone.GetValue(m) ? "1" : "0";
                }
                catch { }

                f.WpType = wp.Wp?.GetType().Name;
                f.PitchMode = set.PitchMode;
                f.Done = wp.WpStatus.Done;
                f.AllowExceed = set.AllowExceedingAngleLimits;
                f.ClimbAngle = set.Constraints.ClimbAngle;
                f.DescentAngle = set.Constraints.DescentAngle;
                f.EndAlt = set.EndHeight.Altitude;
                f.EndHeightType = set.EndHeight.HeightType;
                f.Boundary = wp.Wp is Waypoint.DistanceToTargetWaypoint d ? d.Distance : -1f;
                f.Pct = pct; f.Total = total; f.CtxV = ctxV; f.Arc = arc;
                f.Goal = goal; f.Future = future;
                f.LastWp = m._lastWaypointPosition.ToUnity();
                f.Rate = rate; f.Num2 = num2; f.Num3 = num3; f.Num4 = num4; f.Num6 = num6;
                f.Num7 = num7; f.Num8 = num8; f.X = x;
            }

            GuardFrame prev = s.GuardPrev;   // null on the round's first tick
            bool changed = prev != null && !f.SameState(prev);
            bool due = simNow >= s.NextGuardSample;
            if (changed && !s.GuardLastWritten)
                s.GuardTrack.Add(prev.Format() + ":before");
            if (changed || due || prev == null)
            {
                if (s.GuardTrack.Count < GuardTrackCap)
                    s.GuardTrack.Add(f.Format() + (changed ? ":change" : ":cadence"));
                s.NextGuardSample = simNow + (age < GuardDenseEndSim
                    ? GuardDenseIntervalSim : GuardSparseIntervalSim);
                s.GuardLastWritten = true;
            }
            else s.GuardLastWritten = false;
            s.GuardNext = prev;
            s.GuardPrev = f;
        }

        /// <summary>
        /// One tick of the values a guard-track line prints. The recorder keeps roughly one tick in
        /// fifty (a change of state, the cadence, or the tick just before a change), and it used to
        /// format its state key and its whole 37-field line on every tick to decide. Now each tick
        /// is captured into a reused frame and formatted only when kept. <see cref="Format"/> is the
        /// same expression the recorder always used, so the corpus lines are unchanged.
        /// </summary>
        private sealed class GuardFrame
        {
            internal float Age, Dt, Vy, Pitch;
            internal WeaponBase.FlightStage Stage;
            internal int WpCount;
            internal Vector3 Pos, Intercept;
            internal bool HasTarget, InGroup;
            internal ObjectBase.ObjectType TgtType;
            // The waypoint's side, meaningful only while WpCount > 0.
            internal string WpType, Arc;
            internal Waypoint.ToHeightType PitchMode;
            internal Waypoint.HeightType EndHeightType;
            internal bool Done, AllowExceed, RateTest, TimeTest, Distinct, Fired;
            internal float ClimbAngle, DescentAngle, EndAlt, Boundary, Pct, Total, CtxV;
            internal Vector3 Goal, Future, LastWp;
            internal float Rate, Num2, Num3, Num4, Num6, Num7, Num8, X;

            /// <summary>
            /// The discrete state a change is detected on, which is exactly what the key string
            /// used to hold: stage and waypoint count, and with a waypoint its type, pitch mode,
            /// done flag and the four guard tests.
            /// </summary>
            internal bool SameState(GuardFrame o)
            {
                if (Stage != o.Stage || WpCount != o.WpCount) return false;
                if (WpCount == 0) return true;
                return WpType == o.WpType && PitchMode == o.PitchMode && Done == o.Done &&
                       RateTest == o.RateTest && TimeTest == o.TimeTest &&
                       Distinct == o.Distinct && Fired == o.Fired;
            }

            internal string Format()
            {
                string tgtType = HasTarget ? TgtType.ToString() : "none";
                string head = $"{Age:0.000}:{Stage}:{Dt:0.000}:{WpCount}:";
                if (WpCount == 0)
                    return head + $"::::::::::::::{V(Pos)}:{Vy:0.000}:{Pitch:0.000}::::" +
                           $"{V(Intercept)}:::::::::::{tgtType}:" +
                           $"{(InGroup ? 1 : 0)}";
                return head +
                    $"{WpType}:{PitchMode}:{(Done ? 1 : 0)}:" +
                    $"{(AllowExceed ? 1 : 0)}:{ClimbAngle:0.###}:" +
                    $"{DescentAngle:0.###}:{EndAlt:0.###}:" +
                    $"{EndHeightType}:{Boundary:0.000}:{Pct:0.000000}:{Total:0.000}:" +
                    $"{CtxV:0.000}:{Arc}:{V(Pos)}:{Vy:0.000}:{Pitch:0.000}:{V(Goal)}:{V(Future)}:" +
                    $"{V(LastWp)}:" +
                    $"{V(Intercept)}:{Rate:0.###}:{Num2:0.000}:" +
                    $"{Num3:0.000}:{Num4:0.000}:{Num6:0.0000}:{Num7:0.000}:{Num8:0.000}:" +
                    $"{X:0.000}:{(Distinct ? 1 : 0)}:{(Fired ? 1 : 0)}:{tgtType}:" +
                    $"{(InGroup ? 1 : 0)}";
            }
        }

        private static string V(Vector3 v) => $"{v.x:0.000},{v.y:0.000},{v.z:0.000}";
    }
}
