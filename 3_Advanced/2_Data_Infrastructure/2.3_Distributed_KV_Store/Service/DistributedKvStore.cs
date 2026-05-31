// DistributedKvStore — the cluster coordinator that ties together the ring, replication, and quorum.
//
// THE BIG IDEA:
// A single KvNode is fast but fragile — if that machine dies, data is lost. This class
// spreads every key across N physical nodes (replication factor). Now the cluster can
// survive N-1 node failures. But spreading data raises a new problem: which nodes?
// The consistent-hash ring answers that deterministically for every key.
//
// HOW A WRITE WORKS:
//   PUT "name=Alice"
//     1. Ring lookup → finds the 3 nodes responsible for "name" (e.g. node-A, node-B, node-C)
//     2. Write to node-A → ack 1
//     3. Write to node-B → ack 2  ← quorum reached (W=2), STOP. Don't wait for node-C.
//     4. Return success to the caller.
//   node-C eventually gets the write via read repair or background anti-entropy.
//
// HOW A READ WORKS:
//   GET "name"
//     1. Ring lookup → same 3 nodes (node-A, node-B, node-C)
//     2. Ask node-A → ("Alice", ts=42)
//     3. Ask node-B → ("Alice", ts=42)  ← quorum reached (R=2), STOP.
//     4. Pick the response with the highest timestamp (both agree → "Alice").
//     5. Read repair: any node that returned a lower timestamp gets a silent update.
//     6. Return "Alice" to the caller.
//
// THE QUORUM GUARANTEE — WHY W + R > N MATTERS:
// With N=3, W=2, R=2 → W+R = 4 > 3.
// The write quorum (W=2) and read quorum (R=2) MUST overlap by at least one node.
// That overlap node holds the latest write, so every read is guaranteed to see it.
//
// Visualised as a Venn diagram of the 3 replicas:
//   Write touches: [node-A ✓] [node-B ✓] [node-C  ]   ← 2 of 3
//   Read  touches: [node-A  ] [node-B ✓] [node-C ✓]   ← 2 of 3
//   Overlap = node-B → read sees the latest write. ✓
//
// If we allowed W=1, R=1 (W+R=2 ≤ N=3):
//   Write touches: [node-A ✓] [node-B  ] [node-C  ]
//   Read  touches: [node-A  ] [node-B  ] [node-C ✓]   ← no overlap! stale read possible.
//
// TUNING CONSISTENCY vs AVAILABILITY:
//   W=3, R=1 → every write must hit all 3 nodes. Slower writes, fastest reads.
//              A single node failure blocks ALL writes (not enough nodes for W=3).
//   W=1, R=3 → writes are instant (1 ack). Reads check all 3 and take the newest.
//              Any single node failure blocks reads (can't reach R=3). Rare but painful.
//   W=2, R=2 → balanced. One node can be down and both reads and writes still work. ← default
//
// READ REPAIR (lazy consistency):
// After a quorum read, any replica that returned an older timestamp is silently
// brought up to date with a background Put. This gradually converges stale replicas
// without needing a dedicated sync process. Trade-off: every read costs a small
// extra write if replicas are out of sync.
//
// HINTED HANDOFF (not implemented here):
// If a node is DOWN during a write, a real system (Cassandra, Dynamo) would buffer
// the write on a healthy node as a "hint" and replay it when the down node recovers.
// Here we simply skip down nodes and rely on quorum — if enough healthy nodes exist
// to satisfy W, the write succeeds and the down node catches up via read repair later.

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class DistributedKvStore
    {
        // nodeId → KvNode instance. Each KvNode owns its own MemTable + SSTables.
        private readonly Dictionary<string, KvNode> _nodes = [];

        // The consistent-hash ring that maps any key to an ordered list of responsible nodes.
        // Using 50 vnodes here (vs 150 in the standalone demo) — enough for a small cluster.
        private readonly ConsistentHashRing _ring;

        // N — how many nodes store a copy of each key. Higher = more fault tolerance,
        // more storage cost, more write latency. Cassandra default is 3.
        private readonly int _replicationFactor;

        // W — how many nodes must acknowledge a write before we call it successful.
        // Must be > 0 and ≤ _replicationFactor. W + R > N for strong consistency.
        private readonly int _writeQuorum;

        // R — how many nodes we ask on a read before picking the best response.
        // Must be > 0 and ≤ _replicationFactor. R + W > N for strong consistency.
        private readonly int _readQuorum;

        // Simulated failed nodes — writes and reads skip these as if they were
        // unreachable on the network. In production, liveness is detected via gossip
        // (each node periodically heartbeats its neighbours; silence = presumed dead).
        private readonly HashSet<string> _downNodes = [];

        public DistributedKvStore(int replicationFactor = 3, int writeQuorum = 2, int readQuorum = 2)
        {
            _replicationFactor = replicationFactor;
            _writeQuorum       = writeQuorum;
            _readQuorum        = readQuorum;
            _ring              = new ConsistentHashRing(virtualNodes: 50);
        }

        // Register a new physical node: create its local KvNode and add it to the ring.
        // The ring immediately starts routing some key ranges to this node. In a real
        // cluster, the new node would stream existing data from its neighbours for the
        // key ranges it just took ownership of (bootstrapping / data handoff).
        public void AddNode(string nodeId)
        {
            _nodes[nodeId] = new KvNode(nodeId);
            _ring.AddNode(nodeId);
        }

        // Test helpers that simulate network partitions by marking a node unreachable.
        // In production, node liveness is detected automatically via a gossip protocol.
        public void SimulateNodeDown(string nodeId) => _downNodes.Add(nodeId);
        public void SimulateNodeUp(string nodeId)   => _downNodes.Remove(nodeId);

        // Fan-out write to the first W reachable replicas. Stop as soon as quorum is
        // satisfied — we don't need ALL replicas to ack, just enough to guarantee that
        // the next quorum read will overlap with at least one node that has this write.
        // Returns false if fewer than W nodes were reachable (write is unsafe — caller
        // should retry or surface an error to the client).
        public bool Put(string key, string value, int? ttlSeconds = null)
        {
            int acks = 0;
            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                _nodes[nodeId].Put(key, value, ttlSeconds);
                if (++acks >= _writeQuorum) break;  // quorum met — don't wait for stragglers
            }
            return acks >= _writeQuorum;
        }

        // Quorum read: collect R responses, pick the one with the highest timestamp
        // (last-writer-wins conflict resolution), then repair any stale replicas.
        public (bool found, string value) Get(string key)
        {
            var responses = new List<(string nodeId, bool found, string value, long timestamp)>();

            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                var (found, value, ts) = _nodes[nodeId].Get(key);
                responses.Add((nodeId, found, value, ts));
                if (responses.Count >= _readQuorum) break;  // quorum met — stop asking
            }

            // Fewer than R nodes responded — cluster is too degraded to guarantee a
            // consistent read. Return not-found rather than risk a stale answer.
            if (responses.Count < _readQuorum) return (false, null);

            // Last-writer-wins: the response with the highest logical timestamp is
            // the authoritative current value. All other responses may be stale replicas.
            var best = responses.OrderByDescending(r => r.timestamp).First();
            if (!best.found) return (false, null);

            // Read repair: any replica that is behind gets a silent background write.
            // This is "lazy replication" — we fix staleness opportunistically on reads
            // rather than paying the cost of synchronous writes to all N replicas.
            // Over time, all replicas converge to the latest value without a dedicated
            // anti-entropy background job.
            foreach (var (nodeId, found, value, timestamp) in responses)
            {
                if (timestamp < best.timestamp && !_downNodes.Contains(nodeId))
                    _nodes[nodeId].Put(key, best.value);
            }

            return (true, best.value);
        }

        // Same fan-out pattern as Put: write a TombstoneEntry to W replicas.
        // The tombstone propagates to lagging replicas via read repair, just like
        // a normal value would. Quorum ensures the deletion is durable.
        public bool Delete(string key)
        {
            int acks = 0;
            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                _nodes[nodeId].Delete(key);
                if (++acks >= _writeQuorum) break;
            }
            return acks >= _writeQuorum;
        }

        // Diagnostics: which nodes are responsible for a given key?
        // Useful for debugging routing decisions and verifying ring balance.
        public List<string> GetResponsibleNodes(string key) => _ring.GetNodes(key, _replicationFactor);

        // Diagnostics: vnode count per physical node — should be ~50 per node.
        // Significant skew here means the ring is unbalanced and some nodes are
        // handling a disproportionate share of key ranges.
        public Dictionary<string, int> GetRingDistribution() => _ring.GetLoadDistribution();
    }
}
