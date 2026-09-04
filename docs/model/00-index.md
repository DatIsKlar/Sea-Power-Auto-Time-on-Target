# The AutoTOT flight-time model

AutoTOT coordinates missile salvos so they arrive together. That requires knowing, at planning time,
how long each round will fly, per weapon, per range, per launcher. This folder documents how that
number is produced.

The core is a **forward-Euler step integrator** that flies the missile in simulation at a fixed
0.1 s step, calling the game's own thrust and drag functions through reflection and reproducing the
guidance state machine the live mover runs. It holds no per-missile constants and does no
curve fitting: every quantity is a game constant, a field from the ammunition's `.ini`, or a value
returned by a game method.

## Read in order

| # | file | what it answers |
|---|---|---|
| 1 | [Overview](01-overview.md) | why an integrator, the grounding rule, the four-tier fallback chain, when the model declines |
| 2 | [Parameters & reflection](02-parameters.md) | every `.ini` field read, every game method called, every constant and its derivation |
| 3 | [Trajectory](03-trajectory.md) | launch geometry, the stage model, pitch command and slew, altitude hold, dive onset |
| 4 | [Speed](04-speed.md) | thrust and drag, the two speed branches, the vacuum brake, the class taxonomy |
| 5 | [The loop](05-loop.md) | setup and the annotated per-step pseudocode |
| 6 | [Accuracy](06-accuracy.md) | the accuracy band, why it differs by ammunition class, assumptions and limits |
| 7 | [Diagnostics](07-diagnostics.md) | every log line the model emits and how to read it |

## At a glance

Estimation is tiered: each tier is tried in turn and the first that returns a usable answer wins:

| tier | source | used when |
|---|---|---|
| 1 | **step integrator** | beta branch, thrust handle resolved. Primary for every shot. |
| 2 | **ported waypoint sim** | integrator declines; a 1:1 port of the public branch's `SimulateShotLinear` |
| 3 | **the game's own estimator** | `MaxRangePrecise`; on the public branch this is tier 1 |
| 4 | **straight line at max speed** | last resort, never expected to fire |

See also [`../ARCHITECTURE.md`](../ARCHITECTURE.md) for the mod as a whole.
