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

### Flight-time model

Flight time comes from `AmmunitionParameters.MaxRangePrecise(...).InterceptTime`, which
runs `Missile.SimulateShotLinear` — the game's own kinematic integration (boost, loft
arc, drag, velocity bleed). Exact for any missile, stock or modded, with no per-type
tuning. A straight-line max-speed fallback is used only if the simulator declines
(out of range). Results are cached for 0.5 real seconds per shooter/ammo/target to
avoid per-frame stutter in the planner UI.

Scheduling is **open-loop**: the impact time is fixed at FIRE from the longest estimate;
each held shot releases live at `impact − its own live flight time + 0.5 × simStep`
lookahead (absorbs shooter/target motion during the stagger and corrects time-compression
late-bias). No aim-lead — missiles home themselves.

### Salvo interval handling

When firing multiple missiles of the same type from one ship, the launcher physically fires
them one at a time with a per-launcher interval (from the game's `WeaponParameters`:
`max(60/fireRate, reloadTime)`, or `_salvoFireTime` for multi-shot salvos). The coordinator
splits multi-shot orders into individual `Shots=1` intents with staggered impact times
centered on the planned coordination time (mean-centered: for 3 missiles, the middle one
arrives at the planned time). Each ship's launcher interval is read independently so
different ship types still synchronize. The ENGAGEMENTS overview shows the arrival spread
(e.g. `arrival 2m30s ±8s`) when it exceeds 2 seconds.

### Weapon-target compatibility

Validated via `ObjectBase.DoesAmmoMatchTarget()` — the game's definitive check that validates
weapon type exclusions, `_targetType`/`_secondaryTargetType` matching (AAW vs ASuW vs ASW),
`_canNotAttackTypes` blacklist, land attack capability, and situational restrictions (ARH
signal strength, TV visual range). Incompatible weapons are hidden from the planner and
rejected in auto-intercept mode.

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
  flight time for low-kinematics cruise missiles (e.g. Tomahawk ~14s late on a
  ~9.5-min cruise) because their routing adds distance the linear sim doesn't
  capture. Fast missiles (e.g. SM-6) land within ±1s.
- Coordination groups by shared **target** within the collection window; orders
  spaced further apart form separate batches.
- Coordinates **across** orders/missile types, not **within** one salvo: a single
  order of N same-type missiles launches over the launcher's own salvo interval.
- Once a missile is airborne, timing is not re-anchored to the in-flight leader.
  The open-loop approach absorbs shooter/target motion during the stagger but
  trusts the initial impact-time estimate.

## Files

| File | Purpose |
|---|---|
| `AnchorChainEntry.cs` | AnchorChain entry point (`[ACPlugin]` + `IAnchorChainMod`) |
| `Bootstrap.cs` | Mod-menu gate, Harmony patching, config, pump/HUD lifecycle |
| `Coordinator.cs` | Core logic: batching, kinematic flight-time, open-loop scheduling |
| `Hud.cs` | IMGUI planner panel (movable, collapsible, mission-gated) |
| `Patches.cs` | Harmony prefix on `ObjectBase.InsertEngageTask` |
