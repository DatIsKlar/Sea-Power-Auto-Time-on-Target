# Auto Time-on-Target for Sea Power

Coordinates missile launches so the whole salvo arrives at the target simultaneously
(Time-on-Target). Works for **multi-ship formation attacks** and **one ship firing
multiple missile types** at the same target. Packaged as an **AnchorChain** mod: it
appears in the in-game **Mods** menu like any other mod.

- **TOT planner panel** (Alt+G), a movable, collapsible window: hand-pick missiles
  from one ship or from a roster you assemble across formations, and fire them as a
  coordinated strike, with
  live ETA/range readouts, salvo steppers, reload warnings, and a live ENGAGEMENTS
  overview.
- **Auto-coordination mode** (Alt+T, off by default), intercepts your normal missile
  orders and holds/releases them so orders aimed at the same target arrive together.
- **Multi-target strike** (Alt+H, or the panel's **+ TARGET**): stage shots at
  several targets, from any number of formations and from ships, submarines and
  aircraft together, then fire the lot on one shared time-on-target.
- Works for stock and modded missiles alike. All timing comes from the game's own
  weapon data and shot simulator, no per-type tuning.

## Requirements

### To run the mod

| Requirement | Notes |
|---|---|
| Sea Power | Public and beta branches are supported by the same DLL (tested up to the Unity 6 build). On **beta** the game's own `EstimateShot` is ~30 s off on lofting missiles, so AutoTOT uses a grounded step-integrator instead (all reference shots within a few seconds, incl. Mach-10 lofters flying through near-vacuum) with a ported waypoint-sim + legacy fallback; see [`docs/model/`](docs/model/00-index.md). On **public** the integrator gates off and the game's `SimulateShotLinear` drives timing. |
| BepInEx 5.x | Installed in the game folder (provides logging, config, Harmony) |
| Anchor Chain (Steam Workshop item `3380210757`) | The chainloader that loads this mod; it also installs its preloader into `BepInEx/plugins/`. Enable it in the Mods menu too |
| Seapower Multiplayer *(optional)* | Not needed, but AutoTOT ships a shield for that mod's multiplayer world-re-init crash (see [How it works](#how-it-works)) |

### To build the mod

| Requirement | Notes |
|---|---|
| .NET SDK | Project targets `netstandard2.1`; any recent SDK works (developed with SDK 8) |
| A Sea Power install | The build references the game's own DLLs in place: `Seapower-Scripts.dll`, `UnityEngine.*`, BCL, all from `Sea Power_Data/Managed` |
| BepInEx in that install | `BepInEx/core/BepInEx.dll` and `0Harmony.dll` |
| `AnchorChain.dll` | From the Workshop item folder: `steamapps/workshop/content/1286220/3380210757/AnchorChain.dll` |

No NuGet packages are restored; every reference is a direct path, so the build works
offline.

## Build

`AutoTOT.csproj` references the game's DLLs in place, so the build needs two paths
from you: your Sea Power install and the Anchor Chain chainloader DLL.

| Property | Points at |
|---|---|
| `GameDir` | Your Sea Power install folder (the build uses `Sea Power_Data/Managed` and `BepInEx/core` under it) |
| `AnchorChainDll` | The Anchor Chain workshop item's DLL: `<steamapps>/workshop/content/1286220/3380210757/AnchorChain.dll` |

Provide them in any of three ways (checked in this order):

1. On the command line:

   ```
   dotnet build -c Release \
     -p:GameDir="/path/to/Sea Power" \
     -p:AnchorChainDll="/path/to/AnchorChain.dll"
   ```

2. As environment variables: `GAME_DIR` and `ANCHORCHAIN_DLL`.

3. An optional `AutoTOT.local.props` file next to the csproj (machine-local, not
   shipped), so repeated builds need no arguments:

   ```xml
   <Project>
     <PropertyGroup>
       <GameDir>/path/to/Sea Power</GameDir>
       <AnchorChainDll>/path/to/AnchorChain.dll</AnchorChainDll>
     </PropertyGroup>
   </Project>
   ```

Then:

```
cd AutoTOT
dotnet build -c Release
```

If `dotnet` is not on your PATH (e.g. a tarball install of the SDK), point
`DOTNET_ROOT` and `PATH` at the SDK first.

Output: `bin/Release/AutoTOT.dll`. Building alone does not touch the game; that is
the install step below.

## Install & enable

A local mod is any subfolder of `StreamingAssets/` containing an `_info.ini` + the
DLL; it shows up in the in-game **Mods** menu and AnchorChain loads its DLL when
enabled.

Use the helper script (builds, stages `dist/`, installs). The install destination is
the `GAME_DIR` environment variable, falling back to `AutoTOT.local.props` if you
have one:

```
./install.sh
# or explicitly:  GAME_DIR="/path/to/Sea Power" ./install.sh
```

Or manually:

```
dotnet build -c Release
DEST="<Sea Power>/Sea Power_Data/StreamingAssets/AutoTOT"
mkdir -p "$DEST"
cp dist/AutoTOT/_info.ini "$DEST/_info.ini"
cp bin/Release/AutoTOT.dll "$DEST/AutoTOT.dll"
```

Then: launch the game → **Mods** menu → enable **Auto Time-on-Target** →
**fully restart the game**. Code mods are only chainloaded at process start;
clicking Apply only reloads the scene, so enabling or reordering mods
without a full restart leaves the mod listed but inactive in-game.

Confirm in `<Sea Power>/BepInEx/LogOutput.log`:

```
[AutoTOT] Auto Time-on-Target v0.1.5 loaded (Enabled=True, Armed=False, Unity=...)
```

**Keep exactly one copy installed.** If you are also subscribed to the Workshop
version of this mod, AnchorChain finds two copies and loads only the first one it
scans (log: `Attempted to load a duplicate plugin`), so *which* build runs depends
on load order. Use either the local folder or the Workshop subscription, not both.

The `dist/AutoTOT/` folder is exactly what you'd upload as a Steam Workshop item.

## In-game usage

| Key | Action |
|---|---|
| **Alt+G** | Show/hide the TOT planner panel (fully hidden, tab and all) |
| **Alt+T** | Toggle auto-coordination on/off |
| **Alt+H** | Arm/disarm multi-target strike collection |
| **Alt+F** | Fire the staged strike (works with the panel hidden) |

Both use the configurable modifier (`ToggleModifier`; set to `None` for single-key). The **?**
in the panel's title bar lists the keys as configured, along with the salvo-stepper modifiers.

### Planner panel

Starts minimized; expand via the ▸ chevron, drag it anywhere, resize the edges. It
tracks your last-selected friendly ship as shooter and your last-selected enemy as
target (fog-of-war-correct
labels, no intel leakage). Rows list each ship's missiles with **live ETA/range**,
checkboxes, and salvo steppers. The **–/+ salvo steppers** step by 1 per click;
hold **Ctrl** for ±5 or **Shift** for ±10, clamped to the launcher's cell count.
Weapon-target validation uses the game's own
`DoesAmmoMatchTarget()`, so only missiles that can engage the selected
target are pickable. Salvos larger than the launcher's ready rounds show a
⚠ `needs reload` note and arrive in waves (shown in the overview).
**FIRE THIS TARGET** launches the selection coordinated; **FIRE NOW (no sync)** launches
it without sync. The **ENGAGEMENTS** overview shows every coordinated target with
queued/in-flight counts, a synced arrival countdown, and the ±arrival spread.

### Shooter roster

The shooter list normally follows your last-selected unit. The SELECTED row carries two buttons:
**+ UNIT** holds the selected unit (ship, submarine or aircraft) in a persistent **roster**, and
**+ FORMATION** holds every missile-armed unit in its formation. Either one reads **✓ HELD** once
there is nothing left for it to add, so you can tell at a glance whether the unit you just clicked
is already in.

The roster is not limited to one formation, and it survives selection changes. Click a unit
anywhere, press **+ UNIT**, click the next one and repeat, until you have every shooter you want;
then pick a target. A **SHOOTERS (n)** line reports what is held, each unit has a **remove**, and
**CLEAR** empties it. With no target selected the rows still list every missile aboard, so salvo
sizes can be set in advance. An empty roster means the panel follows the single selected ship.

### Multi-target strike

A strike is assembled one target at a time. Pick a target and its shooters as usual,
press **+ TARGET**, then pick the next target and repeat. The staged set appears
directly above the fire buttons, grouped by target and removable per group, and **FIRE STRIKE**
launches all of it on a single shared impact time. Whenever anything is staged, **FIRE STRIKE**
becomes the panel's single green commit button and **FIRE THIS TARGET** stays available beside
it, so a pop-up threat can still be engaged without disturbing the plan. There is no collection window: a
staged strike waits as long as you need.

Press **Alt+H** to also collect the orders you issue in the game's own interface. This does
not need auto-coordination (Alt+T) on: arming a strike collects on its own. While
that is on, every missile order you give is held, whatever its target, and joins the same
strike; **FIRE STRIKE** commits the staged picks and the collected orders together, and
**CLEAR** discards both. Rows sharing a strike are marked with a ◆ in the ENGAGEMENTS
overview.

One anchor is elected across the whole strike (the shot that needs the longest to get
there, whichever target it is aimed at) and everything else is timed against it, so a
submarine that must first rise to launch depth or an aircraft that must first descend
into its envelope still lands with the rest. Where one ship is committed to two targets
off the same non-parallel launcher, the game services those orders one at a time and the
two impacts cannot sync; the panel marks that row and the log says so.

The panel **auto-scales to your screen** (high-DPI/4K-aware). The footer's
**Scale –/+** control fine-tunes the size in 0.1 steps; both the auto reference
(`UIScale`, `0` = auto) and the relative trim (`UIScaleMultiplier`) are also editable
in the config file under `[Interface]`. Pressing **Alt+G** hides the panel completely.

### Automatic mode

Armed and disarmed with Alt+T. When armed, a Harmony patch intercepts
`ObjectBase.InsertEngageTask` for **player-issued missile attacks**. Each launch is held
briefly while the coordinator collects all orders aimed at the same target, then released at
the moment that makes impacts coincide. Guns and single-shot attacks are effectively
unaffected; AI auto-attacks are never intercepted.

## How it works

Short version; pipeline detail in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md),
the full flight-time model in [`docs/model/`](docs/model/00-index.md).

