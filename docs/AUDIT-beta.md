# Audit of the beta branch and the flight model

Audited on 2026-10-10:
- **Mod:** `beta` at `05aa330`.
- **Flight model:** the `FlightModel` submodule (SeaPowerFlightModel) at `b251df2`.

File and line references point at those two commits. The fixes move lines around, so read each reference
against the audited commit, not the current tree.

## Summary

- **What beta changed since Release 0.2.1.**
  - A comment-stripped diff shows the slim-down left `Core/` and `UI/` code untouched; only comments changed
    there.
  - The real code changes:
    - Bootstrap: the VerticalProfile and AlignmentTrace switches went, and the flight-model commit stamp was
      added.
    - Five files were deleted: the four research probes (TargetMotionProbe, AlignmentProbe, VerticalProfiler,
      GuidanceStateProbe) and WaypointSim.
    - New corpus fields in the launch diagnostics.
  - The model sources in the submodule are byte-identical to the ones that lived in this repository before
    the move.
- **Two release-state points:**
  - The version is still `0.2.1`.
  - `dist/AutoTOT/AutoTOT.dll` is the 0.2.1 build. It still contains the deleted probe types, so beta has
    never been built into `dist/`.
- **Results.** 37 issues fixed, 18 reported only plus the release state (R1), and 2 agent claims dismissed after checking.
  - **Mod:** fixed in commits `ae15e69`, `ec9109c` and `f9c37aa` on beta.
  - **Flight model:** fixed in four commits that could not be pushed. The session had no GitHub access to
    SeaPowerFlightModel, so they are in [`flightmodel-patches/`](flightmodel-patches/README.md) with
    instructions.
  - **Reported only:** issues that need game knowledge or measurement, or are design choices, are listed
    below.
