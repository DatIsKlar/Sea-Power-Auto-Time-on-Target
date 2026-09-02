# Flight-Time Model

AutoTOT coordinates salvos by holding launches until the right moment, and the
right moment is computed from a flight-time estimate for each shooter, ammo, and
target. Anchor selection, release timing, and the planner's ETA readouts all run
on this number.

The estimator is a tiered chain. A custom grounded step integrator is primary on
the beta branch. A port of the game's own waypoint shot simulator is the middle
fallback. The game's built-in kinematic estimator is the last resort, and on the
public branch it is the primary path. If every simulator declines, straight-line
max speed keeps coordination functional. One DLL serves both game branches; the
chain configures itself at load time.

## Design principle

The model uses no fitted per-missile constants. Every quantity it consumes is
one of three things:

- a game constant, such as the knots-to-Unity-units conversion
  `KU = 0.0076554087`, 67.200066 m per Unity unit, or the zero-density altitude
  of ~613.5 u derived from the game's air-density model,
- a per-ammo INI parameter read off `AmmunitionParameters`
  (`_maxVelocityInKnots`, `_maxLoftAngle`, `_terminalApproachDist`,
  `_terminalLoft`, and the rest), or
- a value returned by the game's own physics methods through reflection
  (`CalculateThrustOverTime`, `CalculateDrag`, `LoftCap`, `BuildAltitudeNodes`).

A mechanism that only fits one missile gets rejected. Each one has to generalize
to the physical class it belongs to, which is why the estimator handles modded
missiles it has never seen. The single deliberate heuristic is the −40° terminal
lock-drop proxy in §4.5. It describes a physical class, steeply diving
post-burnout flight, not a specific missile.

## The estimation chain

`FlightTime.Estimate(unit, ammoId, target)` is the single entry point. It
resolves the ammo's `AmmunitionParameters` and calls `KinematicRaw`, which tries
each tier in order. The first tier returning a value above `MinValidSeconds`
(0.01 s) wins.

```
Estimate
 ├─ KinematicRaw
 │   ├─ Tier 1: IntegratedEndTime        custom grounded integrator   (beta only)
 │   ├─ Tier 2: WaypointSim.EndTime      ported public shot sim       (beta only)
 │   └─ Tier 3: MaxRangePreciseEndTime   game's built-in estimator    (both branches)
 └─ straight-line max speed              absolute last resort          (both branches)
```

A tier returns −1 when it cannot produce a trustworthy number: a missing
reflection handle, an out-of-range flight, or a stalled missile. The chain then
asks the next tier, so a degraded tier hands the shot down instead of corrupting
it.

### Branch behavior

The game's shot-simulation API differs between Sea Power branches. The chain
detects which one is loaded at startup in `FlightTime.EnsureSimLookup`: it looks
for `Missile.SimulateShotLinear` (the public signature), and if that method is
absent it resolves `MissileSimulator.EstimateShot`, marks the session as beta,
and resolves the integrator's and waypoint sim's reflection surfaces.

| Tier | Public branch | Beta branch |
|---|---|---|
| 1, grounded integrator | gated off (`_simIsBeta` false, declines) | primary |
| 2, ported waypoint sim | not wired (lookup never runs) | middle fallback |
| 3, game estimator | primary: `MaxRangePrecise` single pass drives the game's own `SimulateShotLinear` | last resort (`EstimateShot`-based result) |
| 4, straight line | last resort | last resort |

On the public branch the game's own simulator is accurate, so the mod defers to
it. On the beta branch the built-in `EstimateShot` measured ~30 s off on lofting
missiles (its Chebyshev speed fit underestimates loft arcs), which is why the
custom integrator exists.

### Return contract and caching

- `0` means unknown, never arrives-instantly. Callers treat any value at or
  below `MinValidSeconds` as unavailable.
