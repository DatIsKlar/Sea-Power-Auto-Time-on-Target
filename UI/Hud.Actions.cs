using System.Collections.Generic;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Hud (partial) ; actions: firing and arming the strike, adding the selection to the strike
    /// or the shooter roster, and the strike's target and collected-order lists.
    /// </summary>
    internal sealed partial class Hud
    {
        /// <summary>
        /// Fire whatever the strike holds, from the hotkey. Same commit the panel's FIRE STRIKE
        /// button makes, so a strike built earlier can be launched with the panel hidden; a hidden
        /// panel is a normal way to play, and having to open one to press a button you already
        /// decided on is a delay the shot cannot always afford.
        /// </summary>
        private void FireStrikeNow()
        {
            int held = _strike.Count + Coordinator.StrikeCount;
            if (held == 0)
            {
                Bootstrap.Log.LogInfo("[AutoTOT] fire-strike pressed with nothing staged.");
                return;
            }
            Bootstrap.Log.LogInfo($"[AutoTOT] firing strike from hotkey: {held} order(s).");
            Coordinator.FireStrike(_strike);
            _strike.Clear();
        }

        private void ToggleStrikeArmed()
        {
            if (Coordinator.StrikeArmed) Coordinator.CancelStrike();
            else Coordinator.ArmStrike();
        }

        /// <summary>
        /// Stage the currently checked rows against the current target. Rows already staged for the
        /// same shooter, ammo and target are replaced rather than doubled up.
        ///
        /// The ticks stay as they are. A tick belongs to a ship and a missile type, not to a target,
        /// so a strike across several targets is built by picking each target in turn and pressing
        /// + TARGET. Clearing them here made every new target a row-by-row re-tick. To leave a
        /// shooter out of the next target, untick it or take it off the roster.
        /// </summary>
        private void AddSelectionToStrike(List<ObjectBase> shooters)
        {
            if (_target == null || _target.IsDestroyed) return;
            int added = 0;
            foreach (ObjectBase ship in shooters)
                foreach (Row r in CachedEngageRows(ship))
                {
                    // Count 0 means every guidance channel is already staged at another target.
                    // Without this the row would stage a Salvo of 0, which the coordinator floors
                    // back up to 1 and orders a round the ship cannot guide.
                    if (!r.InRange || r.Count <= 0) continue;
                    string key = Key(ship, r.AmmoId);
                    if (!_checked.TryGetValue(key, out bool on) || !on) continue;
                    int salvo = Mathf.Min(_salvo.TryGetValue(key, out int sv) ? sv : 1, r.Count);
                    _strike.RemoveAll(x => x.Unit == ship && x.AmmoId == r.AmmoId && x.Target == _target);
                    _strike.Add(new Coordinator.Shot
                    {
                        Unit = ship, AmmoId = r.AmmoId, Salvo = salvo, Target = _target,
                    });
                    added++;
                }
            if (added > 0) _strikeGeneration = Coordinator.ResetGeneration;
        }

        /// <summary>
        /// Add the current selection to the shooter roster: just the selected ship, or every
        /// missile-armed ship in its formation. Which one is the caller's choice, not a mode read
        /// off the panel. No target is needed, which is the point: the roster is what you assemble
        /// first, then aim, and it is not limited to one formation.
        /// </summary>
        private void AddSelectionToGroup(bool wholeFormation)
        {
            if (_anchor == null || _anchor.IsDestroyed) return;

            if (wholeFormation && _anchor.Formation != null)
            {
                foreach (Station st in _anchor.Formation.Stations)
                {
                    ObjectBase u = st?.UnitObject;
                    if (IsMissileShip(u) && !_group.Contains(u)) _group.Add(u);
                }
            }
            if (IsMissileShip(_anchor) && !_group.Contains(_anchor)) _group.Add(_anchor);
            _strikeGeneration = Coordinator.ResetGeneration;
        }

        // FormationFullyHeld, once per frame: it walks the whole formation's engage rows.
        private int _formationHeldFrame = -1;

        private ObjectBase _formationHeldFor;

        private bool _formationHeld;

        /// <summary>
        /// True when every ship + FORMATION would add is already in the roster. Tested against
        /// IsMissileShip for the same reason AddSelectionToGroup filters on it: a formation's
        /// non-missile hulls are never added, so counting them would leave the button reading
        /// "+ FORMATION" forever with nothing left to add.
        /// </summary>
        private bool FormationFullyHeld(ObjectBase anchor)
        {
            if (_formationHeldFrame == Time.frameCount && ReferenceEquals(_formationHeldFor, anchor))
                return _formationHeld;
            _formationHeldFrame = Time.frameCount;
            _formationHeldFor = anchor;
            return _formationHeld = ComputeFormationFullyHeld(anchor);
        }

        private bool ComputeFormationFullyHeld(ObjectBase anchor)
        {
            if (anchor?.Formation == null) return false;
            bool any = false;
            foreach (Station st in anchor.Formation.Stations)
            {
                ObjectBase u = st?.UnitObject;
                if (!IsMissileShip(u)) continue;
                any = true;
                if (!_group.Contains(u)) return false;
            }
            // AddSelectionToGroup also adds the anchor itself, whether or not it holds a station.
            if (IsMissileShip(anchor))
            {
                any = true;
                if (!_group.Contains(anchor)) return false;
            }
            return any;
        }

        /// <summary>Distinct targets in the staged strike, in the order they were first staged.</summary>
        // Targets in the whole strike: the panel's staged rows first, then any target that only the
        // collected in-game orders name. Both halves fire together, so both belong in the list and
        // in the counts on the commit button.
        private void CollectStrikeTargets(List<ObjectBase> into)
        {
            into.Clear();
            foreach (Coordinator.Shot s in _strike)
                if (s.Target != null && !into.Contains(s.Target)) into.Add(s.Target);
            foreach (Coordinator.Shot s in CollectedOrders())
                if (s.Target != null && !into.Contains(s.Target)) into.Add(s.Target);
        }

        // Orders the coordinator has caught from the game's own interface, refreshed once a frame.
        private readonly List<Coordinator.Shot> _collected = new List<Coordinator.Shot>();

        private int _collectedFrame = -1;

        private List<Coordinator.Shot> CollectedOrders()
        {
            if (_collectedFrame != Time.frameCount)
            {
                _collectedFrame = Time.frameCount;
                Coordinator.CollectStrikeIntents(_collected);
            }
            return _collected;
        }
    }
}
