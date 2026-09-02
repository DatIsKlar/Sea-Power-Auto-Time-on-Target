# Integrator: mathematical specification

Complete specification of the grounded step integrator, the Tier-1 flight-time
estimator in `Simulation/FlightTime.Integrator.cs`. It computes the estimated
flight time of one missile from one shooter to one target on the beta branch of
Sea Power. Its place in the estimator chain, the fallback tiers, and caching are
documented in `docs/FLIGHT-TIME-MODEL.md`; this file is the full description of
the integrator itself, written so the model can be reproduced by hand.

Every constant in this document is either a game constant or a value read from
the ammo's INI file at runtime. There are no fitted per-missile numbers.

## 1. Scope and decline conditions

The integrator runs only when the loaded game branch is beta. It integrates one
shot and returns the estimated flight time in seconds, or −1 to decline. A
decline hands the shot to the next chain tier (ported waypoint sim, then the
game's built-in estimator). The decline conditions, in order of evaluation:

| # | Condition | Where |
|---|---|---|
| 1 | session is not beta (`_simIsBeta` false) | entry gate |
| 2 | `CalculateThrustOverTime` reflection handle missing | entry gate |
| 3 | kinematic ammo and `CalculateDrag` handle missing | entry gate |
| 4 | stall at closest approach: `v < 1.1 · MinVelocity` | intercept test |
| 5 | speed collapse mid-flight: `v < 1 kn` | speed guard |
| 6 | no intercept within the flight horizon `maxFlight` | loop exit |
| 7 | any reflection call throws | exception guard |

## 2. Units, coordinates, conventions

| Quantity | Unit | Conversion |
|---|---|---|
| position, altitude, distance | Unity units (u) | 1 u = 67.200066 m |
| speed | knots | 1 kn = 0.5144447 m/s |
| speed to position | `KU = 0.0076554087` u/s per kn | = 0.5144447 / 67.200066 |
| acceleration | m/s² → kn/s | × 1.94384 |
| angles | degrees | pitch positive = climbing (our convention) |

The game's `CalculateDrag` uses the opposite pitch convention (positive =
descending). Every pitch handed to it is negated (§6).

Positions are world-space Unity coordinates. Horizontal distance ignores the
y axis. The target is predicted linearly: `predTgt(t) = targetPos + targetVel·t`.

## 3. Inputs

### 3.1 INI parameters

Every `AmmunitionParameters` field the model reads, and its role:

| Field | Role |
|---|---|
| `Kinematics` (`ApplyKinematics`) | `None` selects the speed-seek branch; anything else flies thrust + drag (§5.6) |
| `_maxVelocityInKnots` | cruise and final-phase target speed |
| `_maxLoftVelocityInKnots` | loft-phase target speed (non-kinematic ammo) |
| `_terminalVelocityInKnots` | terminal-phase target speed |
| `_acceleration`, `_deceleration` | speed-seek slew rates, in g (non-kinematic) |
| `_maxLoftAngle` | climb angle for non-kinematic lofters; default 20° |
| `_maxTurnRateDegrees` | pitch slew rate; default 5°/s |
| `_finalFlightPhaseMaxAngle` | descent angle; default fallback 20° |
| `_seaSkimmingMaxDescentAngle` | descent angle alternative and onset cap |
| `_finalFlightPhaseDistToTargetUnity` | loft-end distance (non-skim) |
| `_seaSkimmingStartDistToTargetUnity` | loft-end distance when `_loftToSkim` |
| `_finalFlightPhaseAltUnity`, `_seaSkimmingAltUnity` | cruise altitude |
| `_terminalApproachDist` | terminal-phase onset distance |
| `_terminalAltUnity` | terminal-phase altitude |
| `_loftToSkim` | loft ends at the sea-skim stage instead of the final stage |
| `_terminalLoft` | concave glide class; switches on the node-follow altitude (§7) |
| `_maxFlightTime` | integration horizon; 600 s when unset |
| `GetDragFactor(isAir)` | drag coefficient fed to `CalculateDrag` |
| `LiftFactor` | induced-lift coefficient fed to `CalculateDrag` |
| `MinVelocity` | stall floor; intercept-reject threshold |
| `AssumeEvasiveTarget(target)` | enables the evasive boost (§4.1) |

