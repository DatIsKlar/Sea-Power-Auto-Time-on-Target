# Game-side behaviour

Four behaviours in Sea Power itself decide what an ordered salvo does: whether it leaves the rails,
in what pattern, and where its target actually goes. None of them originate in AutoTOT. The first three
were reproduced with the mod out of the loop by ordering fire manually; the fourth is arithmetic in
the game's movement code and shows in any recorded ship track. They are documented here because the
mod warns about two of them in the planner panel, because they are the first thing to check when a
salvo arrives spread out, short or off time, and because the second one looks like an engine bug
worth reporting upstream.

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

## 4. A slow ship's map speed depends on where it is

`Vessel.applyMainThrust` (`Vessel.cs:438-443`) moves a surface ship once per physics tick:

```csharp
_velocityInUnity = _velocityInKnots * 0.0076554087f;
Vector3 direction = Vector3.forward * _velocityInUnity;
Vector3 vector = base.transform.TransformDirection(direction);
vector.y = 0f;
_velocityVecInUnity = vector;
base.transform.Translate(vector * GameTime.fixedDeltaTime, Space.World);
```

A transform position is float32, measured from the current centre tile. Adding the same small
increment every tick rounds each sum to the nearest representable value, and within one power of two
those values are evenly spaced. Per component:

```
ulp(p)          = 2^(floor(log2 |p|) - 23)
moved per tick  = round(v * dt / ulp(p)) * ulp(p)
realised speed  = moved per tick / dt
```

The rounding comes out the same on every tick, so it is a steady bias rather than noise. At 6.6 kn
and a 1/60 s step one tick is 8.42e-4 u. For a ship heading along one axis:

| distance from the centre tile | spacing | spacings per tick | realised speed |
|---|---|---|---|
| 1,024 to 2,048 u | 1.22e-4 u | 6.90, rounds to 7 | 1.015 of reported |
| 2,048 to 4,096 u | 2.44e-4 u | 3.45, rounds to 3 | 0.870 of reported |
| 4,096 to 8,192 u | 4.88e-4 u | 1.72, rounds to 2 | 1.160 of reported |
| 16,384 to 32,768 u | 1.95e-3 u | 0.43, rounds to 0 | the ship does not move |

A ship on a diagonal course rounds its x and z moves separately, so the factor depends on heading too.
It also changes with the physics step, which `GameTime.SetAdaptiveFixedDeltaTime` switches between
1/60 and 1/30 s with frame rate, and with the centre tile, whose change re-derives every position from
its geographic coordinates (`ObjectBase.cs:2224-2228`). Every speed the game reports, including
`_velocityInKnots` and `_velocityVecInUnity`, is the commanded value; only the transform shows the
realised one.

Missiles move the same way (`Missile.PerformMoveForward` adds `speed * 0.0076554087 * fixedDeltaTime * forward` to a
float32 position and writes it back at `Missile.cs:2103`), and a fast round is far from immune. Near the centre tile
a tick is hundreds of spacings and the bias is negligible, but at 16,384 to 32,768 u a 900 kn tick at 1/60 s is 0.1148 u,
58.79 spacings, rounded to 59: the round crosses the map 0.35 percent faster than its speed. That was measured on
recorded SS-N-3 flights at x = 27,800 to 29,100 u (realised minus predicted +0.003 percent, realised minus nominal
+0.355 percent, over eleven cruise stretches of 20 s or more), about 0.7 s over a 200 s cruise. The flight-time model
integrates in the same coordinates, so at the game's own step it reproduces this; at its 0.1 s production step it
rounds a different increment and does not.

AutoTOT predicts a surface target with the realised velocity (`FlightTime.TargetMapVelocity`), taken at
the solve's position and at the base step the game has held over the preceding minute of sim time, so
that the short 1/30 s stretch of a mission load or a frame-rate dip does not set the rate for a whole
flight. A ship whose position crosses a power of two during the flight, and a lasting step change or
a recentre after launch, still move the true intercept away from the prediction.

## 5. A ship's round leaves from its launcher cell, not from the ship's centre

`WeaponContainer.createAmmunitionObjectInstance` creates each round as a child of its container's gun
object and hands it that container's attachment offset:

```csharp
GameObject gameObject = instance.getWeapon(ammo._ap._ammunitionFileName, ammo._ap._type, _gunObject.transform);
WeaponBase component = gameObject.GetComponent<WeaponBase>();
component.init(_baseObject, _attachmentPositions[attachmentId], ammo._ap);
```

