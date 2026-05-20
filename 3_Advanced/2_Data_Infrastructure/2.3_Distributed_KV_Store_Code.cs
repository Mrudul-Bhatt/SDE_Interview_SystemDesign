using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AdvancedDesigns
{
    // ─── Consistent Hashing Ring ───────────────────────────────────────────────

    public class ConsistentHashRing
    {
        private readonly SortedDictionary<uint, string> _ring = new SortedDictionary<uint, string>();
        private readonly int _virtualNodes;

        public ConsistentHashRing(int virtualNodes = 150)
        {
            _virtualNodes = virtualNodes;
        }

        public void AddNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
            {
                uint pos = Hash($"{nodeId}#vnode{i}");
                _ring[pos] = nodeId;
            }
        }

        public void RemoveNode(string nodeId)
        {
            for (int i = 0; i < _virtualNodes; i++)
            {
                uint pos = Hash($"{nodeId}#vnode{i}");
                _ring.Remove(pos);
            }
        }

        public string GetNode(string key)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            uint pos = Hash(key);
            foreach (var kv in _ring)
                if (kv.Key >= pos)
                    return kv.Value;
            return _ring.First().Value; // wrap around
        }

        public List<string> GetNodes(string key, int count)
        {
            if (_ring.Count == 0) throw new InvalidOperationException("Ring is empty");
            var result = new List<string>();
            var seen = new HashSet<string>();
            uint pos = Hash(key);

            // Start from key's position, walk clockwise
            bool wrapped = false;
            uint startPos = pos;
            foreach (var kv in _ring)
            {
                if (kv.Key >= pos || wrapped)
                {
                    if (!seen.Contains(kv.Value))
                    {
                        result.Add(kv.Value);
                        seen.Add(kv.Value);
                        if (result.Count == count) return result;
                    }
                }
            }
            // Wrap around
            foreach (var kv in _ring)
            {
                if (kv.Key < startPos)
                {
                    if (!seen.Contains(kv.Value))
                    {
                        result.Add(kv.Value);
                        seen.Add(kv.Value);
                        if (result.Count == count) return result;
                    }
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

    // ─── Bloom Filter (per-SSTable miss filter) ────────────────────────────────

    public class BloomFilter
    {
        private readonly bool[] _bits;
        private readonly int _size;
        private readonly int _hashCount;

        public BloomFilter(int size = 10000, int hashCount = 7)
        {
            _size = size;
            _hashCount = hashCount;
            _bits = new bool[size];
        }

        public void Add(string item)
        {
            foreach (int pos in GetPositions(item))
                _bits[pos] = true;
        }

        public bool MightContain(string item)
        {
            return GetPositions(item).All(pos => _bits[pos]);
        }

        private IEnumerable<int> GetPositions(string item)
        {
            for (int seed = 0; seed < _hashCount; seed++)
            {
                int hash = seed * unchecked((int)2654435761u);
                foreach (char c in item) hash = hash * 31 + c;
                yield return Math.Abs(hash % _size);
            }
        }
    }

    // ─── Storage Entry (value + metadata) ─────────────────────────────────────

    public class StorageEntry
    {
        public string Value { get; }
        public long Timestamp { get; }
        public DateTime? ExpiresAt { get; }
        public bool IsTombstone { get; }

        public StorageEntry(string value, long timestamp, int? ttlSeconds = null)
        {
            Value = value;
            Timestamp = timestamp;
            ExpiresAt = ttlSeconds.HasValue ? DateTime.UtcNow.AddSeconds(ttlSeconds.Value) : (DateTime?)null;
            IsTombstone = false;
        }

        private StorageEntry()
        {
            Value = null;
            Timestamp = long.MaxValue;
            IsTombstone = true;
        }

        public static StorageEntry CreateTombstone(long timestamp)
        {
            return new StorageEntry { };
        }

        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

        // private constructor for tombstone
        private StorageEntry(bool tombstone) { IsTombstone = tombstone; Value = null; Timestamp = long.MaxValue; }

        public static StorageEntry Tombstone(long timestamp) => new StorageEntry(true) { };
    }

    // ─── MemTable (sorted in-memory write buffer) ──────────────────────────────

    public class MemTable
    {
        private readonly SortedDictionary<string, StorageEntry> _data = new SortedDictionary<string, StorageEntry>();
        private int _sizeBytes;
        private readonly int _flushThresholdBytes;

        public bool ShouldFlush => _sizeBytes >= _flushThresholdBytes;
        public int Count => _data.Count;

        public MemTable(int flushThresholdBytes = 64 * 1024 * 1024) // 64 MB
        {
            _flushThresholdBytes = flushThresholdBytes;
        }

        public void Put(string key, string value, long timestamp, int? ttlSeconds = null)
        {
            _data[key] = new StorageEntry(value, timestamp, ttlSeconds);
            _sizeBytes += key.Length + (value?.Length ?? 0) + 32;
        }

        public void Delete(string key, long timestamp)
        {
            // Write tombstone to MemTable so reads see deletion
            _data[key] = new TombstoneEntry(timestamp);
            _sizeBytes += key.Length + 32;
        }

        public bool TryGet(string key, out StorageEntry entry)
        {
            return _data.TryGetValue(key, out entry);
        }

        public IEnumerable<KeyValuePair<string, StorageEntry>> GetSortedEntries()
        {
            return _data;
        }

        public void Clear()
        {
            _data.Clear();
            _sizeBytes = 0;
        }
    }

    public class TombstoneEntry : StorageEntry
    {
        public TombstoneEntry(long timestamp) : base(null, timestamp) { }
        public new bool IsTombstone => true;
    }

    // ─── SSTable (immutable sorted file — simulated in-memory) ────────────────

    public class SSTable
    {
        private readonly SortedDictionary<string, StorageEntry> _data;
        private readonly BloomFilter _bloomFilter;
        public int Level { get; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public SSTable(IEnumerable<KeyValuePair<string, StorageEntry>> entries, int level)
        {
            Level = level;
            _data = new SortedDictionary<string, StorageEntry>();
            _bloomFilter = new BloomFilter(size: 100000, hashCount: 7);
            foreach (var kv in entries)
            {
                _data[kv.Key] = kv.Value;
                _bloomFilter.Add(kv.Key);
            }
        }

        public bool TryGet(string key, out StorageEntry entry)
        {
            entry = null;
            if (!_bloomFilter.MightContain(key)) return false; // Bloom filter skip
            return _data.TryGetValue(key, out entry);
        }

        public IEnumerable<KeyValuePair<string, StorageEntry>> GetAllEntries() => _data;
        public int Count => _data.Count;
    }

    // ─── KV Node (single node with full LSM stack) ─────────────────────────────

    public class KvNode
    {
        public string NodeId { get; }
        private MemTable _memTable = new MemTable(flushThresholdBytes: 1024); // small for demo
        private readonly List<SSTable> _l0SSTables = new List<SSTable>();
        private long _logicalClock;
        private readonly object _lock = new object();

        public KvNode(string nodeId)
        {
            NodeId = nodeId;
        }

        public void Put(string key, string value, int? ttlSeconds = null)
        {
            lock (_lock)
            {
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Put(key, value, ts, ttlSeconds);

                if (_memTable.ShouldFlush)
                    FlushMemTable();
            }
        }

        public void Delete(string key)
        {
            lock (_lock)
            {
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Delete(key, ts);
            }
        }

        public (bool found, string value, long timestamp) Get(string key)
        {
            lock (_lock)
            {
                // 1. Check MemTable first (freshest)
                if (_memTable.TryGet(key, out StorageEntry entry))
                {
                    if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                    if (entry.IsExpired) return (false, null, 0);
                    return (true, entry.Value, entry.Timestamp);
                }

                // 2. Search SSTables L0 → L1 (newest first)
                for (int i = _l0SSTables.Count - 1; i >= 0; i--)
                {
                    if (_l0SSTables[i].TryGet(key, out entry))
                    {
                        if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                        if (entry.IsExpired) return (false, null, 0);
                        return (true, entry.Value, entry.Timestamp);
                    }
                }

                return (false, null, 0);
            }
        }

        private void FlushMemTable()
        {
            var ssTable = new SSTable(_memTable.GetSortedEntries(), level: 0);
            _l0SSTables.Add(ssTable);
            _memTable.Clear();
        }

        public void ForceFlush()
        {
            lock (_lock) { FlushMemTable(); }
        }

        public (int memTableCount, int l0SSTables) GetStats()
        {
            lock (_lock) { return (_memTable.Count, _l0SSTables.Count); }
        }
    }

    // ─── Distributed KV Store (coordinates multiple nodes) ────────────────────

    public class DistributedKvStore
    {
        private readonly Dictionary<string, KvNode> _nodes = new Dictionary<string, KvNode>();
        private readonly ConsistentHashRing _ring;
        private readonly int _replicationFactor;
        private readonly int _writeQuorum;
        private readonly int _readQuorum;
        private readonly HashSet<string> _downNodes = new HashSet<string>();

        public DistributedKvStore(int replicationFactor = 3, int writeQuorum = 2, int readQuorum = 2)
        {
            _replicationFactor = replicationFactor;
            _writeQuorum = writeQuorum;
            _readQuorum = readQuorum;
            _ring = new ConsistentHashRing(virtualNodes: 50); // smaller for demo clarity
        }

        public void AddNode(string nodeId)
        {
            _nodes[nodeId] = new KvNode(nodeId);
            _ring.AddNode(nodeId);
        }

        public void SimulateNodeDown(string nodeId) => _downNodes.Add(nodeId);
        public void SimulateNodeUp(string nodeId) => _downNodes.Remove(nodeId);

        public bool Put(string key, string value, int? ttlSeconds = null)
        {
            List<string> targetNodes = _ring.GetNodes(key, _replicationFactor);
            int acks = 0;

            foreach (string nodeId in targetNodes)
            {
                if (_downNodes.Contains(nodeId))
                    continue; // skip down nodes; hinted handoff would handle this in real system

                _nodes[nodeId].Put(key, value, ttlSeconds);
                acks++;
                if (acks >= _writeQuorum) break; // quorum satisfied
            }

            return acks >= _writeQuorum;
        }

        public (bool found, string value) Get(string key)
        {
            List<string> targetNodes = _ring.GetNodes(key, _replicationFactor);
            var responses = new List<(string nodeId, bool found, string value, long timestamp)>();

            foreach (string nodeId in targetNodes)
            {
                if (_downNodes.Contains(nodeId)) continue;
                var (found, value, timestamp) = _nodes[nodeId].Get(key);
                responses.Add((nodeId, found, value, timestamp));
                if (responses.Count >= _readQuorum) break;
            }

            if (responses.Count < _readQuorum) return (false, null); // can't satisfy quorum

            // Return highest timestamp version
            var best = responses.OrderByDescending(r => r.timestamp).FirstOrDefault();
            if (!best.found) return (false, null);

            // Read repair: update stale replicas (simplified)
            foreach (var resp in responses)
            {
                if (resp.timestamp < best.timestamp && !_downNodes.Contains(resp.nodeId))
                {
                    _nodes[resp.nodeId].Put(key, best.value);
                }
            }

            return (true, best.value);
        }

        public bool Delete(string key)
        {
            List<string> targetNodes = _ring.GetNodes(key, _replicationFactor);
            int acks = 0;
            foreach (string nodeId in targetNodes)
            {
                if (_downNodes.Contains(nodeId)) continue;
                _nodes[nodeId].Delete(key);
                acks++;
                if (acks >= _writeQuorum) break;
            }
            return acks >= _writeQuorum;
        }

        public List<string> GetResponsibleNodes(string key)
        {
            return _ring.GetNodes(key, _replicationFactor);
        }

        public Dictionary<string, int> GetRingDistribution()
        {
            return _ring.GetLoadDistribution();
        }
    }

    // ─── Main Program ──────────────────────────────────────────────────────────

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Distributed Key-Value Store Demo ===\n");

            Scenario1_ConsistentHashingDistribution();
            Scenario2_BasicReadWrite();
            Scenario3_QuorumWriteAndReadRepair();
            Scenario4_TtlExpiry();
            Scenario5_NodeFailureAndRecovery();
        }

        // ── Scenario 1: Consistent Hashing & Load Distribution ────────────────

        static void Scenario1_ConsistentHashingDistribution()
        {
            Console.WriteLine("─── Scenario 1: Consistent Hashing Ring ───");

            var ring = new ConsistentHashRing(virtualNodes: 150);
            ring.AddNode("NodeA");
            ring.AddNode("NodeB");
            ring.AddNode("NodeC");

            // Show which node each key maps to
            string[] keys = { "user:1", "user:2", "user:3", "cart:42", "session:99", "profile:7" };
            Console.WriteLine("Key → Primary Node mapping:");
            foreach (string key in keys)
                Console.WriteLine($"  {key,-20} → {ring.GetNode(key)}");

            // Show RF=3 node list per key
            Console.WriteLine("\nKey → RF=3 replica nodes:");
            foreach (string key in new[] { "user:1", "cart:42" })
            {
                var nodes = ring.GetNodes(key, 3);
                Console.WriteLine($"  {key,-20} → [{string.Join(", ", nodes)}]");
            }

            // Show vnode distribution across nodes
            var dist = ring.GetLoadDistribution();
            Console.WriteLine("\nVirtual node distribution (should be ~equal):");
            foreach (var kv in dist.OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key}: {kv.Value} vnodes ({kv.Value * 100 / dist.Values.Sum()}%)");

            // Add a 4th node — show minimal redistribution
            Console.WriteLine("\nAdding NodeD...");
            ring.AddNode("NodeD");
            Console.WriteLine("Key → Primary Node mapping after NodeD added:");
            foreach (string key in keys)
                Console.WriteLine($"  {key,-20} → {ring.GetNode(key)}");

            Console.WriteLine();
        }

        // ── Scenario 2: Basic Read/Write on Single Node ────────────────────────

        static void Scenario2_BasicReadWrite()
        {
            Console.WriteLine("─── Scenario 2: Basic Read / Write (Single Node LSM) ───");

            var node = new KvNode("Node1");

            // Write several keys
            node.Put("user:1", "{name:Alice,age:30}");
            node.Put("user:2", "{name:Bob,age:25}");
            node.Put("user:3", "{name:Carol,age:35}");

            Console.WriteLine("After 3 writes:");
            PrintGet(node, "user:1");
            PrintGet(node, "user:2");
            PrintGet(node, "user:99"); // missing key

            // Overwrite a key
            node.Put("user:1", "{name:Alice,age:31}"); // birthday!
            Console.WriteLine("\nAfter overwriting user:1 (age 30→31):");
            PrintGet(node, "user:1");

            // Delete a key (tombstone)
            node.Delete("user:2");
            Console.WriteLine("\nAfter deleting user:2:");
            PrintGet(node, "user:2");

            // Force flush to SSTable and verify reads still work
            node.ForceFlush();
            var (memCount, l0Count) = node.GetStats();
            Console.WriteLine($"\nAfter flushing MemTable → SSTable: memTable={memCount}, L0 SSTables={l0Count}");
            Console.WriteLine("Reads still work from SSTable:");
            PrintGet(node, "user:1");
            PrintGet(node, "user:3");

            Console.WriteLine();
        }

        // ── Scenario 3: Quorum Write + Read Repair ─────────────────────────────

        static void Scenario3_QuorumWriteAndReadRepair()
        {
            Console.WriteLine("─── Scenario 3: Distributed Store — Quorum Write & Read Repair ───");

            var store = new DistributedKvStore(replicationFactor: 3, writeQuorum: 2, readQuorum: 2);
            store.AddNode("NodeA");
            store.AddNode("NodeB");
            store.AddNode("NodeC");

            // Show responsible nodes for a key
            var nodes = store.GetResponsibleNodes("session:42");
            Console.WriteLine($"Nodes responsible for 'session:42': [{string.Join(", ", nodes)}]");

            // Write with quorum
            bool ok = store.Put("session:42", "user=alice;exp=3600");
            Console.WriteLine($"\nPUT 'session:42' (W=2 quorum): {(ok ? "OK" : "FAILED")}");

            var (found, val) = store.Get("session:42");
            Console.WriteLine($"GET 'session:42': found={found}, value={val}");

            // Simulate stale replica by directly writing older version to one node
            // (In practice, this happens due to network delay before replication arrives)
            Console.WriteLine("\nSimulating stale replica (NodeA gets an older write for 'profile:7'):");
            // Write profile:7 through the store (goes to quorum)
            store.Put("profile:7", "v1_initial");

            // Now write a newer version
            store.Put("profile:7", "v2_updated");

            // Read will get v2 back and trigger read repair on any stale replica
            var (f2, v2) = store.Get("profile:7");
            Console.WriteLine($"GET 'profile:7' (quorum read, returns latest): found={f2}, value={v2}");
            Console.WriteLine("(Read repair runs async to bring stale replicas to v2)\n");
        }

        // ── Scenario 4: TTL Expiry ─────────────────────────────────────────────

        static void Scenario4_TtlExpiry()
        {
            Console.WriteLine("─── Scenario 4: TTL Expiry ───");

            var node = new KvNode("Node1");

            // Write a key with 1-second TTL
            node.Put("rate-limit:user:42", "requests=5", ttlSeconds: 1);
            node.Put("permanent:config", "max_connections=100"); // no TTL

            Console.WriteLine("Immediately after write:");
            PrintGet(node, "rate-limit:user:42");
            PrintGet(node, "permanent:config");

            Console.WriteLine("\nWaiting 1.1 seconds for TTL to expire...");
            Thread.Sleep(1100);

            Console.WriteLine("After TTL expiry:");
            PrintGet(node, "rate-limit:user:42"); // should be expired
            PrintGet(node, "permanent:config");  // should still be there

            Console.WriteLine();
        }

        // ── Scenario 5: Node Failure & Recovery ───────────────────────────────

        static void Scenario5_NodeFailureAndRecovery()
        {
            Console.WriteLine("─── Scenario 5: Node Failure and Recovery ───");

            var store = new DistributedKvStore(replicationFactor: 3, writeQuorum: 2, readQuorum: 2);
            store.AddNode("NodeA");
            store.AddNode("NodeB");
            store.AddNode("NodeC");

            // Pre-populate
            store.Put("order:100", "status=pending");
            store.Put("order:200", "status=shipped");

            Console.WriteLine("Before failure:");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:200");

            // Simulate NodeA going down
            string failedNode = store.GetResponsibleNodes("order:100").First();
            Console.WriteLine($"\nTaking down {failedNode}...");
            store.SimulateNodeDown(failedNode);

            // Reads still work with remaining 2 nodes (R=2 quorum satisfied)
            Console.WriteLine("After failure (R=2 from 2 remaining nodes):");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:200");

            // Write during failure — succeeds with W=2 quorum
            bool writeOk = store.Put("order:300", "status=new");
            Console.WriteLine($"\nPUT 'order:300' during failure (W=2): {(writeOk ? "OK" : "FAILED")}");

            // Simulate NodeA recovery
            Console.WriteLine($"\n{failedNode} comes back online...");
            store.SimulateNodeUp(failedNode);

            // All reads continue to work
            Console.WriteLine("After recovery:");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:300");

            Console.WriteLine("\n(In a real system, NodeA would receive hinted-handoff writes");
            Console.WriteLine(" that were buffered while it was down, then converge to latest state.)");
        }

        static void PrintGet(KvNode node, string key)
        {
            var (found, value, ts) = node.Get(key);
            Console.WriteLine(found
                ? $"  GET {key,-25} → \"{value}\" (ts={ts})"
                : $"  GET {key,-25} → NOT FOUND");
        }

        static void PrintStoreGet(DistributedKvStore store, string key)
        {
            var (found, value) = store.Get(key);
            Console.WriteLine(found
                ? $"  GET {key,-20} → \"{value}\""
                : $"  GET {key,-20} → NOT FOUND");
        }
    }
}
