# Plan: Analytical Group-Forming Model (Idea 1)

Status: **SUPERSEDED — never implemented.**

> **2026-08-27 update.** The grouped-salvo lateness was later diagnosed empirically as a
> *group-drag* effect (the leader throttles to 0.6× stage speed to let the ripple form) and a
> correction now ships in `FlightTime.GroupFormingDelay` (called from `Coordinator`). It is
> profile-based (reads the game's own `SimulateShotLinear` speed/time output) with no per-type
> constants. Known limitation: it over-predicts at long range; a promising range-aware `τ_form`
> refinement is documented in that method's header comment. This file's join-range/`T_form=0`
> framing below is NOT how the real mechanism works — see the code comment and the memory note
> `autotot-grouped-flight-underestimate` for the current understanding.

Phase 1 shipped a different solution: **observation anchoring** — measuring the
launcher's realized cadence live instead of modeling it a-priori (see
`ISSUE-grouped-salvo-convergence.md` → "Resolution", and
`Coordinator.UpdateAnchorTracking` in the code). The forming delay of the last
round needs no model at all (it is ~0 by the symmetric-clamp argument), and the
launch span — which this plan correctly identified as un-readable from the INI —
is observed rather than predicted.

Kept for the record: the decompiled-mechanics analysis below (MissileGroup speed
clamps, constants, forming ODE) is still accurate and informed the shipped model.

## Problem summary

When N grouped missiles (e.g., 16× SS-N-19) are launched, two delays compound that the mod
doesn't account for:

1. **Launch span** — time to physically ripple-fire N rounds from the launcher(s)
2. **In-flight forming delay** — the leader slows by up to 40% while stragglers catch up

The current code only estimates the lone-missile kinematic flight time and adds a HalfSpan
based on the *INI cadence* (which is wrong for SS-N-19 — defaults to 1s/round, reality is
~7-15s/round). The group forming delay is not modeled at all.

## What the game actually does (from decompiled code)

**`MissileGroup.AdjustMembersVelocities`** (`MissileGroup.cs:106-141`):

```
offset = distance from member to leader's forward plane
if (maxOffset > 0.13779747):          // threshold in Unity units
    leaderSpeed -= Clamp(maxOffset * 20, 0, 0.4 * leaderSpeed)  // up to -40%
    wingmanSpeed = leaderSpeed + Clamp(offset * 20, -0.2 * wingman, 0.4 * wingman)
```

**Key constants** (from `Constants.cs` and `AmmunitionParameters.cs`):
- `KNOTS_TO_UNITY = 0.0076554087f` — converts knots → Unity units/s
- `NM_TO_UNITY = 27.559496f` — converts nautical miles → Unity distance units
- `GroupJoinRange` default: 20 nm → `551.19` Unity units
- `GroupSpacing` default: 0.4 nm → `11.02` Unity units
- Formation placement: members at `leader.forward * 20` Unity units behind leader,
  laterally spaced by `GroupSpacing`

**`MissileGroup.OnUpdate`** (`MissileGroup.cs:80-88`):
1. `InitialSetOfMembersVelocities()` — sets each member's speed to its stage speed
   (cruise/loft/terminal)
2. If `Count > 1`: `AdjustMembersVelocities()` + `AdjustMembersPositions()`
3. `AdjustMembersPositions()` uses `Lerp(position, targetPos, deltaTime * 5f)` —
   exponential pull to formation, ~1s to converge

## The analytical model

**Physics**: At time `T_launch` (when the last missile fires), the first missile is
`v × T_launch` ahead along the flight path. The group forms when this offset drops to
`GroupJoinRange`.

**Phase 1 — Launch span**: `T_launch = (N-1) × Δt`

**Phase 2 — Forming**: The offset `d(t)` evolves as:

```
d(0) = v_unity × T_launch                        (initial offset)
d'(t) = v_leader(t) - v_member(t)                (rate of change)

v_leader(t) = v_knots - Clamp(d(t) * 20, 0, 0.4 * v_knots)
v_member(t) = v_knots + Clamp(d(t) * 20, -0.2 * v_knots, 0.4 * v_knots)
```

When `d(t) > 0.13779747` (always true for any meaningful salvo):
- Leader slowdown = `min(d(t) * 20, 0.4 * v_knots)` — hits the 40% cap for `d > 0.02`
- Member speedup = `min(d(t) * 20, 0.4 * v_knots)` — also hits cap for same reason
- Closing speed = `(v_knots + 0.4 * v_knots) - (v_knots - 0.4 * v_knots) = 0.8 * v_knots`
- In Unity units/s: closing = `0.8 * v_knots * KNOTS_TO_UNITY = 0.8 * v_unity`