- **Flight times** come from a tiered estimator chain. On **beta** a grounded
  step integrator built on the game's own physics helpers is primary, with a
  ported waypoint sim and the game's built-in estimator as fallbacks. On
  **public** the integrator gates off and the game's own shot simulator drives
  timing. Estimates are cached 0.5 real s per shooter/ammo/target; straight-line
  max speed is the last-resort bound.
- Scheduling is **open-loop**: the impact time is fixed at FIRE, and each held shot
  releases when its live flight-time estimate reaches the time-to-impact; shooter or
  target motion during the stagger is absorbed by re-evaluating every tick.
- Multi-shot orders are **kept whole** (a single `InsertEngageTask(N)`, exactly like
  the game-UI path) and led by **half** their ripple span (independent salvos: centers
  the arrivals) or the **full** span (grouped salvos: they converge at the ripple's
  trailing edge).
- **Observation anchoring**: the batch's slowest order releases first; its *actual*
  launches are observed and rewrite the shared impact time every tick, so the batch
  locks to the launcher's realized cadence instead of an INI promise.
- **Group-drag correction** (`τ_form`): grouped missiles (e.g. SS-N-12/19) shed speed
  while their formation forms, arriving later than a solo estimate; a range-aware
  delay computed from the game's own shot speed profile compensates, with no per-type
  constants.
