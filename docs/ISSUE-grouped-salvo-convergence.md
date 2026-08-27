# ISSUE: AutoTOT — grouped Soviet salvos don't converge on the shared Time-on-Target

Status: **RESOLVED (phase 1 shipped)** — observation-anchored impact timing, no fitted constants.
The original analysis below is kept for history, with corrections marked **CORRECTION**.

> Note: file/line references below predate the 2026-08 cleanup that split
> `Coordinator.cs` into Coordinator/FlightTime/LauncherFacts/LaunchDiagnostics/
> EngagementBoard (see `ARCHITECTURE.md` for the current file map).

## Resolution (what shipped)

**Model:** a grouped salvo's convergent impact = **last launch + that round's lone kinematic
flight time**. Proven from the game's own forming mechanics (`MissileGroup.AdjustMembersVelocities`,
`MissileGroup.cs:106-141`): the leader's speed loss `min(d·20, 40%)` and the farthest trailer's
speed gain `min(d·20, 40%)` are symmetric, so while the last-launched round is the farthest behind
it flies **exactly** its solo stage speed; once it closes up, the whole group returns to nominal
speed and cashes in together (`Missile.cs:839-842`). The group therefore arrives when the last
missile's solo flight ends — not earlier, and in practice not later.

**Mechanism (Coordinator.cs, "observation anchoring"):** the batch's longest-enroute intent is the
**anchor** and is released first. Its ACTUAL launches are then observed (the existing
`CreditLaunch`/`WeaponBase._launchTime` path), the launcher's live cadence is measured from them,
and the batch's shared impact time is rewritten every tick:
`predictedImpact = lastObservedLaunch + measuredInterval·remaining + liveKinematicEstimate`
(finalize: `lastLaunch + liveEstimate`). All held orders are released against that running
prediction. No cadence constant exists anywhere — the game's own weapon timing is measured as it
happens, which is exactly what's needed because the realized cadence is produced by machinery no
INI declares (per-cell hatch animations in the ship's animation INI + engage-task reassignment
overhead; e.g. Kirov SS-N-19 realizes ~3.9 s/round while its INI says nothing → game default
60/min = 1 s/round).

**Grounding ruling (user):** observing own launch times is acceptable; no learned per-type speeds,
no in-flight position reads, no fitted constants.

## CORRECTIONS to the original analysis below

1. **CORRECTION — "~15 s/round, only 10 of 20 launched" is wrong.** The same log proves **all 20
   SS-N-19s launched** (20 impact lines) at **~3.9 s/round over a 74.5 s span** (sim 20.4 → 94.9).
   The `SHORTFALL launched 10/20` was a false alarm: its deadline was computed from the wrong INI
   cadence and expired mid-ripple. (The fixed adaptive deadline now extends with every observed
   launch.)
2. **CORRECTION — the launch span IS modelable live.** It is not readable a priori from the INI
   (correct), but it is directly observable once the ripple starts, and the forming delay needs no
   a-priori term at all: it is ~0 for the last round by the symmetric-clamp argument above (SS-N-12
   in the same log fits "last launch + lone flight" to ~1 s).
3. **Error decomposition of the 79 s miss:** ~55 s = wrong launch span (19 s modeled vs 74.5 s
   actual), remaining ~20-25 s = kinematic-estimate bias and/or forming drag on the last round
   (not separable from one engagement — measured in the next in-game pass; phase 2 material).

## Phase 2 (deferred, only if the residual proves systematic)

First-principles catch-up correction derived solely from game-code constants (the 20 kn/unit gain,
40%/20% clamps, stage velocities) for the ~20 s residual seen on SS-N-19-class missiles, or a
better flight-time source if the residual turns out to be kinematic-sim bias.

---

## Original analysis (history)

## Symptom

In a two-shooter TOT strike (Slava SS-N-12 + Kirov SS-N-19 on one target), the two salvos do
**not** arrive together. The SS-N-12 salvo hits near its planned time and kills the target; the
SS-N-19 salvo is still ~41 km away when that happens, so it never hits. The planner's predicted
impact time for the grouped salvos is far earlier than reality.

## What the log proves (SS-N-19 strike, latest `.../Sea Power/BepInEx/LogOutput.log`)

- SS-N-19 launched at sim **17.3**, planner **est flight 382.0s (kinematic)**, planned impact **418.3**.
- Only **10 of 20** launched: `SHORTFALL wp_ss-n-19 ... launched 10/20, ready 11, reserve 0` (reload-limited).
- **None hit.** All 10 "impact" lines are terminations at sim **423–427 while still ~40,950 m from
  the target** (`final range 40946 m`). The target `[7003]` was **already dead** (killed by the
  Slava's SS-N-12 near the earlier TOT), so the SS-N-19s had nothing to hit and self-terminated ~41 km out.
- The 10 rounds cashed in **together** (all ended 423–427) yet their flight times span **332–403s
  (~70s spread)** — i.e. they were all still cruising as a **slowed, forming group** when the
  engagement ended, trailing rounds included.

**So this is a convergence failure, not an aiming error:** SS-N-19's estimated time-to-impact (~382s)
was ~60–90s too optimistic, so the batch set the shared TOT too early and didn't wait for it.

## Root cause — the needed lead time is NOT in the weapon data

A grouped salvo's real arrival = launch span + in-flight group loiter. **Neither is readable a priori:**

1. **Launch cadence isn't in the INI.** The ~70s spread over 10 rounds implies **~15 s/round realized**,
   but SS-N-19 declares **no `SharedLaunchInterval`** (checked `wp_ss-n-19.ini` and the Kirov
   `[WeaponSystem4]` block) and **no `FireRate`**, so the game defaults `FireRate=60/min` → **1 s/round**
   (`ObjectBaseLoader.cs:2739`; launch gate `WeaponSystemLauncher.cs:634`). The mod, reading the INI,
   models a ~9 s span when it's really ~70 s. **Fix A (SharedLaunchInterval) does nothing for SS-N-19
   because it has none** — Fix A only helps the Slava, which declares `SharedLaunchInterval=5`.
2. **In-flight group loiter is emergent.** The leader sheds up to −40% speed while stragglers lag
   (`MissileGroup.AdjustMembersVelocities`, `MissileGroup.cs:106-141`); the group cruises slowed until
   assembled, then cashes in together near the target (`MissileGroup.CashIn`). This is live multi-body
   behavior, not a field.
3. **The kinematic sim can't produce it.** `Missile.SimulateShotLinear` (via
   `AmmunitionParameters.MaxRangePrecise`) is **single-missile** — no group parameter exists anywhere
   in the game. Confirmed dead end.

