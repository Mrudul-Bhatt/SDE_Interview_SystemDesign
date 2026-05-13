// Q1. Implement a Quorum Read/Write System
// Given N replicas, a write succeeds if W nodes confirm it. A read succeeds
// if R nodes respond. Strong consistency is guaranteed when W + R > N.
//
// N = total replicas
// W = write quorum (nodes that must confirm a write)
// R = read quorum (nodes that must respond to a read)
//
// W + R > N  →  the read set and write set must overlap by at least 1 node
//            →  that overlapping node always has the latest write
//            →  strong consistency guaranteed
//
// Examples with N=3:
//   Eventual:  W=1, R=1  → W+R=2 ≤ 3, fast, stale reads possible
//   Quorum:    W=2, R=2  → W+R=4 > 3, strong consistency, tolerates 1 failure
//   Strong:    W=3, R=1  → all nodes confirm, any single read is current
//
// Real-world guidance:
//   Payment / booking:   W=2, R=2  → balanced: tolerates 1 failure, still consistent
//   Social feed:         W=1, R=1  → fastest, stale reads acceptable
//   Analytics counters:  W=1, R=1  → losing a count increment is acceptable
//
// Complexity: Write O(N), Read O(N) — must contact all replicas;
//             quorum is about how many must RESPOND, not just be contacted

using System;
using System.Collections.Generic;
using System.Linq;

namespace DistributedSystems
{

    // ---------------------------------------------------------------------------
    // Replica — represents a single node in the distributed store
    // ---------------------------------------------------------------------------
    public class Replica
    {
        // init-only: Id is set at construction and never changed.
        // Nodes in a real cluster have stable, immutable identifiers (IP, hostname, UUID).
        public string Id { get; init; } = "";

        // Flipped externally to simulate a node going down without deleting its data.
        // Real systems detect this via heartbeat timeouts, not a boolean flag.
        public bool IsHealthy { get; set; } = true;

        // Each key stores (value, timestamp) so the coordinator can pick the most
        // recent write when multiple replicas respond with different values (divergence).
        // We need the timestamp here, not just the value, because the coordinator must
        // compare timestamps across replicas to decide which write "won".
        private readonly Dictionary<string, (string Value, long Timestamp)> _data = new();

        public bool Write(string key, string value, long timestamp)
        {
            // Reject writes if the node is down — simulates a network partition or crash.
            // In reality, the coordinator gets a timeout/connection error; we model that as false.
            if (!IsHealthy) return false;

            _data[key] = (value, timestamp);
            return true;
        }

        public (string? Value, long Timestamp) Read(string key)
        {
            // An unhealthy node cannot serve reads — return sentinel (-1 timestamp)
            // so the coordinator knows this response doesn't count toward quorum.
            if (!IsHealthy) return (null, -1);

            // TryGetValue avoids a KeyNotFoundException on missing keys.
            // Return (null, -1) if the key has never been written to this replica.
            return _data.TryGetValue(key, out var entry) ? entry : (null, -1);
        }
    }

    // ---------------------------------------------------------------------------
    // QuorumStore — coordinator that enforces W/R quorum rules
    // ---------------------------------------------------------------------------
    public class QuorumStore
    {
        private readonly List<Replica> _replicas;
        private readonly int _w; // write quorum — minimum confirmations needed
        private readonly int _r; // read quorum — minimum responses needed
        private readonly int _n; // total replicas — used only for logging

        public QuorumStore(int n, int w, int r)
        {
            _n = n; _w = w; _r = r;

            // Build N replicas up front. In a real system these would be remote nodes
            // discovered via a service registry; here we instantiate them in-process.
            _replicas = Enumerable.Range(1, n)
                .Select(i => new Replica { Id = $"node_{i}" })
                .ToList();

            // Log whether W+R>N holds so it's immediately visible when tuning parameters.
            Console.WriteLine($"[CONFIG] N={n}, W={w}, R={r}  " +
                              $"→ Strong consistency: {(w + r > n ? "YES" : "NO")} " +
                              $"(W+R={w + r} {(w + r > n ? ">" : "≤")} N={n})");
        }

        public bool Write(string key, string value)
        {
            // Use wall-clock milliseconds as the write timestamp.
            // All replicas that accept this write store the same timestamp, so
            // a reader comparing timestamps across replicas can identify the latest write.
            // (In production, logical clocks or Hybrid Logical Clocks are preferred
            // because wall clocks can skew across machines.)
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int confirmed = 0;

            // Broadcast to ALL replicas — we want as many copies as possible.
            // 'confirmed' only counts the ones that actually accepted (i.e. were healthy).
            foreach (var replica in _replicas)
            {
                if (replica.Write(key, value, timestamp))
                    confirmed++;
            }

            // The write is only durable if at least W nodes confirmed it.
            // Fewer than W means we can't guarantee a future quorum read will see it.
            bool success = confirmed >= _w;
            Console.WriteLine($"[WRITE] key={key} value={value}  " +
                              $"confirmed={confirmed}/{_n}  required={_w}  " +
                              $"→ {(success ? "SUCCESS" : "FAILED")}");
            return success;
        }