- **Nothing was compiled or run.** The audit environment has no .NET SDK and no game DLLs.
  - Every changed C# file was parse-checked with a C# parser.
  - The submarine change was checked against a Python port of its loop.
  - The `mkmission.py` fix was run on a synthetic mission.
  - The builds and live tests listed under [Verification still owed](#verification-still-owed) are needed
    before a release.

Severity:
- **High:** can stop coordination, or fire or hold orders wrongly.
- **Medium:** wrong timing or a broken feature in a real scenario.
- **Low:** diagnostics, tooling, edge cases.

Status:
- **mod:** fixed on beta, in the named commit.
- **model:** fixed in the named patch, waiting to be pushed.
- **reported:** not changed.

## Findings

| # | Sev | Finding | Where (at audit) | Status |
|---|---|---|---|---|
| 1 | High | A throw in the flight model's step loop escapes the synchronous path into `Coordinator.Tick`; on a worker it strands the key for 10 s | model `FlightTime.Integrator.cs:760-768`, `FlightTime.Async.cs:134-157` | model 0001 |
| 2 | High | One fault repeating every tick stops every held order releasing: pending, commit and anchor stages share one `try` with release, and a faulting pending commit is never removed | `Core/Coordinator.cs:643-663`, `Core/Coordinator.Pending.cs:119-133` | mod `ae15e69` |
| 3 | High | Anchor fallback snaps every held order back to the commit-time impact: `PredictAnchorImpact` returns `a.ImpactAtSim`, which `PublishImpact` never updates for the anchor | `Core/Coordinator.Anchor.cs:34,44,500-506` | mod `ae15e69` |
| 4 | High | With the panel hidden, the indicator off or the mod disabled, the panel calls the game's `setMouseIsOverUIWindow(false)` every frame, wiping the flag the game sets for its own windows | `UI/Hud.cs:224-298` | mod `ec9109c` |
| 5 | Medium | A stalled anchor ripple is held open, and the impact keeps sliding later, for up to 300 s: the stall test reads an ascent verdict frozen (usually true) at the first launch | `Core/Coordinator.Anchor.cs:136-141,322,354-356` | mod `ae15e69` |
| 6 | Medium | With auto-coordination off and no strike armed, an order inside minimum range is still swallowed (the refusal runs before the auto-off test), contrary to the README | `Core/Coordinator.cs:468-491` | mod `ae15e69` |
| 7 | Medium | After an eviction and re-queue (or a reset), an older worker request publishes over the fresh one and the fresh result is discarded | model `FlightTime.Async.cs:182,204-218,244` | model 0001 |
| 8 | Medium | The stranded-key timer starts at enqueue, so a backlog on a slow machine evicts healthy keys | model `FlightTime.Async.cs:183` | model 0001 |
| 9 | Medium | A declined worker result re-runs the full integration on the main thread (`KinematicRaw`), the most expensive solve there is | model `FlightTime.Async.cs:284`, `FlightTime.cs:486` | model 0001 |
| 10 | Medium | Planner ETAs run a full synchronous integration per row inside OnGUI, and every row's 5 s entry expires in the same frame | model `FlightTime.cs:184-192`, `UI/Hud.Render.cs:293` | model 0001, mod `ec9109c` |
| 11 | Medium | A submarine's ascent is timed to the commanded depth, but the launch unblocks 25.4 ft deeper, at the weapon's ceiling; the startup lead runs long (Echo II validation run, ported loop: 58.3 s to 75 ft vs 49.9 s to 100 ft) | model `SubmarineAscent.cs:99`, `Support/SubmarineFacts.cs:45-46,126` | model 0003 + `mod-submarine-launch-gate.patch` |
| 12 | Medium | The ascent's no-progress bail fires during an ordinary reversal (a boat still descending on its tanks), so the launch delay reads 0 | model `SubmarineAscent.cs:102` | model 0003 |
| 13 | Medium | An unreadable sensor channel count (`int.MaxValue`, "uncapped") is summed. Two such sensors wrap negative, so every order for that ammunition is refused | `Support/LauncherFacts.cs:465,588` | mod `ec9109c` |
| 14 | Medium | Any launch of the same ammunition from the ship (FIRE NOW, AI, an order with collection off) frees a coordinated order's reserved channel, allowing over-commit | `Core/Coordinator.Channels.cs:75-88` | mod `ae15e69` |
| 15 | Medium | After a gap with no tick, the release lookahead (half the step) covers the whole gap and releases followers early | `Core/Coordinator.Release.cs:87-88` | mod `ae15e69` |
| 16 | Medium | A mission loaded without the exit being seen is detected (clock runs backwards) but only warned, so the old state carries on | `Core/Bootstrap.cs:441-445` | mod `ae15e69` |
| 17 | Medium | Corpus tracks are formatted with the current culture, so a comma-decimal locale writes files the lab cannot parse | `Diagnostics/LaunchDiagnostics.Tracing.cs:214-417,596-629`, `LaunchDiagnostics.cs:167,310,446,624`, `SolveInputDump.cs:337-338` | mod `ec9109c` |
| 18 | Medium | `mkmission.py alt` never changes waypoint heights: the regex sits inside an f-string. That is a syntax error before Python 3.12, and a backslash class from 3.12 on (reproduced on 3.13) | `tools/livetest/mkmission.py:247` | mod `f9c37aa` |
| 19 | Low | **New on beta:** `GameAttackLineEstimate` binds members that may not exist on the public branch at compile time, so it faults at JIT, before its own `try`. Trace-only; contained by the scan recovery, but loses the corpus record | `Diagnostics/LaunchDiagnostics.cs:683-698` | mod `ec9109c` |
| 20 | Low | Worker results are cached with the publish time, so `TryCached` and the release back-dating under-report their age | model `FlightTime.Async.cs:268`, `TtlCache.cs:73-78` | model 0001 |
| 21 | Low | `ResolveByName` binds the sim helpers by name and parameter count only, so an overload a game update adds could bind and then throw on every step | model `FlightTime.Reflection.cs:120-128,204-217` | model 0002 |
| 22 | Low | "integrator ACTIVE" is logged without checking drag; without it every kinematic round declines | model `FlightTime.Reflection.cs:164` | model 0002 |
| 23 | Low | `GameClock.Resolve` can throw out of `SimNow` once, and then reads 0 forever; the warning is verbose-only | model `GameClock.cs:72-103` | model 0002 |
| 24 | Low | Hard `(float)` unbox of `InterceptTime` throws on every call if the field turns double | model `FlightTime.cs:533` | model 0001 |
| 25 | Low | `WorkerFaults`/`WorkerAborts` are never reset, so after one fault the census line prints every interval for the session | model `FlightTime.Async.cs:294-320` | model 0001 |
| 26 | Low | A NaN envelope lead or group delay makes `timeLeft <= NaN` false forever, so the order is held until the mission ends | `Core/Coordinator.cs` `PrepareIntent`/`GroupDelay`, `Coordinator.Release.cs:225` | mod `ae15e69` |
| 27 | Low | A follower the game refuses leaves a never-fired board row for the rest of the mission | `Core/Coordinator.Release.cs:198-209` | mod `ae15e69` |
| 28 | Low | The past-due warning compares flight time alone with time-to-impact, missing collapses on orders with long leads | `Core/Coordinator.Release.cs:171` | mod `ae15e69` |
| 29 | Low | A fired anchor still runs `ResolveFlightEstimate` every tick, spending the per-frame solve budget | `Core/Coordinator.Release.cs:146-158` | mod `ae15e69` |
| 30 | Low | `RecoverFromScanFault` repeats the reads that may have faulted, from inside the `catch` | `Diagnostics/LaunchDiagnostics.cs:496-511` | mod `ec9109c` |
| 31 | Low | The retired-expectations cap clears right after the insert, wiping the order just retired | `Diagnostics/LaunchDiagnostics.Expectations.cs:487-496` | mod `ae15e69` |
| 32 | Low | The DOTS shield logs a fault it re-throws as "treating it as non-ECS so mission load can continue" | `Support/DotsScanHardening.cs:168,198-200` | mod `ec9109c` |
| 33 | Low | `run.sh`: with `CFG_OVERRIDE`, Ctrl-C returns to the loop and launches the next section without a plan. `Compression=0.5` divides by zero in shell arithmetic | `tools/livetest/run.sh:117,129-134` | mod `f9c37aa` |
| 34 | Low | `install.sh`: dies silently under `set -e` outside a git checkout; `GAME_DIR` is unchecked; the model-pin note looks at `../SeaPowerFlightModel`, not the checkout the build compiles | `install.sh:24-31,50-58` | mod `f9c37aa` |
| 35 | Low | `Enabled=false` still ticks the coordinator and scans every missile each frame | `Core/Bootstrap.cs:448-450` | mod `ae15e69` |
| 36 | Low | A failed or deferred ETA shows "0:00" | `UI/Hud.Render.cs:296` | mod `ec9109c` |
| 37 | Low | Stray BOM in the middle of the step-loop file (line 2) and one mis-indented line | model `FlightTime.Solve.cs:2,561` | model 0004 |
| R1 | — | Release state: version 0.2.1, and `dist/` holds the 0.2.1 DLL | `AutoTOT.csproj`, `dist/AutoTOT/AutoTOT.dll` | reported (release step) |

### Notes on the fixes

- **#2.**
  - Pending, commit and anchor now each run under their own `try` in `Tick`, and so does each order in
    `ReleaseDueLaunches`.
  - A commit that throws part way is dropped rather than retried. The orders it leaves neither scheduled nor
    pending are named in an ERROR line ("… failed part way: N order(s) were dropped and will NOT fire"),
    because they were intercepted and the game will not fire them on its own.
  - README's troubleshooting table lists the new lines.
- **#3.** `Expectations.cs:397` already assumed this field was "its live prediction rather than its scheduled
  one", so the fix restores the intended design. When every round is away, the launch-state estimate is now
  tried before giving up on a failed live estimate.
- **#5.**
  - The mid-ripple stall test is now `AnchorStillCycling`: the launcher's own intent, bounded by
    `NoLaunchMaxHoldSim` as before.
  - The pre-launch test is unchanged, ascent verdict included.
  - **Changes behaviour:** a stalled ripple from a submarine or aircraft anchor now finalizes on the rounds
    that flew after `max(4 × cadence, 30 s)` instead of up to 300 s.
- **#11.**
  - The model change is inert until the mod passes `GateDepthU`, which is `mod-submarine-launch-gate.patch`.
  - **Changes behaviour:** submarine startup leads get shorter by roughly the time to cover the last 25 ft.
    Confirm with the `envelope-residual` lines.
- **#15.** The step used for the lookahead is capped at two frames of sim time
  (`Time.unscaledDeltaTime × max(TimeCompression, 1)`).
- **#16.** **Changes behaviour:** a backwards mission clock now resets the coordinator, which discards anything
  held. The log says so.

## Reported, not changed

| Finding | Where (at audit) | Why not changed |
|---|---|---|
| A released order drops the intercepted `targetPosition` and `markAsReturned` (always `Vector3.zero`/`false`) | `Core/Patches.cs:28-33`, `Core/Coordinator.Release.cs:350-351` | Depends on what the game does with them; a stale position minutes later may be worse than zero |
| The estimate cache's 0.5 s TTL is real time; commit, anchor and pending readers take a hit as fresh, which is up to 10 sim s at 20x | model `FlightTime.cs:11`; `Coordinator.Pending.cs:150-157`, `Coordinator.cs:753`, `Coordinator.Anchor.cs:37` | Design choice; the release path already back-dates. Measure before changing |
| A menu that holds `_mainGameViewModel` null for more than 0.5 s mid-mission would reset the coordinator, and the panel would drop its picks | `Core/Bootstrap.cs:380-382,425-429` | Needs to know how long the game nulls it |
| An identified-by-class contact may show its hull name, against the comment's fog-of-war intent | `UI/Hud.Labels.cs:26,45-48` | Depends on what `Name` holds for a classified contact |
| "+ UNIT" silently adds nothing when the selected target is one the unit's missiles cannot engage | `UI/Hud.Rows.cs:158-164`, `Hud.Actions.cs:82-85` | UX choice |
| With `ToggleModifier=None`, typing F in a game text field fires the staged strike | `UI/Hud.cs` `Update` | Needs the game's text-focus API |
| `AircraftDescent` with `MaxG` ≈ 1 commands a climb and returns "never" | model `AircraftDescent.cs:93-100` | Mirrors the game's own formula |
| Planner staged rounds are counted by exact ammo id while the intake counts every ammo sharing the radar | `UI/Hud.Rows.cs:236-248`, `Core/Coordinator.Channels.cs:211-216` | Low impact; needs a sensor-aware provider |
| The flight tracker is keyed by a pooled `WeaponBase`, so a recycled object could inherit an old sample | `Diagnostics/LaunchDiagnostics.cs:135,299` | Needs evidence the pool recycles within one tick |
| `mkmission.py copy`/`drop` do not renumber units; `alt`/`heading` raise a KeyError on an unknown section | `tools/livetest/mkmission.py:339-369` | Tooling; needs the loader's indexing rule |
| `run.sh` section files from two plans can collide on number and name | `tools/livetest/run.sh:103-109` | Tooling |
| A plan file with no section never quits the game | `Diagnostics/LiveTestRunner.cs:55-60` | Live-test build only |
| `RequestRefresh` runs the game's `SearchOptimalLoftAltitude` in setup, on the main thread, for every order of a commit | model `FlightTime.Integrator.cs:838-855` | Performance; measure with Profiling first |
| `PickAnchor`'s `TryCached` and fresh-estimate branches cannot run (every caller has `CommitEstDone`) | `Core/Coordinator.cs:753-767` | Dead but harmless defence |
| The expectation deadline counts the reload gap twice | `Diagnostics/LaunchDiagnostics.Expectations.cs:131-146` | Only makes the deadline more lenient |
| `PinOverUi` calls `SetValue` without the guard `SetOverUi` has | `UI/Hud.Mouse.cs:131-142` | Teardown edge case |
| `Bypass` is reset to false rather than restored | `Core/Coordinator.Release.cs` `Fire`/`FireNow` | Calls never nest today |
| Rounds in flight at mission end leave an orphan `.solveinput`, and `AutoTOT-solve/` grows without bound | `Diagnostics/SolveInputDump.cs` | Trace-only research output |

Dismissed after checking:
- **The panel's Layout and Repaint passes could disagree when a 0.5 s cache entry expires between them.** They
  can't: `TtlCache` is clocked by `Time.unscaledTime`, which is fixed for the whole frame.
- **`ResolveFlightEstimate` duplicates a worker's solve.** It doesn't: `RequestRefresh` returns true for a key
  that is already queued.

## Checked and found sound

- **Core:**
  - No collection is mutated while it is enumerated. Every `_scheduled` removal is at the current index of a
    backward loop.
  - Re-entrancy is guarded: `Bypass` is set in try/finally and is `[ThreadStatic]`.
  - No double-fire path exists.
  - `Coordinator.Reset` covers every static.
  - Config values are clamped.
  - Unity null semantics are used correctly.
- **Flight model:**
  - `SolveInput` arrays are built fresh per setup and never mutated.
  - Workers touch only thread-safe Unity math.
  - The `ModelStats` counters are interlocked or locked.
  - `TofKey` equality and hash are correct.
  - The unit constants are mutually consistent.
  - `Asin`/`Acos` inputs are clamped.
  - The lead solver's divisions are guarded in practice.
  - The semaphore and queue cannot lose a wakeup.
- **Slim-down:**
  - Core and UI changes are comment-only.
  - LauncherFacts' 434-line diff is comment-only.
  - No live-test plan uses a key the runner no longer parses.

## Verification still owed

None of this could be run in the audit environment.

1. **Build.** `dotnet build -c Release` with `GameDir` and `AnchorChainDll` set, on beta as it stands (it builds
   against `b251df2`).
2. **Apply the model patches** (see [`flightmodel-patches/`](flightmodel-patches/README.md)), then in
   SeaPowerFlightModel run `dotnet run --project tools/lab -- parity tools/lab/corpus` and
   `dotnet run --project tools/lab -- selftest`. Both must pass unchanged, since only the ascent model's
   arithmetic moved. Rebuild the mod against the bumped submodule.
3. **Live tests** (`LIVETEST=1 ./install.sh`, then `tools/livetest/run.sh`), with `VerifySolve=true` where it
   fits:
   - `mixed-strike-follower-release.ini` and `essm-repeat.ini`: release and anchor tracking (#2, #3, #15).
   - `aircraft-anchor.ini` and `kynda-arc-commit.ini`: the mid-ripple stall rule (#5).
   - A submarine plan (`slava-ssn.ini` or `multi-domain.ini`) after the model bump: read the
     `envelope-residual` lines (#11, #12).
4. **Panel performance.** With `Profiling=true` and the panel open on a large roster with a target selected, the
   `UI (outside tick)` spikes every 5 s should be gone (#10, after the model bump).
5. **Release.** Bump the version and rebuild `dist/AutoTOT/AutoTOT.dll` when beta is shipped (R1).
