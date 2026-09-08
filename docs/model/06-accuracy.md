# 6. Accuracy, assumptions and limits

[← The loop](05-loop.md) · [index](00-index.md) · next: [Diagnostics](07-diagnostics.md)

## 6.1 Accuracy

Measured against real flights on the beta branch, integrator owning every shot with no fallback.
*Gap* = actual flight time − the estimate made at launch, matching what the `gap` line in the log
reports (`LaunchDiagnostics.cs:366`). **Positive means the estimate ran SHORT** and the round arrived
later than predicted; negative means it ran long and the round arrived early.

| shot | class | launch | geometry | gap | flight |
|---|---|---|---|---|---|
| yj-18a | non-kinematic sea-skimmer | 90° | on-bearing / abeam | -0.3 / -0.3 / -0.1 s | 401 / 395 / 396 s |
| ss-n-12 | non-kinematic lofter | 17.5° | on-bearing | -0.2 s | 615 s |
| rgm-84d | non-kinematic sea-skimmer | 35° | rail on-bearing / abeam | +0.7 / +0.2 / +0.5 s | 509 / 502 / 502 s |
| rim-66b | trainable-rail SAM | 15° | aims before firing | -0.1 s | 40 s |
| ss-n-19 | non-kinematic lofter | 45° | on-bearing | -0.8 s | 725 s |
| ss-n-19 | non-kinematic lofter | 45° | on-bearing / 72° off | -0.7 / +0.2 / +0.2 s | 387 s |
| rgm-109b | non-kinematic cruise | 90° | on-bearing | -1.1 s | 500 s |
| rgm-109b | non-kinematic cruise | 90° | **abeam** | +0.9 / +1.0 s | 500 s |
| ss-n-3b | non-kinematic lofter, seeker-gated | 25° | 77° off, four ranges | -0.4 / -1.0 / -2.5 / -2.7 s | 197 / 585 / 729 / 580 s |
| ss-n-3 | non-kinematic lofter, seeker-gated | 0.5° | on-bearing, three ranges | -0.2 / -0.3 / -0.8 s | 565 / 418 / 708 s |
| ss-n-22b | non-kinematic lofter | 15° | on-bearing, two ranges | -0.6 / -0.3 s | 224 / 115 s |
| ss-n-22 | non-kinematic sea-skimmer | 15° | on-bearing | +0.1 s | 119 s |
| ss-n-12 | non-kinematic lofter | 17.5° | on-bearing, three ranges | -0.4 / +1.0 / +0.5 s | 423 / 254 / 143 s |
| ss-n-19 | non-kinematic lofter | 45° | on-bearing, three ranges | -0.5 / +0.7 / +0.1 s | 437 / 302 / 170 s |
| hhq-9b | kinematic terminal-loft | 90° | on-bearing | -0.8 s | 102 s |
| yj-20 | kinematic high ballistic lofter | 90° | on-bearing | +1.4 s | 180 s |

**Non-kinematic mean |gap| 0.55 s, maximum 2.7 s**, across thirty-six measurements. Flights span
40 s to 16 minutes. For comparison, the game's own `EstimateShot` is 3 to 69 s off on the same
shots.

The 2026-09-08 validation set, nine solo shots at isolated targets across six ammunition and ranges
from 90 to 320 km, came in at **mean |gap| 0.51 s, maximum 1.0 s**.

