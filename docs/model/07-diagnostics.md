# 7. Diagnostics

[← Accuracy](06-accuracy.md) · [index](00-index.md)

Every line below is gated behind the mod's **`VerboseLog`** config option and is silent in normal
play; the per-frame telemetry is heavy enough to cause disk-I/O stutter. Output goes to the BepInEx
log, prefixed `[AutoTOT]`.

Telemetry is scoped to missiles AutoTOT fired (`EngagementBoard.IsCoordinated`), so planning
churn and defensive-SAM launches do not drown the log.

## 7.1 The lines

### Model side

| line | content |
|---|---|
| `estimator` | two lines at startup, never gated by `VerboseLogging`: which tier is bound (`integrator ACTIVE` or `UNAVAILABLE` with the reason), and whether the loop runs on worker threads or the main thread |
| `sim-init` | which reflection handles resolved |
| `sim-launch` | one line per shot: `launchPitch`, `initPhase`, `turnRate`, `loftAlt`, `descentDeg`, `onsetDeg`, `bearingErr`, `range`, `iniPitch` (the `.ini` value, for comparison), `railAz` |
| `sim-track` | the model's own state: `t` / speed / altitude / pitch / `hdgErr` / `roll` / drag / phase / flat distance / slant |
| `stage-model` | the model's phase boundaries: `finalDist`, `termDist`, `diveStart`, `loftAlt`, `onsetDeg` |
| `launch-rail` | every candidate launcher transform (`gunObj` / `containerBase` / `mount`), the `fixedRail` verdict, the `predicted` launch angle, `railAz`, and the rotatable / joined / elevation-arc flags |
| `wp-track` | the tier-2 waypoint sim's own state, for tier comparison |

### Reality side

| line | content |
|---|---|
| `track` | the **live** missile's speed / altitude / stage / distance |
| `stage-obs` | the live missile's own flight-stage transitions, one line per change: `prev -> next`, sim time, flat and slant distance, altitude, speed |
| `drag-break` | live `CalculateDrag` component split (aero / induced / gravity), the seeker's **lock HELD/DROPPED** state, and the `targetAlt` fed |
| `gap` | the outcome: `simEst` vs `actual`, plus `peakAlt`, `realPeakSpd`, `termSpd`, and `legacyEst`. Replaced by `SKIPPED, seeker switched` when the round struck a ship other than the one it was ordered against, since that difference measures formation geometry rather than the estimator |
| `impact` | flight time, final range, and `[RETARGETED -> <ship>]` when the seeker switched ships mid-flight |
| `stage-src` | the game's own `CreateWaypointConfigs` plan, dumped per config |

## 7.2 Sampling cadence

`sim-track` and `track` share three tiers, so the two series can be read against each other directly:

| window | interval | why |
|---|---|---|
| t+0 → t+5 | **0.25 s** | the launch nose-over completes in ~2 s; 1 s sampling gives two samples of it |
| t+5 → t+20 | **1 s** | the launch phase, where fixed offsets are created |
| beyond t+20 | **7 s** | cruise |

Two properties of this schedule are deliberate:

- **Timestamps print to one decimal**, so sub-second samples can be aligned across series. Bucketing
  them by whole second manufactures phase artefacts against a fast speed ramp.
- **7 s is not a round number.** A sampler must not share a period with what it samples: a 15 s
  sampler once aliased a limit cycle of period exactly 15.0 s and reported a 10 km altitude
  oscillation as a constant altitude.

`launch-rail` is the exception to everything above. It reports pure launcher **geometry**, with no
dependence on a missile flying or on firing at all, so it is emitted from the *planning* path:
selecting a target dumps every launcher on the ship. It is keyed per (unit, ammunition) and
re-emitted only when the rail **moves** more than 2°, so a trainable mount's slew from
parked to firing elevation is captured in a few lines while a fixed rail logs once and goes quiet.

## 7.3 Reading them together

**`stage-obs` against `stage-model`** is the boundary instrument. The model derives its phase
boundaries from `.ini` fields; `stage-obs` reports where the real missile changed stage. The
real `MaintainLoftAlt → Maintain{SeaSkimming,FinalFlightAlt}` distance is what `finalDist` should be,
and the real `→ TerminalApproach` distance is what `diveStart` should be.

