using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The rounds a commit may order: guidance-channel reservations from dispatch to launch, the channel
    /// and magazine checks at intake and commit, and the launcher contention warning.
    /// </summary>
    internal static partial class Coordinator
    {
        // Shooter+ammo pairs already handled by the current clamp pass, so each group is costed once.
        private static readonly HashSet<ShipAmmoKey> _clampSeen = new HashSet<ShipAmmoKey>();
        // Intents the clamp reduced to nothing, removed after the walk rather than during it.
        private static readonly List<Intent> _clampDropped = new List<Intent>();

        /// <summary>
        /// A dispatched order whose rounds have not all launched. A sensor's used channels only count weapons
        /// in flight, so without this a second order could take channels the first order's rounds need.
        /// </summary>
        private sealed class Reservation
        {
            public ObjectBase Unit;
            public ObjectBase Target;  // the order's target: only a round aimed at it is this order's
            public string AmmoId;      // the order's ammunition id, for the sensor-sharing lookup
            // AmmunitionParameters._ammunitionFileName, what a launched round reports; can differ from the ammo id.
            public string AmmoFile;
            public int Shots;
            public int Launched;
            public float DispatchedSim;
            public float ExpirySim;    // when the unlaunched remainder stops being reserved
        }

        private static readonly List<Reservation> _reservations = new List<Reservation>();

        /// <summary>
        /// How long past its expected ripple a dispatched order keeps its reservation: enough for a slow
        /// launcher, bounded so a dead order cannot hold channels all mission.
        /// </summary>
        private const float ReservationGraceSim = 120f;

        /// <summary>
        /// Reserve a dispatched order's rounds until they are seen launching. Channel-limited ammunition
        /// only; grouping ammunition is bounded by its group.
        /// </summary>
        private static void ReserveDispatched(Intent it, float simNow)
        {
            if (it?.Unit == null || it.AmmoId == null) return;
            if (LauncherFactsSource.GuidanceChannelCap(it.Unit, it.AmmoId) == int.MaxValue) return;

            int shots = Mathf.Max(1, it.Shots);
            LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
            float interval = f.ShotIntervalOrFallback;
            float ripple = (shots - 1) * interval + Mathf.Max(0, it.Waves - 1) * (f.Valid ? f.ReloadGap : 0f);

            string ammoFile = it.Unit.getAmmunitionByName(it.AmmoId)?._ap?._ammunitionFileName;

            _reservations.Add(new Reservation
            {
                Unit = it.Unit,
                Target = it.Target,
                AmmoId = it.AmmoId,
                AmmoFile = string.IsNullOrEmpty(ammoFile) ? it.AmmoId : ammoFile,
                Shots = shots,
                Launched = 0,
                DispatchedSim = simNow,
                ExpirySim = simNow + it.StartupLead + ripple + ReservationGraceSim,
            });
        }

        /// <summary>
        /// Credit one observed launch against the oldest matching reservation. Called from the launch
        /// tracker's first sighting, which sees every round. Matched on the target as well as the ship and
        /// ammunition: a round of the same weapon fired at something else (FIRE NOW, an order given with
        /// collection off, the AI) is not this order's, and crediting it would free a channel the order's own
        /// round still needs.
        /// </summary>
        internal static void CreditReservedLaunch(ObjectBase unit, string ammoFile, ObjectBase target)
        {
            if (unit == null || ammoFile == null) return;
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit || r.Launched >= r.Shots) continue;
                if (r.Target != null && target != null && r.Target != target) continue;
                if (!string.Equals(r.AmmoFile, ammoFile, StringComparison.Ordinal) &&
                    !string.Equals(r.AmmoId, ammoFile, StringComparison.Ordinal)) continue;
                r.Launched++;
                if (r.Launched >= r.Shots) _reservations.RemoveAt(i);
                return;
            }
        }

        /// <summary>
        /// Drop reservations whose rounds arrived, whose shooter is gone, or which outlived their grace. An
        /// expiry is logged: rounds were ordered and never flew.
        /// </summary>
        private static void PruneReservations(float simNow)
        {
            for (int i = _reservations.Count - 1; i >= 0; i--)
            {
                Reservation r = _reservations[i];
                if (r.Unit == null || r.Unit.IsDestroyed) { _reservations.RemoveAt(i); continue; }
                if (simNow < r.ExpirySim) continue;

                _reservations.RemoveAt(i);
                if (VerboseLog && r.Launched < r.Shots)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] reservation-expired {r.AmmoId} from {UnitNaming.SafeName(r.Unit)}: " +
                        $"{r.Shots - r.Launched} of {r.Shots} round(s) never left in " +
                        $"{simNow - r.DispatchedSim:0.0}s; their guidance channels are free again.");
            }
        }

        /// <summary>Rounds of exactly this ammunition still owed by dispatched orders (the magazine
        /// question).</summary>
        private static int ReservedRounds(ObjectBase unit, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit) continue;
                if (!string.Equals(r.AmmoId, ammoId, StringComparison.Ordinal)) continue;
                n += Mathf.Max(0, r.Shots - r.Launched);
            }
            return n;
        }

        /// <summary>
        /// Rounds this ship has spoken for that do not yet occupy a channel: committed but unreleased, plus
        /// dispatched but unlaunched. Counted across every ammunition sharing <paramref name="ammoId"/>'s
        /// guiding sensor; rounds in flight are left out because the cap already subtracts them.
        /// </summary>
        private static int CommittedUnfired(ObjectBase unit, string ammoId)
        {
            int n = 0;
            for (int i = 0; i < _scheduled.Count; i++)
            {
                Scheduled s = _scheduled[i];
                if (s == null || s.Fired || s.Item == null) continue;
                if (s.Item.Unit == unit &&
                    LauncherFactsSource.SharesGuidanceSensor(unit, s.Item.AmmoId, ammoId))
                    n += Mathf.Max(1, s.Item.Shots);
            }
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit) continue;
                if (!LauncherFactsSource.SharesGuidanceSensor(unit, r.AmmoId, ammoId)) continue;
                n += Mathf.Max(0, r.Shots - r.Launched);
            }
            // Committed orders still waiting on their estimates are in neither _scheduled nor a batch.
            n += PendingGuidedRounds(unit, ammoId);
            return n;
        }

        /// <summary>Rounds of <paramref name="ammoId"/> this ship holds in batch <paramref name="b"/>.</summary>
        private static int BatchRounds(Batch b, ObjectBase unit, string ammoId)
        {
            int n = 0;
            if (b == null) return 0;
            for (int i = 0; i < b.Items.Count; i++)
            {
                Intent it = b.Items[i];
                if (it != null && it.Unit == unit &&
                    LauncherFactsSource.SharesGuidanceSensor(unit, it.AmmoId, ammoId))
                    n += Mathf.Max(1, it.Shots);
            }
            return n;
        }

        /// <summary>
        /// Every round of <paramref name="ammoId"/> the coordinator holds for this ship: committed and
        /// unfired, plus the armed strike and every open batch, which never overlap.
        /// </summary>
        internal static int HeldRounds(ObjectBase unit, string ammoId)
        {
            int n = CommittedUnfired(unit, ammoId);
            n += BatchRounds(_strikeBatch, unit, ammoId);
            foreach (KeyValuePair<ObjectBase, Batch> kv in _openBatches)
                n += BatchRounds(kv.Value, unit, ammoId);
            return n;
        }

        /// <summary>
        /// Rounds staged in the planner panel but not yet fired, supplied by the Hud, so the intake and the
        /// panel cannot hand out channels the other has promised.
        /// </summary>
        internal static System.Func<ObjectBase, string, int> StagedRoundsProvider;
        private static bool _stagedRoundsFaultLogged;

        private static int StagedRounds(ObjectBase unit, string ammoId)
        {
            System.Func<ObjectBase, string, int> f = StagedRoundsProvider;
            if (f == null) return 0;
            try { return f(unit, ammoId); }
            catch (System.Exception e)
            {
                // A UI fault must never block firing, so fall back to 0, and say so once.
                if (!_stagedRoundsFaultLogged)
                {
                    _stagedRoundsFaultLogged = true;
                    ModLog.Warn("planner staged-round count failed, so guidance-channel budgets ignore " +
                                "staged picks for the rest of the session", e);
                }
                return 0;
            }
        }

        /// <summary>
        /// Rounds of <paramref name="ammoId"/> this ship could still guide if an order arrived now;
        /// int.MaxValue when the ammunition is not channel-limited.
        /// </summary>
        private static int GuidanceRoom(ObjectBase unit, string ammoId)
        {
            int cap = LauncherFactsSource.GuidanceChannelCap(unit, ammoId);
            if (cap == int.MaxValue) return int.MaxValue;
            return Mathf.Max(0, cap - HeldRounds(unit, ammoId) - StagedRounds(unit, ammoId));
        }

        /// <summary>
        /// Flag shooters whose armed orders already fill their guidance channels, without trimming, so the
        /// panel can warn while the strike is still being built. The commit clamp comes too late for that.
        /// </summary>
        internal static void MarkChannelPressure(List<Intent> items)
        {
            _channelCappedShooters.Clear();
            _clampSeen.Clear();
            // Every armed batch, so an order at one target does not clear the warning for another.
            MarkPressureFrom(_strikeBatch?.Items);
            foreach (KeyValuePair<ObjectBase, Batch> kv in _openBatches)
                MarkPressureFrom(kv.Value.Items);
            MarkPressureFrom(items);
        }

        private static void MarkPressureFrom(List<Intent> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                Intent lead = items[i];
                if (lead?.Unit == null || lead.AmmoId == null) continue;
                var key = new ShipAmmoKey(lead.Unit, lead.AmmoId);
                if (!_clampSeen.Add(key)) continue;

                int cap = LauncherFactsSource.GuidanceChannelCap(lead.Unit, lead.AmmoId);
                if (cap == int.MaxValue) continue;

                // HeldRounds already includes `items`.
                int requested = HeldRounds(lead.Unit, lead.AmmoId) + StagedRounds(lead.Unit, lead.AmmoId);
                // >=, not >: a ship at exactly its cap is the case to show, because its next order will be refused.
                if (requested >= cap) _channelCappedShooters.Add(key);
            }
        }

        /// <summary>
        /// True when an earlier item in this commit shares <paramref name="lead"/>'s guiding sensor
        /// on the same ship, so its pass already covered these rounds.
        /// </summary>
        private static bool PoolAlreadyClamped(List<Intent> items, int index, Intent lead)
        {
            for (int j = 0; j < index; j++)
            {
                Intent prior = items[j];
                if (prior?.Unit != lead.Unit || prior.AmmoId == null) continue;
                if (string.Equals(prior.AmmoId, lead.AmmoId, StringComparison.Ordinal)) continue;
                if (LauncherFactsSource.SharesGuidanceSensor(lead.Unit, prior.AmmoId, lead.AmmoId))
                    return true;
            }
            return false;
        }

        // Scratch for the commit revalidation, reused so a commit allocates nothing.
        private static readonly List<Intent> _revalDropped = new List<Intent>();
        private static readonly Dictionary<ShipAmmoKey, int> _revalBudget = new Dictionary<ShipAmmoKey, int>();

        /// <summary>
        /// Last check before scheduling: is every order still one the game would accept, and does the
        /// magazine hold its rounds? A strike can be staged for minutes while shooters, targets and rounds
        /// are lost. Runs before <see cref="ClampToGuidanceChannels"/> and PrepareIntent; every reduction is logged.
        /// </summary>
        private static void RevalidateCommit(List<Intent> items)
        {
            if (items == null || items.Count == 0) return;
            _revalDropped.Clear();
            _revalBudget.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it == null) { continue; }

                if (it.Unit == null || it.Unit.IsDestroyed || it.Target == null || it.Target.IsDestroyed)
                {
                    _revalDropped.Add(it);
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] order dropped at commit: {it.AmmoId} from " +
                        $"{UnitNaming.SafeName(it.Unit)} -> {UnitNaming.SafeName(it.Target)}, " +
                        $"{(it.Unit == null || it.Unit.IsDestroyed ? "the shooter" : "the target")} " +
                        $"is gone. The rest of the strike is unaffected.");
                    continue;
                }

                Ammunition ammo = it.Unit.getAmmunitionByName(it.AmmoId);
                if (ammo?._ap == null || !it.Unit.DoesAmmoMatchTarget(ammo._ap, it.Target, out _))
                {
                    _revalDropped.Add(it);
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] order dropped at commit: {UnitNaming.SafeName(it.Unit)} can no longer " +
                        $"engage {UnitNaming.SafeName(it.Target)} with {it.AmmoId}.");
                    continue;
                }

                // Minimum range re-tested at commit: a shooter closing on its target can cross it after
                // staging, and the game would then never fire.
                if (LaunchEnvelope.IsInsideMinimumRange(it.Unit, ammo._ap, it.Target,
                                                        out float closeNm, out float minNm))
                {
                    _revalDropped.Add(it);
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] order dropped at commit: {UnitNaming.SafeName(it.Unit)} has closed to " +
                        $"{closeNm:0.0}nm from {UnitNaming.SafeName(it.Target)}, inside the {minNm:0.0}nm " +
                        $"minimum launch range for {it.AmmoId}. The game would hold the order without " +
                        $"launching. The rest of the strike is unaffected.");
                }
            }
            for (int i = 0; i < _revalDropped.Count; i++) items.Remove(_revalDropped[i]);
            _revalDropped.Clear();
            if (items.Count == 0) return;

            // Magazine per shooter and ammunition, across the whole commit and what the ship already owes;
            // trimmed in list order like the channel clamp.
            for (int i = 0; i < items.Count; i++)
            {
                Intent it = items[i];
                if (it == null) continue;
                var key = new ShipAmmoKey(it.Unit, it.AmmoId);
                if (!_revalBudget.TryGetValue(key, out int left))
                {
                    left = Mathf.Max(0, LauncherFactsSource.AvailableRounds(it.Unit, it.AmmoId)
                                        - OutstandingRounds(it.Unit, it.AmmoId));
                }

                int want = Mathf.Max(1, it.Shots);
                int give = Mathf.Min(want, left);
                _revalBudget[key] = left - give;
                if (give == want) continue;

                if (give <= 0)
                {
                    _revalDropped.Add(it);
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] order dropped at commit: {UnitNaming.SafeName(it.Unit)} has no " +
                        $"{it.AmmoId} left for {UnitNaming.SafeName(it.Target)}; the rounds went to the " +
                        $"targets staged before it.");
                }
                else
                {
                    it.Shots = give;
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] order reduced at commit: {UnitNaming.SafeName(it.Unit)} can supply " +
                        $"{give} of {want} {it.AmmoId} for {UnitNaming.SafeName(it.Target)}. The strike " +
                        $"is timed on what will actually fly.");
                }
            }
            for (int i = 0; i < _revalDropped.Count; i++) items.Remove(_revalDropped[i]);
            _revalDropped.Clear();
        }

        /// <summary>
        /// Trim every shooter's salvo to the rounds its guiding sensor can control, across the whole commit.
        /// Surplus radio-command rounds would launch, find no channel and self-destruct, so ordering them
        /// only wastes the magazine. Done here because it is the one point that sees every order from every
        /// path, before PrepareIntent uses the counts. Only ungroupable launcher-guided ammunition is capped.
        /// </summary>
        private static void ClampToGuidanceChannels(List<Intent> items)
        {
            _channelCappedShooters.Clear();
            _channelTrimmedShooters.Clear();
            if (items == null || items.Count == 0) return;

            _clampSeen.Clear();
            _clampDropped.Clear();

            // Indexed, not foreach: the group walk below reads the same list.
            for (int i = 0; i < items.Count; i++)
            {
                Intent lead = items[i];
                if (lead?.Unit == null || lead.AmmoId == null) continue;
                var key = new ShipAmmoKey(lead.Unit, lead.AmmoId);
                if (!_clampSeen.Add(key)) continue;
                // One pass per channel pool: two ammunition types on one radar share its channels.
                if (PoolAlreadyClamped(items, i, lead)) continue;

                int cap = LauncherFactsSource.GuidanceChannelCap(lead.Unit, lead.AmmoId);
                if (cap == int.MaxValue) { _channelCapWarned.Remove(key); continue; }

                int committed = CommittedUnfired(lead.Unit, lead.AmmoId);

                int budget = Mathf.Max(0, cap - committed);
                int requested = 0;
                for (int j = 0; j < items.Count; j++)
                {
                    Intent it = items[j];
                    if (it != null && it.Unit == lead.Unit &&
                        LauncherFactsSource.SharesGuidanceSensor(lead.Unit, it.AmmoId, lead.AmmoId))
                        requested += Mathf.Max(1, it.Shots);
                }

                // Commit side of the channel check; planner-staged orders arrive here rather than at intake.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] channels-commit {lead.AmmoId} from {UnitNaming.SafeName(lead.Unit)}: " +
                        $"cap {cap}, committed-unlaunched {committed}, budget {budget}, requested " +
                        $"{requested} | live {LauncherFactsSource.GuidanceOccupancyText(lead.Unit, lead.AmmoId)}");

                if (requested <= budget) { _channelCapWarned.Remove(key); continue; }

                // Trim in list order: the targets staged first keep their full salvo.
                int left = budget;
                for (int j = 0; j < items.Count; j++)
                {
                    Intent it = items[j];
                    if (it == null || it.Unit != lead.Unit ||
                        !LauncherFactsSource.SharesGuidanceSensor(lead.Unit, it.AmmoId, lead.AmmoId))
                        continue;
                    int give = Mathf.Min(Mathf.Max(1, it.Shots), left);
                    it.Shots = give;
                    left -= give;
                    if (give <= 0) _clampDropped.Add(it);
                }

                _channelCappedShooters.Add(key);
                _channelTrimmedShooters.Add(key);
                if (_channelCapWarned.Add(key))
                    Bootstrap.Log.LogWarning(
                        $"[AutoTOT] guidance channels: {UnitNaming.SafeName(lead.Unit)} can control " +
                        $"{cap} {lead.AmmoId} round(s) at once" +
                        (committed > 0 ? $" and has {committed} already committed" : "") +
                        $", but {requested} were ordered. Trimmed to {budget}; the targets added " +
                        $"last lose their rounds. Ordering more would not deliver them: the extra " +
                        $"rounds launch, lose guidance for want of a channel and self-destruct. " +
                        $"Spread the shots across more shooters.");
            }

            for (int i = 0; i < _clampDropped.Count; i++) items.Remove(_clampDropped[i]);
            _clampDropped.Clear();
        }

        /// <summary>
        /// Warn when a shooter is committed to more targets than its launcher can service at once: the game
        /// queues every engage task but starts one only on a free weapon system, so a box launcher fires them
        /// serially and their impacts cannot be synchronised. The shots still fire.
        /// </summary>
        private static void WarnOnLauncherContention(List<Intent> items)
        {
            _contendedShooters.Clear();
            foreach (Intent it in items)
            {
                if (it.Unit == null) continue;

                // Orders already open on this shooter for this ammo, excluding the ones just added.
                int open = 0;
                for (int i = 0; i < _scheduled.Count; i++)
                {
                    Intent other = _scheduled[i].Item;
                    if (other == null || other == it) continue;
                    if (other.Unit == it.Unit && other.AmmoId == it.AmmoId && other.Target != it.Target)
                        open++;
                }
                var key = new ShipAmmoKey(it.Unit, it.AmmoId);
                if (open == 0) { _contentionWarned.Remove(key); continue; }

                LauncherFactsSource.Facts f = LauncherFactsSource.Get(it.Unit, it.AmmoId);
                bool parallel = f.Valid && f.PerContainer;
                if (parallel) continue;   // VLS cells cycle independently; no serialisation to warn about

                // Recorded before the once-per-shooter log gate, so the panel keeps showing it.
                _contendedShooters.Add(key);

                // Warned once per shooter and ammo while the contention lasts.
                if (_contentionWarned.Contains(key)) continue;
                _contentionWarned.Add(key);

                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] launcher contention: {UnitNaming.SafeName(it.Unit)} now has {open + 1} open " +
                    $"{it.AmmoId} order(s) at different targets on a non-parallel launcher. The game " +
                    $"services these one at a time, so rounds will leave serially and time-on-target " +
                    $"across these targets will not hold. Spread the shots across more shooters.");
            }
        }
    }
}