Three defects were closed that day, all of them one model field standing in for two distinct game
stages, and all three invisible until an ammunition or a launch range exercised the difference
([§3.3](03-trajectory.md#33-the-stage-model)):

| defect | worst measured | after |
|---|---|---|
| loft speed ended at the sea-skimming boundary rather than at seeker activation | -72.0 s, `ss-n-3b` | -1.0 s |
| phase 1 held one altitude where the game holds two | -4.4 s, `ss-n-3b` at 91 km | -0.4 s |
| the loft was entered on an ammunition property rather than on launch range | +11.6 s, `ss-n-12` at 167 km | +1.0 s |

The `ss-n-3b` rows above still carry 1.0 to 2.7 s at long range, the largest residual in the table.
Roughly 1.7 s of the original 2.7 s was the second defect. What remains is the same order as the
`rgm-109b` orientation residual, and `ss-n-3b` is the only entry fired 77° off the bow while also
lofting, so it is parked with that one rather than treated separately.

Where several figures are given, the shot was fired in more than one launch geometry, or at more
than one range. Launch geometry is listed because it is not incidental: a launcher that cannot train
fires along its own bearing, a vertical cell leaves carrying the ship's yaw, and pitch and heading
compete for one turn budget, so the same missile costs a different amount depending on where the
ship was pointing ([§3.1](03-trajectory.md#31-launch-geometry),
[§3.2](03-trajectory.md#pitch-and-heading-share-one-budget)).

Geometry is also measured rather than assumed. A ship will turn to unmask a launcher before firing,
so the bearing a shot is actually taken on can differ from the one intended, and each estimate is
judged against the geometry that occurred. The ss-n-19 rows above are a case in point: the launching
ship swung to face the target, and those shots left at 72° off the bow rather than abeam.

rgm-109b is the shot most sensitive to orientation and still carries about 2 s of spread between the
two geometries, the largest orientation effect that remains.

The kinematic figures are quoted on-bearing only. Their run-to-run spread is wider than most of the
effects being measured, for the reason in the next section, and they are not comparable between
ranges.

### These figures are measured against isolated targets

Every shot above was fired at a single ship with nothing else nearby, and that condition is part of
the measurement rather than an incidental detail.

Against a **ship formation** the numbers cannot be reproduced, for a reason that has nothing to do
with the estimator. These missiles carry their own active seeker and do not split up the way a
grouped salvo does, so a round sent at a ship deep in a formation locks onto whatever it detects
first and strikes a ship in front of the one it was ordered against. Its flight is then shorter than
the estimate, by however much nearer the substitute ship was, which can be tens of seconds.

The mod detects this and refuses to report it as error: such a round is tagged `[RETARGETED -> ...]`
on its impact line, and its `gap` line is replaced by `SKIPPED, seeker switched`
([§7](07-diagnostics.md)). In one 152-order strike against eleven ships in company, 15 of 16 rounds
that would otherwise have reported a gap had switched targets; the single round that reached its
assigned ship came in at **+0.7 s on a 438 s flight**.

So a formation strike is a poor accuracy test and a good demonstration of seeker behaviour. Validate
the model against isolated targets.

## 6.2 The accuracy floor is per ammunition class

The floor differs per ammunition class for structural reasons.

The game rolls a random **±2% motor-performance multiplier** per missile at launch (`Missile.cs:62`,
applied to thrust at `:3133`). What that roll does to flight time depends entirely on which speed
branch the round uses:

| class | how the roll propagates | observed spread |
|---|---|---|
| **kinematic** | thrust is integrated directly into speed, so the multiplier compounds over the whole flight | **1.4–4.3%** of flight time |
| **non-kinematic** | speed converges on a commanded stage target, which the multiplier only affects the *approach rate* to | **0.03–0.06%** |

So a kinematic round is irreducibly noisy: the same shot fired twice can differ by seconds, and the
estimate cannot be better than that spread. A non-kinematic round is nearly deterministic. Any single
kinematic measurement should be read against that band, not treated as a point value.

## 6.3 Assumptions and limitations

- **Surface targets.** Aim and schedule assume a sea-level target (`targetAlt0 = max(targetPos.y,
  0)`). An air target is aimed at the surface beneath it.
- **Constant-velocity target prediction.** The lead is `targetPos + targetVel · t`, with the same
  evasive-manoeuvre boost the game's own estimator applies. A target that turns hard mid-flight is
  not modelled.
- **The per-axis turn split is not modelled.** Both mover paths spend one combined rotation
  budget, and the model matches that ([§3.1](03-trajectory.md#heading)). What it does not carry
  is the game's split into separate yaw and pitch budgets, which needs a `TerminalVerticalTurnRate`
  more than 1 deg/s from `MaxTurnRate` and the round already in TerminalApproach. Six of 446
  shipped ammunition declare such a rate.
- **Three phases, not the game's full stage list.** The model collapses the guidance machine to
  loft / final / terminal with instant speed transitions. The boundary values it uses are tuned to
  that collapsed form; feeding it the game's own multi-stage waypoint boundaries makes it *worse*,
  because those anchor a five-stage plan with gradual transitions.
- **The −40° lock-drop proxy.** The vacuum brake is gated on geometry rather than on seeker state,
  since seeker lock is not available at planning time
  ([§4.4](04-speed.md#44-the-vacuum-brake)).
- **Beta branch only.** On the public branch the model gates off entirely and the game's own
  simulator drives timing ([§1](01-overview.md#branch-behaviour)).
- **Fixed 0.1 s step.** No adaptive stepping. A 16-minute flight is ~9,600 steps, which the 0.5 s
  result cache keeps off the per-frame path.

## 6.4 Error the model cannot remove

Four of these are properties of the game, not defects in the model. They bound how good any estimate
can be, and they are recorded so nobody spends a test run chasing them.

- **`MotorPerformance` is rolled per round.** Every missile draws a thrust multiplier in
  [0.98, 1.02] at launch and keeps it for the flight. It is unpredictable by design, so plus or
  minus 2 percent of thrust is a noise floor under every estimate. Section 6.2 measures what that
  becomes in seconds for each ammunition class.
- **A game bug the model inherits on purpose.** When an integration window straddles sustainer
  burnout, `CalculateThrustOverTime` returns `remainingTime * timeWindow`, which is dimensionally
  seconds squared; the sustainer acceleration it should have multiplied is dropped. The mod calls
  that function rather than reimplementing it, so it inherits the bug, which is correct: the bug is
  the ground truth. The two do not lose the same amount, because the mover hits it with a 0.0333 s
  window and the model with 0.1 s, so the model loses up to about 3 times the impulse on that one
  step, bounded by `sustainerAccel * 0.1` knots, and only for ammunition with a sustainer. Fixing it
  in the model would make the model wrong.
- **Wobble is not modelled.** `WobblingStrength` and `VerticalWobblingStrength` add a sinusoidal
  heading and pitch offset outside ToBearing, gated above 0.1. Cosmetic in scale.
- **Snake search is not modelled.** On terminal entry with a non-zero `SearchMode` the mover enters
  `EnterSearchMode` and possibly `PerformSnakeSearch`, which flies a sinusoidal heading at
  `SearchVelocity`. This is documented rather than declined because it cannot reach a shipped
  missile: 43 of the 45 shipped ammunition that declare a search mode are torpedoes, which do not
  run the missile mover at all, and the only two missiles that declare one (`usn_rgm-109b`,
  `usn_ugm-109b`) set `SearchVelocity` equal to their `MaxVelocity`, so there is no speed change to
  model. Both states also exit to `TerminalApproach` as soon as the seeker holds an echo, which is
  the same condition that let the round leave cruise. It becomes real for a modded round whose
  `SearchVelocity` differs from its `MaxVelocity`, and one such round is installed: the PLAN Pack
  `plan_yj-18` searches at 1824 kn against a `MaxVelocity` of 530, modelling the type's supersonic
  sprint as its search state. A yj-18 that reaches terminal with no echo flies more than three
  times the speed the model gives it, and the estimate runs late by whatever that leg costs.
  `yj-18a` and `yj-18c` do not declare a search mode and are unaffected.
- **`TerminalDelay` is not modelled.** The mover refuses `TerminalApproach` until
  `elapsedSinceLaunch` passes `TerminalDelay`, which defaults to `0.8 * GoActiveTime` and is set
  explicitly by none of the 446 shipped ammunition. It can only matter for a shot launched already
  inside terminal distance, since the delay is seconds and terminal distance is tens of kilometres.
  Modelling it would add a branch to the hottest loop in the mod for a case the coordinator does not
  plan.
- **Tiers 2 and 3 disagree with the mover for a moving surface launcher, and the mover is right.**
  The ported waypoint sim seeds launch speed from the platform for every unit, matching what the live
  mover does. The game's own `MaxRangePrecise` seeds it only for air units. This is a deliberate
  deviation from tier 3, not an oversight, and should not be "fixed" toward it.
- **The vacuum brake is a proxy, not a lock reading.** The model's `dragTargetAlt` override mirrors
  the mover's own `CurrentTarget ? target.y : own y` for the part of flight before the seeker is up.
  The -40 degree pitch and phase-2 gate stand in for "seeker not up yet, past apex", because seeker
  state is not available at planning time ([§4.4](04-speed.md#44-the-vacuum-brake)).
