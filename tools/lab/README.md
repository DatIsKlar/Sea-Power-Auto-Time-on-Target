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

`AUTOTOT_SCORE_CSV=<file>` writes one keyed row per round beside the table:
`key,label,actualFlight,offlineEst,offGap,motor,grouped,schema,flownRail,scored,formation,targetRounded,diveStartU,finalFlightStartU,offGapAtStep,hullArrival,arrivalFlatM,heightAt202M`,
where `flownRail` is 1 for a round replayed on the rail it flew (see "launch rail" below), and the last two
are the flat distance to the ship at which the first sweep column entered the terminal phase and the final
flight, -1 when it never did, for reading against the recorded `StageEvents`. `offGapAtStep` is the
table's `offGap@step`, blank where the round has no comparable step. The table prints a row per round but no
key, so a round cannot be joined back to its capture and correlated against what it flew. That join
is what a question like "why do this family's rounds disagree with each other" needs, as opposed to
"how far is this family from the game", which the table already answers.

Merge freshly captured rounds into the stored corpus, and write its index:

    dotnet run -- import <capture-dir> [corpus-dir]
    dotnet run -- index [corpus-dir]

Replay one round with the full step trace, which is how a disagreement gets localised to a phase.
Its first line is the waypoint route the replay flies, or `none` when the capture predates it:

    dotnet run -- trace <solveinput-file> [step]

Check the port of the game's lead intercept point against the guard frames of flown rounds. Each frame's
recorded known target, known velocity, position, speed and travelled distance go through
`FlightTime.LeadInterceptTime`, and the result is compared with the intercept point the game recorded on
that frame, overall and at the last frame before each stage change. Launch-state frames miss by design:
the game computes no lead before ToBearing, so their recorded point is still the launch aim point.

    dotnet run -- leadcheck <run-or-capture-dir> [...]

Put the model's flight beside the recorded one, matched on flat distance to the target's centre, to find the
phase where a timing error is made. Each recorded `FlightTrack` sample and stage event gets the model's height,
speed and pitch at the same distance and `dt`, the model's time there minus the real time; a `dt` that steps between
two rows was made between them. It replays the way `score` does (rail flown, rounded target velocity, motor roll,
recorded steps), and `--no-hull` ends the replay 3 u from the target's centre instead of at its hull. `--time` matches each
row to the model at the same time since launch instead of the same distance, which is the comparison for a speed
error (`dt` then reads zero):

    dotnet run -- pathcmp <solveinput-file> [--no-hull] [--time]

Put the model's own flight at two integration steps side by side, at common times, to find where a step-size
difference is made: flat distance, height, speed and pitch, and the flat difference converted to seconds. `--raw t0 t1`
prints every step of both flights in that window with its flat closure and how far the flown and wanted headings sit
from the bearing to the target:

    dotnet run -- stepcmp <solveinput-file> [stepA] [stepB] [every-s] [--raw t0 t1]

Check the model's waypoint look-ahead against the game's own steering points. The guard track records the
game's `DesiredPosition` (1.5 s ahead) and `FuturePosition` (8 s ahead); `popcheck` feeds each frame's recorded
state into `FlightTime.WaypointPointAtTime` on every frame whose look-ahead reaches a terminal pop-up leg and prints
the recorded heights beside the port's, with the pop-up look-ahead off and on:

    dotnet run -- popcheck <result-file-or-dir> [...]

Check a kinematic loft round's steering against the game's live KappaLoft curve. On every ToBearing and
MaintainLoftAlt guard frame `kappacheck` compares the recorded `DesiredPosition` height with a port of
`Waypoint.GetOrComputeLoftParams` and `GetKappaLoftAltitude` fed the frame's own state, with the model's Kappa-block
command at the same state, and with the model's own curve put through the game's look-ahead arithmetic:

    dotnet run -- kappacheck <result-file-or-dir> [...]

Measure how much the model's answer moves with the per-round thrust roll, for one round:

    dotnet run -- motor <solveinput-file> [scale ...]

