# Offline replay lab

Replays a solver input captured in game, at any integration step size, without running the game.

## Why

Comparing two step sizes by flying the same mission twice does not work. The shooter and the target
are both under way, so a second run does not reproduce the launch geometry, and on a long
sea-skimming shot 100 m of range difference is already 0.37 s of flight time. Every round also draws
its own random motor performance (`Missile.cs`, `_motorPerformance = 1.02 - rand/25`), worth up to
±2% of flight time on its own. Both effects are larger than the difference being measured.

Replaying one captured input removes both. Every solve reads identical values, so a difference
between two rows is caused by the step size and nothing else.

## Capturing an input

Enable `TraceFlightModel` in the mod's config and fire. Each traced round writes one file to
`BepInEx/AutoTOT-solve/`, alongside the `sim-launch` and `sim-track` lines it already emits.

`FlightTime.SolveInput` is a pure value, and the step loop reads no Unity API and no mutable game
state, so that struct plus eight ammunition fields fully determine an estimate.

## Running

Sweep one round, or a directory of them, across step sizes:

    dotnet run -- <dump-dir-or-file> [step ...]

Score the whole corpus against what the rounds actually flew:

    dotnet run -- score <corpus-dir> [step ...]

With no steps given both sweep 0.1 (production), 0.05, 0.0333333 and 0.0166667 (the game's own 30 Hz
and 60 Hz physics steps) and 0.005.

## The corpus

Each round contributes two files: the `.solveinput` written when it was solved, and a `.result`
written when it landed, carrying the actual flight time, the final range, whether the seeker
switched target, the per-round motor roll, and the time-compression band it flew in. An input
without an outcome is not a test case, so scoring skips it.

Scoring replays each input with the motor multiplier that round actually flew
(`FlightTime.OfflineMotorScale`), rather than the 1.0 the estimator has to assume in advance. That
removes the ±2% thrust randomness, which on a fast round is worth several seconds and otherwise
buries any model difference.

Four kinds of round are excluded, because each is unfair to score rather than merely inaccurate:

- **no arrival** - the round never got there, so there is no flight time to compare against;
- **retargeted** - the seeker switched ships, so the flight is to one target and the estimate to
  another;
- **shared input** - several rounds of one ammunition were solved from a single input, so which
  round it belonged to is ambiguous;
- **above 10x compression** - past its physics cap the game enlarges its own step, so the round
  really did fly a different trajectory.

A round whose target manoeuvred hard after launch is worth excluding by hand too. `SolveInput`
freezes the target's velocity at launch, so no launch-time estimate could have predicted it.

## How it works

The lab compiles the mod's own simulation sources rather than a copy of them, so it cannot drift
from what ships. It supplies its own `Main`, small stubs for the ambient services the solver touches
(logging, two diagnostic gates, a profiler counter, a clock), and reads the game's real thrust and
drag helpers out of `Seapower-Scripts.dll` exactly as the mod does.

One substitution is unavoidable. Unity implements `Quaternion.Euler` and `Quaternion.RotateTowards`
as engine-native calls, which raise `SecurityException` outside the player. `GameMath` routes both
through wrappers that the `AUTOTOT_OFFLINE` build replaces with managed equivalents; the shipped mod
still calls Unity. The `Quaternion` struct itself needs no substitute, since its constructor,
`normalized`, `Dot`, `Angle` and vector-rotation operator are all ordinary managed code.

Because of that substitution, a sweep is only trustworthy once the lab reproduces the estimate the
game logged for the same round at the production 0.1 s step. Check that first; it validates the
replacements and the reconstruction together.
