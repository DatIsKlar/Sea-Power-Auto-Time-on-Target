# 3. Trajectory

[← Parameters](02-parameters.md) · [index](00-index.md) · next: [Speed](04-speed.md)

The game does not fly missiles with proportional navigation. It runs a **guidance state machine**:
Launch → ToBearing → MaintainLoftAlt / SeaSkimming / FinalFlightAlt → TerminalApproach. Each
stage commands an altitude and a speed, and the airframe slews toward that command at a finite
rate. The integrator reproduces that machine. Trajectory shape is the only thing modelled by hand;
everything else is the game's own physics ([§4](04-speed.md)).

## 3.1 Launch geometry

A missile does not begin flight pointing at its target. It leaves the rail pointing wherever the rail
points, holds that attitude for `_initialFlightPhaseDuration`, and only then turns. Both axes matter,
and both are read from the launcher's transform rather than from any `.ini` field.

### Choosing the launcher

A ship may mount several launchers for the same ammunition, including pairs installed 180° apart,
one bearing to port, one to starboard. The round comes off whichever launcher bears on the target, so
the model iterates every launcher carrying the ammunition and picks the one whose horizontal rail
direction is closest to the target bearing, skipping rails that are vertical (no bearing to compare).
Taking an arbitrary launcher would apply a spurious ~180° turn.

### Elevation

The rail's true attitude comes from `_containers[i]._gunObject.transform`, the object the game
elevates (`WeaponSystem.alignToTarget:1379-1381`), falling back to `_containerBaseObject`
when containers are joined. Elevation is `asin(forward.y)`, floored at 0°:

```
launchPitch = max( asin(rail.forward.y) · rad2deg , 0 )
```

`launchPitch >= 0` is the sentinel for a known launch attitude; −1 means unknown, and the model
steers at the target from t = 0.

Fixed rails and trainable mounts are handled separately, because a trainable mount's *current*
transform is wherever it happens to be parked and must never be read as a launch attitude:

| launcher | source of the launch elevation |
|---|---|
| **fixed** (`!_isMountRotatable && !_areContainersRotatable`) | the rail as built: `clamp(asin(forward.y), 0, 90)` |
| **trainable** | the game's own aim is recomputed: elevation to the target (or `_fixVerticalLaunchAngle` where the launcher uses a fixed angle), plus `_additionalFixVerticalLaunchAngle`, minus the mount's own pitch, clamped to the elevation arc (`alignToTarget:1360-1377`) |

This covers vertical launch for free: a VLS cell cannot train, so `alignToTarget` returns immediately
and the cell stays as built, vertical.

### Heading

A launcher that cannot train fires **along its own bearing**. An off-bearing shot therefore spends
its initial flight phase flying the wrong way and must then turn, closure that a model steering
straight at the target from t = 0 never pays for.

So for a fixed, non-vertical rail the model carries a heading vector, the horizontal mirror of the
pitch hold: initialise it to the rail's bearing, hold it for `_initialFlightPhaseDuration`, then
`RotateTowards` the target bearing at the turn rate.

```
if launchHeading is set:
    if t >= initialPhaseDur:
        launchHeading = RotateTowards(launchHeading, horizDir, turnRate · dt)
    horizDir = launchHeading
```

### A vertical cell inherits the ship's heading

A vertical rail has no bearing of its own, so there is no rail direction to seed from. The round
still leaves the cell carrying the **ship's** yaw, though, and must turn onto the target bearing
before it can close. A vertically launched round fired abeam runs its ToBearing stage to the full
10 s cap.

So for a vertical rail the heading is seeded from the ship's flattened forward vector instead, and
the same hold-then-turn logic above applies unchanged:

```
launchHeading = rail is vertical ? horizontal(ship.forward)
                                 : horizontal(rail.forward)
```

The mechanism is inert wherever the launcher is already on-bearing or trainable. A trainable mount
aims before firing, so it has no launch turn to model.

### Pitch and heading share one budget

The two axes are not rate-limited separately, on either mover path. Non-kinematic ammunition turns
the whole airframe with a single `Quaternion.RotateTowards` covering pitch, yaw and roll at once
(`WeaponBase.setCourseTowardsPositionLegacy`). Kinematic ammunition spends one budget too: a
surface target takes a single `Vector3.RotateTowards` of the forward vector, and an air target a
single `Quaternion.AngleAxis` cone rotation (`WeaponBase.setCourseTowardsPosition`). Either way,
yawing and pitching compete for the same degrees per second, and a round that must do both
finishes later than one doing either alone.

