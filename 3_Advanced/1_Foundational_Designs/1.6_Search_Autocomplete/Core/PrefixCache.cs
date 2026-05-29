// PrefixCache — LRU cache mapping prefix strings to their top-K results.
// Simulates a Redis layer in front of the trie:
//   Cache hit  → return results instantly, no trie traversal
//   Cache miss → call trie, store result, serve
//
// Why LRU here: the top ~200 prefixes (e.g. "a", "ap", "app") account for the
// vast majority of traffic (Zipf's law). LRU keeps them hot and evicts rare prefixes.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class PrefixCache
    {
        // Same Dictionary + LinkedList pattern as LruCache<T,V> in the URL shortener.
        // Specialised here for string keys and List<RankedCompletion> values.
        private readonly Dictionary<string, LinkedListNode<(string Key, List<RankedCompletion> Value)>> _map
            = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, List<RankedCompletion> Value)> _list = new();
        private readonly int _capacity;

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        // Default 200: covers the hot set of common 1–3 character prefixes.
        public PrefixCache(int capacity = 200) => _capacity = capacity;

        public bool TryGet(string prefix, out List<RankedCompletion> results)
        {
            if (_map.TryGetValue(prefix, out var node))
            {
                // Promote to front — this prefix was just queried, so it's hot.
                _list.Remove(node);
                _list.AddFirst(node);
                results = node.Value.Value;
                Hits++;
                return true;
            }
            results = null;
            Misses++;
            return false;
        }

        public void Put(string prefix, List<RankedCompletion> results)
        {
            if (_map.TryGetValue(prefix, out var existing))
            {
                // Replace existing entry — fires on every Put for a cached prefix,
                // including trend surge re-warming and normal cache refresh.
                _list.Remove(existing);
                _map.Remove(prefix);
            }
            else if (_map.Count >= _capacity)
            {
                // Evict least recently used (tail of list) to stay within memory budget.
                var lru = _list.Last;
                _list.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
            var n = _list.AddFirst((prefix, results));
            _map[prefix] = n;
        }

        // Called when the trie is rebuilt (e.g. hourly batch job).
        // All cached results are stale after a rebuild — flush everything.
        // In production this is "DEL *" or a Redis FLUSHDB on the cache instance.
        public void Flush()
        {
            _map.Clear();
            _list.Clear();
        }

        public void ResetStats() { Hits = 0; Misses = 0; }
    }
}