        public string? Read(string key)
        {
            var responses = new List<(string NodeId, string? Value, long Timestamp)>();

            // Ask ALL replicas — collect every response so we can pick the freshest.
            // We can't ask only R replicas because we don't know in advance which ones
            // are healthy or have the latest write.
            foreach (var replica in _replicas)
            {
                var (value, ts) = replica.Read(key);

                // Only count responses that actually have the key (value != null).
                // A replica that never received the write (due to a prior failure)
                // returns null and should not count toward quorum.
                if (value != null)
                    responses.Add((replica.Id, value, ts));
            }

            // If fewer than R nodes responded with data, we cannot guarantee we saw
            // the latest write — return null rather than risk serving a stale value.
            // This is CP behavior: prefer unavailability over inconsistency.
            if (responses.Count < _r)
            {
                Console.WriteLine($"[READ]  key={key}  responses={responses.Count}/{_n}  " +
                                  $"required={_r}  → FAILED (not enough nodes responded)");
                return null;
            }

            // Return the value with the highest timestamp — Last Write Wins (LWW).
            // This is why we store timestamps: two replicas might have different values
            // if a write was partially propagated; the most recent timestamp wins.
            var latest = responses.OrderByDescending(r => r.Timestamp).First();
            Console.WriteLine($"[READ]  key={key}  responses={responses.Count}/{_n}  " +
                              $"required={_r}  value={latest.Value} (from {latest.NodeId})  → SUCCESS");
            return latest.Value;
        }

        // Simulate a node crash — the node retains its data but stops accepting requests.
        public void MarkDown(int nodeIndex) => _replicas[nodeIndex].IsHealthy = false;

        // Simulate node recovery — it comes back with stale data (no catch-up here).
        public void MarkUp(int nodeIndex) => _replicas[nodeIndex].IsHealthy = true;
    }

    // ---------------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            // ===================================================================
            // Demo 1 — Eventual Consistency (W=1, R=1, W+R=2 ≤ N=3)
            // ===================================================================
            Console.WriteLine("╔══════════════════════════════════════════╗");
            Console.WriteLine("║  Eventual Consistency  (N=3, W=1, R=1)  ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");

            var eventual = new QuorumStore(n: 3, w: 1, r: 1);
            eventual.Write("user:1", "Alice");
            eventual.Read("user:1");  // reads from any 1 node → SUCCESS

            Console.WriteLine("\n--- Take 2 nodes down ---");
            eventual.MarkDown(1);
            eventual.MarkDown(2);
            eventual.Read("user:1"); // only node_1 up, R=1 → still SUCCESS (but could be stale)

            // ===================================================================
            // Demo 2 — Strong Consistency (W=2, R=2, W+R=4 > N=3)
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════╗");
            Console.WriteLine("║  Strong Consistency    (N=3, W=2, R=2)  ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");

            var strong = new QuorumStore(n: 3, w: 2, r: 2);
            strong.Write("balance", "1000");
            strong.Read("balance");  // 3 responses ≥ R=2 → SUCCESS

            Console.WriteLine("\n--- Take 2 nodes down ---");
            strong.MarkDown(1);
            strong.MarkDown(2);
            strong.Write("balance", "500"); // only 1 node up, need W=2 → FAILED
            strong.Read("balance");         // only 1 response, need R=2 → FAILED
                                            // System rejects rather than risk inconsistency (CP behavior)

            // ===================================================================
            // Demo 3 — Recovery: node comes back, fresh write is readable
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════╗");
            Console.WriteLine("║  Recovery Demo         (N=3, W=2, R=2)  ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");

            var recovery = new QuorumStore(n: 3, w: 2, r: 2);
            recovery.Write("seat", "available");

            Console.WriteLine("\n--- node_2 crashes before write ---");
            recovery.MarkDown(1); // node_2 goes down (0-indexed: index 1 = node_2)
            recovery.Write("seat", "booked"); // confirmed by node_1 + node_3 → W=2 ✓

            Console.WriteLine("\n--- node_2 comes back (has stale data) ---");
            recovery.MarkUp(1);
            // Read returns "booked" — the quorum overlap guarantees we see the latest write
            // even though node_2 still has "available"
            recovery.Read("seat");
        }
    }

} // namespace DistributedSystems
