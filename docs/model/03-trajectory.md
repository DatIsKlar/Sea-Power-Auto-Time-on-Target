# 3. Trajectory

[← Parameters](02-parameters.md) · [index](00-index.md) · next: [Speed](04-speed.md)

The game does not fly missiles with proportional navigation. It runs a **guidance state machine** —
Launch → ToBearing → MaintainLoftAlt / SeaSkimming / FinalFlightAlt → TerminalApproach — where each
stage commands an altitude and a speed, and the airframe slews toward that command at a finite rate.
The integrator reproduces that machine. Trajectory shape is the only thing modelled by hand;
everything else is the game's own physics ([§4](04-speed.md)).

## 3.1 Launch geometry

A missile does not begin flight pointing at its target. It leaves the rail pointing wherever the rail
points, holds that attitude for `_initialFlightPhaseDuration`, and only then turns. Both axes matter,
and both are read from the launcher's transform rather than from any `.ini` field.

### Choosing the launcher

A ship may mount several launchers for the same ammunition, including pairs installed 180° apart —
one bearing to port, one to starboard. The round comes off whichever launcher bears on the target, so
the model iterates every launcher carrying the ammunition and picks the one whose horizontal rail
direction is closest to the target bearing, skipping rails that are vertical (no bearing to compare).
Taking an arbitrary launcher would apply a spurious ~180° turn.

### Elevation

The rail's true attitude comes from `_containers[i]._gunObject.transform` — the object the game
actually elevates (`WeaponSystem.alignToTarget:1379-1381`), falling back to `_containerBaseObject`
when containers are joined. Elevation is `asin(forward.y)`, floored at 0°:

```
launchPitch = max( asin(rail.forward.y) · rad2deg , 0 )
```

`launchPitch >= 0` is the sentinel meaning "a launch attitude is known"; −1 means unknown, and the
model then simply steers at the target from t = 0.

Fixed rails and trainable mounts are handled separately, because a trainable mount's *current*
transform is wherever it happens to be parked and must never be read as a launch attitude:

| launcher | source of the launch elevation |
|---|---|
| **fixed** (`!_isMountRotatable && !_areContainersRotatable`) | the rail as built: `clamp(asin(forward.y), 0, 90)` |
| **trainable** | the game's own aim is recomputed — elevation to the target (or `_fixVerticalLaunchAngle` where the launcher uses a fixed angle), plus `_additionalFixVerticalLaunchAngle`, minus the mount's own pitch, clamped to the elevation arc (`alignToTarget:1360-1377`) |

This covers vertical launch for free: a VLS cell cannot train, so `alignToTarget` returns immediately
and the cell stays as built — vertical.

### Heading

A launcher that cannot train fires **along its own bearing**. An off-bearing shot therefore spends
its initial flight phase flying the wrong way and must then turn — closure that a model steering
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

It is inert wherever the rail is already on-bearing, vertical, or trainable — which is most
launchers.

> **Known approximation.** The game rate-limits *combined* pitch and yaw in a single
> `Quaternion.RotateTowards` (`WeaponBase.cs:1770`); the model limits heading and pitch
> independently. For an abeam launch the turn is overwhelmingly yaw, so the error is small, but this
> is not a faithful port.

## 3.2 The turn-rate budget

Pitch slews toward its command at `_maxTurnRateDegrees` (5°/s when unset):

```
pitchDeg = MoveTowards(prevPitch, targetPitch, turnRate · dt)
```

This finite rate is not a detail — it is what produces the **loft overshoot**. A missile commanded to
level off at its loft altitude is still near +90° pitch when it arrives, and needs several seconds to
swing down, climbing the whole way. A model that levels instantly peaks hundreds of Unity units low.

### Banking adds a second budget

A **non-kinematic** missile with `_supportsBanking` gets a *second* rotation call every physics tick:
`setCourseTowardsPositionLegacy` runs its normal `RotateTowards` at `_maxTurnRateDegrees`, and then
`performToTargetRoll` runs another at a hardcoded 60°/s (`WeaponBase.cs:1773-1776`, `:1789-1792`).
That second call assigns a locally-read Euler angle to *world* rotation, which is gimbal-degenerate
near vertical — so after a vertical launch its budget lands largely on **pitch**, not roll.

```
if nonKinematic && ap._supportsBanking:
    turnRate += 60
```

Modelling only `_maxTurnRateDegrees` makes such a round nose over roughly three times too slowly.

## 3.3 The stage model

Three phases, selected each step by remaining horizontal distance to the predicted intercept:

| phase | selected when | altitude | speed target |
|---|---|---|---|
| 0 — loft | lofting, and beyond `finalDist` | `loftAlt` from the game's `LoftCap` | `_maxLoftVelocityInKnots` |
| 1 — final / sea-skim | within `finalDist` | `finalAlt` | `_maxVelocityInKnots` |
| 2 — terminal | within `diveStart` | `termAlt` | `_terminalVelocityInKnots` |

Boundaries come straight from the `.ini`, with `_loftToSkim` selecting which pair applies:

```
toSkim    = _loftToSkim && _seaSkimmingStartDistToTargetUnity > 0
finalDist = toSkim ? _seaSkimmingStartDistToTargetUnity : _finalFlightPhaseDistToTargetUnity
finalAlt  = toSkim ? _seaSkimmingAltUnity              : _finalFlightPhaseAltUnity
```

### Launch and ToBearing command the maximum speed

The loft speed applies only once the stage is actually `MaintainLoftAlt`. During Launch and ToBearing
the commanded speed is `_maxVelocityInKnots` (`Missile.cs:3142-3145`), so the model overrides the
stage target for that window. Inert for ammunition whose loft speed equals its maximum speed.

### Terminal onset

The dive begins at whichever comes first — the declared terminal distance, or the geometric distance
at which the descent angle still reaches the terminal altitude:

```
descentGeomDist = (alt − termAlt) / tan(max(descentOnsetDeg, 5°))
diveStart       = max(termDist, descentGeomDist)
```

The geometric term matters for a high lofter, which would otherwise arrive far too high to dive.

**`_searchForTargetsTime` pulls the onset in.** The game does not enter `TerminalApproach` on
distance alone: with `SearchForTargetsTime > 0` the seeker must also hold an echo for that long
first, and the clock resets on every tick without one (`Missile.cs:584-593`). The missile therefore
keeps closing at cruise speed past its nominal terminal distance, so the model subtracts the distance
covered during the search:

```
termDist = max( _terminalApproachDist − _maxVelocityInKnots · KU · _searchForTargetsTime , 0 )
```

Inert by construction for ammunition that leaves the field unset.

## 3.4 The pitch command

Within a phase, pitch is commanded from the altitude error `altErr = stageAlt − alt`.

### Bang-bang while transiting, proportional while holding

The two are not interchangeable, and each is wrong where the other belongs:

- **Transiting** to an altitude, the real missile holds *full* climb until it arrives and only then
  noses over at its turn rate. Bang-bang reproduces that, and the overshoot it produces is a
  consequence of the turn-rate limit, not an error.
- **Holding** an altitude, bang-bang limit-cycles: pitch saturates one way, overshoots, saturates
  back. On a long cruise this costs real closure — a round can sit at +33° with its altitude
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
crossing cannot be stepped over; a band can — at cruise the loop moves 3–5.5 u of altitude per 0.1 s
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

While `t < _initialFlightPhaseDuration` and a launch attitude is known, the command is simply the
launch elevation — the round flies off the rail before it flies its stage.

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

Nodes are used **only** for terminal-loft ammunition. The generator describes that specific concave
profile; applied to an ordinary lofter it would command the wrong shape.
