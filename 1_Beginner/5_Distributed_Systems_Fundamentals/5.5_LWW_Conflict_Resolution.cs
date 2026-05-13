// Q2. Implement Last Write Wins (LWW) Conflict Resolution
// In master-master replication, two nodes can accept conflicting writes to the same
// key simultaneously. Implement Last Write Wins using timestamps, detect conflicts,
// and show the risk of clock skew.
//
// Last Write Wins (LWW):
//   ✓ Simple to implement
//   ✓ Always converges — no permanent conflicts
//   ✗ Can silently discard writes (the lost update problem)
//   ✗ Vulnerable to clock skew — NTP sync must be <1ms
//   Used by: Cassandra (default), Redis (AOF conflicts)
//
// Alternatives:
//   CRDT (Conflict-free Replicated Data Types):
//     → Data structures designed to merge without conflicts
//     → Example: G-Counter (grow-only counter) — increment is always safe
//     → Used by: Redis CRDT, Riak, collaborative editors
//
//   Vector Clocks:
//     → Track causal history instead of wall-clock time
//     → Can detect "truly concurrent" vs "causally ordered" writes
//     → Used by: Amazon DynamoDB (behind the scenes), Riak
//
//   Application-defined merge:
//     → Developer writes custom conflict handler
//     → Example: shopping cart — merge both carts, deduplicate items
//
// Complexity: Write O(1), Merge O(1), Replicate O(1) per key

using System.Collections.Generic;
using System.Linq;

namespace DistributedSystems
{

    // ---------------------------------------------------------------------------
    // LWWRegister — a single master-master node using Last Write Wins
    // ---------------------------------------------------------------------------
    public class LWWRegister
    {
        // init-only: node identity is fixed at creation, same as a real server's hostname/IP.
        public string NodeId { get; init; } = "";

        // Store (value, timestamp, originNode) per key.
        // We keep originNode for logging — it tells us WHICH node's write survived,
        // which is critical for debugging lost-update bugs in production.
        private readonly Dictionary<string, (string Value, long Timestamp, string OriginNode)> _data = new();

        public void Write(string key, string value, long? timestampOverride = null)
        {
            // Use the real wall clock by default. timestampOverride exists only for
            // tests and demos where we need deterministic, controlled timestamps.
            // In production you'd never expose this — it's a test seam.
            long ts = timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _data[key] = (value, ts, NodeId);
            Console.WriteLine($"[{NodeId}] WRITE key={key} value={value} ts={ts}");
        }

        // Called when this node receives a write from another node during replication.
        // This is the heart of LWW: compare timestamps and keep only the higher one.
        public void Merge(string key, string value, long timestamp, string fromNode)
        {
            if (_data.TryGetValue(key, out var existing))
            {
                if (timestamp > existing.Timestamp)
                {
                    // Incoming write is newer — overwrite local value.
                    // The old local write is permanently discarded here (lost update risk).
                    Console.WriteLine($"[{NodeId}] MERGE key={key}: " +
                                      $"'{existing.Value}'(ts={existing.Timestamp}) " +
                                      $"← '{value}'(ts={timestamp} from {fromNode})  → ACCEPTED (newer)");
                    _data[key] = (value, timestamp, fromNode);
                }
                else
                {
                    // Local write is newer (or equal) — reject the incoming write.
                    // Equal timestamps are broken by rejecting the incoming write,
                    // which is a valid choice as long as it's applied consistently on
                    // all nodes (otherwise nodes could converge to different values).
                    Console.WriteLine($"[{NodeId}] MERGE key={key}: " +
                                      $"'{existing.Value}'(ts={existing.Timestamp}) " +
                                      $"← '{value}'(ts={timestamp} from {fromNode})  → REJECTED (older or equal)");
                }
            }
            else
            {
                // No local value for this key yet — accept unconditionally.
                // This handles the case where the remote node wrote first and we
                // never had a local write for this key.
                _data[key] = (value, timestamp, fromNode);
                Console.WriteLine($"[{NodeId}] MERGE key={key}: no local value → ACCEPTED '{value}'");
            }
        }

        public string? Get(string key) =>
            _data.TryGetValue(key, out var entry) ? entry.Value : null;