From the launcher (`GetWeaponSystemsForAmmunition(...)[0]._vwp`), when
`_fixVerticalLaunchAngleForLauncher` is set: launch pitch =
`_fixVerticalLaunchAngle + _additionalFixVerticalLaunchAngle` (VLS ≈ 90°).
Plus `_initialFlightPhaseDuration`: how long the missile holds that elevation.

### 3.2 Reflection signatures

All static methods on `SeaPower.MissileSimulator`, resolved once and cached:

```
float CalculateThrustOverTime(AmmunitionParameters ap, bool isAir, float t, float dt)
float CalculateDrag(float alt, float vel, float dt, float pitch, float dragFactor,
                    bool motorBurning, float targetAlt, float liftFactor,
                    float stallKn, float pitchRate)
float LoftCap(AmmunitionParameters ap, float launchAlt, float targetAlt)
List<Vector2> BuildAltitudeNodes(AmmunitionParameters ap, float launchAlt,
                    float targetAlt, float flatDistTotal, float loftAltOverride,
                    out float loftEndFlat)     // private; needs an exact GetMethod
```

`CalculateThrustOverTime` returns the speed increment in knots for the step,
0 after burnout. `CalculateDrag` returns the knots to subtract for the step (§6).

## 4. Setup, once per shot

Launch and target state:

```
launchPos = shooter position                    (u)
targetPos = target position                     (u)
targetVel = target velocity                     (u/s)
isAir     = shooter is an air unit
startVel  = max(shooter speed, 0)               (kn; inherited by air launches)
maxFlight = _maxFlightTime > 0 ? _maxFlightTime : 600     (s)
targetAlt0 = max(targetPos.y, 0)                (sea-level floor for surface aim)
```

### 4.1 Evasive target boost

When the game classifies the target as evasive and it is moving, its velocity
is boosted along the horizontal axis away from the shooter, then re-clamped to
its original magnitude:

```
flee = targetPos − launchPos, y zeroed
if |targetVel| > 0 and AssumeEvasiveTarget and |flee| > 1e-8:
    targetVel += normalize(flee) · (0.8 · |targetVel|)
    targetVel  = normalize(targetVel) · min(|targetVel|, original magnitude)
```

This mirrors the convention of the game's own estimators.

### 4.2 Loft altitude and the lofting decision

```
cap   = LoftCap(ap, max(launchPos.y, 0), targetAlt0)
floor = max(max(launchPos.y, 0), targetAlt0)
loftAlt = cap          if cap > floor + 0.5, else −1
lofting = loftAlt > max(launchPos.y, 0) + 0.5
```

`LoftCap` is the altitude the loft flies, not a range optimum. The alternative
`SearchOptimalLoftAltitude` returns ~1 u for exotic lofters and collapses the
flight, so it is not used.

### 4.3 Class gates

```
nonKin                = (ap.Kinematics == None)
isTerminalLoft        = ap._terminalLoft
isHighBallisticLofter = !nonKin and lofting and loftAlt > ZeroDensityAltU
ZeroDensityAltU       = 1 / 0.00163 ≈ 613.5 u        (ρ(h) = 0, §6)
```

### 4.4 Stage boundaries, speeds, angles

