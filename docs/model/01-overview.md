# 1. Overview

[← index](00-index.md) · next: [Parameters & reflection](02-parameters.md)

## What the model is for

`FlightTime.Estimate(unit, ammoId, target)` answers one question: **how many seconds will this round
take to reach that target, if fired now?** Time-on-target coordination is built entirely on that
number. The schedule anchors on the slowest weapon and releases every other launch late enough to
converge, so a biased estimate spreads the salvo and the first hit wastes the rest.

The estimate must therefore hold up across weapon types that share no trajectory: a Mach-10 ballistic
lofter climbing out of the atmosphere, a sea-skimmer holding 0.2 u for eight minutes, a SAM flying a
concave terminal-loft profile, and modded missiles the mod has never seen.

## Design principle

**No fitted per-missile constants.** Every quantity the model consumes is one of three things:

- a **game constant**: the knots-to-Unity-units conversion `KU = 0.0076554087`, 67.200066 m per
  Unity unit, the ~613.5 u zero-density altitude implied by the game's air-density curve;
- a **per-ammunition `.ini` field** read off `AmmunitionParameters`: `_maxVelocityInKnots`,
  `_maxLoftAngle`, `_terminalApproachDist`, `_terminalLoft`, and the rest;
- a **value returned by a game method** through reflection: `CalculateThrustOverTime`,
  `CalculateDrag`, `LoftCap`, `BuildAltitudeNodes`.

A mechanism that only fits one missile is rejected. Each must generalise to the physical class it
belongs to, which is what lets the estimator handle unseen modded ammunition. The one deliberate
heuristic is the −40° terminal lock-drop proxy ([§4](04-speed.md)), and it too describes a class
(steeply diving post-burnout flight) rather than a specific round.

## The estimation chain

`Estimate` resolves the ammunition's `AmmunitionParameters` and calls `KinematicRaw`, which tries
each tier in order. The first tier returning a value above `MinValidSeconds` (0.01 s) wins.

```
Estimate
 ├─ KinematicRaw
 │   ├─ Tier 1: IntegratedEndTime        grounded step integrator     (beta only)
 │   ├─ Tier 2: WaypointSim.EndTime      ported public shot sim       (beta only)
 │   └─ Tier 3: MaxRangePreciseEndTime   the game's own estimator     (both branches)
 └─ straight-line at max speed           last resort                  (both branches)
```

A tier returns −1 when it cannot produce a trustworthy number: a missing reflection handle, an
out-of-range flight, a stalled missile. The chain then asks the next tier, so a degraded tier hands
the shot down rather than corrupting it.

### Branch behaviour

The game's shot-simulation API differs between Sea Power branches. `FlightTime.EnsureSimLookup`
detects which is loaded at startup: it looks for `Missile.SimulateShotLinear` (the public signature),
and if that method is absent it resolves `MissileSimulator.EstimateShot`, marks the session as beta,
and resolves the integrator's and waypoint sim's reflection surfaces.

| tier | public branch | beta branch |
|---|---|---|
| 1: grounded integrator | gated off (`_simIsBeta` false) | **primary** |
| 2: ported waypoint sim | not wired | middle fallback |
| 3: game estimator | **primary**: `MaxRangePrecise` drives the game's own `SimulateShotLinear` | last resort |
| 4: straight line | last resort | last resort |

On the public branch the game's own simulator is accurate and the mod defers to it. On beta the
built-in `EstimateShot` measures ~30 s off on lofting missiles; its Chebyshev speed fit smears sharp
speed transitions and underestimates loft arcs, which is why the integrator exists.

## When tier 1 declines

The integrator returns −1, handing the shot to tier 2, when:

| condition | reason |
|---|---|
| session is not beta | the game's own sim is better; gate |
| thrust handle unresolved (or drag, for kinematic ammo) | cannot call the game's physics |
| arrival speed below `MinVelocity × 1.1` | the round would stall short; not a trustworthy time |
| speed falls below 1 kn mid-flight | stalled |
| `t` reaches `_maxFlightTime` without intercept | out of range |
| any exception | fail-soft, logged only when verbose |

## Return contract and caching

- **`0` means unknown**, never an instant arrival. Callers treat any value at or below
  `MinValidSeconds` as unavailable.
- Results are cached per `(UnitId, AmmoFile, TargetId)` for 0.5 s of real time (capacity 512,
  expired-first eviction). The per-frame release gate would otherwise re-run a full simulation every
  frame. Declined and negative results are cached too, so a dead tier is not retried inside the
  window.
- The straight-line fallback uses a **separate** cache, so a cached fallback can never be misreported
  as a kinematic result.
- All caches clear on mission reset (`Coordinator.Reset`).

## File map

| file | role |
|---|---|
| `Simulation/FlightTime.cs` | entry point, tier chain, caching, reflection lookup |
| `Simulation/FlightTime.Integrator.cs` | tier 1, the integrator |
| `Simulation/WaypointSim.cs` | tier 2, the ported public shot sim |
| `Diagnostics/LaunchDiagnostics.cs` | observation of real flights for comparison |