        // Push one key's current value to another node's Merge() method.
        // In reality this would be a network call (gRPC, HTTP PATCH, etc.).
        // We call it directly here to keep the demo in-process.
        public void ReplicateTo(LWWRegister other, string key)
        {
            if (_data.TryGetValue(key, out var entry))
                other.Merge(key, entry.Value, entry.Timestamp, NodeId);
        }

        public void PrintState() =>
            Console.WriteLine($"[{NodeId}] State: " +
                string.Join(", ", _data.Select(kv =>
                    $"{kv.Key}={kv.Value.Value}(ts={kv.Value.Timestamp})")));
    }

    // ---------------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            // ===================================================================
            // Scenario 1 — Normal replication (no conflict)
            // One node writes, then replicates to a second node that had no data.
            // ===================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Normal replication (no conflict)   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            var nodeA = new LWWRegister { NodeId = "MasterA" };
            var nodeB = new LWWRegister { NodeId = "MasterB" };

            nodeA.Write("email", "alice@old.com", timestampOverride: 1000);
            nodeA.ReplicateTo(nodeB, "email"); // nodeB has no local value → accepted
            nodeB.PrintState();

            // ===================================================================
            // Scenario 2 — Concurrent conflict (LWW resolution)
            // Both nodes write different values during a network partition.
            // When partition heals, both replicate — higher timestamp wins.
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Concurrent conflict (LWW wins)     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            var nodeC = new LWWRegister { NodeId = "MasterA" };
            var nodeD = new LWWRegister { NodeId = "MasterB" };

            // Two writes happen simultaneously during a network partition.
            nodeC.Write("email", "alice@version1.com", timestampOverride: 2000);
            nodeD.Write("email", "alice@version2.com", timestampOverride: 2050);

            Console.WriteLine("\n--- Partition heals, nodes exchange their writes ---");
            nodeC.ReplicateTo(nodeD, "email"); // MasterA sends ts=2000 → REJECTED (nodeD has ts=2050)
            nodeD.ReplicateTo(nodeC, "email"); // MasterB sends ts=2050 → ACCEPTED (nodeC had ts=2000)

            Console.WriteLine("\n--- Final state (both nodes should converge to same value) ---");
            nodeC.PrintState(); // → alice@version2.com (ts=2050 won)
            nodeD.PrintState(); // → alice@version2.com (ts=2050 won)
                                // version1 is permanently lost — this is the "lost update" problem of LWW.

            // ===================================================================
            // Scenario 3 — Clock skew danger
            // MasterA's clock is 200ms behind. The user writes version1 on MasterA
            // AFTER writing version2 on MasterB — but MasterA's skewed clock gives it
            // a lower timestamp, so the OLDER real-world write (version1) wins. Data loss.
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Clock skew — wrong winner          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            var nodeE = new LWWRegister { NodeId = "MasterA" };
            var nodeF = new LWWRegister { NodeId = "MasterB" };

            // Real-world order: version2 written first (real t=3100), then version1 (real t=3200).
            // But MasterA's clock is 200ms behind, so it records ts=3000 for a write that
            // actually happened at t=3200 — AFTER version2.
            nodeF.Write("email", "alice@version2.com", timestampOverride: 3100); // real t=3100 (first)
            nodeE.Write("email", "alice@version1.com", timestampOverride: 3000); // real t=3200 (second, but clock is behind!)

            Console.WriteLine("\n--- Replication with skewed clocks ---");
            nodeE.ReplicateTo(nodeF, "email"); // ts=3000 < ts=3100 → REJECTED (correct reject, wrong outcome)
            nodeF.ReplicateTo(nodeE, "email"); // ts=3100 > ts=3000 → ACCEPTED on MasterA

            Console.WriteLine("\n--- Final state ---");
            nodeE.PrintState(); // → alice@version2.com
            nodeF.PrintState(); // → alice@version2.com
            Console.WriteLine("\n⚠ Clock skew caused data loss:");
            Console.WriteLine("  version1 (written LAST in real time) lost to version2 (written FIRST).");
            Console.WriteLine("  Fix: use Hybrid Logical Clocks (HLC) or version vectors instead of wall time.");
        }
    }

} // namespace DistributedSystems
