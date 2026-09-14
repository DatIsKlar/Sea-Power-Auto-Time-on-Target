# 5. The integration loop

[← Speed](04-speed.md) · [index](00-index.md) · next: [Accuracy](06-accuracy.md)

`IntegratedEndTimeCore(unit, ap, target)`, in full, with the source's own variable names. Forward
Euler, fixed `dt = 0.1 s`, returning the intercept time in seconds or −1 to decline.

## 5.1 Setup, once per shot

The division in this chapter is also the threading boundary. Setup reads live game state, so it runs
on the main thread. The per-step loop in 5.2 reads none, so it is written as a pure function of a
snapshot and can run on a worker. See "Where the integrator runs" in
[`../ARCHITECTURE.md`](../ARCHITECTURE.md). Anything added below that reads the shooter, the target
or a field the game rewrites during flight belongs in this section, not the next one.

```
EnsureSimLookup()                                   # resolve reflection handles (§2.3)
if !_simIsBeta or thrust handle missing: return −1   # → next tier

nonKin      = ap.Kinematics == None                  # speed branch selector
targetPos   = target.transform.position
launcher    = ResolveLauncher(unit, ap, targetPos)   # the firing-arc pick the engage task takes
launchPos   = mean of launcher's cells               # Unity units; unit.transform.position when it
                                                     # has none (GAME-BEHAVIOUR.md section 5)
targetVel   = TargetMapVelocity(target)              # rate a surface ship realises (§6.3), + evasive boost
dragFactor  = ap.GetDragFactor(isAir)
targetAlt0  = max(targetPos.y, 0)                    # surface-target floor
maxFlight   = ap._maxFlightTime > 0 ? ap._maxFlightTime : 600

# --- class gates (§4.5) ---
loftAlt     = LoftCap(ap, launchAlt, targetAlt0)     # the game's own flown loft altitude
lofting     = loftAlt > max(launchAlt, 0) + 0.5
isTerminalLoft        = ap._terminalLoft
isHighBallisticLofter = !nonKin && lofting && loftAlt > 613.5

# --- rates (§3.2) ---
boostClimbDeg = isHighBallisticLofter ? 90 : (ap._maxLoftAngle || 30)
turnRate      = ap._maxTurnRateDegrees || 5
if nonKin && ap._supportsBanking:  turnRate += 60    # the roll budget lands on pitch,
                                                     # except under a ToBearing override (§3)

# --- launch geometry (§3.1) ---
pick        = launcher whose horizontal rail bearing is closest to the target
rail        = pick._containers[0]._gunObject ?? pick._vwp._containerBaseObject
fixedRail   = !_isMountRotatable && !_areContainersRotatable
launchPitch = fixedRail ? clamp(asin(rail.forward.y), 0, 90)
                        : recomputed aim (target elevation − mount pitch, clamped to the arc)
initialPhaseDur = max(ap._initialFlightPhaseDuration, 0)
launchHeading   = (fixedRail && rail not vertical) ? horizontal(rail.forward) : none

# --- stage boundaries and speeds (§3.3) ---
maxVelKn/loftVelKn/termVelKn, decelPerStep = _deceleration · 9.81 · dt
toSkim    = ap._loftToSkim && _seaSkimmingStartDistToTargetUnity > 0
finalDist, finalAlt                                  # sea-skim pair or final-phase pair
termDist  = ap._terminalApproachDist
          − maxVelKn · KU · ap._searchForTargetsTime  # floored at 0
termAlt, descentDeg, descentOnsetDeg
altNodes  = BuildAltitudeNodes(...) if isTerminalLoft else null

pos = launchPos;  velKnots = unit._velocityInKnots;  t = 0
prevPitch = launchPitch >= 0 ? launchPitch : 0
altLatched = false;  altLatchPhase = −1;  prevAltErr = NaN
```

## 5.2 Per step