`WeaponBase.init` makes that offset the round's local position, so the round starts at
`_gunObject.transform.TransformPoint(_attachmentPositions[attachmentId])`. On a warship the cells sit well
away from the ship's own origin: a Type 055's YJ-18A cells are 42 to 44 m from it, a Spruance's Harpoon
canisters 16 m from it and 15 m above the waterline. In every launch recorded, the round's first position
matched a cell computed this way. An aircraft round leaves its hardpoint about 3.5 m from the airframe's
origin.

The start point changes the flight time by the distance it adds or removes along the line to the target:
40 m is about 0.15 s at 530 kn, earlier or later depending on where the target lies relative to the
launcher. AutoTOT starts its flight estimate at the mean position of the cells of the launcher the engage
task will take, the same firing-arc pick the game makes, which lies within about 2 m of the cell that
fires. A unit whose launcher has no cells keeps its own position, and the fallback estimators used on the
public branch still start at the unit's position.

## 6. A missile changes stage at the point it is aiming for, not at the ship

`MaintainHeight.takeAction` decides the terminal approach, the final-flight altitude and the sea-skimming
leg from one distance:

```csharp
Vector3 position = missile.transform.position;
Vector3 vector2 = missile._targetInterceptPosition.ToUnity();
vector2.y = position.y;
float magnitude = (position - vector2).magnitude;
```

`CalculateInterceptAimPoint` sets `_targetInterceptPosition` every tick to a lead intercept: the target
position the missile believes in, advanced by the target velocity it believes in over the time the missile
needs to get there. `CalculateLeadInterceptPoint` solves that time as a quadratic twice, the second time at
a drag-corrected average speed. The waypoint route completes its legs against the same point, and the
seeker is switched on against the same distance.

The position the missile believes in is not always where the ship is. A missile guided by its launcher
(`RadioCommand`, `Datalink`) is given the true position while guidance holds. Any other missile takes the
ship's position when it leaves the ToBearing stage and advances it at the ship's reported velocity until
its own seeker locks. Section 4 describes a slow ship whose motion over the map differs from its reported
speed, and for that ship the estimate drifts away from the real hull: a YJ-18A fired at a merchant
reporting 6.60 kn that covers the map at 5.74 kn carries an estimate 156 m ahead of it five minutes into
the flight.

The lead and the drift together put a YJ-18A's terminal switch 310 to 525 m outside its declared terminal
distance, measured to the ship. The YJ-18A accelerates from 530 to 1900 kn at that switch, so measuring
to the ship made its flight 0.7 to 1.3 s longer than it is. Once a missile has entered TerminalApproach it
stays there, even when the seeker lock moves its known target back outside the threshold.

AutoTOT computes the same intercept point at every integration step from what is known when the shot is
planned: the target's position and reported velocity, the ammunition's drag parameters, and the modelled
flight. INS drift, aim deviation and the seeker's own estimate are random per missile and are left out.
A `ForceDirectPoint` weapon never updates its intercept point after launch, and semi-active homers against
aircraft measure to the target itself, so both keep measuring to the target.

## 7. A missile's motor clock advances once per rendered frame

`Missile.PerformMoveForward` (`Missile.cs:3133`) asks for this tick's thrust with the time since the booster
started:

```csharp
num = MissileSimulator.CalculateThrustOverTime(_ap, _airLaunched,
    (float)(GameTime.missionElapsedTime - _boosterEffectStartTime), GameTime.fixedDeltaTime) * _motorPerformance;
```

`missionElapsedTime` advances in `GameTime.OnUpdateUnpaused` (section 3), which runs once per rendered frame, while
`PerformMoveForward` runs on every physics tick. Every tick of a frame therefore reads the clock as the frame began.
`CalculateThrustOverTime` returns full booster thrust whenever the booster's remaining time is at least one tick, so
the booster burns to the end of the frame in which its nominal end falls. The sustainer's end, `WeaponBase._dt`, and
every other `missionElapsedTime -` timer in `Missile.cs` are quantised the same way.

