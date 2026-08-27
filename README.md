# Auto Time-on-Target (Sea Power mod)

Coordinates missile launches so the whole salvo arrives at the target simultaneously
(Time-on-Target). Works for **multi-ship formation attacks** and **one ship firing
multiple missile types** at the same target. Packaged as an **AnchorChain** mod: it
appears in the in-game **Mods** menu like any other mod.

- **TOT planner panel** (Alt+G) — a movable, collapsible window: hand-pick missiles
  from one ship or the whole formation and fire them as a coordinated strike, with
  live ETA/range readouts, salvo steppers, reload warnings, and a live ENGAGEMENTS
  overview.
- **Auto-coordination mode** (Alt+T, off by default) — intercepts your normal missile
  orders and holds/releases them so orders aimed at the same target arrive together.
- Works for stock and modded missiles alike — all timing comes from the game's own
  weapon data and shot simulator, no per-type tuning.

## Requirements

### To run the mod

| Requirement | Notes |
|---|---|
| Sea Power | Public and beta branches are supported by the same DLL (tested up to the Unity 6 build) |
| BepInEx 5.x | Installed in the game folder (provides logging, config, Harmony) |
| Anchor Chain (Steam Workshop item `3380210757`) | The chainloader that loads this mod; it also installs its preloader into `BepInEx/plugins/`. Enable it in the Mods menu too |
| Seapower Multiplayer *(optional)* | Not needed, but AutoTOT ships a shield for that mod's multiplayer world-re-init crash (see [How it works](#how-it-works)) |

### To build the mod

| Requirement | Notes |
|---|---|
| .NET SDK | Project targets `netstandard2.1`; developed and built with SDK 8 (installed at `~/.dotnet-sdk` on this machine) |
| A Sea Power install | The build references the game's own DLLs in place: `Seapower-Scripts.dll`, `UnityEngine.*`, BCL — from `Sea Power_Data/Managed` |
| BepInEx in that install | `BepInEx/core/BepInEx.dll` and `0Harmony.dll` |
| `AnchorChain.dll` | From the Workshop item folder: `steamapps/workshop/content/1286220/3380210757/AnchorChain.dll` |

No NuGet packages are restored; every reference is a direct path, so the build works
offline.

## Build

`AutoTOT.csproj` resolves all references through three properties:

| Property | Default on this machine | Resolves |
|---|---|---|
| `GameDir` | `/NEW-DRIVE/SteamLibrary/steamapps/common/Sea Power` | Game/Unity/BCL DLLs via `Sea Power_Data/Managed` |
| `BepInExCore` | `$(GameDir)/BepInEx/core` | `BepInEx.dll`, `0Harmony.dll` |
| `AnchorChainDll` | `steamapps/workshop/content/1286220/3380210757/AnchorChain.dll` | the chainloader |

```
cd AutoTOT
dotnet build -c Release
```

If your game or AnchorChain live elsewhere, override the paths:

```
dotnet build -c Release \
  -p:GameDir="/path/to/Sea Power" \
  -p:AnchorChainDll="/path/to/AnchorChain.dll"
```

If your .NET SDK is not on `PATH` (e.g. a private install in `~/.dotnet-sdk`):

```
export DOTNET_ROOT="$HOME/.dotnet-sdk" PATH="$HOME/.dotnet-sdk:$PATH"
```

Output: `bin/Release/AutoTOT.dll`. Building alone does not touch the game — that is
the install step below.

## Install & enable

A local mod is any subfolder of `StreamingAssets/` containing an `_info.ini` + the
DLL; it shows up in the in-game **Mods** menu and AnchorChain loads its DLL when
enabled.

Use the helper script (builds, stages `dist/`, installs):