`trace` takes `--motor <value>` to replay at a multiplier of your choosing rather than the one the
round flew. Two traces of one input at two multipliers isolate the model's response to thrust from
every other difference between two rounds, which a live salvo cannot do: its rounds differ in
geometry as well as in motor.

Put the round's recorded altitude beside the model's, sample for sample:

    dotnet run -- climb <solveinput-file>

Run the focused assertions (stage and Kappa selectors, the waypoint route builder, legacy capture
reconstruction):

    dotnet run -- selftest [corpus-dir]

Check that the lab replays the model the game ran:

    dotnet run -- parity [corpus-dir] [--launch-estimate]

Compare the two flight times the game's tactical map shows (`GameTooltipEst`, the hover tooltip, and
`GameAttackLineEst`, the attack line) with the recorded flight and with `EstAtLaunch`, per family. Only
captures that carry the fields are read; grouped salvos are scored on their last round:

    dotnet run -- readouts [corpus-dir] [--rounds]

`-` in place of a directory means the stored corpus, so `score -` is the usual command.

Set `AUTOTOT_NO_WAYPOINT_LEGS=1` to replay with the non-kinematic waypoint route switched off, so one
build can be scored with and without it.

Set `AUTOTOT_NO_LEAD_SWITCH=1` to measure stage switches to the ship again instead of to the missile's
lead intercept point. A capture that predates the recorded switch target derives it from its raw
ammunition fields; one without `Ammo.MaxLoftAltUnity` (before `rich-flight-v3`) keeps the older rule.

A replay ends where the round's last step passes through the target's hull, as the mod's estimate does: the hull
the capture recorded (`HullSource`, `HullLengthU`, `HullBeamU`, `HullHeightU`, `HullAxis`), sized by what the
shooter's side knew (identified: the target's collider; a contact label showing a size class: that class's
representative hull; noise or no track: the middle class). A capture from before the hull rule gets its target
name's median hull from the corpus's world-aligned collider bounds and courses, which is what an identified target
gets. Set `AUTOTOT_NO_HULL_ARRIVAL=1` to end every replay 3 u from the target's centre instead.
The CSV carries `hullArrival` (1 when a hull was applied), `arrivalFlatM` (flat distance
from the target's centre at the end of the replay) and `heightAt202M` (the round's height above the target's
centre on its first step inside 3 u).

Six corrections to the flight path are on in the mod, and each has a switch that turns it off for an A/B:

- `AUTOTOT_NO_POPUP_LOOKAHEAD=1`: a steering look-ahead that reaches a terminal pop-up leg takes the leg's end
  height again, instead of that leg's own curve and climb limit as `Waypoint.CalculateFuturePointRecursive` does.
- `AUTOTOT_NO_POPUP_CLAMP=1`: a cruise missile's active pop-up leg is no longer held to its pop-up angle below
  0.95 progress.
- `AUTOTOT_NO_TERMINAL_TURN_STAGE=1`: the terminal vertical turn rate applies only inside a pop-up leg again,
  instead of for the whole TerminalApproach stage (from the terminal distance once the flight is older than the
  terminal delay, tested against the raw turn rate for a cruise missile and the derated one for a kinematic one).
  The delay is the capture's `TerminalDelaySec`; an older capture takes it from `Ammo.TerminalDelay` and
  `Ammo.MinLaunchRangeInMiles` when a disposable enrichment added them, and is 0 without them.
