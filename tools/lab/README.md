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
state. The dump includes both the configured loft ceiling and the range-dependent altitude selected
by `MissileSimulator.SearchOptimalLoftAltitude`, plus the resulting Kappa nodes. It also records the
raw terminal altitude/mode, `LoftToSkim`, and target type so terminal waypoint semantics do not have
to be inferred. That struct and its recorded ammunition fields fully determine an estimate.

## Running

Sweep one round, or a directory of them, across step sizes:

    dotnet run -- <dump-dir-or-file> [step ...]

Score the whole corpus against what the rounds actually flew:

    dotnet run -- score <corpus-dir> [step ...]

Merge freshly captured rounds into the stored corpus, and write its index:

    dotnet run -- import <capture-dir> [corpus-dir]
    dotnet run -- index [corpus-dir]

Replay one round with the full step trace, which is how a disagreement gets localised to a phase.
Its first line is the waypoint route the replay flies, or `none` when the capture predates it:

    dotnet run -- trace <solveinput-file> [step]

Put the round's recorded altitude beside the model's, sample for sample:

    dotnet run -- climb <solveinput-file>

Run the focused assertions (stage and Kappa selectors, the waypoint route builder, legacy capture
reconstruction):

    dotnet run -- selftest [corpus-dir]

Check that the lab replays the model the game ran:

    dotnet run -- parity [corpus-dir] [--launch-estimate]

`-` in place of a directory means the stored corpus, so `score -` is the usual command.

Set `AUTOTOT_NO_WAYPOINT_LEGS=1` to replay with the non-kinematic waypoint route switched off, so one
build can be scored with and without it.

Timing residuals use the same sign convention as the production mod: **actual flight time minus
estimate**, so positive means the model predicted too little time. The two residuals are deliberately
named separately because they do not use the same estimate:

- `actualMinusLaunchEstimate` in `index.csv` compares the flight with `EstAtLaunch`, the estimate
  recorded by the installed mod when the round launched;
- `offGap@...` in `score` compares the flight with the current offline model replay, including that
  round's recorded motor multiplier. `offGap@step` uses its recorded steady physics step.

They can differ in magnitude even for the same round. Never negate or directly substitute one for
the other.

Results from `rich-flight-v1` and `v2` also include `WaypointTrack` at one-second cadence from 35 to
65 seconds after launch, fields `t:mode:pitch:verticalSpeed:desiredY:futureY:num3:num7:num8:x:boundary`,
mirroring the live `Missile.SetMissileCourse` pre-apex guard. `rich-flight-v3` drops it: every
field it held is in `GuardTrack`, below. It is diagnostic evidence only and is not consumed by
offline replay.

`rich-flight-v2` results add `GuardTrack`, the same guard recomputed term for term from launch on:
every 0.5 seconds through t+120, every 5 seconds after, and on both sides of any change in its
discrete state (stage, active waypoint and pitch mode, each of the guard's three tests, and its
result). The field order is written into the same file as `GuardTrackFields`. Vectors are `x,y,z`
in Unity units, and the last field of each line is `cadence`, `change` or `before`. It is also
diagnostic only.

`rich-flight-v3` inputs add the last raw fields the waypoint route needs (`Ammo.TerminalDiveDistance`,
`Ammo.MaxLoftAltUnity`, `Ammo.GroupLeaderAltUnity`), the route the mod built (`WaypointLegs`) and
`GroupLeader`. The replay rebuilds the route from the raw fields and prints a `route mismatch` warning
if it differs from the recorded one. A capture that lacks any raw route field replays with the older
altitude-command law, exactly as before.

Captures with schema `rich-flight-v1` additionally persist `FlightTrack` at the same adaptive
cadence as the live/model log traces: 0.25 seconds through the first five seconds, one second through
t+20, then seven seconds. Its fields are
`t:px:py:pz:speed:pitch:heading:stage:flatM:slantM:tx:ty:tz:targetCourse:targetSpeed:groupActive:groupLeader:groupSpeed`.
Positions are Unity units and target-relative distances are metres. This retains the missile path,
target path, attitude, stage, and formation behavior even after the rotating game log is gone.
`StageEvents` records each transition as
`t:old>new:flatU:slantM:altU:speedKn`; `GroupChanges` records discrete group/leader changes while
the continuously varying commanded group speed remains in `FlightTrack`.

The paired files also carry `AmmoFingerprint`, an FNV-1a fingerprint of all raw ammunition fields
used by the flight models. The `.solveinput` retains those raw controller/trajectory fields beside
their resolved `SolveInput` values. This separates two revisions of a modded missile that reuse the
same ammunition name. The fingerprint is provenance and change detection, not a security hash.

Older kinematic-loft captures predate the resolved-altitude fields. The lab reconstructs those in
memory through the game assembly's own optimizer and altitude-node builder before replay; it never
uses `.result` timing or peak data as an input. New captures bypass that compatibility path.

### parity

Every offline score assumes the lab flies the same model the game ran. `parity` checks that.
`rich-flight-v3` inputs record `InGameEstimate`, the value the mod's own step loop returned for
that exact input, and `InGameStep`, the step it ran at (0.1 s in production). The lab replays the
input at that step with the motor multiplier at 1.0, which is what the in-game estimator assumes,
and reports any round that differs by more than 0.0005 s.

Only captures made by a build compiled from the lab's current source can match; a capture from an
older build is replaying different code. `--launch-estimate` falls back to `EstAtLaunch` for
captures without `InGameEstimate`. That value reaches the tracker through the estimate cache, so it
can belong to an earlier solve from a slightly different position: a difference of one or two
0.1 s steps against it says nothing about the lab.

### climb

Peak altitude alone cannot separate the two ways a round ends up below the altitude it was commanded
to hold. If the recorded climb is still rising when the round turns over, it ran out of distance and
the climb is rate-limited by something the model does not apply. If it flattens early and holds, it
was commanded lower than the model thinks. Those are different defects with different fixes, and
`climb` is what tells them apart. It needs the `Track` series, so a round captured before that was
recorded has to be re-flown.

With no steps given both sweep 0.1 (production), 0.05, 0.0333333 and 0.0166667 (the game's own 30 Hz
and 60 Hz physics steps) and 0.005.

## The corpus

`corpus/` beside this README is the stored set, one text pair per round, checked in. The game writes
fresh captures to `BepInEx/AutoTOT-solve/`; `import` copies complete pairs across and never
overwrites, so re-importing is safe and the corpus only grows. `index.csv` is derived by `index` and
is never edited by hand.

The generated index is ordered by kinematics class (`kinematic` first, then `non-kinematic`),
ammunition, capture time, and key. Its leading provenance columns are `kinematics`, `modBuild`,
`artifactSha256`, `captureSchema`, and `buildNote`. New captures record the assembly MVID as
`ModBuild`, which distinguishes development builds that share the same public mod version.
`build-map.csv` supplies exact artifact hashes for older captures where the installed DLL is known;
unattributable historical builds remain explicitly `unknown` rather than being guessed.

Each round contributes two files: the `.solveinput` written when it was solved, and a `.result`
written when it landed, carrying the actual flight time, the full sampled flight/target track, stage
and group transitions, final range, whether the seeker switched target, the per-round motor roll,
and the time-compression/physics-step bands it flew in. An input without an outcome is not a test
case, so scoring skips it.

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
- **physics step changed during flight** - no single offline step can reproduce a flight that
  switched, for example, from 60 Hz to 30 Hz. A steady enlarged step remains scoreable because the
  lab replays that exact recorded step.

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