```
while t < maxFlight:

    predTgt  = targetPos + targetVel · t                  # moving-target lead
    flatDist = horizontal distance |predTgt − pos|

    # --- intercept: the hull, closest approach, or inside the radius ---
    slantDist = |predTgt − pos|                           # 3-D, not flattened
    rel       = pos − predTgt
    arrived   = hull ? hull.segmentHits(prevRel, rel) : flatDist < 3
    if (flatDist > prevFlat and slantDist > prevSlant and t > dt and hasClosed) or arrived:
        return velKnots < ap.MinVelocity · 1.1 ? −1 : t   # reject a too-slow arrival
    prevFlat = flatDist;  prevSlant = slantDist;  prevRel = rel

    # --- heading (§3.1) ---
    horizDir = unit vector toward predTgt, flattened
    if launchHeading is set:
        if t >= initialPhaseDur:
            launchHeading = RotateTowards(launchHeading, horizDir, turnRate · dt)
        horizDir = launchHeading

    # --- switch distance (§3.3, beta branch) ---
    switchFlat = flatDist                                 # air target, ForceDirectPoint
    if the round leads its intercept point:
        intercept  = known + vRep · leadTime(pos, velKnots, travelledFlat, known, vRep)
        switchFlat = horizontal distance |intercept − pos|
        terminalLatched |= switchFlat <= termDist

    # --- stage selection (§3.3) ---
    descentGeomDist = (pos.y − termAlt) / tan(max(descentOnsetDeg, 5))
    diveStart       = max(termDist, descentGeomDist)
    if   switchFlat <= diveStart or terminalLatched:
                                  phase = 2;  stageAlt = termAlt;   stageTgt = termVelKn
    elif switchFlat <= finalDist: phase = 1;  stageAlt = finalAlt;  stageTgt = maxVelKn
    elif lofting:                 phase = 0;  stageAlt = loftAlt;   stageTgt = loftVelKn
    else:                         phase = 1;  stageAlt = finalAlt;  stageTgt = maxVelKn

    if t < initialPhaseDur or inToBearing:  stageTgt = maxVelKn     # Launch/ToBearing command

    # --- pitch command (§3.4) ---
    altErr  = stageAlt − pos.y
    diveDeg = isHighBallisticLofter ? descentOnsetDeg : descentDeg

    if phase != altLatchPhase:  altLatchPhase = phase; altLatched = false; prevAltErr = NaN
    if sign(prevAltErr) != sign(altErr):  altLatched = true          # crossed the stage altitude
    prevAltErr = altErr

    if altLatched:                                                   # holding → proportional
        look        = max(velKnots · KU · dt · 20, 50)
        targetPitch = clamp(atan2(altErr, look), −diveDeg, boostClimbDeg)
    elif altErr >  0.5:  targetPitch =  boostClimbDeg                # transiting → bang-bang
    elif altErr < −0.5:  targetPitch = −diveDeg
    else:                targetPitch =  0

    if isTerminalLoft and lofting:  targetPitch = follow altNodes over the lookahead   # §3.5
    if launchPitch >= 0 and t < initialPhaseDur:  targetPitch = launchPitch            # §3.1

    pitchDeg  = MoveTowards(prevPitch, targetPitch, turnRate · dt)   # finite slew → loft overshoot
    pitchRate = (pitchDeg − prevPitch) / dt

    # --- speed (§4) ---
    thrust = CalculateThrustOverTime(ap, isAir, t, dt);  motorBurning = thrust > 0
    if nonKin:
        seek stageTgt at _acceleration / decelPerStep                # no drag
    else:
        velKnots += thrust
        inVacuumDive = phase == 2 and pos.y > 613.5 and pitchDeg < −40
        targetAltArg = inVacuumDive ? pos.y : predTgt.y              # the vacuum brake
        velKnots −= CalculateDrag(pos.y, velKnots·KU, dt, −pitchDeg, dragFactor,
                                  motorBurning, targetAltArg,
                                  ap.LiftFactor, ap.MinVelocity, −pitchRate)
        # no stage-speed clamp: kinematic is thrust − drag, uncapped

    if velKnots < 1:  return −1                                      # stalled → next tier

    # --- position ---
    dir  = horizDir · cos(pitchDeg) + up · sin(pitchDeg)             # +pitch = climbing
    pos += velKnots · KU · dt · dir

    accumulate phase timings and peak altitude;  emit sim-track on the sampling schedule
    prevPitch = pitchDeg;  t += dt;  clock += dt           # clock is double: see below

return −1                                                            # out of range
```

## 5.3 The closest-approach intercept test

The loop does not test for a distance threshold alone. It returns at the step where distance
**stops decreasing**, or when it reaches the target's hull (a surface ship) or comes inside 3 u
horizontally (any other target). A pure threshold test would miss
any flight whose closest approach is wider than the threshold, and would then run to `maxFlight` and
decline a shot that in fact intercepts.

**Both horizontal and slant distance have to be receding.** Horizontal distance alone is the wrong
measure for a round that is diving. A terminal waypoint leaves `AllowExceedingAngleLimits` at its
default `true` and uses `Immediate` pitch, so a round that launches inside `_terminalApproachDist`
from altitude is commanded straight down: `usn_agm-84d` fired 10.8 km out from 20,000 ft holds -90
degrees for eleven seconds with its x and z frozen. Horizontal distance then stops shrinking, and a
target under way pushes it back up, so the test fired at 7.1 s of a 56.8 s flight.

Adding the slant term cannot strand the loop. The test is deferred only while the round is trading
altitude for no ground progress, and there is a finite amount of altitude to trade; once the round
levels out the vertical separation goes to zero and slant recedes exactly when horizontal does.

**The flight time returned is a double sum of the steps.** The loop's `t` is float32, and adding the
same step to it rounds every sum the same way within a power of two: 0.1 s steps come out 0.015 s long
by 400 s, 1/60 s steps 0.05 s short by 400 s and 0.1 s short by 600 s. Motion already uses the step
itself, so only the reported time carried that error, and the loop returns `clock` instead. The
comparisons inside the loop keep `t`, whose error is far below a step.

**A surface ship's flight ends at its hull.** Against a `Vessel` the round arrives on the step whose
segment passes through a box centred on the ship's position and turned to its heading, which is
where the game's collision cast ends the flight. The test is swept (a slab test on the step's
segment), because a hypersonic round covers more than a hull's height in one step. Any other target
keeps the 3 u radius, which against a ship ends the flight up to 200 m and 0.3 s before the hull.

The box is sized by what the shooter's side knows about the contact, so the estimate uses no more
than a player could:

| contact | hull |
|---|---|
| identified | the target's own collider, measured in its heading frame |
| held by a sensor whose label shows a size class (every sensor but passive sonar and ESM) | the representative hull of that class ([§2](02-parameters.md)) |
| passive sonar or ESM only, or not held by any sensor now | the `Medium` class's hull |

The hull only ends the estimate's flight; nothing about it is shown.