- **Reload waves**: orders larger than a launcher's ready rounds are predicted and
  displayed as separate waves with the reload gap between them.
- **Multiplayer crash shield** (`DotsScanHardening.cs`): shields a base-game crash on
  the multiplayer mission-load path. The DOTS world re-init scans every loaded
  assembly and throws on any whose name it cannot read (e.g. a mod's emitted dynamic
  assembly), which aborts the mission load. The shield patches the scan filter(s),
  auto-adapts to the installed Unity Entities version, and installs whenever
  `Unity.Entities` loads (immediately or deferred).

## Code organization

Sources live in five folders by concern: `Core/` (pipeline + lifecycle),
`Simulation/` (flight-time estimation), `UI/` (planner panel), `Diagnostics/`
(launch observation and profiling), `Support/` (shared utilities).

| File | Purpose |
|---|---|
| `Core/AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]` + `IAnchorChainMod`) |
| `Core/Bootstrap.cs` | Mod-menu gate, Harmony patching + DOTS shield install, config, pump/HUD lifecycle, Unity-exception forwarding |
| `Core/Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` |
| `Core/Coordinator.cs` (+`Coordinator.Anchor.cs`, `Coordinator.Release.cs`, `Coordinator.Diagnostics.cs`) | Core pipeline: batching and anchor selection; observation anchoring; open-loop release and fire; the coordinator's log lines |
| `Simulation/FlightTime.cs` | Flight-time API + caches: estimate entry, 3-tier kinematic wiring, speed profiles, group forming delay |
| `Simulation/FlightTime.Integrator.cs` | Grounded step-integrator (primary tier, beta): setup, which reads the game state, and phase diagnostics |
| `Simulation/FlightTime.Solve.cs` | The integration loop as a pure function of a snapshot, so it can run off the main thread |
| `Simulation/FlightTime.Async.cs` | Worker pool that runs the loop, plus the self-check that compares a threaded result against the main thread |
| `Simulation/FlightTime.Reflection.cs` | Reflection resolution for the game's sim internals (beta/legacy drift) + the typed-delegate fast path for per-step thrust/drag |
| `Simulation/FlightTime.Stats.cs` | Estimator cost counters (simulations, steps, which tier answered) behind the `Profiling` switch |
| `Simulation/WaypointSim.cs` | Reflection port of the public `SimulateShotLinear`, the middle-tier fallback estimator |
| `UI/Hud.cs` (+`Hud.Render.cs`, `Hud.Mouse.cs`, `Hud.Styles.cs`) | IMGUI planner panel: layout/data, drawing, pointer capture, styling |
| `Diagnostics/LaunchDiagnostics.cs` (+`LaunchDiagnostics.Expectations.cs`, `LaunchDiagnostics.Tracing.cs`) | Impact reports; launch shortfall detection; per-missile tracing. Feeds anchor observations |
| `Diagnostics/EngagementBoard.cs` | Per-target engagement state behind the HUD's ENGAGEMENTS list |
| `Diagnostics/TelemetryCadence.cs` | Sampling offsets shared by the model and live-flight traces |
| `Diagnostics/CoordinatorProfiler.cs` | Per-frame timing report behind the `Profiling` switch |
| `Support/GameClock.cs` | Version-agnostic sim clock + launch-timestamp access (float/double beta drift) |
| `Support/GameUnits.cs` | Shared unit conversions (Unity units ↔ metres/nm/knots) |
| `Support/GameMath.cs` | Horizontal flatten, elevation and pitch helpers shared by the flight-time setup paths |
| `Support/UnitNaming.cs` | The one name-for-display helper, shared by the logs and the panel |
| `Support/ModLog.cs` | Shared exception reporting, with and without the verbose gate |
| `Support/LauncherFacts.cs` | Launcher cadence/ready rounds/reserve + cache, reload-wave helpers |
| `Support/TtlCache.cs` | Tiny real-time TTL cache used on the per-frame UI paths |
| `Support/DotsScanHardening.cs` | Multiplayer mission-load crash shield for the DOTS assembly scan (discovery + deferred install) |

