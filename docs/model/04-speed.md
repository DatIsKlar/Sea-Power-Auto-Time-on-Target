# 4. Speed

[← Trajectory](03-trajectory.md) · [index](00-index.md) · next: [The loop](05-loop.md)

The game runs **two different speed models**, selected by `ApplyKinematics`, and they are not
variations of one another. The integrator branches the same way.

## 4.1 Non-kinematic ammunition (`Kinematics == None`)

The round does not integrate forces. It seeks its current stage's commanded speed at its own
acceleration and deceleration rates, and drag is never applied:

```
if vel > stageTgt:  vel −= min(decelPerStep, vel − stageTgt)
else if vel < stageTgt:  vel += min(thrust, stageTgt − vel)

decelPerStep = _deceleration · 9.81 · dt        # g → kn/s
```

Stage targets are `_maxLoftVelocityInKnots`, `_maxVelocityInKnots` and `_terminalVelocityInKnots` per
[§3.3](03-trajectory.md#33-the-stage-model). This branch covers most cruise missiles and
sea-skimmers.

## 4.2 Kinematic ammunition (`ApplyKinematics = True`)

Speed is thrust minus drag, **uncapped**:

```
vel += CalculateThrustOverTime(ap, isAir, t, dt)
vel −= CalculateDrag(alt, vel·KU, dt, −pitch, dragFactor, motorBurning,
                     targetAltArg, LiftFactor, MinVelocity, −pitchRate)
```

There is deliberately **no stage-speed clamp** here. The live mover applies the stage target only in
its non-kinematic branch; the kinematic branch is `_velocityInKnots += num` with nothing bounding it
(`Missile.cs:3151`), and a real round is routinely observed above its nominal maximum.

Note the two sign flips: the model's convention is positive-pitch-is-climbing, and `CalculateDrag`
uses the opposite, so both pitch and pitch rate are negated at the call.

## 4.3 `CalculateDrag` expanded

`CalculateDrag(alt, vel, dt, pitch, dragFactor, motorBurning, targetAlt, liftFactor, stallKn,
pitchRate)` returns the knots to **subtract** this step (`MissileSimulator.cs:2011-2033`):

```
ρ(h)  = (1 − 0.00163·h)^4.256, clamped ≥ 0        → reaches 0 at h ≈ 613.5 u
aero  num  = ρ(alt) · vel² · 0.2 · dt · dragFactor          # classic ½ρv², vel in u/s
stall (only when !motorBurning): induced term from (stallKn, pitch, pitchRate, vel)  # small
grav  num8 = 9.81 · sin(−pitch)                             # descending gains speed
lift  num9 = 0                                              if motorBurning
             sqrt(|cos pitch|) · dragFactor · liftFactor · 9.81
             / max(ρ(targetAlt)/1.225, 0.001)               otherwise

return num + (num9 + num8) · 1.94384 · dt
```

## 4.4 The vacuum brake

The key property of the expansion above: **`num9` is speed-independent, and it blows up as
`ρ(targetAlt) → 0`.** The divisor floors at 0.001, so it can grow by a factor of roughly 800.

The live mover feeds `targetAlt` conditionally (`Missile.cs:3170-3175`): the *target's* altitude
while the seeker holds lock — dense air, divisor ≈ 0.816, the term is negligible — and the missile's
*own* altitude once lock drops. A round coasting through near-vacuum after lock loss therefore
decelerates hard, and any model that ignores this arrives far too fast.

The integrator mirrors it with a physical gate rather than a seeker model:

```
inVacuumDive = phase == 2 && alt > 613.5 u && pitch < −40°
targetAltArg = inVacuumDive ? alt : predictedTarget.y
```

This is the model's one deliberate heuristic. It describes a class — steeply diving, post-burnout,
above the atmosphere — not a particular missile.

**Worked example.** A Mach-10 lofter at own altitude 708 u (vacuum), pitch 59°, `dragFactor` 2.4,
`liftFactor` 0.005, lock dropped so `targetAlt` = 708 u ⇒ `ρ(708) = 0` ⇒ divisor 0.001:

```
num9 = sqrt(cos 59°) · 2.4 · 0.005 · 9.81 / 0.001
     = 0.718 · 2.4 · 0.005 · 9.81 / 0.001  ≈  84.5 m/s²
     · 1.94384                             ≈  164 kn/s
```

which matches the deceleration observed on a real flight. In dense air (`targetAlt` = 0, divisor
0.816) the same term is ≈ 0.2 kn/s — negligible.

## 4.5 Class taxonomy

Which mechanisms apply to a given round:

```
                  ┌─ Kinematics == None ──► NON-KINEMATIC
                  │                          speed  = stage-seek, no drag
   ammunition ────┤                          pitch  = stage model (+ nodes if TerminalLoft)
                  │                          turn   = MaxTurnRate (+60°/s if SupportsBanking)
                  │
                  └─ kinematic ─┬─ _terminalLoft ──► TERMINAL-LOFT
                                │                     altitude = BuildAltitudeNodes
                                │                     speed    = thrust + drag
                                │                     no vacuum brake (loft below the density line)
                                │
                                ├─ lofting, loftAlt > 613.5 u ──► HIGH BALLISTIC LOFTER
                                │                     90° boost climb, steep dive at onsetDeg,
                                │                     thrust + drag, VACUUM BRAKE reachable
                                │
                                ├─ lofting, below the line ──► KINEMATIC LOFTER
                                │                     climbs its own _maxLoftAngle
                                │
                                └─ not lofting ──► GENERIC KINEMATIC
                                                      stage model, thrust + drag
```

Two gates are worth stating plainly, because both are narrower than they look:

- **The 90° boost climb** belongs only to lofters above 613.5 u, not to kinematic lofters generally.
  A kinematic lofter inside the atmosphere flies its own `_maxLoftAngle`.
- **The vacuum brake** is reachable only by that same class, since its altitude gate is the same
  line.