Earlier reconstructions suggested a clean "impact ≈ last_launch + lone kinematic" or a universal
"×1.26" multiplier. The log breaks the clean version: the trailing rounds are also slowed, and the
launch span itself is unknowable from the INI, so both the "full-span lead" and the "one coefficient"
models are guesses that happened to fit SS-N-12 (whose span ≈ its penalty) and miss SS-N-19.

## What's already done and correct (keep)

- **Fix A — `SharedLaunchInterval` in the shot interval** (`ComputeLauncherFacts`, `Coordinator.cs`):
  `interval = max(fire-rate interval, ship._sharedLaunchIntervals[vwp._systemName])`, both public
  fields. Correct and shipped; tightens cadence for any launcher that *declares* a shared interval
  (e.g. Slava SS-N-12 = 5s). Does not help launchers that declare nothing (SS-N-19).

## The still-flawed shipped code (needs replacing once model is chosen)

- `PrepareIntent` (`Coordinator.cs:193-205`): `int wave1 = Mathf.Min(n, readyRounds)`; grouped ammo
  (`ap._maxGroupSize > 1 && wave1 > 1`) gets `HalfSpan = (wave1-1)*ShotInterval` (full span), non-group
  gets half-span. Two problems: (a) span is built on the INI cadence that's 15× too fast for SS-N-19,
  and (b) it's capped at ready rounds. This is the version that left SS-N-19 short.

## Open decision — how to estimate a grouped salvo's arrival (NOT yet chosen)

The user rejected a tuned coefficient and "ask the sim" is infeasible, so the realistic options are:

- **A. Read the launcher's live cadence.** Once launches begin, measure the launcher's OWN realized
  fire interval from the game's `_lastLaunchTime` (and read the group's live forming state). This is
  the game's own weapon timing — **not** per-type speed learning, **not** target-closure feedback,
  **not** reading our own missile's position for guidance. Most accurate. **Needs the user's ruling on
  whether reading the weapon's runtime clock fits the grounding principle** (which so far has meant:
  player sensor track + weapon INI performance + the kinematic sim only).
- **B. Geometric forming model.** Compute assembly time a-priori from INI geometry (`GroupSize`,
  `GroupSpacing`, `GroupJoinRange`, missile speed) — how long N missiles take to close into formation.
  No coefficient, no runtime feedback, fully within principle, but more code and unproven accuracy;
  still doesn't solve the un-readable launch cadence.
- **C. One universal coefficient.** Inflate grouped enroute by a single fitted ~1.26× (one number for
  all grouped missiles). Trivial and accurate on tested cases; user disliked a tuned constant.

None of A/B/C is agreed. A is the most accurate but is a grounding-principle judgment call only the
user can make; the clarification the user wanted to raise before choosing is still pending.

## Grounding principle (must preserve — do not revisit abandoned approaches)

Use ONLY: the player's sensor track of the target + the weapon's known performance (INI) + the game's
kinematic simulator. Do **not**: learn per-type speeds at runtime, read our own missile's in-flight
position, use closure-rate feedback, or use a launch-transient slowness factor. (Whether option A's
launcher-cadence read crosses this line is the open question for the user.)

## Key references

- `AutoTOT/Coordinator.cs` — the only mod code file. `ComputeLauncherFacts` (Fix A, ~line 339–398,
  `ShotInterval`), `PrepareIntent` (~187–214, grouped `HalfSpan`), `CommitBatch`/`Schedule` (batch TOT
  sync, ~156–246), `EstimateEnroute` (~282–286, pure lone kinematic).
- Game: `WeaponSystemLauncher.cs:624-666` (launch gates), `ObjectBaseLoader.cs:2732-2738` (FireRate/Salvo
  defaults), `MissileGroup.cs:106-141` (`AdjustMembersVelocities`) & `CashIn`, `ObjectBase.cs:569`
  (`_sharedLaunchIntervals`).
- Data: `.../StreamingAssets/original/ammunition/wp_ss-n-19.ini` & `wp_ss-n-12.ini`,
  `.../vessels/wp_rkr_kirov.ini` (SS-N-19: no cadence fields), `wp_rkr_slava.ini` (SS-N-12:
  `SharedLaunchInterval=5`).
- Log: `/NEW-DRIVE/SteamLibrary/steamapps/common/Sea Power/BepInEx/LogOutput.log` (grep `[AutoTOT]`).

## Verification (once a model is implemented)

Build+install via `AutoTOT/install.sh`, full game restart (Proton — user runs it), `VerboseLog` on:
1. Slava SS-N-12 + Kirov SS-N-19 on one target → both salvos **converge** (impact within a few
   seconds); SS-N-19 reaches the target instead of terminating ~41 km short.
2. Slava 16× SS-N-12 alone → impacts land near planned `impactAt`.
3. Harpoon (`usn_rgm_84d`) salvo (non-group) still centers on its TOT (unchanged path).
