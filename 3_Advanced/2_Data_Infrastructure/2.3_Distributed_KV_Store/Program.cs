// Program — entry point for all Distributed KV Store demo scenarios.

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

        static void Scenario1_ConsistentHashingDistribution()
        {
            Console.WriteLine("─── Scenario 1: Consistent Hashing Ring ───");

            var ring = new ConsistentHashRing(virtualNodes: 150);
            ring.AddNode("NodeA");
            ring.AddNode("NodeB");
            ring.AddNode("NodeC");

            string[] keys = ["user:1", "user:2", "user:3", "cart:42", "session:99", "profile:7"];
            Console.WriteLine("Key → Primary Node mapping:");
            foreach (string key in keys)
                Console.WriteLine($"  {key,-20} → {ring.GetNode(key)}");

            Console.WriteLine("\nKey → RF=3 replica nodes:");
            foreach (string key in new[] { "user:1", "cart:42" })
                Console.WriteLine($"  {key,-20} → [{string.Join(", ", ring.GetNodes(key, 3))}]");

            var dist = ring.GetLoadDistribution();
            Console.WriteLine("\nVirtual node distribution (should be ~equal):");
            foreach (var kv in dist.OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key}: {kv.Value} vnodes ({kv.Value * 100 / dist.Values.Sum()}%)");

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

            node.Put("user:1", "{name:Alice,age:30}");
            node.Put("user:2", "{name:Bob,age:25}");
            node.Put("user:3", "{name:Carol,age:35}");

            Console.WriteLine("After 3 writes:");
            PrintGet(node, "user:1");
            PrintGet(node, "user:2");
            PrintGet(node, "user:99"); // missing

            node.Put("user:1", "{name:Alice,age:31}");
            Console.WriteLine("\nAfter overwriting user:1 (age 30→31):");
            PrintGet(node, "user:1");

            node.Delete("user:2");
            Console.WriteLine("\nAfter deleting user:2:");
            PrintGet(node, "user:2");

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

            var nodes = store.GetResponsibleNodes("session:42");
            Console.WriteLine($"Nodes responsible for 'session:42': [{string.Join(", ", nodes)}]");

            bool ok = store.Put("session:42", "user=alice;exp=3600");
            Console.WriteLine($"\nPUT 'session:42' (W=2 quorum): {(ok ? "OK" : "FAILED")}");

            var (found, val) = store.Get("session:42");
            Console.WriteLine($"GET 'session:42': found={found}, value={val}");

            Console.WriteLine("\nSimulating stale replica (NodeA gets an older write for 'profile:7'):");
            store.Put("profile:7", "v1_initial");
            store.Put("profile:7", "v2_updated");

            var (f2, v2) = store.Get("profile:7");
            Console.WriteLine($"GET 'profile:7' (quorum read, returns latest): found={f2}, value={v2}");
            Console.WriteLine("(Read repair runs async to bring stale replicas to v2)\n");
        }

        // ── Scenario 4: TTL Expiry ─────────────────────────────────────────────

        static void Scenario4_TtlExpiry()
        {
            Console.WriteLine("─── Scenario 4: TTL Expiry ───");

            var node = new KvNode("Node1");
            node.Put("rate-limit:user:42", "requests=5", ttlSeconds: 1);
            node.Put("permanent:config",   "max_connections=100");

            Console.WriteLine("Immediately after write:");
            PrintGet(node, "rate-limit:user:42");
            PrintGet(node, "permanent:config");

            Console.WriteLine("\nWaiting 1.1 seconds for TTL to expire...");
            Thread.Sleep(1100);

            Console.WriteLine("After TTL expiry:");
            PrintGet(node, "rate-limit:user:42");
            PrintGet(node, "permanent:config");

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

            store.Put("order:100", "status=pending");
            store.Put("order:200", "status=shipped");

            Console.WriteLine("Before failure:");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:200");

            string failedNode = store.GetResponsibleNodes("order:100").First();
            Console.WriteLine($"\nTaking down {failedNode}...");
            store.SimulateNodeDown(failedNode);

            Console.WriteLine("After failure (R=2 from 2 remaining nodes):");
            PrintStoreGet(store, "order:100");
            PrintStoreGet(store, "order:200");

            bool writeOk = store.Put("order:300", "status=new");
            Console.WriteLine($"\nPUT 'order:300' during failure (W=2): {(writeOk ? "OK" : "FAILED")}");

            Console.WriteLine($"\n{failedNode} comes back online...");
            store.SimulateNodeUp(failedNode);

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
