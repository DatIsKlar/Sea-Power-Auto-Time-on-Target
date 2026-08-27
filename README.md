# Auto Time-on-Target (Sea Power mod)

Coordinates missile launches so the whole salvo arrives at the target simultaneously
(Time-on-Target). Works for **multi-ship formation attacks** and **one ship firing
multiple missile types** at the same target.

## How it works

### Automatic mode (Alt+T)

When armed, a Harmony patch intercepts `ObjectBase.InsertEngageTask` for
**player-issued missile attacks**. Each launch is held briefly while the coordinator
collects all orders aimed at the same target, estimates each missile's flight time
using the game's own kinematic shot simulator, and releases each launch at the right
moment so impacts coincide. Orders made within the collection window are grouped;
guns and single-type single attacks are effectively unaffected.

### Planner panel (Alt+G)

A movable, collapsible IMGUI window (starts minimised) that lets you hand-pick
missiles from one ship or the whole formation and fire them as a coordinated TOT
strike. Features:

- Tracks last-selected friendly ship (shooter) and enemy (target)
- **Weapon-target validation** using game's `DoesAmmoMatchTarget()` — only shows missiles that can actually engage the selected target (checks weapon type, guidance, target class, land attack capability)
- **This-ship / Whole-formation** toggle
- Per-missile rows with **live ETA, range**, checkboxes, and salvo steppers
- Only missiles with correct guidance type and within range are pickable
- **FIRE — TIME ON TARGET** and **FIRE NOW (no sync)** buttons
- Live **ENGAGEMENTS** overview with queued/in-flight counts and synced arrival countdown
- Fog-of-war-correct target labels (no intel leakage)
- Only active inside a mission (nothing in the main menu)

### Coordination model (short version)

Scheduling is **open-loop**: the impact time is fixed at FIRE from the longest
estimate, then refined live by **observation anchoring** — the batch's slowest order
(the anchor) releases first, its *actual* launches are observed, and the shared
impact time every other held order syncs to is rewritten every tick from the game's
own launch timing. Each held shot releases when
`timeLeft ≤ liveFlightTime + releaseLead + ½·simStep`.

- **Flight times** come from the game's own kinematic simulator
  (`MaxRangePrecise` → `SimulateShotLinear`), cached 0.5 real s per
  shooter/ammo/target; straight-line max speed only as a fallback.
- **Release lead**: independent salvos lead by half their ripple span (centers
  arrivals); grouped salvos (fly in formation, e.g. SS-N-12/19) lead by the full
  span because the group's convergent impact lands at the ripple's trailing edge.
- **Group-drag correction**: a grouped salvo's leader throttles to 0.6× speed while
  the group forms, so it arrives later than the solo estimate. A range-aware `τ_form`
  term (`FlightTime.GroupFormingDelay`) adds this delay, computed per shot from the
  game's own speed profile — no per-type constants (see `docs/ARCHITECTURE.md`).
- **Reload waves**: orders larger than the ready rounds arrive in waves, shown
  split out in the ENGAGEMENTS overview.

Full detail with formulas and the why: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Code organization

| File | Purpose |
|---|---|
| `AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]` + `IAnchorChainMod`) |
| `Bootstrap.cs` | Mod-menu gate, Harmony patching, config, pump/HUD lifecycle |
| `Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` |
| `Coordinator.cs` | Core pipeline: batching, anchor selection, open-loop scheduling, release, fire |
| `FlightTime.cs` | Kinematic flight-time estimation + cache |
| `LauncherFacts.cs` | Launcher cadence/ready rounds/reserve + cache, reload-wave helpers |
| `LaunchDiagnostics.cs` | Impact reports + launch shortfall detection; feeds anchor observations |
| `EngagementBoard.cs` | Per-target engagement state behind the HUD's ENGAGEMENTS list |
| `Hud.cs` (+`Hud.Render.cs`, `Hud.Mouse.cs`, `Hud.Styles.cs`) | IMGUI planner panel: layout/data, drawing, pointer capture, styling |
| `GameUnits.cs` | Shared unit conversions (Unity units ↔ metres/nm/knots) |
| `TtlCache.cs` | Tiny real-time TTL cache used on the per-frame UI paths |

