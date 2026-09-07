using System;
using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    // NOTE: the file is named for the nested Facts type; the class itself is LauncherFactsSource.
    /// <summary>
    /// Launcher timing/capacity facts for one ship + ammo type: per-round interval, reload gap,
    /// ready rounds, magazine reserve. Cached for the per-frame UI path.
    /// See docs/ARCHITECTURE.md "Launcher facts timing".
    /// </summary>
    internal static class LauncherFactsSource
    {
        private const float CacheTtlSeconds = 0.5f;   // real seconds

        /// <summary>Cadence used when a launcher declares nothing usable: the game's own
        /// FireRate default of 60 rounds/min (ObjectBaseLoader.cs:2739) = 1 s/round.</summary>
        internal const float FallbackShotInterval = 1f;

        private const float SecondsPerMinute = 60f;

        /// <summary>
        /// Upper sanity bound on the hatch-animation cadence floor below. Same number as
        /// <see cref="SecondsPerMinute"/> and deliberately a separate constant: this one is a
        /// duration nobody would design a launcher around, not a unit conversion, and the two would
        /// need to move independently.
        /// </summary>
        private const float MaxPlausibleHatchSeconds = 60f;

        /// <summary>Launcher timing/capacity facts for one ship+ammo, read from the game's own params.</summary>
        internal struct Facts
        {
            public bool Valid;
            public float ShotInterval;   // I: seconds between rounds within a burst / at fire-rate
            public float StartupDelay;   // S: fixed fire-to-first-launch offset the engage cycle pays
                                         // before round 1 leaves: PreLaunchDelay + the EXPECTED
                                         // reaction draw (MaxReactiontime/2, a uniform [0,max] roll)
                                         // + the acquisition gate + LauncherCycle below.
            public float ReloadGap;      // R: magazine reload (s); 0 for per-container (VLS) reload
            public int ReadyRounds;      // X: rounds ready to fire before a reload (logical tally)
            public int Reserve;          // rounds behind the rails that a reload would pull from
            public bool PerContainer;    // cells reload in parallel -> no whole-launcher gap

            // Guidance facts. A radio-command weapon holds a fire-control channel for its whole
            // flight, so a launcher with more rounds than channels physically cannot put them all
            // up at once. See GuidanceChannelCap.
            public bool RequiresGuidance;     // ap._requiresGuidance: needs a launcher-side channel
            public bool CanGroup;             // ap._maxGroupSize > 1: rounds share one channel
            public int MaxGroupSize;          // ap._maxGroupSize: members one group can ever hold
            public float GroupJoinRangeU;     // ap._groupJoinRangeUnity: leader-to-mount join radius
            public bool HasGuidanceSensors;   // the serving launchers declare associated sensors
            public bool GuidanceSensorReady;  // at least one of them is operable and can guide this
            public int FreeGuidanceChannels;  // int.MaxValue when the cap does not apply

            // A launcher serving this ammo is mid launch cycle. See IsPreparingToFire.
            public bool PreparingToFire;

            // Longest open-hatch animation across the containers (s). Already used as a cadence
            // floor below; also one half of LauncherCycle.
            public float HatchCycleSeconds;

            // C: what one round costs the launcher's own state machine, declared OnRailWarmup plus
            // the hatch animation. Paid before round 1 (hence its place in StartupDelay) and again
            // before every later round, because WeaponSystemLauncher.cs:842 clears _onRail after
            // every launch. Measured per state in the 2026-09-06 engage-state trace; see
            // docs/plans/open/launcher-startup-delay.md.
            public float LauncherCycle;
        }

        /// <summary>
        /// Engage states that mean "a launch is under way", as opposed to idle, blocked or refused.
        /// Matched BY NAME, not by enum member: the mod ships one DLL for two game branches, and a
        /// branch that renumbers or renames the enum then degrades to "not preparing" instead of
        /// throwing or, worse, silently matching the wrong state.
        ///
        /// LauncherTooLow and LauncherTooHigh are deliberately absent. A submarine holding a
        /// radio-command weapon it cannot guide sits in LauncherTooLow forever (the launcher raises
        /// the boat only to the weapon's depth ceiling, never to the periscope depth its guidance
        /// radar mast needs), and treating that as progress would hang the order for the full hold.
        /// </summary>
        private static readonly HashSet<string> PreparingStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "WarmingUp", "PrelaunchSensorAlignment", "PrelaunchSensorCheck", "ChoosingContainer",
            "OpeningSystem", "OnRailWarmup", "AligningLauncher", "OpeningHatches",
            "WaitingForPitch", "WaitingForRoll", "PreLaunchDelay", "FirerateDelay",
            "SharedLaunchDelay", "LaunchingSalvo", "Launch", "Firing",
        };

        /// <summary>
        /// The engage state a trainable-launcher ship reports while it TURNS to bring its launcher
        /// onto the target. Not in <see cref="PreparingStates"/> because, on its own, it is also
        /// what a ship reports when it will never get the target into arc.
        /// </summary>
        private const string OutOfArcState = "TargetIsNotInFiringArcs";

        /// <summary>
        /// True while the ship is out of firing arc AND actively turning to fix that, which is
        /// progress toward a launch even though no launcher timer is running.
        ///
        /// A Kynda firing SS-N-3b does this mid-salvo: it heels to one side, fires the rounds that
        /// bear, then turns the other way for the rest. The turn is far longer than any launcher
        /// cycle and it is not a launcher cycle at all, so the salvo looked stalled and an 8-round
        /// order was closed out at 2/8 while the ship was doing exactly what it was told.
        ///
        /// The alignment-task test is what keeps this from hanging an impossible order: the game
        /// only holds an <c>AlignUnitToTarget</c> while it is actually steering the ship for an
        /// engagement (<c>ObjectBase.Aligning</c>, ObjectBase.cs:1064), and the launcher's
        /// out-of-arc branch creates one through <c>alignToTarget</c> right where it sets this state
        /// (WeaponSystemLauncher.cs:709-716). A ship that is not turning does not qualify, and
        /// callers still bound the whole thing by NoLaunchMaxHoldSim.
        /// </summary>
        private static bool IsUnmasking(ObjectBase ship, string state)
            => state == OutOfArcState && ship != null && ship.Aligning;

        private struct FactsKey : IEquatable<FactsKey>
        {
            public int UnitId; public string AmmoId;
            public bool Equals(FactsKey o) => UnitId == o.UnitId && AmmoId == o.AmmoId;
            public override bool Equals(object obj) => obj is FactsKey k && Equals(k);
            public override int GetHashCode() { unchecked { return (UnitId * 397) ^ (AmmoId?.GetHashCode() ?? 0); } }
        }

        private static readonly TtlCache<FactsKey, Facts> _cache = new TtlCache<FactsKey, Facts>(CacheTtlSeconds);

        /// <summary>
        /// Reads the firing ship's launcher cadence and ready-round count for <paramref name="ammoId"/>.
        /// Ready rounds use the game's LOGICAL seated tally (getLoadedAmmoCount), not the count of
        /// spawned missile objects on the rails ; SpawnWhenNeeded launchers keep the latter near 0
        /// even when fully loaded. Reserve is what a reload would pull from the magazine. Cached
        /// for <see cref="CacheTtlSeconds"/> real seconds because this sits on the per-frame UI path.
        /// </summary>
        internal static Facts Get(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null) return default;

            FactsKey key = new FactsKey { UnitId = ship.GetInstanceID(), AmmoId = ammoId };
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

        /// <summary>Zero for anything a launcher declares that is NaN, infinite or negative.</summary>
        // The OODA acquisition gate. WeaponSystemLauncher.Update holds the shot at
        // `if (!AcquisitionComplete(_engageStartTime))` (WeaponSystemLauncher.cs:386), sitting in
        // EngageState.AligningLauncher until `elapsed >= ReactionTime.Acquisition(unit, vwp)`. It is
        // a third fixed order-to-first-round cost, separate from PreLaunchDelay and the reaction
        // draw, and it was measured at a repeatable 2.7 s on a Type 055 whose other two terms are
        // both zero, which made it the mod's largest single timing error.
        //
        // The game does the arithmetic. It reads a crew skill, the unit's reaction band, the
        // datalink tier and three Globals scale factors, and reimplementing that here would be a
        // second copy to keep in step. Ask the game what it intends.
        //
        // ReactionTime exists ONLY on the beta branch, hence reflection: on the public branch there
        // is no such gate and 0 is the correct answer, not a fallback.
        private static MethodInfo _acquisitionMethod;
        private static bool _acquisitionResolved;
        private static bool _acquisitionLogged;

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
                return (r is float f && !float.IsNaN(f) && !float.IsInfinity(f)) ? Mathf.Max(0f, f) : 0f;
            }
            catch (Exception e)
            {
                // Say so. A silent catch here returns the same 0 as a gate that is genuinely off,
                // and those two need very different fixes.
                Bootstrap.Log.LogWarning($"[AutoTOT] reaction-gate call failed, treating acquisition " +
                                         $"as 0: {e.InnerException?.Message ?? e.Message}");
                _acquisitionMethod = null;   // stop paying for a call that throws
                return 0f;
            }
        }

        /// <summary>
        /// One line per shooter and ammo naming every term of <see cref="Facts.StartupDelay"/> and
        /// the launcher parameters they come from. The measured order-to-first-round time on some
        /// mounts is seconds while the modelled total is 0.0 s, and a total of zero says nothing
        /// about WHICH term is missing. Verbose only, once per launcher per mission.
        /// </summary>
        private static readonly HashSet<string> _startupLogged = new HashSet<string>();

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
        /// Declared OnRailWarmup as a duration to sum: the "no entry in the table" answer (-1) means
        /// the game skips the gate, which costs nothing, so it collapses to 0 here. Keep
        /// <see cref="DeclaredWarmupText"/> for the log, where the two must stay distinguishable.
        /// </summary>
        private static float DeclaredWarmup(ObjectBase ship, string ammoId)
            => Mathf.Max(0f, LauncherProbe.DeclaredOnRailWarmup(ship, ammoId));

        private static void LogStartupTerms(ObjectBase ship, string ammoId, WeaponParameters vwp,
                                            float acquisition, float total, float hatchCycle,
                                            float launcherCycle)
        {
            if (!Coordinator.VerboseLog || ship == null || vwp == null) return;
            string key = ship.GetInstanceID() + "/" + ammoId;
            if (!_startupLogged.Add(key)) return;

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] launcher-startup {ammoId} from {ship.getUIDAndName()}: total {total:0.00}s = " +
                $"preLaunch {vwp._preLaunchDelay:0.00}s + halfReaction {0.5f * vwp._maxReactiontime:0.00}s " +
                $"+ acquisition {acquisition:0.00}s " +
                $"+ launcherCycle {launcherCycle:0.00}s | " +
                // The two terms LauncherCycle is made of, kept separate so a residual can be
                // attributed to one of them. "none" is a launcher position absent from the ammo's
                // table, where the game skips the gate; 0.00s is a weapon that declares no warm-up.
                $"onRailWarmup {DeclaredWarmupText(ship, ammoId)}, " +
                $"hatchCycle {hatchCycle:0.00}s | " +
                $"targetAcqTime {vwp._targetAcquisitionTime:0.00}s, " +
                $"automatic {vwp._automatic}, standalone {vwp._worksStandalone}, " +
                $"actActive {ReactionFlag(ship, "ActActive")}, orientActive {ReactionFlag(ship, "OrientActive")}, " +
                $"scale {ReactionScale(ship):0.00}");
        }

        // Diagnostic reads of the game's own OODA switches, so the line above can say whether a zero
        // acquisition means "stage disabled" or "stage on and genuinely zero for this mount".
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
            // Assumes all launchers serving one ammo share a reload style, so the first speaks for
            // the group; ready/reserve below are summed across all of them. A ship mixing
            // per-container and whole-launcher reloaders for the SAME ammo would be misclassified.
            f.PerContainer = vwp._perContainerReload;

            // Per-round interval: within-salvo spacing when the launcher ripples a burst, else the
            // single-shot fire-rate cadence. Guard the divide (0 fire-rate -> +Infinity).
            float interval = (vwp._salvoFireAmount > 1)
                ? vwp._salvoFireTime
                : ((vwp._fireRatePerMinute > 0f) ? SecondsPerMinute / vwp._fireRatePerMinute : 0f);
            interval = NonNegativeFinite(interval);

            // The game gates each launch on BOTH the fire-rate timer AND a per-SystemName shared
            // timer (WeaponSystemLauncher.cs:633-642) ; e.g. the Slava's SS-N-12 declares
            // SharedLaunchInterval=5, shared across its port+starboard launchers. The effective
            // cadence is the slower of the two; without this the interval reads ~5x too fast and
            // every span/wave figure on these launchers is far too small.
            string sysName = vwp._systemName;
            if (sysName != null && ship._sharedLaunchIntervals != null &&
                ship._sharedLaunchIntervals.TryGetValue(sysName, out float shared) &&
                shared > interval && !float.IsNaN(shared) && !float.IsInfinity(shared))
                interval = shared;

            // Cadence FLOOR from the hatch-open animation. Some launchers (the Kirov's SS-N-19: 20
            // tubes, each its own container and hatch) declare no FireRate, SharedLaunchInterval or
            // SalvoFireTime, so `interval` falls back to the 1s default while the realized cadence is
            // dominated by opening each tube's hatch. That duration lives in the animation asset (last
            // keyframe _time), not in any numeric cadence field. Only ever RAISES the interval, so a
            // launcher that declares real timing is untouched.
            f.HatchCycleSeconds = MaxHatchOpenSeconds(launchers[0]);
            if (vwp._salvoFireAmount <= 1 && launchers[0]._containers != null && launchers[0]._containers.Count > 1)
            {
                float hatch = f.HatchCycleSeconds;
                if (hatch > interval && hatch < MaxPlausibleHatchSeconds) interval = hatch;
            }

            f.ShotInterval = interval;

            // Fire-to-first-launch startup offset (WeaponSystemLauncher.cs engage cycle):
            //   PreLaunchDelay  ; a fixed wait after the hatch opens (INI, default 0)
            //   MaxReactiontime ; a random reaction delay re-rolled per engage as uniform
            //                     [0, MaxReactiontime]; we can only take its EXPECTED value (half).
            // The launcher pays this ONCE before round 1, not between rounds, so it belongs in the
            // release lead as a fixed offset ; NOT in the per-round ShotInterval span.
            //   OnRailWarmup   ; a flat declared hold the launcher sits in before the hatch opens
            //   the hatch cycle ; the open-hatch animation, previously excluded on surface ships
            // Those last two are LauncherCycle. Excluding them was a deliberate choice made when
            // every consumer was an anchor, whose startup observation anchoring rewrites away; a
            // follower pays the whole of it as a late arrival. The 2026-09-06 trace measured the
            // declared warm-up against the observed hold on 25 launchers and they agree to the
            // 0.25s trace cadence. The sub-0.3s dispatch tick stays unmodelled: it is below the
            // resolution that measured it.
            float acquisition = AcquisitionSeconds(ship, vwp);
            f.LauncherCycle = NonNegativeFinite(DeclaredWarmup(ship, ammoId) + f.HatchCycleSeconds);
            f.StartupDelay = NonNegativeFinite(vwp._preLaunchDelay + 0.5f * vwp._maxReactiontime
                                               + acquisition + f.LauncherCycle);
            LogStartupTerms(ship, ammoId, vwp, acquisition, f.StartupDelay, f.HatchCycleSeconds,
                            f.LauncherCycle);

            // (see AcquisitionSeconds for the third term)

            // Reload gap is paid only when loaded rails empty; parallel-reload (VLS) cells have none.
            f.ReloadGap = NonNegativeFinite(f.PerContainer ? 0f : vwp._magazineReloadTime);

            // Ready rounds = the game's logical seated tally (getLoadedAmmoCount, includes
            // SpawnWhenNeeded + over-slot surplus). Reserve = rounds in the magazine behind the
            // rails that a reload would pull from. Both are on the WeaponSystem base.
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
        /// Fire-control channel accounting for <paramref name="ammoId"/>.
        ///
        /// A weapon whose mid-course correction is radio command (<c>_requiresGuidance</c>) occupies
        /// one of its guiding sensor's weapon channels for as long as it needs guidance. Rounds that
        /// can join a group share that, so only UNGROUPABLE guided ammo is capped by CHANNELS: the
        /// Echo II's SS-N-3 declares no GroupSize and its Front_Door radar declares WeaponChannels=4,
        /// so an 8-round salvo can only ever put 4 up. Its SS-N-12 fit has GroupSize=16 and is not
        /// channel-capped, which is exactly the difference observed between the two test runs.
        ///
        /// Groupable ammo is not uncapped, though: it is capped by the GROUP instead, and that cap
        /// is shared across every shooter within GroupJoinRange of the group leader. See
        /// Coordinator's shared-group check for why that costs a co-located pair half its salvo.
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

            // Distinct sensors: port and starboard launchers routinely name the same radar, and
            // counting it twice would double the apparent channel budget.
            HashSet<SensorSystem> seen = null;
            int free = 0;
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
                    free += Mathf.Max(0, WeaponChannels(sn) - (sn._associatedWeapons?.Count ?? 0));
                }
            }

            // Grouped ammo shares a channel, and ammo whose launchers name no usable guiding sensor
            // is left uncapped rather than clamped to 0 on missing data.
            if (f.CanGroup || !f.HasGuidanceSensors || seen == null) return;
            f.FreeGuidanceChannels = free;
        }

        // branch-divergent sensor members
        // _weaponChannels and _associatedWeapons are public on BOTH branches. EffectiveWeaponChannels
        // (which narrows the count when a datalink FCR is present) and CanGuideAmmo exist ONLY on
        // beta, so both are reached by cached reflection and fall back to the raw values. Binding
        // them directly would compile here and throw on the public branch. Same reasoning as
        // GameClock: one DLL runs on both.

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
        /// Most rounds of <paramref name="ammoId"/> this ship can have guided at once, or
        /// <see cref="int.MaxValue"/> when guidance imposes no limit (unguided ammo, groupable ammo,
        /// or launchers naming no usable guiding sensor). Used to clamp the salvo picker so an
        /// order that physically cannot fire is never issued.
        /// </summary>
        internal static int GuidanceChannelCap(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.FreeGuidanceChannels : int.MaxValue;
        }

        /// <summary>
        /// Longest open-hatch animation (s) across the containers of a launcher serving this ammo,
        /// or 0 when none is declared. Bounded by the same sanity limit the cadence floor uses.
        /// </summary>
        internal static float HatchCycleSeconds(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            if (!f.Valid) return 0f;
            float h = f.HatchCycleSeconds;
            return (h > 0f && h < MaxPlausibleHatchSeconds) ? h : 0f;
        }

        /// <summary>
        /// What one round costs the launcher's own state machine (s): declared OnRailWarmup plus the
        /// hatch animation. Part of <see cref="Facts.StartupDelay"/>; exposed for the diagnostics,
        /// which report the modelled terms separately from the total.
        /// </summary>
        internal static float LauncherCycleSeconds(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.LauncherCycle : 0f;
        }

        /// <summary>
        /// True while a launcher serving this ammo is mid launch cycle: warming up, choosing a
        /// container, aligning, opening hatches, or inside a fire-rate or pre-launch delay.
        ///
        /// This is the game stating its own intent, and it is a far better stall signal than any
        /// timer. An Oscar ordered up from 350 ft spends over a minute in OpeningHatches cycling 24
        /// tubes before the first round leaves; a fixed grace window cut that order off and the
        /// salvo then fired uncoordinated. Not submarine-specific: a surface ship mid hatch cycle
        /// is equally "working".
        /// </summary>
        internal static bool IsPreparingToFire(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid && f.PreparingToFire;
        }

        /// <summary>
        /// Describes the missile groups this ship could currently feed for <paramref name="ammoId"/>:
        /// the ones matching the ammo that are within its join range. Empty string when the ammo
        /// does not group or nothing matches, so it can be appended unconditionally.
        ///
        /// This is the field that tells a "group is full" stall apart from an empty magazine, which
        /// the SHORTFALL line previously could not do. Read live, never cached: occupancy changes
        /// every time a round joins or a group cashes in.
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
        /// True when the ship will MANOEUVRE to fire this ammo, rather than simply shooting when the
        /// target is inside the launcher's arcs.
        ///
        /// A launcher that bears only abeam is steered differently from every other mount. When more
        /// than one weapon system takes the engage task and their combined arcs cover both beams but
        /// neither dead ahead nor dead astern, AlignUnitToTarget.CalculateFiringArcs throws the real
        /// arcs away and steers to two SIX degree windows instead:
        ///
        ///     list.Add(new Vector2(-93f, -87f));
        ///     list.Add(new Vector2(87f, 93f));
        ///
        /// so the ship keeps turning until the target is within 3 degrees of exactly abeam, long
        /// after WeaponSystem.targetIsInFiringArc would already let it shoot. A Kynda firing SS-N-3b
        /// is the case in the base game: two linked mounts at FiringArcs=-105,-75|75,105. It fires a
        /// fore/aft pair, drifts out of the 3 degree band, turns again, then fires the rest, and
        /// because both beams qualify the second turn can settle on the opposite side.
        ///
        /// Deliberately not a function of the current target bearing. The turn happens even when the
        /// target is already well inside the launcher's own arc, so a bearing test would miss it.
        ///
        /// The thresholds below are copied from the game so they can be diffed against it after a
        /// patch. Vector2.zero arcs (no restriction) fall into the wrapping branch and set the
        /// astern flag, which clears the verdict, so an unrestricted mount never qualifies.
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

            // The game analyses the INTERSECTION of the participating mounts' arcs. Intersecting is
            // only identity-safe when they all declare the same arcs, which is the real case (the
            // Kynda's two mounts are identical), so require that rather than reimplementing
            // IntersectFiringArcs. Mixed arcs decline to warn instead of guessing.
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
        /// True when this ammo needs launcher guidance but no operable sensor can currently provide
        /// it, which for a submerged submarine means a radar mast that is still under water. The
        /// order will never fire until the boat comes shallow enough to raise it.
        /// </summary>
        internal static bool GuidanceSensorUnavailable(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid && f.RequiresGuidance && f.HasGuidanceSensors && !f.GuidanceSensorReady;
        }

        /// <summary>
        /// Longest open-hatch animation duration (seconds) across a launcher's containers ; the last
        /// keyframe time of each container's <c>_openAnimation</c>. Used as a per-round cadence floor
        /// for launchers that ripple each round through its own hatch. All fields are public on the
        /// game types (WeaponSystem._containers → WeaponContainer._openAnimation →
        /// ObjectCodeAnimation._sequences → …_sequenceData[last]._time). 0 if none.
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
        /// Rounds this ship can actually fire for <paramref name="ammoId"/> through the launchers that
        /// serve it (loaded on the rails + magazine reserve behind them). This can be LESS than the
        /// ship-wide inventory in <see cref="ObjectBase.AmmunitionAmountDictionary"/> when some rounds
        /// sit behind a launcher/magazine that can't feed them. Returns int.MaxValue when the launcher
        /// facts can't be read, so callers don't clamp on missing data.
        /// </summary>
        internal static int AvailableRounds(ObjectBase ship, string ammoId)
        {
            Facts f = Get(ship, ammoId);
            return f.Valid ? f.ReadyRounds + f.Reserve : int.MaxValue;
        }

        /// <summary>
        /// UI helper: does firing <paramref name="salvo"/> rounds of this ammo require a
        /// mid-salvo reload? Only true when the order outruns the ready rounds AND there is a
        /// magazine reserve to reload from (so all-tubes-ready launchers like the Slava never warn).
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