```
./install.sh
# or to a different install:  GAME_DIR="/path/to/Sea Power" ./install.sh
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
"applying" a mod change merely reloads the scene, so enabling or reordering mods
without a full restart leaves the mod listed but inactive in-game.

Confirm in `<Sea Power>/BepInEx/LogOutput.log`:

```
[AutoTOT] Auto Time-on-Target v0.1.1 loaded (Enabled=True, Armed=False, Unity=...)
```

**Keep exactly one copy installed.** If you are also subscribed to the Workshop
version of this mod, AnchorChain finds two copies and loads only the first one it
scans (log: `Attempted to load a duplicate plugin`) — so *which* build runs depends
on load order. Use either the local folder or the Workshop subscription, not both.

The `dist/AutoTOT/` folder is exactly what you'd upload as a Steam Workshop item.

## In-game usage

| Key | Action |
|---|---|
| **Alt+G** | Open/close the TOT planner panel |
| **Alt+T** | Toggle auto-coordination on/off |

Both use the configurable modifier (`ToggleModifier`; set to `None` for single-key).

### Planner panel (Alt+G)

Starts minimized; expand via the ▸ chevron, drag it anywhere, resize the edges. It
tracks your last-selected friendly ship as shooter (with a **This-ship /
Whole-formation** toggle) and your last-selected enemy as target (fog-of-war-correct
labels — no intel leakage). Rows list each ship's missiles with **live ETA/range**,
checkboxes, and salvo steppers. Weapon-target validation uses the game's own
`DoesAmmoMatchTarget()`, so only missiles that can actually engage the selected
target are pickable. Salvos larger than the launcher's ready rounds show a
⚠ `needs reload` note and arrive in waves (shown in the overview).
**FIRE — TIME ON TARGET** launches the selection coordinated; **FIRE NOW** launches
it without sync. The **ENGAGEMENTS** overview shows every coordinated target with
queued/in-flight counts, a synced arrival countdown, and the ±arrival spread.

### Automatic mode (Alt+T)

When armed, a Harmony patch intercepts `ObjectBase.InsertEngageTask` for
**player-issued missile attacks**. Each launch is held briefly while the coordinator
collects all orders aimed at the same target, then released at the moment that makes
impacts coincide. Guns and single-shot attacks are effectively unaffected; AI
auto-attacks are never intercepted.

## How it works

Short version — full detail with formulas in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

- **Flight times** come from the game's own kinematic shot simulator
  (`AmmunitionParameters.MaxRangePrecise`, invoked via reflection, `iterations=0`
  single pass), cached 0.5 real s per shooter/ammo/target; straight-line max speed
  only as a fallback.
- Scheduling is **open-loop**: the impact time is fixed at FIRE, and each held shot
  releases when its live flight-time estimate reaches the time-to-impact — shooter or
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
  delay computed from the game's own shot speed profile compensates — no per-type
  constants.
- **Reload waves**: orders larger than a launcher's ready rounds are predicted and
  displayed as separate waves with the reload gap between them.
- **Multiplayer crash shield** (`DotsScanHardening.cs`): shields a base-game crash on
  the multiplayer mission-load path — the DOTS world re-init scans every loaded
  assembly and throws on any whose name it cannot read (e.g. a mod's emitted dynamic
  assembly), which aborts the mission load. The shield patches the scan filter(s),
  auto-adapts to the installed Unity Entities version, and installs whenever
  `Unity.Entities` loads (immediately or deferred).

## Code organization

| File | Purpose |
|---|---|
| `AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]` + `IAnchorChainMod`) |
| `Bootstrap.cs` | Mod-menu gate, Harmony patching + DOTS shield install, config, pump/HUD lifecycle, Unity-exception forwarding |
| `Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` |
| `Coordinator.cs` | Core pipeline: batching, anchor selection, open-loop scheduling, release, fire |
| `FlightTime.cs` | Kinematic flight-time estimation, speed profiles, group forming delay + caches |
| `LauncherFacts.cs` | Launcher cadence/ready rounds/reserve + cache, reload-wave helpers |
| `LaunchDiagnostics.cs` | Impact reports + launch shortfall detection; feeds anchor observations |
| `EngagementBoard.cs` | Per-target engagement state behind the HUD's ENGAGEMENTS list |
| `DotsScanHardening.cs` | Multiplayer mission-load crash shield for the DOTS assembly scan (discovery + deferred install) |
| `Hud.cs` (+`Hud.Render.cs`, `Hud.Mouse.cs`, `Hud.Styles.cs`) | IMGUI planner panel: layout/data, drawing, pointer capture, styling |
| `GameUnits.cs` | Shared unit conversions (Unity units ↔ metres/nm/knots) |
| `TtlCache.cs` | Tiny real-time TTL cache used on the per-frame UI paths |

Design history: [`docs/ISSUE-grouped-salvo-convergence.md`](docs/ISSUE-grouped-salvo-convergence.md)
(the grouped-salvo convergence problem and its resolution),
[`docs/PLAN-analytical-group-forming-model.md`](docs/PLAN-analytical-group-forming-model.md)
(superseded approach, kept for the decompiled-mechanics analysis),
[`docs/FUTURE-grouped-salvo-refinements.md`](docs/FUTURE-grouped-salvo-refinements.md)
(deferred refinements).

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
| Timing | `GroupWindowSeconds` | `0.75` | Quiet gap (real s) after the last order before the batch locks in. |
| Timing | `MaxCollectSeconds` | `6.0` | Hard cap (real s) on how long one target collects orders. |
| Debug | `VerboseLogging` | `false` | Log every queued and released launch with timing details. |

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

- The kinematic simulator (`iterations=0`, single pass) slightly underestimates
  flight time for low-kinematics cruise missiles (e.g. Tomahawk ~a few s off on a
  ~9.5-min cruise) because their routing adds distance the linear sim doesn't
  capture. Fast missiles (e.g. SM-6) land within ±1s.
- Grouped-salvo group drag (e.g. SS-N-19, once ~20s late) is corrected by the
  `τ_form` term and lands within ~±2s at mid/long range. Remaining: at **very short
  range** the terminal seeker trips before the group forms, so grouped salvos land
  ~10–20s early (they still converge). Deferred; see
  [`docs/FUTURE-grouped-salvo-refinements.md`](docs/FUTURE-grouped-salvo-refinements.md).
- Coordination groups by shared **target** within the collection window; orders
  spaced further apart form separate batches.
- Coordinates **across** orders/missile types, not **within** one salvo: a single
  order of N same-type missiles launches over the launcher's own salvo interval
  (grouped salvos converge at the trailing edge; independent salvos are centered).
- Held orders track the anchor's predicted impact until the anchor's ripple
  completes; after that the impact time is final. A salvo ordered after the anchor
  finalized joins a fresh batch with its own anchor.

## Troubleshooting

Everything interesting lands in `<Sea Power>/BepInEx/LogOutput.log`, prefixed
`[AutoTOT]` (chainloader messages are prefixed `AnchorChain`). Enable
`VerboseLogging` for the full picture. Key lines:

| Log line | Meaning |
|---|---|
| `Auto Time-on-Target v... loaded` | mod initialized (note the version: with two copies installed it reveals which one AnchorChain loaded) |
| `queued A -> B` | an order was intercepted and is being held |
| `coordinating N missile order(s)` | batch locked in, anchor chosen |
| `launch ... (anchor)` | a held shot was released; shows est flight vs. impactAt |
| `anchoring tgt: k/n launched` | anchor ripple being observed (also shown in the panel as `anchoring k/n`) |
| `anchored tgt: k/n launched over Xs` | ripple finalized; shared impact fixed |
| `impact ... (flight Ns, final range m)` | verbose: where/when a missile ended |
| `SHORTFALL ...` | **WARN**: fewer missiles launched than ordered against a live target (check ready/reserve numbers in the message) |
| `DOTS scan hardening active: N scan method(s) shielded` | multiplayer crash shield installed — OK |
| `DOTS scan hardening: Unity.Entities not loaded yet ...` | shield defers until DOTS loads — OK; a `target resolved` line follows later |
| `DOTS assembly scan would have crashed on an unnameable assembly` | the shield absorbed the multiplayer mission-load crash — load continues |
| `DOTS scan hardening target NOT found` | **WARN**: shield disabled (DOTS layout changed); the rest of the mod still works |
| `AnchorChain: Attempted to load a duplicate plugin` | two copies of the mod are installed (local + Workshop); remove one |
| `present but not enabled in the Mods menu — standing down` | mod is unticked in the menu |
| `coordinator tick error` / `Unity exception` | something threw; report with the stack |

If the mod is enabled but does nothing in-game: **fully restart the game** (code mods
only chainload at process start), and check for the `loaded` line above. The panel
shows `— no missiles that can engage this target —` when the selected weapon types
can't hit the selected target class (game's own `DoesAmmoMatchTarget`). Only
player-issued orders are intercepted — AI auto-attacks never are.