```
climbDeg      = _maxLoftAngle > 0.5 ? _maxLoftAngle : 20
boostClimbDeg = (!nonKin and lofting) ? 90 : climbDeg
turnRate      = _maxTurnRateDegrees > 0.001 ? _maxTurnRateDegrees : 5
maxVelKn      = max(_maxVelocityInKnots, 1)
loftVelKn     = _maxLoftVelocityInKnots > 0 ? _maxLoftVelocityInKnots : maxVelKn
termVelKn     = _terminalVelocityInKnots > 0 ? _terminalVelocityInKnots : maxVelKn
decelPerStep  = _deceleration · 9.8 · 1.94384 · dt        (kn per step)

toSkim    = _loftToSkim and _seaSkimmingStartDistToTargetUnity > 0
finalDist = toSkim ? _seaSkimmingStartDistToTargetUnity
          : (_finalFlightPhaseDistToTargetUnity > 0 ? _finalFlightPhaseDistToTargetUnity
          : max(_seaSkimmingStartDistToTargetUnity, _finalFlightPhaseDistToTargetUnity))
finalAlt  = toSkim ? max(_seaSkimmingAltUnity, 0)
          : (_finalFlightPhaseAltUnity > 0 ? _finalFlightPhaseAltUnity
          : (_seaSkimmingAltUnity > 0 ? _seaSkimmingAltUnity : 0))
termDist  = _terminalApproachDist
termAlt   = _terminalAltUnity > 0 ? _terminalAltUnity : finalAlt
descentDeg = _finalFlightPhaseMaxAngle > 0.01 ? _finalFlightPhaseMaxAngle
           : (_seaSkimmingMaxDescentAngle > 0.01 ? _seaSkimmingMaxDescentAngle : 20)
descentOnsetDeg = max(descentDeg, _finalFlightPhaseMaxAngle, _seaSkimmingMaxDescentAngle)
```

### 4.5 Launch pitch

If the launcher declares a fixed vertical launch angle (§3.1), the missile
holds it for `initialPhaseDur = max(_initialFlightPhaseDuration, 0)` seconds
before guidance takes over. Otherwise the initial pitch is 0.

### 4.6 TerminalLoft altitude nodes

For `_terminalLoft` ammo with a resolved node handle and a shot farther than
1 u, the game's own altitude-vs-distance curve is fetched once:

```
flatDistTotal = horizontal distance launch → target
nodes = BuildAltitudeNodes(ap, max(launchPos.y, 0), targetAlt0,
                           flatDistTotal, −1, out loftEndFlat)
```

`nodes` is a list of `(flat distance from launch, altitude)` pairs, used if it
has ≥ 2 entries. The node-follow controller is §7. When the handle is missing
or the list is too short, the glide fallback in §7.3 takes over.

## 5. The integration loop

State: `pos` (u), `v` (kn), `t` (s), `prevPitch` (deg), `prevFlat` (u).
Step size `dt = 0.1 s`. Loop while `t < maxFlight`.

### 5.1 Predict the target

```
predTgt = targetPos + targetVel · t
```

### 5.2 Horizontal distance and intercept test

```
flatDist = sqrt((predTgt.x − pos.x)² + (predTgt.z − pos.z)²)
if (flatDist > prevFlat and t > dt) or flatDist < 3:
    return −1   if v < 1.1 · MinVelocity        (stall at intercept)
    return t    otherwise
prevFlat = flatDist
```

Intercept is closest approach: the first step where the horizontal distance
stops shrinking, or distance below 3 u.

### 5.3 Stage selection

```
horizDir = (dx, 0, dz) / flatDist        if flatDist > 1e-4, else (0, 0, 1)
descentGeomDist = (pos.y − termAlt) / tan(max(descentOnsetDeg, 5°))
diveStart = max(termDist, descentGeomDist)

phase 2 (terminal):   diveStart > 0 and flatDist ≤ diveStart
                      stageTgt = termVelKn, stageAlt = termAlt
phase 1 (final/skim): finalDist > 0 and flatDist ≤ finalDist
                      stageTgt = maxVelKn, stageAlt = finalAlt
phase 0 (loft):       lofting
                      stageTgt = loftVelKn, stageAlt = loftAlt
else:                 phase 1, stageTgt = maxVelKn, stageAlt = finalAlt
```

The geometric dive onset makes a high-apex missile start descending gradually:
it begins the terminal phase no closer than the distance needed to lose
`pos.y − termAlt` altitude at the onset angle.

### 5.4 Pitch command

```
altErr = stageAlt − pos.y
diveDeg = descentOnsetDeg   if isHighBallisticLofter, else descentDeg
targetPitch = boostClimbDeg   if altErr > 0.5
            = −diveDeg        if altErr < −0.5
            = 0               otherwise
```

