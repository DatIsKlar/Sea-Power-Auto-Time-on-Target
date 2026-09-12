using System.Collections.Generic;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Small TTL cache clocked by <see cref="Time.unscaledTime"/> (real seconds, unaffected by
    /// game pause / time compression). Used on per-frame UI paths ; flight-time ETAs and launcher
    /// facts ; where recomputing every frame causes visible stutter. Positions and loadouts barely
    /// move within the TTL window, so a short cache is lossless in practice.
    ///
    /// Eviction: inserting a new key into a full cache drops expired entries first; if the cache
    /// is still full of live ones, the oldest quarter goes. Allocation-free apart from the two
    /// scratch lists.
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
        private long _hits;
        private long _misses;
        private long _evictionsTtl;
        private long _evictionsCapacity;

        public TtlCache(float ttlSeconds, int capacity = 512)
        {
            _ttlSeconds = ttlSeconds;
            _capacity = capacity;
        }

        /// <summary>Live (non-expired) value for the key, if any.</summary>
        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out Entry entry) && (Time.unscaledTime - entry.StampUnscaled) < _ttlSeconds)
            {
                value = entry.Value;
                _hits++;
                return true;
            }
            value = default;
            _misses++;
            return false;
        }

        /// <summary>Insert/overwrite. Inserting a new key into a full cache evicts first.</summary>
        public void Set(TKey key, TValue value)
        {
            float now = Time.unscaledTime;
            if (_map.Count >= _capacity && !_map.ContainsKey(key)) Evict(now);
            _map[key] = new Entry { StampUnscaled = now, Value = value };
        }

        /// <summary>
        /// Make room: drop expired entries, and if the cache is still full of live ones, drop the
        /// oldest quarter. It used to clear everything at that point, which made the next reader of
        /// every key miss at once; a strike with more orders than the cache holds then had every
        /// order re-solved on the main thread in a single frame.
        /// </summary>
        private void Evict(float now)
        {
            _evictScratch.Clear();
            foreach (KeyValuePair<TKey, Entry> kv in _map)
                if ((now - kv.Value.StampUnscaled) >= _ttlSeconds) _evictScratch.Add(kv.Key);
            for (int i = 0; i < _evictScratch.Count; i++) _map.Remove(_evictScratch[i]);
            _evictionsTtl += _evictScratch.Count;
            if (_map.Count < _capacity) return;

            _ageScratch.Clear();
            foreach (KeyValuePair<TKey, Entry> kv in _map)
                _ageScratch.Add(new KeyValuePair<TKey, float>(kv.Key, kv.Value.StampUnscaled));
            _ageScratch.Sort(OldestFirst);
            int drop = _map.Count - (_capacity * 3) / 4;
            for (int i = 0; i < drop; i++) _map.Remove(_ageScratch[i].Key);
            _evictionsCapacity += drop;
        }

        private static readonly System.Comparison<KeyValuePair<TKey, float>> OldestFirst =
            (a, b) => a.Value.CompareTo(b.Value);
        private readonly List<KeyValuePair<TKey, float>> _ageScratch = new List<KeyValuePair<TKey, float>>();

        public long HitCount => _hits;
        public long MissCount => _misses;
        public int Count => _map.Count;
        public long EvictionsTtl => _evictionsTtl;
        public long EvictionsCapacity => _evictionsCapacity;
        public void ResetStats() { _hits = 0; _misses = 0; _evictionsTtl = 0; _evictionsCapacity = 0; }
        public void Clear() { _map.Clear(); _hits = 0; _misses = 0; _evictionsTtl = 0; _evictionsCapacity = 0; }
    }
}