The model does the same, for both classes:

```
att  = RotateTowards(att, Euler(-targetPitch, yaw(targetHeading)), turnRate · dt)
pitch, heading = read back off att · forward
```

**The attitude is carried from step to step**, and that is what makes the coupling work. Turning a
nose-up airframe in yaw is largely roll, and roll is real travel out of the same budget. Rebuilding
the attitude from pitch and heading each step, and reading only the forward vector back out,
discards that roll and hands the budget back every step, which reproduces independent limiting
almost exactly. Carrying the attitude keeps the cost. The target orientation carries no roll, so
whatever the turn builds up bleeds away as the round settles on its bearing.

The effect is largest where the turn is slowest and both axes are loaded. Rotating from 90° pitch
and 90° of heading error to level and on-bearing is 120° of travel, not the 90° that independent
limiting would charge; at 10°/s that is 12 s of turning rather than 9 s.

With no heading error the quaternion angle equals the pitch change exactly, so this reduces to the
plain slew above and cannot disturb a shot that has nothing to yaw.

The kinematic path excludes roll from that budget and levels the airframe in a separate call, so
the model's attitude, which carries no roll of its own, is the matching shape there. In
TerminalApproach the game splits the budget into separate yaw and pitch rates when the
ammunition's `TerminalVerticalTurnRate` lies more than 1 deg/s from the turn rate (the raw
`MaxTurnRate` on the legacy path, the derated rate on the kinematic one). The model does the same
for the whole stage, once the flight is older than the terminal delay: `TerminalDelay`, or 0.8 of
the time to fly `MinLaunchRange` at `MaxVelocity` when that is unset. Six of 446 shipped ammunition
declare such a rate.

## 3.2 The turn-rate budget

Pitch slews toward its command at `_maxTurnRateDegrees` (5°/s when unset):

```
pitchDeg = MoveTowards(prevPitch, targetPitch, turnRate · dt)
```

This finite rate produces the **loft overshoot**. A missile commanded to
level off at its loft altitude is still near +90° pitch when it arrives, and needs several seconds to
swing down, climbing the whole way. A model that levels instantly peaks hundreds of Unity units low.

### Banking adds a second budget

A **non-kinematic** missile with `_supportsBanking` gets a *second* rotation call every physics tick:
`setCourseTowardsPositionLegacy` runs its normal `RotateTowards` at `_maxTurnRateDegrees`, and then
`performToTargetRoll` runs another at a hardcoded 60°/s (`WeaponBase.performToTargetRoll`).
That second call assigns a locally-read Euler angle to *world* rotation, which is gimbal-degenerate
near vertical, so after a vertical launch its budget lands largely on **pitch**, not roll.

```
if nonKinematic && ap._supportsBanking:
    turnRate += 60
```

Modelling only `_maxTurnRateDegrees` makes such a round nose over roughly three times too slowly.

The addend applies only where the mover spends one combined rotation. When the rate in force differs from
`MaxTurnRate` by more than 1°/s, `setCourseTowardsPositionLegacy` rotates roll, yaw and pitch in three
separate calls, pitch at its own rate, and the roll call that follows aims at the pitch the round already
has. A ToBearing launch-rate override is such a case: `plan_yj-18a` noses over at exactly its 50°/s
`LaunchTurnRate` through ToBearing, not at 110°/s. The model therefore drops the addend while the override
is in force (next section).

### Two corrections ride on the base rate

The mover does not spend `MaxTurnRate` as written. It derates it for the G-limit first, then floors
it at the launch turn rate while in ToBearing, and only then dispatches to one of the two rotation
paths. The model resolves the same two in the same order, per step, and adds the banking roll addend
afterwards, because the mover's roll call runs at a hardcoded rate that no G-limit touches:

```
rate = MaxTurnRate
if knots > gThreshold:  rate *= gThreshold / knots
override = inToBearing && LaunchTurnRate > rate
if override:  rate = LaunchTurnRate
if not override:  rate += bankingRollAddend      # the three-rotation branch gives pitch its own rate
```

**The G-limit.** The mover converts the commanded rate into a load at the round's *current* speed
and cuts the rate when it exceeds `MaxTurnG`:

```
v      = 0.514444 · knots
radius = v · (360 / rate) / 2π
g      = v² / (radius · 9.8)
if g > MaxTurnG: rate *= MaxTurnG / g
```

Substituting the radius collapses `g` to `v · rate · 2π / (360 · 9.8)`, linear in speed, so there is
one speed at which the load reaches the limit. The model solves for that speed once at setup and the
step loop pays a compare. Above it the surviving rate is `rate · threshold / knots`.