**Closed-form solution** (valid while offset > threshold, which is always true):

```
T_form = (v_unity × T_launch - GroupJoinRange_unity) / (0.8 × v_unity)
       = T_launch / 0.8 - GroupJoinRange_unity / (0.8 × v_unity)
```

If `T_form < 0` (last missile already within GroupJoinRange at launch), set to 0.

**Total group delay** = `T_launch + T_form`

**Example**: 16× SS-N-19 at 7s/round:
- `v_knots = 486`, `v_unity = 3.72`, `T_launch = 105s`
- `GroupJoinRange_unity = 551.2`
- `T_form = 105/0.8 - 551.2/(0.8×3.72) = 131.3 - 185.2 = -53.9` → clamped to 0
- Total delay = 105s (launch span only)

**But**: at 15s/round: `T_launch = 225s`, `T_form = 225/0.8 - 185.2 = 96.0s`,
total = 321s.

The forming time becomes significant at higher launch intervals. The two sub-delays are
coupled: the slower the launch cadence, the more time the leader has to fly ahead, the
longer the forming phase.

## Implementation steps

### Step 1: Add `EstimateGroupFormingTime` to `Coordinator.cs`

New static method, placed near `EstimateEnroute` (~line 282):

```csharp
/// <summary>
/// Estimates the in-flight forming delay (seconds) for a grouped salvo.
/// Models MissileGroup.AdjustMembersVelocities: leader slows up to -40% while
/// stragglers catch up. Returns 0 if the last missile is already within
/// GroupJoinRange when it launches (no forming needed).
/// </summary>
internal static float EstimateGroupFormingTime(
    int missileCount, float launchIntervalSec,
    AmmunitionParameters ap)
{
    if (missileCount <= 1 || ap == null || ap._maxGroupSize <= 1)
        return 0f;

    // Launch span
    float T_launch = (missileCount - 1) * launchIntervalSec;

    // Missile speed in Unity units/s
    float v_unity = ap._maxVelocityInKnots * KNOTS_TO_UNITY;

    // Offset when last missile launches: leader has been flying for T_launch
    float initialOffset = v_unity * T_launch;

    // GroupJoinRange in Unity units (INI value already converted at load time)
    float joinRange = ap._groupJoinRangeUnity;

    if (initialOffset <= joinRange)
        return 0f;  // last missile already within join range

    // Closing speed: leader at 0.6v, straggler at 1.4v → 0.8v closing
    float closingSpeed = 0.8f * v_unity;

    float T_form = (initialOffset - joinRange) / closingSpeed;
    return Mathf.Max(0f, T_form);
}
```

### Step 2: Modify `PrepareIntent` to use forming time

Current code (lines 196-205):

```csharp
it.Grouped = ap != null && ap._maxGroupSize > 1 && wave1 > 1;
it.HalfSpan = it.Grouped
    ? (wave1 - 1) * f.ShotInterval
    : (wave1 - 1) / 2f * f.ShotInterval;
```

New code:

```csharp
it.Grouped = ap != null && ap._maxGroupSize > 1 && wave1 > 1;
if (it.Grouped)
{
    float launchSpan = (wave1 - 1) * f.ShotInterval;
    float formingDelay = EstimateGroupFormingTime(wave1, f.ShotInterval, ap);
    it.HalfSpan = launchSpan + formingDelay;
}
else
{
    it.HalfSpan = (wave1 - 1) / 2f * f.ShotInterval;
}
```

The HalfSpan is the **lead time** — how early to launch relative to the planned impact.
For grouped missiles, the group impacts at the trailing edge of the launch span PLUS the
forming delay. So the lead = launch span + forming delay.

### Step 3: Add verbose logging

In `PrepareIntent`, after computing HalfSpan:

```csharp
if (VerboseLog && it.Grouped && it.HalfSpan > 0.1f)
{
    float launchSpan = (wave1 - 1) * f.ShotInterval;
    float forming = EstimateGroupFormingTime(wave1, f.ShotInterval, ap);
    Bootstrap.Log.LogInfo(
        $"[AutoTOT] group forming: N={wave1}, interval={f.ShotInterval:0.0}s, " +
        $"launchSpan={launchSpan:0.0}s, formingTime={forming:0.0}s, " +
        $"total lead={it.HalfSpan:0.0}s");
}
```