- Results are cached per `(UnitId, AmmoFile, TargetId)` for 0.5 s of real time
  (capacity 512, expired-first eviction). The per-frame release gate would
  otherwise re-run a full simulation every frame. Declined and negative results
  are cached too, so a dead tier is not retried inside the window.
- The straight-line fallback lives in a separate cache so a cached fallback can
  never be misreported as a kinematic result in verbose logs.
- All caches clear on mission reset (`Coordinator.Reset`).

## Tier 1: grounded step integrator

Code: `Simulation/FlightTime.Integrator.cs`. A forward-Euler step integrator
that calls the game's own physics helpers and models only the trajectory shape
itself. The shape, specifically the loft arc, is where the built-in estimator's
error lives.

The tier declines immediately unless the session is beta and the thrust handle
(and, for kinematic ammo, the drag handle) resolved.

### Integration loop

Fixed step `dt = 0.1 s`, running from launch to `_maxFlightTime` (600 s if the
ammo declares none). State is position in Unity units, speed in knots, and
pitch. Per step:

1. Predict the target: `predTgt = targetPos + targetVel·t`. If the game treats
   the target as evasive, the target velocity gains up to 0.8× its own speed
   along the flee axis, mirroring the built-in sim's convention.
2. Test for intercept. Intercept is closest approach: the first step where the
   horizontal distance to the predicted target stops shrinking, or distance
   below 3 u. If the missile is slower than `1.1 × MinVelocity` at that moment
   the shot is rejected as a stall (−1, next tier).
3. Pick the stage, altitude, and commanded pitch (§4.3).
4. Read thrust from `CalculateThrustOverTime(ap, isAir, t, dt)`. The motor is
   burning while thrust > 0.
5. Update speed, hybrid by ammo kind (§4.4).
6. Move along `horizDir·cos(pitch) + up·sin(pitch)`. Positive pitch is up.

### Reflection surface

Resolved once, cached, and each miss degrades to a declined shot.

| Handle | Game member | Role |
|---|---|---|
| `_thrustMethod` | `MissileSimulator.CalculateThrustOverTime` | per-step thrust increment; 0 after burnout |
| `_dragMethod` | `MissileSimulator.CalculateDrag` (10-arg) | per-step drag, gravity, and induced lift for kinematic ammo |
| `_loftCapMethod` | `MissileSimulator.LoftCap` | the flown loft altitude |
| `_altNodesMethod` | `MissileSimulator.BuildAltitudeNodes` (private, `out` param) | authoritative altitude-vs-distance curve for TerminalLoft ammo (§4.6) |

Handles resolve by exact signature first, then by name plus parameter count as a
drift fallback. `BuildAltitudeNodes` needs the exact match because its
`out float` parameter is invisible to by-name scans. Resolution state is logged
once by the `sim-init` line under `VerboseLogging`.

### Stage model

Everything keys off remaining horizontal distance to the predicted target,
mirroring the game's own staging:

- Phase 0, loft/climb: climb toward `loftAlt` from `LoftCap` at
  `_maxLoftVelocityInKnots`. `LoftCap` is the flown loft altitude. The
  range-optimizer alternative `SearchOptimalLoftAltitude` returns ~1 u for
  exotic lofters and is deliberately not used.
- Phase 1, final or sea-skim cruise: at `_finalFlightPhaseAltUnity` or
  `_seaSkimmingAltUnity`, speed `_maxVelocityInKnots`. The loft ends at
  `_seaSkimmingStartDistToTargetUnity` when `_loftToSkim` is set, otherwise at
  `_finalFlightPhaseDistToTargetUnity`.
- Phase 2, terminal dive: at `_terminalAltUnity`, speed
  `_terminalVelocityInKnots`. It begins at the later of
  `_terminalApproachDist` and the geometric onset distance
  `(altitude − termAlt) / tan(onsetAngle)`, so a high-apex missile starts
  descending gradually instead of plunging.

Three shape mechanisms carry most of the accuracy:

Launcher elevation and finite turn rate. The launcher fires at a fixed vertical
angle (`_fixVerticalLaunchAngle`, ~90° for VLS), not toward the target. The
missile holds that elevation for `_initialFlightPhaseDuration`, then pitch slews
toward the commanded value at `_maxTurnRateDegrees` per second. The finite
nose-over is what makes a high lofter overshoot its declared loft altitude: the
YJ-20 climbs to ~1425 u above its ~1190 u INI cap. The built-in estimator misses
this because it snaps to the profile.

Kinematic lofters climb near-vertical. While lofting, kinematic ammo uses a 90°
climb angle. This mirrors the game's loft waypoint, which permits exceeding the
angle limit for kinematic missiles.

Steep dive for high ballistic lofters. Kinematic lofters whose loft exceeds the
zero-density altitude (~613.5 u) dive at the onset angle, up to the INI descent
caps, instead of the default descent angle, so they clear the vacuum band
quickly (§4.5).

### Speed model

Non-kinematic ammo (`Kinematics == None`: SS-N-19, SS-N-12, YJ-18A, cruise and
sea-skimming missiles) seeks the per-stage target speed with no drag. It
decelerates toward the target at `_deceleration·g` and accelerates through the
game's thrust helper, clamped at the stage target. This branch lands within ±3 s
on the reference set.

Kinematic ammo (`ApplyKinematics = True`: YJ-20, HHQ-9B) flies on the game's
thrust and drag physics. The missile burns out, then coasts:

```
vel += CalculateThrustOverTime(ap, isAir, t, dt)
vel -= CalculateDrag(alt, vel, dt, −pitch, dragFactor, motorBurning,
                     targetAlt, liftFactor, minVel, −pitchRate)
if (motorBurning && vel > stageTgt) vel = stageTgt    // clamp THRUST only
```

Two conventions are load-bearing. First, pitch sign: the game's `CalculateDrag`
treats positive pitch as descending (its gravity term accelerates a dive), while
the integrator treats positive as climbing, so pitch and pitch-rate are negated
before the call. Second, the clamp gates on motor burn: it stops thrust from
overshooting the stage cap, and nothing else. After burnout the missile coasts
freely and may legitimately exceed the cap in a dive (YJ-20: 6600 to 7094 kn).

### The terminal vacuum brake

`CalculateDrag`'s induced-lift term is

```
num9 = sqrt(|cos pitch|) · dragFactor · liftFactor · 9.81 / max(ρ(targetAlt)/1.225, 0.001)
```

It is active only post-burnout, independent of speed, and amplified ~800× when
the supplied target altitude lies in vacuum because the density divisor floors
at 0.001. The live game feeds the target's altitude while the seeker holds lock
and the missile's own altitude once the lock drops. Telemetry attributed the
YJ-20's terminal deceleration (~164 kn/s at ~700 u altitude, bleeding 7135 to
5325 kn) to this term firing during the brief lock drop at the steep nose-over.

The integrator reproduces the effect by feeding own altitude under a gate that
mirrors the lock drop:

```
inVacuumDive = phase == 2 && altitude > 613.5 u && pitch < −40°
```

Below the zero-density altitude the term self-limits because dense air restores
the divisor. The steep-dive requirement keeps the brake off until the missile is in a
hard dive, so it clears the vacuum band in a few seconds like the real ~10 s
transient. Low-loft and non-kinematic ammo never satisfy the gate:
HHQ-9B's loft sits at 386 u, and non-kinematic ammo never enter the drag
branch.

### TerminalLoft glide

TerminalLoft ammo (HHQ-9B) flies a concave profile: hold loft altitude, then
descend steeply into dense air where ordinary aero drag bleeds the speed
(3844 to 1206 kn measured). A hand-rolled glide cannot match it. A flat hold
arrives ~7 s early because nothing decelerates the missile in thin air, and a
straight aim-at-target glide descends too early, over-decelerates, and stalls.