This is inert for every anti-ship missile in the game: none declares `MaxTurnG`, so all take the
200 g default, and the fastest of them reaches 14.2 g. It is not inert generally. 82 shipped
ammunition declare a limit, 25 being the most common value, and 57 missiles exceed their own limit at
their own maximum speed, down to 0.26x on `usn_rim-2f`. Every one is a SAM or an AAM.

**The launch turn rate.** While in ToBearing the mover raises the rate to `LaunchTurnRate` when the
ini sets it above the rate already in hand. It defaults to -1, and three shipped ammunition set it:

| ammo | `LaunchTurnRate` | `MaxTurnRate` |
|---|---|---|
| `usn_rgm-84a` | 40°/s | 15°/s |
| `wp_sa-n-6` | 40°/s | 20°/s |
| `wp_sa-n-9` | 360°/s | 30°/s |

Harpoon is the one that matters in the base game, because it is a strike round the coordinator
plans with. Left unmodelled it turns onto its target too slowly, flies further off-axis, and the
estimate runs late. `rgm-84c` and `rgm-84d` leave the key at its default, so the error appears on
the A-variant alone.

Workshop ammunition uses the key far more: 38 of 106 modded missiles set it, against 3 of 160 in
the base game. Anti-ship rounds among them include `plan_yj-18a` (50 against 15), `knm_nsm_1a`,
`knm_3sm_tyrfing`, `plan_cj-10` and the `usn_rgm-109e` family. On a modded loadout this is a broad
correction rather than a single-round one.

## 3.3 The stage model

Three phases, selected each step by remaining horizontal distance to the predicted intercept:

| phase | selected when | altitude | speed target |
|---|---|---|---|
| 0: loft | lofting, launched beyond `loftEntryDist`, and beyond `finalDist` | `loftAlt` from the game's `LoftCap` | `_maxLoftVelocityInKnots` |
| 1: cruise | within `finalDist` | `cruiseAlt`, or `finalFlightAlt` within `finalFlightDist` | `_maxVelocityInKnots`, held at loft speed outside `loftSpeedHoldDist` |
| 2: terminal | within `diveStart` | `termAlt` | `_terminalVelocityInKnots` |

Boundaries come straight from the `.ini`, with `_loftToSkim` selecting which distance bounds phase 1:

```
toSkim         = _loftToSkim && _seaSkimmingStartDistToTargetUnity > 0
finalDist      = toSkim ? _seaSkimmingStartDistToTargetUnity : _finalFlightPhaseDistToTargetUnity
cruiseAlt      = _seaSkimmingAltUnity
finalFlightAlt = _finalFlightPhaseAltUnity
finalFlightDist= _finalFlightPhaseDistToTargetUnity
loftEntryDist  = _seaSkimmingStartDistToTargetUnity
```

### Entering the loft depends on the launch range, not on the ammunition

A round enters the loft only if it was **launched** outside `_seaSkimmingStartDistToTargetUnity`.
Coming out of `ToBearing` the stage is not `MaintainLoftAlt`, so the
`_loftToSkim || stage != MaintainLoftAlt` clause at `Missile.cs:641` is true for every ammunition
and the sea-skimming branch wins. Once there, the stage rank is 2 and the loft branch, which
requires rank 1 or less, is unreachable for the rest of the flight.

So `_loftToSkim` does not decide whether a round lofts. It decides what happens to a round that is
already lofted, meaning it was launched outside the boundary: `False` keeps it lofted until the
final-flight distance, `True` drops it to sea-skimming at the boundary.

The model's own `lofting` flag is `loftAlt > launchAlt`, a property of the ammunition that is true
at every range, so it says a round *can* loft rather than that it *will*. The launch-range test is
applied separately in the phase selector. It bites only where `finalDist` is smaller than the
sea-skimming boundary, which is the `LoftToSkim=False` case: `wp_ss-n-12` between 50 and 100 nmi and
`wp_ss-n-19` between 80 and 100 nmi. Measured before the test existed, the model climbed to 44300
and 50000 ft while both rounds sea-skimmed, for +11.6 s and -4.8 s. The signs differ because a long
spurious loft repays its climb at loft speed and a short one does not.

### Phase 1 has two altitudes

The game runs `MaintainSeaSkimming` at `SeaSkimmingAlt` and `MaintainFinalFlightAlt` at
`FinalFlightPhaseAlt`, switching at `FinalFlightPhaseDistToTarget`. Phase 1 covers both, so it holds
`cruiseAlt` outside that distance and `finalFlightAlt` within it.

