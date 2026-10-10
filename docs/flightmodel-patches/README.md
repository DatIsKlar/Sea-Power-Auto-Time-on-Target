# Flight-model fixes waiting to be pushed

The audit in [`../AUDIT-beta.md`](../AUDIT-beta.md) fixed the flight model (SeaPowerFlightModel) as well as
the mod, but the session that made the fixes could not push to `DatIsKlar/SeaPowerFlightModel`, because the
Claude GitHub App has no access to that repository. The four model commits are here as patches instead, made
against `b251df2`, the commit the `FlightModel` submodule pins. Until they are applied, beta builds against
`b251df2` and none of the model fixes are in the DLL.

| Patch | What it fixes |
|---|---|
| `0001-Contain-step-loop-faults-…` | Step-loop throws contained on both solve paths; only the in-flight request publishes; stranded timer split into solve and queue time; no second main-thread integration on a decline; worker results dated to their capture; planner ETA solve budget; safe `InterceptTime` read; worker fault counters reset per mission |
| `0002-Bind-the-game-s-sim-helpers-…` | Thrust, drag and loft helpers bound by exact signature; missing drag reported; game clock binding guarded |
| `0003-Time-a-submarine-s-ascent-…` | Ascent timed to the launch gate (`GateDepthU`); no-progress bail ignores a reversal |
| `0004-Remove-a-stray-BOM-…` | Cosmetic only |
| `mod-submarine-launch-gate.patch` | **Mod side**, applied after the bump: passes `GateDepthU` from `SubmarineFacts`. It needs patch 0003, so it is not on beta yet |

## Applying them

From the mod repository root, on `beta`:

```
git submodule update --init                       # if FlightModel/ is empty
cd FlightModel
git fetch origin
git checkout -b beta b251df2
git am ../docs/flightmodel-patches/000*.patch
git push -u origin beta                           # SeaPowerFlightModel gets a beta branch
cd ..
git apply docs/flightmodel-patches/mod-submarine-launch-gate.patch
git add FlightModel Support/SubmarineFacts.cs
git rm -r -q docs/flightmodel-patches
git commit -m "Move FlightModel to the model's beta fixes and time submarine launches to the gate"
git push
```

If you build against a separate model checkout (`FlightModelDir` in `AutoTOT.local.props`, normally
`../SeaPowerFlightModel`), run the `git am` there instead and point the submodule at the pushed commit.

Before the push, check that the lab's `parity` and `selftest` still pass, as described under Verification in
`../AUDIT-beta.md`. None of these patches change the step loop's arithmetic, apart from the ascent model in 0003.

Alternatively, give the Claude GitHub App access to SeaPowerFlightModel
(<https://github.com/apps/claude/installations/select_target>) and ask for the push to be redone from a
session.
