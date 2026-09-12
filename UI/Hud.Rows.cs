using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) ; row data: which ships can shoot, the missile rows each offers with stock,
    /// range and guidance channels applied, the per-frame caches behind them, the shared-group
    /// check, and the selection prune.
    /// </summary>
    internal sealed partial class Hud
    {
        // The slower per-row questions a drawn row asks the game, answered once per frame. OnGUI
        // makes two to four passes a frame and every pass used to ask again; the launch-envelope one
        // integrates a submarine's ascent or an aircraft's descent step by step. Answering once also
        // keeps the Layout and Repaint passes agreeing on which warning lines exist. Keyed like
        // _checked and pruned with it; an entry is reused across frames, so a row allocates once.
        private sealed class RowFacts
        {
            internal int Frame = -1;
            internal bool HaveChannel, HaveChecked;
            internal bool ChannelTrimmed, ChannelCapped;
            internal bool Manoeuvres, Contended, SensorUnavailable;
            internal float WindowDeg, EnvelopeDelay;
        }

        private readonly Dictionary<string, RowFacts> _rowFacts = new Dictionary<string, RowFacts>();

        /// <summary>
        /// A shared-missile-group finding for one row: several checked shooters are close enough to
        /// feed ONE group, and between them they have staged more rounds than that group can hold.
        /// </summary>
        private struct GroupShare
        {
            public int Combined;    // rounds staged across every shooter sharing this group
            public int Cap;         // ap._maxGroupSize
            public float JoinNm;    // ap._groupJoinRangeUnity, in nautical miles
            public int Shooters;    // how many shooters are inside that radius, this one included
        }

        // Rebuilt once per drawn frame, keyed like _checked/_salvo. Cross-shooter by nature, so it
        // cannot be computed from inside a single row. Frame-gated like _rowCache: this pass is
        // O(shooters^2) and OnGUI fires several events per frame.
        private readonly Dictionary<string, GroupShare> _groupShare = new Dictionary<string, GroupShare>();

        private int _groupShareFrame = -1;

        private List<ObjectBase> GetShooters()
        {
            _shootersCache.Clear();

            // A group, once assembled, IS the shooter list. Members are listed even when they carry
            // nothing that can engage the current target: the per-ship "no missiles that can engage
            // this target" line is the answer the player needs, and silently dropping the ship
            // instead looks like the group forgot it.
            if (_group.Count > 0)
            {
                for (int i = 0; i < _group.Count; i++)
                {
                    ObjectBase u = _group[i];
                    if (u != null && !u.IsDestroyed && !_shootersCache.Contains(u)) _shootersCache.Add(u);
                }
                return _shootersCache;
            }

            if (_anchor == null || _anchor.IsDestroyed) return _shootersCache;

            // No roster means "follow the selection", and the selection is one ship. Shooting with
            // more than one hull goes through the roster, which is the only place the panel keeps
            // a shooter list; there is no second, invisible way to assemble one.
            if (IsMissileShip(_anchor)) _shootersCache.Add(_anchor);
            return _shootersCache;
        }

        /// <summary>
        /// Finds rows whose shooters will share one missile group and have staged more rounds than
        /// the group can hold. Rebuilt once per drawn frame, before the row loop.
        ///
        /// Grouped ammo (GroupSize > 1) does not consume a fire-control channel per round: the first
        /// round away forms a group and holds one channel, and every later round that JOINS that
        /// group skips the channel check entirely. One ship therefore empties its magazine through a
        /// single channel, which is why a lone Slava fires all 16 SS-N-12.
        ///
        /// The trap is a second shooter within the ammo's GroupJoinRange. Its rounds join the FIRST
        /// ship's group instead of forming their own, so it never engages its own channels; the
        /// shared group fills at GroupSize, nothing more can join, and neither ship has a spare
        /// channel left. Both launchers then sit in the game's ReadyUpWhileWaiting with loaded tubes.
        /// Measured: two Slavas 8 or 13 nm apart put up 16 rounds between them, the same pair at 25
        /// or 40 nm put up all 32. Beyond the join range each ship forms its own group and is fine.
        ///
        /// The game measures group leader to firing mount, not ship to ship. Ship separation is the
        /// proxy used here because the leader starts at its shooter, and at decision time (before
        /// anything has launched) there is no leader to measure against.
        /// </summary>
        private void RecomputeGroupSharing(List<ObjectBase> shooters)
        {
            if (_groupShareFrame == Time.frameCount) return;
            _groupShareFrame = Time.frameCount;
            _groupShare.Clear();
            if (_target == null || _target.IsDestroyed || shooters.Count < 2) return;

            for (int i = 0; i < shooters.Count; i++)
            {
                ObjectBase a = shooters[i];
                if (a == null || a.IsDestroyed) continue;

                foreach (Row r in CachedEngageRows(a))
                {
                    if (!r.InRange) continue;
                    string key = Key(a, r.AmmoId);
                    if (!_checked.TryGetValue(key, out bool on) || !on) continue;

                    LauncherFactsSource.Facts f = LauncherFactsSource.Get(a, r.AmmoId);
                    if (!f.Valid || !f.CanGroup || f.MaxGroupSize <= 1 || f.GroupJoinRangeU <= 0f) continue;

                    // The game groups on _ammunitionFileName, not on the ammo id, and the two are
                    // not interchangeable elsewhere in this codebase. Resolve both sides.
                    string ammoFile = a.getAmmunitionByName(r.AmmoId)?._ap?._ammunitionFileName;
                    if (string.IsNullOrEmpty(ammoFile)) continue;

                    int combined = _salvo.TryGetValue(key, out int mine) ? mine : 1;
                    int peers = 1;
                    float joinSq = f.GroupJoinRangeU * f.GroupJoinRangeU;
                    for (int j = 0; j < shooters.Count; j++)
                    {
                        if (j == i) continue;
                        ObjectBase b = shooters[j];
                        if (b == null || b.IsDestroyed) continue;
                        if ((b.transform.position - a.transform.position).sqrMagnitude > joinSq) continue;

                        foreach (Row br in CachedEngageRows(b))
                        {
                            if (!br.InRange) continue;
                            if (b.getAmmunitionByName(br.AmmoId)?._ap?._ammunitionFileName != ammoFile) continue;
                            string bKey = Key(b, br.AmmoId);
                            if (!_checked.TryGetValue(bKey, out bool bOn) || !bOn) continue;
                            int bSalvo = _salvo.TryGetValue(bKey, out int sv) ? sv : 1;
                            if (bSalvo <= 0) continue;
                            combined += bSalvo;
                            peers++;
                        }
                    }

                    if (peers > 1 && combined > f.MaxGroupSize)
                        _groupShare[key] = new GroupShare
                        {
                            Combined = combined,
                            Cap = f.MaxGroupSize,
                            JoinNm = f.GroupJoinRangeU * GameUnits.UnityToNm,
                            Shooters = peers,
                        };
                }
            }
        }

        private struct Row
        {
            public string AmmoId;
            public int Count;          // rounds selectable here: stock, launcher and channels applied
            public bool InRange;
            // Which of the two bounds an unselectable row failed. The row states it as a
            // comparison against the live range ("312.0nm > max range", "12.4nm < min range"), so
            // the near and far cases read the same way round.
            public bool TooClose;      // inside the weapon's minimum, rather than beyond its maximum
            // Guidance-channel state, carried so the row can EXPLAIN a count of zero instead of
            // vanishing. int.MaxValue = this ammunition is not channel-limited at all.
            public int ChannelCap;
            public int ChannelsElsewhere;   // rounds of it already staged at other targets
        }

        private bool IsMissileShip(ObjectBase u)
        {
            if (u == null || u.IsDestroyed || !u.isUnit() || !u.IsPlayerObject) return false;
            foreach (Row r in CachedEngageRows(u))
                return true;
            return false;
        }

        // Cached wrapper around EngageRows ; materialises the IEnumerable to a list once per
        // ship per frame, then returns the same list for all callers within that frame.
        private List<Row> CachedEngageRows(ObjectBase ship)
        {
            int id = ship.GetInstanceID();
            if (_rowCache.TryGetValue(id, out List<Row> cached)) return cached;
            var list = new List<Row>();
            try
            {
                foreach (Row r in EngageRows(ship)) list.Add(r);
            }
            catch (System.Exception e)
            {
                // One unreadable ship used to take the whole panel down through DrawWindow, and the
                // stack that came out named neither the ship nor the ammo. Contain it here: the ship
                // lists as carrying nothing, everything else still draws, and the log says which hull
                // and how many rows it managed before it threw.
                list.Clear();
                if (_rowErrorLogged.Add(id))
                    Bootstrap.Log.LogError(
                        $"[AutoTOT] engage rows failed for {UnitNaming.SafeName(ship)}; listing it as carrying " +
                        $"nothing. This ship is excluded until the mission ends.\n{e}");
            }
            _rowCache[id] = list;
            return list;
        }

        // Ships whose row build already threw, so the log carries one report each rather than one
        // per frame. Instance ids, cleared with the rest of the selection state.
        private readonly HashSet<int> _rowErrorLogged = new HashSet<int>();

        /// <summary>
        /// Rounds of this ammo this ship already has staged at OTHER targets. Entries for the
        /// current target are excluded because committing the current selection replaces them
        /// (AddToStrike removes the matching Unit+Ammo+Target entry first), so counting them would
        /// charge the same rounds twice while the player edits a row they have already staged.
        /// </summary>
        private int StagedElsewhere(ObjectBase ship, string ammoId)
        {
            // Rounds the coordinator is already holding for this ship count too. They are a
            // different pool (orders taken in through Alt+H or auto-coordination, plus anything
            // committed and not yet away) and the panel used to ignore them entirely, so four
            // rounds collected in-game still left the panel offering four more.
            return Coordinator.HeldRounds(ship, ammoId) + StrikeRounds(ship, ammoId, _target);
        }

        /// <summary>
        /// Rounds of this ammunition the ship owes to orders that are not this target's: staged in
        /// the panel against another target, plus everything the coordinator is holding or has
        /// dispatched without seeing away. The magazine analogue of <see cref="StagedElsewhere"/>,
        /// which answers the same question for guidance channels.
        /// </summary>
        private int AmmoCommittedElsewhere(ObjectBase ship, string ammoId)
        {
            return Coordinator.AmmoOutstanding(ship, ammoId) + StrikeRounds(ship, ammoId, _target);
        }

        /// <summary>
        /// True if anything staged in the panel has filled its shooter's guidance channels. The
        /// coordinator's own flag cannot see planner picks until the strike is fired, and the title
        /// bar is the only place this is readable with the panel collapsed.
        /// </summary>
        private bool AnyStagedChannelCap()
        {
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit != null && Coordinator.IsChannelCapped(s.Unit, s.AmmoId)) return true;
            }
            return false;
        }

        /// <summary>
        /// Rounds staged in the panel for this ship and ammunition, across every target. Published
        /// to the coordinator so the intake gate can see picks that have not been fired yet; the
        /// mirror of <see cref="StagedElsewhere"/> reading the coordinator's own holdings.
        /// </summary>
        private int StagedRoundsFor(ObjectBase ship, string ammoId) => StrikeRounds(ship, ammoId, null);

        /// <summary>
        /// Rounds staged in the panel for this ship and ammunition, skipping the entries aimed at
        /// <paramref name="exceptTarget"/> (null counts every target). The walk behind the three
        /// helpers above.
        /// </summary>
        private int StrikeRounds(ObjectBase ship, string ammoId, ObjectBase exceptTarget)
        {
            int n = 0;
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit == ship && s.AmmoId == ammoId &&
                    (exceptTarget == null || s.Target != exceptTarget))
                    n += Mathf.Max(1, s.Salvo);
            }
            return n;
        }

        // Missiles this ship carries that can engage the current target (by type). Each row
        // also reports whether the target is within that missile's max range. With no target,
        // all missiles are listed and treated as in range.
        private IEnumerable<Row> EngageRows(ObjectBase ship)
        {
            if (ship == null) yield break;
            bool haveTarget = _target != null && !_target.IsDestroyed;
            float dist = haveTarget
                ? (_target.transform.position - ship.transform.position).magnitude : 0f;

            foreach (KeyValuePair<string, int> kv in ship.AmmunitionAmountDictionary)
            {
                if (kv.Value <= 0) continue;
                Ammunition a = ship.getAmmunitionByName(kv.Key);
                if (a?._ap == null || a._ap._type != Ammunition.Type.Missile) continue;

                bool inRange = true;
                bool tooClose = false;
                if (haveTarget)
                {
                    // Wrong target type or weapon incompatibility -> hide entirely.
                    if (!ship.DoesAmmoMatchTarget(a._ap, _target, out _))
                        continue;
                    inRange = dist <= ship.GetMaxRangeForAmmo(a, _target);
                    // The near bound, which the game enforces just as hard as the far one and
                    // which this panel used to ignore entirely: 290 of the game's 437 ammunition
                    // files carry a minimum, and inside it the launcher turns the ship to unmask
                    // and then refuses to fire, forever. Folded into the SAME flag as the far
                    // bound on purpose, so every caller that already filters on InRange (+ TARGET,
                    // + FORMATION, the fire buttons) is covered without touching any of them.
                    if (inRange &&
                        LaunchEnvelope.IsInsideMinimumRange(ship, a._ap, _target, out _, out _))
                    {
                        inRange = false;
                        tooClose = true;
                    }
                }

                // Cap the selectable count at what the serving launchers can actually fire (loaded +
                // magazine reserve), not the ship-wide inventory ; otherwise the salvo picker could
                // request rounds sitting behind an unusable launcher, and the strike fires short.
                int stock = Mathf.Min(kv.Value, LauncherFactsSource.AvailableRounds(ship, kv.Key));
                // Rounds of this ammunition already spoken for elsewhere: staged at ANOTHER target
                // in this panel, held by the coordinator, or dispatched and not yet away. The
                // channel cap below subtracts its own kind of commitment, but ordinary ammunition
                // has no channel cap at all, so before this a ship could be staged past its
                // magazine simply by visiting two targets. Finding 11 of
                // docs/plans/open/BETA-RELEASE-AUDIT-PLAN.md.
                stock = Mathf.Max(0, stock - AmmoCommittedElsewhere(ship, kv.Key));
                if (stock <= 0) continue;   // nothing physically firable: the row has no purpose
                // Second cap: a radio-command weapon that cannot group holds one fire-control
                // channel per round in flight, so the boat physically cannot put up more than it
                // has channels. Offering more cannot deliver them: the surplus launches, fails to
                // win a channel and self-destructs. See LauncherFactsSource.GuidanceChannelCap.
                //
                // Budget is per SHIP, not per target, so rounds this ship already has staged at
                // other targets come off the count offered here. Without that subtraction each
                // target was offered the full budget and a multi-target strike quietly ordered a
                // multiple of it. Coordinator.ClampToGuidanceChannels is the backstop that also
                // covers orders issued in the game's own interface; this only keeps the number the
                // panel shows honest while staging.
                int cap = LauncherFactsSource.GuidanceChannelCap(ship, kv.Key);
                int elsewhere = cap == int.MaxValue ? 0 : StagedElsewhere(ship, kv.Key);
                int left = cap == int.MaxValue ? int.MaxValue : Mathf.Max(0, cap - elsewhere);

                // Count may legitimately be 0 here, and the row is still emitted. Dropping it left
                // the ammunition simply missing from the list the moment its channels were spent at
                // another target, with nothing to say why; the row explains itself instead and
                // renders unselectable, the same way an out-of-range one does.
                yield return new Row
                {
                    AmmoId = kv.Key,
                    Count = Mathf.Min(stock, left),
                    InRange = inRange,
                    TooClose = tooClose,
                    ChannelCap = cap,
                    ChannelsElsewhere = elsewhere,
                };
            }
        }

        // Remove entries from _checked/_salvo for ships that no longer exist, preventing unbounded
        // dictionary growth over long sessions. Liveness is tested against the game's own unit list
        // (ObjectsManager._listOfAllUnits, which drops an object on destruction) ; NOT against the
        // currently-selected shooters, which would wrongly wipe saved selections for every ship the
        // player isn't looking at right now.
        private static readonly HashSet<int> _liveIdScratch = new HashSet<int>();

        private readonly List<string> _deadKeyScratch = new List<string>();

        private void PruneCheckSalvo()
        {
            if (!Singleton<ObjectsManager>.InstanceExists()) return;

            _liveIdScratch.Clear();
            List<ObjectBase> units = Singleton<ObjectsManager>.Instance._listOfAllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                ObjectBase u = units[i];
                if (u != null && !u.IsDestroyed) _liveIdScratch.Add(u.GetInstanceID());
            }

            _deadKeyScratch.Clear();
            foreach (KeyValuePair<string, bool> kv in _checked)
            {
                int pipeIdx = kv.Key.IndexOf('|');
                if (pipeIdx <= 0) continue;
                if (!int.TryParse(kv.Key.Substring(0, pipeIdx), out int instanceId)) continue;
                if (!_liveIdScratch.Contains(instanceId)) _deadKeyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _deadKeyScratch.Count; i++)
            {
                _checked.Remove(_deadKeyScratch[i]);
                _salvo.Remove(_deadKeyScratch[i]);
                _rowFacts.Remove(_deadKeyScratch[i]);
            }

            // Same liveness test for the staged strike: a shot whose shooter or target has died can
            // never be fired, and holding the reference keeps a destroyed object alive.
            _strike.RemoveAll(sh =>
                sh.Unit == null || sh.Target == null ||
                !_liveIdScratch.Contains(sh.Unit.GetInstanceID()) ||
                !_liveIdScratch.Contains(sh.Target.GetInstanceID()));

            _group.RemoveAll(u => u == null || !_liveIdScratch.Contains(u.GetInstanceID()));
        }

        private bool AnyChecked(List<ObjectBase> shooters)
        {
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                    if (r.InRange && _checked.TryGetValue(Key(ship, r.AmmoId), out bool on) && on) return true;
            return false;
        }

        /// <summary>
        /// True if a direct fire at the current target would fire anything: a checked, in-range row
        /// with rounds left that is not already staged against this target. Ticks outlive + TARGET,
        /// so without the staged test the fire buttons stayed live for rows the strike will fire.
        /// </summary>
        private bool AnyFireable(List<ObjectBase> shooters)
        {
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                    if (r.InRange && r.Count > 0 && !IsStagedHere(ship, r.AmmoId) &&
                        _checked.TryGetValue(Key(ship, r.AmmoId), out bool on) && on) return true;
            return false;
        }

        /// <summary>True if this shooter and missile type is already staged against the current target.</summary>
        private bool IsStagedHere(ObjectBase ship, string ammoId)
        {
            for (int i = 0; i < _strike.Count; i++)
            {
                Coordinator.Shot s = _strike[i];
                if (s.Unit == ship && s.AmmoId == ammoId && s.Target == _target) return true;
            }
            return false;
        }

        private static string Key(ObjectBase u, string ammoId) => u.GetInstanceID() + "|" + ammoId;
    

        /// <summary>This frame's <see cref="RowFacts"/> for a row, reset on the first ask of a frame.</summary>
        private RowFacts FactsFor(string key)
        {
            if (!_rowFacts.TryGetValue(key, out RowFacts f)) _rowFacts[key] = f = new RowFacts();
            if (f.Frame != Time.frameCount)
            {
                f.Frame = Time.frameCount;
                f.HaveChannel = f.HaveChecked = false;
            }
            return f;
        }

        /// <summary>The warnings a checked, in-range row can raise, asked of the game once a frame.</summary>
        private RowFacts CheckedRowFacts(ObjectBase ship, string ammoId, string key)
        {
            RowFacts f = FactsFor(key);
            if (f.HaveChecked) return f;
            f.HaveChecked = true;
            f.Manoeuvres = LauncherFactsSource.WillManoeuvreToFire(ship, ammoId, out f.WindowDeg);
            f.Contended = Coordinator.IsContended(ship, ammoId);
            f.SensorUnavailable = LauncherFactsSource.GuidanceSensorUnavailable(ship, ammoId);
            CoordinatorProfiler.Begin(CoordinatorProfiler.Stage.UiEnvelope);
            f.EnvelopeDelay = LaunchEnvelope.TimeToReady(ship, ammoId);
            CoordinatorProfiler.End(CoordinatorProfiler.Stage.UiEnvelope);
            return f;
        }
}
}
