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
| `launch-rail` | every candidate launcher transform (`gunObj` / `containerBase` / `mount`), the `fixedRail` verdict, the `predicted` launch angle, `railAz`, and the rotatable / joined / elevation-arc flags. Also the resolved stage schedule (`loftHold`, `loftEntry`, `cruiseAlt`, `finalFlight`) and turn budget (`turn`, `launchTurn`, `gLimit`), so a traced shot records what the model thought it should fly without anyone re-reading the ini |
| `wp-track` | the tier-2 waypoint sim's own state, for tier comparison |

### Reality side

| line | content |
|---|---|
| `track` | the **live** missile's speed / altitude / stage / distance |
| `stage-obs` | the live missile's own flight-stage transitions, one line per change: `prev -> next`, sim time, flat and slant distance, altitude, speed |
| `drag-break` | live `CalculateDrag` component split (aero / induced / gravity), the seeker's **lock HELD/DROPPED** state, and the `targetAlt` fed |
| `gap` | the outcome: `simEst` vs `actual`, plus `peakAlt`, `realPeakSpd`, `termSpd`, and `legacyEst`. Replaced by `SKIPPED, seeker switched` when the round struck a ship other than the one it was ordered against, since that difference measures formation geometry rather than the estimator |
| `gap-sub` | the same measurement for a round whose seeker switched, taken against the ship it actually hit. Deliberately a separate name: it is **approximate** and must never be recorded in the accuracy table. The substitute's position at launch is not recorded, so it is back-projected from the impact position, and the line prints how far it rewound and the tolerance that implies. Good to a few seconds, which is enough to catch the class of defect worth reporting |
| `impact` | flight time, final range, and `[RETARGETED -> <ship>]` when the seeker switched ships mid-flight |
| `stage-src` | the game's own `CreateWaypointConfigs` plan, dumped per config |

## 7.2 Sampling cadence

`sim-track` and `track` share three tiers, so the two series can be read against each other directly.
The schedule has one definition, `Diagnostics/TelemetryCadence.cs`, which both traces read: the two
series are only comparable while they agree, and two copies of a cadence agree until someone tunes
one of them.


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
from `MaxRangePrecise`. That is correct behaviour, and it is invisible in play: nothing fails, the
mod still coordinates, and only the timing precision drops. A full measurement run was lost to it
once, because the only line that would have shown it sat behind `VerboseLogging`, which performance
runs require to be off. The `estimator: integrator UNAVAILABLE` warning exists for exactly that case
and is logged whatever the verbose setting.

That warning names `MaxRangePrecise` alone. The waypoint tier cannot cover the public branch:
`WaypointSim.EnsureLookup` runs only from the beta branch of `EnsureSimLookup`, so off beta the type
never initialises and tier 2 is skipped.

The second is the threading mode. `EstimatorThreads = 0` and asynchronous operation produce the same
flight times, so a log with no marker cannot say which one ran.

`tiers:` on the profiling line is the corroborating check: `integrator N, waypoint 0, maxRange 0` is a
healthy beta run. `integrator 0` with everything in `maxRange` and a matching `integrator declined`
count means the integrator answered nothing.

`VerifySolve` settles correctness of the threaded path directly. It re-runs every threaded simulation
on the main thread and compares bitwise, so `verify N checked, 0 MISMATCHED` proves the snapshot
carries every value the loop reads. It doubles the simulation work, so it belongs in a correctness
run and never in a timing one.

## 7.6 Submarine ascent model

`SubmarineAscent.cs` steps the game's own vertical model to predict how long a submarine needs to
reach its weapon's launch depth. The game drives depth with two mutually exclusive mechanisms, and
which one is active changes the answer several-fold:

```
err       = targetDepth - depth                              // unity units, + = go deeper
ballTgt   = clamp(err, +/-_maxDepthChangeUsingTanks)
ballast  -> ballTgt at _ballastTankChangeRate per second
normSpd   = |vKnots| / _maxForwardVelocitySubmergedInKnots
depthFac  = clamp01(2 * depth)
tgtPitch  = normSpd * clamp(_maxPitchAngle * err, +/-_maxPitchAngle * depthFac)
pitch    -> tgtPitch at _pitchChangeRate per second
if (normSpd > 0.1 && |pitch| > 5)  ballast = 0              // THE INTERLOCK
depth    += (ballast + vKnots * KnotsToUnity * sin(pitch)) * dt
```

The pitch term is not applied inside `applyDepth`. The boat translates along its local forward
vector (`Submarine.cs:704`, `TransformDirection` then `Translate` in world space), so a pitched hull
carries vertical motion, and `applyDepth` then reads the resulting y and adds only the ballast
delta. The two compose, which is why both appear in the step above.

### Measurement evidence

Measured against two real ascents, both hulls declaring the same parameters:

- Echo II, slow, 170 to 75 ft: below the interlock, so ramp-limited ballast alone. Closed form
  `sqrt(2d/a)` with `a = 0.055 ft/s^2` gives 58.7 s against ~56 s observed.
- Oscar, under way, 350 to 75 ft: ballast alone covers 86 ft of the 275 ft travelled and the
  observed 6.1 ft/s peak exceeds the 5 ft/s ballast cap outright, so the interlock had
  zeroed ballast and the boat was flying its planes.

A single rate constant fits neither; this fits both. The apparent 3x spread was a regime
boundary, not noise.

### The interlock

The interlock zeroes the ballast state, not just the current step's contribution, exactly as the
game does (`_currentDepthChangeUsingTanks = 0f`). Getting this wrong is invisible in steady state
and costs ~10% on the total: with the state left charging in the background, ballast is released
fully wound up at its 5 ft/s cap the instant pitch drops below the threshold near the target, and
the boat finishes far too fast. It must ramp again from zero at `_ballastTankChangeRate`,
which on a deep run is a minute of the approach. Measured against 13 real manoeuvres this single
behaviour moved mean error from 4.5% to 1.4%.

### Time compression

This is compression-invariant, which matters because players run the game fast. The game keeps
these same equations at every compression and only grows its own step
(`fixedDeltaTime = Time.fixedDeltaTime * TimeCompression / _physicsTimeScaleCap`, `GameTime.cs`),
adding anti-overshoot clamps once that step is large. Both ramps here are linear in time, so a
coarse Euler step integrates them exactly; only `sin(pitch)` is nonlinear and it varies slowly.
Simulated against the game's own step sizes the answer moves <0.2% out to 300x and <1% at
1000x, inside the 1.4% validation error, so this needs no compression-dependent behaviour.

### Known limit

Speed is held constant for the whole ascent. A boat re-ordered to a new speed mid-climb will
diverge, and nothing here notices.

## 7.7 Vertical profiler

`VerticalProfiler.cs` captures how a platform changes depth or altitude, off by default. This is
a research tool, not a runtime diagnostic.

### Why separate from the asc-* lines

The `asc-*` lines only fire while an AutoTOT order is pending, which is useless for modelling: to
characterise a hull you want to order a depth or altitude change with no engagement at all and
watch the response. The profiler samples every submarine and aircraft whenever its commanded
altitude differs from where it is, and stops when it settles, so one dive or climb produces one
labelled block that can be pulled straight out of the log.

Sampling is deliberately fast (1 s) because an aircraft crosses its whole envelope in the time a
submarine moves a few feet. Each block carries a run number so several manoeuvres in one mission
stay separable.

### What the trace captures

A trace is only worth having if the model's own variables are in it. The two game models take
different inputs:

- **Submarine** (`Submarine.applyDepth`) is a proportional controller on depth error with a
  ballast/dive-plane interlock, so it needs depth, commanded depth, pitch, speed and the
  tank rate. See `SubmarineAscent`, which already implements it.
- **Aircraft** (`FixedWingFlightPhysics`) has two paths. The approximation path is a constant
  `MoveTowards` at `_maxClimbRate` (`FixedWingFlightPhysics.cs:361`). The full path is
  energy-based: `sin(climbAngle) = clamp01((thrust*lapse - drag)/weight + speedTrade)`,
  optionally capped at `_maxClimbRate/V` (`FixedWingFlightPhysics.cs:800-816`). Which one runs
  changes the answer completely, so the card records the approximation flag, and mass and
  speed are sampled because thrust-to-weight drives the energy path.

### Opening thresholds

Aircraft hold altitude to within a few feet and never sit exactly on the commanded value,
so a 3 ft opening test turned station-keeping jitter into 321 junk blocks out of 330 in
the first session. A run has to be worth measuring before it is worth opening. The opening
thresholds are 50 ft for aircraft and 5 ft for submarines.

### Position snaps

The game teleports a unit on spawn and on altitude correction (`transform.position = ...DesiredAltitude`),
and the first session logged peaks of 1372 and 1612 ft/s, above the airframes' own declared maxima.
Samples beyond a plausible ceiling are dropped from the rate statistics and counted, so a
snap-heavy run is visibly suspect rather than quietly biased.

### Reading `turn`, `launchTurn` and `gLimit`

`turn` is the ammunition's `MaxTurnRate`. The two beside it are the corrections that can move it,
and both print `none` for almost every round:

- `launchTurn` shows the ToBearing floor from `LaunchTurnRate`, or `none` when the ini leaves it at
  its default. Three shipped ammunition set it, `usn_rgm-84a` among them.
- `gLimit` shows the speed at which the G-derate STARTS cutting the rate, or `none` when no limit can
  apply. Read it against the speeds on the same shot's `sim-track` lines: a `gLimit` above every
  speed the round reached means the derate never fired. No anti-ship missile can reach its own limit.

A shot printing `none` for both is one where neither correction can have moved it, so a residual
there has some other cause.
