# Game-side launch behaviour

Three behaviours in Sea Power itself decide whether an ordered salvo actually leaves the rails, and
in what pattern. None of them originate in AutoTOT, and all three were reproduced with the mod out
of the loop by ordering fire manually. They are documented here because the mod warns about two of
them in the planner panel, because they are the first thing to check when a salvo arrives spread out
or short, and because the second one looks like an engine bug worth reporting upstream.

All line references are to the decompiled 2026-09 beta branch.

## 1. Missile-group capacity is shared between shooters

`GroupSize` in an ammunition ini is the member limit of one `MissileGroup`. In the base game only
two weapons group: SS-N-12 at 16 and SS-N-19 at 24.

Groups are held on `Taskforce._missileGroups` (`Taskforce.cs:83`), not on the firing ship, and
`WeaponSystem.CheckForMissileGroup` (`WeaponSystem.cs:1783`) accepts a group when four conditions
hold: same `_ammunitionFileName`, `Count != _maxMembers`, the leader's aimpoint is near the new
target, and the group **leader** is within `GroupJoinRange` of the firing mount.

Group membership is what lets a guidance-requiring round launch without its own fire-control
channel. In `WeaponSystemLauncher.OnUpdate` (`WeaponSystemLauncher.cs:435-450`):

```csharp
if (_ammoForEngage._ap._requiresGuidance) {
    CheckExternalRadarGuidance();
    flag = CheckForMissileGroup(_ammoForEngage._ap);
    if (!flag) {
        ...
        if (!hasFreeWeaponChannel()) { TryHandOverToBearingLauncher(); ReadyUpWhileWaiting(); return; }
    }
}
```

So the first round away forms a group and holds one channel, and every later round that joins skips
the channel test. One ship therefore empties its magazine through a single channel, which is why a
lone Slava fires all 16 SS-N-12.

The trap is a second shooter inside the ammunition's join range. Its rounds join the first ship's
group rather than forming their own, so it never engages its own channels. Once the shared group
reaches `GroupSize` nothing further can join, neither ship has a spare channel, and both launchers
park in `ReadyUpWhileWaiting` with loaded tubes and full magazines.

`GroupJoinRange` is read in nautical miles and converted at `AmmunitionParameters.cs:1756`:

```csharp
_groupJoinRangeUnity = ini.readValue("Guidance", "GroupJoinRange", 20f) * 27.559496f;
```

SS-N-12 declares 20 nm. Two Slavas at 8 nm and at 13 nm put up 16 rounds between them; the same pair
at 25 nm and at 40 nm put up all 32. Formation and taskforce membership are not the variable, and
different ammunition types never interact: an SS-N-19 shooter alongside SS-N-12 shooters is
unaffected, because the group match is on `_ammunitionFileName`.

**What the mod does.** `LauncherFactsSource` exposes `MaxGroupSize` and `GroupJoinRangeU`, and
`Hud.RecomputeGroupSharing` warns when checked shooters within the join range have staged more
rounds than the group can hold. The salvo is not trimmed: the condition is one the player can remove
by repositioning, and trimming would discard rounds that would fly perfectly well if the ships were
spread out. `PrepareIntent` does cap wave 1 at `MaxGroupSize` for release-lead purposes, since
timing the ripple on rounds that cannot fly would push the ones that do fly out of the window.

## 2. Beam-only launchers are steered to three degrees, not to their arc

This is the behaviour that looks like a bug. See "Reporting notes" below.

`AlignUnitToTarget.CalculateFiringArcs` (`AlignUnitToTarget.cs:1011-1122`) does not steer to the
launcher's declared firing arcs. When more than one weapon system takes the engage task, it first
classifies the combined arcs:

| Flag | Meaning | Test (non-wrapping arc) |
| --- | --- | --- |
| `flag3` | covers the port beam | `x < -93 && y > -87` |
| `flag4` | covers the starboard beam | `x < 87 && y > 93` |
| `flag` | covers dead ahead | `x < -3 && y > 3` |
| `flag2` | covers dead astern | wrapping arcs only |

When both beams are covered and neither ahead nor astern is, it discards the real arcs entirely:

```csharp
_fullFiringArcs = Utils.CopyList(list);
list.Clear();
list.Add(new Vector2(-93f, -87f));
list.Add(new Vector2(87f, 93f));
```

