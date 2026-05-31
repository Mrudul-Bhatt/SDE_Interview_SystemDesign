// ConsistentHashRing — maps keys to nodes using a virtual-node ring.
//
// THE BIG IDEA:
// Imagine a clock face with numbers 0 to 4 billion around the edge. Every server
// gets placed at some position on the clock by hashing its name. Every key gets
// placed at some position by hashing the key. To find which server owns a key,
// you stand at the key's position and walk CLOCKWISE until you bump into a server.
// That server owns the key.
//
// Why this is magic:
//   - If you add a new server, it only steals keys from ONE neighbour on the ring.
//     ~5% of keys move; the other 95% stay put. (Compare to plain hash-modulo,
//     where adding a server means EVERY key gets remapped.)
//   - If a server dies, its keys roll over to the next server clockwise.
//     Again, only one slice of the ring is affected.
//
// WHY VIRTUAL NODES (vnodes):
// If we placed each physical server at just one position, random hashing could
// land two servers right next to each other, leaving a huge arc for one unlucky
// server and a tiny arc for the others — load imbalance.
//
// Fix: give each physical server 150 positions on the ring (here `_virtualNodes`).
// Each vnode is independent: hash "ServerA#vnode0", "ServerA#vnode1", ... etc.
// With 150 random positions per server, the law of large numbers takes over —
// each physical server ends up owning ~1/N of the ring on average. When a server
// dies, its 150 vnodes go away, and their slices get absorbed by MANY different
// neighbours instead of dumping the whole load on one unlucky successor.
//
// WHY MD5 (NOT GetHashCode):
// MD5 produces the SAME number for the same string on every machine, every
// process restart, every .NET version. GetHashCode is randomized per process —
// fine for in-memory hashtables, catastrophic for distributed routing where every
// node must independently agree on "key X belongs to server Y". We're not using
// MD5's cryptographic properties — just its stability and good bit distribution.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AdvancedDesigns
{
    public class ConsistentHashRing
    {
        // SortedDictionary so we can walk positions in clockwise (ascending) order.
        // Key = position on the ring (0 to 4 billion), Value = which physical server.
        private readonly SortedDictionary<uint, string> _ring = [];
        private readonly int _virtualNodes;

        // 150 vnodes per physical node is the Cassandra/Dynamo default —
        // empirically it gives a load imbalance of < 10% between the busiest
        // and least busy nodes. More vnodes = smoother distribution but more
        // memory + slower ring operations.
        public ConsistentHashRing(int virtualNodes = 150)
        {
            _virtualNodes = virtualNodes;
        }

        // Add a server to the ring by sprinkling 150 of its vnodes across random
        // positions. Each vnode is just "nodeId#vnodeN" hashed — same nodeId always
        // produces the same 150 positions, so a restart rebuilds an identical ring.
        public void AddNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
                _ring[Hash($"{nodeId}#vnode{i}")] = nodeId;
        }

        // Remove a server by deleting all 150 of its vnodes. Whatever sat in those
        // slots now rolls over to whoever sits clockwise of each vacated position.
        public void RemoveNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
                _ring.Remove(Hash($"{nodeId}#vnode{i}"));
        }

        // The core lookup: given a key, who owns it?
        // Steps:
        //   1. Hash the key → land it somewhere on the 0..4B ring.
        //   2. Walk clockwise (ascending positions) until we find a vnode.
        //   3. That vnode's physical server is the owner.
        public string GetNode(string key)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            uint pos = Hash(key);

            // Walk clockwise — the first vnode we hit at or past our position wins.
            // Note: this is O(N) in the simple form below; production code uses
            // a binary search on the sorted keys for O(log N). Fine for a demo.
            foreach (var kv in _ring)
                if (kv.Key >= pos) return kv.Value;

            // We walked off the end of the ring without finding a vnode — that means
            // our key landed past the highest vnode position. WRAP AROUND to the
            // first vnode (the one at the smallest position). This wrap is what
            // makes the structure a RING rather than a one-way list — every key
            // is guaranteed to find an owner.
            return _ring.First().Value;
        }

        // Replication: returns the FIRST `count` distinct physical servers walking
        // clockwise from the key's position. With RF=3, we get [primary, replica1,
        // replica2] — the three servers that each store a copy of this key.
        //
        // Key subtlety: we want DISTINCT PHYSICAL servers, not distinct vnodes.
        // If serverA's vnode0 owns the key, the next clockwise vnode might ALSO
        // be serverA (its vnode42). We skip that — replicas only make sense on
        // different physical machines.
        public List<string> GetNodes(string key, int count)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            var result = new List<string>();
            var seen = new HashSet<string>();   // tracks PHYSICAL servers already counted
            uint pos = Hash(key);
            uint start = pos;

            // First pass: clockwise from `pos` to the end of the ring.
            foreach (var kv in _ring)
            {
                if (kv.Key >= pos && !seen.Contains(kv.Value))
                {
                    result.Add(kv.Value);
                    seen.Add(kv.Value);
                    if (result.Count == count) return result;
                }
            }
            // Second pass: we walked off the end of the ring, so WRAP to the
            // beginning. Same logic as GetNode's wrap — the ring is circular.
            // This pass is essential when the key sits near the high end of the
            // ring and there aren't enough distinct servers between the key and
            // the ring's max position.
            foreach (var kv in _ring)
            {
                if (kv.Key < start && !seen.Contains(kv.Value))
                {
                    result.Add(kv.Value);
                    seen.Add(kv.Value);
                    if (result.Count == count) return result;
                }
            }
            return result;
        }

        // Diagnostic: how many vnodes does each physical server own?
        // In a well-balanced ring, every server should have ~_virtualNodes entries.
        // Big skew here means a server is either too new (not enough vnodes
        // registered) or there's a bug in node registration.
        public Dictionary<string, int> GetLoadDistribution()
        {
            var counts = new Dictionary<string, int>();
            foreach (var nodeId in _ring.Values)
                counts[nodeId] = counts.TryGetValue(nodeId, out int c) ? c + 1 : 1;
            return counts;
        }

        // Convert a string to a 32-bit position on the ring.
        // MD5 returns 16 bytes; we take just the first 4 (32 bits) since our ring
        // is parameterized over uint. That's plenty of address space — 4 billion
        // positions is far more than the number of vnodes we'll ever have, so
        // collisions are astronomically rare.
        private static uint Hash(string input)
        {
            byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToUInt32(bytes, 0);
        }
    }
}