### Step 4: Handle the launch cadence problem (prerequisite)

The forming model needs the **real** launch interval `Δt`. For SS-N-19, the INI declares
no `SharedLaunchInterval`, so `ComputeLauncherFacts` returns the game's default 1s/round.
The model would compute `T_launch = (16-1) × 1 = 15s`, when reality is ~105-225s.

**Options for the real cadence** (user must decide):

| Option | Description | Accuracy | Grounding concern |
|--------|-------------|----------|-------------------|
| **A. Measure from `_lastLaunchTime`** | After first 2-3 launches, compute actual Δt from the launcher's own clock | Best | Reads runtime weapon state |
| **B. Ship-specific override INI** | Add `[AutoTOT] LaunchInterval=7` to the mod's config per vessel type | Good | Per-type tuning (static) |
| **C. Accept the game default** | Use 1s/round; forming model handles the rest | Worst | No extra concern |

Without fixing the cadence, the forming model is correct but receives the wrong input.
The model itself is sound regardless of which cadence option is chosen.

### Step 5: Update `CommitBatch` logging

In `CommitBatch` (~line 169), include the forming time in the log:

```csharp
if (VerboseLog || b.Items.Count > 1)
{
    string details = string.Join(", ", b.Items.Select(i =>
        $"{i.AmmoId}(lead={i.HalfSpan:0.0}s, grouped={i.Grouped})"));
    Bootstrap.Log.LogInfo(
        $"[AutoTOT] coordinating {b.Items.Count} orders on " +
        $"{b.Target?.getUIDAndName()}: longest enroute {maxEnroute:0.0}s — " +
        $"{details}");
}
```

## Edge cases to handle

1. **N=1**: Forming time = 0 (no group to form). Already guarded by `missileCount <= 1`.
2. **Non-grouped missiles** (`GroupSize=0`): `EstimateGroupFormingTime` returns 0
   immediately.
3. **Last missile already within GroupJoinRange**: `T_form` clamped to 0. Happens when
   launch interval is small or GroupJoinRange is large.
4. **Loft/terminal speed phases**: The model uses `_maxVelocityInKnots` (cruise speed).
   During loft, the missile is slower, which increases forming time. A refinement would
   use `_maxLoftVelocityInKnots` when the missile is in loft phase, but this adds
   complexity for marginal accuracy.
5. **Reload waves** (Fix 3): Wave gap is separate from forming time. Each wave is a fresh
   group formation.

## What this does NOT solve

- **Launch cadence for SS-N-19**: The model needs the real Δt, which isn't in the INI.
  See Step 4 options.
- **Non-uniform launch intervals**: If a launcher fires at variable intervals (e.g., shared
  cooldown with another launcher), the model assumes uniform spacing. This is a minor
  simplification.
- **Multiple launcher interaction**: The model assumes all missiles launch from one
  launcher. If two launchers serve the same ammo with interleaved timing, the effective
  cadence is different.

## Testing plan

1. **Slava SS-N-12** (has `SharedLaunchInterval=5`): Fire 16× SS-N-12 → impacts should
   land near planned `impactAt`. Known-good baseline (Fix A already works here).
2. **Kirov SS-N-19** (no SharedLaunchInterval): Fire 16× SS-N-19 → both salvos should
   converge. **This is the critical test.** Requires fixing the cadence (Step 4) for the
   full model to work.
3. **Harpoon** (non-grouped): Verify no regression — impacts should land near planned TOT.
4. **Small salvo** (2-3 missiles): Verify forming time is small/zero (last missile within
   GroupJoinRange).
5. **Verbose log** check: Confirm forming time values are reasonable.

Build+install via `AutoTOT/install.sh`, full game restart (Proton), `VerboseLog` on.

## Summary of code changes

| File | Change |
|------|--------|
| `Coordinator.cs` | Add `EstimateGroupFormingTime()` method (~20 lines) |
| `Coordinator.cs` | Modify `PrepareIntent()` to call it and add forming time to HalfSpan (~6 lines changed) |
| `Coordinator.cs` | Add verbose logging (~8 lines) |
| `Coordinator.cs` | Update `CommitBatch` log (~2 lines) |

Total: ~35 lines of new/changed code. No new files. No new dependencies.