How late the booster ends depends on the frame pacing, not on the frame length alone. The clock starts on a frame
(`ScheduleFlightEffects` stamps `_boosterEffectStartTime` from `missionElapsedTime`), so with a steady 60 fps at 10x
compression, frames of exactly 1/6 s land on a booster that ends on a whole second and it ends on time. With an
irregular frame rate the end falls anywhere in a frame. At 10x a frame is about ten ticks, and a RIM-66P booster that
ran 0.12 s past its 4 s was recorded 40 kn faster than one that ended on time, a difference that lasted the whole
flight. At 1x and 60 fps a frame is one tick and the effect vanishes.

AutoTOT ends each motor on time. The overrun cannot be known when the shot is planned, and an expected half-frame
lag was tested against recorded flights and made rounds from steady sessions worse. The offline lab replays a
round's measured overrun the way it replays the round's motor roll.

## 8. A kinematic missile's nose moves in 0.256 degree steps

`WeaponBase.setCourseTowardsPosition` turns a `Kinematics = Full` missile toward a surface target with one call
(`WeaponBase.cs:1731-1733`):

```csharp
vector2 = Vector3.RotateTowards(base.transform.forward, target, num * (MathF.PI / 180f) * GameTime.fixedDeltaTime, 0f);
base.transform.rotation = Quaternion.FromToRotation(base.transform.forward, vector2) * base.transform.rotation;
```

Unity's `Vector3.RotateTowards` treats two directions whose dot product exceeds `1 - 1e-5` as the same direction and
returns the current one. The nose therefore holds until the steering error passes `acos(1 - 1e-5)`, 0.2562 degrees,
and then turns the whole error in one tick. On a RIM-66P's descent from its loft apex, sampled once per frame, the
pitch rate was exactly zero on 97 to 98 percent of ticks, and every nonzero sample was 15.4 to 16.9 deg/s, which is
one 0.256 degree step in a 1/60 s tick. Eighteen degrees of descent took 71 such steps, one about every 49 ticks.
The same held at 2x and at 10x compression.

The hold matters because of how drag reads the pitch rate. `Missile.OnFixedUpdate` copies `_currentPitch` into
`_prevPitch` each tick, and `PerformMoveForward` passes `(_currentPitch - _prevPitch) / fixedDeltaTime` to
`MissileSimulator.CalculateDrag`. With the motor off, that function adds a stall term scaled by the square of
`clamp01(|cos pitch| - v * max(pitchRate, 0) / g)`. A smooth nose-down rate keeps that factor small on a ballistic
descent; a held tick sets it to `|cos pitch|` and charges the full term. Over a RIM-66P's motor-off descent that is
67 to 74 kn of speed by the terminal phase.

The branch does not apply everywhere. In TerminalApproach, when the ammunition's terminal vertical turn rate differs
from the derated turn rate by more than 1 deg/s, the same function turns pitch and yaw separately with `MoveTowards`
on angles. An air target takes a `Quaternion.AngleAxis` rotation guarded only by a 1e-12 cross-product test. The
legacy mover (`Kinematics = None`) uses `Quaternion.RotateTowards`, which snaps to the target inside its own
threshold instead of holding.

AutoTOT models the hold on the kinematic surface-target path with the same test and the same exclusions
(`FlightTime.RotateHolds`). On every recorded RIM-66P, HHQ-9B and RIM-174B round checked, it removes the motor-off
speed error.

## 9. A missile's recorded flight ends a frame before it hits

Against a ship, a missile's flight ends in `WeaponBase.CheckCollision`, a raycast along each physics tick's step (the
proximity fuse returns early for a surface target, `Missile.cs:1633`). The collision destroys the round on that tick,
and `WeaponBase.destroyObject` hands a missile straight to `PoolManager.freeObject`, so by the next rendered frame the
object is back in the pool and its transform no longer describes the round.

Anything that samples missiles once per rendered frame therefore sees a round alive on its last frame and gone on the
next, and the collision lies between the two. At 10x compression a frame is about 1/6 s of game time: the last
sighting comes up to 0.2 to 0.3 s before the hit, 40 to 260 m from the ship's centre. At 2x it is within 0.03 s.
Reading the impact point from the destroyed round's transform does not work, because the pool has already taken it
back.

On a traced round, AutoTOT repeats the game's collision cast from the last sighting (position, nose direction and
speed) against the target's colliders (layer mask -131073), as far as the round could fly before it was found
missing, and takes the time of the hit from the distance along the cast. On the 13 rounds where it found a hit, every
impact time lay between the last sighting and the first frame the round was missing. The impact, gap and residual
lines report that time, marked `to the hit`, and the last sighting otherwise.
