using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Batches the launches of a single group missile order, works out each shooter's
    /// flight time, and releases each launch late enough that the whole salvo converges.
    /// </summary>
    internal static class Coordinator
    {
        // Unity world-units -> metres. The game's own scale (ScaledBody / velocity constant).
        private const float MetersPerUnity = 67.200066f;
        private const float KnotsToMs = 0.5144447f;

        // Tunables (wired to config in Plugin.Awake).
        internal static bool Enabled = true;   // master switch (config)
        internal static bool Active = true;    // runtime toggle (hotkey / on-screen button)
        internal static float DebounceSeconds = 0.75f; // real time with no new orders => batch is complete
        internal static float MaxWindowSeconds = 6.0f; // hard cap on how long a batch stays open
        internal static bool VerboseLog = false;

        private sealed class Intent
        {
            public ObjectBase Unit;
            public string AmmoId;
            public ObjectBase Target;
            public int Shots;
            public int Priority;
            public bool IsFormation;
            public float ImpactOffset;
        }

        private sealed class Batch
        {
            public ObjectBase Target;
            public float FirstRealTime;
            public float LastRealTime;
            public readonly List<Intent> Items = new List<Intent>();
        }

        private sealed class Scheduled
        {
            public Intent Item;
            public float ImpactAtSim;   // fixed target impact time; launch decided live
        }

        // Open batches, keyed by the shared target.
        private static readonly Dictionary<ObjectBase, Batch> _openBatches = new Dictionary<ObjectBase, Batch>();
        private static readonly List<Scheduled> _scheduled = new List<Scheduled>();

        /// <summary>Called from the Harmony prefix. Returns true if the launch was deferred.</summary>
        internal static bool TryIntercept(
            ObjectBase unit, string ammoId, ObjectBase target,
            bool autoAttack, bool isFormationAttack, int shots, int priority)
        {
            if (!Enabled || !Active) return false; // master off, or toggled off -> fire normally
            if (autoAttack) return false;          // only player-issued orders
            if (unit == null || target == null) return false;
            if (!unit.IsPlayerObject) return false;
            // Coordinates BOTH cases: several ships firing at one target (formation attack),
            // and one ship firing several missile orders (different types) at one target.
            // Grouping is by shared target within the collection window.

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            if (ammo == null || ammo._ap == null || ammo._ap._type != Ammunition.Type.Missile)
                return false; // only missiles

            if (!unit.DoesAmmoMatchTarget(ammo._ap, target, out _))
                return false; // weapon cannot engage this target type

            if (!_openBatches.TryGetValue(target, out Batch batch))
            {
                batch = new Batch { Target = target, FirstRealTime = Time.unscaledTime };
                _openBatches[target] = batch;
            }
            batch.LastRealTime = Time.unscaledTime;
            batch.Items.Add(new Intent
            {
                Unit = unit,
                AmmoId = ammoId,
                Target = target,
                Shots = shots,
                Priority = priority,
                IsFormation = isFormationAttack,
            });

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] queued {unit.getUIDAndName()} -> {target.getUIDAndName()} ({ammoId} x{shots})");
            return true;
        }

        /// <summary>Clears all coordinator state. Called on mission end to prevent stale data.</summary>
        internal static void Reset()
        {
            _openBatches.Clear();
            _scheduled.Clear();
            _tofCache.Clear();
            _flightTracker.Clear();
            _salvoMap.Clear();
            _firedAt.Clear();
            _impactByTarget.Clear();
            _impactSpreadByTarget.Clear();
            _trackerScratch.Clear();
            _pruneScratch.Clear();
            _lastReleaseSimNow = -1f;
            if (VerboseLog) Bootstrap.Log.LogInfo("[AutoTOT] coordinator state reset.");
        }

        /// <summary>Pumped every frame from Plugin.Update.</summary>
        internal static void Tick()
        {
            UpdateFlightTracker();
            CommitReadyBatches();
            ReleaseDueLaunches();
        }

        private static void CommitReadyBatches()
        {
            if (_openBatches.Count == 0) return;

            List<ObjectBase> ready = null;
            float now = Time.unscaledTime;
            foreach (KeyValuePair<ObjectBase, Batch> kv in _openBatches)
            {
                Batch b = kv.Value;
                bool settled = (now - b.LastRealTime) >= DebounceSeconds;
                bool timedOut = (now - b.FirstRealTime) >= MaxWindowSeconds;
                if (settled || timedOut)
                {
                    (ready ??= new List<ObjectBase>()).Add(kv.Key);
                }
            }
            if (ready == null) return;

            foreach (ObjectBase key in ready)
            {
                Batch b = _openBatches[key];
                _openBatches.Remove(key);
                CommitBatch(b);
            }
        }

        private static void CommitBatch(Batch b)
        {
            var expanded = new List<Intent>();
            foreach (Intent it in b.Items)
            {
                if (it.Shots > 1)
                {
                    float interval = GetLauncherInterval(it.Unit, it.AmmoId);
                    if (interval > 0f)
                    {
                        expanded.AddRange(SplitIntents(it, interval));
                        continue;
                    }
                }
                it.ImpactOffset = 0f;
                expanded.Add(it);
            }

            float maxEnroute = 0f;
            for (int i = 0; i < expanded.Count; i++)
            {
                float e = EstimateEnrouteSeconds(expanded[i]);
                float needed = e + expanded[i].ImpactOffset;
                if (needed > maxEnroute) maxEnroute = needed;
            }
            Schedule(expanded, GameTime.time + maxEnroute);

            if (VerboseLog || expanded.Count > 1)
            {
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] coordinating {expanded.Count} missile launch(es) on {b.Target?.getUIDAndName()}: " +
                    $"longest enroute {maxEnroute:0.0}s, impacts synced.");
            }
        }

        private static void Schedule(IEnumerable<Intent> items, float baseImpact)
        {
            float minImpact = float.MaxValue, maxImpact = float.MinValue;
            ObjectBase target = null;
            
            foreach (Intent it in items)
            {
                float impactAt = baseImpact + it.ImpactOffset;
                _scheduled.Add(new Scheduled { Item = it, ImpactAtSim = impactAt });
                if (it.Target != null)
                {
                    target = it.Target;
                    _impactByTarget[target] = baseImpact;
                    minImpact = Mathf.Min(minImpact, impactAt);
                    maxImpact = Mathf.Max(maxImpact, impactAt);
                }
            }
            
            if (target != null && minImpact < float.MaxValue)
            {
                float spread = (maxImpact - minImpact) / 2f;
                _impactSpreadByTarget[target] = spread;
                if (VerboseLog && spread > 0.1f)
                    Bootstrap.Log.LogInfo($"[AutoTOT] scheduled spread for {target.getUIDAndName()}: ±{spread:0.0}s");
            }
        }

        private static float EstimateEnrouteSeconds(Intent it)
            => EstimateEnroute(it.Unit, it.AmmoId, it.Target);

        /// <summary>
        /// Flight-time estimate (s) to the target. Primary path is the game's own kinematic shot
        /// simulator (correct for lofted/bleeding missiles); a straight-line max-speed estimate is
        /// used only if the simulator declines. Used for the ETA readout and launch timing.
        /// </summary>
        internal static float EstimateEnroute(ObjectBase unit, string ammoId, ObjectBase target)
        {
            if (unit == null || target == null) return 0f;

            Ammunition ammo = unit.getAmmunitionByName(ammoId);
            AmmunitionParameters ap = ammo?._ap;
            if (ap == null) return 0f;

            float kinematic = KinematicFlightTime(unit, ap, target);
            if (kinematic > 0.01f) return kinematic;

            // Fallback only if the simulator declined (out of range / no kinematics): straight
            // line at max speed, better than holding a launch forever.
            float speedMs = ap._maxVelocityInKnots * KnotsToMs;
            if (speedMs <= 0.1f) return 0f;
            float meters = (target.transform.position - unit.transform.position).magnitude * MetersPerUnity;
            return meters / speedMs;
        }

        private static float GetLauncherInterval(ObjectBase ship, string ammoId)
        {
            if (ship == null || ammoId == null) return 0f;
            var launchers = ship.GetWeaponSystemsForAmmunition(ammoId);
            if (launchers == null || launchers.Count == 0)
            {
                if (VerboseLog) Bootstrap.Log.LogInfo($"[AutoTOT] GetLauncherInterval: no launchers found for {ammoId}");
                return 0f;
            }

            var vwp = launchers[0]._vwp;
            if (vwp == null)
            {
                if (VerboseLog) Bootstrap.Log.LogInfo($"[AutoTOT] GetLauncherInterval: vwp null for {ammoId}");
                return 0f;
            }

            if (VerboseLog) Bootstrap.Log.LogInfo($"[AutoTOT] GetLauncherInterval raw: fireRate={vwp._fireRatePerMinute}, reloadTime={vwp._magazineReloadTime}, salvoAmount={vwp._salvoFireAmount}, salvoTime={vwp._salvoFireTime}, preLaunchDelay={vwp._preLaunchDelay}, targetAcqTime={vwp._targetAcquisitionTime}, burstTime={vwp._burstTime}");
            float interval;
            if (vwp._salvoFireAmount > 1)
            {
                interval = vwp._salvoFireTime;
            }
            else
            {
                // Guard the division: a 0 fire-rate would make 60/rate = +Infinity, which propagates
                // through the salvo split into an impact time that never comes due -> shots held forever.
                float rateInterval = (vwp._fireRatePerMinute > 0f) ? 60f / vwp._fireRatePerMinute : 0f;
                interval = Mathf.Max(rateInterval, Mathf.Max(vwp._magazineReloadTime, vwp._targetAcquisitionTime));
            }

            // Never let a non-finite / negative interval escape (callers treat 0 as "don't split").
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0f)
                interval = 0f;


            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] GetLauncherInterval: {ammoId} = {interval:0.0}s ({launchers.Count} launcher(s), salvoAmount={vwp._salvoFireAmount})");

            return interval;
        }

        private static List<Intent> SplitIntents(Intent baseIntent, float interval)
        {
            var result = new List<Intent>(baseIntent.Shots);
            int count = baseIntent.Shots;
            float halfSpan = (count - 1) / 2f * interval;

            for (int i = 0; i < count; i++)
            {
                float offset = (i * interval) - halfSpan;
                result.Add(new Intent
                {
                    Unit = baseIntent.Unit,
                    AmmoId = baseIntent.AmmoId,
                    Target = baseIntent.Target,
                    Shots = 1,
                    Priority = baseIntent.Priority,
                    IsFormation = baseIntent.IsFormation,
                    ImpactOffset = offset,
                });
            }
            return result;
        }


        // The kinematic sim is ~100+ integration steps, and the planner UI asks for the flight
        // time of every weapon row on every OnGUI pass. Cache each answer briefly (keyed by
        // shooter+ammo+target) so those repeated calls collapse to one sim per shot per refresh
        // window — positions barely move in a fraction of a real second, so this is lossless in
        // practice and removes the per-frame stutter.
        private struct TofKey : System.IEquatable<TofKey>
        {
            public int UnitId;
            public string AmmoFile;
            public int TargetId;

            public bool Equals(TofKey o) =>
                UnitId == o.UnitId && TargetId == o.TargetId && AmmoFile == o.AmmoFile;
            public override bool Equals(object obj) => obj is TofKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked { return ((UnitId * 397) ^ TargetId) * 397 ^ (AmmoFile?.GetHashCode() ?? 0); }
            }
        }

        private struct TofCacheEntry { public float StampUnscaled; public float Value; }
        private static readonly Dictionary<TofKey, TofCacheEntry> _tofCache =
            new Dictionary<TofKey, TofCacheEntry>();
        private const float TofCacheTtl = 0.5f;   // real seconds
        private static readonly List<TofKey> _tofEvictScratch = new List<TofKey>();

        /// <summary>
        /// Flight time (s) from the game's own kinematic shot simulator, or -1 if unavailable.
        /// Result is cached for <see cref="TofCacheTtl"/> real seconds per shooter/ammo/target.
        /// </summary>
        private static float KinematicFlightTime(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            float nowReal = Time.unscaledTime;
            TofKey key = new TofKey
            {
                UnitId = unit.GetInstanceID(),
                AmmoFile = ap._ammunitionFileName,
                TargetId = target.GetInstanceID(),
            };
            if (_tofCache.TryGetValue(key, out TofCacheEntry hit) && (nowReal - hit.StampUnscaled) < TofCacheTtl)
                return hit.Value;

            float value = KinematicFlightTimeRaw(unit, ap, target);

            // Smart eviction: expire stale entries first; only if still over limit, clear all.
            if (_tofCache.Count > 512)
            {
                _tofEvictScratch.Clear();
                foreach (KeyValuePair<TofKey, TofCacheEntry> kv in _tofCache)
                {
                    if ((nowReal - kv.Value.StampUnscaled) >= TofCacheTtl)
                        _tofEvictScratch.Add(kv.Key);
                }
                for (int i = 0; i < _tofEvictScratch.Count; i++)
                    _tofCache.Remove(_tofEvictScratch[i]);

                if (_tofCache.Count > 512) _tofCache.Clear();
            }

            _tofCache[key] = new TofCacheEntry { StampUnscaled = nowReal, Value = value };
            return value;
        }

        private static float KinematicFlightTimeRaw(ObjectBase unit, AmmunitionParameters ap, ObjectBase target)
        {
            try
            {
                Vector3 targetPos = target.transform.position;
                Vector3 targetVel = target._velocityVecInUnity;
                bool evasive = ap.AssumeEvasiveTarget(target);
                // iterations = 0: single-pass estimate. The precise iterative version (the game uses
                // 8) is ~8-9x the sim work and only nudged the fast kinematic missile by ~3s while
                // doing nothing for the low-kinematics cruise missile (which the game forces to a
                // single pass anyway) — not worth the per-frame cost / stutter for a ~3s gain.
                Missile.KinematicRangeResult kr = ap.MaxRangePrecise(unit, targetPos, targetVel, 0, evasive);
                return (kr != null) ? kr.InterceptTime : -1f;   // InterceptTime < 0 => out of range
            }
            catch (System.Exception e)
            {
                if (VerboseLog) Bootstrap.Log.LogWarning($"[AutoTOT] kinematic flight-time failed: {e.Message}");
                return -1f;
            }
        }

        // Per-missile record kept while a friendly missile is airborne, so we can report its
        // impact (flight time + final range) after the missile object is gone.
        private struct FlightSample
        {
            public float LaunchTime; public string AmmoName; public string TargetName;
            public float LastDistM; public float LastSeenTime;
        }
        private static readonly Dictionary<WeaponBase, FlightSample> _flightTracker =
            new Dictionary<WeaponBase, FlightSample>();
        private static readonly List<WeaponBase> _trackerScratch = new List<WeaponBase>();

        /// <summary>
        /// Each tick: record a baseline the first time we see each friendly missile airborne, keep
        /// its last-known distance/time updated, and when a tracked missile vanishes (hit target,
        /// intercepted, or ran out) report its outcome — flight time and final range — so the log
        /// shows when each salvo member ACTUALLY arrived, not just when it launched.
        /// </summary>
        private static void UpdateFlightTracker()
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;
            float now = GameTime.time;

            List<WeaponBase> weapons = Singleton<ObjectsManager>.Instance._listOfAllWeapons;
            for (int i = 0; i < weapons.Count; i++)
            {
                WeaponBase w = weapons[i];
                if (w == null || w.IsDestroyed) continue;
                if (w._type != ObjectBase.ObjectType.Missile || !w.IsPlayerObject) continue;
                ObjectBase tgt = w.CurrentIntendedTargetObject;
                if (tgt == null || tgt.IsDestroyed) continue;

                float distM = (tgt.transform.position - w.transform.position).magnitude * MetersPerUnity;
                if (_flightTracker.TryGetValue(w, out FlightSample existing))
                {
                    existing.LastDistM = distM;
                    existing.LastSeenTime = now;
                    _flightTracker[w] = existing;
                }
                else
                {
                    _flightTracker[w] = new FlightSample
                    {
                        LaunchTime = w._launchTime,
                        AmmoName = (w._ap != null ? w._ap._ammunitionFileName : "?"),
                        TargetName = tgt.getUIDAndName(),
                        LastDistM = distM,
                        LastSeenTime = now,
                    };
                }
            }

            _trackerScratch.Clear();
            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed || w._type != ObjectBase.ObjectType.Missile)
                    _trackerScratch.Add(w);
            }
            for (int i = 0; i < _trackerScratch.Count; i++)
            {
                WeaponBase w = _trackerScratch[i];
                if (_flightTracker.TryGetValue(w, out FlightSample s))
                {
                    float flightTime = s.LastSeenTime - s.LaunchTime;
                    if (VerboseLog)
                    {
                        string outcome = (s.LastDistM <= 500f) ? "HIT" : "ended";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] impact {s.AmmoName} -> {s.TargetName}: {outcome} at sim {s.LastSeenTime:0.0} " +
                            $"(flight {flightTime:0.0}s, final range {s.LastDistM:0} m)");
                    }
                }
                _flightTracker.Remove(w);
            }
        }

        // ---- Explicit fire from the planner panel ----

        internal struct Shot
        {
            public ObjectBase Unit;
            public string AmmoId;
            public int Salvo;
        }

        // ---- Live engagement overview (for the planner's status list) ----

        internal struct SalvoLine
        {
            public ObjectBase Target;
            public int Queued;
            public int InFlight;
            public float ImpactSim;
            public float ImpactSpread;
        }

        private static readonly Dictionary<ObjectBase, SalvoLine> _salvoMap =
            new Dictionary<ObjectBase, SalvoLine>();

        private static readonly Dictionary<ObjectBase, float> _firedAt = new Dictionary<ObjectBase, float>();
        private static readonly Dictionary<ObjectBase, float> _impactByTarget = new Dictionary<ObjectBase, float>();
        private static readonly Dictionary<ObjectBase, float> _impactSpreadByTarget = new Dictionary<ObjectBase, float>();
        private static readonly List<ObjectBase> _pruneScratch = new List<ObjectBase>();
        private const float EngageGrace = 8f;

        /// <summary>
        /// Snapshot of what we're currently coordinating, grouped by target: shots still held for
        /// timing (<see cref="SalvoLine.Queued"/>) and friendly missiles already in flight at that
        /// target (<see cref="SalvoLine.InFlight"/>). Reuses <paramref name="outList"/> to avoid
        /// per-frame allocation.
        /// </summary>
        internal static void CollectSalvos(List<SalvoLine> outList)
        {
            outList.Clear();
            _salvoMap.Clear();
            float now = GameTime.time;

            foreach (Scheduled s in _scheduled)
            {
                ObjectBase t = s.Item.Target;
                if (t == null || t.IsDestroyed) continue;
                _salvoMap.TryGetValue(t, out SalvoLine ln);
                ln.Target = t;
                ln.Queued += Mathf.Max(1, s.Item.Shots);
                _salvoMap[t] = ln;
            }

            foreach (KeyValuePair<WeaponBase, FlightSample> kv in _flightTracker)
            {
                WeaponBase w = kv.Key;
                if (w == null || w.IsDestroyed) continue;
                ObjectBase t = w.CurrentIntendedTargetObject;
                if (t == null || t.IsDestroyed) continue;
                if (!_firedAt.ContainsKey(t)) continue;
                _salvoMap.TryGetValue(t, out SalvoLine ln);
                ln.Target = t;
                ln.InFlight += 1;
                _salvoMap[t] = ln;
            }

            foreach (KeyValuePair<ObjectBase, SalvoLine> kv in _salvoMap)
            {
                SalvoLine ln = kv.Value;
                ln.ImpactSim = _impactByTarget.TryGetValue(kv.Key, out float imp) ? imp : -1f;
                ln.ImpactSpread = _impactSpreadByTarget.TryGetValue(kv.Key, out float spread) ? spread : 0f;
                outList.Add(ln);
            }

            _pruneScratch.Clear();
            foreach (KeyValuePair<ObjectBase, float> kv in _firedAt)
            {
                ObjectBase t = kv.Key;
                bool active = _salvoMap.TryGetValue(t, out SalvoLine ln) && (ln.Queued > 0 || ln.InFlight > 0);
                bool inGrace = (now - kv.Value) < EngageGrace;
                if (t == null || t.IsDestroyed || (!active && !inGrace))
                    _pruneScratch.Add(t);
            }
            foreach (ObjectBase t in _pruneScratch) { _firedAt.Remove(t); _impactByTarget.Remove(t); _impactSpreadByTarget.Remove(t); }
        }

        /// <summary>
        /// Fire a hand-picked set of missile shots at one target, staggered so they arrive together.
        /// Returns the longest flight time in the group (seconds), for UI feedback.
        /// </summary>
        internal static float FireCoordinated(System.Collections.Generic.List<Shot> shots, ObjectBase target)
        {
            if (shots == null || shots.Count == 0 || target == null) return 0f;

            bool multi = shots.Count > 1;
            var baseItems = new List<Intent>(shots.Count);
            foreach (Shot s in shots)
            {
                baseItems.Add(new Intent
                {
                    Unit = s.Unit,
                    AmmoId = s.AmmoId,
                    Target = target,
                    Shots = Mathf.Max(1, s.Salvo),
                    Priority = 1000,
                    IsFormation = multi,
                });
            }

            var expanded = new List<Intent>();
            foreach (Intent it in baseItems)
            {
                if (it.Shots > 1)
                {
                    float interval = GetLauncherInterval(it.Unit, it.AmmoId);
                    if (interval > 0f)
                    {
                        expanded.AddRange(SplitIntents(it, interval));
                        continue;
                    }
                }
                it.ImpactOffset = 0f;
                expanded.Add(it);
            }

            float maxEnroute = 0f;
            for (int i = 0; i < expanded.Count; i++)
            {
                float e = EstimateEnroute(expanded[i].Unit, expanded[i].AmmoId, target);
                float needed = e + expanded[i].ImpactOffset;
                if (needed > maxEnroute) maxEnroute = needed;
            }

            Schedule(expanded, GameTime.time + maxEnroute);

            Bootstrap.Log.LogInfo(
                $"[AutoTOT] planner firing {expanded.Count} shot(s) at {target.getUIDAndName()}: " +
                $"longest enroute {maxEnroute:0.0}s, impacts synced.");
            return maxEnroute;
        }

        private static float _lastReleaseSimNow = -1f;

        private static void ReleaseDueLaunches()
        {
            if (_scheduled.Count == 0) { _lastReleaseSimNow = GameTime.time; return; }

            float simNow = GameTime.time;

            float simStep = (_lastReleaseSimNow >= 0f) ? Mathf.Max(0f, simNow - _lastReleaseSimNow) : 0f;
            float lookahead = 0.5f * simStep;
            _lastReleaseSimNow = simNow;

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                Scheduled s = _scheduled[i];
                Intent it = s.Item;

                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                {
                    _scheduled.RemoveAt(i);
                    if (VerboseLog)
                    {
                        bool targetGone = it.Target == null || it.Target.IsDestroyed;
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] dropped held {it.AmmoId} from " +
                            $"{(it.Unit != null ? it.Unit.getUIDAndName() : "?")}: " +
                            $"{(targetGone ? "target already destroyed" : "shooter gone")} before release.");
                    }
                    continue;
                }

                float timeLeft = s.ImpactAtSim - simNow;
                float flightNow = EstimateEnroute(it.Unit, it.AmmoId, it.Target);
                if (timeLeft <= flightNow + lookahead)
                {
                    _scheduled.RemoveAt(i);
                    if (VerboseLog)
                    {
                        AmmunitionParameters ap = it.Unit.getAmmunitionByName(it.AmmoId)?._ap;
                        float overshoot = flightNow - timeLeft;
                        float kin = (ap != null) ? KinematicFlightTime(it.Unit, ap, it.Target) : -1f;
                        string src = (kin > 0.01f) ? "kinematic" : "straight-line fallback";
                        Bootstrap.Log.LogInfo(
                            $"[AutoTOT] launch {it.AmmoId} from {it.Unit.getUIDAndName()}: " +
                            $"est flight {flightNow:0.0}s ({src}), " +
                            $"impactAt {s.ImpactAtSim:0.0}, now {simNow:0.0}, " +
                            $"simStep {simStep:0.0}s, overshoot {overshoot:0.0}s");
                    }
                    Fire(it);
                }
            }
        }

        /// <summary>Fire one shot immediately, uncoordinated (used by the planner's "Fire now").</summary>
        internal static void FireNow(ObjectBase unit, string ammoId, ObjectBase target, int salvo)
        {
            if (unit == null || unit.IsDestroyed || target == null || target.IsDestroyed) return;
            InsertEngageTask_Patch.Bypass = true;
            try
            {
                unit.InsertEngageTask(ammoId, target, Vector3.zero, Mathf.Max(1, salvo), 1000,
                    autoAttack: false, markAsReturned: false, isFormationAttack: false);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] fire-now failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }
        }

        private static void Fire(Intent it)
        {
            ObjectBase unit = it.Unit;
            ObjectBase target = it.Target;

            if (unit == null || unit.IsDestroyed) return;
            if (target == null || target.IsDestroyed) return;

            InsertEngageTask_Patch.Bypass = true;
            try
            {
                unit.InsertEngageTask(it.AmmoId, target, Vector3.zero, it.Shots, it.Priority,
                    autoAttack: false, markAsReturned: false, isFormationAttack: it.IsFormation);
            }
            catch (System.Exception e)
            {
                Bootstrap.Log.LogError($"[AutoTOT] launch failed for {unit.getUIDAndName()}: {e}");
            }
            finally
            {
                InsertEngageTask_Patch.Bypass = false;
            }

            _firedAt[target] = GameTime.time;

            if (VerboseLog)
                Bootstrap.Log.LogInfo($"[AutoTOT] launched {unit.getUIDAndName()} -> {target.getUIDAndName()}");
        }
    }
}
