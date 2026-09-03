# 6. Accuracy, assumptions and limits

[← The loop](05-loop.md) · [index](00-index.md) · next: [Diagnostics](07-diagnostics.md)

## 6.1 Accuracy

Measured against real flights on the beta branch, integrator owning every shot with no fallback.
*Gap* = estimate − actual flight time; positive means the estimate ran long.

| shot | class | launch | gap | flight | error |
|---|---|---|---|---|---|
| rim-66b | trainable-rail SAM | 15° | −0.1 s | 40 s | 0.25% |
| ss-n-12 | non-kinematic lofter | 17.5° | −0.2 s | 615 s | 0.03% |
| yj-18a | non-kinematic sea-skimmer | 90° | −0.3 / +0.4 s | 404 / 895 s | 0.07% |
| hhq-9b | kinematic terminal-loft | 90° | −0.8 s | 102 s | 0.78% |
| ss-n-19 | non-kinematic lofter | 45° | −0.8 s | 725 s | 0.11% |
| rgm-109b | non-kinematic cruise | 90° | −1.1 s | 975 s | 0.11% |
| yj-20 | kinematic high ballistic lofter | 90° | +1.4 s | 180 s | 0.78% |
| rgm-84d | non-kinematic sea-skimmer | 35° | +3.4 s | 512 s | 0.66% |

**Mean |gap| 0.89 s across eight measurements** — 0.53 s and a 1.1 s maximum excluding rgm-84d.
Flights span 40 s to 16 minutes. For comparison, the game's own `EstimateShot` is 6–33 s off on the
same lofting shots.

## 6.2 The accuracy floor is per ammunition class

The floor is not uniform, and the difference is structural rather than a property of any particular
missile.

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
  that collapsed form — feeding it the game's own multi-stage waypoint boundaries makes it *worse*,
  because those anchor a five-stage plan with gradual transitions.
- **The −40° lock-drop proxy.** The vacuum brake is gated on geometry rather than on seeker state,
  since seeker lock is not available at planning time
  ([§4.4](04-speed.md#44-the-vacuum-brake)).
- **Beta branch only.** On the public branch the model gates off entirely and the game's own
  simulator drives timing ([§1](01-overview.md#branch-behaviour)).
- **Fixed 0.1 s step.** No adaptive stepping. A 16-minute flight is ~9,600 steps, which the 0.5 s
  result cache keeps off the per-frame path.
