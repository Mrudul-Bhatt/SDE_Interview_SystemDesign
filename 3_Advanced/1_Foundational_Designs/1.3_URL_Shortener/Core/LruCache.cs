// LruCache<TKey, TValue>
// Capacity-bounded cache that evicts the Least Recently Used entry when full.
// Simulates Redis with the allkeys-lru eviction policy.
//
// Data structure: Dictionary + doubly-linked list
//   Dictionary  → O(1) key lookup to find the node
//   LinkedList  → O(1) move-to-front and evict-tail
//   Together they give O(1) Get and Put — impossible with either alone.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class LruCache<TKey, TValue>
    {
        private readonly int _capacity;

        // Maps each key directly to its LinkedListNode so we can remove it in O(1)
        // without scanning the list (LinkedList.Remove(node) is O(1); Remove(value) is O(n)).
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;

        // Front = most recently used, tail = least recently used (eviction candidate).
        private readonly LinkedList<(TKey Key, TValue Value)> _list;

        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Count => _map.Count;

        public LruCache(int capacity)
        {
            _capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity);
            _list = new LinkedList<(TKey, TValue)>();
        }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Promote to front on every access — this is what makes it "LRU":
                // the node at the tail is the one that was accessed longest ago.
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                Hits++;
                return true;
            }
            value = default;
            Misses++;
            return false;
        }

        public void Put(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                // Key already in cache — remove old node so we can re-insert at front.
                // This refreshes its "recently used" position.
                _list.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                // Cache is full — evict the tail (least recently used).
                // We store the key inside the node value so we can remove it from _map
                // without a separate reverse-lookup structure.
                var lru = _list.Last;
                _list.RemoveLast();
                _map.Remove(lru.Value.Key);
            }

            // Insert at front — this entry is now the most recently used.
            var node = _list.AddFirst((key, value));
            _map[key] = node;
        }

        public void Remove(TKey key)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _map.Remove(key);
            }
        }

        // Reset hit/miss counters between test scenarios for clean reporting.
        public void ResetStats() { Hits = 0; Misses = 0; }
    }
}