Design history: `docs/ISSUE-grouped-salvo-convergence.md` (the grouped-salvo
convergence problem and its resolution), `docs/PLAN-analytical-group-forming-model.md`
(superseded approach, kept for the decompiled-mechanics analysis).

## Build

Requires the .NET SDK (installed at `~/.dotnet-sdk` on this machine).

```
export DOTNET_ROOT="$HOME/.dotnet-sdk"; export PATH="$HOME/.dotnet-sdk:$PATH"
cd "AutoTOT"
dotnet build -c Release
```

Output: `bin/Release/AutoTOT.dll`. If the game moves, override the path:
`dotnet build -c Release -p:GameDir="/path/to/Sea Power"`.

## Install (local, no Steam upload)

This is an **AnchorChain** mod (requires the Anchor Chain workshop item). A local
mod is any subfolder of `StreamingAssets/` containing an `_info.ini` + the DLL; it
shows up in the in-game **Mods** menu and AnchorChain loads its DLL when enabled.

Use the helper script (builds + stages + installs):

```
./install.sh
```

Or manually:

```
dotnet build -c Release
DEST="<Sea Power>/Sea Power_Data/StreamingAssets/AutoTOT"
mkdir -p "$DEST"
cp dist/AutoTOT/_info.ini "$DEST/_info.ini"
cp bin/Release/AutoTOT.dll "$DEST/AutoTOT.dll"
```

Then: launch the game → **Mods** menu → enable **Auto Time-on-Target** → fully
restart. Confirm in `<Sea Power>/BepInEx/LogOutput.log`:
`Auto Time-on-Target v0.1.0 loaded`.

The `dist/AutoTOT/` folder is also exactly what you'd upload as a Steam Workshop
item.

## Config

After first launch: `<Sea Power>/BepInEx/config/com.seapowermods.autotot.cfg`

All settings take effect live when edited (no restart needed).

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

## Hotkeys

| Key | Action |
|---|---|
| **Alt+G** | Open/close the TOT planner panel |
| **Alt+T** | Toggle auto-coordination on/off |

Both require the modifier key (configurable; set to `None` for single-key).

## What it coordinates

Grouping is **by target**, within the collection window:

- **Formation / multiple ships** → order the group to attack one target with
  missiles; all launches are staggered to arrive together.
- **One ship, multiple missile types** → attack the same target with different
  missile types within the collection window; they're staggered to converge.

## Known limitations

- The kinematic simulator (`iterations=0`, single pass) slightly underestimates
  flight time for low-kinematics cruise missiles (e.g. Tomahawk ~a few s off on a
  ~9.5-min cruise) because their routing adds distance the linear sim doesn't
  capture. Fast missiles (e.g. SM-6) land within ±1s.
- Grouped-salvo group drag (e.g. SS-N-19, once ~20s late) is now corrected by the
  `τ_form` term and lands within ~±2s at mid/long range. Remaining: at **very short
  range** the terminal seeker trips before the group forms, so grouped salvos land
  ~10–20s early (they still converge). Deferred; see
  `docs/FUTURE-grouped-salvo-refinements.md`.
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
`[AutoTOT]`. Enable `VerboseLogging` for the full picture. Key lines:

| Log line | Meaning |
|---|---|
| `queued A -> B` | an order was intercepted and is being held |
| `coordinating N missile order(s)` | batch locked in, anchor chosen |
| `launch ... (anchor)` | a held shot was released; shows est flight vs. impactAt |
| `anchoring tgt: k/n launched` | anchor ripple being observed (also shown in the panel as `anchoring k/n`) |
| `anchored tgt: k/n launched over Xs` | ripple finalized; shared impact fixed |
| `impact ... (flight Ns, final range m)` | verbose: where/when a missile ended |
| `SHORTFALL ...` | **WARN**: fewer missiles launched than ordered against a live target (check ready/reserve numbers in the message) |
| `coordinator tick error` / `Unity exception` | something threw; report with the stack |

Panel shows `— no missiles that can engage this target —` when the selected weapon
types can't hit the selected target class (game's own `DoesAmmoMatchTarget`). If
the mod loads but nothing is intercepted, check the panel's AUTO indicator and
that the target orders are player-issued (AI auto-attacks are never intercepted).
