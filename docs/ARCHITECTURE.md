# AutoTOT Architecture

How the mod turns a handful of separate missile orders into one simultaneous impact.

## The pipeline

```
 player order (game UI)
      │
      ▼
 InsertEngageTask Harmony prefix ──(not interceptable)──► normal game launch
      │ (player missile order, valid weapon/target match)
      ▼
 Coordinator.TryIntercept ── order held, added to the target's open Batch
      │
      │  real-time debounce (GroupWindowSeconds) or hard cap (MaxCollectSeconds)
      ▼
 CommitBatch
      ├─ PrepareIntent: per order → release lead, grouped flag, reload waves
      ├─ pick ANCHOR = longest (flight time + release lead)
      └─ Schedule: shared impact time = now + that longest need
      │
      │  every frame (Coordinator.Tick)
      ▼
 UpdateAnchorTracking ── anchor's REAL launches observed; shared impact time
      │                   rewritten live (observation anchoring)
      ▼
 ReleaseDueLaunches ── per held shot: release when
      │                 timeLeft ≤ liveFlightTime + releaseLead + startupLead
      │                            + groupDelay + ½·simStep
      ▼
 Fire ── InsertEngageTask with Bypass=true (the mod's own call, not re-intercepted)
      │
      ▼
 LaunchDiagnostics ── observes airborne missiles, tallies launches vs. requested,
                      feeds the anchor's observed launch times
```

Two entry paths feed the same pipeline:

- **Automatic mode** (Alt+T armed): the Harmony prefix intercepts normal player
  orders and defers them into batches grouped by shared target.
- **Planner panel** (Alt+G): `Coordinator.FireCoordinated` builds the same
  Intent/Schedule structures directly from hand-picked shots, bypassing the
  collection window.

### Interceptability gates

`Coordinator.TryIntercept` (Coordinator.cs) is called from the Harmony
prefix on every `InsertEngageTask`. It returns `true` (order deferred into a
batch) only when all of these hold:

| Gate | Condition | Rationale |
|---|---|---|
| 1 | `Enabled` and `Active` | Master config switch and runtime toggle both on |
| 2 | Not `autoAttack` | Only player-issued orders are coordinated |
| 3 | `unit.IsPlayerObject` | Friendly units only |
| 4 | Ammo is a missile (`ap._type == Ammunition.Type.Missile`) | Artillery, torpedoes, etc. pass through |
| 5 | `DoesAmmoMatchTarget` | Weapon can engage this target class |

Any gate failing returns `false` and the original method runs unmodified. The
order is appended to `_openBatches[target]` (Coordinator.cs), keyed by
the target object reference.

### Re-validation at fire time

`Fire` (Coordinator.cs) re-checks that the unit and target are still
alive before issuing the deferred launch. If either is null or destroyed, the
order is silently dropped. This handles the case where a held order's shooter
or target dies during the stagger.

## Batch collection and commit

### Collection window

Orders accumulate in `_openBatches` until one of two conditions triggers commit
(Coordinator.cs):

- **Debounce**: `(now - LastRealTime) >= DebounceSeconds` (0.75 s)
- **Hard cap**: `(now - FirstRealTime) >= MaxWindowSeconds` (6.0 s)

Both use `Time.unscaledTime` (Unity unscaled real time), so the collection
window advances during game pause and is unaffected by time compression. The
config keys are `GroupWindowSeconds` and `MaxCollectSeconds` (Bootstrap.cs,
217), wired into the fields at Bootstrap.cs.

### Anchor selection

`CommitBatch` (Coordinator.cs) calls `PrepareIntent` for every item in
the batch, then `PickAnchor` selects the anchor (the planner path,
`FireCoordinated`, calls the same helper). The anchor is the item with the
strictly greatest enroute-with-lead total, computed inline in `PickAnchor`
(Coordinator.cs):

```
enroute = FlightTime.Estimate(unit, ammoId, target)
        + ReleaseLead
        + StartupLead
        + GroupDelay(unit, ammoId, target, ReleaseLead)
```

On a tie, the first item in `b.Items` order wins (the first maximum encountered
is kept). The base impact time is fixed at commit: `GameClock.SimNow() + maxEnroute`
(Coordinator.cs).

## Time model

The mod uses three distinct time bases. Mixing them up is the most common source
of subtle bugs.

| Time base | Source | Where used |
|---|---|---|
| Unscaled real time | `Time.unscaledTime` | Batch collection windows, TTL caches, mod-menu gate deadline |
| Simulation time | `GameClock.SimNow()` (beta: `GameTime.missionElapsedTime`, fallback old `GameTime.time`) | Everything after commit: impact times, stalls, releases, LaunchTimes, board grace, expectation deadlines |
| Frame count | `Time.frameCount` | HUD row cache invalidation |

### Pause and time compression

**Pause**: sim time stops advancing, so `timeLeft` never shrinks and no
releases fire while paused. The `½·simStep` lookahead contributes zero because
`simStep = 0`. Meanwhile the collection debounce and cap keep running on
unscaled time, so batches can still lock in during pause (they cannot
release until sim time resumes).

**Time compression**: sim time advances faster than frames. The
`½·simStep` lookahead (Coordinator.cs) compensates the late-bias of
evaluating flight time a fraction of a (sim) step before the missile
launches. `Mathf.Max(0f, ...)` guards against negative deltas.

## Open-loop scheduling

The mod never guides missiles. It only decides WHEN each order is handed to the
game. The shared impact time is fixed at commit (then refined by the anchor), and
each held shot releases live against it using a fresh flight-time estimate every
frame. Drift of shooter or target during the stagger is absorbed because the
release condition re-evaluates `liveFlightTime` every tick; a `½·simStep`
lookahead absorbs motion during the release frame and corrects time-compression's
late-bias (simStep is measured in sim time, so pause adds no lookahead).

