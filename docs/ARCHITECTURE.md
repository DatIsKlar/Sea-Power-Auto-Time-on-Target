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

## Why open-loop scheduling

The mod never guides missiles. It only decides WHEN each order is handed to the
game. The shared impact time is fixed at commit (then refined by the anchor), and
each held shot releases live against it using a fresh flight-time estimate every
frame. Drift of shooter or target during the stagger is absorbed because the
release condition re-evaluates `liveFlightTime` every tick; a `½·simStep`
lookahead absorbs motion during the release frame and corrects time-compression's
late-bias (simStep is measured in sim time, so pause adds no lookahead).

## Release lead (half-span vs full-span)

A launcher ripples N rounds over `(N−1)·interval` seconds; the release lead is how
long BEFORE the coordinated impact that ripple must start
(`Coordinator.PrepareIntent`):

| Salvo kind | Arrival shape | Lead |
|---|---|---|
| Independent (no missile group) | arrivals spread across the ripple | **half** the span → centers arrivals on the TOT |
| Grouped (`_maxGroupSize > 1`) | one convergent impact at the ripple's **trailing edge** | **full** span |

Why grouped salvos land near the trailing edge: `MissileGroup.AdjustMembersVelocities`
(MissileGroup.cs:106-141) applies ±40% speed clamps — the leader sheds up to 40% while
stragglers lag, and the farthest trailer gains up to 40%. The group cashes in together
once formed (Missile.cs:839-842). The baseline is **convergent impact = last launch +
that round's lone flight time**.

### Group-drag correction (`groupDelay`)

The baseline above under-predicts, because the leader spends a real interval throttled
to 0.6× stage speed while the ripple forms — so the GROUP flies slower than the solo
kinematic estimate. `FlightTime.GroupFormingDelay` adds a range-aware **τ_form** term,
computed per shot from the game's OWN shot speed profile (`SimulateShotLinear`) plus the
observed launch span — no per-type constants:

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

**Known limitation** (deferred — see `FUTURE-grouped-salvo-refinements.md`): at very short
range the terminal seeker trips before the group finishes forming (it cashes in near the
LEADING edge), so the full-span trailing-edge assumption over-predicts and the salvo lands
~10–20 s early. Salvos still converge; only very-short-range ETAs are affected.

## Observation anchoring

The realized launch cadence of many launchers is produced by machinery no INI
declares as a cadence field (per-cell hatch animations, engage-task reassignment) —
e.g. the Kirov's SS-N-19 realizes ~3.9 s/round while its INI fire-rate implies
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
launchers that declare no cadence field — e.g. the SS-N-19's ~3 s shaft-hatch animation,
which its INI omits. This only RAISES an unset cadence, never overrides a declared one,
so it's a pure fallback with no effect on launchers that set their timing.

Held orders track the running prediction: their `ImpactAtSim` is overwritten every
tick until the ripple finalizes, so their unchanged release formula tracks reality.

## Reload waves

An order larger than the launcher's ready rounds fires in reload-separated waves
(only when a magazine reserve exists — all-tubes-ready launchers like the Slava
stay one wave). The first wave carries the anchoring; later waves arrive
`waveGap = readyRounds·interval + reloadGap` apart each, shown split out in the
ENGAGEMENTS overview.

## Flight-time estimates

`FlightTime.Estimate` asks the game's own kinematic simulator
(`AmmunitionParameters.MaxRangePrecise` → `Missile.SimulateShotLinear`, invoked via
reflection, `iterations=0` single pass) and falls back to straight-line max speed
only if the simulator declines (out of range). Estimates are single-missile;
grouped behaviour never enters here (see above). All estimates are cached 0.5 real
seconds per shooter/ammo/target (`TtlCache`) because the planner UI asks for every
weapon row's ETA every OnGUI frame. Same caching for `LauncherFactsSource` (launcher
cadence/ready rounds), which also sits on the UI path.

## File map

| File | Responsibility |
|---|---|
| `AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]`) |
| `Bootstrap.cs` | Mod-menu gate, config, Harmony patching, pump/HUD lifecycle, Unity-exception forwarding |
| `Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` (ThreadStatic `Bypass` flag) |
| `Coordinator.cs` | The pipeline: batching, anchor selection, open-loop scheduling, release, fire |
| `FlightTime.cs` | Kinematic flight-time estimation + TTL cache |
| `LauncherFacts.cs` | Launcher cadence / ready rounds / reserve + TTL cache + reload-wave helpers |
| `LaunchDiagnostics.cs` | Flight tracker (impact reports) + launch expectations (shortfall detection); feeds anchor launch times |
| `EngagementBoard.cs` | Per-target engagement state for the HUD (consolidated: one row per target) |
| `Hud.cs` (+ `.Render` `.Mouse` `.Styles` partials) | IMGUI planner panel: layout/data, drawing, pointer capture, styling |
| `GameUnits.cs` | Shared unit conversions (Unity units ↔ metres/nm/knots) |
| `TtlCache.cs` | Tiny real-time TTL cache used by FlightTime and LauncherFacts |

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

## Grounding principle (design constraint — preserve)

The mod uses ONLY: the player's sensor track of the target, the weapon's declared
performance (INI), and the game's own kinematic simulator — plus observation of the
PLAYER'S OWN launch timing (user-approved ruling). It does NOT: learn per-type
speeds at runtime, read own missiles' in-flight positions for guidance, use
closure-rate feedback, or apply fitted constants. See
`docs/ISSUE-grouped-salvo-convergence.md` for the history that produced this.