The game parses `FinalFlightPhaseAlt` with `SeaSkimmingAlt` as its default
(`AmmunitionParameters`, ini key `TerminalVelocity`), so for most ammunition the two are equal and the switch is
inert. Where they differ it matters most on a shot that starts inside the final-flight distance and
so never sea-skims at all: an `ss-n-3b` fired at 91 km flew its whole flight at 1312 ft while a
single-altitude model held 13200 ft, worth -4.4 s on a 199 s flight.

### Loft speed can outlast the loft altitude

The game gives a round `_maxLoftVelocityInKnots` only while its stage is `MaintainLoftAlt`
(`Missile.cs:3143`), and **both** distance-based exits from that stage sit behind a local flag that
`RequiresTargetToProceed` holds false until the seeker activates (`Missile.cs:413`, activation at
`BearingLaunchPrediction.cs:97-108`). Such a round descends to sea-skimming altitude on cue and
keeps flying at loft speed until `max(_seekerActiveRange, _seekerPassiveRange)`.

`loftSpeedHoldDist` carries that distance, and phase 1 holds the loft speed outside it while the
altitude schedule proceeds normally. Three shipped ammunition declare the flag and only the two
SS-N-3 variants also loft; for everything else the hold is zero and the code path is unchanged.
Before it existed the SS-N-3B was 72 s fast and the SS-N-3 51 s fast, constant in seconds at every
range beyond the sea-skimming boundary.

### Launch and ToBearing command the maximum speed

The loft speed applies only when the stage is `MaintainLoftAlt`. During Launch and ToBearing
the commanded speed is `_maxVelocityInKnots` (`Missile.cs:3142-3145`), so the model overrides the
stage target for that window. Inert for ammunition whose loft speed equals its maximum speed.

### Which descent angle the profile commands

`_finalFlightPhaseMaxAngle` constrains one waypoint, the final-flight leg, and
`Missile.CreateWaypointConfigs` builds that leg only when `_finalFlightPhaseDistToTarget` is
positive. Where it is not, the descent to cruise altitude rides the sea-skimming leg instead, so the
profile commands `_seaSkimmingMaxDescentAngle`:

```
descentDeg = finalFlightDist > 0 and _finalFlightPhaseMaxAngle > 0
             ? _finalFlightPhaseMaxAngle
             : _seaSkimmingMaxDescentAngle           # 30 deg if that is unset too
```

63 of 853 shipped and workshop ammunition declare the two angles differently with no final-flight
leg, so for those the choice decides the descent. `usn_agm-84d` is one: it declares 30 and 45, and
the game's recorded track holds a flat -45.00 degrees from 20,000 ft to the sea. Taking the 30
unconditionally put that family 2.7 s early on every long shot.

`descentOnsetDeg`, which sets the terminal geometry below, stays the maximum of both angles either
way.

### Terminal onset

The dive begins at whichever comes first: the declared terminal distance, or the geometric distance
at which the descent angle still reaches the terminal altitude:

```
descentGeomDist = (alt − termAlt) / tan(max(descentOnsetDeg, 5°))
diveStart       = max(termDist, descentGeomDist)
```

The geometric term matters for a high lofter, which would otherwise arrive far too high to dive.

A kinematic round whose terminal leg is Linear has no geometric onset. Every altitude leg of a kinematic
round allows exceeding its angle limits, so the game holds the leg before the terminal one until the
terminal distance and only then flies the terminal leg, a straight line from where the round entered it
to the terminal height. The model starts that phase at `termDist` and steers each step at the point
1.5 s ahead on the line (`Waypoint.CalculatePointAtTime` on a Linear leg), instead of a pitch fixed when
the phase began. It starts down before that: once the 1.5 s look-ahead reaches past the boundary, the
game continues it into the terminal leg from the boundary, at the height the round's vertical speed
projects there (`Waypoint.CalculateFuturePointRecursive`), and the model steers at the same point, so
the dive begins about 1.5 s before `termDist` as recorded rounds show.

**`_searchForTargetsTime` pulls the onset in.** The game does not enter `TerminalApproach` on
distance alone: with `SearchForTargetsTime > 0` the seeker must also hold an echo for that long
first, and the clock resets on every tick without one (`Missile.cs:584-593`). The missile therefore
keeps closing at cruise speed past its nominal terminal distance, so the model subtracts the distance
covered during the search:

```
termDist = max( _terminalApproachDist − _maxVelocityInKnots · KU · _searchForTargetsTime , 0 )
```

Inert by construction for ammunition that leaves the field unset.

**The distance is to the missile's intercept point, not to the ship** (beta branch). The game tests these
boundaries, the seeker switch-on and every waypoint leg against the flat distance to
`_targetInterceptPosition` (`Missile.cs:374-376`; GAME-BEHAVIOUR.md section 6). The step loop computes the
same point every step (`FlightTime.Lead.cs`):

```
known      = launcher-guided or seeker locked
               ? predTgt
               : targetPos + targetVel · min(t, initialPhaseDur) + vRep · max(t − initialPhaseDur, 0)
tLead      = CalculateLeadInterceptPoint(pos, speed, travelledFlat, known, vRep)
intercept  = known + vRep · tLead                    # kept from the last step when there is no root
switchFlat = horizontal |intercept − pos|
```

`vRep` is the target's reported velocity, before the map-speed rounding (GAME-BEHAVIOUR.md section 4), and
`travelledFlat` is the flat distance flown so far. A dead-reckoning round's seeker locks once phase 2 has
started and the slant range is inside its seeker range. `diveStart`, `finalDist`, the final-flight altitude
switch, the loft-speed hold and every leg boundary compare against `switchFlat`. The arrival test,
steering and drag keep `flatDist` to the predicted ship. Once `switchFlat` has crossed `termDist` the round
stays in phase 2: TerminalApproach is a state in the game, and a seeker lock can move the known target back
outside the threshold. An air target, a `ForceDirectPoint` weapon, or an unbound drag method keeps
`switchFlat = flatDist`.

## 3.4 The pitch command

Within a phase, pitch is commanded from the altitude error `altErr = stageAlt − alt`, except for a
non-kinematic round against a surface target, which follows its waypoint route instead.

### A cruise missile steers at its waypoint

A non-kinematic round does not fly a climb angle to an altitude. `Missile.CreateWaypointConfigs`
(`Missile.cs:2145`) gives it a route of waypoints, each ending at a flat distance to target, and the
missile steers at a look-ahead point on the active one. `FlightTime.BuildNonKinLegs` rebuilds that
route at setup, in flight order:

| leg | ends at | height | climb / descent limit |
|---|---|---|---|
| loft | `_seaSkimmingStartDistToTarget` | `_maxLoftAlt` | `_maxLoftAngle` / `_finalFlightPhaseMaxAngle` |
| sea-skimming | `_finalFlightPhaseDistToTarget` | `_seaSkimmingAlt` | `_seaSkimmingMaxDescentAngle` |
| final flight | `_terminalApproachDist` | `_finalFlightPhaseAlt` | `_finalFlightPhaseMaxAngle` |
| terminal approach | `_terminalDiveDistance` | `_terminalAlt` | none |
| terminal dive | the target | target height, Linear | none |

Legs the round is already inside at launch are dropped (`FastForwardWaypoints`). A loft leg with
`LoftToSkim = False` hands its own settings to the sea-skimming leg when it completes
(`Waypoint.cs:687`), so such a round lofts on to the final-flight boundary. A group leader flies
`_groupLeaderAlt` on the final-flight and terminal legs, and every round of an ammunition with
`GroupSize > 1` leads its own group when fired alone. A leader cashes its group in on entering
TerminalApproach and rebuilds its route, after which it flies only the terminal dive.

The look-ahead point (`Waypoint.CalculatePointAtTime`, `Waypoint.cs:423`) is lerped from the missile
toward the **target** by a fraction measured against the distance to the **waypoint**:

```
remaining = flatDist − leg.EndDist
fraction  = min(flatV · T / remaining, 1)          T = 1.5 s (steer) or 8 s (guard)
lead      = flatDist · fraction                    # grows without limit near a boundary
height    = leg.Height, clamped to alt ± lead · tan(limit) while progress < 0.95
targetPitch = atan2(height − alt, lead)
```

Because `lead` is stretched by `flatDist / remaining`, the commanded pitch flattens before every
boundary, and a lofting round levels off below its loft height before the first boundary and
climbs again after it. When the look-ahead runs past a boundary it continues into the next leg, from
the boundary, using that leg's own settings. A terminal pop-up leg's own settings are its KappaLoft
curve up to `TerminalPopUpHeight`, limited to `TerminalPopUpAngle`, so a look-ahead that reaches it
climbs on that curve rather than jumping to the leg's end height. While the round flies the pop-up
leg, its steering point is held to `TerminalPopUpAngle` below 0.95 progress, because that waypoint
does not allow exceeding its angle limits. The pre-apex guard (`Missile.cs:2559-2581`) compares
the pitch change needed between the 1.5 s and 8 s points with the raw `_maxTurnRate` times pitch's
share of the steering error, and commands level flight when the round cannot make it.