## Release lead: half-span vs full-span

A launcher ripples N rounds over `(N−1)·interval` seconds; the release lead is how
long BEFORE the coordinated impact that ripple must start
(`Coordinator.PrepareIntent`):

| Salvo kind | Arrival shape | Lead |
|---|---|---|
| Independent (no missile group) | arrivals spread across the ripple | **half** the span → centers arrivals on the TOT |
| Grouped (`_maxGroupSize > 1`) | one convergent impact at the ripple's **trailing edge** | **full** span |

Why grouped salvos land near the trailing edge: `MissileGroup.AdjustMembersVelocities`
(MissileGroup.cs:106-141) applies ±40% speed clamps: the leader sheds up to 40% while
stragglers lag, and the farthest trailer gains up to 40%. The group cashes in together
once formed (Missile.cs:839-842). The baseline is **convergent impact = last launch +
that round's lone flight time**.

### Group-drag correction: `groupDelay`

The baseline above under-predicts, because the leader spends a real interval throttled
to 0.6× stage speed while the ripple forms, so the GROUP flies slower than the solo
kinematic estimate. `FlightTime.GroupFormingDelay` adds a range-aware **τ_form** term,
computed per shot from the game's OWN shot speed profile (`SimulateShotLinear`) plus the
observed launch span, with no per-type constants:

```
P(t)      = cumulative distance from the sim speed profile
tauForm   = time when P(t) reaches 2.5·P(span)     (2.5 = 1/0.4, leader-0.6v vs straggler-1.0v closing)
groupDelay = max(0, 0.4·tauForm − span)            (0.4 = the −40% leader throttle)
```

It is range-aware for free: a flat profile gives `tauForm = 2.5·span` ⇒ delay 0; a lofting
missile that has descended to slow final-flight by `2.5·P(span)` (short range) stretches
`tauForm` ⇒ positive delay; at long range it is still in fast loft there ⇒ delay ≈ 0.
Validated in-game (SS-N-19) to ~±2 s at mid/long range. `groupDelay` is 0 for non-grouped
ammo (`_maxGroupSize ≤ 1`), so it never affects independent salvos.

**Known limitation** (deferred): at close
range the terminal seeker trips before the group finishes forming (it cashes in near the
LEADING edge), so the full-span trailing-edge assumption over-predicts and the salvo lands
~10–20 s early. Salvos still converge; only close-range ETAs are affected.

## Release formula

`ReleaseDueLaunches` (Coordinator.cs) evaluates every scheduled item every tick.
The release condition is:

```
timeLeft   = s.ImpactAtSim - simNow
flightNow  = FlightTime.Estimate(it.Unit, it.AmmoId, it.Target)
groupDelay = GroupDelay(it, it.ReleaseLead)

release when:
timeLeft <= flightNow + it.ReleaseLead + it.StartupLead + groupDelay + lookahead
```

Terms:

- **`flightNow`**: live kinematic flight-time estimate at current geometry, refreshed
  every tick (TTL-cached 0.5 s real time per shooter/ammo/target).
- **`it.ReleaseLead`**: ripple-centering lead (half-span for independent, full-span for
  grouped).
- **`it.StartupLead`**: fixed offset paid once before round 1, computed as
  `PreLaunchDelay + ½·MaxReactiontime` (LauncherFacts.cs). `PreLaunchDelay` is
  the fixed wait after hatch opens (INI field, default 0). `MaxReactiontime` is the
  random reaction delay re-rolled per engage as uniform `[0, MaxReactiontime]`; only its
  expected value (half) can be taken.
- **`groupDelay`**: the group-drag term above (0 for independent salvos).
- **`lookahead`**: `0.5 * simStep`, where `simStep = simNow - _lastReleaseSimNow`
  (Coordinator.cs). `simStep` is measured in sim time, so pause adds no lookahead.
  `_lastReleaseSimNow` is reset to `-1f` on `Reset()` (Coordinator.cs), so the first
  post-reset tick computes no lookahead.

## Launcher facts deep-dive

`LauncherFacts.Compute` (LauncherFacts.cs) derives the cadence, ready rounds,
reserve, and reload timing for a given ship/ammo pair. It is the source of truth for
`ShotInterval`, `StartupDelay`, `ReloadGap`, `ReadyRounds`, `Reserve`, and `PerContainer`.

### ShotInterval derivation

The derivation chain (LauncherFacts.cs) has four stages:

1. **Base interval**: if `_salvoFireAmount > 1`, use `_salvoFireTime` (within-salvo ripple
   spacing). Else `60 / _fireRatePerMinute` if `_fireRatePerMinute > 0`, else 0.
2. **Shared-launch-interval gate**: the game gates each launch on BOTH the fire-rate timer
   and a per-SystemName shared timer (WeaponSystemLauncher.cs:633-642). If
   `ship._sharedLaunchIntervals[sysName] > interval`, use the shared value. Example:
   Slava's SS-N-12 declares `SharedLaunchInterval=5` shared across port+starboard
   launchers; without this the interval reads ~5× too fast.
3. **Hatch-open animation floor**: applies only when `_salvoFireAmount <= 1` AND the
   launcher has multiple containers (per-tube-hatch). `hatch = MaxHatchOpenSeconds(launchers[0])`;
   if `hatch > interval && hatch < 60`, use `hatch`. This only ever raises the cadence,
   never lowers a declared one. Motivation: Kirov's SS-N-19 declares no cadence field;
   the duration lives in the animation asset, not a numeric field.
4. **Guard**: NaN/Infinity/negative → 0.

`MaxHatchOpenSeconds` (LauncherFacts.cs) walks the field chain:
`WeaponSystem._containers` → `WeaponContainer._openAnimation` → `ObjectCodeAnimation._sequences`
→ each sequence's `_sequenceData` → last keyframe's `_time`. Takes the max across all
containers and sequences.