## Config

After first launch: `<Sea Power>/BepInEx/config/com.seapowermods.autotot.cfg`

All settings take effect live when edited (BepInEx reloads the file; no restart needed).

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Master on/off. When off, mod does nothing and the panel hides. |
| General | `AutoModeOnStart` | `false` | Whether auto-coordination is armed at mission start. The planner panel works regardless. |
| Interface | `ShowIndicator` | `true` | Show the planner panel and header bar. |
| Interface | `ToggleModifier` | `LeftAlt` | Modifier held with hotkeys (`None` for single-key). |
| Interface | `ToggleKey` | `T` | Key (with modifier) to arm/disarm auto-coordination. |
| Interface | `OpenPanelKey` | `G` | Key (with modifier) to open/close the planner panel. |
| Interface | `StrikeArmKey` | `H` | Key (with modifier) to arm/disarm multi-target strike collection. |
| Interface | `FireStrikeKey` | `F` | Key (with modifier) to fire the staged strike, panel open or not. |
| Interface | `UIScale` | `0` | Panel scale factor; `0` = auto with screen height (4K gets a larger panel), else an explicit multiplier (0–4). |
| Interface | `UIScaleMultiplier` | `1.0` | Fine-trim on top of `UIScale` (0.5–2.0), so you can shrink the auto scale without giving it up. |
| Timing | `GroupWindowSeconds` | `0.75` | Quiet gap (real s) after the last order before the batch locks in. |
| Timing | `MaxCollectSeconds` | `6.0` | Hard cap (real s) on how long one target collects orders. |
| Debug | `VerboseLogging` | `false` | Log every queued and released launch with timing details, plus per-shot flight-model diagnostics. Costly during a large salvo: it runs an extra flight simulation per missile, so leave it off unless you are investigating something. |
| Debug | `Profiling` | `false` | Log a timing report every 60 frames: the mod's share of the frame, worst frame, where the time went, and how many flight simulations ran. |
| Debug | `VerifySolve` | `false` | Run every threaded flight simulation a second time on the main thread and warn if the answers differ. Doubles the simulation work, so use it to check correctness rather than to measure speed. |
| Performance | `EstimatorThreads` | `-1` | Worker threads for flight simulation. `-1` takes a quarter of the logical cores, at least 1 and at most 4. `0` runs everything on the main thread. Raise it only if the profiling line shows the queue backing up. |