- `AUTOTOT_NO_LIVE_KAPPA=1`: a kinematic loft round steers by the sampled `AltNodes` curve again, instead of the
  live KappaLoft curve (from the round's position when its initial phase ends to the leg's end point, at the
  steering point's progress by distance to the target, with no `MaxLoftAngle` clamp).
- `AUTOTOT_NO_ROTATE_DEADBAND=1`: a kinematic surface-target round always turns, instead of holding its nose on a
  step where its forward and its steering direction already agree to a dot above 1 - 1e-5 (0.256 degrees), as
  Unity's `Vector3.RotateTowards` does in `WeaponBase.setCourseTowardsPosition`. The hold never applies in the
  terminal split (angles by `MoveTowards`) or against an air target.
- `AUTOTOT_NO_KIN_TERMINAL_LINE=1`: a kinematic round's Linear terminal leg starts at its geometric onset and flies
  a pitch latched at entry again, instead of starting at the terminal distance (every altitude leg of a kinematic
  round allows exceeding its angle limits, so it holds its height to the boundary) and steering each step at the
  1.5 s point on the line from where it entered the leg to the terminal height. Reaches `plan_yj-20` and
  `usn_rim-174b`.

The game's motor clock is `GameTime.missionElapsedTime`, which advances once per rendered frame, so every tick of a
frame reads the clock as the frame began and a booster burns on to the end of the frame its nominal end falls in.
Set `AUTOTOT_THRUST_FRAMES=n` to take the motor clock from the recorded `FlightTrack` rows under 5 s (each row is a
frame start at that cadence), each gap split into `n` equal frames. Set `AUTOTOT_THRUST_LAG=s` to delay the motor
clock by `s` seconds after those rows, or everywhere when frames are unset. `AUTOTOT_THRUST_LAG=auto` delays it by
half a frame of game time at the round's `MaxCompression` and 60 fps; `AUTOTOT_THRUST_LAG=recorded` by the round's
own `ThrustClockLagSec`, a key only a disposable enrichment writes (0 when absent). The overrun is zero with a steady
frame rate and random with an irregular one, so `recorded` is the replay of a per-round input, like the motor roll,
and `auto` is kept only as the test that showed an expected lag does not belong in the estimator. `score` and
`pathcmp` read both. Counterfactuals only.

Set `AUTOTOT_NO_DOUBLE_CLOCK=1` to return the loop's float32 clock as the flight time again instead of the double sum
of its steps. The float sum reads early on long flights at small steps (0.1 s short by 600 s at 1/60 s), which is what
made the flown-step column look earlier than the 0.1 s production column.

Set `AUTOTOT_NO_SEEKER_BRAKE=1` to gate the drag brake on the older `phase 2, height above 613.5 u, pitch below -40`
stand-in instead of the seeker window, and `AUTOTOT_NO_KIN_LINE_LOOKAHEAD=1` to hold a kinematic round's height to the
terminal distance instead of starting down where its 1.5 s look-ahead crosses into the terminal leg.

Three counterfactuals of the production-step difference, each off by default: `AUTOTOT_ARRIVAL_INTERP=1` places the
arrival inside its step, from the hull's slab entry fraction or the 3 u crossing; `AUTOTOT_GAME_ROUNDING=1` moves each
step by the velocity the game realises on its float32 grid at the round's physics step, accumulated in double;
`AUTOTOT_MOTOR_SUBSTEPS=1` integrates a kinematic round's thrust and drag in physics-step substeps while its motor
burns. `score` sets the physics step per round from the capture. Counterfactuals only.

Set `AUTOTOT_NO_TOBEARING_OWN_PITCH=1` to add the banking roll budget to the turn rate during a ToBearing
turn-rate override again, as the model did before the override was found to give pitch its own rotation.

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

`StepChanges` on the result is `t:step` for every physics step the round flew under, starting with
the step it launched on. `MinFlownStep` and `MaxFlownStep` say only that a step change happened
somewhere; this says when. That matters because a step change is the largest single reason the
corpus discards a round, 59 of 94 exclusions, and a change at t+0.3 while the frame rate is still
settling after a mission load is a different fact about the round from one at apex under load.
The scoring replay flies it. A round whose step moved is no longer excluded: `offGap@step` for such
a round is a replay at the recorded timeline rather than at one averaged step, and its reported
flown step is the time-weighted mean. A capture from before this field existed has no timeline and
is still excluded, because for it no single step describes the flight.

### motor

The scorer replays each round with the motor multiplier it flew, on the grounds that this removes
the per-round thrust roll. That only holds while the model's response to thrust matches the game's.
`motor` sweeps one captured input across a set of multipliers and reports
`d(estimate)/d(motor)` in seconds per unit and as a multiple of the round's flight time, which is
the form the live measurement comes in. The game's figure sits near 1.15 times the flight on every
family measured so far; a model figure far from that for the same weapon is a defect in that
weapon's thrust or drag handling, not in the motor term.

### launch rail

The scorer also replays a round that left a different fixed launcher from the one its capture
modelled on the rail it actually flew, on the same footing as the motor multiplier: which canister a
round leaves from is decided by the game after the solve. A ship with two opposed launchers for one
ammunition can hand a round the other canister, and captures made before `e90c13f` recorded the
canister the solve named, 180 degrees out on a Spruance Harpoon and worth 5 to 6 s of false gap.

The flown heading is the first sample of the round's own `FlightTrack`, and the opening attitude is
rebuilt from it with the mod's `FlightTime.YawOf`, which is what the mod now records at capture;
`selftest` checks that rebuild against every recorded attitude. It applies only above a 90 degree
disagreement. Over the corpus the two headings either agree to 0.05 degrees, differ by a steady 2.5
degrees on `wp_ss-n-3`, or differ by 180, so the threshold selects the different-launcher rounds and
nothing else. `score` lists every round it replayed this way, `AUTOTOT_NO_FLOWN_HEADING=1` turns it
off, and `parity` never substitutes, because it checks the input as it was solved.

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
captures without `InGameEstimate`. On the builds that made those captures the value reached the
tracker through the estimate cache, so it can belong to an earlier solve from a slightly different
position: a difference of one or two 0.1 s steps against it says nothing about the lab. Current
builds solve it from the round's own launch state with no cache between.

### climb

Peak altitude alone cannot separate the two ways a round ends up below the altitude it was commanded
to hold. If the recorded climb is still rising when the round turns over, it ran out of distance and
the climb is rate-limited by something the model does not apply. If it flattens early and holds, it
was commanded lower than the model thinks. Those are different defects with different fixes, and
`climb` is what tells them apart. It needs the `Track` series, so a round captured before that was
recorded has to be re-flown. It prints model speed beside recorded speed as well as altitude,
because a timing difference is either a path difference or a speed difference and the altitude
columns alone cannot say which.

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
- **physics step changed during flight AND the capture predates `StepChanges`** - without the
  timeline no single offline step reproduces a flight that switched, for example, from 60 Hz to
  30 Hz. A steady enlarged step has always been scoreable because the lab replays that exact step,
  and since the timeline exists a round that changed step is scoreable too: the replay flies the
  changes at the times they happened.

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

## Launch schedules

`schedule` scores launch timing rather than flight time. It reads a folder of live-test runs
(`tools/livetest/runs`), joins each round capture's launch time to its order's registration, and compares two
predictions of when each round leaves: the coordinator's shot interval and release lead, and a per-round simulation of
the game's launcher gates (`LaunchSchedule.Simulate` in `LabSchedule.cs`).

    dotnet run -- schedule <runs-dir> [--overhead s] [--last-round-waits] [--last-round-waits-for sys,sys] [--orders]

The error is the recorded minus the predicted launch offset, averaged over a unit's rounds (the last round of a grouped
salvo). A unit is an order fired alone on its ship's weapon system, or every order that shared that system in the run,
because the coordinator centres a shared launch clock's rounds as one ripple. Two errors are printed: measured from each
order's release, which is what moves the time on target, and measured from the unit's own first launch, which separates
the cadence from the first round's lead. `AUTOTOT_SCHEDULE_CSV=<file>` writes a row per order. FireRate and salvo
settings come from the installed game's `weapons.ini`.

## Launch and burnout counterfactuals

`AUTOTOT_BURNOUT_SUBSTEPS=1` integrates only the step in which a motor burns out, in physics-tick substeps.
`AUTOTOT_LAUNCH_PHASE_RECORDED=1` makes `score` replay each round with the recorded end of its Launch state as the
initial phase.

## The installed game branch

The lab copies the installed game's `Seapower-Scripts.dll` when it builds and calls the game's own thrust and drag
functions through it, so a lab built while the public branch is installed replays public code. Check before scoring:

    strings -n 6 bin/Release/net8.0/Seapower-Scripts.dll | grep -c '^EstimateShot$'

It prints 1 for the beta branch. `max-solo-error-below-1s.enrich.py` also reads the installed ammunition files.
