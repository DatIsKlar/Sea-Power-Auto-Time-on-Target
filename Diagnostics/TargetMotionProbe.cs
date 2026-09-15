using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// What moves
    /// a target ship across the map at 0.87 to 1.05 times the speed it reports.
    ///
    /// Read in decompiled-beta-new, the only horizontal mover of a surface ship is
    /// Vessel.applyMainThrust, which Translates the transform by _velocityVecInUnity times
    /// GameTime.fixedDeltaTime (Vessel.cs:438-443). The hull's rigidbody is locked in world X and Z
    /// (Compartments.cs:590, constraints 10), and GameMain.PerformFixedUpdate calls every object's
    /// OnFixedUpdate once per tick with one shared step. No line read explains a factor that differs
    /// per ship, so this splits the displacement a ship actually makes into the parts that could
    /// carry one:
    ///
    ///   translate   sum of _velocityVecInUnity * fixedDeltaTime, read right after applyMainThrust
    ///   inside      transform motion across the ship's own OnFixedUpdate
    ///   outside     transform motion from one OnFixedUpdate to the next (physics step, frame code)
    ///
    /// inside + outside equals the fixed-tick displacement by construction, and the line prints the
    /// residual so a broken probe shows itself. It also keeps the tick-mean speed Translate used
    /// beside the mean of a once-per-frame read, which is how the corpus recorded "reported" speed,
    /// and the displacement measured from frame reads, which is how the 0.873 factor was found.
    ///
    /// D1 found the extra motion inside OnFixedUpdate. D2 brackets the parts of Vessel.OnFixedUpdate
    /// (Vessel.cs:313-337) with position and yaw deltas: thrust, rudder, compartments, the base
    /// ObjectBase update and the state machine. Whatever inside motion they do not account for is
    /// printed as rest: UpdateWantedVelocityInKnots, the tilt forces and the two threat checks.
    ///
    /// Installed on the first watched target, and targets are watched only under TraceFlightModel,
    /// so a session that never turns that on never carries the patch. Once installed it stays until
    /// the game restarts.
    /// </summary>
    internal static class TargetMotionProbe
    {
        private const float WindowSec = 20f;
        private const float DropAfterSec = 30f;
        // Knots to Unity units per second, as ObjectBase.setVelocityInKnots converts.
        private const float UnityPerSecPerKnot = 0.0076554087f;

        private const int SlotThrust = 0, SlotRudder = 1, SlotCompartments = 2, SlotBase = 3, SlotStateMachine = 4;
        private const int SlotCount = 5;
        private static readonly string[] SlotNames = { "thrust", "rudder", "comp", "base", "sm" };

        private sealed class Track
        {
            public string Name;
            public float LastWatched;

            // A window opens on the first fixed tick after the frame code asks for it, so the
            // fixed-tick sums start from a fixed-tick position and inside + outside telescopes.
            public bool OpenRequested;
            public bool Open;
            public float FixedT0, FixedT1;
            public Vector3 FixedP0, Pre, LastPost;
            public float PreYaw, LastPostYaw;
            public bool HavePost;
            public double DtSum;
            public int Ticks;
            public Vector3 Translate, Inside, Outside;
            public double InsideYaw, OutsideYaw;
            public double TickKnotsSum;
            public int TickKnotsN;

            // D2. Only one slot is open at a time, so a slot called from inside another is not
            // counted twice.
            public readonly Vector3[] SlotDisp = new Vector3[SlotCount];
            public readonly double[] SlotYaw = new double[SlotCount];
            public int OpenSlot = -1;
            public Vector3 SlotPre;
            public float SlotPreYaw;
            public double RudderAbsYaw;
            public double TurnRateSum, TurnRateAbsSum;
            public int TurnRateN;

            public float FrameT0;
            public Vector3 FrameP0;
            public double FrameKnotsSum;
            public int FrameKnotsN;
        }

        private static readonly Dictionary<Vessel, Track> _tracks = new Dictionary<Vessel, Track>();
        private static readonly List<Vessel> _scratch = new List<Vessel>();
        private static bool _installAttempted;
        private static bool _installed;
        private static bool _thrustPatched;
        private static string _slotsPatched = "";

        // The watched vessel whose OnFixedUpdate is running, so a slot with no vessel of its own
        // (compartments, state machine) is credited to the ship that called it.
        private static Vessel _curVessel;
        private static Track _cur;

        internal static void Reset()
        {
            _tracks.Clear();
            _curVessel = null;
            _cur = null;
        }

        /// <summary>
        /// D3: the speed a surface target is converging on, beside the speed it has now.
        ///
        /// <c>Vessel.OnFixedUpdate</c> refreshes <c>_compartments.WantedVelocityInKnots</c> from the
        /// speed command every tick, and <c>applyMainThrust</c> caps it for sea state and rudder, then
        /// accelerates the ship toward the capped value, or multiplies its speed by
        /// <c>_linearDamping</c> while it is at or above it (decompiled-beta-new Vessel.cs:352-436).
        /// <c>_linearDamping</c> is <c>LinearDrag^fixedDeltaTime</c> taken once in <c>Vessel.init</c>
        /// (Vessel.cs:217), at whatever step the game had when the ship spawned.
        /// </summary>
        internal struct SpeedCommand
        {
            public float Speed;          // _velocityInKnots
            public float Wanted;         // Compartments.WantedVelocityInKnots, as the speed command set it
            public float Converge;       // Wanted after applyMainThrust's sea-state and rudder caps
            public float Rudder;         // _currentRudderAngle in degrees, NaN when unreadable
            public int SeaState;         // -1 when the environment is not up
            public float DampingPerTick; // _linearDamping, NaN when unreadable
        }

        private static FieldInfo _rudderField, _dampingField;
        private static bool _speedFieldsResolved;

        /// <summary>Read a <see cref="Vessel"/> target's <see cref="SpeedCommand"/>. False for anything else.</summary>
        internal static bool TryReadSpeedCommand(ObjectBase target, out SpeedCommand c)
        {
            c = default;
            Vessel v = target as Vessel;
            if (v == null || v.IsDestroyed || v._compartments == null || v.VP == null) return false;
            if (!_speedFieldsResolved)
            {
                _speedFieldsResolved = true;
                // Both protected, on both branches.
                const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic;
                _rudderField = typeof(Vessel).GetField("_currentRudderAngle", Inst);
                _dampingField = typeof(Vessel).GetField("_linearDamping", Inst);
            }
            try
            {
                c.Speed = v._velocityInKnots;
                c.Wanted = v._compartments.WantedVelocityInKnots;
                c.Rudder = _rudderField?.GetValue(v) is float r ? r : float.NaN;
                c.DampingPerTick = _dampingField?.GetValue(v) is float d ? d : float.NaN;
                c.SeaState = Singleton<SeaPower.Environment>.InstanceExists()
                           ? Singleton<SeaPower.Environment>.Instance.SeaState : -1;
                c.Converge = CappedWanted(v, c.Wanted, c.SeaState, c.Rudder);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>applyMainThrust's two caps on the wanted speed, in its order (Vessel.cs:352-386).</summary>
        private static float CappedWanted(Vessel v, float wanted, int seaState, float rudder)
        {
            float num = wanted;
            VesselParameters vp = v.VP;
            if (seaState > 3)
            {
                float forwardCap = vp._maxForwardVelocityInKnots - (seaState - 3f) * vp._displacementCoeff;
                float backwardCap = -vp._maxBackwardVelocityInKnots + (seaState - 3f) * vp._displacementCoeff;
                if (num > 0f && num > forwardCap) num = forwardCap > 5f ? forwardCap : 5f;
                if (num < 0f && num < backwardCap) num = backwardCap < -2f ? backwardCap : -2f;
            }
            // NaN compares false, so an unreadable rudder applies no cap.
            float absRudder = Mathf.Abs(rudder);
            if (absRudder > 1f)
            {
                float fraction = Mathf.Min(absRudder / 25f, 1f);
                float rudderCap = v.MaxForwardSpeedInKnots - fraction * v.MaxForwardSpeedInKnots / 2f;
                if (rudderCap < num) num = rudderCap;
            }
            return num;
        }

        /// <summary>Follow a coordinated round's target. Called per tick per round; cheap after the first.</summary>
        internal static void Watch(ObjectBase target, float simNow)
        {
            if (!Coordinator.TraceFlightModel) return;
            Vessel v = target as Vessel;
            if (v == null || v.IsDestroyed) return;
            EnsureInstalled();
            if (!_installed) return;
            if (!_tracks.TryGetValue(v, out Track t))
            {
                t = new Track { Name = UnitNaming.SafeName(v) };
                _tracks[v] = t;
            }
            t.LastWatched = simNow;
        }

        /// <summary>Frame side: take the frame read, close windows that are due, drop unwatched ships.</summary>
        internal static void Tick(float simNow)
        {
            if (_tracks.Count == 0) return;
            _scratch.Clear();
            _scratch.AddRange(_tracks.Keys);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Vessel v = _scratch[i];
                Track t = _tracks[v];
                if (v == null || v.IsDestroyed || simNow - t.LastWatched > DropAfterSec)
                {
                    if (v != null && t.Open) Emit(v, t, simNow, "final");
                    _tracks.Remove(v);
                    continue;
                }
                if (!t.Open && !t.OpenRequested)
                {
                    Request(v, t, simNow);
                    continue;
                }
                t.FrameKnotsSum += v._velocityInKnots;
                t.FrameKnotsN++;
                if (t.Open && t.Ticks > 0 && simNow - t.FrameT0 >= WindowSec)
                {
                    Emit(v, t, simNow, "window");
                    Request(v, t, simNow);
                }
            }
        }

        private static void Request(Vessel v, Track t, float simNow)
        {
            t.Open = false;
            t.OpenRequested = true;
            t.FrameT0 = simNow;
            t.FrameP0 = v.transform.position;
            t.FrameKnotsSum = 0.0;
            t.FrameKnotsN = 0;
        }

        private static void EnsureInstalled()
        {
            if (_installAttempted) return;
            _installAttempted = true;
            try
            {
                Harmony harmony = Bootstrap.Harmony;
                if (harmony == null) return;
                MethodInfo fixedUpdate = AccessTools.Method(typeof(Vessel), nameof(Vessel.OnFixedUpdate));
                harmony.Patch(fixedUpdate,
                    prefix: new HarmonyMethod(typeof(TargetMotionProbe), nameof(FixedPrefix)),
                    postfix: new HarmonyMethod(typeof(TargetMotionProbe), nameof(FixedPostfix)));
                _installed = true;
                // Private, so looked up by name; without it the translate term is read after the
                // whole OnFixedUpdate instead, which a speed change later in the tick would blur.
                _thrustPatched = PatchSlot(harmony, AccessTools.Method(typeof(Vessel), "applyMainThrust"),
                                           nameof(ThrustPrefix), nameof(ThrustPostfix), "thrust");
                PatchSlot(harmony, AccessTools.Method(typeof(Vessel), "applyRudderThrust"),
                          nameof(RudderPrefix), nameof(RudderPostfix), "rudder");
                PatchSlot(harmony, AccessTools.Method(typeof(Compartments), nameof(Compartments.OnFixedUpdate)),
                          nameof(NestedPrefixCompartments), nameof(NestedPostfixCompartments), "comp");
                PatchSlot(harmony, AccessTools.Method(typeof(ObjectBase), nameof(ObjectBase.OnFixedUpdate)),
                          nameof(BasePrefix), nameof(BasePostfix), "base");
                PatchSlot(harmony, AccessTools.Method(typeof(StateMachine), nameof(StateMachine.OnFixedUpdate)),
                          nameof(NestedPrefixStateMachine), nameof(NestedPostfixStateMachine), "sm");
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] tgt-motion probe installed, translate read {(_thrustPatched ? "after applyMainThrust" : "after OnFixedUpdate")}, slots{_slotsPatched}");
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] tgt-motion probe failed to install: {e.Message}");
            }
        }

        private static bool PatchSlot(Harmony harmony, MethodInfo target, string prefix, string postfix, string name)
        {
            if (target == null) return false;
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(TargetMotionProbe), prefix),
                    postfix: new HarmonyMethod(typeof(TargetMotionProbe), postfix));
                _slotsPatched += " " + name;
                return true;
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] tgt-motion probe could not bracket {name}: {e.Message}");
                return false;
            }
        }

        private static float Yaw(Vessel v) => v.transform.eulerAngles.y;

        private static void FixedPrefix(Vessel __instance)
        {
            _curVessel = null;
            _cur = null;
            if (_tracks.Count == 0 || !_tracks.TryGetValue(__instance, out Track t)) return;
            Vector3 p = __instance.transform.position;
            float yaw = Yaw(__instance);
            if (t.OpenRequested)
            {
                t.OpenRequested = false;
                t.Open = true;
                t.FixedT0 = GameClock.SimNow();
                t.FixedT1 = t.FixedT0;
                t.FixedP0 = p;
                t.DtSum = 0.0;
                t.Ticks = 0;
                t.Translate = t.Inside = t.Outside = Vector3.zero;
                t.InsideYaw = t.OutsideYaw = 0.0;
                t.TickKnotsSum = 0.0;
                t.TickKnotsN = 0;
                Array.Clear(t.SlotDisp, 0, SlotCount);
                Array.Clear(t.SlotYaw, 0, SlotCount);
                t.RudderAbsYaw = 0.0;
                t.TurnRateSum = t.TurnRateAbsSum = 0.0;
                t.TurnRateN = 0;
            }
            else if (t.Open && t.HavePost)
            {
                t.Outside += p - t.LastPost;
                t.OutsideYaw += Mathf.DeltaAngle(t.LastPostYaw, yaw);
            }
            t.Pre = p;
            t.PreYaw = yaw;
            // A slot whose method threw never ran its postfix; do not let that block this tick.
            t.OpenSlot = -1;
            if (t.Open)
            {
                _curVessel = __instance;
                _cur = t;
            }
        }

        private static void FixedPostfix(Vessel __instance)
        {
            _curVessel = null;
            _cur = null;
            if (_tracks.Count == 0 || !_tracks.TryGetValue(__instance, out Track t) || !t.Open) return;
            Vector3 p = __instance.transform.position;
            float yaw = Yaw(__instance);
            t.Inside += p - t.Pre;
            t.InsideYaw += Mathf.DeltaAngle(t.PreYaw, yaw);
            t.LastPost = p;
            t.LastPostYaw = yaw;
            t.HavePost = true;
            t.FixedT1 = GameClock.SimNow();
            t.DtSum += GameTime.fixedDeltaTime;
            t.Ticks++;
            if (!_thrustPatched) AddTranslate(__instance, t);
        }

        private static void SlotBegin(int slot)
        {
            Track t = _cur;
            if (t == null || t.OpenSlot != -1) return;
            t.OpenSlot = slot;
            t.SlotPre = _curVessel.transform.position;
            t.SlotPreYaw = Yaw(_curVessel);
        }

        private static float SlotEnd(int slot)
        {
            Track t = _cur;
            if (t == null || t.OpenSlot != slot) return 0f;
            t.OpenSlot = -1;
            float dYaw = Mathf.DeltaAngle(t.SlotPreYaw, Yaw(_curVessel));
            t.SlotDisp[slot] += _curVessel.transform.position - t.SlotPre;
            t.SlotYaw[slot] += dYaw;
            return dYaw;
        }

        private static void ThrustPrefix(Vessel __instance)
        {
            if (ReferenceEquals(__instance, _curVessel)) SlotBegin(SlotThrust);
        }

        private static void ThrustPostfix(Vessel __instance)
        {
            if (_tracks.Count == 0 || !_tracks.TryGetValue(__instance, out Track t) || !t.Open) return;
            if (ReferenceEquals(__instance, _curVessel)) SlotEnd(SlotThrust);
            AddTranslate(__instance, t);
        }

        private static void RudderPrefix(Vessel __instance)
        {
            if (ReferenceEquals(__instance, _curVessel)) SlotBegin(SlotRudder);
        }

        private static void RudderPostfix(Vessel __instance)
        {
            if (_cur == null || !ReferenceEquals(__instance, _curVessel)) return;
            float dYaw = SlotEnd(SlotRudder);
            _cur.RudderAbsYaw += Mathf.Abs(dYaw);
            float rate = __instance.CurrentTurnRate;
            _cur.TurnRateSum += rate;
            _cur.TurnRateAbsSum += Mathf.Abs(rate);
            _cur.TurnRateN++;
        }

        // ObjectBase.OnFixedUpdate runs for every object in the mission; only the watched ship's
        // own base call is a slot.
        private static void BasePrefix(ObjectBase __instance)
        {
            if (_cur != null && ReferenceEquals(__instance, _curVessel)) SlotBegin(SlotBase);
        }

        private static void BasePostfix(ObjectBase __instance)
        {
            if (_cur != null && ReferenceEquals(__instance, _curVessel)) SlotEnd(SlotBase);
        }

        private static void NestedPrefixCompartments() { if (_cur != null) SlotBegin(SlotCompartments); }
        private static void NestedPostfixCompartments() { if (_cur != null) SlotEnd(SlotCompartments); }
        private static void NestedPrefixStateMachine() { if (_cur != null) SlotBegin(SlotStateMachine); }
        private static void NestedPostfixStateMachine() { if (_cur != null) SlotEnd(SlotStateMachine); }

        private static void AddTranslate(Vessel v, Track t)
        {
            t.Translate += v._velocityVecInUnity * GameTime.fixedDeltaTime;
            t.TickKnotsSum += v._velocityInKnots;
            t.TickKnotsN++;
        }

        private static void Emit(Vessel v, Track t, float simNow, string kind)
        {
            float fixedSpan = t.FixedT1 - t.FixedT0;
            float frameSpan = simNow - t.FrameT0;
            if (t.Ticks == 0 || fixedSpan <= 1f || frameSpan <= 1f) return;

            Vector3 fwd = v.transform.forward;
            Vector3 along = new Vector3(fwd.x, 0f, fwd.z);
            if (along.sqrMagnitude < 1e-8f) return;
            along.Normalize();
            Vector3 cross = new Vector3(along.z, 0f, -along.x);

            Vector3 frameDisp = v.transform.position - t.FrameP0;
            Vector3 fixedDisp = t.LastPost - t.FixedP0;
            double frameKn = t.FrameKnotsN > 0 ? t.FrameKnotsSum / t.FrameKnotsN : 0.0;
            double tickKn = t.TickKnotsN > 0 ? t.TickKnotsSum / t.TickKnotsN : 0.0;
            float frameAlong = Knots(frameDisp, along, frameSpan);
            Vector3 closure = t.Inside + t.Outside - fixedDisp;
            closure.y = 0f;

            Vector3 rest = t.Inside;
            double restYaw = t.InsideYaw;
            var slotAlong = new System.Text.StringBuilder();
            var slotCross = new System.Text.StringBuilder();
            var slotYaw = new System.Text.StringBuilder();
            for (int s = 0; s < SlotCount; s++)
            {
                rest -= t.SlotDisp[s];
                restYaw -= t.SlotYaw[s];
                slotAlong.Append(' ').Append(SlotNames[s]).Append(' ').Append(Knots(t.SlotDisp[s], along, fixedSpan).ToString("0.000"));
                slotCross.Append(' ').Append(SlotNames[s]).Append(' ').Append(Knots(t.SlotDisp[s], cross, fixedSpan).ToString("0.000"));
                slotYaw.Append(' ').Append(SlotNames[s]).Append(' ').Append(t.SlotYaw[s].ToString("0.0000"));
            }
            slotAlong.Append(" rest ").Append(Knots(rest, along, fixedSpan).ToString("0.000"));
            slotCross.Append(" rest ").Append(Knots(rest, cross, fixedSpan).ToString("0.000"));
            slotYaw.Append(" rest ").Append(restYaw.ToString("0.0000"));

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] tgt-motion {t.Name} {kind} {t.FixedT0:0.0}-{t.FixedT1:0.0}s ticks {t.Ticks} " +
                $"dt/clock {t.DtSum / fixedSpan:0.0000} | read kn frame {frameKn:0.000} tick {tickKn:0.000} | " +
                $"along kn frame-disp {frameAlong:0.000} fixed-disp {Knots(fixedDisp, along, fixedSpan):0.000} " +
                $"translate {Knots(t.Translate, along, fixedSpan):0.000} inside {Knots(t.Inside, along, fixedSpan):0.000} " +
                $"outside {Knots(t.Outside, along, fixedSpan):0.000} | cross kn disp {Knots(fixedDisp, cross, fixedSpan):0.000} " +
                $"outside {Knots(t.Outside, cross, fixedSpan):0.000} | factor {(frameKn > 0.0 ? frameAlong / frameKn : 0.0):0.0000} " +
                $"closure {closure.magnitude * GameUnits.MetersPerUnity:0.000}m | {RigidbodyText(v)} " +
                $"step {GameTime.fixedDeltaTime:0.0000} comp {GameTime.TimeCompression:0.#}");

            double turnMean = t.TurnRateN > 0 ? t.TurnRateSum / t.TurnRateN : 0.0;
            double turnAbs = t.TurnRateN > 0 ? t.TurnRateAbsSum / t.TurnRateN : 0.0;
            Bootstrap.Log.LogInfo(
                $"[AutoTOT] tgt-motion-slots {t.Name} {kind} {t.FixedT0:0.0}-{t.FixedT1:0.0}s |" +
                $" along kn{slotAlong} | cross kn{slotCross} | yaw deg inside {t.InsideYaw:0.0000} outside {t.OutsideYaw:0.0000}" +
                $"{slotYaw} | rudder abs-yaw/tick {(t.Ticks > 0 ? t.RudderAbsYaw / t.Ticks : 0.0):0.000000}" +
                $" turn-rate mean {turnMean:0.000000} abs {turnAbs:0.000000} heading {Yaw(v):0.000}");
        }

        private static float Knots(Vector3 d, Vector3 axis, float span) =>
            Vector3.Dot(d, axis) / span / UnityPerSecPerKnot;

        /// <summary>
        /// Kept apart from <see cref="Emit"/>: Rigidbody.linearVelocity is a Unity 6 name the public
        /// branch's own code never uses, and if that build lacks it the failure lands here as an
        /// exception instead of failing the whole line.
        /// </summary>
        private static string RigidbodyText(Vessel v)
        {
            try { return RigidbodyTextUnchecked(v); }
            catch (Exception e) { return "rb unreadable (" + e.GetType().Name + ")"; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RigidbodyTextUnchecked(Vessel v)
        {
            Rigidbody rb = v.RigidBody;
            if (rb == null) return "rb none";
            Vector3 lv = rb.linearVelocity;
            float offM = (rb.position - v.transform.position).magnitude * GameUnits.MetersPerUnity;
            return $"rb kin {(rb.isKinematic ? 1 : 0)} con {(int)rb.constraints} interp {rb.interpolation} " +
                   $"vel {lv.x / UnityPerSecPerKnot:0.000},{lv.z / UnityPerSecPerKnot:0.000}kn off {offM:0.00}m";
        }
    }
}
