// Program — entry point for all Distributed Message Queue demo scenarios.

namespace AdvancedDesigns
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Distributed Message Queue (Kafka) Demo ===\n");

            Scenario1_BasicPubSub();
            Scenario2_KeyBasedPartitioning();
            Scenario3_ConsumerGroupsParallelism();
            Scenario4_ConsumerLagAndCatchup();
            Scenario5_RebalanceOnJoin();
            Scenario6_LogCompaction();
        }

        // ── Scenario 1: Basic Pub/Sub with Offset Tracking ────────────────────

        static void Scenario1_BasicPubSub()
        {
            Console.WriteLine("─── Scenario 1: Basic Pub/Sub with Offset Tracking ───");

            var broker   = new Broker();
            broker.CreateTopic("notifications", partitions: 1);
            var producer = new Producer(broker);
            var consumer = new Consumer("consumer-1", broker);

            producer.Produce("notifications", null, "Welcome, Alice!");
            producer.Produce("notifications", null, "Your order shipped");
            producer.Produce("notifications", null, "Payment confirmed");
            producer.Produce("notifications", null, "Review requested");

            Console.WriteLine($"Produced 4 messages. Latest offset = {broker.GetTopic("notifications").GetLatestOffsets()[0]}");

            var batch1 = consumer.Poll("notifications", 0, maxMessages: 10);
            Console.WriteLine($"\nFirst poll (from offset 0): {batch1.Count} messages");
            foreach (var m in batch1) Console.WriteLine($"  {m}");

            consumer.CommitAll("notifications", batch1);
            Console.WriteLine($"Committed offset. Next read from: {consumer.GetCommittedOffset("notifications", 0)}");

            producer.Produce("notifications", null, "Flash sale!");
            producer.Produce("notifications", null, "Account activity");

            var batch2 = consumer.Poll("notifications", 0, maxMessages: 10);
            Console.WriteLine($"\nSecond poll (from committed offset): {batch2.Count} messages");
            foreach (var m in batch2) Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 2: Key-Based Partitioning (ordering per user) ────────────

        static void Scenario2_KeyBasedPartitioning()
        {
            Console.WriteLine("─── Scenario 2: Key-Based Partitioning (same key → same partition) ───");

            var broker   = new Broker();
            broker.CreateTopic("orders", partitions: 3);
            var producer = new Producer(broker);

            var orders = new[]
            {
                ("user:Alice", "order:101 created"),
                ("user:Bob",   "order:102 created"),
                ("user:Carol", "order:103 created"),
                ("user:Alice", "order:101 payment"),
                ("user:Bob",   "order:102 shipped"),
                ("user:Alice", "order:101 delivered"),
                ("user:Carol", "order:103 payment"),
            };

            Console.WriteLine("Producing orders (key=user_id ensures ordering per user):");
            var seenPartitions = new Dictionary<string, int>();
            foreach (var (key, value) in orders)
            {
                var r = producer.Produce("orders", key, value);
                Console.WriteLine($"  {key,-15} → P{r.Partition}@{r.Offset}  \"{value}\"");
                if (!seenPartitions.ContainsKey(key)) seenPartitions[key] = r.Partition;
                else if (seenPartitions[key] != r.Partition)
                    Console.WriteLine($"  *** ERROR: {key} went to different partitions! ***");
            }

            Console.WriteLine("\nVerification: each user always maps to same partition:");
            foreach (var kv in seenPartitions)
                Console.WriteLine($"  {kv.Key,-15} → P{kv.Value} (consistent)");

            int alicePartition = seenPartitions["user:Alice"];
            Console.WriteLine($"\nReading P{alicePartition} (all Alice's orders in order):");
            foreach (var m in broker.GetTopic("orders").GetPartition(alicePartition).ReadFrom(0))
                Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 3: Consumer Groups — Independent Consumption ─────────────

        static void Scenario3_ConsumerGroupsParallelism()
        {
            Console.WriteLine("─── Scenario 3: Consumer Groups (two groups read same messages independently) ───");

            var broker   = new Broker();
            broker.CreateTopic("events", partitions: 3);
            var producer = new Producer(broker);

            for (int i = 1; i <= 9; i++)
                producer.Produce("events", $"key:{i}", $"event-{i}");

            var groupA = new ConsumerGroup("analytics", broker);
            groupA.Subscribe("events");
            groupA.AddConsumer("analytics-1");
            groupA.AddConsumer("analytics-2");

            Console.WriteLine("Group A (analytics) partition assignment:");
            foreach (var kv in groupA.GetAssignment().OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key} → partitions [{string.Join(", ", kv.Value)}]");

            var groupB = new ConsumerGroup("billing", broker);
            groupB.Subscribe("events");
            groupB.AddConsumer("billing-1");
            groupB.AddConsumer("billing-2");
            groupB.AddConsumer("billing-3");

            Console.WriteLine("\nGroup B (billing) partition assignment:");
            foreach (var kv in groupB.GetAssignment().OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key} → partitions [{string.Join(", ", kv.Value)}]");

            var batchA = groupA.PollAll(maxPerPartition: 10);
            var batchB = groupB.PollAll(maxPerPartition: 10);
            Console.WriteLine($"\nGroup A polled: {batchA.Count} messages");
            Console.WriteLine($"Group B polled: {batchB.Count} messages");
            Console.WriteLine("(Both groups get the full set — consumer groups are independent)");

            Console.WriteLine();
        }

        // ── Scenario 4: Consumer Lag — Producer Outpaces Consumer ─────────────

        static void Scenario4_ConsumerLagAndCatchup()
        {
            Console.WriteLine("─── Scenario 4: Consumer Lag and Catch-up ───");

            var broker   = new Broker();
            broker.CreateTopic("metrics", partitions: 1);
            var producer = new Producer(broker);
            var consumer = new Consumer("metrics-consumer", broker);

            for (int i = 1; i <= 20; i++)
                producer.Produce("metrics", null, $"cpu={i}%");

            Console.WriteLine($"Producer wrote 20 messages. Latest offset = {broker.GetTopic("metrics").GetLatestOffsets()[0]}");
            Console.WriteLine($"Consumer lag: {consumer.GetLag("metrics", 0)} messages behind");

            int batch = 0;
            while (consumer.GetLag("metrics", 0) > 0)
            {
                var msgs = consumer.Poll("metrics", 0, maxMessages: 5);
                if (msgs.Count == 0) break;
                consumer.CommitAll("metrics", msgs);
                Console.WriteLine($"  Batch {++batch}: processed {msgs.Count} messages, lag remaining: {consumer.GetLag("metrics", 0)}");
            }
            Console.WriteLine($"Consumer fully caught up. Final offset: {consumer.GetCommittedOffset("metrics", 0)}");

            producer.Produce("metrics", null, "cpu=5%");
            producer.Produce("metrics", null, "cpu=7%");
            producer.Produce("metrics", null, "cpu=3%");
            Console.WriteLine($"\nProducer adds 3 more. New lag: {consumer.GetLag("metrics", 0)}");

            Console.WriteLine();
        }

        // ── Scenario 5: Consumer Rebalance on Group Join ──────────────────────

        static void Scenario5_RebalanceOnJoin()
        {
            Console.WriteLine("─── Scenario 5: Consumer Group Rebalance ───");

            var broker   = new Broker();
            broker.CreateTopic("payments", partitions: 4);
            var producer = new Producer(broker);

            for (int i = 1; i <= 12; i++)
                producer.Produce("payments", $"txn:{i}", $"amount=${i * 10}");

            var group = new ConsumerGroup("payment-processor", broker);
            group.Subscribe("payments");
            group.AddConsumer("worker-1");
            group.AddConsumer("worker-2");

            Console.WriteLine("Initial assignment (2 consumers, 4 partitions):");
            PrintAssignment(group);

            var initialBatch = group.PollAll(maxPerPartition: 3);
            foreach (var g in initialBatch.GroupBy(x => x.consumer.ConsumerId))
                Console.WriteLine($"  {g.Key} polled {g.Count()} messages");

            foreach (var g in initialBatch.GroupBy(x => (x.consumer, x.msg.Partition)))
                g.Key.consumer.CommitAll("payments", g.Select(x => x.msg));

            Console.WriteLine("\nworker-3 joins the group → REBALANCE");
            group.AddConsumer("worker-3");
            Console.WriteLine("New assignment (3 consumers, 4 partitions):");
            PrintAssignment(group);

            Console.WriteLine("\nworker-4 joins the group → REBALANCE");
            group.AddConsumer("worker-4");
            Console.WriteLine("New assignment (4 consumers, 4 partitions — perfect 1:1):");
            PrintAssignment(group);

            Console.WriteLine("\nworker-5 joins the group → REBALANCE");
            group.AddConsumer("worker-5");
            Console.WriteLine("New assignment (5 consumers, 4 partitions — worker-5 is idle):");
            PrintAssignment(group);

            Console.WriteLine();
        }

        // ── Scenario 6: Log Compaction ─────────────────────────────────────────

        static void Scenario6_LogCompaction()
        {
            Console.WriteLine("─── Scenario 6: Log Compaction ───");

            var rawLog = new List<Message>
            {
                new("user:1", "{name:Alice, age:30}"),
                new("user:2", "{name:Bob, age:25}"),
                new("user:3", "{name:Carol, age:35}"),
                new("user:1", "{name:Alice, age:31}"),
                new("user:2", "{name:Robert, age:25}"),
                new("user:4", "{name:Dave, age:28}"),
                new("user:1", "{name:Alice, age:32}"),
                new("user:3", null),                     // TOMBSTONE: delete user:3
                new("user:2", "{name:Robert, age:26}"),
            };
            for (int i = 0; i < rawLog.Count; i++) rawLog[i].Offset = i;

            Console.WriteLine($"Raw log ({rawLog.Count} messages):");
            foreach (var m in rawLog)
                Console.WriteLine($"  offset {m.Offset}: key={m.Key,-8} value={m.Value ?? "(tombstone)"}");

            var compacted = LogCompactor.Compact(rawLog);
            Console.WriteLine($"\nAfter compaction ({compacted.Count} messages — only latest per key):");
            foreach (var m in compacted)
                Console.WriteLine($"  offset {m.Offset}: key={m.Key,-8} value={m.Value}");

            Console.WriteLine($"\nSpace saved: {rawLog.Count} → {compacted.Count} messages " +
                $"({(rawLog.Count - compacted.Count) * 100 / rawLog.Count}% reduction)");
            Console.WriteLine("user:3 deleted (tombstone), user:1 has only latest age=32, user:2 has latest name+age.");
        }

        static void PrintAssignment(ConsumerGroup group)
        {
            foreach (var kv in group.GetAssignment().OrderBy(x => x.Key))
            {
                string partitions = kv.Value.Count == 0
                    ? "(IDLE — no partition)"
                    : $"→ partitions [{string.Join(", ", kv.Value)}]";
                Console.WriteLine($"  {kv.Key} {partitions}");
            }
        }
    }
}