The game already computes the flown curve. `BuildAltitudeNodes(ap, launchAlt,
targetAlt, flatDistTotal, −1, out loftEndFlat)` returns altitude-vs-distance
nodes. The integrator fetches them once and follows them with a short lookahead
aim: lookahead is 20 integration steps, floored at 50 u. While the curve is
high the aim is level and the missile holds loft; it noses down where the curve
descends. If the handle is unavailable, a straight aim-at-target glide capped at
the descent angle takes over. The node schedule stays scoped to TerminalLoft.
Applied to a high lofter it would cap the loft at `MaxLoftAlt` and kill the
zoom-climb the vacuum brake depends on.

### Class taxonomy

```
                    ┌─ Kinematics == None ──► NON-KINEMATIC: stage-speed seek, no drag
       ammo ────────┤                          region model (+ nodes if TerminalLoft)
                    └─ kinematic ─┬─ _terminalLoft ─► TERMINAL-LOFT: node glide,
                                  │                    thrust+drag, no vacuum brake
                                  └─ loftAlt > 613.5 u ─► HIGH BALLISTIC LOFTER:
                                  │                        90° boost, steep dive, vacuum brake
                                  └─ otherwise ─► GENERIC KINEMATIC: region model, thrust+drag
```

### Measured accuracy

Gaps from in-game reference shots, 2026-09-02, with the integrator owning every
shot. Gap = estimate − actual flight time; positive means the estimate ran long.

| Shot | Class | Integrator | Waypoint port | Legacy |
|---|---|---|---|---|
| SS-N-19 | non-kin lofter | +1.6 s | +5.6 s | −33 s |
| SS-N-12 | non-kin lofter | −2.5 s | +1.1 s | −31 s |
| YJ-18A | sea-skimmer | +0.5 s | +2.6 s | +33 s |
| HHQ-9B | kinematic TerminalLoft SAM | +2 s | +2.3 s | +5 s |
| YJ-20 | kinematic Mach-10 lofter | ~−4 s * | +19 s | +31 to +34 s |
| RGM-109B | low-kin cruise | +1.7 s | +4.7 s | n/a |

\* The YJ-20 estimate is stable at ~132.5 s. The actual varies 125 to 129 s
between runs because the target ship maneuvers during the ~2-minute flight.
That variance is target-motion prediction, not integrator error.

### Model constants

| Constant | Value | Meaning |
|---|---|---|
| `IntegrationStepSim` | 0.1 s | fixed Euler step |
| `AltToleranceU` | 0.5 u | altitude deadband for pitch commands |
| `DefaultClimbDeg`, `DefaultDescentDeg` | 20° | fallback when INI angles are unset |
| `BoostClimbDeg` | 90° | kinematic loft climb |
| `DefaultTurnRateDeg` | 5°/s | fallback pitch slew rate |
| `MinDescentOnsetDeg` | 5° | floor for the geometric dive onset |
| `StallSpeedMultiplier` | 1.1 | intercept-reject threshold vs `MinVelocity` |
| `CloseEnoughDistU` | 3 u | intercept radius |
| `MinSpeedKn` | 1 kn | mid-flight stall abort |
| `VacuumDivePitchThreshold` | −40° | vacuum-brake gate |
| `LookaheadMultiplier`, `MinLookaheadU` | 20 steps, 50 u | node-follow lookahead |
| `ZeroDensityAltU` | 1/0.00163 ≈ 613.5 u | game's zero-density altitude |
| `EvasiveBoostFraction` | 0.8 | evasive-target speed boost (shared) |
| `MaxFlightTimeFallback` | 600 s | sim horizon when `_maxFlightTime` is unset (shared) |
| `KU` | 0.0076554087 | knots to Unity units per second |

### Assumptions and limitations

- Surface targets. Aim and schedule assume a sea-level target
  (`max(targetAlt, 0)`). An air target would be aimed at the surface beneath it.