### StartupDelay

`StartupDelay = PreLaunchDelay + ½·MaxReactiontime` (LauncherFacts.cs). Paid ONCE
before round 1, not between rounds. Belongs in release lead as a fixed offset, not in
`ShotInterval`.

### ReloadGap

`ReloadGap = PerContainer ? 0 : _magazineReloadTime` (LauncherFacts.cs).
Per-container/VLS cells reload in parallel, so no whole-launcher gap. Otherwise the
magazine reload time field.

### Ready rounds vs reserve

Summed across ALL launchers serving the ammo (LauncherFacts.cs):

- **`ReadyRounds`**: `getLoadedAmmoCount(ammoId)`, the game's LOGICAL seated tally
  (includes SpawnWhenNeeded launchers that keep spawned missile objects near 0 even when
  fully loaded, and over-slot surplus).
- **`Reserve`**: `getMagazineAmmoCount(ammoId)`, rounds in the magazine behind the rails
  that a reload would pull from.

Both clamped `Mathf.Max(0, ...)`. `AvailableRounds = ReadyRounds + Reserve` when Valid,
else `int.MaxValue` (so callers don't clamp on missing data).

### INI field list

Exact game-API field names used in `LauncherFacts.Compute`:

`GetWeaponSystemsForAmmunition`, `_vwp`, `_perContainerReload`, `_salvoFireAmount`,
`_salvoFireTime`, `_fireRatePerMinute`, `_systemName`, `_sharedLaunchIntervals`,
`_containers`, `_preLaunchDelay`, `_maxReactiontime`, `_magazineReloadTime`,
`getLoadedAmmoCount`, `getMagazineAmmoCount`, `_openAnimation`, `_sequences`,
`_sequenceData`, `_time`.

## Observation anchoring

The realized launch cadence of many launchers is produced by machinery no INI
declares as a cadence field (per-cell hatch animations, engage-task reassignment).
The Kirov's SS-N-19 realizes ~3.9 s/round while its INI fire-rate implies
1 s/round. Once launches are observed, measuring it is trivial. So:

1. The batch's anchor (longest enroute incl. lead) releases first.
2. `LaunchDiagnostics` sees each missile leave the rail (`WeaponBase._launchTime`)
   and appends the time to the anchor's `LaunchTimes`.
3. Every tick, `UpdateAnchorTracking` rewrites the shared impact time every held
   order syncs to:

```
interval  = (lastLaunch − firstLaunch) / (k−1)      once k ≥ 2 observed launches,
            else the INI interval (a-priori seed)
lastRound = lastLaunch + interval·(n − k)            while the ripple is incomplete,
            else lastLaunch
impact    = lastRound + liveKinematicEstimate − centering + groupDelay
            centering = releaseLead (independent) or 0 (grouped)
            groupDelay = the group-drag term above (0 for independent salvos)
```

4. Finalizes when wave 1 has fully launched (`k ≥ n`) or launches stall
   (no launch for `max(4×cadence, 30s)`; or nothing at all for 120s).

**A-priori cadence for anchor SELECTION.** Anchor selection happens at commit, before
any launch is observed, so it can't use the measured cadence. To keep the right order
leading, `LauncherFacts.Compute` floors the a-priori `ShotInterval` with the launcher's
hatch-open animation duration (`max(declared, hatchOpenSeconds)`) for per-tube-hatch
launchers that declare no cadence field, e.g. the SS-N-19's ~3 s shaft-hatch animation,
which its INI omits. This only RAISES an unset cadence, never overrides a declared one,
so it's a pure fallback with no effect on launchers that set their timing. Full derivation:
[ShotInterval derivation](#shotinterval-derivation).

Held orders track the running prediction: their `ImpactAtSim` is overwritten every
tick until the ripple finalizes, so their unchanged release formula tracks reality.

**Prediction cache.** `PredictAnchorImpact` is cached per anchor and reused while the
ripple state (observed launches `k`, measured cadence) is unchanged, on a **0.5 s
sim-time TTL** (`PredictCacheTtlSim`): an expired entry re-runs the prediction so the
live flight estimate keeps tracking shooter/target motion between launches. The cache
key is a `(k, interval)` struct. An earlier integer-mixed key collided once cadence
reached 10 s and had no TTL at all, which froze the prediction between launches.

## Reload waves

An order larger than the launcher's ready rounds fires in reload-separated waves
(only when a magazine reserve exists; all-tubes-ready launchers like the Slava
stay one wave). The first wave carries the anchoring; later waves arrive
`waveGap = readyRounds·interval + reloadGap` apart each, shown split out in the
ENGAGEMENTS overview.

## Flight-time estimates

Full model documentation lives in [`model/`](model/00-index.md).
This section only summarizes what the scheduling pipeline consumes.

`FlightTime.Estimate` runs a tiered chain and returns the first valid result:

| Tier | Estimator | Where it runs |
|---|---|---|
| 1 | Grounded step integrator (`FlightTime.IntegratedEndTime`): forward-Euler sim over the game's own thrust/drag helpers; models the trajectory shape itself | beta only, primary |
| 2 | Ported waypoint sim (`WaypointSim.EndTime`): reflection port of the public branch's `SimulateShotLinear` | beta only, middle fallback |
| 3 | Game estimator (`AmmunitionParameters.MaxRangePrecise`, `iterations=0`, reflection) | both branches; primary on public, last resort on beta |
| 4 | Straight-line max speed | both branches, last-resort bound |

On the **beta** branch the built-in `EstimateShot` measured ~30 s off on lofting
missiles, which is why the integrator is primary there. On the **public** branch
the integrator gates itself off (`_simIsBeta`) and the game's own
`SimulateShotLinear` drives timing. Each tier declines with −1 (missing
reflection handle, stalled missile, out of range) and the chain asks the next
one. `0` means unknown, never arrives-instantly; callers treat values at or
below `MinValidSeconds` (0.01 s) as unavailable.

### Where the integrator runs

One simulation costs about 1.8 ms over roughly 4,300 steps, so a synchronized salvo that refreshes
many estimates at once used to put 20 ms on a single frame. The integrator is therefore split in two:

- **Setup** (`FlightTime.Integrator.cs`) reads everything that comes from the live game: shooter and
  target transforms, target velocity, launcher rail orientation, and `_finalFlightPhaseAltUnity`,
  which the game rewrites every frame for each missile in flight. Main thread only.
- **Solve** (`FlightTime.Solve.cs`) is the step loop, taking a `SolveInput` snapshot of 41 values and
  touching no Unity API. It is a pure function, so the same input always yields the same float.

`EstimatorThreads` worker threads run Solve. The main thread queues a snapshot and keeps using its
previous estimate; `FlightTime.DrainCompleted`, called at the top of the tick, publishes finished
results into the cache. Only the main thread writes that cache, so there is still one estimator and
one writer. Commit and release read the same values they always did, computed at a different time.

Two cases stay synchronous. An order with no estimate at all computes immediately, because the anchor
releases almost as soon as it is scheduled. Verbose runs also solve inline, since the loop writes
`sim-track` lines and concurrent logging would interleave them.

Asynchronous rather than a parallel loop joined inside the tick: the game already schedules Unity
`IJobParallelFor` batches and blocks on `Complete()`, so it occupies most job workers during the
tick. A parallel loop competes with those for the same cores, and its speedup is bounded by spare
cores, which is what a slower machine lacks. Queueing removes the work from the frame whatever the
core count; a slow machine gets its answers a few frames later instead. Workers run at below-normal
priority so the game wins any contended core.

Setting `EstimatorThreads = 0` runs everything on the main thread and is the rollback.

`VerifySolve` re-runs each threaded simulation on the main thread and compares the two results
bitwise. 1,205 simulations across kinematic, non-kinematic and waypoint-planned rounds have been
checked this way with no mismatch.

Estimates are cached 0.5 s real time per `(UnitId, AmmoFile, TargetId)` key,
declined results included. The straight-line fallback lives in a separate cache
so it is never misreported as a kinematic result. `FlightTime.ClearCache()`,
called from `Coordinator.Reset()`, clears all of them.

The **speed profile** behind the group-drag correction (§Release lead) uses the
same plumbing: `ComputeSpeedProfile` invokes the branch's shot simulator and
records `(time, speed)` samples. When neither sim method resolves, the profile
is empty and `groupDelay` no-ops.

## Launch observation and shortfall detection

`LaunchDiagnostics` (LaunchDiagnostics.cs) has two subsystems sharing one tick: a flight
tracker and launch expectations.

### Airborne missile discovery

`Tick(simNow)` (LaunchDiagnostics.cs) is called every frame from `Coordinator.Tick`
(Coordinator.cs). It polls `Singleton<ObjectsManager>.Instance._listOfAllWeapons`
(LaunchDiagnostics.cs), the game's master weapon list. Filters per entry: skip
null/destroyed; require `w._type == ObjectBase.ObjectType.Missile` AND `w.IsPlayerObject`;
require `w.CurrentIntendedTargetObject` non-null and not destroyed.

### Launch crediting

First sighting of a friendly missile object in `_listOfAllWeapons` is the detection event
(LaunchDiagnostics.cs). The timestamp attributed to the launch is the game's own
`WeaponBase._launchTime` (LaunchDiagnostics.cs), not the mod's observation time.

`CreditLaunch` (LaunchDiagnostics.cs) matches the launched missile to an open
expectation: linear scan for the first expectation with `Launched < Requested`, matching
`Unit`, `Target`, and `AmmoFile` (the resolved `_ammunitionFileName`). On match:
`Launched++`, `LastLaunchSim = w._launchTime`. If the expectation is linked to an anchor
(`Linked != null && Linked.IsAnchor && !Linked.RippleDone`), append `w._launchTime` to
`Linked.LaunchTimes` (LaunchDiagnostics.cs). This is the only writer of the
anchor's `LaunchTimes` list.

### Impact reporting

Detection is disappearance-based: after the scan, the tracker is walked; any key where
`w == null || w.IsDestroyed || w._type != Missile` is collected (LaunchDiagnostics.cs).
For each vanished missile: `flightTime = LastSeenTime - LaunchTime`; outcome is `"HIT"` if
`LastDistM <= HitRangeM` (500 m), else `"ended"` (LaunchDiagnostics.cs). Residual is
printed only if `PredictedImpact >= 0f`: `residual = LastSeenTime - PredictedImpact`
(observed impact − anchor-finalized predicted impact). The predicted impact is stamped
from `EngagementBoard.TryGetPredictedImpact` at sample creation and survives target death
because the board row is pruned on death.

### Expectation registration

`RegisterExpectation` (LaunchDiagnostics.cs) is called from `Coordinator.Fire`
after the order is issued (Coordinator.cs). It computes:

```
ripple    = (shots - 1) * interval + Max(0, Waves - 1) * reload
waveTail  = (Waves - 1) * (AnchorShots * interval + reload)   when Waves > 1, else 0
DeadlineSim = GameClock.SimNow() + ripple + waveTail + ExpectationMarginSim (10 s)
```

`Linked` is set to the anchor's `Scheduled` entry only for anchors (LaunchDiagnostics.cs).

### Adaptive deadline

`FinalizeExpectations` (LaunchDiagnostics.cs) is called every tick. The measured cadence is
`(last − first) / (count − 1)` from the anchor's `LaunchTimes` (once count ≥ 2). The adaptive
deadline is `since + Max(4 * interval, 30 s) + WaveTailSim`, and it only ever extends.

`since` is the reference point, chosen in this order:

1. the order's own `LastLaunchSim`, if it has launched anything;
2. otherwise the **ship's** last observed launch of that ammunition, tracked in
   `_shipLastLaunchSim` and updated on every sighting including rounds that match no open
   order;
3. otherwise `RegisteredSim`, when the order was issued.

The question a shortfall answers is "has the **ship** stopped launching", not "has this order
started". A ship can hold many engage tasks at once and the game services them through
whatever launcher is free, so an order can sit queued for minutes while rounds are still
leaving the rails. Keying the deadline to the order's own first launch gave a one-shot order
10 s and no extension at all, which reported the tail of a queue as short while it was still
being worked through.

### Shortfall detection

Close-out (LaunchDiagnostics.cs): `done = Launched >= Requested`. If not done and
`simNow < DeadlineSim`, keep waiting. Otherwise: if `Launched >= Requested`, log "order
complete" (VerboseLog only). Else compute `targetGone` / `shooterGone`. If the shooter is
alive, gather `ready`/`reserve`/`inventory`. Severity split: if `targetGone || shooterGone`,
log "order ended early" (VerboseLog only). Else unconditional `LogWarning` "SHORTFALL".
SHORTFALL is the only unconditional (non-VerboseLog) log in LaunchDiagnostics. The detail line
also reports how long ago the ship last fired that ammunition, which distinguishes a launcher
that has stalled from one that is still working through a queue.

### Seeker re-targeting

These missiles carry their own active seeker and, unlike grouped rounds, do not split up. A round
sent at a ship deep in a formation locks onto whatever it detects first and strikes a ship in
front of the one it was ordered against. `w.CurrentIntendedTargetObject` follows that switch;
the order's assignment does not.

Each `FlightSample` therefore keeps both: `Target`, stamped at first sighting, and `CurrentTarget`,
refreshed every tick. When they differ at impact:

- the `impact` line is tagged `[RETARGETED -> <ship>]`, because "final range 4192 m" otherwise
  reads as a miss when the round in fact killed something else;
- the `gap` line is **replaced** by a `SKIPPED, seeker switched` line. A retargeted round's flight
  time is to the ship the seeker chose while `simEst` was computed for the ship it was assigned,
  so the difference measures formation geometry, not the estimator, and is worth tens of seconds.

Both comparisons use `ReferenceEquals`. `UnityEngine.Object` overloads `==` so a destroyed object
compares equal to null, and a ship sinking is precisely the case this detects.

## Engagement board and HUD

### Engagement board

`EngagementBoard` (EngagementBoard.cs) consolidates five former per-target dictionaries
(fired-at, impact time, impact spread, wave count, wave gap) into one row per target with
a single prune path.

#### Row model

`Engagement` class (EngagementBoard.cs) has fields: `FiredAtSim` (float, init −1;
sim time of last release at this target, `GameClock.SimNow()`; −1 = held only), `ImpactSim` (float, init −1;
scheduled or anchor-tracked live impact time; −1 = none), `ImpactSpread` (float; ± arrival
spread in seconds; independent salvos only, 0 for grouped), `Waves` (int, init 1;
reload-separated waves), `WaveGap` (float; sim seconds between successive wave impacts).

Storage: `_byTarget` `Dictionary<ObjectBase, Engagement>` (EngagementBoard.cs),
created lazily by `GetOrCreate` (EngagementBoard.cs).

#### Snapshot model

`SalvoLine` struct (EngagementBoard.cs) is one HUD row: `Target`, `Queued` (shots
still held), `InFlight` (friendly missiles in flight at target), `ImpactSim` (−1 if unknown),
`ImpactSpread` (± s), `Waves` (1 = single wave), `WaveGap`, `AnchorLaunched` (launches
observed so far), `AnchorTotal` (>0 while a batch anchor's ripple is tracked).

Snapshot scratch buffers are reused every call to avoid per-frame allocation: `_salvoMap`
(`Dictionary<ObjectBase, SalvoLine>`) and `_pruneScratch` (`List<ObjectBase>`)
(EngagementBoard.cs).

#### Write API

- `RecordScheduled(target, impactSim, impactSpread, waves, waveGap)` (EngagementBoard.cs):
  creates or overwrites the row. Called when a batch is scheduled (Coordinator.cs).
- `UpdateImpact(target, impactSim)` (EngagementBoard.cs): rewrites `ImpactSim` only.
  Called every coordinator tick while an anchor ripple is live (Coordinator.cs).
- `MarkFired(target)` (EngagementBoard.cs): stamps `FiredAtSim = GameClock.SimNow()`.
  Called from `Coordinator.Fire` (Coordinator.cs).
- `Drop(target)` (EngagementBoard.cs): removes the row only if the target never
  fired (`!HasFired`). Called from `Coordinator.DropImpactDataIfUnscheduled` (Coordinator.cs).

#### Consolidation and prune

`CollectSalvos` (EngagementBoard.cs) builds the snapshot in three passes:

1. **Held/anchor pass** over `Coordinator.ScheduledItems`: accumulate `Queued` for
   non-fired entries; for fired anchors with `!RippleDone`, set `AnchorLaunched` and
   `AnchorTotal`.
2. **In-flight pass** via `LaunchDiagnostics.ForEachInFlight`: count only for targets
   coordinated/fired (`HasFired(t)`), else any friendly missile at that contact
   would inflate the overview.
3. **Prune pass**: for each `_byTarget` entry, skip never-fired rows. `active = row exists
   in _salvoMap && (Queued > 0 || InFlight > 0)`. `inGrace = (now - FiredAtSim) < EngageGrace`
   (8 s sim seconds). Prune if `target == null || target.IsDestroyed || (!active && !inGrace)`.
   Destroyed targets are pruned immediately regardless of grace/activity.

### HUD

`Hud` (Hud.cs + partials Hud.Render.cs, Hud.Mouse.cs, Hud.Styles.cs) is an IMGUI planner
panel. It is a `MonoBehaviour` added to the same `DontDestroyOnLoad` GameObject as `Pump`
(Bootstrap.cs).

#### Data flow

`DrawWindowInner` (Hud.cs) calls `EngagementBoard.CollectSalvos(_salvos)` at the
top (Hud.cs), including while collapsed. The snapshot rebuild and the board's prune
pass run every HUD draw frame.

The panel shows:

- **Header**: chevron toggle, title "TIME-ON-TARGET", live engagement summary
  `"● {tgts} tgt / {rounds} msl"`, AUTO status.
- **Selection header**: TARGET row (fogged label or "click an enemy contact to set target"),
  SHOOTERS row (anchor name or "click one of your ships"), "whole formation" checkbox.
- **Missile rows**: per ship, per ammo: checkbox, ammo name, count, ETA, range, salvo
  stepper (–/+), reload warning if `WillNeedReload`. The stepper's increment comes from
  `SalvoStep()` (Hud.Render.cs), which reads `Event.current` modifiers: Shift → ±10,
  Ctrl → ±5, else ±1. The result is clamped `Mathf.Max(1, …)` / `Mathf.Min(r.Count, …)`
  so it lands on the launcher cell count. A dim hint line above the list advertises it.
- **ENGAGEMENTS section**: per target: fogged label, status (`"{Queued} queued"`,
  `"{InFlight} in flight"`, `"anchoring {AnchorLaunched}/{AnchorTotal}"`), arrival
  countdown (multi-wave or single-wave with ± spread).
- **Fire buttons**: "FIRE — TIME ON TARGET" (coordinated), "FIRE NOW\n(no sync)"
  (uncoordinated).

#### Fog-of-war-correct labels

`FoggedLabel` (Hud.cs) uses the same source the game uses for contact display.
Enemy contacts are looked up via `Globals._playerTaskforce?.PlottingTable?.VehicleForObject(o)`.
If not on the player's plot, "Unknown contact" (reveal nothing). If classified
(`v.Class.HasValue`), real name. If unclassified, `"Contact {v.Id}"` plus `" — " + v.IncomingSignalInfo()`
when `v.HasSignalInfo()`.

#### Mouse capture

`UpdateMouseCapture` (Hud.Mouse.cs) pins `MouseControlState._isMouseOverUIWindow`
via reflection to avoid the setter's per-frame `FindObjectsByType` (Hud.Mouse.cs).
The latch logic honors prior-frame hover and covers same-frame arrival with no hover frame.
Resize is driven by raw `Input` rather than IMGUI drag events, because IMGUI drag stops
being delivered once a fast cursor outruns the window rect (Hud.Mouse.cs).

#### DPI scaling

IMGUI draws in raw screen pixels and does not adapt to DPI, so the panel looked tiny at
4K. `EffectiveScale()` (Hud.cs) is the single source of truth: `Bootstrap.UiScale`
(config `[Interface] UIScale`, `0` = auto) resolves auto to `Screen.height / 1280`, then
multiplies by `Bootstrap.UiScaleMultiplier` (config `UIScaleMultiplier`, the footer
Scale –/+ control writes this via `Bootstrap.SetUiScaleMultiplier`), clamped 0.5–4×.
`OnGUI` wraps `GUI.Window` in `GUI.matrix = Matrix4x4.TRS(…scale…)` and does its clamps /
first-paint placement in scaled GUI space (`Screen.width/s`, `Screen.height/s`). On a
scale change the window's x/y are re-anchored by `_lastScale / s` so its top-left stays
put instead of drifting. **Gotcha:** the two Update-path handlers in Hud.Mouse.cs read
raw `Input.mousePosition` but compare against `_win` (scaled GUI space), so both divide
the converted point by `s` or hover/resize hit-testing is offset.

Alt+G toggles `_visible` (full hide, distinct from `_open`'s collapse-to-tab); while
hidden `OnGUI` early-outs and Update releases the over-UI capture (`SetOverUi(false)`).

#### Style policy

Scrollbar styles are applied only around the panel's own scroll view and restored, never
written to the process-global `GUI.skin` shared with the BepInEx console and other IMGUI
mods (Hud.Styles.cs, 177-179). The palette is sampled directly from the game's
own panels (Hud.Styles.cs).

## Bootstrap and lifecycle

### AnchorChain entry

`AnchorChainEntry` (AnchorChainEntry.cs) is the `[ACPlugin]` entry point. AnchorChain's
chainloader discovers the class via `[ACPlugin]` and calls `TriggerEntryPoint` when the
mod is enabled. The only action is `Bootstrap.InitIfEnabled()` (AnchorChainEntry.cs),
wrapped in try/catch; any exception is logged.

### Mod-menu gate

`InitIfEnabled` (Bootstrap.cs) calls `ModMenuEnabled()` (Bootstrap.cs).
Three outcomes:

- `false`: logs "AutoTOT is present but not enabled in the Mods menu — standing down."
  and returns.
- `true`: calls `Init()`.
- `null` (state not readable yet): creates `GameObject("AutoTOTModGate")`,
  `DontDestroyOnLoad`, adds `ModMenuGate` component (Bootstrap.cs).

`ModMenuGate` (Bootstrap.cs) polls `ModMenuEnabled()` in `Update`. While `null`
and before the 120 s deadline (`GateDeadlineSeconds = 120f`, Bootstrap.cs), it
returns. If `false`, logs and destroys itself without Init. If `true`, calls `Init()`.
If still `null` at deadline, warns "Mod menu state still unreadable at deadline; loading
anyway." then calls `Init()`.

`ModMenuEnabled` computes the mod's own folder from the assembly location and iterates
`SearchDirectory` entries, matching on full paths trimmed of trailing slashes,
`OrdinalIgnoreCase`. Returns `sd.IsChecked`. If the mod's own folder is not listed (e.g.
run from BepInEx/plugins), returns `true`: it cannot tell where it lives, so it does not block.

### Init ordering

`Init` (Bootstrap.cs) runs once, guarded by `_initialized`:

1. `LoadConfig()` (Bootstrap.cs).
2. Unity exception forwarding: subscribe to `Application.logMessageReceived`
   (Bootstrap.cs). Only `LogType.Exception` is relayed as `Log.LogError`
   (Bootstrap.cs).
3. `Harmony = new Harmony(Guid)` (Bootstrap.cs).
4. Patch-target probe: `AccessTools.Method(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask))`
   (Bootstrap.cs). Null → error log "patch target ... NOT found — the game version may be incompatible".
5. `Harmony.PatchAll(typeof(Bootstrap).Assembly)` in try/catch (Bootstrap.cs).
6. `DotsScanHardening.Install(Harmony)` after PatchAll (Bootstrap.cs). Explicit install
   instead of PatchAll because the exact target differs between Entities versions and
   `Unity.Entities.dll` may not be loaded yet.
7. `LogAssembliesThatFailGetName()` diagnostic sweep (Bootstrap.cs).
8. Pump GameObject: `new GameObject("AutoTOTPump")`, `DontDestroyOnLoad`,
   `AddComponent<Pump>()`, `AddComponent<Hud>()` (Bootstrap.cs).
9. Final log: `"Auto Time-on-Target v{Version} loaded (Enabled={Coordinator.Enabled}, Armed={Coordinator.Active}, Unity={Application.unityVersion})."`

### Config

Persistence: `path = Path.Combine(Paths.ConfigPath, Guid + ".cfg")` →
`BepInEx/plugins/.../com.seapowermods.autotot.cfg` (Bootstrap.cs).

| Section | Key | Type | Default | Range |
|---|---|---|---|---|
| General | Enabled | bool | true | - |
| General | AutoModeOnStart | bool | false | - |
| Interface | ShowIndicator | bool | true | - |
| Interface | ToggleModifier | KeyCode | LeftAlt | - |
| Interface | ToggleKey | KeyCode | T | - |
| Interface | OpenPanelKey | KeyCode | G | - |
| Interface | UIScale | float | 0 | 0–4.0; 0 = auto with screen height |
| Interface | UIScaleMultiplier | float | 1.0 | 0.5–2.0 fine-trim on top of UIScale |
| Timing | GroupWindowSeconds | float | 0.75 | 0.05–5.0 |
| Timing | MaxCollectSeconds | float | 6.0 | 0.25–20.0 |
| Debug | VerboseLogging | bool | false | - |
| Debug | Profiling | bool | false | - |

`ApplyConfig` (Bootstrap.cs) pushes config values into `Coordinator` fields and
HUD statics. `SettingChanged` handlers are wired on all twelve entries (Bootstrap.cs).

### Pump mechanism

`Pump` (Bootstrap.cs) is a `MonoBehaviour` that drives the coordinator once per
frame. `Update`:

- `inMission = Globals._mainGameViewModel != null` (Bootstrap.cs).
- Mission-exit detection: `_wasInMission && !inMission` → `Coordinator.Reset()`
  (Bootstrap.cs).
- If not in mission, return.
- `Coordinator.Tick()` in try/catch (Bootstrap.cs). On exception, deduped against
  `_lastErrorMsg`, logged as `Log.LogError($"[AutoTOT] coordinator tick error:\n{e}")`
  only when the message changes.

## File map

Sources live in five folders by concern: `Core/` (pipeline + lifecycle),
`Simulation/` (flight-time estimation), `UI/` (planner panel), `Diagnostics/`
(launch observation and profiling), `Support/` (shared utilities).

| File | Responsibility |
|---|---|
| `Core/AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]`) |
| `Core/Bootstrap.cs` | Mod-menu gate, config, Harmony patching + DOTS shield install, pump/HUD lifecycle, Unity-exception forwarding |
| `Core/Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` (ThreadStatic `Bypass` flag) |
| `Core/Coordinator.cs` | The pipeline: batching, anchor selection, open-loop scheduling, release, fire |
| `Simulation/FlightTime.cs` | Flight-time API: tier wiring, TTL caches, straight-line fallback, speed profile + group-forming delay |
| `Simulation/FlightTime.Integrator.cs` | Grounded step integrator (tier 1, beta): the setup half, which reads game state, plus phase diagnostics |
| `Simulation/FlightTime.Solve.cs` | The integration loop, written as a pure function of a snapshot |
| `Simulation/FlightTime.Async.cs` | Worker pool, result drain, and the solve self-check |
| `Simulation/FlightTime.Reflection.cs` | Branch detection + reflection resolution, and the typed-delegate fast path for the per-step thrust/drag calls |
| `Simulation/FlightTime.Stats.cs` | Estimator cost counters: sims, integration steps, setup vs loop time, which tier answered |
| `Simulation/WaypointSim.cs` | Reflection port of the public `SimulateShotLinear` (tier 2, beta) |
| `Support/GameClock.cs` | Version-agnostic sim clock + launch-timestamp access (float/double beta drift) |
| `Support/GameUnits.cs` | Shared unit conversions (Unity units ↔ metres/nm/knots) |
| `Support/LauncherFacts.cs` | Launcher cadence / ready rounds / reserve + TTL cache + reload-wave helpers |
| `Support/TtlCache.cs` | Tiny real-time TTL cache used by FlightTime and LauncherFacts |
| `Support/DotsScanHardening.cs` | Multiplayer mission-load crash shield for the DOTS assembly scan |
| `Diagnostics/LaunchDiagnostics.cs` | Flight tracker (impact reports) + launch expectations (shortfall detection); feeds anchor launch times |
| `Diagnostics/EngagementBoard.cs` | Per-target engagement state for the HUD (consolidated: one row per target) |
| `Diagnostics/CoordinatorProfiler.cs` | Per-frame timing (`Profiler`): stage timers, counters, frame share, the 60-frame report |
| `UI/Hud.cs` (+ `.Render` `.Mouse` `.Styles` partials) | IMGUI planner panel: layout/data, drawing, pointer capture, styling |

## Multiplayer crash shield: `DotsScanHardening`

Separate from the TOT pipeline: a defensive Harmony shield for a base-game crash on
the multiplayer mission-load path. `PlottingTableSerializer.RecreateWorldUsingTemp`
re-runs the DOTS bootstrap (`DefaultWorldInitialization.Initialize`), which scans
every AppDomain assembly through a `Unity.Entities.TypeManager.IsAssemblyReferencing*`
filter that calls `Assembly.GetName()`. An assembly with an unreadable name (e.g. a
mod's emitted dynamic assembly with a bad culture string) makes that throw and aborts
the LoadMission coroutine.

Install is load-order- and version-independent:

1. Resolve `Unity.Entities.TypeManager` (`AccessTools.TypeByName`); if the assembly
   isn't loaded yet, try `Assembly.Load("Unity.Entities")`, else register an
   `AppDomain.AssemblyLoad` hook and install the moment it loads.
2. Discover and patch every static `IsAssemblyReferencing*(Assembly, ...)` method.
   Older builds have `IsAssemblyReferencingEntities(Assembly)`, the Unity 6 build
   additionally `IsAssemblyReferencingEntitiesOrUnityEngine(Assembly, out bool, out bool)`.
3. Each gets a finalizer-only patch: a swallowed throw leaves the caller-side
   result/outputs at their defaults ("does not reference entities"), so the unnameable
   assembly is skipped and mission load continues. The throw is logged once.

A missing target logs a warning and the mod continues unshielded; the TOT pipeline
itself never depends on it.

## State lifecycle

| State | Lives in | Cleared when |
|---|---|---|
| Open batches | `Coordinator._openBatches` | committed (debounce/cap) |
| Held/fired-anchor entries | `Coordinator._scheduled` | released (non-anchor), ripple finalized (anchor), unit/target destroyed |
| Per-target engagement rows | `EngagementBoard` | fired + idle past 8s grace (prune in `CollectSalvos`), dropped held target, mission end |
| Flight tracker / expectations | `LaunchDiagnostics` | missile gone / expectation finalized; mission end |
| Flight-time & launcher caches | `FlightTime`, `LauncherFactsSource` | TTL (0.5s), capacity eviction, mission end |

`Coordinator.Reset()` runs on mission exit (detected by `Bootstrap.Pump`) and
clears all of the above. The HUD's per-frame row cache resets every frame; its
checked/salvo selections prune dead ships every ~300 frames.

### Threading model

Everything runs on Unity's main thread. `Pump.Update` calls `Coordinator.Tick` once per
frame. Harmony prefixes execute on whatever thread calls `InsertEngageTask`, but there is
no evidence in this codebase of `InsertEngageTask` being called off the main thread. The
`[ThreadStatic] Bypass` flag in `Patches.cs` is defensive isolation: it guarantees the flag
set on the main thread cannot leak to a concurrent call on another thread, and vice versa.

### Tunables and constants

| Constant | Value | Location | Meaning |
|---|---|---|---|
| `DebounceSeconds` | 0.75 s | Coordinator.cs | Real-time quiet gap before batch locks in |
| `MaxWindowSeconds` | 6.0 s | Coordinator.cs | Real-time hard cap on open batch |
| `LookaheadFraction` | 0.5 | Coordinator.cs | Release lookahead as fraction of one sim step |
| `StallCadenceMultiplier` | 4 | Coordinator.cs | Cadence-interval multiplier for stall detection |
| `StallMinWindowSim` | 30 s | Coordinator.cs | Floor (sim s) for the stall window |
| `NoLaunchStallSim` | 120 s | Coordinator.cs | Fired-but-zero-launches stall timeout (sim s) |
| `PlannerTaskPriority` | 1000 | Coordinator.cs | Task priority for planner-issued orders |
| `EngageGrace` | 8 s | EngagementBoard.cs | Sim seconds a fired target's row stays listed after going idle |
| `ExpectationMarginSim` | 10 s | LaunchDiagnostics.cs | Slack added beyond computed ripple time in the expectation deadline |
| `HitRangeM` | 500 m | LaunchDiagnostics.cs | A missile vanishing closer than this to its target counts as HIT |
| `CacheTtlSeconds` | 0.5 s | FlightTime.cs, LauncherFacts.cs | Kinematic estimate and launcher facts cache TTL (real time) |
| `TtlCache` default capacity | 512 | TtlCache.cs | Soft cap; expired-first purge, else full wipe |
| `GateDeadlineSeconds` | 120 s | Bootstrap.cs | Mod-menu read deadline; load anyway on timeout |
| `MinValidSeconds` | 0.01 s | FlightTime.cs | Below this an estimate is "unavailable" |
| `FallbackShotInterval` | 1 s | LauncherFacts.cs | Seed cadence when launcher facts are invalid (game's 60 rds/min default) |

## Grounding principle

Design constraint for all future pipeline work. The mod uses ONLY: the player's
sensor track of the target, the weapon's declared performance (INI), and the game's
own kinematic simulator, plus observation of the PLAYER'S OWN launch timing
(user-approved ruling). It does NOT: learn per-type speeds at runtime, read own
missiles' in-flight positions for guidance, use closure-rate feedback, or apply
fitted constants. [`model/`](model/00-index.md) applies the
same rule to the flight-time model.