Air-launched rounds fly this route as well. They were held out of it until 2026-09-13, when the
closest-approach test in [§5.3](05-loop.md) learned to require slant range to recede: without that,
a round released inside `_terminalApproachDist` ended its own solve part way down the terminal dive,
and the route looked far worse than the law below rather than far better.

Captures without the raw route fields, and terminal-loft ammunition, keep the law below.

### Bang-bang while transiting, proportional while holding

The two are not interchangeable, and each is wrong where the other belongs:

- **Transiting** to an altitude, the real missile holds *full* climb until it arrives and only then
  noses over at its turn rate. Bang-bang reproduces that, and the overshoot it produces is a
  consequence of the turn-rate limit, not an error.
- **Holding** an altitude, bang-bang limit-cycles: pitch saturates one way, overshoots, saturates
  back. On a long cruise this costs real closure: a round can sit at +33° with its altitude
  effectively frozen, bleeding `cos 33° = 0.825` of its speed into a climb that goes nowhere.

So the model latches on first arrival and switches:

```
if sign(prevAltErr) != sign(altErr):   altLatched = true      # crossed the stage altitude
prevAltErr = altErr

if altLatched:                                                 # holding → proportional
    look        = max(vel · KU · dt · 20, 50)
    targetPitch = clamp( atan2(altErr, look), −diveDeg, climbDeg )
elif altErr >  0.5:  targetPitch =  climbDeg                   # transiting → bang-bang
elif altErr < −0.5:  targetPitch = −diveDeg
else:                targetPitch =  0
```

Arrival is detected as a **sign change** in the altitude error, not as entry into a position band. A
crossing cannot be stepped over; a band can. At cruise the loop moves 3–5.5 u of altitude per 0.1 s
step against a 1 u-wide band. The latch resets whenever the phase changes.

### The climb angle

Commanded climb is the ammunition's own `_maxLoftAngle`, **except** for a lofter whose loft altitude
is above the air-density line, which flies vertical:

```
isHighBallisticLofter = kinematic && lofting && loftAlt > 613.5 u
boostClimbDeg         = isHighBallisticLofter ? 90 : (_maxLoftAngle || 30)
```

`AllowExceedingAngleLimits = (Kinematics != None)` (`Missile.cs:2197`) is what *permits* a kinematic
round to exceed its commanded angle; altitude above the atmosphere is what makes it do so. A
kinematic lofter that stays inside the atmosphere flies its own `_maxLoftAngle`.

The same gate steepens the dive: a high ballistic lofter descends at `descentOnsetDeg` rather than
`descentDeg`.

### The launch hold

While `t < _initialFlightPhaseDuration` and a launch attitude is known, the command is the launch
elevation; the round flies off the rail before it flies its stage.

## 3.5 Terminal-loft altitude

Ammunition with `_terminalLoft` does not fly the three-phase altitude profile. It follows a concave
arc, and the game generates that arc itself: `BuildAltitudeNodes` returns a list of
(flat-distance, altitude) nodes for the shot's own launch altitude, target altitude and range.

The model follows those nodes by aiming at a point one lookahead ahead of its current position along
the arc:

```
xNow     = flatDistTotal − flatDist
look     = max(vel · KU · dt · 20, 50)
altAhead = interpolate(altNodes, min(xNow + look, flatDistTotal))
pitch    = −clamp( atan2(alt − altAhead, look), −climbDeg, descentDeg )
```

If the nodes are unavailable, it falls back to a glide: once the loft altitude is reached, descend at
the angle that reaches the target, capped at `descentDeg`.

Every kinematic lofter follows this curve, not only terminal-loft ammunition: the game flies a
`Kinematics != None` round by `Waypoint.KappaLoft` whether `TerminalLoft` is set or not, and
`BuildAltitudeNodes` is the game's sampled form of that curve. Once the nodes exist, the step's command
comes from a port of the live curve (`Waypoint.GetOrComputeLoftParams` and `BallisticHeightAt`) rather
than from the samples: the curve starts where the round is when its initial phase ends, ends on the
line to the target at the leg's end distance, and is read at the steering point's progress by distance
to the target, with no `_maxLoftAngle` clamp.