- Fixed dt of 0.1 s. Adequate for a one-shot estimate; not adaptive.
- Air launch inherits the platform's speed but not its heading vector. The
  error is negligible against the launch phase.
- Target-motion variance on long flights, per the YJ-20 note above.
- Low-kinematic cruise missiles (Tomahawk class) run a few seconds short on
  ~9.5-minute cruises in every tier. Their guidance routes around waypoints and
  adds distance that no straight-line forward sim captures.

## Tier 2: ported waypoint sim

Code: `Simulation/WaypointSim.cs`. The public branch's unified shot simulator
`Missile.SimulateShotLinear` was removed from beta, but nearly everything it
stood on survives. The port reconstructs the loop by reflection:

1. Build the flight plan with the game's own generator,
   `Missile.CreateWaypointConfigs`: loft, cruise, and terminal waypoints from
   the ammo INI.
2. Seed an initial intercept time with the game's analytical and simple
   estimators.
3. Fly the plan at fixed 0.5 s steps. The game's waypoint guidance stepper
   `Waypoint.UpdateAndGetActiveWaypoint` supplies stage commands,
   `MissileSimulator.ComputePN` steers, and `CalculateThrustOverTime` plus
   `CalculateDrag` set the speed.
4. Detect intercept at closest approach, the same criterion as the integrator.

Two readiness gates control the tier. `Ready` covers the flight-plan surface;
`FullReady` adds the full-loop handles (PN, thrust, drag, acceleration times,
both seed estimators). If either gate is false, `EndTime` returns −1 and the
tier no-ops. Both resolve only on beta, because `EnsureLookup` is called from
`EnsureSimLookup` only after beta detection.

The tier is not primary because the game's linear sim caps the loft at the
declared `MaxLoftAlt` with instantaneous pitch; only lateral guidance is
turn-rate limited. It cannot reproduce the high-lofter overshoot from §4.3 and
lands the YJ-20 ~19 s early. As a fallback net it still beats the legacy tier
by ~30 s on lofters (SS-N-19: +5.6 s vs −33 s), which is why it sits in the
middle of the chain.

## Tier 3: the game's built-in estimator

`FlightTime.MaxRangePreciseEndTime` invokes
`AmmunitionParameters.MaxRangePrecise(shooter, targetPos, targetVel,
iterations: 0, evasive)` through reflection. `iterations = 0` means a single
pass. The game itself uses 8, but the iterative variant costs ~8 to 9× the sim
work, moved fast kinematic missiles by ~3 s in testing, and did nothing for
low-kinematics cruise missiles whose error is routing distance.

The result type is not public API and was renamed between branches
(`Missile.KinematicRangeResult` became `MissileSimulator`). The code never
names it. The `InterceptTime` field is bound lazily off the runtime type of the
first returned object, so only the field name matters and the rename is
absorbed. Any miss returns −1.

On the public branch this is the working path: its single pass drives the
game's own `SimulateShotLinear`. On beta it is a last-resort net, and in normal
operation the integrator answers every shot before this tier is consulted.

## Tier 4: straight-line max speed

If every simulator declines: `time = distance / (_maxVelocityInKnots ·
0.5144447)`, with the speed floored at 0.1 m/s against division by zero. This
is a bound, not a flight model. It exists so coordination still functions on a
game version whose internals fail to resolve.

## Speed profile and group-forming delay

Grouped salvos (SS-N-12, SS-N-19) shed speed while their formation forms and
arrive later than a solo estimate predicts. The correction,
`FlightTime.GroupFormingDelay`, feeds the coordinator's `groupDelay` term and is
computed from the game's own shot speed profile with no per-type constants:

1. `ComputeSpeedProfile` invokes the branch's shot simulator (same plumbing and
   drift absorption as the tiers above) and records `(time, speed)` samples.
2. Integrate cumulative distance `P(t)` by trapezoidal rule. With `span` as the
   launcher's ripple duration, solve `P(τ_form) = 2.5 · P(span)`. The factor
   2.5 = 1/0.4 mirrors the leader's −40% speed clamp closing on the
   stragglers.
