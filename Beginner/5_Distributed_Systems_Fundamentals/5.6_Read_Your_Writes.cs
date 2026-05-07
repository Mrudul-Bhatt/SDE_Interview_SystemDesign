// Q3. Simulate Replication Lag and Read-Your-Writes Consistency
// Model a master-replica system where writes go to the master and reads go to
// replicas. Show stale reads caused by replication lag, then fix it with the
// read-your-writes pattern.
//
// Consistency levels (weakest → strongest):
//
// 1. Eventual Consistency:
//    → All nodes converge eventually if no new writes happen
//    → Stale reads possible at any time
//    → Example: DNS propagation, Cassandra default
//
// 2. Read-Your-Writes:
//    → After you write, YOU always see your own write
//    → Other clients may still see stale data — that is acceptable
//    → Fix: route the writing client's reads to master for a short window
//    → Used by: Facebook (sticky routing), PostgreSQL read replicas
//
// 3. Monotonic Read:
//    → Once you read a value, you never read an older one
//    → Fix: pin a client session to the same replica for its lifetime
//    → Example: Stick user to replica R2 for the whole session
//
// 4. Strong / Linearizable (strongest):
//    → Every read sees the latest write globally
//    → Requires quorum (W+R>N) or single-master routing
//    → Example: ZooKeeper, etcd, Google Spanner
//    → Cost: higher latency, lower availability
//
// Rule of thumb for interviews:
//   Money / inventory / auth tokens → Strong consistency
//   Profile views / feeds / counts  → Eventual + read-your-writes is enough
//
// What interviewers test:
//   1. Quorum — can you derive the W+R>N rule and explain the overlap guarantee?
//   2. LWW — what is the lost update problem? What breaks with clock skew?
//   3. Replication lag — what is read-your-writes and how do you implement it?
//   4. CAP — what happens when nodes go down in a quorum store?
//      Eventual (W=1,R=1): still serves with 1 node up
//      Strong (W=2,R=2): refuses both reads+writes with only 1 node (CP)
//
// Complexity:
//   Write O(1), Read O(1), ReplicateAll O(keys on master)

namespace DistributedSystems
{

    // ---------------------------------------------------------------------------
    // ReplicationLagSimulator — models a single master + one async replica
    // ---------------------------------------------------------------------------
    public class ReplicationLagSimulator
    {
        // Master receives all writes immediately — single source of truth.
        private readonly Dictionary<string, string> _master = new();

        // Replica is updated asynchronously — it can be behind by any number of writes.
        // In a real system this is a separate process (or machine) applying a replication log.
        private readonly Dictionary<string, string> _replica = new();

        // Tracks keys this client wrote since their last write-tracking reset.
        // Used by ReadWithConsistency() to decide whether to route to master or replica.
        // In production this would be per-session, stored in a cookie or JWT claim,
        // not a shared in-memory set — but in-memory works for a single-client demo.
        private readonly HashSet<string> _recentWrites = new();

        // Counter of writes not yet replicated — used only for the stale warning in logs.
        // A real system tracks this via replication log sequence numbers, not a counter.
        private int _pendingReplicationCount = 0;

        public void Write(string key, string value)
        {
            // All writes go to master only. The replica learns about them only when
            // ReplicateAll() is called — simulating the async replication delay.
            _master[key] = value;

            // Record that this client wrote this key so ReadWithConsistency() knows
            // to route their next read to master instead of the (potentially stale) replica.
            _recentWrites.Add(key);

            _pendingReplicationCount++;
            Console.WriteLine($"[MASTER]  WRITE key={key} value='{value}'  " +
                              $"(replica lag: {_pendingReplicationCount} pending)");
        }

        // Simulate the replica's background replication thread catching up.
        // In a real system: MySQL binlog, PostgreSQL WAL streaming, Kafka topic replay.
        // We copy the entire master here; a real system would apply only the delta (new writes).
        public void ReplicateAll()
        {
            foreach (var (key, value) in _master)
                _replica[key] = value;

            Console.WriteLine($"[REPLICA] Caught up — {_pendingReplicationCount} write(s) replicated");
            _pendingReplicationCount = 0;
        }