TerminalLoft ammo overrides `targetPitch` while lofting (§7). Then the launch
hold:

```
if launchPitch ≥ 0 and t < initialPhaseDur: targetPitch = launchPitch
```

### 5.5 Pitch slew

Pitch is rate-limited to `turnRate`:

```
pitchDeg  = prevPitch + clamp(targetPitch − prevPitch, −turnRate·dt, +turnRate·dt)
pitchRate = (pitchDeg − prevPitch) / dt
```

The finite slew is what makes a high lofter overshoot its declared loft
altitude: the nose keeps climbing at the rate limit while guidance is already
commanding level flight (YJ-20 apex ~1425 u above its ~1190 u INI cap).

### 5.6 Speed update

```
thrust = CalculateThrustOverTime(ap, isAir, t, dt)        (kn this step)
motorBurning = thrust > 0
```

Non-kinematic ammo seeks the stage speed with no drag:

```
if v > stageTgt:            v −= min(decelPerStep, v − stageTgt)
elif v < stageTgt − 0.001:  v += min(thrust, stageTgt − v)
```

Kinematic ammo flies on thrust and the game's drag (§6):

```
v += thrust
targetAltArg = pos.y      if phase == 2 and pos.y > 613.5 and pitchDeg < −40
             = predTgt.y  otherwise
v −= CalculateDrag(pos.y, v·KU, dt, −pitchDeg, dragFactor, motorBurning,
                   targetAltArg, LiftFactor, MinVelocity, −pitchRate)
if motorBurning and v > stageTgt: v = stageTgt        (clamp THRUST only)
```

The clamp gates on motor burn: after burnout the missile coasts and may exceed
the stage speed in a dive (measured YJ-20: 6600 to 7094 kn). The
`targetAltArg` switch is the vacuum brake (§6.2).

Speed guard: `if v < 1: return −1`.

### 5.7 Position update and telemetry

```
dir = horizDir · cos(pitchDeg) + up · sin(pitchDeg)       (positive pitch = up)
pos += v · KU · dt · dir
t   += dt
```

Per-step telemetry accumulates climb/cruise/descent durations, exit speeds,
and peak altitude into `IntegratedPhases` (§10).

## 6. CalculateDrag, fully expanded

`MissileSimulator.CalculateDrag` returns the knots to subtract for one step.
Its terms, as implemented in the game:

```
ρ(h) = (1 − 0.00163·h)^4.256, clamped ≥ 0          → 0 at h ≈ 613.5 u
aero = ρ(alt) · vel² · 0.2 · dt · dragFactor       (vel in u/s; classic ½ρv² form)
stall = induced term from (stallKn, pitch, pitchRate, vel), only if !motorBurning
grav  = 9.81 · sin(−pitch)                         (game convention: +pitch = down)
lift  = 0                                          if motorBurning
        sqrt(|cos pitch|) · dragFactor · liftFactor · 9.81
            / max(ρ(targetAlt)/1.225, 0.001)       otherwise
return aero + (lift + grav) · 1.94384 · dt
```

Game constants in these formulas:

| Constant | Meaning |
|---|---|
| 0.00163, 4.256 | the game's air-density model ρ(h); density reaches 0 at 1/0.00163 ≈ 613.5 u |
| 0.2 | the game's drag constant (½ρv² form) |
| 9.81 | gravitational acceleration, m/s² |
| 1.225 | sea-level air density, kg/m³; normalizes ρ to a sea-level ratio |
| 0.001 | density-ratio floor; keeps the divisor non-zero in vacuum (→ up to ~800×) |
| 1.94384 | m/s to knots |

Properties that matter for the model:

- The `lift` term is speed-independent and active only post-burnout. Its
  divisor floors at 0.001, an ~800× amplification when `targetAlt` lies in
  vacuum (ρ = 0). At sea level the divisor is 1/1.225 ≈ 0.816 and the term is
  negligible.
