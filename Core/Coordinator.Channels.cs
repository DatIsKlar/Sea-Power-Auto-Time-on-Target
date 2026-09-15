using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// The budget of rounds a commit may order: guidance-channel reservations held from dispatch
    /// to launch, the channel and magazine checks that intake and commit run, and the launcher
    /// contention warning. Split out of Coordinator.Release.cs, which keeps the release timing.
    /// </summary>
    internal static partial class Coordinator
    {
        // Shooter+ammo pairs already handled by the current clamp pass, so each group is costed once.
        private static readonly HashSet<ShipAmmoKey> _clampSeen = new HashSet<ShipAmmoKey>();
        // Intents the clamp reduced to nothing, removed after the walk rather than during it.
        private static readonly List<Intent> _clampDropped = new List<Intent>();

        /// <summary>
        /// One dispatched order whose rounds have not all left the rail yet.
        ///
        /// The channel budget is read as "sensor channels minus weapons the sensor is guiding", and
        /// a weapon only joins that list when it LAUNCHES. An order that AutoTOT has handed to the
        /// game therefore occupies nothing measurable until its ripple runs, which on a submarine
        /// can be half a minute later. Between those two moments the rounds were invisible to both
        /// halves of the accounting, and a second order could be accepted against channels the first
        /// one was already going to need. Measured on 2026-09-09: a Prj.675 with a 4-channel
        /// Front_Door took a 4-round order and then a 2-round order 26 s later, and two of the four
        /// rounds that flew ended 2.4 km and 4.8 km short with the target still afloat.
        /// </summary>
        private sealed class Reservation
        {
            public ObjectBase Unit;
            public string AmmoId;      // the order's ammunition id, for the sensor-sharing lookup
            public string AmmoFile;    // AmmunitionParameters._ammunitionFileName, what a LAUNCHED
                                       // round reports; the two are not always the same string
            public int Shots;
            public int Launched;
            public float DispatchedSim;
            public float ExpirySim;    // when the unlaunched remainder stops being reserved
        }

        private static readonly List<Reservation> _reservations = new List<Reservation>();

        /// <summary>How long past its expected ripple a dispatched order keeps reserving the rounds
        /// that have not appeared. Long enough to cover a launcher working through a slow cycle,
        /// bounded so a dead order cannot hold a ship's channels for the rest of the mission.</summary>
        private const float ReservationGraceSim = 120f;

        /// <summary>
        /// Reserve the rounds of a dispatched order until they are seen leaving. Only
        /// channel-limited ammunition is tracked: everything that can form a missile group is
        /// bounded by the group instead, so reserving it would refuse orders for no reason.
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
        /// tracker's first-sighting path, which sees every round the game puts up, so this does not
        /// depend on the diagnostic expectation list and its verbose-only membership rule.
        /// </summary>
        internal static void CreditReservedLaunch(ObjectBase unit, string ammoFile)
        {
            if (unit == null || ammoFile == null) return;
            for (int i = 0; i < _reservations.Count; i++)
            {
                Reservation r = _reservations[i];
                if (r.Unit != unit || r.Launched >= r.Shots) continue;
                if (!string.Equals(r.AmmoFile, ammoFile, StringComparison.Ordinal) &&
                    !string.Equals(r.AmmoId, ammoFile, StringComparison.Ordinal)) continue;
                r.Launched++;
                if (r.Launched >= r.Shots) _reservations.RemoveAt(i);
                return;
            }
        }

        /// <summary>
        /// Drop reservations whose rounds have arrived, whose shooter is gone, or which have waited
        /// past their grace. Called once per tick.
        ///
        /// An expiry is worth a line: it means rounds were ordered, the channels were held for them,
        /// and they never flew. That is the shortfall case seen from the guidance side.
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

        /// <summary>
        /// Rounds of exactly this ammunition still owed by dispatched orders. The magazine question,
        /// as opposed to the channel question below.
        /// </summary>
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
        /// Rounds this ship has spoken for that are not yet occupying a channel: orders committed and
        /// not released, plus orders dispatched whose rounds have not all launched.
        ///
        /// Counted across every ammunition that SHARES a guiding sensor with
        /// <paramref name="ammoId"/>, because the budget belongs to the sensor rather than to the
        /// ammunition. Rounds already in the air are deliberately absent: they appear in the sensor's
        /// own weapon list, which the cap subtracts, and counting them here would charge them twice.
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
            // Orders that are committed but still waiting on their flight estimates. They are not
            // in _scheduled yet and they are no longer in a batch, so without this they are
            // invisible to the channel budget for the length of the pending window and a second
            // commit can hand the same channels out twice.
            n += PendingGuidedRounds(unit, ammoId);
            return n;
        }

        /// <summary>
        /// Rounds of <paramref name="ammoId"/> this ship could still guide if an order arrived now:
        /// its channel budget less what is already committed and unfired, less what
        /// <paramref name="batch"/> is already holding for it. int.MaxValue when the ammunition is
        /// not channel-limited, which is everything that can form a missile group.
        /// </summary>
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
        /// Every round of <paramref name="ammoId"/> this ship is already spoken for by the
        /// coordinator: committed and unfired, plus anything in the armed strike batch or in an open
        /// per-target batch. A batch is either the strike batch or in _openBatches, never both, so
        /// these three pools do not overlap.
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
        /// Rounds the PLANNER PANEL has staged but not yet handed over, supplied by the Hud because
        /// the coordinator cannot see them: a staged pick lives in the panel until FireStrike.
        ///
        /// <para>Without this the two halves of the budget were blind to each other. Orders taken in
        /// through Alt+H did not stop the panel offering more, and picks staged in the panel did not
        /// stop Alt+H accepting more, so either order of operations could exceed the channels the
        /// other half was carefully protecting.</para>
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
                // A UI fault must never block firing, so the count falls back to 0. Said once,
                // because a 0 here lets the budget ignore every staged pick.
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
        /// Rounds of <paramref name="ammoId"/> this ship could still guide if an order arrived now.
        /// int.MaxValue when the ammunition is not channel-limited, which is everything that can
        /// form a missile group.
        /// </summary>
        private static int GuidanceRoom(ObjectBase unit, string ammoId)
        {
            int cap = LauncherFactsSource.GuidanceChannelCap(unit, ammoId);
            if (cap == int.MaxValue) return int.MaxValue;
            return Mathf.Max(0, cap - HeldRounds(unit, ammoId) - StagedRounds(unit, ammoId));
        }

        /// <summary>
        /// Flag the shooters an armed batch has already pushed past their guidance-channel budget,
        /// WITHOUT trimming anything. Called as each order is collected, so the indicator and the
        /// panel can say a trim is coming while the strike is still being built and the player can
        /// still do something about it.
        ///
        /// <para>The clamp itself cannot serve this purpose: it runs at commit, which is the same
        /// instant the rounds leave. A player with the panel collapsed then learns what happened
        /// only from a log line, after the fact.</para>
        /// </summary>
        internal static void MarkChannelPressure(List<Intent> items)
        {
            _channelCappedShooters.Clear();
            _clampSeen.Clear();
            // Every armed batch, not only the one that just took an order. Clearing every mark and
            // re-marking one batch let an order at one target put out the light for another.
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

                // HeldRounds already covers `items`, since the caller's list is one of the batches
                // it walks. Adding the list again here would double-count it.
                int requested = HeldRounds(lead.Unit, lead.AmmoId) + StagedRounds(lead.Unit, lead.AmmoId);
                // >= not >: the point of the light is "this ship's channels are spoken for", and a
                // ship holding exactly its cap is the case the player most needs to see, since the
                // next order they try will be refused outright. Requiring a strict overrun made it
                // unreachable on the intercept path, which now refuses the overrun before it can be
                // counted at all.
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
        /// Last check before a strike is scheduled: is every order still one the game would accept,
        /// and does the magazine actually hold the rounds it promises?
        ///
        /// A strike can be staged over minutes. In that time a shooter or a target can die, a
        /// launcher can be destroyed, and rounds can be spent by an earlier order or by the ship's
        /// own automatic fire. Commit used to check none of it: it trimmed guidance channels and
        /// scheduled whatever was left, so an order could be timed, held, and released against a
        /// magazine that had emptied.
        ///
        /// Runs BEFORE <see cref="ClampToGuidanceChannels"/> and before PrepareIntent, because both
        /// derive from the shot counts and from which orders survive. Every reduction is stated: a
        /// strike that quietly shrinks is worse than one that says what it lost.
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

                // The near bound, re-tested here rather than trusted from staging time. The panel
                // refuses to stage inside it and the intake refuses to collect inside it, but a
                // strike can be built minutes before it is fired and a shooter closing on its
                // target crosses the minimum without anything else noticing. Same treatment as a
                // shooter that can no longer engage at all, because the outcome is the same: no
                // round leaves.
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

            // Magazine, aggregated per shooter and ammunition across the WHOLE commit and against
            // what the ship already owes. Trimmed in list order, so the targets staged first keep
            // their rounds, which is the same rule the channel clamp uses.
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
        /// Trim every shooter's salvo to the number of rounds its guiding sensor can actually
        /// control, across the WHOLE commit rather than one target at a time.
        ///
        /// <para><b>Why this clamps instead of warning.</b> The surplus rounds are not held back by
        /// the game: they launch, fail to win a channel in Missile.CheckRadioConnection, set
        /// ConnectionLost and are logged "no weapon channel". A radio-command round in that state is
        /// refused its run-in by Missile.RunInPermitted, so it flies on and self-destructs. The
        /// magazine is spent either way, so ordering them can only waste rounds, never deliver
        /// them.</para>
        ///
        /// <para><b>Why here.</b> This is the only point that sees the whole wave, and it covers the
        /// planner path and the intercepted-order path alike. The panel's own per-row cap cannot do
        /// it: it is applied per target, and orders issued through the game's own interface never
        /// reach the panel at all. It must also run BEFORE PrepareIntent, because anchor selection
        /// and the shared impact time are computed from the shot counts.</para>
        ///
        /// <para>Only ungroupable launcher-guided ammo is capped at all (see
        /// LauncherFactsSource.ComputeGuidance); everything that can form a missile group shares one
        /// channel and is bounded by the group instead.</para>
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
                // One pass per CHANNEL POOL, not per ammunition. Two radio-command types served by
                // one radar draw on one set of channels, and budgeting them separately handed the
                // same pool out twice. An earlier item in this same commit that shares the pool has
                // already trimmed everything in it, this one included.
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

                // D3, commit side. The intake line only covers orders issued through the game's own
                // interface; everything staged in the planner arrives here instead, which is the
                // path the 2026-09-09 over-commit came in on.
                if (VerboseLog)
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] channels-commit {lead.AmmoId} from {UnitNaming.SafeName(lead.Unit)}: " +
                        $"cap {cap}, committed-unlaunched {committed}, budget {budget}, requested " +
                        $"{requested} | live {LauncherFactsSource.GuidanceOccupancyText(lead.Unit, lead.AmmoId)}");

                if (requested <= budget) { _channelCapWarned.Remove(key); continue; }

                // Trim in list order, so the targets staged first keep their full salvo and the ones
                // added last absorb the cut. Deterministic, and it matches the order the player
                // built the strike in.
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
        /// Warn when a shooter is being committed to more targets than its launcher can service at
        /// once. The game queues every engage task (ObjectBase.InsertEngageTask appends and re-sorts;
        /// nothing is replaced), but HandleEngageTasks will only execute a task whose weapon system
        /// is actually free, so a launcher already engaging one target cannot start another. On a
        /// non-per-container launcher, a box mount, those orders therefore leave SERIALLY and their
        /// impacts cannot be synchronised no matter what this mod schedules.
        ///
        /// Diagnostic only. The shot the player asked for is still fired.
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

                // The HUD marks these rows. Recorded before the once-per-shooter log gate below, so
                // the panel keeps showing the condition after the log has said its piece.
                _contendedShooters.Add(key);

                // Once per shooter and ammo while the contention lasts; re-warning as the player adds
                // targets says nothing new.
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
