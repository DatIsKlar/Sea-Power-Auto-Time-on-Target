using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// A (unit, ammunition) dictionary key: the unit's instance id and the ammunition file name. A struct,
    /// because these keys are built on per-frame paths where a string key allocates on every lookup. Keyed on
    /// the instance id so a destroyed unit cannot keep an entry alive; Unity reuses instance ids, so every
    /// collection keyed on this is cleared on a mission reset.
    /// </summary>
    internal readonly struct ShipAmmoKey : System.IEquatable<ShipAmmoKey>
    {
        private readonly int _unitId;
        private readonly string _ammoId;

        internal ShipAmmoKey(ObjectBase unit, string ammoId)
        {
            _unitId = unit != null ? unit.GetInstanceID() : 0;
            _ammoId = ammoId;
        }

        public bool Equals(ShipAmmoKey o) => _unitId == o._unitId && _ammoId == o._ammoId;
        public override bool Equals(object obj) => obj is ShipAmmoKey k && Equals(k);
        public override int GetHashCode() { unchecked { return (_unitId * 397) ^ (_ammoId?.GetHashCode() ?? 0); } }
    }
}
