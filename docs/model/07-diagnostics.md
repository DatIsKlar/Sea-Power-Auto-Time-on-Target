# 7. Diagnostics

[← Accuracy](06-accuracy.md) · [index](00-index.md)

Every line below is gated behind the mod's **`VerboseLog`** config option and is silent in normal
play — the per-frame telemetry is heavy enough to cause disk-I/O stutter. Output goes to the BepInEx
log, prefixed `[AutoTOT]`.

Telemetry is scoped to missiles AutoTOT actually fired (`EngagementBoard.IsCoordinated`), so planning
churn and defensive-SAM launches do not drown the log.

## 7.1 The lines

### Model side

| line | content |
|---|---|
| `sim-init` | which reflection handles resolved |
| `sim-launch` | one line per shot: `launchPitch`, `initPhase`, `turnRate`, `loftAlt`, `descentDeg`, `onsetDeg`, `bearingErr`, `range`, `iniPitch` (the `.ini` value, for comparison), `railAz` |
| `sim-track` | the model's own state: `t` / speed / altitude / pitch / drag / phase / flat distance / slant |
| `stage-model` | the model's phase boundaries: `finalDist`, `termDist`, `diveStart`, `loftAlt`, `onsetDeg` |
| `launch-rail` | every candidate launcher transform (`gunObj` / `containerBase` / `mount`), the `fixedRail` verdict, the `predicted` launch angle, `railAz`, and the rotatable / joined / elevation-arc flags |
| `wp-track` | the tier-2 waypoint sim's own state, for tier comparison |

### Reality side

| line | content |
|---|---|
| `track` | the **live** missile's speed / altitude / stage / distance |
| `stage-obs` | the live missile's own flight-stage transitions, one line per change: `prev -> next`, sim time, flat and slant distance, altitude, speed |
| `drag-break` | live `CalculateDrag` component split (aero / induced / gravity), the seeker's **lock HELD/DROPPED** state, and the `targetAlt` actually fed |
| `gap` | the outcome: `simEst` vs `actual`, plus `peakAlt`, `realPeakSpd`, `termSpd`, and `legacyEst` |
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

`launch-rail` is the exception to everything above. It reports pure launcher **geometry** — no
dependence on a missile flying, or on firing at all — so it is emitted from the *planning* path:
selecting a target dumps every launcher on the ship. It is keyed per (unit, ammunition) and
re-emitted only when the rail actually **moves** more than 2°, so a trainable mount's slew from
parked to firing elevation is captured in a few lines while a fixed rail logs once and goes quiet.

## 7.3 Reading them together

**`stage-obs` against `stage-model`** is the boundary instrument. The model derives its phase
boundaries from `.ini` fields; `stage-obs` reports where the real missile actually changed stage. The
real `MaintainLoftAlt → Maintain{SeaSkimming,FinalFlightAlt}` distance is what `finalDist` should be,
and the real `→ TerminalApproach` distance is what `diveStart` should be.

**`sim-track` against `track`** compares model to reality step by step.

> **`track` logs 3D slant distance; `sim-track` logs flat distance.** `stage-obs` prints both. On a
> missile 80 km up these differ enormously, and comparing one column against the other is the single
> most repeated mistake in this project's history.

**`drag-break`** calls the 13-argument `CalculateDrag` overload with the live missile's exact state
and the mover's own `targetAltitude` rule, so its `induced` column is the real vacuum-brake magnitude
sample by sample.