**`sim-track` against `track`** compares model to reality step by step.

> **`track` logs 3D slant distance; `sim-track` logs flat distance.** `stage-obs` prints both. On a
> missile 80 km up these differ enormously, and comparing one column against the other is the single
> most repeated mistake in this project's history.

**`drag-break`** calls the 13-argument `CalculateDrag` overload with the live missile's exact state
and the mover's own `targetAltitude` rule, so its `induced` column is the real vacuum-brake magnitude
sample by sample.

## 7.4 Cost, and the `Profiling` switch

`Profiling` (a separate config key from `VerboseLogging`) emits one report every 60 frames. It exists
to answer where the mod's time goes, and it is the only honest way to judge that: unaided guesses
have been wrong every time it has been checked.

```
[AutoTOT Profiling] 60 frames: tick 243.7ms total, 4.062ms avg, WORST 26.99ms (release 4.33ms)
  frame 31.66ms avg (32 fps), worst 150.14ms => AutoTOT 8.50ms/frame = 26.8% (tick + UI)
  release staleness: 46 released, estimate age 7.57s avg, 17.68s worst (sim seconds)
  Diag ... | Commit ... | Anchor ... | Release ...
    -> FlightTime.Estimate: 185.9ms over 519 calls
       hits: 411 calls @ 0.000ms | misses: 108 calls @ 1.721ms avg
       model: 132 sims, 574855 steps (4354 avg), setup 0.7ms + loop 238.7ms, 415us/1k steps
       tiers: integrator 132, waypoint 0, maxRange 0, failed 0, integrator declined 0
    -> UI (outside tick): ...
```

Reading notes:

- **Window totals lead, per-frame averages follow.** The work arrives in rare bursts, so a per-frame
  average of a one-off 1.8 ms sim rounds to nothing and hides it.
- **`WORST` is the number that matters** for smoothness. An average over 60 frames conceals the single
  spike that drops a frame.
- **`UI (outside tick)` is counted separately** because the panel's estimates run in `OnGUI`, outside
  the tick entirely. The percentage on the frame line includes it; the tick figure does not.
- **`us/1k steps`** is the only figure comparable between builds. Record it before and after any
  change inside the integration loop.
- **`release staleness`** is how old the flight estimate was when an order actually fired. The
  per-frame sim cap trades freshness for frame time, and this is the cost side of that trade.
- **`VerboseLogging` distorts everything here.** It runs an extra full integrator sim per missile for
  the `int-phases` line, worth about 12.5 ms per frame in a large salvo against 0.03 ms with it off.
  Measure performance with verbose **off**.
- **`async`** reports the worker pool: how many simulations were queued and completed, how many the
  integrator declined, and the queue depth. A depth that stays near zero means the pool is keeping
  up. A depth that climbs during a salvo is the signal to raise `EstimatorThreads`.

## 7.5 Checking which estimator produced a number

Read the `estimator` lines before trusting any run. They are logged unconditionally because the two
ways this goes wrong are both silent.

The integrator needs the beta `MissileSimulator` internals. On the public branch it finds
`Missile.SimulateShotLinear` instead, binds nothing, declines every call, and every flight time comes
from `MaxRangePrecise` instead. Nothing fails and nothing warns; the numbers are simply worse. A full
measurement run was lost this way, because the only line that would have shown it was behind
`VerboseLogging`, which performance runs require to be off.

The second is the threading mode. `EstimatorThreads = 0` and asynchronous operation produce the same
flight times, so a log with no marker cannot say which one ran.

`tiers:` on the profiling line is the corroborating check: `integrator N, waypoint 0, maxRange 0` is a
healthy beta run. `integrator 0` with everything in `maxRange` and a matching `integrator declined`
count means the integrator answered nothing.

`VerifySolve` settles correctness of the threaded path directly. It re-runs every threaded simulation
on the main thread and compares bitwise, so `verify N checked, 0 MISMATCHED` proves the snapshot
carries every value the loop reads. It doubles the simulation work, so it belongs in a correctness
run and never in a timing one.
