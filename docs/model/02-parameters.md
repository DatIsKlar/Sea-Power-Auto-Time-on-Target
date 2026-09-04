# 2. Parameters, reflection surface, constants

[← Overview](01-overview.md) · [index](00-index.md) · next: [Trajectory](03-trajectory.md)

Units throughout: **positions and altitudes in Unity units** (1 u = 67.200066 m), **speeds in
knots**, **angles in degrees**. `KU = 0.0076554087` converts knots → Unity units per second.

Sign convention: **positive pitch is climbing.** The game's `CalculateDrag` uses the opposite sign,
so pitch and pitch-rate are negated at the call boundary; see [§4](04-speed.md).

## 2.1 Ammunition `.ini` fields

Every `AmmunitionParameters` field the model reads, all via `ap.`:

| field | role in the model | see |
|---|---|---|
| `Kinematics` (`ApplyKinematics`) | selects the speed branch: `None` → stage-seek, else thrust+drag | [§4](04-speed.md) |
| `_maxVelocityInKnots` | cruise / final-phase target speed; also the launch and ToBearing command | [§3](03-trajectory.md), [§4](04-speed.md) |
| `_maxLoftVelocityInKnots` | loft-dash target speed (non-kinematic) | [§4](04-speed.md) |
| `_terminalVelocityInKnots` | terminal target speed | [§4](04-speed.md) |
| `_acceleration` / `_deceleration` | stage-seek slew rates (non-kinematic) | [§4](04-speed.md) |
| `_maxLoftAngle` | commanded climb angle | [§3](03-trajectory.md) |
| `_finalFlightPhaseMaxAngle` | descent pitch | [§3](03-trajectory.md) |
| `_seaSkimmingMaxDescentAngle` | descent pitch alternative; also the onset maximum | [§3](03-trajectory.md) |
| `_finalFlightPhaseDistToTargetUnity` / `_seaSkimmingStartDistToTargetUnity` | loft → cruise transition distance | [§3](03-trajectory.md) |
| `_finalFlightPhaseAltUnity` / `_seaSkimmingAltUnity` | cruise altitude | [§3](03-trajectory.md) |
| `_terminalApproachDist` / `_terminalAltUnity` | terminal onset distance / altitude | [§3](03-trajectory.md) |
| `_searchForTargetsTime` | delays the real terminal transition; pulls the modelled onset in | [§3](03-trajectory.md) |
| `_loftToSkim` | whether the loft ends at sea-skim or at the final phase | [§3](03-trajectory.md) |
| `_terminalLoft` | concave glide; altitude comes from `BuildAltitudeNodes` | [§3](03-trajectory.md) |
| `_initialFlightPhaseDuration` | how long the round flies its launch attitude and heading | [§3](03-trajectory.md) |
| `_maxTurnRateDegrees` | finite pitch and heading slew rate | [§3](03-trajectory.md) |
| `_supportsBanking` | adds a second rotation budget on non-kinematic ammunition | [§3](03-trajectory.md) |
| `_maxFlightTime` | loop bound (600 s when unset) | [§5](05-loop.md) |
| `GetDragFactor(isAir)` | drag coefficient passed to `CalculateDrag` | [§4](04-speed.md) |
| `LiftFactor` | induced-lift coefficient passed to `CalculateDrag` | [§4](04-speed.md) |
| `MinVelocity` | stall floor, and the intercept-reject threshold | [§4](04-speed.md) |

### `_fixVerticalLaunchAngle`: reads 35° everywhere, unusable