- The live game feeds `targetAlt` = the target's altitude while the seeker
  holds lock, and the missile's own altitude once the lock drops.

### 6.1 Pitch-sign negation

The game's convention is positive pitch = descending: its gravity term
`9.81·sin(−pitch)` accelerates a dive. The integrator uses positive = climbing,
so both `pitchDeg` and `pitchRate` are negated in the call (§5.6).

### 6.2 The vacuum brake

Telemetry attributed the YJ-20's terminal deceleration to the lift term firing
during the brief seeker lock-drop at the steep nose-over. The integrator
reproduces the effect by feeding own altitude under the gate

```
inVacuumDive = phase == 2 and pos.y > 613.5 u and pitchDeg < −40°
```

Worked sample from the real missile at t+105 s: own altitude 708 u, pitch 59°,
dragFactor 2.4, liftFactor 0.005, lock dropped so targetAlt = 708 u:

```
ρ(708) = 0  →  divisor = 0.001
lift = sqrt(cos 59°) · 2.4 · 0.005 · 9.81 / 0.001 ≈ 84.5 m/s²
     · 1.94384 ≈ 164 kn/s                            (measured brake: ~164 kn/s)
```

In dense air (targetAlt = 0, divisor 0.816) the same state gives ≈ 0.2 kn/s.

The altitude bound keeps the brake self-limiting below the density line; the
pitch bound keeps it off until the missile is in a hard dive, so it
clears the vacuum band in a few seconds like the real ~10 s transient. HHQ-9B
(loft 386 u) never satisfies the altitude bound; non-kinematic ammo never enter
the drag branch.

## 7. TerminalLoft altitude control

`_terminalLoft` ammo (HHQ-9B) flies a concave profile: hold loft altitude, then
descend steeply into dense air where ordinary aero drag bleeds the speed
(measured 3844 to 1206 kn). While lofting, the pitch command of §5.4 is
replaced by one of the following.

### 7.1 Node follow (primary)

With altitude nodes from §4.6:

```
xNow    = flatDistTotal − flatDist                   (flat distance covered)
look    = max(v · KU · dt · 20, 50)                  (lookahead, u)
altAhead = InterpNodeAlt(nodes, min(xNow + look, flatDistTotal))
slopeDeg = atan2(pos.y − altAhead, look)             (degrees)
targetPitch = −clamp(slopeDeg, −boostClimbDeg, descentDeg)
```

`InterpNodeAlt` clamps at both ends of the node list and linearly interpolates
between the surrounding nodes otherwise. While the curve ahead is high the aim
is level and the missile holds loft; it noses down where the curve descends.

### 7.2 Why nodes only for TerminalLoft

Applied to a high lofter, `BuildAltitudeNodes` caps the loft at `MaxLoftAlt`
and kills the zoom-climb the vacuum brake depends on. The node schedule is
therefore scoped to `_terminalLoft`; every other ammo stays on §5.4.

### 7.3 Glide fallback

When no nodes are available: once `pos.y ≥ loftAlt − 0.5` the glide latches on,

```
glideDeg = atan2(max(pos.y − targetAlt0, 0), max(flatDist, 1))
targetPitch = −min(glideDeg, descentDeg)
```

## 8. Class taxonomy

```
                    ┌─ Kinematics == None ──► NON-KINEMATIC: stage-speed seek, no drag
       ammo ────────┤                          region model (+ nodes if TerminalLoft)
                    └─ kinematic ─┬─ _terminalLoft ──► TERMINAL-LOFT: node glide,
                                  │                     thrust+drag, no vacuum brake
                                  ├─ lofting ────────► KINEMATIC LOFTER: 90° boost climb,
                                  │                     region model, thrust+drag
                                  │    └─ loftAlt > 613.5 u ─► HIGH BALLISTIC LOFTER: adds
                                  │                             steep dive + vacuum brake
                                  └─ not lofting ────► GENERIC KINEMATIC: region model, thrust+drag
```

The 90° boost climb applies to every kinematic lofter (`boostClimbDeg` keys on
`!nonKin and lofting`). The 613.5 u class adds the steep dive pitch and is the
only class that reaches the vacuum brake's altitude gate.