The ship is then steered until the target is within three degrees of exactly abeam, while
`WeaponSystem.targetIsInFiringArc` (`WeaponSystem.cs:1277`) still uses the full declared arc. The
two disagree, so the ship keeps manoeuvring long after it is able to shoot.

The observable result on a Kynda firing SS-N-3b, whose two mounts both declare
`FiringArcs=-105,-75|75,105`: the ship turns, fires a linked fore and aft pair, drifts out of the
six degree band, turns again, and fires the remaining six. Because both beam windows qualify, the
second turn can settle on the opposite beam, so the ship ends up broadside on one side and then the
other. The turn-direction hysteresis at `AlignUnitToTarget.cs:794-816` commits to a direction for
two seconds before allowing a reversal, which permits that flip.

Every affected system in the base game, found by applying the classification above to all vessel,
land-unit and aircraft inis:

| Ship | System | Declared arcs |
| --- | --- | --- |
| Kynda | SS-N-3 | `-105,-75 \| 75,105` |
| Luda I | HY-1 | `-110,-75 \| 75,110` |
| Jianghu I, Jianghu II | HY-1 | `-110,-75 \| 75,110` |
| Saar 3 | Gabriel | `-160,-65 \| 65,160` |

Arc width is not part of the test. The Saar 3's windows are 95 degrees wide and are still replaced
by six degree windows.

**What the mod does.** `LauncherFactsSource.WillManoeuvreToFire` reproduces the classification from
the declared arcs, and the planner panel warns that the salvo may split across turns. It states no
number, because the split is not predictable (see section 3). The check requires two or more mounts,
matching the game's own `weaponSystemsToUse.Count > 1` guard, and requires that the mounts declare
identical arcs rather than reimplementing `IntersectFiringArcs`.

### Reporting notes

Points that suggest this is unintended rather than a design choice:

1. The declared arcs are saved to `_fullFiringArcs` and then not used for steering. A launcher with
   a 95 degree window is aimed as if it had a 6 degree one.
2. Steering and firing use different arc definitions, so a ship manoeuvres while already able to
   fire. On a multi-round salvo this splits the launch into bursts separated by ship turns.
3. Because both beam windows satisfy the goal, a ship can reverse its turn and present the opposite
   beam mid-salvo.
4. The resulting behaviour is not reproducible across time-compression settings (section 3), so the
   same order produces different launch patterns depending on a player setting.

Minimal reproduction: a single Kynda, one surface target, order all eight SS-N-3b at it, and observe
at 5x compression. Ordering manually through the game UI is sufficient; no mod is required.

## 3. Above 10x compression, hull physics runs slower than sim time

`GameTime.OnUpdateUnpaused` (`GameTime.cs:330-343`):

```csharp
float num = TimeCompression / Time.timeScale;   // becomes TimeDilation
deltaTime = TimeCompression * Time.deltaTime / Time.timeScale;
missionElapsedTime += deltaTime;
```

`_physicsTimeScaleCap` is 10. At or below it, `Time.timeScale` equals the compression and sim time
tracks physics exactly, so `TimeDilation` is 1. Above it, `Time.timeScale` is pinned at 10 while
`TimeCompression` keeps its full value, so `missionElapsedTime` advances faster than Unity's physics
does and `TimeDilation` rises above 1.

Hull rotation is Unity rigidbody physics; launcher intervals such as `SharedLaunchInterval` run on
`missionElapsedTime`. A ship therefore rotates less per simulated second at high compression than at
low. Eight SS-N-3b at a five second shared interval span roughly 35 simulated seconds either way,
but at 20x the ship has physically turned only half as far during them, never leaves the three
degree band described in section 2, and fires the whole salvo in one go. At 5x it turns at full rate,
leaves the band, and has to re-align. The low-compression behaviour is the physically intended one.

The consequence for any timing model is that a manoeuvring ship's ripple span is not a function of
simulated time. AutoTOT therefore does not model these turns, and instead relies on observation
anchoring, which re-predicts the impact from launches actually seen. The `commit` log line records
`compression` and, when dilation exceeds 1, marks the order `PHYSICS APPROXIMATED`, so a report of a
spread-out arrival can be told apart from a flight-time estimator error.
`Diagnostics/VerticalProfiler.cs` applies the same marker to its own runs.