`_fixVerticalLaunchAngle` (with `_additionalFixVerticalLaunchAngle` and
`_fixVerticalLaunchAngleForLauncher`) reads **35° for every launcher in the game**. It is the `.ini`
default, and the bool gating it also defaults true (`ObjectBaseLoader.cs:2688-2690`), so it is not a
usable test for vertical launch and not a usable launch elevation. The model reads the launcher's
transform instead ([§3.1](03-trajectory.md#31-launch-geometry)). The field is still logged beside the
measured value for comparison.

## 2.2 Launcher fields

Read off the `WeaponSystem` / `_vwp` that will fire the round:

| field | role |
|---|---|
| `_containers[i]._gunObject` | the object the game elevates, the rail's true attitude |
| `_containerBaseObject` | shared base when containers are joined; fallback source for the rail |
| `_mountObject` | mount pitch, subtracted when computing a trainable launcher's aim |
| `_isMountRotatable`, `_areContainersRotatable` | together decide fixed rail vs trainable mount |

## 2.3 Reflection surface

Resolved once in `FlightTime.EnsureSimLookup`, all static on `MissileSimulator` unless noted. A miss
on any handle makes the integrator decline, and the shot falls to the next tier.

- `CalculateThrustOverTime(AmmunitionParameters, bool isAir, float t, float dt) → float`
- `CalculateDrag(float alt, float vel, float dt, float pitch, float dragFactor, bool motorBurning,
  float targetAlt = 0, float liftFactor = 0.005, float stallKn = 0, float pitchRate = 0) → float`
  (10-arg)
- `CalculateDrag(…, out float aero, out float induced, out float parallelG, …) → float` (13-arg,
  diagnostic; resolved with three `float.MakeByRefType()`)
- `LoftCap(AmmunitionParameters, float launchAlt, float targetAlt) → float`
- `BuildAltitudeNodes(AmmunitionParameters, float launchAlt, float targetAlt, float flatDistTotal,
  float loftAltOverride, out float loftEndFlat) → List<Vector2>`; **private static**, resolved by
  exact `GetMethod` with `typeof(float).MakeByRefType()` for the `out` parameter, because a by-name
  search skips by-ref signatures
- `BurnEndTime(AmmunitionParameters, bool) → float`

## 2.4 Model constants

Every constant below is either a game value or a numerical property of the integration, never a
per-missile tuning.

| constant | value | meaning and derivation |
|---|---|---|
| `IntegrationStepSim` | 0.1 s | fixed Euler step |
| `KU` | 0.0076554087 | knots → Unity units/s (game constant) |
| `MetersPerUnity` | 67.200066 | Unity unit → metres (game constant) |
| `ZeroDensityAltU` | 1 / 0.00163 ≈ 613.5 u | where the game's air density `(1 − 0.00163·h)^4.256` reaches zero |
| `AltToleranceU` | 0.5 u | altitude deadband on the pitch command |
| `DefaultClimbDeg`, `DefaultDescentDeg` | 30° | fallback when the `.ini` angles are unset; the game's own `.ini` defaults (`AmmunitionParameters.cs:1633/1662/1683`) |
| `BoostClimbDeg` | 90° | vertical boost climb, applied only to high ballistic lofters ([§3](03-trajectory.md)) |
| `BankingRollRateDeg` | 60°/s | the game's hardcoded roll rate (`WeaponBase.cs:1792`) |
| `DefaultTurnRateDeg` | 5°/s | fallback slew rate when `_maxTurnRateDegrees` is unset |
| `ToBearingConeDeg`, `ToBearingMaxSeconds` | 5°, 10.0 s | the game's ToBearing exit test (`Missile.cs:343`) |
| `MinDescentOnsetDeg` | 5° | floor on the geometric dive onset, bounds the tangent |
| `VacuumDivePitchThreshold` | −40° | lock-drop proxy for the vacuum brake ([§4](04-speed.md)) |
| `StallSpeedMultiplier` | 1.1 | intercept-reject threshold against `MinVelocity` |
| `CloseEnoughDistU` | 3 u | intercept radius |
| `MinSpeedKn` | 1 kn | mid-flight stall abort |
| `LookaheadMultiplier`, `MinLookaheadU` | 20 steps, 50 u | lookahead for proportional hold and node following |
| `EvasiveBoostFraction` | 0.8 | evasive-target speed boost, mirroring the game's own estimator |
| `MaxFlightTimeFallback` | 600 s | horizon when `_maxFlightTime` is unset |
