// Program — five demo scenarios that exercise the full Distributed KV Store stack.
//
// Reading order for the underlying code:
//   Core/ConsistentHashRing.cs   — maps keys to nodes via virtual-node ring
//   Core/BloomFilter.cs          — per-SSTable probabilistic membership filter
//   Models/StorageEntry.cs       — immutable value wrapper (timestamp, TTL, tombstone)
//   Storage/MemTable.cs          — sorted in-memory write buffer (first LSM tier)
//   Storage/SSTable.cs           — immutable sorted on-disk file (second LSM tier)
//   Service/KvNode.cs            — single node: MemTable + SSTables + logical clock
//   Service/DistributedKvStore.cs — cluster: ring routing + quorum + read repair
//
// Each scenario is self-contained and demonstrates one concept:
//   1. Consistent hashing ring  — how keys are routed to nodes, vnode balance
//   2. Single-node LSM          — write → MemTable → SSTable, overwrite, delete
//   3. Quorum reads/writes      — W+R>N guarantee, read repair on stale replicas
//   4. TTL expiry               — lazy expiry checked on read, not background scan
//   5. Node failure & recovery  — cluster stays available when one of three nodes is down

using System;
using System.Linq;
using System.Threading;

namespace AdvancedDesigns
{
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
        //
        // WHAT THIS SHOWS:
        // The ring maps every key to the same node deterministically — NodeA always
        // owns "user:1", regardless of which process or machine asks. Adding NodeD
        // only moves the keys whose position on the ring sits between NodeD and its
        // predecessor; the rest stay put. With plain hash-modulo, adding NodeD would
        // remap virtually every key.
        //
        // WHAT TO LOOK FOR:
        //   - Each key maps to exactly one primary node.
        //   - RF=3 replica list contains 3 distinct physical nodes.
        //   - Vnode counts are close to 150 each (law of large numbers at 150 vnodes/node).
        //   - After adding NodeD, only a minority of keys change their primary node.
        static void Scenario1_ConsistentHashingDistribution()
        {
            Console.WriteLine("─── Scenario 1: Consistent Hashing Ring ───");

            var ring = new ConsistentHashRing(virtualNodes: 150);
            ring.AddNode("NodeA");
            ring.AddNode("NodeB");
            ring.AddNode("NodeC");

            // Each key hashes to a position on the 0–4B ring and walks clockwise
            // to the nearest vnode. Same key always lands on the same physical node.
            string[] keys = ["user:1", "user:2", "user:3", "cart:42", "session:99", "profile:7"];
            Console.WriteLine("Key → Primary Node mapping:");
            foreach (string key in keys)
                Console.WriteLine($"  {key,-20} → {ring.GetNode(key)}");

            // Replication: walk clockwise past the primary to find 2 more DISTINCT
            // physical nodes. These are the nodes that store backup copies (RF=3).
            Console.WriteLine("\nKey → RF=3 replica nodes:");
            foreach (string key in new[] { "user:1", "cart:42" })
                Console.WriteLine($"  {key,-20} → [{string.Join(", ", ring.GetNodes(key, 3))}]");

            // With 150 vnodes per node, each node should own ~33% of the ring.
            // The exact count varies by hash luck, but should be within ~10% of 150.
            var dist = ring.GetLoadDistribution();
            Console.WriteLine("\nVirtual node distribution (should be ~equal):");
            foreach (var kv in dist.OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key}: {kv.Value} vnodes ({kv.Value * 100 / dist.Values.Sum()}%)");

            // Adding a 4th node: only keys whose ring arc falls between NodeD and its
            // predecessor migrate to NodeD. ~25% move; ~75% are unaffected.
            Console.WriteLine("\nAdding NodeD...");
            ring.AddNode("NodeD");
            Console.WriteLine("Key → Primary Node mapping after NodeD added:");
            foreach (string key in keys)
                Console.WriteLine($"  {key,-20} → {ring.GetNode(key)}");

            Console.WriteLine();
        }

