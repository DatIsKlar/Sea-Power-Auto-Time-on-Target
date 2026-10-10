using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    // The file is named for the nested Facts type; the class itself is LauncherFactsSource.
    /// <summary>
    /// Launcher timing and capacity facts for one ship and ammo type: per-round interval, reload gap, ready rounds and
    /// magazine reserve, cached for the per-frame UI path. See docs/ARCHITECTURE.md "Launcher facts timing".
    /// </summary>
    internal static class LauncherFactsSource
    {
        private const float CacheTtlSeconds = 0.5f;   // real seconds

        /// <summary>Cadence used when a launcher declares nothing usable: the game's own
        /// FireRate default of 60 rounds/min (ObjectBaseLoader, ini key "FireRate") = 1 s/round.</summary>
        internal const float FallbackShotInterval = 1f;

        private const float SecondsPerMinute = 60f;

        /// <summary>
        /// Upper sanity bound on the hatch-animation cadence floor. Equal to <see cref="SecondsPerMinute"/>,
        /// but a duration rather than a unit conversion, so kept separate.
        /// </summary>
        private const float MaxPlausibleHatchSeconds = 60f;

        /// <summary>Launcher timing/capacity facts for one ship+ammo, read from the game's own params.</summary>
        internal struct Facts
        {
            public bool Valid;
            public float ShotInterval;   // I: seconds between rounds within a burst / at fire-rate

            /// <summary>The shot interval, or <see cref="FallbackShotInterval"/> when there is none.</summary>
            public float ShotIntervalOrFallback => (Valid && ShotInterval > 0f) ? ShotInterval : FallbackShotInterval;

            // S: fixed fire-to-first-launch offset paid before round 1: PreLaunchDelay, the expected reaction draw
            // (MaxReactiontime/2 of a uniform [0,max] roll), the acquisition gate, LauncherCycle and ColdStart.
            public float StartupDelay;
            public float ReloadGap;      // R: magazine reload (s); 0 for per-container (VLS) reload
            public int ReadyRounds;      // X: rounds ready to fire before a reload (logical tally)
            public int Reserve;          // rounds behind the rails that a reload would pull from
            public bool PerContainer;    // cells reload in parallel -> no whole-launcher gap

            // Guidance facts: a radio-command weapon holds a fire-control channel for its whole flight, so a
            // launcher with more rounds than channels cannot put them all up at once. See GuidanceChannelCap.
            public bool RequiresGuidance;     // ap._requiresGuidance: needs a launcher-side channel
            public bool CanGroup;             // ap._maxGroupSize > 1: rounds share one channel
            public int MaxGroupSize;          // ap._maxGroupSize: members one group can ever hold
            public float GroupJoinRangeU;     // ap._groupJoinRangeUnity: leader-to-mount join radius
            public bool HasGuidanceSensors;   // the serving launchers declare associated sensors
            public bool GuidanceSensorReady;  // at least one of them is operable and can guide this
            public int FreeGuidanceChannels;  // int.MaxValue when the cap does not apply
            // The sensors that guide this ammunition. The channel budget belongs to the sensor, so two
            // ammunition types served by one radar draw on one pool. Held by reference, since SensorSystem is
            // not a UnityEngine.Object and has no instance id. Null when nothing guides it.
            public SensorSystem[] GuidanceSensors;

            // A launcher serving this ammo is mid launch cycle. See IsPreparingToFire.
            public bool PreparingToFire;

            // Longest open-hatch animation across the containers (s): a cadence floor, and half of LauncherCycle.
            public float HatchCycleSeconds;

            // C: what one round costs the launcher's own state machine, declared OnRailWarmup plus the hatch
            // animation. Paid before round 1 (so it is in StartupDelay) and before every later round, because
            // WeaponSystemLauncher.cs:842 clears _onRail after every launch.
            public float LauncherCycle;

            // D: what a launcher that has never fired owes before its first round beyond LauncherCycle: the
            // magazine hoist of the game's warm-up reload. Zero for a parallel-reload (VLS) mount and while
            // every serving launcher is hot.
            public float ColdStart;  // the game's warm-up reload, when a cold launcher will run it
        }

        /// <summary>
        /// Engage states that mean a launch is under way, as opposed to idle, blocked or refused. Matched by
        /// name, so a branch that renumbers or renames the enum degrades to "not preparing" instead of
        /// matching the wrong state. LauncherTooLow and LauncherTooHigh are absent: a submarine reports them
        /// for its whole ascent to launch depth, minutes long, which would hold an order for the full
        /// NoLaunchMaxHoldSim before any launcher cycle starts.
        /// </summary>
        private static readonly HashSet<string> PreparingStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "WarmingUp", "PrelaunchSensorAlignment", "PrelaunchSensorCheck", "ChoosingContainer",
            "OpeningSystem", "OnRailWarmup", "AligningLauncher", "OpeningHatches",
            "WaitingForPitch", "WaitingForRoll", "PreLaunchDelay", "FirerateDelay",
            "SharedLaunchDelay", "LaunchingSalvo", "Launch", "Firing",
        };

        /// <summary>
        /// The engage state a trainable-launcher ship reports while turning its launcher onto the target. Not
        /// in <see cref="PreparingStates"/>: on its own it is also what a ship reports when it will never get
        /// the target into arc.
        /// </summary>
        private const string OutOfArcState = "TargetIsNotInFiringArcs";

        /// <summary>
        /// True while the ship is out of firing arc and actively turning to fix that: progress toward a
        /// launch with no launcher timer running. A Kynda firing SS-N-3b heels to fire the rounds that bear,
        /// then turns for the rest, far longer than any launcher cycle. The alignment-task test keeps an
        /// impossible order from hanging: the game holds an <c>AlignUnitToTarget</c> only while steering for
        /// an engagement (<c>ObjectBase.Aligning</c>, ObjectBase.cs:1064), created by the launcher's
        /// out-of-arc branch (WeaponSystemLauncher.cs:709-716), and callers still bound the wait by NoLaunchMaxHoldSim.
        /// </summary>
        private static bool IsUnmasking(ObjectBase ship, string state)
            => state == OutOfArcState && ship != null && ship.Aligning;

        private static readonly TtlCache<ShipAmmoKey, Facts> _cache = new TtlCache<ShipAmmoKey, Facts>(CacheTtlSeconds);

        /// <summary>
        /// The firing ship's launcher cadence and ready-round count for <paramref name="ammoId"/>. Ready
        /// rounds use the game's logical seated tally (getLoadedAmmoCount), not spawned missile objects,
        /// which SpawnWhenNeeded launchers keep near 0 when loaded. Cached for <see cref="CacheTtlSeconds"/>
        /// real seconds for the per-frame UI path.
        /// </summary>
        internal static Facts Get(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null) return default;

            var key = new ShipAmmoKey(ship, ammoId);
            if (_cache.TryGet(key, out Facts cached)) return cached;

            Facts f = Compute(ship, ammoId);
            _cache.Set(key, f);
            return f;
        }

        internal static void ClearCache()
        {
            _cache.Clear();
            _startupLogged.Clear();   // one launcher-startup line per mission, not per session
        }
        internal static long CacheHits => _cache.HitCount;
        internal static long CacheMisses => _cache.MissCount;
        internal static int CacheSize => _cache.Count;
        internal static void ResetStats() => _cache.ResetStats();

        // The OODA acquisition gate: WeaponSystemLauncher.Update holds the shot at `if
        // (!AcquisitionComplete(_engageStartTime))` (WeaponSystemLauncher.cs:386), in
        // EngageState.AligningLauncher, until `elapsed >= ReactionTime.Acquisition(unit, vwp)`: a third fixed
        // order-to-first-round cost beside PreLaunchDelay and the reaction draw. The game's method is asked
        // rather than its arithmetic copied (crew skill, reaction band, datalink tier, three Globals
        // factors). ReactionTime does not exist on the public branch, hence reflection; there the gate does
        // not exist and 0 is correct.
        private static MethodInfo _acquisitionMethod;
        private static bool _acquisitionResolved;
        private static bool _acquisitionLogged;
        private const int AcquisitionFailuresBeforeDisable = 3;
        private static int _acquisitionFailuresInARow;
        private static bool _acquisitionFailureLogged;

        private static float AcquisitionSeconds(ObjectBase ship, WeaponParameters vwp)
        {
            if (ship == null || vwp == null) return 0f;
            if (!_acquisitionResolved)
            {
                _acquisitionResolved = true;
                try
                {
                    Type t = typeof(ObjectBase).Assembly.GetType("SeaPower.ReactionTime");
                    _acquisitionMethod = t?.GetMethod("Acquisition",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(ObjectBase), typeof(WeaponParameters) }, null);
                }
                catch (Exception e)
                {
                    Bootstrap.Log.LogWarning($"[AutoTOT] reaction-gate lookup failed: {e.Message}");
                    _acquisitionMethod = null;
                }
                if (!_acquisitionLogged)
                {
                    _acquisitionLogged = true;
                    Bootstrap.Log.LogInfo(_acquisitionMethod != null
                        ? "[AutoTOT] reaction-gate: ReactionTime.Acquisition ACTIVE, launcher startup includes the OODA acquisition delay."
                        : "[AutoTOT] reaction-gate: ReactionTime.Acquisition UNAVAILABLE (public branch); no acquisition delay to model.");
                }
            }
            if (_acquisitionMethod == null) return 0f;

            try
            {
                object r = _acquisitionMethod.Invoke(null, new object[] { ship, vwp });
                _acquisitionFailuresInARow = 0;
                return (r is float f && !float.IsNaN(f) && !float.IsInfinity(f)) ? Mathf.Max(0f, f) : 0f;
            }
            catch (Exception e)
            {
                // Say so: a silent catch returns the same 0 as a gate that is off, and the two need different
                // fixes. Given up only after several failures in a row, so one ship's fault cannot remove the
                // acquisition delay for every ship.
                string why = e.InnerException?.Message ?? e.Message;
                if (!_acquisitionFailureLogged)
                {
                    _acquisitionFailureLogged = true;
                    Bootstrap.Log.LogWarning($"[AutoTOT] reaction-gate call failed for {UnitNaming.SafeName(ship)}, " +
                                             $"treating acquisition as 0: {why}");
                }
                if (++_acquisitionFailuresInARow >= AcquisitionFailuresBeforeDisable)
                {
                    Bootstrap.Log.LogWarning($"[AutoTOT] reaction-gate call failed {_acquisitionFailuresInARow} times " +
                                             $"in a row, so launcher startup leaves out the acquisition delay for " +
                                             $"the rest of the session: {why}");
                    _acquisitionMethod = null;   // stop paying for a call that keeps throwing
                }
                return 0f;
            }
        }

        /// <summary>
        /// One line per shooter and ammo naming every term of <see cref="Facts.StartupDelay"/> and the
        /// launcher parameters behind them, since a total says nothing about which term is missing. Verbose
        /// only, once per launcher per mission.
        /// </summary>
        private static readonly HashSet<ShipAmmoKey> _startupLogged = new HashSet<ShipAmmoKey>();

        /// <summary>
        /// The declared OnRailWarmup for the log line, keeping "absent" distinct from "zero": 0 is
        /// a weapon that declares no warm-up, absent is a launcher position missing from the ammo's
        /// table, where the game skips the gate outright.
        /// </summary>
        private static string DeclaredWarmupText(ObjectBase ship, string ammoId)
        {
            float w = LauncherProbe.DeclaredOnRailWarmup(ship, ammoId);
            return w < 0f ? "none" : $"{w:0.00}s";
        }

        /// <summary>
        /// Declared OnRailWarmup as a duration to sum: absent (-1) means the game skips the gate, so it collapses to 0.
        /// </summary>
        private static float DeclaredWarmup(ObjectBase ship, string ammoId)
            => Mathf.Max(0f, LauncherProbe.DeclaredOnRailWarmup(ship, ammoId));

        private static void LogStartupTerms(ObjectBase ship, string ammoId, WeaponParameters vwp,
                                            float acquisition, float total, float hatchCycle,
                                            float launcherCycle, float coldStart)
        {
            if (!Coordinator.VerboseLog || ship == null || vwp == null) return;
            if (!_startupLogged.Add(new ShipAmmoKey(ship, ammoId))) return;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] launcher-startup {ammoId} from {UnitNaming.SafeName(ship)}: total {total:0.00}s = " +
                $"preLaunch {vwp._preLaunchDelay:0.00}s + halfReaction {0.5f * vwp._maxReactiontime:0.00}s " +
                $"+ acquisition {acquisition:0.00}s " +
                $"+ launcherCycle {launcherCycle:0.00}s " +
                $"+ coldStart {coldStart:0.00}s | " +
                // The two terms of LauncherCycle, kept apart so a residual can be attributed; "none" is a
                // launcher position absent from the ammo's table, 0.00s a weapon that declares no warm-up.
                $"onRailWarmup {DeclaredWarmupText(ship, ammoId)}, " +
                $"hatchCycle {hatchCycle:0.00}s | " +
                // The cold-start term's input and the flags that gate it.
                $"magazineReload {vwp._magazineReloadTime:0.00}s, " +
                $"perContainer {vwp._perContainerReload}, " +
                $"hasWarmUp {vwp._hasWarmUp}, " +
                $"needsWarmup {LauncherProbe.AnyLauncherNeedsWarmup(ship, ammoId)} | " +
                $"targetAcqTime {vwp._targetAcquisitionTime:0.00}s, " +
                $"automatic {AutomaticText(vwp)}, standalone {vwp._worksStandalone}, " +
                $"actActive {ReactionFlag(ship, "ActActive")}, orientActive {ReactionFlag(ship, "OrientActive")}, " +
                $"scale {ReactionScale(ship):0.00}");
        }

        /// <summary>
        /// <c>WeaponParameters._automatic</c> for the log line, or "n/a" where the field does not exist (public). A
        /// compile-time read would fault the whole method at JIT time there, not at the read, so it is reflected.
        /// </summary>
        private static FieldInfo _automaticField;
        private static bool _automaticResolved;

        private static string AutomaticText(WeaponParameters vwp)
        {
            if (!_automaticResolved)
            {
                _automaticResolved = true;
                _automaticField = typeof(WeaponParameters).GetField("_automatic",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            if (_automaticField == null) return "n/a";
            try { return _automaticField.GetValue(vwp)?.ToString() ?? "n/a"; }
            catch { return "err"; }
        }

        // The game's own OODA switches, so a zero acquisition reads as "stage disabled" or "on and zero for
        // this mount".
        private static string ReactionFlag(ObjectBase ship, string method)
        {
            try
            {
                Type t = typeof(ObjectBase).Assembly.GetType("SeaPower.ReactionTime");
                MethodInfo mi = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static,
                                             null, new[] { typeof(ObjectBase) }, null);
                object r = mi?.Invoke(null, new object[] { ship });
                return r is bool b ? b.ToString() : "n/a";
            }
            catch { return "err"; }
        }

        private static float ReactionScale(ObjectBase ship)
        {
            try
            {
                Type t = typeof(ObjectBase).Assembly.GetType("SeaPower.ReactionTime");
                MethodInfo mi = t?.GetMethod("ScaleFor", BindingFlags.Public | BindingFlags.Static,
                                             null, new[] { typeof(ObjectBase) }, null);
                object r = mi?.Invoke(null, new object[] { ship });
                return r is float f ? f : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>Zero for anything a launcher declares that is NaN, infinite or negative.</summary>
        private static float NonNegativeFinite(float v)
            => (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) ? 0f : v;

        private static Facts Compute(ObjectBase ship, string ammoId)
        {
            Facts f = default;

            var launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
            if (launchers == null || launchers.Count == 0) return f;

            var vwp = launchers[0]._vwp;
            if (vwp == null) return f;

            f.Valid = true;
            // All launchers serving one ammo are assumed to share a reload style, so the first speaks for the
            // group; ready and reserve below are summed. A ship mixing both reload styles for the same ammo
            // would be misclassified.
            f.PerContainer = vwp._perContainerReload;

            // Per-round interval: within-salvo spacing when the launcher ripples a burst, else the fire-rate
            // cadence (guarded, since a 0 fire rate divides to +Infinity).
            float interval = (vwp._salvoFireAmount > 1)
                ? vwp._salvoFireTime
                : ((vwp._fireRatePerMinute > 0f) ? SecondsPerMinute / vwp._fireRatePerMinute : 0f);
            interval = NonNegativeFinite(interval);

            // The game gates each launch on both the fire-rate timer and a per-SystemName shared timer
            // (WeaponSystemLauncher.cs:633-642), e.g. SharedLaunchInterval=5 across the Slava's two SS-N-12
            // launchers. The effective cadence is the slower of the two.
            string sysName = vwp._systemName;
            if (sysName != null && ship._sharedLaunchIntervals != null &&
                ship._sharedLaunchIntervals.TryGetValue(sysName, out float shared) &&
                shared > interval && !float.IsNaN(shared) && !float.IsInfinity(shared))
                interval = shared;

            // Cadence floor from the hatch-open animation: some launchers (the Kirov's SS-N-19, 20 tubes each
            // with its own hatch) declare no FireRate, SharedLaunchInterval or SalvoFireTime, so the 1 s
            // fallback applies while the real cadence is set by the animation's last keyframe _time. Only
            // ever raises the interval.
            f.HatchCycleSeconds = MaxHatchOpenSeconds(launchers[0]);
            // Derived here because the cycle floor below needs it.
            f.LauncherCycle = NonNegativeFinite(DeclaredWarmup(ship, ammoId) + f.HatchCycleSeconds);
            if (vwp._salvoFireAmount <= 1 && launchers[0]._containers != null && launchers[0]._containers.Count > 1)
            {
                float hatch = f.HatchCycleSeconds;
                if (hatch > interval && hatch < MaxPlausibleHatchSeconds) interval = hatch;
            }

            // Cadence floor from the launcher's own state cycle, shared across the launchers serving this
            // ammunition. A launcher pays its whole cycle, warm-up included, for every round, so one
            // launcher's cycle is the cadence and several cycling in parallel deliver a round every
            // cycle/count. Only ever raises the interval, under the hatch floor's plausibility ceiling.
            if (vwp._salvoFireAmount <= 1)
            {
                float perRound = f.LauncherCycle / Mathf.Max(1, launchers.Count);
                if (perRound > interval && perRound < MaxPlausibleHatchSeconds) interval = perRound;
            }

            f.ShotInterval = interval;

            // Fire-to-first-launch startup offset, paid once before round 1, so it belongs in the release
            // lead and not in the per-round ShotInterval span:
            //   PreLaunchDelay   a fixed wait after the hatch opens (INI, default 0)
            //   MaxReactiontime  a per-engage roll, uniform [0, max]; only its expected value (half) is knowable
            //   acquisition      the OODA gate, see AcquisitionSeconds
            //   LauncherCycle    declared OnRailWarmup plus the hatch animation; a follower pays it as a late arrival
            //   ColdStart        below
            // The sub-0.3 s dispatch tick stays unmodelled.
            float acquisition = AcquisitionSeconds(ship, vwp);

            // D: what a launcher that has not fired yet owes before its first round. A magazine-fed rail
            // launcher lifts its first round from below deck (WarmingUp) before it can shoot. The gate is the
            // game's own (WeaponSystemLauncher.cs:471): warm-up runs, costing _magazineReloadTime, only when
            // the launcher declares _hasWarmUp, the ammunition sets _requiresWarmUp, the mount is not
            // _perContainerReload and the launcher is not IsHot. Mounts failing those flags (the Spruance Sea
            // Sparrow's 900 s, the Type 055 HQ-10's 600 s) carry an ordinary magazine reload there. See
            // LauncherProbe.AnyLauncherNeedsWarmup. ChoosingContainer and AligningLauncher, the states after
            // the warm-up, are not charged: they run on warm orders too, and nothing declared lies behind either.
            f.ColdStart = LauncherProbe.AnyLauncherNeedsWarmup(ship, ammoId)
                ? NonNegativeFinite(vwp._magazineReloadTime)
                : 0f;

            f.StartupDelay = NonNegativeFinite(vwp._preLaunchDelay + 0.5f * vwp._maxReactiontime
                                               + acquisition + f.LauncherCycle + f.ColdStart);
            LogStartupTerms(ship, ammoId, vwp, acquisition, f.StartupDelay, f.HatchCycleSeconds,
                            f.LauncherCycle, f.ColdStart);

            // Reload gap is paid only when loaded rails empty; parallel-reload (VLS) cells have none.
            f.ReloadGap = NonNegativeFinite(f.PerContainer ? 0f : vwp._magazineReloadTime);

            // Ready rounds: the game's logical seated tally (getLoadedAmmoCount, including SpawnWhenNeeded
            // and over-slot surplus). Reserve: magazine rounds a reload would pull. Both are on the WeaponSystem base.
            int ready = 0, reserve = 0;
            for (int i = 0; i < launchers.Count; i++)
            {
                WeaponSystem ws = launchers[i];
                if (ws == null) continue;
                ready += ws.getLoadedAmmoCount(ammoId);
                reserve += ws.getMagazineAmmoCount(ammoId);
            }
            f.ReadyRounds = Mathf.Max(0, ready);
            f.Reserve = Mathf.Max(0, reserve);
            ComputeGuidance(ship, ammoId, launchers, ref f);

            for (int i = 0; i < launchers.Count; i++)
            {
                if (launchers[i] == null) continue;
                string state = launchers[i]._engageState.ToString();
                if (!PreparingStates.Contains(state) && !IsUnmasking(ship, state)) continue;
                f.PreparingToFire = true;
                break;
            }
            return f;
        }

        /// <summary>
        /// Fire-control channel accounting for <paramref name="ammoId"/>. A radio-command weapon
        /// (<c>_requiresGuidance</c>) occupies one of its guiding sensor's weapon channels while it needs
        /// guidance. Rounds that join a group share one, so only ungroupable guided ammo is capped by
        /// channels: the Echo II's SS-N-3 declares no GroupSize and its Front_Door radar WeaponChannels=4, so
        /// an 8-round salvo can only put 4 up. Groupable ammo is capped by the group instead, shared by every
        /// shooter within GroupJoinRange of the leader; see Hud.RecomputeGroupSharing.
        /// </summary>
        private static void ComputeGuidance(ObjectBase ship, string ammoId, IList<WeaponSystem> launchers, ref Facts f)
        {
            f.FreeGuidanceChannels = int.MaxValue;

            AmmunitionParameters ap = ship.getAmmunitionByName(ammoId)?._ap;
            if (ap == null) return;
            f.RequiresGuidance = ap._requiresGuidance;
            f.CanGroup = ap._maxGroupSize > 1;
            f.MaxGroupSize = ap._maxGroupSize;
            f.GroupJoinRangeU = ap._groupJoinRangeUnity;
            if (!f.RequiresGuidance) return;

            // Distinct sensors: port and starboard launchers often name the same radar, which must not count twice.
            HashSet<SensorSystem> seen = null;
            int free = 0;
            bool unreadable = false;
            for (int i = 0; i < launchers.Count; i++)
            {
                var sensors = launchers[i]?._vwp?._associatedSensors;
                if (sensors == null) continue;
                for (int j = 0; j < sensors.Count; j++)
                {
                    SensorSystem sn = sensors[j];
                    if (sn == null || !CanGuide(sn, ap)) continue;
                    f.HasGuidanceSensors = true;
                    if (sn.Inoperable.Value) continue;
                    if (seen == null) seen = new HashSet<SensorSystem>();
                    if (!seen.Add(sn)) continue;
                    f.GuidanceSensorReady = true;
                    int channels = WeaponChannels(sn);
                    // int.MaxValue is WeaponChannels' "could not read it", meaning uncapped: summed, it would
                    // overflow (two such sensors wrap negative and refuse every order) or read as a real cap.
                    if (channels == int.MaxValue) { unreadable = true; continue; }
                    free += Mathf.Max(0, channels - (sn._associatedWeapons?.Count ?? 0));
                }
            }

            // Grouped ammo shares a channel, and ammo whose launchers name no usable guiding sensor, or one
            // whose channel count cannot be read, is left uncapped rather than clamped on missing data.
            if (f.CanGroup || !f.HasGuidanceSensors || seen == null || unreadable) return;
            f.FreeGuidanceChannels = free;

            // The identity of the pool, not just its size.
            SensorSystem[] ids = new SensorSystem[seen.Count];
            seen.CopyTo(ids);
            f.GuidanceSensors = ids;
        }

        // Branch-divergent sensor members. _weaponChannels and _associatedWeapons are public on both
        // branches; EffectiveWeaponChannels (narrowed when a datalink FCR is present) and CanGuideAmmo do not
        // exist on public, so both are reached by cached reflection with the raw values as fallback.

        private static bool _guidanceReflectionResolved;
        private static PropertyInfo _effectiveChannelsProp;
        private static MethodInfo _canGuideAmmoMethod;
        private static FieldInfo _weaponChannelsField;

        private static void ResolveGuidanceReflection()
        {
            _guidanceReflectionResolved = true;
            const BindingFlags I = BindingFlags.Public | BindingFlags.Instance;
            Type t = typeof(SensorSystem);
            _effectiveChannelsProp = t.GetProperty("EffectiveWeaponChannels", I);
            if (_effectiveChannelsProp != null && _effectiveChannelsProp.GetGetMethod() == null)
                _effectiveChannelsProp = null;
            _canGuideAmmoMethod = t.GetMethod("CanGuideAmmo", I, null, new[] { typeof(AmmunitionParameters) }, null);
            _weaponChannelsField = t.GetField("_weaponChannels", I);
        }

        private static int WeaponChannels(SensorSystem sn)
        {
            if (!_guidanceReflectionResolved) ResolveGuidanceReflection();
            try
            {
                if (_effectiveChannelsProp != null)
                    return (int)_effectiveChannelsProp.GetValue(sn, null);
                if (_weaponChannelsField != null)
                    return (int)_weaponChannelsField.GetValue(sn);
            }
            // fall through: treat as uncapped rather than clamping on a bad read
            catch { }
            return int.MaxValue;
        }

        private static bool CanGuide(SensorSystem sn, AmmunitionParameters ap)
        {
            if (!_guidanceReflectionResolved) ResolveGuidanceReflection();
            if (_canGuideAmmoMethod == null) return true;   // public branch: no per-ammo filter
            try { return (bool)_canGuideAmmoMethod.Invoke(sn, new object[] { ap }); }
            catch { return true; }
        }

        /// <summary>
        /// True when two ammunition types on one ship share at least one guiding sensor, so their rounds draw
        /// on one channel pool: the budget belongs to the sensor, not the ammunition. Same ammunition is
        /// trivially true.
        /// </summary>
        internal static bool SharesGuidanceSensor(ObjectBase ship, string ammoA, string ammoB)
        {
            if (ship == null || ammoA == null || ammoB == null) return false;
            if (string.Equals(ammoA, ammoB, StringComparison.Ordinal)) return true;

            SensorSystem[] a = Get(ship, ammoA).GuidanceSensors;
            SensorSystem[] b = Get(ship, ammoB).GuidanceSensors;
            if (a == null || b == null || a.Length == 0 || b.Length == 0) return false;

            // One or two entries each in practice, so a nested walk on reference identity, which is exactly
            // the question.
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (ReferenceEquals(a[i], b[j])) return true;
            return false;
        }

        /// <summary>
        /// Most rounds of <paramref name="ammoId"/> this ship can have guided at once, or
        /// <see cref="int.MaxValue"/> when guidance imposes no limit (unguided, groupable, or no usable
        /// guiding sensor). Clamps the salvo picker.
        /// </summary>
        internal static int GuidanceChannelCap(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.FreeGuidanceChannels : int.MaxValue;
        }

        /// <summary>
        /// A live read of the guiding sensors for this ammunition: each sensor, its channel count and how
        /// many weapons it is guiding; empty when the ammunition is not channel-limited. Uncached and
        /// separate from <see cref="ComputeGuidance"/>: occupancy rises as rounds launch while a reservation
        /// is released at dispatch, and only a read at the decision shows the two cross.
        /// </summary>
        internal static string GuidanceOccupancyText(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null) return "";
            try
            {
                AmmunitionParameters ap = ship.getAmmunitionByName(ammoId)?._ap;
                if (ap == null || !ap._requiresGuidance) return "";

                var launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
                if (launchers == null || launchers.Count == 0) return "";

                HashSet<SensorSystem> seen = new HashSet<SensorSystem>();
                StringBuilder sb = new StringBuilder();
                int free = 0;
                for (int i = 0; i < launchers.Count; i++)
                {
                    var sensors = launchers[i]?._vwp?._associatedSensors;
                    if (sensors == null) continue;
                    for (int j = 0; j < sensors.Count; j++)
                    {
                        SensorSystem sn = sensors[j];
                        if (sn == null || !CanGuide(sn, ap) || sn.Inoperable.Value) continue;
                        if (!seen.Add(sn)) continue;
                        int ch = WeaponChannels(sn);
                        int occ = sn._associatedWeapons?.Count ?? 0;
                        // An unreadable count (int.MaxValue) is uncapped; adding it would overflow the total.
                        if (ch != int.MaxValue) free += Mathf.Max(0, ch - occ);
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(sn._systemName ?? "sensor").Append(" cap ")
                          .Append(ch == int.MaxValue ? "n/a" : ch.ToString())
                          .Append(" occupied ").Append(occ);
                    }
                }
                if (sb.Length == 0) return "";
                return $"{sb} => free {free}" + (ap._maxGroupSize > 1 ? " (groupable, not capped)" : "");
            }
            catch { return "occupancy err"; }
        }

        /// <summary>
        /// What one round costs the launcher's state machine (s): declared OnRailWarmup plus the hatch
        /// animation. Part of <see cref="Facts.StartupDelay"/>, exposed for the diagnostics.
        /// </summary>
        internal static float LauncherCycleSeconds(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.LauncherCycle : 0f;
        }

        /// <summary>
        /// True while a launcher serving this ammo is mid launch cycle: warming up, choosing a container,
        /// aligning, opening hatches, or inside a fire-rate or pre-launch delay. The game's own intent, a far
        /// better stall signal than a timer: an Oscar ordered up from 350 ft spends over a minute in
        /// OpeningHatches before its first round.
        /// </summary>
        internal static bool IsPreparingToFire(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid && f.PreparingToFire;
        }

        /// <summary>
        /// The missile groups this ship could feed for <paramref name="ammoId"/>: those matching the ammo
        /// within its join range; empty when the ammo does not group or nothing matches. Tells a full group
        /// apart from an empty magazine on the SHORTFALL line. Read live: occupancy changes whenever a round
        /// joins or a group cashes in.
        /// </summary>
        internal static string DescribeGroups(ObjectBase ship, string ammoId)
        {
            if (ship == null || ship.IsDestroyed) return "";
            Facts f = Get(ship, ammoId);
            if (!f.Valid || !f.CanGroup || f.GroupJoinRangeU <= 0f) return "";

            AmmunitionParameters ap = ship.getAmmunitionByName(ammoId)?._ap;
            string ammoFile = ap?._ammunitionFileName;
            List<MissileGroup> groups = ship._taskforce?._missileGroups;
            if (ammoFile == null || groups == null) return "";

            float joinSq = f.GroupJoinRangeU * f.GroupJoinRangeU;
            int inRange = 0, full = 0, members = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                MissileGroup g = groups[i];
                Missile leader = g?._leader;
                if (leader == null || leader._ap?._ammunitionFileName != ammoFile) continue;
                if ((leader.transform.position - ship.transform.position).sqrMagnitude > joinSq) continue;
                inRange++;
                members += g.Count;
                if (g.Count >= g._maxMembers) full++;
            }
            if (inRange == 0) return $", no joinable {ammoFile} group within {f.GroupJoinRangeU * GameUnits.UnityToNm:0} nm";
            return $", {inRange} {ammoFile} group(s) within {f.GroupJoinRangeU * GameUnits.UnityToNm:0} nm " +
                   $"holding {members} round(s), {full} at the {f.MaxGroupSize}-member cap";
        }

        /// <summary>
        /// True when the ship will manoeuvre to fire this ammo rather than simply shooting once the target is in arc.
        ///
        /// When more than one weapon system takes the engage task and their combined arcs cover both beams
        /// but neither dead ahead nor dead astern, AlignUnitToTarget.CalculateFiringArcs discards the real
        /// arcs and steers to two six-degree windows:
        ///
        ///     list.Add(new Vector2(-93f, -87f));
        ///     list.Add(new Vector2(87f, 93f));
        ///
        /// so the ship keeps turning until the target is within 3 degrees of abeam, long after
        /// WeaponSystem.targetIsInFiringArc would let it shoot. The base-game case is the Kynda's two SS-N-3b mounts at
        /// FiringArcs=-105,-75|75,105: a pair, a turn, then the rest, possibly on the opposite beam.
        ///
        /// Not a function of the current bearing, since the turn happens with the target well inside the
        /// launcher's arc. The thresholds are copied from the game so they can be diffed after a patch;
        /// Vector2.zero arcs (no restriction) set the astern flag, so an unrestricted mount never qualifies.
        /// </summary>
        internal static bool WillManoeuvreToFire(ObjectBase ship, string ammoId, out float windowDeg)
        {
            windowDeg = 0f;
            if (ship == null || ship.IsDestroyed || ammoId == null) return false;

            var launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
            // Count > 1 is the game's own guard: a single mount keeps its declared arcs untouched.
            if (launchers == null || launchers.Count < 2) return false;

            List<Vector2> arcs = launchers[0]?._vwp?._firingArcs;
            if (arcs == null || arcs.Count == 0) return false;

            // The game analyses the intersection of the participating mounts' arcs. That is identity-safe
            // only when all declare the same arcs, the real case, so require it; mixed arcs decline to warn
            // rather than reimplement IntersectFiringArcs.
            for (int i = 1; i < launchers.Count; i++)
            {
                List<Vector2> other = launchers[i]?._vwp?._firingArcs;
                if (other == null || other.Count != arcs.Count) return false;
                for (int j = 0; j < arcs.Count; j++)
                    if (arcs[j] != other[j]) return false;
            }

            bool ahead = false, astern = false, portBeam = false, stbdBeam = false;
            foreach (Vector2 a in arcs)
            {
                if (a.x < a.y)
                {
                    if (a.x < -93f && a.y > -87f) portBeam = true;
                    if (a.x < 87f && a.y > 93f) stbdBeam = true;
                    if (a.x < -3f && a.y > 3f) ahead = true;
                }
                else
                {
                    if ((a.x > -87f && a.y > -87f) || (a.x < -93f && a.y < -93f)) portBeam = true;
                    if ((a.x < 87f && a.y < 87f) || (a.x > 93f && a.y > 93f)) stbdBeam = true;
                    if (a.x < 177f && a.y > -177f) astern = true;
                }
            }

            if (!portBeam || !stbdBeam || ahead || astern) return false;
            windowDeg = 6f;   // -93..-87 and 87..93, the windows the game substitutes
            return true;
        }

        /// <summary>
        /// True when this ammo needs launcher guidance and every sensor that could provide it has been shot away;
        /// <c>WeaponSystemLauncher</c> then parks the engage task in <c>ReadyUpWhileWaiting</c> indefinitely. Reads
        /// <c>Inoperable</c> (damage), not depth: no guided ammunition declares a nonzero MaxDepth, so a submerged boat
        /// surfaces before firing and a mast is never what stands in the way.
        /// </summary>
        internal static bool GuidanceSensorUnavailable(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid && f.RequiresGuidance && f.HasGuidanceSensors && !f.GuidanceSensorReady;
        }

        /// <summary>
        /// Longest open-hatch animation (s) across a launcher's containers: the last keyframe time of each
        /// <c>_openAnimation</c> (WeaponSystem._containers → WeaponContainer._openAnimation →
        /// ObjectCodeAnimation._sequences → …_sequenceData[last]._time), all public. A per-round cadence
        /// floor for launchers that ripple each round through its own hatch; 0 if none.
        /// </summary>
        private static float MaxHatchOpenSeconds(WeaponSystem ws)
        {
            if (ws == null || ws._containers == null) return 0f;
            float max = 0f;
            foreach (WeaponContainer c in ws._containers)
            {
                var anim = c?._openAnimation;
                if (anim == null || anim._sequences == null) continue;
                foreach (var seq in anim._sequences)
                {
                    var data = seq?._sequenceData;
                    if (data == null || data.Count == 0) continue;
                    float last = data[data.Count - 1]._time;
                    if (!float.IsNaN(last) && !float.IsInfinity(last) && last > max) max = last;
                }
            }
            return max;
        }

        /// <summary>
        /// Rounds this ship can fire for <paramref name="ammoId"/> through the launchers serving it (loaded
        /// plus magazine reserve), which can be less than the ship-wide
        /// <see cref="ObjectBase.AmmunitionAmountDictionary"/>. int.MaxValue when the facts cannot be read,
        /// so callers do not clamp on missing data.
        /// </summary>
        internal static int AvailableRounds(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.ReadyRounds + f.Reserve : int.MaxValue;
        }

        /// <summary>
        /// Whether firing <paramref name="salvo"/> rounds needs a mid-salvo reload: the order outruns the
        /// ready rounds and a magazine reserve exists (so all-tubes-ready launchers like the Slava never warn).
        /// </summary>
        internal static bool WillNeedReload(ObjectBase ship, string ammoId, int salvo, out int readyRounds, out int waves)
        {
            readyRounds = 0; waves = 1;
            Facts f = Get(ship, ammoId);
            if (!f.Valid || f.PerContainer || f.ReadyRounds <= 0 || f.Reserve <= 0) return false;
            readyRounds = f.ReadyRounds;
            int n = Mathf.Max(1, salvo);
            if (n <= f.ReadyRounds) return false;
            waves = Mathf.CeilToInt((float)n / f.ReadyRounds);
            return true;
        }
    }
}
