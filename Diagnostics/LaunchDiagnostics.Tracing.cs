using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Per-missile and per-launcher tracing: engage-state sampling, the first-sighting card, stage changes,
    /// throttled telemetry and the recorded tracks. Everything here is gated behind a diagnostic switch.
    /// </summary>
    internal static partial class LaunchDiagnostics
    {
        private const float EngageStateSampleSim = 0.25f; // sim-time cadence for the state trace

        // Reused by every launcher probe on the tick; main thread only.
        private static readonly List<LauncherProbe.LauncherView> _probeScratch =
            new List<LauncherProbe.LauncherView>();

        /// <summary>
        /// Trace each launcher's state machine for the whole life of an order: every transition, how long the
        /// previous state held, and the shot count. It shows which state an order-to-first-round delay sits
        /// in, and covering the whole order shows a mid-ripple ship turn. Transitions only; verbose only.
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
                        // First sighting: record the state without logging a transition into it.
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
                    // A launcher serving two orders is read once per tick.
                    if (!_tracedThisTick.Add(v.Id)) continue;
                    if (t.State == v.State) continue;

                    float held = simNow - t.SinceSim;
                    // Observed warm-up, captured on the way out of the state, when its duration is known.
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

        /// <summary>The engage state whose observed duration is compared with the declared warm-up.</summary>
        private const string OnRailWarmupState = "OnRailWarmup";

        private static LauncherTrace FindTrace(List<LauncherTrace> traces, int id)
        {
            // Linear: single-digit launchers per ammo, and it avoids depending on BaseSystem's Equals.
            for (int i = 0; i < traces.Count; i++) if (traces[i].Id == id) return traces[i];
            return null;
        }

        /// <summary>
        /// Training context for the engage-state line: the target's bearing relative to the ship's head, and
        /// whether the ship is steering. It tells an arc problem from a launcher problem (a Kynda's SS-N-3
        /// bears only through two 30-degree beam windows). A relative bearing rather than an arc test, since
        /// targetIsInFiringArc needs an intercept point that does not exist before launch.
        /// </summary>
        private static string Unmasking(LaunchExpectation e)
        {
            ObjectBase u = e.Unit, t = e.Target;
            if (u == null || t == null || t.IsDestroyed) return "";
            Vector3 to = t.transform.position - u.transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) return "";
            // Signed about world up: negative to port, positive to starboard, matching the ini's FiringArcs.
            float rel = Vector3.SignedAngle(u.transform.forward, to, Vector3.up);
            return $", target rel brg {rel:+0;-0} deg, aligning {u.Aligning}";
        }

        /// <summary>
        /// Visit every tracked friendly missile in flight with its live intended target (both non-null and
        /// alive). Used by the engagement overview.
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
        /// True while a tracked friendly round is still flying at this target. For the engagement board's
        /// prune, which runs on the tick and cannot use the display scratch.
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

#if LIVETEST
        /// <summary>
        /// True while any round AutoTOT fired is still flying, whatever it is aimed at now. Not per target: a
        /// seeker that switches to a formation sibling leaves <see cref="HasInFlightAt"/> false while the round flies.
        /// </summary>
        internal static bool AnyCoordinatedInFlight()
        {
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed) continue;
                if (kv.Value != null && kv.Value.Coordinated) return true;
            }
            return false;
        }
