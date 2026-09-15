using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Research trace of a ship turning to bring a launcher to bear. Off by default
    /// (<c>Debug/AlignmentTrace</c>).
    ///
    /// When a launcher accepts an order the game gives the ship one <c>AlignUnitToTarget</c>, keyed
    /// by the order's uid (<c>ObjectBase.HandleEngageTasks</c>), and removes it through
    /// <c>ObjectBase.DelayedEndAlignment</c> or <c>EndAlignment</c>, both of which receive the name of
    /// the method that asked. A launcher that fires only part of a multi-round order ends its
    /// alignment from <c>WeaponSystem.EndEngageTask</c> after the crew delay, while the ship has
    /// already accepted the rest of the order under the same uid with a second alignment.
    /// <c>EndAlignment</c> removes the LAST alignment with a matching uid, which is the replacement, so
    /// the order goes on executing with no alignment and its remaining rounds wait for a firing arc
    /// the ship is no longer turning to. This logs every creation and every end with its caller (and
    /// the round and call path when a freed guidance channel is the caller), and once a sim second
    /// the ship's heading, rudder, allowed and selected arcs and the state of each launcher that took
    /// one of its orders.
    ///
    /// Surface ships only. Every patched method has the same signature on the public branch and on
    /// stable s366 and s367. The arc selection fields exist only from s367 and are read by reflection.
    /// </summary>
    internal static class AlignmentProbe
    {
        internal static bool Enabled;

#if LIVETEST
        // Counterfactual for the same-uid collision above: plan key AlignmentUidFix in the live test
        // build, never a config entry. See TryRemoveEndingFirst.
        internal static bool UidCollisionFix;
#else
        // The Workshop build never changes an alignment; it can only trace them.
        private const bool UidCollisionFix = false;
#endif

        private const float SteerIntervalSim = 1f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class ShipTrack
        {
            public readonly List<WeaponSystem> Launchers = new List<WeaponSystem>();
            public bool Idle;
        }

        private static readonly Dictionary<Vessel, ShipTrack> _ships = new Dictionary<Vessel, ShipTrack>();
        private static float _nextSteerSim = float.NegativeInfinity;
        private static bool _installAttempted;
        private static readonly HashSet<string> _faults = new HashSet<string>();

        // s367 Vessel.SelectAlignmentArc state; null on older builds.
        private static FieldInfo _fSelectedArc, _fTurnCommitment;
        private static FieldInfo _fAllowedArcs;

        // Set for the duration of SensorSystem.FreeChannels, which is what hands DelayedEndAlignment
        // the caller name "FreeChannels" but not the round.
        [ThreadStatic] private static WeaponBase _freeingWeapon;

        private static readonly Dictionary<string, MemberInfo> _members = new Dictionary<string, MemberInfo>();

        internal static void Reset()
        {
            _ships.Clear();
            _nextSteerSim = float.NegativeInfinity;
            _faults.Clear();
        }

        internal static void Tick(float simNow)
        {
            if (!Enabled && !UidCollisionFix) return;
            EnsureInstalled();
            if (!Enabled || simNow < _nextSteerSim || _ships.Count == 0) return;
            _nextSteerSim = simNow + SteerIntervalSim;

            List<Vessel> gone = null;
            foreach (KeyValuePair<Vessel, ShipTrack> kv in _ships)
            {
                Vessel v = kv.Key;
                ShipTrack t = kv.Value;
                if (v == null || v.IsDestroyed)
                {
                    if (gone == null) gone = new List<Vessel>();
                    gone.Add(v);
                    continue;
                }
                try
                {
                    int tasks = v._alignmentTasks != null ? v._alignmentTasks.Count : 0;
                    bool executing = false;
                    foreach (WeaponSystem ws in t.Launchers)
                        if (ws != null && ws._executingEngageTask) { executing = true; break; }
                    if (tasks == 0 && !executing)
                    {
                        if (!t.Idle)
                        {
                            t.Idle = true;
                            Log(FormattableString.Invariant(
                                $"align-idle {UnitNaming.SafeName(v)} t {simNow:0.00}: no alignment task and no launcher executing"));
                        }
                        continue;
                    }
                    t.Idle = false;
                    Log(SteerLine(v, t, simNow));
                }
                catch (Exception e) { Fault("steer", e); }
            }
            if (gone != null)
                foreach (Vessel v in gone) _ships.Remove(v);
        }

        private static string SteerLine(Vessel v, ShipTrack t, float simNow)
        {
            var b = new StringBuilder("align-steer ");
            b.Append(UnitNaming.SafeName(v))
             .Append(" t ").Append(simNow.ToString("0.00", Inv))
             .Append(" heading ").Append(Heading(v).ToString("0.0", Inv))
             .Append(" rudder ").Append(v.getCurrentRudderAngle().ToString("0.0", Inv))
             .Append(" desired ").Append(v._debugAlignDesiredHeading.ToString("0.0", Inv))
             .Append(" steerTo ").Append(v._debugAlignTargetHeading.ToString("0.0", Inv))
             .Append(" allowed ").Append(Arcs(_fAllowedArcs?.GetValue(v) as List<Vector2>))
             .Append(" selected ").Append(SelectedArc(v))
             .Append(" commit ").Append(_fTurnCommitment != null
                 ? Convert.ToString(_fTurnCommitment.GetValue(v), Inv) : "n/a")
             .Append(" tasks [");
            bool first = true;
            foreach (AlignUnitToTarget a in v._alignmentTasks)
            {
                if (a == null) continue;
                if (!first) b.Append(", ");
                first = false;
                b.Append(a._uid.ToString(Inv));
                if (a._endAlignmentDelay > -1f) b.Append(" ending ").Append(a._endAlignmentDelay.ToString("0.00", Inv));
                if (a._isGuidingWeapon) b.Append(" guiding");
            }
            b.Append("] launchers [");
            first = true;
            foreach (WeaponSystem ws in t.Launchers)
            {
                if (ws == null) continue;
                if (!first) b.Append(", ");
                first = false;
                b.Append(SysName(ws)).Append(' ').Append(ws._engageState)
                 .Append(" uid ").Append(ws._alignmentUID.ToString(Inv))
                 .Append(ws._executingEngageTask ? " executing" : " free");
            }
            return b.Append(']').ToString();
        }

        private static void EnsureInstalled()
        {
            if (_installAttempted) return;
            _installAttempted = true;
            try
            {
                Harmony harmony = Bootstrap.Harmony;
                if (harmony == null) return;
                _fSelectedArc = AccessTools.Field(typeof(Vessel), "_selectedAlignmentArc");
                _fTurnCommitment = AccessTools.Field(typeof(Vessel), "_alignTurnCommitment");
                _fAllowedArcs = AccessTools.Field(typeof(Vessel), "_debugAllowedHeadingArcs");

                var got = new StringBuilder();
                got.Append(Patch(harmony,
                    AccessTools.Constructor(typeof(AlignUnitToTarget), new[] { typeof(ObjectBase), typeof(EngageTask), typeof(WeaponSystem) }),
                    null, nameof(NewPostfix), "new"));
                got.Append(Patch(harmony,
                    AccessTools.Method(typeof(ObjectBase), "DelayedEndAlignment", new[] { typeof(int), typeof(float), typeof(string) }),
                    nameof(DelayedEndPrefix), null, "delayed-end"));
                got.Append(Patch(harmony,
                    AccessTools.Method(typeof(ObjectBase), "EndAlignment", new[] { typeof(int), typeof(string) }),
                    nameof(EndPrefix), null, "end"));
                got.Append(Patch(harmony,
                    AccessTools.Method(typeof(SensorSystem), "FreeChannels", new[] { typeof(ObjectBase), typeof(WeaponBase) }),
                    nameof(FreePrefix), nameof(FreePostfix), "free-channels"));
                Log("alignment probe installed:" + got +
                    "; arc selection fields " + (_fSelectedArc != null && _fTurnCommitment != null ? "found" : "absent (before s367)"));
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] alignment probe failed to install: {e.Message}");
            }
        }

        private static string Patch(Harmony harmony, MethodBase target, string prefix, string postfix, string name)
        {
            if (target == null) return " " + name + "(missing)";
            try
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(AlignmentProbe), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(AlignmentProbe), postfix) : null);
                return " " + name;
            }
            catch (Exception e)
            {
                return " " + name + "(failed: " + e.Message + ")";
            }
        }

        private static void NewPostfix(AlignUnitToTarget __instance, ObjectBase baseObject, EngageTask engageTask, WeaponSystem weaponSystem)
        {
            if (!Enabled) return;
            try
            {
                if (!(baseObject is Vessel v) || __instance == null) return;
                if (!_ships.TryGetValue(v, out ShipTrack t)) { t = new ShipTrack(); _ships[v] = t; }
                t.Idle = false;
                if (weaponSystem != null && !t.Launchers.Contains(weaponSystem)) t.Launchers.Add(weaponSystem);

                float h = Heading(v);
                string brg = "none";
                ObjectBase tgt = engageTask?._targetObject;
                if (tgt != null)
                {
                    Vector3 d = tgt.transform.position - v.transform.position;
                    float bearing = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
                    brg = FormattableString.Invariant($"{bearing:0.0} rel {Mathf.DeltaAngle(h, bearing):+0.0;-0.0}");
                }
                Log(FormattableString.Invariant($"align-new {UnitNaming.SafeName(v)} uid {__instance._uid} order {engageTask?._uid} ammo {engageTask?._ammoId} launcher {SysName(weaponSystem)} heading {h:0.0} target brg {brg} goingIntoRange {__instance._goingIntoFiringRange} arcs {Arcs(__instance._firingArcs)} fullArcs {Arcs(__instance._fullFiringArcs)} tasks before {v._alignmentTasks.Count} t {GameClock.SimNow():0.00}"));
            }
            catch (Exception e) { Fault("new", e); }
        }

        private static void DelayedEndPrefix(ObjectBase __instance, int uid, float time, string callerName)
        {
            if (!Enabled) return;
            try
            {
                if (!(__instance is Vessel v) || uid < 0) return;
                AlignUnitToTarget task = Find(v, uid);
                // The game ignores a request for a task that is already ending, and so does the trace.
                if (task == null || task._endAlignmentDelay >= -1f) return;
                float delay = time < 0.01f ? (v._crew != null ? v._crew._endAlignmentDelay : -1f) : time;
                var b = new StringBuilder(FormattableString.Invariant($"align-delayed-end {UnitNaming.SafeName(v)} uid {uid} caller {callerName} delay {delay:0.00} heading {Heading(v):0.0} tasks {v._alignmentTasks.Count} t {GameClock.SimNow():0.00}"));
                if (callerName == "FreeChannels")
                    b.Append(" weapon ").Append(DescribeWeapon(_freeingWeapon)).Append(" from ").Append(Callers());
                Log(b.ToString());
            }
            catch (Exception e) { Fault("delayed-end", e); }
        }

        private static void EndPrefix(ObjectBase __instance, int uid, string callerName)
        {
            if (!Enabled && !UidCollisionFix) return;
            try
            {
                if (!(__instance is Vessel v) || uid < 0 || Find(v, uid) == null) return;
#if LIVETEST
                if (UidCollisionFix && callerName == "CheckInterruptors") TryRemoveEndingFirst(v, uid);
#endif
                if (!Enabled) return;
                Log(FormattableString.Invariant($"align-end {UnitNaming.SafeName(v)} uid {uid} caller {callerName} heading {Heading(v):0.0} tasks left {v._alignmentTasks.Count - 1} t {GameClock.SimNow():0.00}"));
            }
            catch (Exception e) { Fault("end", e); }
        }