        // ── Scenario 2: Basic Read/Write on Single Node ────────────────────────
        //
        // WHAT THIS SHOWS:
        // The LSM write path on a single KvNode: writes land in the MemTable first,
        // overwrite by writing a newer-timestamp entry (old stays underneath),
        // delete by writing a TombstoneEntry, and flush moves everything to an SSTable.
        // Reads work identically before and after flush — the layers are transparent.
        //
        // WHAT TO LOOK FOR:
        //   - user:99 returns NOT FOUND (never written).
        //   - user:1 overwrite: the new value (age=31) is returned, not the old one.
        //   - user:2 delete: returns NOT FOUND even though the original write is still
        //     physically in the MemTable as a tombstone.
        //   - After ForceFlush: memTable count drops to 0, L0 count rises to 1.
        //   - Reads after flush still return correct values from the SSTable.
        static void Scenario2_BasicReadWrite()
        {
            Console.WriteLine("─── Scenario 2: Basic Read / Write (Single Node LSM) ───");

            var node = new KvNode("Node1");

            // Three writes → three entries in the MemTable, sorted by key.
            node.Put("user:1", "{name:Alice,age:30}");
            node.Put("user:2", "{name:Bob,age:25}");
            node.Put("user:3", "{name:Carol,age:35}");

            Console.WriteLine("After 3 writes:");
            PrintGet(node, "user:1");
            PrintGet(node, "user:2");
            PrintGet(node, "user:99"); // deliberately missing — should print NOT FOUND

            // Overwrite: a new StorageEntry for "user:1" with a higher timestamp lands
            // in the MemTable on top of the old one. The old entry becomes invisible.
            node.Put("user:1", "{name:Alice,age:31}");
            Console.WriteLine("\nAfter overwriting user:1 (age 30→31):");
            PrintGet(node, "user:1");

            // Delete: writes a TombstoneEntry for "user:2". The MemTable still holds
            // both the original value and the tombstone, but reads return the tombstone
            // first (it has a higher timestamp) and report NOT FOUND.
            node.Delete("user:2");
            Console.WriteLine("\nAfter deleting user:2:");
            PrintGet(node, "user:2");

            // ForceFlush: freeze the current MemTable into an immutable L0 SSTable,
            // then wipe the MemTable. Now memTableCount=0, l0Count=1.
            node.ForceFlush();
            var (memCount, l0Count) = node.GetStats();
            Console.WriteLine($"\nAfter flushing MemTable → SSTable: memTable={memCount}, L0 SSTables={l0Count}");

            // The read path is transparent: KvNode checks MemTable (empty) then falls
            // through to the L0 SSTables and finds the values there.
            Console.WriteLine("Reads still work from SSTable:");
            PrintGet(node, "user:1");
            PrintGet(node, "user:3");

            Console.WriteLine();
        }

        // ── Scenario 3: Quorum Write + Read Repair ─────────────────────────────
        //
        // WHAT THIS SHOWS:
        // The distributed store fans writes out to W=2 nodes and reads from R=2 nodes.
        // W+R=4 > N=3, so the read quorum always overlaps with the write quorum by at
        // least one node — guaranteeing the latest write is always visible.
        // Read repair fixes any stale replica silently during the read.
        //
        // WHAT TO LOOK FOR:
        //   - PUT returns OK when 2 of 3 nodes acknowledge.
        //   - GET returns the value written (quorum overlap guarantees this).
        //   - Writing "v1" then "v2" for profile:7: the GET returns "v2" (highest
        //     timestamp wins across the R=2 responses). Any node that returned "v1"
        //     gets silently repaired to "v2" as a side effect of the read.
        static void Scenario3_QuorumWriteAndReadRepair()
        {
            Console.WriteLine("─── Scenario 3: Distributed Store — Quorum Write & Read Repair ───");

            var store = new DistributedKvStore(replicationFactor: 3, writeQuorum: 2, readQuorum: 2);
            store.AddNode("NodeA");
            store.AddNode("NodeB");
            store.AddNode("NodeC");

            // Show which 3 nodes the ring assigns to this key — all 3 will receive
            // writes for this key, but only W=2 acks are needed to call it done.
            var nodes = store.GetResponsibleNodes("session:42");
            Console.WriteLine($"Nodes responsible for 'session:42': [{string.Join(", ", nodes)}]");

            bool ok = store.Put("session:42", "user=alice;exp=3600");
            Console.WriteLine($"\nPUT 'session:42' (W=2 quorum): {(ok ? "OK" : "FAILED")}");

            var (found, val) = store.Get("session:42");
            Console.WriteLine($"GET 'session:42': found={found}, value={val}");

            // Two sequential writes for the same key: v1 lands on some replicas at ts=T1,
            // v2 lands on the same (or different) replicas at ts=T2 > T1. The quorum
            // read picks the response with the highest timestamp (T2 → v2). Any replica
            // that still holds v1 gets silently updated to v2 by read repair.
            Console.WriteLine("\nSimulating stale replica (NodeA gets an older write for 'profile:7'):");
            store.Put("profile:7", "v1_initial");
            store.Put("profile:7", "v2_updated");

            var (f2, v2) = store.Get("profile:7");
            Console.WriteLine($"GET 'profile:7' (quorum read, returns latest): found={f2}, value={v2}");
            Console.WriteLine("(Read repair runs async to bring stale replicas to v2)\n");
        }