#endif

        // One line per missile at first sighting: nominal speeds and the model's estimate. Flight-model trace.
        private static void LogTrackInit(WeaponBase w, float est, in FlightTime.LaunchState launch)
        {
            if (!Coordinator.TraceFlightModel) return;
            AmmunitionParameters ap = w?._ap;
            if (ap == null) return;
            bool grouped = ap._maxGroupSize > 1;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] track-init {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                $"nominal cruise {ap._maxVelocityInKnots:0}/loft {ap._maxLoftVelocityInKnots:0}/" +
                $"term {ap._terminalVelocityInKnots:0} kn, kinEst {est:0.0}s, grouped {grouped}");

            // The integrator's per-phase breakdown, to say which loft phase disagrees. Hoisted so the run is
            // timed, since it bypasses FlightTime.Estimate.
            bool phaseDiagOk = false;
            float intT = 0f;
            FlightTime.IntegratedPhases ph = default;
            if (w._launchPlatform != null && w.CurrentIntendedTargetObject != null)
            {
                CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.FlightEstimate);
                phaseDiagOk = FlightTime.TryIntegratedPhaseDiag(w._launchPlatform, ap._ammunitionFileName,
                    w.CurrentIntendedTargetObject, launch, out intT, out ph);
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

                // The model's phase boundaries, in the units `stage-obs` prints, so the two can be diffed:
                // finalDist against the real loft-to-final transition, diveStart against TerminalApproach.
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] stage-model {ap._ammunitionFileName}#{w.GetInstanceID()}: " +
                    $"finalDist {ph.FinalDistU:0}u, termDist {ph.TermDistU:0}u, " +
                    $"diveStart {(ph.DiveStartU >= 0f ? ph.DiveStartU.ToString("0") + "u" : "never")}, " +
                    $"loftAlt {ph.LoftAltTarget:0}u, onsetDeg {ph.DescentOnsetDeg:0.0}°");
            }

        }

        // Once per transition of the game's own flight-stage machine: the ground truth for the model's
        // finalDist and diveStart. Distances three ways, since `track` logs slant and `sim-track` flat.
        // Flight-model trace, coordinated rounds only.
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

        // Throttled per-missile telemetry: speed against nominal, group state, stage, altitude, distance.
        // Separates group drag from a solo estimate error. Flight-model trace.
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

        // By reflection: _prevPitch is private, and a branch without either member loses only PitchRateTrack.
        private static readonly FieldInfo MissilePrevPitch =
            typeof(Missile).GetField("_prevPitch", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo GameSimulationTick =
            typeof(GameTime).GetProperty("simulationTick", BindingFlags.Static | BindingFlags.Public);

        /// <summary>
        /// Once a frame, the pitch rate the game's drag function reads on that frame's last tick, pooled into
        /// one-second rows. With the motor off the drag's stall term is convex in the rate, so tick-to-tick
        /// jitter raises drag above what the mean rate gives.
        ///
        /// Fields: t:frames:meanRate:sdRate:minRate:maxRate:shareAtOrBelowZero:meanSquare:squareAtMeanRate:
        /// meanPitch:meanSpeedKn:ticksPerFrame. t is whole seconds since launch; rate and pitch use the
        /// game's sign (nose down positive), in deg/s and deg.
        /// </summary>
        private static void MaybeRecordPitchRate(WeaponBase w, FlightSample s, float simNow)
        {
            Missile m = w as Missile;
            if (m == null || MissilePrevPitch == null || s.PitchRateTrack.Count >= MaxFlightTrackSamples)
                return;
            float dt = GameTime.fixedDeltaTime;
            if (dt <= 0f) return;
            float window = Mathf.Floor(simNow - s.LaunchTime);
            if (s.PitchRateN > 0 && window > s.PitchRateWindow) FlushPitchRate(s);
            if (s.PitchRateN == 0)
            {
                s.PitchRateWindow = window;
                s.PitchRateTickFirst = GameSimulationTick != null ? (long)GameSimulationTick.GetValue(null) : -1L;
            }

            float pitch = m._currentPitch;
            float rate = (pitch - (float)MissilePrevPitch.GetValue(m)) / dt;
            float vU = w._velocityInUnity;
            float llf = LiftLoad(pitch, rate, vU);
            s.PitchRateTickLast = GameSimulationTick != null ? (long)GameSimulationTick.GetValue(null) : -1L;
            s.PitchRateN++;
            s.PitchRateMin = s.PitchRateN == 1 ? rate : Mathf.Min(s.PitchRateMin, rate);
            s.PitchRateMax = s.PitchRateN == 1 ? rate : Mathf.Max(s.PitchRateMax, rate);
            if (rate <= 0f) s.PitchRateAtOrBelowZero++;
            s.PitchRateSum += rate;
            s.PitchRateSq += (double)rate * rate;
            s.PitchRateLlf2 += (double)llf * llf;
            s.PitchRatePitch += pitch;
            s.PitchRateSpeedU += vU;
        }

        // MissileSimulator.LiftLoadFactor, which is private.
        private static float LiftLoad(float pitchDeg, float rateDegPerSec, float velocityU) =>
            Mathf.Clamp01(Mathf.Abs(Mathf.Cos(pitchDeg * Mathf.Deg2Rad)) -
                          velocityU * 67.200066f * Mathf.Max(rateDegPerSec, 0f) * Mathf.Deg2Rad / 9.81f);

        private static void FlushPitchRate(FlightSample s)
        {
            int n = s.PitchRateN;
            float mean = (float)(s.PitchRateSum / n);
            float sd = Mathf.Sqrt(Mathf.Max((float)(s.PitchRateSq / n) - mean * mean, 0f));
            float meanPitch = (float)(s.PitchRatePitch / n);
            float meanU = (float)(s.PitchRateSpeedU / n);
            float atMean = LiftLoad(meanPitch, mean, meanU);
            string ticks = n > 1 && s.PitchRateTickFirst >= 0
                ? $"{(s.PitchRateTickLast - s.PitchRateTickFirst) / (float)(n - 1):0.0}" : "";
            s.PitchRateTrack.Add(
                $"{s.PitchRateWindow:0}:{n}:{mean:0.000}:{sd:0.000}:{s.PitchRateMin:0.000}:" +
                $"{s.PitchRateMax:0.000}:{s.PitchRateAtOrBelowZero / (float)n:0.00}:" +
                $"{s.PitchRateLlf2 / n:0.0000}:{atMean * atMean:0.0000}:{meanPitch:0.00}:" +
                $"{meanU / GameUnits.KnotsToUnityPerSecond:0}:{ticks}");
            s.PitchRateN = 0;
            s.PitchRateAtOrBelowZero = 0;
            s.PitchRateSum = s.PitchRateSq = s.PitchRateLlf2 = s.PitchRatePitch = s.PitchRateSpeedU = 0.0;
        }

        /// <summary>
        /// Record the live trajectory and target state. Fields:
        /// t:px:py:pz:speed:pitch:heading:stage:flatM:slantM:tx:ty:tz:targetCourse:targetSpeed:groupActive:
        /// groupLeader:groupSpeed:dragTargetAltU (Unity units for positions, metres for distances), on the
        /// sim-track and track cadence. dragTargetAltU is the game's y3 (Missile.cs:3170-3175), the altitude
        /// CalculateDrag is given: the CurrentTarget's, or the round's own when CurrentTarget is null.
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
            // The game's y3, read the same way the live mover reads it.
            ObjectBase dragTgt = w.CurrentTarget;
            float dragTargetAltU = dragTgt != null && dragTgt.transform != null
                                 ? dragTgt.transform.position.y : pos.y;
            Missile m = w as Missile;
            bool groupActive = m != null && m._inMissileGroup;
            bool groupLeader = m != null && m.GroupLeader;
            float groupSpeed = m != null ? m._inGroupVelocityInKnots : -1f;

            s.FlightTrack.Add(
                $"{age:0.000}:{pos.x:0.000}:{pos.y:0.000}:{pos.z:0.000}:" +
                $"{w._velocityInKnots:0.000}:{pitch:0.000}:{heading:0.000}:{w._flightStage}:" +
                $"{flatM:0.000}:{slantM:0.000}:{targetPos.x:0.000}:{targetPos.y:0.000}:" +
                $"{targetPos.z:0.000}:{tgt.getHeading():0.000}:{tgt._velocityInKnots:0.000}:" +
                $"{(groupActive ? 1 : 0)}:{(groupLeader ? 1 : 0)}:{groupSpeed:0.000}:" +
                $"{dragTargetAltU:0.000}");
        }

        /// <summary>
        /// Record the moment the drag target altitude changes hands, into StageEvents with a
        /// `seekOn,echo>seekOn,echo` token. CurrentTarget is null only when the seeker is on, holds no echo
        /// and the ammunition declares no TargetMemory (WeaponBase.cs:362); the round's own altitude then
        /// feeds CalculateDrag, which swings the lift-induced term sharply. A sampled series cannot pin that moment.
        /// </summary>
        private static void MaybeRecordSeekerEvent(WeaponBase w, ObjectBase tgt, FlightSample s,
                                                   float simNow)
        {
            if (w.transform == null || tgt == null || tgt.transform == null) return;
            bool seekerOn = w._seeker != null && w._seeker.isActive();
            bool echo = w.CurrentStrongestTargetEcho?._objectBase != null;
            int state = (seekerOn ? 1 : 0) | (echo ? 2 : 0);
            if (state == s.LastSeekerState) return;
            int prev = s.LastSeekerState;
            s.LastSeekerState = state;
            if (prev < 0 || s.StageEvents.Count >= 32) return;

            Vector3 pos = w.transform.position;
            float slantM = GameUnits.MetersBetween(w, tgt);
            float slantU = slantM / GameUnits.MetersPerUnity;
            float flatU = GameMath.FlatFromSlant(slantU, pos.y - tgt.transform.position.y);
            string Name(int v) => $"seek{(v & 1)},echo{((v & 2) >> 1)}";
            s.StageEvents.Add(
                $"{simNow - s.LaunchTime:0.000}:{Name(prev)}>{Name(state)}:{flatU:0.000}:" +
                $"{slantM:0.000}:{pos.y:0.000}:{w._velocityInKnots:0.000}");
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
            "rate:num2:num3:num4:num6:num7:num8:x:desiredNeFuture:fired:tgtType:inGroup:" +
            "knownTgt:knownVel:trueTgt:travel:speedU:insDrift:leadOn:why";

        // Dense through the climb, apex and first step-down, sparse after; transitions are written at any age.
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
        /// Recompute Missile.SetMissileCourse's surface-target pre-apex guard (Missile.cs:2559-2581) from the
        /// live waypoint, term for term, and record it with its inputs from launch on. The discrete state is
        /// keyed every frame; on a change the previous frame is written too, so both sides of a transition survive.
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
            // What the game builds the intercept point from, beside where the target really is; every frame.
            f.KnownTgt = m._currentKnownTargetPosition;
            f.KnownVel = m._lastKnownTargetVelocity;
            ObjectBase intended = m.CurrentIntendedTargetObject;
            f.TrueTgt = intended != null && intended.transform != null ? intended.transform.position : Vector3.zero;
            f.Travel = m._traveledDistance;
            f.SpeedU = m._velocityInUnity;
            f.InsDrift = InsDrift(m);
            f.LeadOn = m._leadTarget;
            f.WpCount = m.Waypoints != null ? m.Waypoints.Count : 0;

            if (f.WpCount > 0)
            {
                Waypoint wp = m.Waypoints[0];
                Waypoint.Settings set = wp.WpSettings;
                Vector3 goal = wp.WpStatus.DesiredPosition.ToUnity();
                Vector3 future = wp.WpStatus.FuturePosition.ToUnity();
                bool kappa = set.PitchMode == Waypoint.ToHeightType.KappaLoft;

                // The guard as the game writes it: `rate` is the raw ini value, or the ToBearing override in
                // that state, with no G-derate or roll addend.
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
        /// One tick of the values a guard-track line prints, captured into a reused frame and formatted only
        /// when kept. <see cref="Format"/> is the recorder's original expression, so corpus lines are unchanged.
        /// </summary>
        internal sealed class GuardFrame
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
            // The missile's guidance state (see RecordGuardTrack).
            internal Vector3 KnownTgt, KnownVel, TrueTgt;
            internal float Travel, SpeedU, InsDrift;
            internal bool LeadOn;

            /// <summary>
            /// The discrete state a change is detected on: stage and waypoint count, and with a waypoint its
            /// type, pitch mode, done flag and the four guard tests.
            /// </summary>
            internal bool SameState(GuardFrame o)
            {
                if (Stage != o.Stage || WpCount != o.WpCount) return false;
                if (WpCount == 0) return true;
                return WpType == o.WpType && PitchMode == o.PitchMode && Done == o.Done &&
                       RateTest == o.RateTest && TimeTest == o.TimeTest &&
                       Distinct == o.Distinct && Fired == o.Fired;
            }

            private string GuidanceTail() =>
                $":{V(KnownTgt)}:{V5(KnownVel)}:{V(TrueTgt)}:{Travel:0.00}:{SpeedU:0.00000}:" +
                $"{InsDrift:0.0000}:{(LeadOn ? 1 : 0)}";

            internal string Format()
            {
                string tgtType = HasTarget ? TgtType.ToString() : "none";
                string head = $"{Age:0.000}:{Stage}:{Dt:0.000}:{WpCount}:";
                if (WpCount == 0)
                    return head + $"::::::::::::::{V(Pos)}:{Vy:0.000}:{Pitch:0.000}::::" +
                           $"{V(Intercept)}:::::::::::{tgtType}:" +
                           $"{(InGroup ? 1 : 0)}" + GuidanceTail();
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
                    $"{(InGroup ? 1 : 0)}" + GuidanceTail();
            }
        }

        private static string V(Vector3 v) => $"{v.x:0.000},{v.y:0.000},{v.z:0.000}";

        // A ship's velocity is a few hundredths of a unit per second; three decimals would round it away.
        private static string V5(Vector3 v) => $"{v.x:0.00000},{v.y:0.00000},{v.z:0.00000}";

        // ---- Capture helpers: a Vessel target's speed command, a missile's INS drift and the launcher cell
        // a round spawned in. Written into the .result of a traced round; the corpus scripts read them.

        private static readonly System.Globalization.CultureInfo CellInv = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// The speed a surface target is converging on, beside its current speed. Vessel.OnFixedUpdate
        /// refreshes the wanted speed each tick; applyMainThrust caps it for sea state and rudder and
        /// accelerates toward it, or multiplies speed by _linearDamping (LinearDrag^fixedDeltaTime, fixed at
        /// spawn) while at or above it (Vessel.cs:352-436).
        /// </summary>
        internal struct SpeedCommand
        {
            public float Speed;          // _velocityInKnots
            public float Wanted;         // Compartments.WantedVelocityInKnots, as the speed command set it
            public float Converge;       // Wanted after applyMainThrust's sea-state and rudder caps
            public float Rudder;         // _currentRudderAngle in degrees, NaN when unreadable
            public int SeaState;         // -1 when the environment is not up
            public float DampingPerTick; // _linearDamping, NaN when unreadable
        }

        private static FieldInfo _rudderField, _dampingField;
        private static bool _speedFieldsResolved;

        /// <summary>Read a <see cref="Vessel"/> target's <see cref="SpeedCommand"/>. False for anything else.</summary>
        internal static bool TryReadSpeedCommand(ObjectBase target, out SpeedCommand c)
        {
            c = default;
            Vessel v = target as Vessel;
            if (v == null || v.IsDestroyed || v._compartments == null || v.VP == null) return false;
            if (!_speedFieldsResolved)
            {
                _speedFieldsResolved = true;
                // Both protected, on both branches.
                const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic;
                _rudderField = typeof(Vessel).GetField("_currentRudderAngle", Inst);
                _dampingField = typeof(Vessel).GetField("_linearDamping", Inst);
            }
            try
            {
                c.Speed = v._velocityInKnots;
                c.Wanted = v._compartments.WantedVelocityInKnots;
                c.Rudder = _rudderField?.GetValue(v) is float r ? r : float.NaN;
                c.DampingPerTick = _dampingField?.GetValue(v) is float d ? d : float.NaN;
                c.SeaState = Singleton<SeaPower.Environment>.InstanceExists()
                           ? Singleton<SeaPower.Environment>.Instance.SeaState : -1;
                c.Converge = CappedWanted(v, c.Wanted, c.SeaState, c.Rudder);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>applyMainThrust's two caps on the wanted speed, in its order (Vessel.cs:352-386).</summary>
        private static float CappedWanted(Vessel v, float wanted, int seaState, float rudder)
        {
            float num = wanted;
            VesselParameters vp = v.VP;
            if (seaState > 3)
            {
                float forwardCap = vp._maxForwardVelocityInKnots - (seaState - 3f) * vp._displacementCoeff;
                float backwardCap = -vp._maxBackwardVelocityInKnots + (seaState - 3f) * vp._displacementCoeff;
                if (num > 0f && num > forwardCap) num = forwardCap > 5f ? forwardCap : 5f;
                if (num < 0f && num < backwardCap) num = backwardCap < -2f ? backwardCap : -2f;
            }
            // NaN compares false, so an unreadable rudder applies no cap.
            float absRudder = Mathf.Abs(rudder);
            if (absRudder > 1f)
            {
                float fraction = Mathf.Min(absRudder / 25f, 1f);
                float rudderCap = v.MaxForwardSpeedInKnots - fraction * v.MaxForwardSpeedInKnots / 2f;
                if (rudderCap < num) num = rudderCap;
            }
            return num;
        }

        private static FieldInfo _insDriftField;
        private static bool _resolved;

        /// <summary>The INS drift the game rolls per missile at launch (private on both branches); NaN when
        /// unreadable.</summary>
        internal static float InsDrift(Missile m)
        {
            if (!_resolved)
            {
                _resolved = true;
                _insDriftField = typeof(Missile).GetField("_insDriftValue", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            try { return _insDriftField?.GetValue(m) is float f ? f : float.NaN; }
            catch (System.Exception) { return float.NaN; }
        }

        /// <summary>
        /// Preformatted <c>.result</c> lines: where the round is at first sighting, against every cell of
        /// the weapon system it left from and against the launching ship's centre, and whether the
        /// launcher the mod picks by firing arc is the one it left.
        /// </summary>
        internal static string DescribeLaunchCell(WeaponBase w, ObjectBase tgt)
        {
            try
            {
                var b = new System.Text.StringBuilder();
                Vector3 spawn = w.transform.position;
                ObjectBase platform = w._launchPlatform;
                b.AppendLine("LaunchSpawnPosU = " + CellV(spawn));
                if (platform != null && platform.transform != null)
                    b.AppendLine("LaunchCentreDistM = " + CellM(spawn - platform.transform.position));

                WeaponSystem ws = w._weaponSystem;
                if (ws == null)
                {
                    b.AppendLine("LaunchSystem = none");
                    return b.ToString();
                }
                string name;
                try { name = ws._vwp?._systemName ?? "?"; } catch (System.Exception) { name = "?"; }
                b.AppendLine("LaunchSystem = " + name);

                int cells = 0, bestContainer = -1, bestCell = -1;
                float best = float.MaxValue;
                Vector3 sum = Vector3.zero, bestResidualLocal = Vector3.zero;
                if (ws._containers != null)
                {
                    for (int c = 0; c < ws._containers.Count; c++)
                    {
                        WeaponContainer container = ws._containers[c];
                        if (container == null || container._gunObject == null || container._attachmentPositions == null)
                            continue;
                        Transform gun = container._gunObject.transform;
                        for (int a = 0; a < container._attachmentPositions.Count; a++)
                        {
                            Vector3 world = gun.TransformPoint(container._attachmentPositions[a]);
                            sum += world;
                            cells++;
                            float d = (world - spawn).magnitude;
                            if (d < best)
                            {
                                best = d;
                                bestContainer = container._containerIndex;
                                bestCell = a;
                                bestResidualLocal = gun.InverseTransformPoint(spawn) - container._attachmentPositions[a];
                            }
                        }
                    }
                }
                b.AppendLine("LaunchCellCount = " + cells.ToString(CellInv));
                if (cells > 0)
                {
                    b.AppendLine($"LaunchCellNearest = {bestContainer}:{bestCell}:{(best * GameUnits.MetersPerUnity).ToString("0.00", CellInv)}");
                    b.AppendLine("LaunchCellResidualLocal = " + CellV(bestResidualLocal));
                    b.AppendLine("LaunchCellsMeanDistM = " + CellM(sum / cells - spawn));
                }

                if (platform != null && tgt != null && w._ap != null)
                {
                    var picks = WeaponSelector.GetWeaponSystemsForAmmunition(
                        platform, tgt.transform.position, w._ap._ammunitionFileName, null);
                    bool match = picks != null && picks.Count > 0 && ReferenceEquals(picks[0], ws);
                    b.AppendLine("LaunchPickMatches = " + (match ? "true" : "false"));
                }
                return b.ToString();
            }
            catch (System.Exception e)
            {
                return "LaunchCellError = " + e.GetType().Name + System.Environment.NewLine;
            }
        }

        private static string CellV(Vector3 v) =>
            v.x.ToString("0.000", CellInv) + "," + v.y.ToString("0.000", CellInv) + "," + v.z.ToString("0.000", CellInv);

        private static string CellM(Vector3 d) => (d.magnitude * GameUnits.MetersPerUnity).ToString("0.00", CellInv);
    }
}