#if LIVETEST
        /// <summary>
        /// Counterfactual, live test build only. When a launcher has fired the part of an order it took,
        /// <c>WeaponSystem.EndEngageTask</c> starts the end delay of the order's alignment, and the ship
        /// re-accepts the rest of the order under the same uid with a second alignment.
        /// <c>ObjectBase.EndAlignment</c> removes the last alignment with a matching uid, which is that
        /// replacement. Swapping the ending alignment into the last matching slot first makes the call
        /// remove the alignment that was meant to end and keeps the replacement.
        /// </summary>
        private static void TryRemoveEndingFirst(Vessel v, int uid)
        {
            List<AlignUnitToTarget> tasks = v._alignmentTasks;
            int last = -1, ending = -1, matches = 0;
            for (int i = 0; i < tasks.Count; i++)
            {
                AlignUnitToTarget a = tasks[i];
                if (a == null || a._uid != uid) continue;
                matches++;
                last = i;
                if (a._endAlignmentDelay > -1f && ending < 0) ending = i;
            }
            if (matches < 2 || ending < 0 || ending == last) return;
            AlignUnitToTarget moved = tasks[last];
            tasks[last] = tasks[ending];
            tasks[ending] = moved;
            Log(FormattableString.Invariant($"align-uidfix {UnitNaming.SafeName(v)} uid {uid}: {matches} alignments share the uid, the ending one moved from slot {ending} to {last} so it is the one removed t {GameClock.SimNow():0.00}"));
        }
