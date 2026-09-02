using System;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Launcher timing/capacity facts for one ship + ammo type, read from the game's own weapon
    /// parameters: per-round firing interval (including shared-launch-interval gating), reload
    /// gap, ready rounds, and magazine reserve. Feeds the coordinator's release-lead / reload-wave
    /// math and the planner UI's ETA rows and reload warnings.
    ///
    /// <see cref="Get"/> is on the per-frame UI path, so results are cached the same way
    /// <see cref="FlightTime"/> caches kinematic estimates.
    /// </summary>
    internal static class LauncherFactsSource
    {
        private const float CacheTtlSeconds = 0.5f;   // real seconds

        /// <summary>Cadence used when a launcher declares nothing usable: the game's own
        /// FireRate default of 60 rounds/min (ObjectBaseLoader.cs:2739) = 1 s/round.</summary>
        internal const float FallbackShotInterval = 1f;

        private const float SecondsPerMinute = 60f;

        /// <summary>Launcher timing/capacity facts for one ship+ammo, read from the game's own params.</summary>
        internal struct Facts
        {
            public bool Valid;
            public float ShotInterval;   // I: seconds between rounds within a burst / at fire-rate
            public float StartupDelay;   // S: fixed fire-to-first-launch offset the engage cycle pays
                                         // ONCE before round 1 leaves: PreLaunchDelay + the EXPECTED
                                         // reaction draw (MaxReactiontime/2, a uniform [0,max] roll).
                                         // Both are config; the hatch-open animation is NOT counted
                                         // (asset-driven, and observation anchoring absorbs it).
            public float ReloadGap;      // R: magazine reload (s); 0 for per-container (VLS) reload
            public int ReadyRounds;      // X: rounds ready to fire before a reload (logical tally)
            public int Reserve;          // rounds behind the rails that a reload would pull from
            public bool PerContainer;    // cells reload in parallel -> no whole-launcher gap
        }

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
        /// spawned missile objects on the rails — SpawnWhenNeeded launchers keep the latter near 0
        /// even when fully loaded. Reserve is what a reload would pull from the magazine. Cached
        /// for <see cref="CacheTtlSeconds"/> real seconds because this sits on the per-frame UI path.
        /// </summary>
        internal static Facts Get(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null) return default;

            FactsKey key = new FactsKey { UnitId = ship.GetInstanceID(), AmmoId = ammoId };
            if (_cache.TryGet(key, out Facts hit)) return hit;

            Facts f = Compute(ship, ammoId);
            _cache.Set(key, f);
            return f;
        }

        internal static void ClearCache() => _cache.Clear();
        internal static long CacheHits => _cache.HitCount;
        internal static long CacheMisses => _cache.MissCount;
        internal static int CacheSize => _cache.Count;
        internal static void ResetStats() => _cache.ResetStats();

        private static Facts Compute(ObjectBase ship, string ammoId)
        {
            Facts f = default;

            var launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
            if (launchers == null || launchers.Count == 0) return f;

            var vwp = launchers[0]._vwp;
            if (vwp == null) return f;

            f.Valid = true;
            // Assumption: all launchers serving one ammo share a reload style, so the first
            // launcher's _perContainerReload speaks for the group. Ready/reserve below are summed
            // across every launcher. This holds for real Sea Power loadouts (one launcher type per
            // ammo); a ship mixing per-container and whole-launcher reloaders for the SAME ammo would
            // be misclassified here, which no current unit does.
            f.PerContainer = vwp._perContainerReload;

            // Per-round interval: within-salvo spacing when the launcher ripples a burst, else the
            // single-shot fire-rate cadence. Guard the divide (0 fire-rate -> +Infinity).
            float interval = (vwp._salvoFireAmount > 1)
                ? vwp._salvoFireTime
                : ((vwp._fireRatePerMinute > 0f) ? SecondsPerMinute / vwp._fireRatePerMinute : 0f);
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0f) interval = 0f;

            // The game gates each launch on BOTH the fire-rate timer AND a per-SystemName shared
            // timer (WeaponSystemLauncher.cs:633-642) — e.g. the Slava's SS-N-12 declares
            // SharedLaunchInterval=5, shared across its port+starboard launchers. The effective
            // cadence is the slower of the two; without this the interval reads ~5x too fast and
            // every span/wave figure on these launchers is far too small.
            string sysName = vwp._systemName;
            if (sysName != null && ship._sharedLaunchIntervals != null &&
                ship._sharedLaunchIntervals.TryGetValue(sysName, out float shared) &&
                shared > interval && !float.IsNaN(shared) && !float.IsInfinity(shared))
                interval = shared;

            // Cadence FLOOR from the hatch-open animation. Some launchers (e.g. the Kirov's SS-N-19:
            // 20 individual tubes, each a container with its own ShaftHatchOpenAnim) declare NO
            // FireRate/SharedLaunchInterval/SalvoFireTime, so `interval` falls back to the 1s
            // fire-rate default — far faster than reality (observed ~3.9s). Their realized per-round
            // cadence is dominated by opening each tube's hatch, whose duration lives in the animation
            // asset (last keyframe _time), NOT a numeric cadence field. When each round ripples through
            // its OWN hatch (per-round engage cycle + multiple hatched tubes), take that hatch-open
            // time as a FLOOR on the interval. This never lowers a launcher that declares a real
            // cadence (we only raise), so it's a pure fallback — zero regression for stock or modded
            // launchers that set their timing; it only fills the gap for ones that leave it unset.
            if (vwp._salvoFireAmount <= 1 && launchers[0]._containers != null && launchers[0]._containers.Count > 1)
            {
                float hatch = MaxHatchOpenSeconds(launchers[0]);
                if (hatch > interval && hatch < 60f) interval = hatch;
            }

            f.ShotInterval = interval;

            // Fire-to-first-launch startup offset (WeaponSystemLauncher.cs engage cycle):
            //   PreLaunchDelay  — a fixed wait after the hatch opens (INI, default 0)
            //   MaxReactiontime — a random reaction delay re-rolled per engage as uniform
            //                     [0, MaxReactiontime]; we can only take its EXPECTED value (half).
            // The launcher pays this ONCE before round 1, not between rounds, so it belongs in the
            // release lead as a fixed offset — NOT in the per-round ShotInterval span.
            float startup = vwp._preLaunchDelay + 0.5f * vwp._maxReactiontime;
            if (float.IsNaN(startup) || float.IsInfinity(startup) || startup < 0f) startup = 0f;
            f.StartupDelay = startup;

            // Reload gap is paid only when loaded rails empty; parallel-reload (VLS) cells have none.
            float reload = f.PerContainer ? 0f : vwp._magazineReloadTime;
            if (float.IsNaN(reload) || float.IsInfinity(reload) || reload < 0f) reload = 0f;
            f.ReloadGap = reload;

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
            return f;
        }

        /// <summary>
        /// Longest open-hatch animation duration (seconds) across a launcher's containers — the last
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
