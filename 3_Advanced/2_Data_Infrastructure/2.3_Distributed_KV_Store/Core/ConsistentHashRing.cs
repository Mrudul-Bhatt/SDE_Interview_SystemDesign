// ConsistentHashRing — maps keys to nodes using a virtual-node ring.
//
// Why virtual nodes: without them, adding/removing a physical node
// only moves keys from one arc of the ring, creating hot spots.
// 150 virtual nodes per physical node spreads the load evenly.
//
// Why MD5 for hashing: unlike GetHashCode(), MD5 is stable across
// processes and platforms — critical for a distributed system where
// every node must independently derive the same key→node mapping.

using System.Security.Cryptography;
using System.Text;

namespace AdvancedDesigns
{
    public class ConsistentHashRing
    {
        private readonly SortedDictionary<uint, string> _ring = new();
        private readonly int _virtualNodes;

        public ConsistentHashRing(int virtualNodes = 150)
        {
            _virtualNodes = virtualNodes;
        }

        public void AddNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
                _ring[Hash($"{nodeId}#vnode{i}")] = nodeId;
        }

        public void RemoveNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
                _ring.Remove(Hash($"{nodeId}#vnode{i}"));
        }

        // Returns the first node clockwise from key's hash position.
        public string GetNode(string key)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            uint pos = Hash(key);
            foreach (var kv in _ring)
                if (kv.Key >= pos) return kv.Value;
            // No virtual node sits clockwise of this key — wrap to the ring's start.
            // This is what makes the structure a ring rather than a list.
            return _ring.First().Value;
        }

        // Returns up to `count` distinct physical nodes clockwise from key.
        // Used for replication: RF=3 means the key is stored on 3 different nodes.
        public List<string> GetNodes(string key, int count)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            var result = new List<string>();
            var seen = new HashSet<string>();
            uint pos = Hash(key);
            uint start = pos;

            // First pass: collect nodes clockwise from the key's position.
            foreach (var kv in _ring)
            {
                if (kv.Key >= pos && !seen.Contains(kv.Value))
                {
                    result.Add(kv.Value); seen.Add(kv.Value);
                    if (result.Count == count) return result;
                }
            }
            // Second pass: wrap around to the start of the ring to fill remaining replica slots.
            // Needed when the key sits near the end of the ring and fewer than `count`
            // distinct nodes exist clockwise before the ring wraps.
            foreach (var kv in _ring)
            {
                if (kv.Key < start && !seen.Contains(kv.Value))
                {
                    result.Add(kv.Value); seen.Add(kv.Value);
                    if (result.Count == count) return result;
                }
            }
            return result;
        }

        public Dictionary<string, int> GetLoadDistribution()
        {
            var counts = new Dictionary<string, int>();
            foreach (var nodeId in _ring.Values)
                counts[nodeId] = counts.TryGetValue(nodeId, out int c) ? c + 1 : 1;
            return counts;
        }

        private static uint Hash(string input)
        {
            byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToUInt32(bytes, 0);
        }
    }
}
