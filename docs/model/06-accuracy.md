# 6. Accuracy, assumptions and limits

[← The loop](05-loop.md) · [index](00-index.md) · next: [Diagnostics](07-diagnostics.md)

## 6.1 Accuracy

Measured against real flights on the beta branch, integrator owning every shot with no fallback.
*Gap* = estimate − actual flight time; positive means the estimate ran long.

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
| hhq-9b | kinematic terminal-loft | 90° | on-bearing | -0.8 s | 102 s |
| yj-20 | kinematic high ballistic lofter | 90° | on-bearing | +1.4 s | 180 s |

**Non-kinematic mean |gap| 0.49 s, maximum 1.1 s**, across fifteen measurements. Flights span 40 s
to 16 minutes. For comparison, the game's own `EstimateShot` is 6 to 33 s off on the same lofting
shots.

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
- **Independent pitch and heading rate limits.** The game rate-limits combined pitch and yaw in one
  `Quaternion.RotateTowards`; the model limits each axis separately
  ([§3.1](03-trajectory.md#heading)). The error is small when a turn is dominated by one axis.
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
