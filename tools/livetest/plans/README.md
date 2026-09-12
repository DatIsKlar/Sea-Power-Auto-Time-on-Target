# Live test plans

One `[section]` per game launch. The driver splits a multi-section file itself and runs the
sections back to back, one process each.

Keys: `Mission`, `Compression`, `WaitForContactSec`, `TimeoutSec`, and `ShotN` lines shaped
`Shooter > Target | ammo | salvo`. Both names are mission-ini section keys such as
`Taskforce1Vessel2`. Ammo `*` picks the first missile aboard that matches the target and is
inside the launch band. A shot that cannot be resolved rejects the whole run at once, which
is deliberate: a plan should be a deterministic test, not an exploration.

`Compression=10` is the physics cap. Above it the game enlarges the integration step, and the
offline lab excludes those rounds from scoring. The game itself limits compression to 20x with
missiles in flight, and the physics tick is 60 Hz or 30 Hz under load; every round's observed
compression and flown step are recorded in the result file.

The four standing scenarios:

| Plan | Mission | What it covers |
|---|---|---|
| `slava-ssn.ini` | test tot slava | Soviet SS-N family, grouped missiles, long range |
| `mixed-kinematic-hypersonic.ini` | teest | two shooters, one kinematic and one hypersonic weapon |
| `multi-domain.ini` | test aa | multi-target strike from ships and a submarine |
| `big-strike-fps.ini` | test sandro fps | many shooters at once, performance under a large commit |

## Instrumentation, per section

`VerboseLogging` is always on: the runner's own done-detection reads the launch expectations that
only that gate registers. The rest are plan keys, so each scenario asks for what it needs.

| Key | Default | What it adds |
|---|---|---|
| `TraceFlightModel` | true | Flight-model internals: the gap line, per-missile track, stage transitions, launch geometry, guard frames. Roughly ten times the log volume and extra sims per round. Turn it OFF for performance runs. |
| `VerticalProfile` | false | One-second depth and altitude traces for submarines and aircraft. |
| `Profiling` | false | Per-frame timing every 60 frames. |
| `VerifySolve` | false | Re-runs every threaded simulation on the main thread and warns if the answers differ. Doubles simulation work. |

Three settings are read once at mod startup and cannot be changed from inside a run: `Enabled`,
`AutoModeOnStart` and `EstimatorThreads`. The driver can edit the config file for those and always
restores the original afterwards:

```
CFG_OVERRIDE="EstimatorThreads=0" tools/livetest/run.sh plans/big-strike-fps.ini
```

## What each run collects

`tools/livetest/runs/<timestamp>-<section>/` holds:

- `LogOutput.log`, the whole BepInEx log with carriage returns stripped. It is overwritten on
  every launch, so do not start the game by hand between a run and reading its folder.
- `<section>.result`, the runner's own summary: status, gates, compression, and one `round=` line
  per round carrying outcome, final range, flight, predicted impact, residual, estimator gap,
  retarget flag, observed compression, flown physics step and the corpus key.
- `solve/`, the `.solveinput` and `.result` pair for each round that was dumped. The input is the
  complete solver state (every ammunition parameter, derived model state, launch attitude,
  waypoint legs, altitude nodes, target position and velocity) and replays offline in `tools/lab`.
  The result carries the flown outcome plus the altitude, flight and guard time series, the stage
  transitions with timestamps, and the game build the round was flown on.
- `driver.txt`, the section name, status, process id and wall time.

## The corpus

Every automated run imports its captures into the checked-in corpus at `tools/lab/corpus/` and
rebuilds `index.csv`, so a real round flown today is available to score a model change tomorrow
without launching the game. Import is keyed on the round id and skips duplicates, so re-running a
plan is safe. Set `IMPORT=0` to skip it.

Rounds that never landed are imported too. Their outcome records `Arrived=false` with the final
range, and the index carries `arrived`, `retargeted`, `minCompression`, `maxCompression` and
`flownStep`, so scoring filters them rather than losing them.

```
tools/lab/bin/Release/net8.0/autotot-lab score tools/lab/corpus   # replay and score every round
tools/lab/bin/Release/net8.0/autotot-lab parity tools/lab/corpus  # lab against the recorded estimate
tools/lab/bin/Release/net8.0/autotot-lab selftest                 # invariants
```

The lab's apphost needs a .NET root, so invoke it as `dotnet .../autotot-lab.dll` when `dotnet`
is not on PATH.

## Holding fire until the geometry is right

Two optional keys delay the shot without touching the mission:

| Key | Meaning |
|---|---|
| `HoldUntilSec` | Wait this many seconds of SIM time from mission start. |
| `HoldUntilRangeNm` | `Shooter:nm`. Wait until that shooter is inside that range of its target. |

The range form exists because which shooter anchors a strike is decided by enroute time, and a
closing aircraft crosses over. A Hornet 70 nm out has a longer Harpoon flight than the ship beside
it and is elected anchor. Let it run in to 30 nm and the same mission tests it as a follower.

## Deriving a mission

`tools/livetest/mkmission.py <base.ini> <new-name> [edits...]` copies a mission and applies edits,
shifting a moved unit's waypoints by the same delta so it keeps its track. Edits are `move`,
`nudge`, `alt`, `heading`, `copy` and `drop`. It refuses to overwrite an existing file, so a
hand-made mission cannot be clobbered. Example, duplicating a target so a heavy salvo cannot sink
its way out of the measurement:

```
tools/livetest/mkmission.py ".../mission 1 ship 1 plane 1 merchant.ini" \
    "autotot aircraft follower.ini" copy Taskforce2Vessel1 Taskforce2Vessel2 0 1.2
```

## The ammunition file is not the authority

`ObjectBase.DoesAmmoMatchTarget` decides what a shooter may fire at, and it refuses things the
ammunition file appears to allow. `usn_ircps` declares `TargetType=ASuW` with
`LandAttackCapability=All`, which reads as an anti-ship weapon, and the game still answers
"ammunition does not match target" for a merchant. Always settle a compatibility question with an
inventory probe rather than by reading the ini.

A `type` swap in `mkmission.py` is useful for putting a specific ship into an existing mission, but
check the resulting session log: swapping a hull into a section written for another one can leave
sensor references the new vessel does not define.
