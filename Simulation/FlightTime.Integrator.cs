using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    internal static partial class FlightTime
    {

        private const float IntegrationStepSim = 0.1f;
        private const float AltToleranceU = 0.5f;
        // Fallbacks matching the game's OWN ini defaults, so an ammo that omits the key behaves
        // here exactly as it does in the game: MaxLoftAngle 30 (AmmunitionParameters.cs:1633),
        // SeaSkimmingMaxDescentAngle 30 (:1662), FinalFlightPhaseMaxAngle 30 (:1683). In practice
        // the loader always populates these, so these fire only for an ini that sets an explicit 0.
        private const float DefaultClimbDeg = 30f;
        private const float DefaultDescentDeg = 30f;
        private const float BoostClimbDeg = 90f;
        private const float DefaultTurnRateDeg = 5f;   // MaxTurnRate default (AmmunitionParameters.cs:1732)
        private const float MinDescentOnsetDeg = 5f;
        private const float GravityKnPerMs = 9.8f * 1.94384f;
        private const float StallSpeedMultiplier = 1.1f;
        private const float CloseEnoughDistU = 3f;
        // Deliberately NOT a round number. A 15s sampler once aliased a limit cycle whose period
        // was exactly 15.0s, reporting a constant altitude while the missile was actually swinging
        // 1115 <-> 1260u -- a 10km oscillation that looked like a steady state and cost a full test
        // round. 7 is prime relative to the plausible cycle periods here.
        private const float TelemetrySampleIntervalSim = 7f;
        // The launch phase (initial flight phase + ToBearing) lasts up to ~19s and is where the
        // residual fixed offset is created, so sample it densely; 7s leaves one point inside it.
        private const float LaunchBurstWindowSim = 20f;
        private const float LaunchBurstIntervalSim = 1f;
        // Inner tier for the nose-over itself. A vertically-launched sea-skimmer reverses its whole
        // launch attitude inside ~2s, which at 1s cadence is TWO samples -- enough that the same
        // yj-18a data reads as either 19 deg/s (matching its MaxTurnRate) or 33.7 deg/s (2.2x it)
        // depending on the averaging window. 0.25s turns that into ~20 samples.
        private const float NoseOverWindowSim = 5f;
        private const float NoseOverIntervalSim = 0.25f;
        private const float VacuumDivePitchThreshold = -40f;
        private const float VelocityEpsilonKn = 0.001f;
        private const float MinSpeedKn = 1f;
        private const float LookaheadMultiplier = 20f;
        private const float MinLookaheadU = 50f;

        // ---- Isolation gates. Each fixes one measured fidelity defect; separate so a single
        // rebuild A/Bs them independently, per the project's Part B/C/E/F isolation pattern.
        //
        // B1 (2026-09-03) is DONE and no longer a gate: the kinematic branch used to clamp velocity
        // to the per-stage target speed, which the live mover never does -- `if (_ap.Kinematics != 0)
        // { _velocityInKnots += num; }` (Missile.cs:3151), with the stage target `num2` consumed only
        // by the non-kinematic branch. Ground truth: the real yj-20 `track` line reads
        // `spd 7134/6600kn`. Removing it took yj-20 from -7.7s to +1.1s (mean-actual basis) and the
        // reference set's mean |gap| from 2.50s to 1.77s, with all four non-kinematic shots provably
        // untouched (simEst identical to 0.1s). The clamp is simply gone.

        // B2 (TESTED 2026-09-03, REVERTED -- see B2' below): hold altitude proportionally instead of
        // bang-bang, triggered purely on a lookahead. The closure fix WORKED (yj-20 level-cruise
        // closure 90.6% -> 98.1%, real 100.0%) but the trigger also eased the CLIMB, starting the
        // nose-over ~200u early: yj-20's sim peakAlt collapsed 1426u -> 1190u (exactly loftAlt)
        // against a real 1430u, destroying the finite-turn-rate loft overshoot that Parts B/C exist
        // to reproduce -- worth ~10s of a 14.8s estimate drop. Mean |gap| went 1.80s -> 2.95s.
        private const bool ProportionalAltitudeHold = false;

        // B2': the same closure fix, correctly scoped. Bang-bang is RIGHT while transiting to an
        // altitude -- the real missile holds full climb to its commanded altitude and only then
        // noses over at _maxTurnRateDegrees, and the overshoot is the CONSEQUENCE of that, not a
        // bug. Proportional control is only right once the missile is already HOLDING. So: latch on
        // first arrival at the phase's target altitude, bang-bang before it, proportional after.
        // Reuses AltToleranceU as an arrival test (what a 0.5u tolerance actually suits) rather
        // than as a control deadband. No new constant.
        // ENABLED 2026-09-03 after the long-range sweep exposed what this costs: yj-20's sim sat
        // at pitch +33 deg with altitude frozen at 1156u for 60s of cruise, bleeding cos(33 deg) =
        // 0.825 of its speed into a climb that went nowhere (measured 5907kn closure at 7160kn
        // speed; the real missile manages 0.892 holding 1191u level). Gap -14.6s. The defect scales
        // with cruise duration, which is why the short-range set only showed ~1s of it.
        private const bool LatchedProportionalHold = true;

        // The game does not enter TerminalApproach on distance alone: with SearchForTargetsTime > 0
        // the seeker must also hold an echo for that long first, and the clock resets every tick
        // there is no echo (Missile.cs:584-593). So the missile keeps closing at CRUISE speed past
        // its nominal _terminalApproachDist. Grounded and exact -- predicted vs the `stage-obs`
        // measurement of the real transition: ss-n-19 678u vs 680u measured (0.3%), ss-n-12 462u vs
        // 490u. Inert by construction for the 4 of 6 reference shots that leave the field unset.
        private const bool SearchTimeTerminalOnset = true;

        // ---- Launch-phase stage fidelity ----
        // The model jumps straight from the launch rail into its cruise/loft commands, but the game
        // runs Launch -> ToBearing first, and BOTH the commanded attitude and the commanded speed
        // differ there. Each produces a fixed offset established in the first seconds and carried
        // unchanged for the rest of the flight -- which is exactly the residual signature measured
        // (rgm-109b sits +1.0km ahead from t+21 to impact at t+931, flat).

        // (a) During ToBearing the missile steers at its AIM POINT, not at the stage altitude. The
        // state exits on a 5 deg cone or a 10.0s cap (Missile.cs:343). Our model instead commands the
        // stage-altitude pitch immediately, so a sea-skimmer noses over into a dive while the real
        // round is still levelling: rgm-109b reaches 7u by t+7 where the real one reaches 16.5u.
        // FALSIFIED 2026-09-03 -- do not re-enable. The premise was that the missile steers at its
        // aim point during ToBearing. It does not: **ToBearing only decides when the stage ENDS; it
        // never overrides the altitude guidance.** hhq-9b's 1s burst, real vs sim through its
        // ToBearing (t+2..11):
        //     real alt  7.5 -> 19.7 -> 37.4 -> 60.4 -> 88.1 u   (climbing at 61 deg = its MaxLoftAngle)
        //     sim  alt  6.5 ->  7.6 ->  9.0 -> 10.8 -> 12.9 u   (pitch pinned at 2 deg, level)
        // Eight seconds of lost climb cost hhq-9b **-17.2s** and yj-20 **-13.7s**. It is wrong for
        // any lofter, and it was never needed: rgm-109b's climb to 21.7u is explained by the
        // VERTICAL LAUNCH alone (3s at 90 deg, then a 10 deg/s nose-over toward its commanded
        // sea-skim altitude keeps it climbing ~9s more). LauncherTransformLaunchAngle does the work.
        private const bool ToBearingAttitudeHold = false;
        private const float ToBearingConeDeg = 5f;      // Missile.cs:343
        private const float ToBearingMaxSeconds = 10f;  // Missile.cs:343

        // (b) Commanded speed during Launch/ToBearing is _maxVelocityInKnots -- the loft speed only
        // applies once the stage is MaintainLoftAlt (Missile.cs:3142-3145). Our model uses the loft
        // speed from t=0. Ground truth, real ss-n-19: 970/970kn through ToBearing at t+7 and t+14,
        // then 1525 at t+21 once MaintainLoftAlt begins. Inert for ammo whose loft speed equals its
        // max speed.
        private const bool LaunchStageSpeed = true;

        // (c) The launch ATTITUDE. `_fixVerticalLaunchAngle` reads 35 deg for every launcher in the
        // game -- it is the ini default, and the bool gating it also defaults true
        // (ObjectBaseLoader.cs:2688-2690) -- but 1s burst telemetry shows three of the six reference
        // missiles leave the rail VERTICAL: rgm-109b climbs 336m in 340m of path by t+3; yj-20's
        // range is frozen at 373.2km for nine seconds; yj-18a climbs at 1.6 u/s against a 1.65 u/s
        // speed. ss-n-19, off the Kirov's inclined tubes (Container20_Rotation=-45), climbs at ~45.
        // So read the direction the missile is actually pointing -- the launcher's container
        // transform -- instead of the ini field. This also covers trainable mounts for free, since
        // WeaponSystemLauncher.alignToTarget:1264 elevates the container when it can and returns
        // immediately when it cannot (a VLS cell, which therefore stays as built).
        // REVERTED: the first attempt read `_containerBaseObject`, which is only the
        // joined-containers branch of WeaponSystem.alignToTarget:1379-1381 -- it returned 0 deg on
        // every shot, so the model launched horizontally. The general case is
        // `_containers[i]._gunObject.transform`. Do NOT re-enable until the launch-rail diagnostic
        // below identifies which object actually carries the elevation, and then enable it TOGETHER
        // with ToBearingAttitudeHold: the two are coupled and neither works alone.
        // Launch elevation from the LAUNCHER ITSELF rather than `_fixVerticalLaunchAngle`, which
        // reads 35 deg for every launcher in the game (ini default, and its gating bool defaults
        // true -- ObjectBaseLoader.cs:2688-2690). Measured against 1s burst telemetry, the rail's
        // own `_containers[i]._gunObject` reproduces every reference shot exactly: 90.0 deg for
        // rgm-109b / yj-20 / yj-18a (VLS) and 45.0 deg for ss-n-19 off the Kirov's inclined tubes,
        // where the flown values derived independently from climb rate were ~90 and 43-45.
        private const bool LauncherTransformLaunchAngle = true;

        // A NON-KINEMATIC missile with SupportsBanking=True gets a SECOND rotation call every physics
        // tick: `setCourseTowardsPositionLegacy` runs its normal RotateTowards at MaxTurnRate, then
        // `performToTargetRoll` runs another at a hardcoded 60 deg/s (WeaponBase.cs:1773-1776 and
        // :1789-1792). That second call's target is
        // `Euler(localEulerAngles.x, localEulerAngles.y, num)` -- local euler read back but assigned
        // to WORLD rotation, which is gimbal-degenerate near vertical, so after a VLS launch its
        // 60 deg/s budget lands largely on PITCH rather than roll.
        //
        // Measured (0.25s burst, rgm-109b fired alongside as a control):
        //   yj-18a   SupportsBanking=True,  MaxTurnRate 15  ->  44 deg/s avg, 76 deg/s peak  (15+60=75)
        //   rgm-109b SupportsBanking=False, MaxTurnRate 10  ->  11.5 deg/s                   (obeys)
        // Modelling only MaxTurnRate made yj-18a nose over 3x too slowly and climb to 16u against a
        // real 5.3u. Reaches only non-kinematic ammo that declares SupportsBanking.
        // A launcher that cannot train fires along its own bearing, so an off-bearing shot spends
        // its initial flight phase flying the wrong way and then has to turn -- closure the model
        // never paid because `horizDir` snapped at the target from t=0. Measured on rgm-84d, same
        // build, same missile, swept by turning the ship:
        //     railAz ~90 deg -> real closes 0.10km in 5s (sim 0.90) -> gap +3.7 / +4.4
        //     railAz ~0 deg  -> real closes 0.80km in 5s (sim 0.90) -> gap +0.2 / +0.5
        // The gap tracks the closure deficit one-for-one, so this is geometry, not a per-missile
        // constant. Inert wherever the rail is already on-bearing or vertical, which is every other
        // launcher measured (ss-n-19 0.1 deg, ss-n-12 0.0 deg, all VLS vertical, trainable mounts aim).
        private const bool FixedRailLaunchHeading = true;

        private const bool BankingAddsRollBudgetToPitch = true;
        private const float BankingRollRateDeg = 60f;   // WeaponBase.cs:1792, hardcoded

        // The 90 deg boost climb applies ONLY above the air-density line, not to kinematic ammo
        // generally. Real climb angle measured from climb-rate vs speed on all three kinematic
        // lofters in the set (altitude logs to 0.1u, so this is precise):
        //     yj-20    loftAlt 1190u  ABOVE 613.5u  MaxLoftAngle 60  ->  90 deg  (dist frozen
        //                                                                165.6km through t+8)
        //     hhq-9b   loftAlt  386u  below         MaxLoftAngle 60  ->  60.7 deg
        //     rim-66b  loftAlt 13.6u  below         MaxLoftAngle 15  ->  18.3 -> 13.5 -> 7.2 deg
        // 3/3: only the missile lofting above the atmosphere exceeds its commanded angle and flies
        // vertical; both inside it fly their own MaxLoftAngle. rim-66b is the strongest case -- its
        // MaxLoftAngle of 15 is nowhere near the others' 60, so it tests the rule rather than
        // agreeing with it, and it reads -0.6/-0.8/-0.9s across a 3x range span on this branch.
        //
        // `AllowExceedingAngleLimits = (Kinematics != None)` (Missile.cs:2197) explains WHY
        // exceeding is permitted -- it does not say the command IS 90. Part B (2026-09-01) measured
        // 90 as better for both lofters, but with launchPitch stuck at 35 and a stack of since-fixed
        // defects; it was compensating. This is the second reversal of that decision, hence the
        // evidence above.

        // `launch-rail` reports pure LAUNCHER GEOMETRY -- no dependence on the missile flying, or on
        // firing at all -- so it is emitted from the planning path rather than behind emitDiag:
        // selecting a target dumps every launcher on the ship. Keyed by (unit, ammo) and re-emitted
        // only when the rail actually MOVES, so a trainable mount's slew from parked to its firing
        // elevation is captured (a few lines) while a fixed rail logs once and goes quiet.
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLogged =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> _railLoggedAz =
            new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, int> _railLogCount =
            new System.Collections.Generic.Dictionary<string, int>();
        private const float RailRelogDeltaDeg = 2f;
        private const int RailMaxLogsPerKey = 24;   // a deliberate heading sweep needs headroom
        internal static void ClearRailLog()
        { _railLogged.Clear(); _railLoggedAz.Clear(); _railLogCount.Clear(); }
        private static float InterpNodeAlt(Vector2[] nodes, float x)
        {
            if (x <= nodes[0].x) return nodes[0].y;
            int last = nodes.Length - 1;
            if (x >= nodes[last].x) return nodes[last].y;
            for (int i = 0; i < last; i++)
            {
                float x0 = nodes[i].x, x1 = nodes[i + 1].x;
                if (x >= x0 && x <= x1)
                {
                    float span = x1 - x0;
                    float f = span > 1e-4f ? (x - x0) / span : 0f;
                    return nodes[i].y + (nodes[i + 1].y - nodes[i].y) * f;
                }
            }
            return nodes[last].y;
        }

        internal struct IntegratedPhases
        {
            public bool Valid;
            public bool Lofting;
            public float LoftAltTarget;
            public float PeakAltU;
            public float ClimbTime, CruiseTime, DescentTime;
            public float VStart;
            public float VClimbExit;
            public float VCruiseExit;
            public float VTerm;
            public float FinalDistU;
            public float TermDistU;
            // Flat distance at which the model actually entered the terminal/dive phase. Unlike
            // TermDistU (the raw ini _terminalApproachDist) this is max(termDist, descentGeomDist),
            // so it reflects the altitude-dependent geometric ramp that governs high lofters.
            // -1 = the model never reached phase 2. Compare against the real TerminalApproach
            // transition reported by the `stage-obs` line.
            public float DiveStartU;
            public float DescentOnsetDeg;
        }

        internal static float IntegratedEndTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
            => IntegratedEndTimeCore(unit, ap, target, out _, emitDiag: false);

        internal static bool TryIntegratedPhaseDiag(ObjectBase unit, string ammoId, ObjectBase target,
            out float interceptTime, out IntegratedPhases phases)
        {
            interceptTime = -1f; phases = default;
            if (unit == null || target == null) return false;
            AmmunitionParameters ap = unit.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return false;
            interceptTime = IntegratedEndTimeCore(unit, ap, target, out phases, emitDiag: true);
            return phases.Valid;
        }

        private static float IntegratedEndTimeCore(ObjectBase unit, AmmunitionParameters ap,
            ObjectBase target, out IntegratedPhases phases, bool emitDiag)
        {
            phases = default;
            try
            {
                EnsureSimLookup();
                if (!_simIsBeta || _thrustMethod == null) return -1f;
                bool nonKin = ap.Kinematics == AmmunitionParameters.KinematicsLevel.None;
                if (!nonKin && _dragMethod == null) return -1f;

                const float KU = GameUnits.KnotsToUnityPerSecond;
                const float dt = IntegrationStepSim;
                const float ZeroDensityAltU = 1f / 0.00163f;
                Vector3 launchPos = unit.transform.position;
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool isAir = unit.IsAirUnit;

                float tvMag = targetVel.magnitude;
                if (tvMag > 0f && ap.AssumeEvasiveTarget(target))
                {
                    Vector3 flee = targetPos - launchPos; flee.y = 0f;
                    if (flee.sqrMagnitude > 1e-8f)
                    {
                        targetVel += flee.normalized * (tvMag * EvasiveBoostFraction);
                        targetVel = targetVel.normalized * Mathf.Min(targetVel.magnitude, tvMag);
                    }
                }

                float dragFactor = ap.GetDragFactor(isAir);
                float startVelKnots = Mathf.Max(unit._velocityInKnots, 0f);
                float maxFlight = ap._maxFlightTime > 0f ? ap._maxFlightTime : MaxFlightTimeFallback;
                float targetAlt0 = Mathf.Max(targetPos.y, 0f);

                float loftAlt = -1f;
                if (_loftCapMethod != null)
                {
                    float cap = (float)_loftCapMethod.Invoke(null,
                        new object[] { ap, Mathf.Max(launchPos.y, 0f), targetAlt0 });
                    float floor = Mathf.Max(Mathf.Max(launchPos.y, 0f), targetAlt0);
                    if (cap > floor + AltToleranceU)
                        loftAlt = cap;
                }
                bool lofting = loftAlt > Mathf.Max(launchPos.y, 0f) + AltToleranceU;

                bool isTerminalLoft = ap._terminalLoft;
                bool isHighBallisticLofter = !nonKin && lofting && loftAlt > ZeroDensityAltU;

                float climbDeg = ap._maxLoftAngle > AltToleranceU ? ap._maxLoftAngle : DefaultClimbDeg;
                float boostClimbDeg = isHighBallisticLofter ? BoostClimbDeg : climbDeg;
                float turnRate = ap._maxTurnRateDegrees > VelocityEpsilonKn ? ap._maxTurnRateDegrees : DefaultTurnRateDeg;
#pragma warning disable 162
                // See BankingAddsRollBudgetToPitch above: the roll call is a second, independent
                // per-tick budget, so the two can sum onto pitch.
                if (BankingAddsRollBudgetToPitch && nonKin && ap._supportsBanking)
                    turnRate += BankingRollRateDeg;
#pragma warning restore 162

                float launchPitch = -1f;
                float launchPitchIni = -1f;
                // Hoisted out of the try below: the step loop and the sim-launch line both need them.
                Transform rail = null;
                bool fixedRail = false;
                string railAzTxt = "n/a";
                try
                {
                    var launchers = unit.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
                    if (launchers != null && launchers.Count > 0)
                    {
                        // A ship carries several launchers for one ammo, and for FIXED mounts they
                        // can point in completely different directions -- every Harpoon ship has two
                        // MK141s, Port and Starboard, 180 deg apart. `launchers[0]` is arbitrary, so
                        // taking it would read the wrong bearing half the time and apply a bogus
                        // ~180 deg turn. The round comes off whichever launcher bears, so pick the
                        // one whose horizontal rail direction is closest to the target.
                        int pick = 0;
                        if (launchers.Count > 1)
                        {
                            Vector3 toTgtH = targetPos - launchPos; toTgtH.y = 0f;
                            float best = float.MaxValue;
                            for (int li = 0; li < launchers.Count; li++)
                            {
                                var lw = launchers[li]?._vwp;
                                Transform lt = null;
                                var lc = launchers[li]?._containers;
                                if (lc != null && lc.Count > 0 && lc[0]?._gunObject != null)
                                    lt = lc[0]._gunObject.transform;
                                else if (lw != null && lw._containerBaseObject != null)
                                    lt = lw._containerBaseObject.transform;
                                if (lt == null || toTgtH.sqrMagnitude < 1e-6f) continue;
                                Vector3 lf = lt.forward; lf.y = 0f;
                                if (lf.sqrMagnitude < 1e-4f) continue;   // vertical: no bearing
                                float off = Vector3.Angle(lf, toTgtH);
                                if (off < best) { best = off; pick = li; }
                            }
                        }
                        var vwp = launchers[pick]?._vwp;
                        if (vwp != null && vwp._fixVerticalLaunchAngleForLauncher)
                            launchPitchIni = vwp._fixVerticalLaunchAngle + vwp._additionalFixVerticalLaunchAngle;
                        launchPitch = launchPitchIni;

                        var ws = launchers[pick];
                        // The object the game actually elevates: gunObject per container, or the
                        // shared container base when they are joined (WeaponSystem.alignToTarget
                        // :1379-1381). gunObj is populated either way, so prefer it.
                        Transform railGun = (ws._containers != null && ws._containers.Count > 0
                                             && ws._containers[0]?._gunObject != null)
                            ? ws._containers[0]._gunObject.transform : null;
                        Transform railBase = (vwp != null && vwp._containerBaseObject != null)
                            ? vwp._containerBaseObject.transform : null;
                        Transform railMount = (vwp != null && vwp._mountObject != null)
                            ? vwp._mountObject.transform : null;
                        rail = railGun ?? railBase;

                        float railDeg = rail != null
                            ? Mathf.Asin(Mathf.Clamp(rail.forward.y, -1f, 1f)) * Mathf.Rad2Deg
                            : float.NaN;

                        // A launcher that cannot move fires along the rail as built. One that can
                        // move aims first, so its CURRENT transform is wherever it is parked and
                        // must never be read as a launch attitude -- compute the game's own aim
                        // instead (alignToTarget:1360-1377; RotateWeaponToAngle:1644 confirms the
                        // angle passed there becomes the gun's local elevation).
                        fixedRail = vwp != null
                                 && !vwp._isMountRotatable && !vwp._areContainersRotatable;
                        float predictedPitch = -1f;
                        if (vwp != null)
                        {
                            if (fixedRail && !float.IsNaN(railDeg))
                            {
                                predictedPitch = Mathf.Clamp(railDeg, 0f, 90f);
                            }
                            else
                            {
                                // Trainable: elevation to the target unless the launcher uses a
                                // fixed angle, then the mount's own pitch removed and the result
                                // clamped to the elevation arc. This is the branch that governs a
                                // Standard missile fired at a surface ship.
                                Vector3 toTgt = targetPos - launchPos;
                                float tgtElev = toTgt.sqrMagnitude > 1e-6f
                                    ? Mathf.Asin(Mathf.Clamp(toTgt.normalized.y, -1f, 1f)) * Mathf.Rad2Deg
                                    : 0f;
                                float mountPitch = railMount != null
                                    ? Mathf.Asin(Mathf.Clamp(railMount.forward.y, -1f, 1f)) * Mathf.Rad2Deg
                                    : 0f;
                                float bas = vwp._fixVerticalLaunchAngleForLauncher
                                    ? vwp._fixVerticalLaunchAngle : tgtElev;
                                float e = bas + vwp._additionalFixVerticalLaunchAngle - mountPitch;
                                if (vwp._elevationArc.y > vwp._elevationArc.x)
                                    e = Mathf.Clamp(e, vwp._elevationArc.x, vwp._elevationArc.y);
                                predictedPitch = Mathf.Clamp(e, 0f, 90f);
                            }
                        }

                        // Horizontal analogue of railDeg: the angle between the rail's own bearing
                        // and the bearing to the target. A FIXED box launcher (neither mount nor
                        // containers rotatable) cannot train, so the missile leaves along the box's
                        // bearing and has to turn -- which `bearingErr` cannot see, because that
                        // measures the SHIP's heading. Meaningless when the rail is near-vertical
                        // (its horizontal component vanishes), which is every VLS shot.
                        float railAzDeg = float.NaN;
                        if (rail != null)
                        {
                            Vector3 rf = rail.forward; rf.y = 0f;
                            Vector3 tt = targetPos - launchPos; tt.y = 0f;
                            if (rf.sqrMagnitude < 1e-4f) railAzTxt = "vertical";
                            else if (tt.sqrMagnitude > 1e-6f)
                            {
                                railAzDeg = Vector3.Angle(rf, tt);
                                railAzTxt = railAzDeg.ToString("0.0") + "°";
                            }
                        }

                        // Re-emitted whenever the rail moves, so a trainable mount's slew from
                        // parked to firing elevation shows up next to `predicted` and the formula
                        // above is measured rather than assumed.
                        string railKey = unit.GetInstanceID() + "/" + (ap._ammunitionFileName ?? "?");
                        if (Coordinator.VerboseLog && vwp != null)
                        {
                            try
                            {
                                _railLogCount.TryGetValue(railKey, out int n);
                                bool firstSeen = !_railLogged.TryGetValue(railKey, out float prev);
                                bool elevMoved = !float.IsNaN(railDeg)
                                              && Mathf.Abs(railDeg - prev) > RailRelogDeltaDeg;
                                // A fixed box launcher never changes ELEVATION, so without this the
                                // line would never re-emit as the ship turns -- and the railAz sweep
                                // that distinguishes a real off-bearing mechanism from a fit to one
                                // launcher would be uninstrumentable.
                                bool azMoved = !float.IsNaN(railAzDeg)
                                            && (!_railLoggedAz.TryGetValue(railKey, out float prevAz)
                                                || Mathf.Abs(railAzDeg - prevAz) > RailRelogDeltaDeg);
                                if ((firstSeen || elevMoved || azMoved) && n < RailMaxLogsPerKey)
                                {
                                    _railLogged[railKey] = railDeg;
                                    if (!float.IsNaN(railAzDeg)) _railLoggedAz[railKey] = railAzDeg;
                                    _railLogCount[railKey] = n + 1;
                                    string E(Transform tr) => tr == null ? "n/a"
                                        : (Mathf.Asin(Mathf.Clamp(tr.forward.y, -1f, 1f)) * Mathf.Rad2Deg)
                                            .ToString("0.0") + "°";
                                    Bootstrap.Log.LogInfo(
                                        $"[AutoTOT] launch-rail {ap._ammunitionFileName}: " +
                                        $"gunObj {E(railGun)}, containerBase {E(railBase)}, mount {E(railMount)}, " +
                                        $"fixedRail {fixedRail}, predicted {predictedPitch:0.0}°, " +
                                    $"railAz {railAzTxt}, " +
                                        $"containersRotatable {vwp._areContainersRotatable}, " +
                                        $"joined {vwp._areContainersJoinedTogether}, " +
                                        $"mountRotatable {vwp._isMountRotatable}, " +
                                        $"elevArc {vwp._elevationArc.x:0.0}/{vwp._elevationArc.y:0.0}, " +
                                        $"iniPitch {launchPitchIni:0.0}°");
                                }
                            }
                            catch { }
                        }

#pragma warning disable 162
                        if (LauncherTransformLaunchAngle && predictedPitch >= 0f)
                            launchPitch = predictedPitch;
#pragma warning restore 162
                    }
                }
                catch { launchPitch = launchPitchIni; }
                float initialPhaseDur = Mathf.Max(ap._initialFlightPhaseDuration, 0f);

                // Diagnostic only: how far off-bearing the shot is at launch, as a horizontal angle
                // between the shooter's heading and the bearing to the target. The game holds a
                // missile in ToBearing until it is within a 5 deg cone of its aim point OR 10.0s
                // elapse (Missile.cs:343), and the integrator does not model that turn at all --
                // it steers straight at the target from t=0, which is why every reference shot runs
                // ahead of the real missile through the first 15s.
                //
                // This is a CORRELATE, not the game's own quantity: the game measures against the
                // MISSILE's forward vector (which carries launch pitch too), not the ship's heading
                // in the horizontal plane. It is here so each shot self-records its launch geometry
                // and the correlation against the ToBearing duration on the `stage-obs` line
                // accumulates during ordinary play. -1 = unavailable.
                float launchBearingErrDeg = -1f;
                try
                {
                    Vector3 shipFwd = unit.transform.forward; shipFwd.y = 0f;
                    Vector3 toTarget = targetPos - launchPos; toTarget.y = 0f;
                    if (shipFwd.sqrMagnitude > 1e-6f && toTarget.sqrMagnitude > 1e-6f)
                        launchBearingErrDeg = Vector3.Angle(shipFwd, toTarget);
                }
                catch { launchBearingErrDeg = -1f; }

                float maxVelKn = Mathf.Max(ap._maxVelocityInKnots, 1f);
                float loftVelKn = ap._maxLoftVelocityInKnots > 0f ? ap._maxLoftVelocityInKnots : maxVelKn;
                float termVelKn = ap._terminalVelocityInKnots > 0f ? ap._terminalVelocityInKnots : maxVelKn;
                float decelPerStep = ap._deceleration * GravityKnPerMs * IntegrationStepSim;

                bool toSkim = ap._loftToSkim && ap._seaSkimmingStartDistToTargetUnity > 0f;
                float finalDist = toSkim
                    ? ap._seaSkimmingStartDistToTargetUnity
                    : (ap._finalFlightPhaseDistToTargetUnity > 0f
                        ? ap._finalFlightPhaseDistToTargetUnity
                        : Mathf.Max(ap._seaSkimmingStartDistToTargetUnity, ap._finalFlightPhaseDistToTargetUnity));
                float finalAlt = toSkim
                    ? Mathf.Max(ap._seaSkimmingAltUnity, 0f)
                    : (ap._finalFlightPhaseAltUnity > 0f ? ap._finalFlightPhaseAltUnity
                       : (ap._seaSkimmingAltUnity > 0f ? ap._seaSkimmingAltUnity : 0f));
                float termDist = ap._terminalApproachDist;
#pragma warning disable 162
                if (SearchTimeTerminalOnset && ap._searchForTargetsTime > 0f)
                {
                    // Distance the missile still covers at cruise while the seeker searches.
                    termDist = Mathf.Max(termDist - maxVelKn * KU * ap._searchForTargetsTime, 0f);
                }
#pragma warning restore 162
                float termAlt = ap._terminalAltUnity > 0f ? ap._terminalAltUnity : finalAlt;
                float descentDeg = ap._finalFlightPhaseMaxAngle > 0.01f ? ap._finalFlightPhaseMaxAngle
                                 : (ap._seaSkimmingMaxDescentAngle > 0.01f ? ap._seaSkimmingMaxDescentAngle : DefaultDescentDeg);
                float descentOnsetDeg = Mathf.Max(descentDeg,
                    Mathf.Max(ap._finalFlightPhaseMaxAngle, ap._seaSkimmingMaxDescentAngle));

                phases.Valid = true;
                phases.Lofting = lofting;
                phases.LoftAltTarget = lofting ? loftAlt : 0f;
                phases.VStart = startVelKnots;
                phases.PeakAltU = launchPos.y;
                phases.FinalDistU = finalDist;
                phases.TermDistU = termDist;
                phases.DiveStartU = -1f;
                phases.DescentOnsetDeg = descentOnsetDeg;

                Vector3 pos = launchPos;
                float velKnots = startVelKnots;
                float t = 0f;
                float prevPitch = launchPitch >= 0f ? launchPitch : 0f;
                // Launch heading for a fixed rail: the horizontal analogue of prevPitch. NaN when
                // there is nothing to model (trainable mount, vertical rail, or no rail resolved),
                // in which case horizDir keeps aiming straight at the target as before.
                Vector3 launchHeading = Vector3.zero;
                if (FixedRailLaunchHeading && fixedRail && rail != null)
                {
                    Vector3 rfwd = rail.forward; rfwd.y = 0f;
                    if (rfwd.sqrMagnitude > 1e-4f) launchHeading = rfwd.normalized;
                }
                float prevFlat = float.MaxValue;
                bool tlGliding = false;
                // B2' arrival latch: has the missile reached the current phase's target altitude
                // yet? Before arrival it is transiting (bang-bang, so the finite-turn-rate nose-over
                // still overshoots); after, it is holding (proportional, so it cannot limit-cycle).
                bool altLatched = false;
                int altLatchPhase = -1;
                float prevAltErr = float.NaN;

                Vector2[] altNodes = null;
                float flatDistTotal = new Vector2(targetPos.x - launchPos.x, targetPos.z - launchPos.z).magnitude;
                if (isTerminalLoft && _altNodesMethod != null && flatDistTotal > 1f)
                {
                    try
                    {
                        object[] an = { ap, Mathf.Max(launchPos.y, 0f), targetAlt0, flatDistTotal, -1f, 0f };
                        var lst = _altNodesMethod.Invoke(null, an)
                            as System.Collections.Generic.List<Vector2>;
                        if (lst != null && lst.Count >= 2) altNodes = lst.ToArray();
                    }
                    catch { altNodes = null; }
                }
                object[] thrustArgs = new object[4];
                object[] dragArgs = new object[10];
                float nextSample = NoseOverIntervalSim;
                bool trackDiag = Coordinator.VerboseLog && emitDiag;
                string ammoLabel = ap._ammunitionFileName ?? "?";
                if (trackDiag)
                    Bootstrap.Log.LogInfo($"[AutoTOT] sim-launch {ammoLabel}: launchPitch " +
                        $"{(launchPitch >= 0f ? launchPitch.ToString("0.0") + "°" : "n/a (heading)")}" +
                        $", initPhase {initialPhaseDur:0.0}s, turnRate {turnRate:0.0}/s, loftAlt {loftAlt:0}u" +
                        $", descentDeg {descentDeg:0.0}°, onsetDeg {descentOnsetDeg:0.0}°" +
                        $", bearingErr {(launchBearingErrDeg >= 0f ? launchBearingErrDeg.ToString("0.0") + "°" : "n/a")}" +
                        // Launch range, so each shot self-records its geometry. Two ranges per shot
                        // cannot falsify a fixed-offset-plus-drift decomposition (any two points fit
                        // exactly); three can, and this makes that fit mechanical.
                        $", range {flatDistTotal:0}u ({flatDistTotal * GameUnits.MetersPerUnity / 1000f:0.0}km)" +
                        // Both values, so the ini default and the rail's real orientation can be
                        // compared directly on one line.
                        $", iniPitch {(launchPitchIni >= 0f ? launchPitchIni.ToString("0.0") + "°" : "n/a")}" +
                        // Sampled here, on the fired-shot path, because the planning-path launch-rail
                        // reading can be stale: the ship keeps turning between planning and launch.
                        // One shot logged railAz 177.6 at planning yet flew on-bearing.
                        $", railAz {railAzTxt}");

                while (t < maxFlight)
                {
                    Vector3 predTgt = targetPos + targetVel * t;
                    float dx = predTgt.x - pos.x, dz = predTgt.z - pos.z;
                    float flatDist = Mathf.Sqrt(dx * dx + dz * dz);

                    if ((flatDist > prevFlat && t > dt) || flatDist < CloseEnoughDistU)
                    {
                        if (velKnots < ap.MinVelocity * StallSpeedMultiplier) return -1f;
                        return t;
                    }
                    prevFlat = flatDist;

                    Vector3 horizDir = flatDist > 1e-4f
                        ? new Vector3(dx / flatDist, 0f, dz / flatDist) : Vector3.forward;

                    // Fixed-rail launch heading (see FixedRailLaunchHeading): fly the rail's bearing
                    // for the initial flight phase, then turn toward the target at MaxTurnRate --
                    // the horizontal mirror of the launchPitch hold further down. Approximation
                    // noted: the game rate-limits COMBINED pitch+yaw in one Quaternion.RotateTowards
                    // (WeaponBase.cs:1770), while this limits heading and pitch independently. For an
                    // abeam launch the turn is overwhelmingly yaw, so the error is small.
                    if (launchHeading.sqrMagnitude > 0.5f)
                    {
                        if (t >= initialPhaseDur)
                            launchHeading = Vector3.RotateTowards(
                                launchHeading, horizDir, turnRate * Mathf.Deg2Rad * dt, 0f).normalized;
                        horizDir = launchHeading;
                    }

                    float pitchDeg = 0f;
                    int phase = 1;
                    float stageTgt = maxVelKn;
                    {
                        float stageAlt;
                        float descentGeomDist = (pos.y - termAlt)
                                              / Mathf.Tan(Mathf.Max(descentOnsetDeg, MinDescentOnsetDeg) * Mathf.Deg2Rad);
                        float diveStart = Mathf.Max(termDist, descentGeomDist);
                        if (diveStart > 0f && flatDist <= diveStart)
                        { stageTgt = termVelKn; stageAlt = termAlt; phase = 2; }
                        else if (finalDist > 0f && flatDist <= finalDist)
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }
                        else if (lofting)
                        { stageTgt = loftVelKn; stageAlt = loftAlt; phase = 0; }
                        else
                        { stageTgt = maxVelKn; stageAlt = finalAlt; phase = 1; }

                        float altErr = stageAlt - pos.y;
                        float targetPitch = 0f;
                        float diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg;

                        // The game's ToBearing test: angle between the missile's attitude and the
                        // line to its aim point, against a 5 deg cone, with a 10.0s cap
                        // (Missile.cs:343). The horizontal component is already aligned on every
                        // reference shot (bearingErr <= 0.6 deg), so this reduces to elevation.
                        float elevToTgtDeg = Mathf.Atan2(predTgt.y - pos.y,
                                                         Mathf.Max(flatDist, 1e-4f)) * Mathf.Rad2Deg;
                        bool inToBearing = launchPitch >= 0f
                                        && t >= initialPhaseDur
                                        && t < initialPhaseDur + ToBearingMaxSeconds
                                        && Mathf.Abs(prevPitch - elevToTgtDeg) >= ToBearingConeDeg;
#pragma warning disable 162
                        // Launch and ToBearing both command _maxVelocityInKnots (Missile.cs:3142).
                        if (LaunchStageSpeed && (t < initialPhaseDur || inToBearing))
                            stageTgt = maxVelKn;
#pragma warning restore 162

                        // CS0162: the isolation gates are compile-time consts, so one arm below is
                        // deliberately unreachable in any given build. That is the point.
#pragma warning disable 162
                        bool holdingAlt = false;
                        if (LatchedProportionalHold)
                        {
                            if (phase != altLatchPhase)
                            { altLatchPhase = phase; altLatched = false; prevAltErr = float.NaN; }
                            // Arrival = the altitude error CHANGES SIGN, i.e. the missile has crossed
                            // the stage altitude. A crossing cannot be stepped over; a position band
                            // can, and was -- at cruise this loop moves 3-5.5u of altitude per 0.1s
                            // step against the 1u-wide AltToleranceU band, so the earlier
                            // |altErr| <= tol test never once fired.
                            //
                            // Latching on the FIRST crossing does not flatten the loft overshoot:
                            // the overshoot comes from the turn-rate limit, not from the bang-bang
                            // command. At the crossing the missile is near +90 deg pitch and
                            // MoveTowards needs ~7.5s to swing it down, climbing the whole way.
                            if (!altLatched && !float.IsNaN(prevAltErr) &&
                                ((prevAltErr > 0f) != (altErr > 0f)))
                                altLatched = true;
                            prevAltErr = altErr;
                            holdingAlt = altLatched;
                        }

                        if (ProportionalAltitudeHold || holdingAlt)
                        {
                            // Pitch that actually reaches stageAlt over a lookahead, clamped to the
                            // ammo's own climb/dive limits. Same lookahead the TerminalLoft node
                            // glide below uses.
                            float holdLook = Mathf.Max(velKnots * KU * dt * LookaheadMultiplier, MinLookaheadU);
                            targetPitch = Mathf.Clamp(Mathf.Atan2(altErr, holdLook) * Mathf.Rad2Deg,
                                                      -diveDeg, boostClimbDeg);
                        }
                        else if (altErr > AltToleranceU) targetPitch = boostClimbDeg;
                        else if (altErr < -AltToleranceU) targetPitch = -diveDeg;
#pragma warning restore 162

                        if (isTerminalLoft && lofting)
                        {
                            if (altNodes != null)
                            {
                                float xNow = flatDistTotal - flatDist;
                                float look = Mathf.Max(velKnots * KU * dt * LookaheadMultiplier, MinLookaheadU);
                                float altAhead = InterpNodeAlt(altNodes, Mathf.Min(xNow + look, flatDistTotal));
                                float slopeDeg = Mathf.Atan2(pos.y - altAhead, look) * Mathf.Rad2Deg;
                                targetPitch = -Mathf.Clamp(slopeDeg, -boostClimbDeg, descentDeg);
                            }
                            else
                            {
                                if (!tlGliding && pos.y >= loftAlt - AltToleranceU) tlGliding = true;
                                if (tlGliding)
                                {
                                    float glideDeg = Mathf.Atan2(Mathf.Max(pos.y - targetAlt0, 0f),
                                        Mathf.Max(flatDist, 1f)) * Mathf.Rad2Deg;
                                    targetPitch = -Mathf.Min(glideDeg, descentDeg);
                                }
                            }
                        }

                        if (launchPitch >= 0f && t < initialPhaseDur)
                            targetPitch = launchPitch;
#pragma warning disable 162
                        else if (ToBearingAttitudeHold && launchPitch >= 0f && inToBearing)
                        {
                            // Steer at the aim point, which is what the live mover does in this
                            // state -- NOT a rigid hold of the launch angle, and not the stage
                            // altitude. Self-terminating: once the turn-rate limit brings the
                            // attitude inside the 5 deg cone, inToBearing goes false on its own.
                            targetPitch = Mathf.Clamp(elevToTgtDeg, -diveDeg, boostClimbDeg);
                        }
#pragma warning restore 162

                        pitchDeg = Mathf.MoveTowards(prevPitch, targetPitch, turnRate * dt);
                    }
                    float pitchRate = (pitchDeg - prevPitch) / dt;

                    thrustArgs[0] = ap; thrustArgs[1] = isAir; thrustArgs[2] = t; thrustArgs[3] = dt;
                    float thrust = (float)_thrustMethod.Invoke(null, thrustArgs);
                    bool motorBurning = thrust > 0f;

                    float dragThisStep = 0f;
                    if (nonKin)
                    {
                        if (velKnots > stageTgt)
                            velKnots -= Mathf.Min(decelPerStep, velKnots - stageTgt);
                        else if (velKnots < stageTgt - VelocityEpsilonKn)
                            velKnots += Mathf.Min(thrust, stageTgt - velKnots);
                    }
                    else
                    {
                        velKnots += thrust;
                        dragArgs[0] = pos.y; dragArgs[1] = velKnots * KU; dragArgs[2] = dt; dragArgs[3] = -pitchDeg;
                        dragArgs[4] = dragFactor; dragArgs[5] = motorBurning;
                        bool inVacuumDive = phase == 2 && pos.y > ZeroDensityAltU && pitchDeg < VacuumDivePitchThreshold;
                        dragArgs[6] = inVacuumDive ? pos.y : predTgt.y;
                        dragArgs[7] = ap.LiftFactor; dragArgs[8] = ap.MinVelocity; dragArgs[9] = -pitchRate;
                        dragThisStep = (float)_dragMethod.Invoke(null, dragArgs);
                        velKnots -= dragThisStep;
                        // No stage-speed clamp here: kinematic ammo is thrust minus drag, uncapped
                        // (see the B1 note at the top of this file).
                    }

                    if (velKnots < MinSpeedKn) return -1f;

                    float pr = pitchDeg * Mathf.Deg2Rad;
                    Vector3 dir = horizDir * Mathf.Cos(pr) + Vector3.up * Mathf.Sin(pr);
                    pos += velKnots * KU * dt * dir;

                    if (phase == 0) { phases.ClimbTime += dt; phases.VClimbExit = velKnots; }
                    else if (phase == 1) { phases.CruiseTime += dt; phases.VCruiseExit = velKnots; }
                    else
                    {
                        phases.DescentTime += dt;
                        // First step in the dive: stamp where the model committed to descend, so the
                        // `stage-model` line can be read straight against the real TerminalApproach
                        // distance from `stage-obs`.
                        if (phases.DiveStartU < 0f) phases.DiveStartU = flatDist;
                    }
                    phases.VTerm = velKnots;
                    if (pos.y > phases.PeakAltU) phases.PeakAltU = pos.y;

                    if (trackDiag && t + dt >= nextSample)
                    {
                        float slantKm = (predTgt - pos).magnitude * GameUnits.MetersPerUnity / 1000f;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] sim-track {ammoLabel}: t+{t:0.0}s spd {velKnots:0}kn alt {pos.y:0.0} " +
                            $"pitch {pitchDeg:0} drag {dragThisStep / dt:0}kn/s phase {phase} " +
                            $"dist {flatDist * GameUnits.MetersPerUnity / 1000f:0.0}km slant {slantKm:0.0}km");
                        nextSample += (t < NoseOverWindowSim) ? NoseOverIntervalSim
                                    : (t < LaunchBurstWindowSim) ? LaunchBurstIntervalSim
                                    : TelemetrySampleIntervalSim;
                    }

                    prevPitch = pitchDeg;
                    t += dt;
                }
                return -1f;
            }
            catch (Exception e)
            {
                if (Coordinator.VerboseLog)
                    Bootstrap.Log.LogWarning($"[AutoTOT] integrated flight-time failed: {e.GetType().Name}: {e.Message}");
                return -1f;
            }
        }
    }
}