        // Naive read — always hits the replica.
        // Fast and scalable (replicas can be scaled out horizontally), but stale
        // when the replica hasn't caught up yet.
        public string? ReadFromReplica(string key)
        {
            _replica.TryGetValue(key, out var value);

            // Warn in the log if we know the replica is behind — helps diagnose
            // "I just updated my profile but don't see the change" bugs.
            string lag = _pendingReplicationCount > 0 ? " ⚠ STALE" : "";
            Console.WriteLine($"[REPLICA] READ key={key} → '{value ?? "null"}'{lag}");
            return value;
        }

        // Read-your-writes: route to master only if THIS CLIENT recently wrote the key.
        // Everyone else still reads from the (cheaper, scalable) replica.
        // This is the minimal fix — we don't force ALL users to pay the master's latency,
        // only the one who made the write and needs to see it immediately.
        public string? ReadWithConsistency(string key)
        {
            if (_recentWrites.Contains(key))
            {
                // This client wrote the key — go to master to guarantee freshness.
                _master.TryGetValue(key, out var fresh);
                Console.WriteLine($"[MASTER]  READ key={key} → '{fresh}' (read-your-writes routing)");
                return fresh;
            }

            // Different client (or write tracking cleared) — replica is fine.
            return ReadFromReplica(key);
        }

        // Call this when the write-tracking window expires (e.g. 10 seconds after the write)
        // or when switching to a different client session.
        // After clearing, reads go back to the replica — the window where we guaranteed
        // read-your-writes has closed (replica should have caught up by now).
        public void ClearWriteTracking() => _recentWrites.Clear();
    }

    // ---------------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            // ===================================================================
            // Scenario 1 — Stale read with no fix
            // User uploads an avatar. Replica hasn't caught up. User refreshes
            // immediately and sees the old (or missing) avatar.
            // ===================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Stale read (no consistency fix)        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            var sim1 = new ReplicationLagSimulator();

            sim1.Write("user:1:avatar", "avatar_v1.png");

            Console.WriteLine("\nUser immediately reads their own profile page:");
            sim1.ReadFromReplica("user:1:avatar"); // ⚠ STALE — replica hasn't caught up yet

            Console.WriteLine("\nUser refreshes after 50ms (replica has now caught up):");
            sim1.ReplicateAll();
            sim1.ReadFromReplica("user:1:avatar"); // fresh — replica is now in sync

            // ===================================================================
            // Scenario 2 — Read-your-writes fix
            // Same upload, but the writing user's reads are routed to master.
            // Other users still read from the replica (acceptable stale).
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Read-your-writes consistency           ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            var sim2 = new ReplicationLagSimulator();

            sim2.Write("user:1:avatar", "avatar_v2.png");

            Console.WriteLine("\nSame user reads their own profile immediately after upload:");
            sim2.ReadWithConsistency("user:1:avatar"); // → routed to master: sees fresh data

            Console.WriteLine("\nDifferent user reads the same profile (stale is acceptable for them):");
            sim2.ClearWriteTracking(); // simulate: this is a different client session
            sim2.ReadWithConsistency("user:1:avatar"); // → goes to replica (may be stale)

            Console.WriteLine("\nReplica catches up (background replication):");
            sim2.ReplicateAll();

            Console.WriteLine("\nDifferent user reads again (replica now fresh):");
            sim2.ReadWithConsistency("user:1:avatar"); // → replica, now up to date

            // ===================================================================
            // Scenario 3 — Multiple writes before replication
            // Show that _pendingReplicationCount tracks the backlog correctly
            // and all writes are visible from master even before replication.
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Multiple writes before replica sync    ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            var sim3 = new ReplicationLagSimulator();

            sim3.Write("user:1:name", "Alice");
            sim3.Write("user:1:avatar", "avatar_v3.png");
            sim3.Write("user:1:bio", "Engineer @ FAANG");

            Console.WriteLine("\nUser reads all three keys immediately (routed to master):");
            sim3.ReadWithConsistency("user:1:name");
            sim3.ReadWithConsistency("user:1:avatar");
            sim3.ReadWithConsistency("user:1:bio");

            Console.WriteLine("\nReplica syncs all 3 writes at once:");
            sim3.ReplicateAll();

            Console.WriteLine("\nReplica is now fresh for all keys:");
            sim3.ClearWriteTracking();
            sim3.ReadWithConsistency("user:1:name");
            sim3.ReadWithConsistency("user:1:avatar");
            sim3.ReadWithConsistency("user:1:bio");
        }
    }

} // namespace DistributedSystems