## Compatibility

- **One DLL for public and beta branches.** The kinematic simulator's result type
  moved between branches (`Missile.KinematicRangeResult` → `MissileSimulator`), so the
  mod invokes it purely via reflection; the same build runs on both.
- Tested on the Unity 6 game build (`6000.0.67f1`). Declared game compatibility lives
  in `dist/AutoTOT/_info.ini` (`ApproximateVersion`, soft Major.Minor match).
- The DOTS crash shield discovers its patch targets at runtime and also defers
  installation until `Unity.Entities` loads, so it is independent of mod load order
  and tolerates Entities version differences.

## Known limitations

- Low-kinematics cruise missiles (e.g. Tomahawk) come out a few seconds short
  on ~9.5-min cruises in every estimator tier: their guidance routes around
  waypoints and adds distance no straight-line forward sim captures. Fast
  missiles (e.g. SM-6) land within ±1s.
- Grouped-salvo group drag (e.g. SS-N-19, once ~20s late) is corrected by the
  `τ_form` term and lands within ~±2s at mid/long range. Remaining: at **close
  range** the terminal seeker trips before the group forms, so grouped salvos land
  ~10–20s early (they still converge).
- Coordination groups by shared **target** within the collection window; orders
  spaced further apart form separate batches.
- Coordinates **across** orders/missile types, not **within** one salvo: a single
  order of N same-type missiles launches over the launcher's own salvo interval
  (grouped salvos converge at the trailing edge; independent salvos are centered).
- Held orders track the anchor's predicted impact until the anchor's ripple
  completes; after that the impact time is final. A salvo ordered after the anchor
  finalized joins a fresh batch with its own anchor.
- **Against a ship formation, rounds often strike a ship other than the one you
  picked.** These missiles carry their own active seeker and do not split up the way
  a grouped salvo does, so one sent at a ship deep in a formation locks onto whatever
  it detects first. Ships at the back of a formation can absorb nothing at all while
  the rounds meant for them kill the escorts in front. This is the game's seeker
  behaviour, not something the mod schedules around; the log marks such rounds
  `[RETARGETED -> ...]` so their flight times are not mistaken for estimator error.
- **One shooter can only engage so many targets at once.** The game queues every
  engage task but services them through whichever launcher is free, so a ship with a
  single box launcher assigned to many targets fires them one after another and
  time-on-target across those targets cannot hold. The log warns with
  `launcher contention` when this is set up. Spread the shots across more shooters.