#endif

        private static void FreePrefix(WeaponBase weapon)
        {
            if (Enabled) _freeingWeapon = weapon;
        }

        private static void FreePostfix()
        {
            _freeingWeapon = null;
        }

        private static AlignUnitToTarget Find(ObjectBase o, int uid)
        {
            List<AlignUnitToTarget> tasks = o._alignmentTasks;
            if (tasks == null) return null;
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i] != null && tasks[i]._uid == uid) return tasks[i];
            return null;
        }

        private static string DescribeWeapon(WeaponBase w)
        {
            if (w == null) return "unknown";
            var b = new StringBuilder();
            b.Append('#').Append(w.GetInstanceID().ToString(Inv))
             .Append(' ').Append(w._ap != null ? w._ap._ammunitionFileName : "?")
             .Append(" stage ").Append(Convert.ToString(Read(w, "_flightStage"), Inv))
             .Append(" lost ").Append(Convert.ToString(Read(w, "ConnectionLost"), Inv))
             .Append('/').Append(Convert.ToString(Read(w, "ConnectionLostForever"), Inv));
            object seeker = Read(w, "_seeker");
            if (seeker != null)
            {
                MethodInfo isActive = AccessTools.Method(seeker.GetType(), "isActive");
                if (isActive != null && isActive.GetParameters().Length == 0)
                    b.Append(" seekerActive ").Append(Convert.ToString(isActive.Invoke(seeker, null), Inv));
            }
            return b.ToString();
        }

        /// <summary>A field or property by name, unwrapping a reactive property's Value. Null when absent.</summary>
        private static object Read(object o, string name)
        {
            if (o == null) return null;
            Type type = o.GetType();
            string key = type.FullName + "." + name;
            if (!_members.TryGetValue(key, out MemberInfo m))
            {
                m = (MemberInfo)AccessTools.Field(type, name) ?? AccessTools.Property(type, name);
                _members[key] = m;
            }
            object value = m is FieldInfo f ? f.GetValue(o) : m is PropertyInfo p ? p.GetValue(o, null) : null;
            if (value == null) return null;
            PropertyInfo inner = value.GetType().GetProperty("Value");
            return inner != null && value.GetType().Name.Contains("ReactiveProperty") ? inner.GetValue(value, null) : value;
        }

        /// <summary>The game frames above the patched methods, nearest first.</summary>
        private static string Callers()
        {
            var trace = new System.Diagnostics.StackTrace(2, false);
            var parts = new List<string>();
            for (int i = 0; i < trace.FrameCount && parts.Count < 4; i++)
            {
                MethodBase m = trace.GetFrame(i)?.GetMethod();
                Type dt = m?.DeclaringType;
                if (dt == null) continue;
                string ns = dt.Namespace ?? "";
                if (ns.StartsWith("AutoTOT") || ns.StartsWith("HarmonyLib") || ns.StartsWith("MonoMod")) continue;
                parts.Add(dt.Name + "." + m.Name);
            }
            return parts.Count == 0 ? "unknown" : string.Join(" < ", parts.ToArray());
        }

        private static string SelectedArc(Vessel v)
        {
            if (_fSelectedArc == null) return "n/a";
            object o = _fSelectedArc.GetValue(v);
            return o is Vector2 a ? FormattableString.Invariant($"({a.x:0},{a.y:0})") : "none";
        }

        private static string Arcs(List<Vector2> arcs)
        {
            if (arcs == null || arcs.Count == 0) return "none";
            var b = new StringBuilder();
            for (int i = 0; i < arcs.Count; i++)
            {
                if (i > 0) b.Append(' ');
                b.Append(FormattableString.Invariant($"({arcs[i].x:0},{arcs[i].y:0})"));
            }
            return b.ToString();
        }

        private static string SysName(WeaponSystem ws)
        {
            if (ws == null) return "none";
            string name = ws._vwp != null ? ws._vwp._systemName : "?";
            UnityEngine.Object unityObject = (object)ws as UnityEngine.Object;
            return unityObject != null ? name + "#" + unityObject.GetInstanceID().ToString(Inv) : name;
        }

        private static float Heading(Vessel v) => Mathf.Repeat(v.transform.eulerAngles.y, 360f);

        private static void Log(string line) => Bootstrap.Log.LogInfo("[AutoTOT] " + line);

        private static void Fault(string site, Exception e)
        {
            if (_faults.Add(site + ":" + e.GetType().Name))
                Bootstrap.Log.LogWarning($"[AutoTOT] alignment probe fault in {site}: {e.GetType().Name}: {e.Message}");
        }
    }
}