        // ── Scenario 4: TTL Expiry ─────────────────────────────────────────────
        //
        // WHAT THIS SHOWS:
        // TTL is stored as an absolute expiry deadline (DateTime) in the StorageEntry.
        // Expiry is lazy — no background scanner. The check happens in KvNode.Get at
        // read time. The key is physically still in the MemTable or SSTable after the
        // TTL passes; it just becomes invisible on reads. Compaction eventually cleans
        // up the expired entries from disk.
        //
        // WHAT TO LOOK FOR:
        //   - Immediately after write: rate-limit key is visible (TTL not yet elapsed).
        //   - After 1.1s sleep: rate-limit key returns NOT FOUND (deadline passed).
        //   - permanent:config is unaffected — no ExpiresAt set, IsExpired is always false.
        static void Scenario4_TtlExpiry()
        {
            Console.WriteLine("─── Scenario 4: TTL Expiry ───");

            var node = new KvNode("Node1");
            // ttlSeconds=1 converts to ExpiresAt = UtcNow + 1s stored in StorageEntry.
            node.Put("rate-limit:user:42", "requests=5", ttlSeconds: 1);
            node.Put("permanent:config",   "max_connections=100");

            Console.WriteLine("Immediately after write:");
            PrintGet(node, "rate-limit:user:42");   // visible — deadline not yet passed
            PrintGet(node, "permanent:config");

            // Sleep past the TTL deadline. The key is still physically in the MemTable;
            // the next Get will check IsExpired and return NOT FOUND.
            Console.WriteLine("\nWaiting 1.1 seconds for TTL to expire...");
            Thread.Sleep(1100);

            Console.WriteLine("After TTL expiry:");
            PrintGet(node, "rate-limit:user:42");   // NOT FOUND — IsExpired is now true
            PrintGet(node, "permanent:config");      // still visible — no TTL set

            Console.WriteLine();
        }

        // ── Scenario 5: Node Failure & Recovery ───────────────────────────────
        //
        // WHAT THIS SHOWS:
        // With N=3, W=2, R=2, the cluster tolerates ONE node being down:
        //   - Reads can still reach R=2 from the 2 healthy nodes.
        //   - Writes can still reach W=2 from the 2 healthy nodes.
        // Losing a SECOND node would drop below quorum and all operations would fail.
        //
        // After the failed node recovers (SimulateNodeUp), it may be stale — it missed
        // writes that happened while it was down. In a real system (Cassandra/Dynamo),
        // hinted handoff replays those writes automatically. Here, read repair during
        // the next GET will converge the recovered node to the latest value.
        //
        // WHAT TO LOOK FOR:
        //   - Before failure: all reads return values.
        //   - After SimulateNodeDown: reads still succeed (2 healthy nodes satisfy R=2).
        //   - PUT during failure still succeeds (2 healthy nodes satisfy W=2).
        //   - After SimulateNodeUp: reads still work; the recovered node catches up
        //     on next read-repair cycle.
        static void Scenario5_NodeFailureAndRecovery()
        {
            Console.WriteLine("─── Scenario 5: Node Failure and Recovery ───");

            var store = new DistributedKvStore(replicationFactor: 3, writeQuorum: 2, readQuorum: 2);
            store.AddNode("NodeA");
            store.AddNode("NodeB");
            store.AddNode("NodeC");

            store.Put("order:100", "status=pending");
            store.Put("order:200", "status=shipped");

            Console.WriteLine("Before failure:");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:200");

            // Find which node is primary for order:100, then kill it.
            // The other two nodes still satisfy the quorum of 2.
            string failedNode = store.GetResponsibleNodes("order:100").First();
            Console.WriteLine($"\nTaking down {failedNode}...");
            store.SimulateNodeDown(failedNode);

            Console.WriteLine("After failure (R=2 from 2 remaining nodes):");
            PrintStoreGet(store, "order:100");  // still works — 2 healthy nodes remain
            PrintStoreGet(store, "order:200");

            // Write during failure: only 2 nodes get the write, but W=2 is satisfied.
            // The failed node will miss this write until it recovers.
            bool writeOk = store.Put("order:300", "status=new");
            Console.WriteLine($"\nPUT 'order:300' during failure (W=2): {(writeOk ? "OK" : "FAILED")}");

            // Bring the node back online. It is stale (missed order:300 and possibly
            // updates to other keys). The next GET will trigger read repair where needed.
            Console.WriteLine($"\n{failedNode} comes back online...");
            store.SimulateNodeUp(failedNode);

            Console.WriteLine("After recovery:");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:300");

            Console.WriteLine("\n(In a real system, NodeA would receive hinted-handoff writes");
            Console.WriteLine(" that were buffered while it was down, then converge to latest state.)");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // Single-node read: prints value + logical timestamp, or NOT FOUND.
        // The timestamp is the logical clock value assigned at write time — useful
        // for verifying that overwrites produce strictly higher timestamps.
        static void PrintGet(KvNode node, string key)
        {
            var (found, value, ts) = node.Get(key);
            Console.WriteLine(found
                ? $"  GET {key,-25} → \"{value}\" (ts={ts})"
                : $"  GET {key,-25} → NOT FOUND");
        }

        // Distributed store read: prints value or NOT FOUND.
        // No timestamp shown — the store hides the internal quorum timestamp from callers.
        static void PrintStoreGet(DistributedKvStore store, string key)
        {
            var (found, value) = store.Get(key);
            Console.WriteLine(found
                ? $"  GET {key,-20} → \"{value}\""
                : $"  GET {key,-20} → NOT FOUND");
        }
    }
}