- **Radio-command missiles are capped by fire-control channels.** A missile with
  `MidCourseCorrection=1` that cannot join a group occupies one weapon channel on its
  guiding sensor for its whole flight. The Echo II's `Front_Door` radar declares four
  channels and SS-N-3 declares no `GroupSize`, so only four can be airborne at once and
  the salvo picker caps the order there. Weapons that group (SS-N-12, `GroupSize=16`) are
  not affected, and neither are unguided ones (SS-N-19, `MidCourseCorrection=0`).
- **A submerged boat firing a radio-command missile must come to periscope depth.** The
  game raises it only to the weapon's own depth ceiling, which can still be below the depth
  its guidance radar mast needs, and it then holds there indefinitely with no message. The
  planner warns. The mod does not command a depth, because surfacing a submarine is the
  player's call.
- **Coordinating a submerged boat shifts the whole strike later.** It must rise to launch
  depth and cycle its hatches before the first round leaves, which took an Oscar 133s from
  350 ft, and the rest of the wave is timed to that. Where the boat leads the strike its
  real launch is measured and the wave re-syncs to it. Where a longer-ranged surface shot
  leads instead, the boat's contribution is timed on an **estimate** of that delay, and
  measured ascent rates vary about threefold between hulls and speeds. Bring the boat to
  launch depth before the engagement and the delay is zero and the timing exact.

## Troubleshooting

Everything interesting lands in `<Sea Power>/BepInEx/LogOutput.log`, prefixed
`[AutoTOT]` (chainloader messages are prefixed `AnchorChain`). Enable
`VerboseLogging` for the full picture. Key lines:

| Log line | Meaning |
|---|---|
| `Auto Time-on-Target v... loaded` | mod initialized (note the version: with two copies installed it reveals which one AnchorChain loaded) |
| `estimator: integrator ACTIVE (beta branch, ...)` | the accurate flight model is running. Logged at startup whether or not `VerboseLogging` is on |
| `estimator: integrator UNAVAILABLE on the public branch ...` | **WARN**: you are on the public branch, where the flight model cannot run. Coordination still works, timing is less accurate |
| `estimator: ASYNC, N solve worker(s) started` / `running SYNCHRONOUSLY` | whether flight simulation runs on worker threads or the main thread (see `EstimatorThreads`) |
| `queued A -> B` | an order was intercepted and is being held |
| `coordinating N missile order(s)` | batch locked in, anchor chosen |
| `launch ... (anchor)` | a held shot was released; shows est flight vs. impactAt |
| `anchoring tgt: k/n launched` | anchor ripple being observed (also shown in the panel as `anchoring k/n`) |
| `anchored tgt: k/n launched over Xs` | ripple finalized; shared impact fixed |
| `impact ... (flight Ns, final range m)` | verbose: where/when a missile ended |
| `SHORTFALL ...` | **WARN**: fewer missiles launched than ordered against a live target (check ready/reserve numbers in the message) |
| `DOTS scan hardening active: N scan method(s) shielded` | multiplayer crash shield installed (OK) |
| `DOTS scan hardening: Unity.Entities not loaded yet ...` | shield defers until DOTS loads (OK); a `target resolved` line follows later |
| `DOTS assembly scan would have crashed on an unnameable assembly` | the shield absorbed the multiplayer mission-load crash; load continues |
| `DOTS scan hardening target NOT found` | **WARN**: shield disabled (DOTS layout changed); the rest of the mod still works |
| `AnchorChain: Attempted to load a duplicate plugin` | two copies of the mod are installed (local + Workshop); remove one |
| `present but not enabled in the Mods menu — standing down` | mod is unticked in the menu |
| `coordinator tick error` / `Unity exception` | something threw; report with the stack |

If the mod is enabled but does nothing in-game: **fully restart the game** (code mods
only chainload at process start), and check for the `loaded` line above. The panel
shows `(no missiles that can engage this target)` when the selected weapon types
can't hit the selected target class (game's own `DoesAmmoMatchTarget`). Only
player-issued orders are intercepted; AI auto-attacks never are.