## 9. Constants and derivations

| Constant | Value | Derivation |
|---|---|---|
| `IntegrationStepSim` | 0.1 s | fixed Euler step |
| `AltToleranceU` | 0.5 u | altitude deadband for pitch commands and loft acceptance |
| `DefaultClimbDeg`, `DefaultDescentDeg` | 20° | fallback when INI angles are unset |
| `BoostClimbDeg` | 90° | kinematic loft climb; game's loft waypoint allows exceeding the angle limit for kinematic missiles |
| `DefaultTurnRateDeg` | 5°/s | fallback pitch slew rate |
| `MinDescentOnsetDeg` | 5° | floor inside the geometric dive onset |
| `GravityKnPerMs` | 9.8 · 1.94384 | g (m/s²) to kn/s |
| `StallSpeedMultiplier` | 1.1 | intercept-reject threshold vs `MinVelocity` |
| `CloseEnoughDistU` | 3 u | intercept radius |
| `VelocityEpsilonKn` | 0.001 kn | deadband in the speed seek |
| `MinSpeedKn` | 1 kn | mid-flight stall abort |
| `VacuumDivePitchThreshold` | −40° | vacuum-brake gate; the steep-dive proxy for the seeker lock-drop |
| `TelemetrySampleIntervalSim` | 15 s | `sim-track` verbose sampling |
| `LookaheadMultiplier`, `MinLookaheadU` | 20 steps, 50 u | node-follow lookahead |
| `EvasiveBoostFraction` | 0.8 | evasive-target speed boost |
| `MaxFlightTimeFallback` | 600 s | horizon when `_maxFlightTime` is unset |
| `ZeroDensityAltU` | 1/0.00163 ≈ 613.5 u | altitude where ρ(h) = 0 in the game's air-density model |
| `KU` | 0.0076554087 | knots to Unity units per second |

## 10. Telemetry

`IntegratedPhases`, accumulated during the loop and emitted by the verbose
`int-phases` line for missiles the mod fired:

| Field | Content |
|---|---|
| `Valid`, `Lofting`, `LoftAltTarget` | run validity and loft state |
| `ClimbTime`, `CruiseTime`, `DescentTime` | seconds spent per phase |
| `VStart`, `VClimbExit`, `VCruiseExit`, `VTerm` | speeds at phase boundaries |
| `PeakAltU` | peak altitude reached by the simulated trajectory |
| `FinalDistU`, `TermDistU` | stage boundaries used |

The verbose `sim-launch` line logs the setup (launch pitch, initial phase, turn
rate, loft altitude, descent angles) and `sim-track` samples the integrator's
own speed, altitude, pitch, phase, and distance every 15 s of simulated flight.

## 11. Measured accuracy

Reference shots fired in-game 2026-09-02, integrator owning every shot.
Gap = estimate − actual; positive means the estimate ran long.

| Shot | Class | Gap |
|---|---|---|
| SS-N-19 | non-kin lofter | +1.6 s |
| SS-N-12 | non-kin lofter | −2.5 s |
| YJ-18A | sea-skimmer | +0.5 s |
| HHQ-9B | kinematic TerminalLoft SAM | +2 s |
| YJ-20 | kinematic Mach-10 lofter | ~−4 s * |
| RGM-109B | low-kin cruise | +1.7 s |

\* The estimate is stable at ~132.5 s; the actual varies 125 to 129 s between
runs because the target ship maneuvers during the ~2-minute flight. That is
target-motion prediction variance, not integrator error.

Worked trace, YJ-20 (exotic kinematic Mach-10 lofter, dragFactor 2.4, loft
~1425 u): sim speed kn / altitude u / pitch, against the live missile track:

| t+ | sim | actual | note |
|---|---|---|---|
| 15 s | 1691 / 87 u / 90° | 1596 / 94 u | vertical boost |
| 45 s | 5168 / 849 u / 90° | 5080 / 839 u | still climbing vertically |
| 60 s | 6600 / 1425 u / 5° | 6533 / 1418 u | apex, turn-rate overshoot |
| 75 s | 6597 / 1151 u / 29° | 7065 / 1192 u | nosing over |
| 90 s | 6280 / 1129 u / −4°, terminal | 7057 / 1191 u | dive onset |
| 105 s | ~4448 / ~700 u / −45° | 5368 / 746 u | vacuum brake fires |
| impact | ≈132.5 s | 125 to 129 s | see note above |

Worked trace, HHQ-9B (kinematic TerminalLoft SAM, altitude from
`BuildAltitudeNodes`):

| t+ | sim | actual | note |
|---|---|---|---|
| 30 s | 3548 / 418 u / −1° | 3417 / 395 u | apex, near-level |
| 60 s | 3661 / 407 u / −1° | 3719 / 365 u | holds loft on nodes |
| 90 s | 3207 / 253 u / −13° | 3266 / 251 u | concave descent begins |
| 105 s | 2670 / 162 u / −19° | 2683 / 157 u | steepening |
| 120 s | 1904 / 69 u / −23°, terminal | 1872 / 65 u | dense-air aero decel |
| impact | 134.3 s | 134 to 136 s | gap +2.2 s |

YJ-18A (sea-skimmer): cruise at ~0.2 u altitude, 530 kn, then terminal at
1900 kn from ~32 km out; sim 542.6 s vs 543 s actual. SS-N-19 and SS-N-12
(non-kin lofters): loft dash at `_maxLoftVelocity`, then sea-skim, then
terminal; +1.6 s and −2.5 s.

## 12. Assumptions and limitations

- Surface targets. Aim and schedule assume a sea-level target
  (`targetAlt0 = max(targetPos.y, 0)`). An air target would be aimed at the
  surface beneath it.
- Fixed dt of 0.1 s. Adequate for a one-shot estimate; not adaptive.
- Air launch inherits the platform speed but not its heading vector. The error
  is negligible against the launch phase.
- The single heuristic is the −40° vacuum-brake pitch bound. Seeker-geometry
  grounding was tried twice and falsified (§13); the pitch bound describes the
  physical class, steeply diving post-burnout flight, not one missile.
- Target-motion variance on long flights, per §11.

## 13. Rejected alternatives

Recorded so nobody re-runs them:

- Blanket phase-2 vacuum brake (own altitude for all of phase 2). Kept braking
  below the density line, cratered the sim below 700 kn, stalled into the
  fallback (+34 s on YJ-20). Fixed by the `>613.5 u and pitch<−40°` gate.
- Straight aim-at-target glide for TerminalLoft. Over-decelerated, stalled, and
  fell through to the legacy tier. Replaced by the node follow (§7.1).
- `BuildAltitudeNodes` for high lofters. Caps the loft at `MaxLoftAlt` and
  kills the zoom-climb. Scoped to TerminalLoft (§7.2).
- Seeker-cone lock-drop grounding (look angle vs `_seekerFOV` or
  `_seekerGimbalFOV`). Tried twice, falsified by telemetry: the real YJ-20 held
  lock at t+90 at a larger look angle and dropped it at t+105 in the steep
  dive. The drop tracks the dive, not look angle.
- `SearchOptimalLoftAltitude` for the loft altitude. A range optimizer that
  returns ~1 u for the YJ-20, after which sea-level drag kills the flight.
  `LoftCap` is the flown altitude (§4.2).
- Boost-to-loft climb steering. Regressed the gradual-climb SAM (HHQ-9B).
  Finite turn-rate nose-over is the mechanism (§5.5).
- Waypoint-generated stage boundaries: grounding the loft-end on
  `CreateWaypointConfigs`. Tested 2026-09-02 and rejected. The generator's
  distances belong to a five-stage plan with gradual PN speed transitions; the
  three-phase model uses its hand-derived loft-end as a load-bearing
  compensation. Flipping it regressed SS-N-19 from +1.6 s to −25.2 s and
  SS-N-12 from −2.5 s to −28.8 s.