3. `groupDelay = max(0, 0.4·τ_form − span)`. The 0.4 is the leader throttle.

The result is range-aware. A flat speed profile gives `τ_form = 2.5·span` and
delay 0. A lofter already descended to slow final-flight by `2.5·P(span)` gives
a positive delay. One still in fast loft at that distance gives ≈ 0. Delay is 0
for non-grouped ammo (`_maxGroupSize ≤ 1`). When neither sim method resolves,
the profile is empty and the correction no-ops.

## Diagnostics

All of these are gated behind `VerboseLogging` and silent in normal play.

| Line | Content |
|---|---|
| `sim-init` | which reflection handles resolved at load |
| `wp-init` | waypoint-sim surface resolution (`Ready`, `FullReady`) |
| `sim-launch` | integrator setup for a shot: launch pitch, initial phase, turn rate, loft altitude, descent angles |
| `sim-track` | the integrator's own speed, altitude, pitch, phase, and distance every 15 s |
| `track`, `int-phases` | the live missile's telemetry and the integrator's phase breakdown for it |
| `wp-track` | the ported waypoint sim's own trace |
| `gap` | at impact: estimate vs actual flight time, plus the waypoint and legacy estimates for comparison. The standing accuracy check |
| `group-tau` | the group-forming delay computation per grouped salvo |

## Rejected alternatives

Recorded so nobody re-runs them:

- Blanket phase-2 vacuum brake (own altitude for all of phase 2). It kept
  braking below the density line, cratered the sim below 700 kn, and stalled
  into the fallback (+34 s on YJ-20). The `>613.5 u && pitch<−40°` gate fixed
  it.
- Straight aim-at-target glide for TerminalLoft. Over-decelerated, stalled, and
  fell through to the legacy tier. Replaced by following `BuildAltitudeNodes`.
- `BuildAltitudeNodes` for high lofters. It caps the loft at `MaxLoftAlt` and
  kills the zoom-climb. Scoped to TerminalLoft only.
- Seeker-cone lock-drop grounding (look angle vs `_seekerFOV` or
  `_seekerGimbalFOV`). Tried twice, falsified by telemetry: the real YJ-20 held
  lock at t+90 at a larger look angle and dropped it at t+105 in the steep
  dive. The drop tracks the dive, not look angle. The −40° pitch proxy is the
  faithful descriptor.
- `SearchOptimalLoftAltitude` for the loft altitude. It is a range optimizer
  and returns ~1 u for the YJ-20, after which sea-level drag kills the flight.
  `LoftCap` is the flown altitude.
- Boost-to-loft climb steering. Regressed the gradual-climb SAM (HHQ-9B).
  Finite turn-rate nose-over is the mechanism.
- Waypoint-generated stage boundaries: grounding the loft-end on
  `CreateWaypointConfigs`. Tested 2026-09-02 and rejected. The generator's
  distances belong to a five-stage plan with gradual PN speed transitions; the
  integrator's three-phase model uses its hand-derived loft-end as a
  load-bearing compensation. Flipping the flag regressed SS-N-19 from +1.6 s
  to −25.2 s and SS-N-12 from −2.5 s to −28.8 s.

## File map

| File | Responsibility |
|---|---|
| `Simulation/FlightTime.cs` | public API (`Estimate`), tier wiring (`KinematicRaw`), TTL caches, straight-line fallback, speed profile and group-forming delay, legacy-tier invocation |
| `Simulation/FlightTime.Integrator.cs` | the grounded step integrator (Tier 1) and phase diagnostics |
| `Simulation/FlightTime.Reflection.cs` | branch detection and reflection resolution for all sim internals |
| `Simulation/WaypointSim.cs` | the ported public shot simulator (Tier 2) |
| `Support/GameUnits.cs` | shared unit constants (Unity units vs metres, nm, knots) |
