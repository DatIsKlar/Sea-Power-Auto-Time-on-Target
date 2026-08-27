using System.Collections.Generic;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Small TTL cache clocked by <see cref="Time.unscaledTime"/> (real seconds, unaffected by
    /// game pause / time compression). Used on per-frame UI paths — flight-time ETAs and launcher
    /// facts — where recomputing every frame causes visible stutter. Positions and loadouts barely
    /// move within the TTL window, so a short cache is lossless in practice.
    ///
    /// Eviction mirrors the original hand-rolled caches: when the entry count exceeds the
    /// capacity, expired entries are dropped first; if that still leaves the cache over capacity
    /// it is cleared wholesale. Simple, allocation-free apart from the scratch list.
    /// </summary>
    internal sealed class TtlCache<TKey, TValue>
    {
        private struct Entry
        {
            public float StampUnscaled;
            public TValue Value;
        }

        private readonly Dictionary<TKey, Entry> _map = new Dictionary<TKey, Entry>();
        private readonly List<TKey> _evictScratch = new List<TKey>();
        private readonly float _ttlSeconds;
        private readonly int _capacity;

        public TtlCache(float ttlSeconds, int capacity = 512)
        {
            _ttlSeconds = ttlSeconds;
            _capacity = capacity;
        }

        /// <summary>Live (non-expired) value for the key, if any.</summary>
        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out Entry hit) && (Time.unscaledTime - hit.StampUnscaled) < _ttlSeconds)
            {
                value = hit.Value;
                return true;
            }
            value = default;
            return false;
        }

        /// <summary>Insert/overwrite, evicting expired entries first when over capacity.</summary>
        public void Set(TKey key, TValue value)
        {
            float now = Time.unscaledTime;
            if (_map.Count > _capacity)
            {
                _evictScratch.Clear();
                foreach (KeyValuePair<TKey, Entry> kv in _map)
                    if ((now - kv.Value.StampUnscaled) >= _ttlSeconds) _evictScratch.Add(kv.Key);
                for (int i = 0; i < _evictScratch.Count; i++) _map.Remove(_evictScratch[i]);
                if (_map.Count > _capacity) _map.Clear();
            }
            _map[key] = new Entry { StampUnscaled = now, Value = value };
        }

        public void Clear() => _map.Clear();
    }
}
