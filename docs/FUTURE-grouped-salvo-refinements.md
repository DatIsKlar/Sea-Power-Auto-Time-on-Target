# Future work: grouped-salvo TOT refinements

Status of grouped-salvo Time-on-Target as of 2026-08-27: **working well at mid/long range**
(residuals ~±2 s at 214/330/400 km, both missile types converge). The live correction is the
range-aware **τ_form group-drag model** in `FlightTime.GroupFormingDelay` (computed per shot from
the game's own `SimulateShotLinear` speed profile + observed launch span; constants 0.4 & 2.5 from
the MissileGroup −40 % leader clamp; no per-type numbers).

Two known refinements remain, both **grounded** (config + game physics, no fitted/per-type
constants). Neither is blocking; recorded here for later.

---

## 1. Short-range EARLY bias — the terminal-seeker cash-in cutoff

### Symptom
At very short range, grouped salvos arrive **early**, not late:
- SS-N-19 ×20 alone, est ~182 s: residual **~−8 s** (converged).
- Mixed SS-N-19 + SS-N-12, est ~145 s: **~−20 s** (the SS-N-12 anchor's 75 s full-span releaseLead
  compounds the −8 s — see refinement #2).

The τ_form correction only *adds* delay (clamped ≥ 0), so it cannot pull an early arrival back.

### Mechanism (decompiled)
- `MissileGroup.CashIn()` (`MissileGroup.cs:155`) **disbands** the group: sets each member
  `_inMissileGroup = false`, clears `_members`, and re-targets members by RCS across the enemy
  group.
- It fires when the **leader enters terminal approach** (`Missile.cs:839-842`), gated by distance
  to target: `magnitude < _ap._terminalApproachDist`, or the seeker tripping at
  `_ap._seekerActiveRange * 0.6` / `_ap._seekerPassiveRange * 0.6` (`Missile.cs:575`).
- So the group only exists between *forming-complete* and *terminal-approach distance*. At short
  range the whole flight is compressed, so the leader reaches terminal distance **before the ripple
  finishes forming** — the last rounds never join, the group cashes in early, and arrivals cluster
  near the **leading edge** rather than the last round's trailing edge.
- Our base assumption is `impact = lastRoundLaunch + est` with grouped `ReleaseLead = full span`
  (`Coordinator.PrepareIntent`). When forming is cut short, that **over-predicts** → salvo lands
  early.

### Proposed grounded fix (sketch)
Cap the grouped impact/`ReleaseLead` so it does not assume forming continues past the terminal
cash-in point. Using the sim speed profile `P(t)` (already available via `FlightTime`):
- Terminal cash-in happens at a config **distance from target** `D_term ≈ max(_terminalApproachDist,
  0.6 · _seekerActiveRange)`. Convert to a downrange position `P_term = P(soloFlight) − D_term`.
- The group can only keep forming while its progress `< P_term`. If forming would complete after
  the missile passes `P_term` (short flight), the effective convergence shifts from the trailing
  edge toward the leading edge — reduce the added span/delay accordingly (interpolate between
  full-span trailing-edge and ~first-round leading-edge as the forming window shrinks to zero).
- All inputs are config (`_terminalApproachDist`, `_seekerActiveRange`) + the sim profile — no
  per-type constants. Validate against the measured short-range residuals (SS-N-19 alone ~−8 s at
  est 182 s should collapse toward 0).

### Data on hand
| est (≈range) | span | measured residual (live τ_form) |
|---|---|---|
| 182 s (short) | ~74 s | ~−8 s (SS-N-19 alone) |
| 382 s (214 km) | ~73 s | ~+5 s |
| 590 s (330 km) | ~73 s | ~−1.8 s |
| 727 s (400 km) | ~73 s | ~−1.5 s |

Short-range TOT matters least operationally (missiles arrive quickly, and salvos still *converge*),
which is why this is deferred rather than blocking.

---

## 2. A-priori launcher cadence from the hatch-open animation — anchor selection

### Symptom
The batch **anchor** is the order with the longest `est + releaseLead`. For a short-range mixed
salvo the *faster* SS-N-12 became the anchor:
- SS-N-12: est 145 + releaseLead **75** (= 15 × SharedLaunchInterval 5) = 220 → anchor
- SS-N-19: est 181.5 + releaseLead **19** (= 19 × **1 s**) = 200.5

SS-N-19's releaseLead is wrong: the Kirov `[WeaponSystem4]` SS-N-19 block declares **no** FireRate /
SharedLaunchInterval / SalvoFireTime, so the a-priori interval falls back to the 1 s fire-rate
default — while the real cadence (observed) was **~3.9 s**. With the correct cadence SS-N-19's lead
would be ~74 s (total ~255) and it would correctly be the anchor.

Observation-anchoring fixes this for the *impact* after launches are seen, but **anchor selection
happens at commit, before any launch**, so it can't use the observed cadence.

### Root cause / grounded source (CONFIRMED)
The real ~3.9 s cadence is the per-tube **shaft-hatch open animation**: each of the 20 SS-N-19 tubes
is its own container with a `ShaftHatchOpenAnim`, whose duration is config —
`animations_wp_rkr_kirov.ini` has `Time2 = 3` (~3 s). Readable at runtime via
`WeaponSystem._containers[i]._openAnimation._sequences[*]._sequenceData[last]._time` (all public).

### Status: IMPLEMENTED 2026-08-27 (fallback/floor)
`LauncherFacts.Compute` now takes `ShotInterval = max(declared interval, hatchOpenSeconds)` — but
**only** when the launcher fires per-round (`_salvoFireAmount ≤ 1`) through **multiple hatched
containers** (`_containers.Count > 1`), i.e. each round opens its own hatch. It never lowers a
launcher that declares a real cadence, so it's a pure fallback: zero regression for any stock/modded
launcher that sets its timing; it only fills the gap for ones (like SS-N-19) that leave it unset.
Helper: `LauncherFacts.MaxHatchOpenSeconds`. **VERIFIED in-game 2026-08-27** (DLL 73c9dcea, 4
ranges): SS-N-19 releaseLead reads **57 s** (19 × 3 s hatch) and SS-N-19 correctly wins anchor
selection over the SS-N-12 in all runs; mid/long-range residuals unchanged (no regression).
