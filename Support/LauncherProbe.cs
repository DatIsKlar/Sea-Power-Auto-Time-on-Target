using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// Per-launcher-OBJECT reads for the order-to-first-round diagnostics.
    ///
    /// Everything else in the mod treats "this ship's launchers for this ammo" as one thing:
    /// <see cref="LauncherFactsSource"/> lets <c>launchers[0]</c> speak for the group, and
    /// <c>SubmarineFacts.EngageStates</c> joins every launcher's DISTINCT state into one
    /// "+"-separated string. That is fine for cadence and reload style, which really are shared,
    /// and useless for the launch-cycle question, where every quantity belongs to one launcher
    /// object:
    ///
    ///   - one engage task at a time, <c>WeaponSystem._takenEngageTask</c> is a single field;
    ///   - the on-rail warm-up clock, <c>WeaponSystemLauncher._onRail</c>, is per object;
    ///   - a busy launcher is filtered OUT of the candidate list rather than queued behind
    ///     (WeaponSelector.isWeaponSystemUsable, default ignoreExecuting = false), so the penalty
    ///     for a second order depends on how many OTHER launchers are free.
    ///
    /// Read against a merged string those are indistinguishable: a launcher holding one state for
    /// five seconds and two launchers alternating produce the same text. This class is what makes
    /// them separable.
    ///
    /// Diagnostics only. Nothing here feeds a timing decision.
    /// </summary>
    internal static class LauncherProbe
    {
        /// <summary>One launcher object's state at one instant.</summary>
        internal struct LauncherView
        {
            public WeaponSystem System;
            public int Id;              // reference identity, stable for the object's lifetime
            public string SystemName;
            public string State;        // _engageState
            public bool Executing;      // _executingEngageTask
            public bool HasTask;        // _takenEngageTask != null
            public bool OnRail;         // private _onRail: the warm-up clock is running or spent
            public float WarmupElapsed; // seconds since the warm-up clock started; -1 when not on rail
            public bool Usable;         // passes the game's own candidate filter for this target
        }

        // Cached reflection. Both fields are private on WeaponSystemLauncher, and the method has
        // optional parameters that a MethodInfo call has to pass explicitly. Resolved once: these
        // sit on the per-order diagnostic path, and a per-frame GetField would be exactly the kind
        // of cost the HUD profiling pass went looking for.
        private static bool _reflectionResolved;
        private static FieldInfo _onRailField, _onRailStartField;
        private static MethodInfo _usableMethod;

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;
            try
            {
                Type launcher = typeof(ObjectBase).Assembly.GetType("SeaPower.WeaponSystemLauncher");
                const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _onRailField = launcher?.GetField("_onRail", Inst);
                _onRailStartField = launcher?.GetField("_onRailWarmupStartTime", Inst);

                // isWeaponSystemUsable(WeaponSystem, string, ObjectBase, bool, bool, bool). Taken by
                // signature rather than by name alone: the class carries an Ammunition overload too,
                // and picking the wrong one would silently report every launcher unusable.
                Type sel = typeof(ObjectBase).Assembly.GetType("SeaPower.WeaponSelector");
                _usableMethod = sel?.GetMethod("isWeaponSystemUsable",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[]
                    {
                        typeof(WeaponSystem), typeof(string), typeof(ObjectBase),
                        typeof(bool), typeof(bool), typeof(bool),
                    }, null);
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] launcher-probe: reflection unavailable, " +
                                         $"per-launcher fields will read as unknown.\n{e}");
            }
        }


        private static readonly object[] _usableArgs = new object[6];

        /// <summary>
        /// Snapshot every launcher object serving <paramref name="ammoId"/> on this ship.
        /// Returns the count written. The list is cleared first and reused by the caller, so this
        /// allocates nothing on a repeat call.
        /// </summary>
        internal static int Collect(ObjectBase ship, string ammoId, ObjectBase target,
                                    List<LauncherView> into)
        {
            into.Clear();
            if (ship == null || ammoId == null) return 0;
            ResolveReflection();

            List<WeaponSystem> launchers;
            try { launchers = ship.GetWeaponSystemsForAmmunition(ammoId); }
            catch { return 0; }
            if (launchers == null) return 0;

            float now = GameClock.SimNow();
            for (int i = 0; i < launchers.Count; i++)
            {
                WeaponSystem ws = launchers[i];
                if (ws == null) continue;

                var v = new LauncherView
                {
                    System = ws,
                    Id = RuntimeHelpers.GetHashCode(ws),
                    SystemName = SafeSystemName(ws),
                    State = SafeState(ws),
                    WarmupElapsed = -1f,
                };
                try { v.Executing = ws._executingEngageTask; } catch { }
                try { v.HasTask = ws._takenEngageTask != null; } catch { }

                if (_onRailField != null)
                {
                    try
                    {
                        v.OnRail = _onRailField.GetValue(ws) is bool b && b;
                        if (v.OnRail && _onRailStartField != null &&
                            _onRailStartField.GetValue(ws) is double started)
                            v.WarmupElapsed = (float)(now - started);
                    }
                    catch { }
                }

                v.Usable = IsUsable(ws, ammoId, target);
                into.Add(v);
            }
            return into.Count;
        }

        /// <summary>
        /// The game's own candidate filter, the one that decides whether a second order can go to a
        /// different launcher or has to wait for this one. Called with every `ignore` flag false, as
        /// ObjectBase does when it builds the candidate list.
        /// </summary>
        private static bool IsUsable(WeaponSystem ws, string ammoId, ObjectBase target)
        {
            if (_usableMethod == null) return false;
            try
            {
                _usableArgs[0] = ws; _usableArgs[1] = ammoId; _usableArgs[2] = target;
                _usableArgs[3] = false; _usableArgs[4] = false; _usableArgs[5] = false;
                return _usableMethod.Invoke(null, _usableArgs) is bool b && b;
            }
            catch { return false; }
        }

        private static string SafeState(WeaponSystem ws)
        {
            try { return ws._engageState.ToString(); } catch { return "?"; }
        }

        private static string SafeSystemName(WeaponSystem ws)
        {
            try { return ws._vwp?._systemName ?? "?"; } catch { return "?"; }
        }

        /// <summary>
        /// Declared on-rail warm-up for this ship+ammo, in seconds.
        ///
        /// This is the term the whole investigation turned on and the one the mod never read. It is
        /// an INI value (AmmunitionParameters, "OnRailWarmup") but it lives on the AMMO keyed by
        /// launcher position name, not on the launcher, which is why enumerating WeaponParameters
        /// never found it. Guarded with ContainsKey exactly as the game guards it before applying
        /// the gate.
        ///
        /// Returns -1 when the ammo declares no entry for this launcher position, which is a
        /// different statement from 0 and needs to stay distinguishable in the log: 0 means "the
        /// weapon declares no warm-up", -1 means "this launcher position is not in the table at all,
        /// so the game skips the gate".
        /// </summary>
        internal static float DeclaredOnRailWarmup(ObjectBase ship, string ammoId)
        {
            try
            {
                List<WeaponSystem> launchers = ship?.GetWeaponSystemsForAmmunition(ammoId);
                if (launchers == null || launchers.Count == 0) return -1f;
                string sysName = launchers[0]._vwp?._systemName;
                if (string.IsNullOrEmpty(sysName)) return -1f;

                AmmunitionParameters ap = ship.getAmmunitionByName(ammoId)?._ap;
                if (ap?._launcherPositions == null) return -1f;
                return ap._launcherPositions.ContainsKey(sysName)
                    ? ap._launcherPositions[sysName]._onRailWarmup
                    : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>
        /// Declared ship-level shared launch interval for this ammo's system name, or -1 when the
        /// ship declares none.
        ///
        /// This is the SECOND serialisation mechanism, and it sits above the launcher: the timer and
        /// the interval both live on ObjectBase keyed by system name, so it gates launches across
        /// DIFFERENT launcher objects that share a name. LauncherFactsSource already reads the
        /// interval, but only to raise the within-order cadence; it has never been read as a
        /// cross-order term.
        /// </summary>
        internal static float DeclaredSharedInterval(ObjectBase ship, string ammoId)
        {
            try
            {
                List<WeaponSystem> launchers = ship?.GetWeaponSystemsForAmmunition(ammoId);
                if (launchers == null || launchers.Count == 0) return -1f;
                string sysName = launchers[0]._vwp?._systemName;
                if (string.IsNullOrEmpty(sysName) || ship._sharedLaunchIntervals == null) return -1f;
                return ship._sharedLaunchIntervals.TryGetValue(sysName, out float v) ? v : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>
        /// True when ANY launcher serving this ammo will run the game's warm-up on its next order,
        /// which is the reload that shows up as the WarmingUp engage state.
        ///
        /// This is the game's own condition, not an inference. WeaponSystemLauncher.cs:471:
        ///
        /// <code>
        /// _engageState = EngageState.WarmingUp;
        /// if (_vwp._hasWarmUp &amp;&amp; _ammoForEngage._ap._requiresWarmUp &amp;&amp; !base.IsHot)
        /// {
        ///     base.IsHot = true;
        ///     if (!DM._disableWarmUp &amp;&amp; !_vwp._perContainerReload) { StartReload(); return; }
        /// }
        /// </code>
        ///
        /// So the cost is <c>_magazineReloadTime</c>, and it is paid only by a launcher that
        /// declares a warm-up, firing ammunition that requires one, while COLD. That is why the
        /// Spruance Sea Sparrow's 900 s and the Type 055 HQ-10's 600 s never appear: those mounts
        /// fail the flags, and the field there is an ordinary magazine reload. Reading
        /// <c>_perContainerReload</c> alone, as the first build did, would have charged them both.
        ///
        /// <c>IsHot</c> is the game's cold flag and it is public. It is set the first time the
        /// launcher warms and cleared again at WeaponSystem.cs:565 once the mount has been at rest
        /// and untasked for 1800 s, so this correctly goes back to true after half an hour idle.
        /// That matches the observed runs, where a second order 612 s later skipped the warm-up.
        ///
        /// Ship-level because the launcher that will take the order is not known at commit: the
        /// game filters candidates at dispatch. "Any cold" fits every observed order, including the
        /// one where two cold launchers took an order while a warm one sat idle.
        /// </summary>
        internal static bool AnyLauncherNeedsWarmup(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null || DM._disableWarmUp) return false;

            AmmunitionParameters ap;
            List<WeaponSystem> launchers;
            try
            {
                ap = ship.getAmmunitionByName(ammoId)?._ap;
                launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
            }
            catch { return false; }
            if (ap == null || !ap._requiresWarmUp || launchers == null) return false;

            for (int i = 0; i < launchers.Count; i++)
            {
                WeaponSystem ws = launchers[i];
                WeaponParameters vwp = ws?._vwp;
                if (vwp == null) continue;
                if (vwp._hasWarmUp && !vwp._perContainerReload && !ws.IsHot) return true;
            }
            return false;
        }

        /// <summary>
        /// Compact one-line rendering of a snapshot, e.g.
        /// <c>2 launcher(s), 1 usable | #4471[Mk41] OnRailWarmup exec onRail+2.3s | #9182[Mk41] Idle free</c>.
        /// </summary>
        internal static string Describe(List<LauncherView> views)
        {
            int usable = 0;
            for (int i = 0; i < views.Count; i++) if (views[i].Usable) usable++;

            var sb = new System.Text.StringBuilder();
            sb.Append(views.Count).Append(" launcher(s), ").Append(usable).Append(" usable");
            for (int i = 0; i < views.Count; i++)
            {
                LauncherView v = views[i];
                sb.Append(" | #").Append(v.Id).Append('[').Append(v.SystemName).Append("] ")
                  .Append(v.State)
                  .Append(v.Executing ? " exec" : " free");
                if (v.HasTask) sb.Append(" tasked");
                if (v.OnRail)
                    sb.Append(v.WarmupElapsed >= 0f
                        ? $" onRail+{v.WarmupElapsed:0.0}s"
                        : " onRail");
                if (!v.Usable) sb.Append(" (filtered)");
            }
            return sb.ToString();
        }
    }
}
