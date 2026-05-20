using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AdvancedDesigns
{
    // ─── Message ───────────────────────────────────────────────────────────────

    public class Message
    {
        public string Key { get; }
        public string Value { get; }
        public long Offset { get; set; }
        public int Partition { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        public Message(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public override string ToString() =>
            $"[P{Partition}@{Offset}] key={Key ?? "(null)"} value={Value ?? "(tombstone)"}";
    }

    // ─── Partition Log (append-only, ordered) ──────────────────────────────────

    public class PartitionLog
    {
        private readonly List<Message> _log = new List<Message>();
        private readonly object _lock = new object();

        public int Index { get; }
        public string TopicName { get; }

        public PartitionLog(string topicName, int index)
        {
            TopicName = topicName;
            Index = index;
        }

        public long Append(Message msg)
        {
            lock (_lock)
            {
                msg.Partition = Index;
                msg.Offset = _log.Count;
                _log.Add(msg);
                return msg.Offset;
            }
        }

        public List<Message> ReadFrom(long fromOffset, int maxCount = 100)
        {
            lock (_lock)
            {
                if (fromOffset >= _log.Count) return new List<Message>();
                int available = _log.Count - (int)fromOffset;
                int take = Math.Min(available, maxCount);
                return _log.GetRange((int)fromOffset, take);
            }
        }

        public long LatestOffset
        {
            get { lock (_lock) { return _log.Count; } }
        }
    }

    // ─── Topic ─────────────────────────────────────────────────────────────────

    public class Topic
    {
        public string Name { get; }
        public int PartitionCount { get; }
        private readonly PartitionLog[] _partitions;
        public bool IsCompacted { get; }

        public Topic(string name, int partitionCount, bool compacted = false)
        {
            Name = name;
            PartitionCount = partitionCount;
            IsCompacted = compacted;
            _partitions = new PartitionLog[partitionCount];
            for (int i = 0; i < partitionCount; i++)
                _partitions[i] = new PartitionLog(name, i);
        }

        public int GetPartitionIndex(string key)
        {
            if (key == null) return 0; // simplified: null key goes to P0
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
            int hashVal = Math.Abs(BitConverter.ToInt32(hash, 0));
            return hashVal % PartitionCount;
        }

        public PartitionLog GetPartition(int index) => _partitions[index];

        public long[] GetLatestOffsets() => _partitions.Select(p => p.LatestOffset).ToArray();
    }

    // ─── Broker (hosts topics and partition logs) ──────────────────────────────

    public class Broker
    {
        private readonly Dictionary<string, Topic> _topics = new Dictionary<string, Topic>();

        public void CreateTopic(string name, int partitions, bool compacted = false)
        {
            _topics[name] = new Topic(name, partitions, compacted);
        }

        public Topic GetTopic(string name)
        {
            if (!_topics.TryGetValue(name, out Topic topic))
                throw new InvalidOperationException($"Topic '{name}' does not exist");
            return topic;
        }

        public bool TopicExists(string name) => _topics.ContainsKey(name);
    }

    // ─── Producer ──────────────────────────────────────────────────────────────

    public class Producer
    {
        private readonly Broker _broker;
        private int _roundRobinIndex;

        public Producer(Broker broker)
        {
            _broker = broker;
        }

        public ProduceResult Produce(string topicName, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);

            int partition;
            if (key != null)
                partition = topic.GetPartitionIndex(key);
            else
                partition = Interlocked.Increment(ref _roundRobinIndex) % topic.PartitionCount;

            var msg = new Message(key, value);
            long offset = topic.GetPartition(partition).Append(msg);

            return new ProduceResult(topicName, partition, offset);
        }

        public ProduceResult ProduceToPartition(string topicName, int partition, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);
            var msg = new Message(key, value);
            long offset = topic.GetPartition(partition).Append(msg);
            return new ProduceResult(topicName, partition, offset);
        }
    }

    public class ProduceResult
    {
        public string Topic { get; }
        public int Partition { get; }
        public long Offset { get; }

        public ProduceResult(string topic, int partition, long offset)
        {
            Topic = topic;
            Partition = partition;
            Offset = offset;
        }
    }

    // ─── Consumer (tracks its own offsets per partition) ──────────────────────

    public class Consumer
    {
        public string ConsumerId { get; }
        private readonly Broker _broker;
        private readonly Dictionary<(string topic, int partition), long> _committedOffsets
            = new Dictionary<(string, int), long>();

        public Consumer(string consumerId, Broker broker)
        {
            ConsumerId = consumerId;
            _broker = broker;
        }

        public List<Message> Poll(string topicName, int partition, int maxMessages = 10)
        {
            Topic topic = _broker.GetTopic(topicName);
            long fromOffset = GetCommittedOffset(topicName, partition);
            return topic.GetPartition(partition).ReadFrom(fromOffset, maxMessages);
        }

        public void Commit(string topicName, int partition, long offset)
        {
            _committedOffsets[(topicName, partition)] = offset + 1; // next to read
        }

        public void CommitAll(string topicName, IEnumerable<Message> messages)
        {
            foreach (var group in messages.GroupBy(m => m.Partition))
                Commit(topicName, group.Key, group.Max(m => m.Offset));
        }

        public long GetCommittedOffset(string topicName, int partition)
        {
            _committedOffsets.TryGetValue((topicName, partition), out long offset);
            return offset;
        }

        public long GetLag(string topicName, int partition)
        {
            Topic topic = _broker.GetTopic(topicName);
            long latest = topic.GetPartition(partition).LatestOffset;
            long committed = GetCommittedOffset(topicName, partition);
            return latest - committed;
        }
    }

    // ─── Consumer Group (coordinates partition assignment) ────────────────────

    public class ConsumerGroup
    {
        public string GroupId { get; }
        private readonly Broker _broker;
        private readonly List<Consumer> _consumers = new List<Consumer>();
        private readonly Dictionary<int, Consumer> _partitionAssignment = new Dictionary<int, Consumer>();
        private string _subscribedTopic;

        public ConsumerGroup(string groupId, Broker broker)
        {
            GroupId = groupId;
            _broker = broker;
        }

        public Consumer AddConsumer(string consumerId)
        {
            var consumer = new Consumer(consumerId, _broker);
            _consumers.Add(consumer);
            if (_subscribedTopic != null) Rebalance();
            return consumer;
        }

        public void RemoveConsumer(string consumerId)
        {
            _consumers.RemoveAll(c => c.ConsumerId == consumerId);
            if (_subscribedTopic != null) Rebalance();
        }

        public void Subscribe(string topicName)
        {
            _subscribedTopic = topicName;
            Rebalance();
        }

        public void Rebalance()
        {
            if (_subscribedTopic == null || _consumers.Count == 0) return;
            Topic topic = _broker.GetTopic(_subscribedTopic);
            _partitionAssignment.Clear();

            // Round-robin assignment: partition i → consumer[i % consumerCount]
            for (int p = 0; p < topic.PartitionCount; p++)
                _partitionAssignment[p] = _consumers[p % _consumers.Count];
        }

        public Dictionary<string, List<int>> GetAssignment()
        {
            var result = new Dictionary<string, List<int>>();
            foreach (var kv in _partitionAssignment)
            {
                string id = kv.Value.ConsumerId;
                if (!result.ContainsKey(id)) result[id] = new List<int>();
                result[id].Add(kv.Key);
            }
            return result;
        }

        public List<(Consumer consumer, Message msg)> PollAll(int maxPerPartition = 5)
        {
            var results = new List<(Consumer, Message)>();
            if (_subscribedTopic == null) return results;

            foreach (var kv in _partitionAssignment)
            {
                int partition = kv.Key;
                Consumer consumer = kv.Value;
                var msgs = consumer.Poll(_subscribedTopic, partition, maxPerPartition);
                foreach (var msg in msgs)
                    results.Add((consumer, msg));
            }
            return results;
        }

        public (long totalLag, Dictionary<int, long> perPartitionLag) GetLag()
        {
            if (_subscribedTopic == null) return (0, new Dictionary<int, long>());
            Topic topic = _broker.GetTopic(_subscribedTopic);
            var perPartition = new Dictionary<int, long>();
            long total = 0;

            foreach (var kv in _partitionAssignment)
            {
                long lag = kv.Value.GetLag(_subscribedTopic, kv.Key);
                perPartition[kv.Key] = lag;
                total += lag;
            }
            return (total, perPartition);
        }
    }

    // ─── Log Compactor ─────────────────────────────────────────────────────────

    public class LogCompactor
    {
        public static List<Message> Compact(IEnumerable<Message> log)
        {
            // Keep only the last message per key; drop tombstones (null value) at the end
            var latest = new Dictionary<string, Message>();
            foreach (var msg in log)
            {
                if (msg.Key != null)
                    latest[msg.Key] = msg;
            }
            return latest.Values
                .Where(m => m.Value != null) // remove tombstones from final output
                .OrderBy(m => m.Offset)
                .ToList();
        }
    }

    // ─── Main Program ──────────────────────────────────────────────────────────

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

            var broker = new Broker();
            broker.CreateTopic("notifications", partitions: 1);

            var producer = new Producer(broker);
            var consumer = new Consumer("consumer-1", broker);

            // Produce 4 messages
            producer.Produce("notifications", null, "Welcome, Alice!");
            producer.Produce("notifications", null, "Your order shipped");
            producer.Produce("notifications", null, "Payment confirmed");
            producer.Produce("notifications", null, "Review requested");

            Console.WriteLine($"Produced 4 messages. Latest offset = {broker.GetTopic("notifications").GetLatestOffsets()[0]}");

            // First poll: reads all 4
            var batch1 = consumer.Poll("notifications", 0, maxMessages: 10);
            Console.WriteLine($"\nFirst poll (from offset 0): {batch1.Count} messages");
            foreach (var m in batch1) Console.WriteLine($"  {m}");

            // Commit after processing
            consumer.CommitAll("notifications", batch1);
            Console.WriteLine($"Committed offset. Next read from: {consumer.GetCommittedOffset("notifications", 0)}");

            // Produce 2 more
            producer.Produce("notifications", null, "Flash sale!");
            producer.Produce("notifications", null, "Account activity");

            // Second poll: only picks up new messages (offset tracking works)
            var batch2 = consumer.Poll("notifications", 0, maxMessages: 10);
            Console.WriteLine($"\nSecond poll (from committed offset): {batch2.Count} messages");
            foreach (var m in batch2) Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 2: Key-Based Partitioning (ordering per user) ────────────

        static void Scenario2_KeyBasedPartitioning()
        {
            Console.WriteLine("─── Scenario 2: Key-Based Partitioning (same key → same partition) ───");

            var broker = new Broker();
            broker.CreateTopic("orders", partitions: 3);

            var producer = new Producer(broker);

            // Orders for 3 users — same user always → same partition
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

                if (!seenPartitions.ContainsKey(key))
                    seenPartitions[key] = r.Partition;
                else if (seenPartitions[key] != r.Partition)
                    Console.WriteLine($"  *** ERROR: {key} went to different partitions! ***");
            }

            Console.WriteLine("\nVerification: each user always maps to same partition:");
            foreach (var kv in seenPartitions)
                Console.WriteLine($"  {kv.Key,-15} → P{kv.Value} (consistent)");

            Console.WriteLine("\nReading P0 (all Alice's orders in order):");
            var topic = broker.GetTopic("orders");
            int alicePartition = seenPartitions["user:Alice"];
            var msgs = topic.GetPartition(alicePartition).ReadFrom(0);
            foreach (var m in msgs) Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 3: Consumer Groups — Independent Consumption ─────────────

        static void Scenario3_ConsumerGroupsParallelism()
        {
            Console.WriteLine("─── Scenario 3: Consumer Groups (two groups read same messages independently) ───");

            var broker = new Broker();
            broker.CreateTopic("events", partitions: 3);
            var producer = new Producer(broker);

            // Produce 9 events across 3 partitions
            for (int i = 1; i <= 9; i++)
                producer.Produce("events", $"key:{i}", $"event-{i}");

            // Group A: Analytics (2 consumers)
            var groupA = new ConsumerGroup("analytics", broker);
            groupA.Subscribe("events");
            groupA.AddConsumer("analytics-1");
            groupA.AddConsumer("analytics-2");

            Console.WriteLine("Group A (analytics) partition assignment:");
            foreach (var kv in groupA.GetAssignment().OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key} → partitions [{string.Join(", ", kv.Value)}]");

            // Group B: Billing (3 consumers — 1:1 with partitions)
            var groupB = new ConsumerGroup("billing", broker);
            groupB.Subscribe("events");
            groupB.AddConsumer("billing-1");
            groupB.AddConsumer("billing-2");
            groupB.AddConsumer("billing-3");

            Console.WriteLine("\nGroup B (billing) partition assignment:");
            foreach (var kv in groupB.GetAssignment().OrderBy(x => x.Key))
                Console.WriteLine($"  {kv.Key} → partitions [{string.Join(", ", kv.Value)}]");

            // Both groups poll — should each see all 9 messages (independently)
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

            var broker = new Broker();
            broker.CreateTopic("metrics", partitions: 1);
            var producer = new Producer(broker);
            var consumer = new Consumer("metrics-consumer", broker);

            // Producer emits 20 messages
            for (int i = 1; i <= 20; i++)
                producer.Produce("metrics", null, $"cpu={i}%");

            Console.WriteLine($"Producer wrote 20 messages. Latest offset = {broker.GetTopic("metrics").GetLatestOffsets()[0]}");
            Console.WriteLine($"Consumer lag: {consumer.GetLag("metrics", 0)} messages behind");

            // Consumer processes in batches of 5 (simulating slow processing)
            int batch = 0;
            while (consumer.GetLag("metrics", 0) > 0)
            {
                batch++;
                var msgs = consumer.Poll("metrics", 0, maxMessages: 5);
                if (msgs.Count == 0) break;
                consumer.CommitAll("metrics", msgs);
                long lag = consumer.GetLag("metrics", 0);
                Console.WriteLine($"  Batch {batch}: processed {msgs.Count} messages, lag remaining: {lag}");
            }

            Console.WriteLine($"Consumer fully caught up. Final offset: {consumer.GetCommittedOffset("metrics", 0)}");

            // Producer adds 3 more during consumer idle
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

            var broker = new Broker();
            broker.CreateTopic("payments", partitions: 4);
            var producer = new Producer(broker);

            for (int i = 1; i <= 12; i++)
                producer.Produce("payments", $"txn:{i}", $"amount=${i * 10}");

            // Start with 2 consumers
            var group = new ConsumerGroup("payment-processor", broker);
            group.Subscribe("payments");
            group.AddConsumer("worker-1");
            group.AddConsumer("worker-2");

            Console.WriteLine("Initial assignment (2 consumers, 4 partitions):");
            PrintAssignment(group);

            // Each consumer handles 2 partitions
            var initialBatch = group.PollAll(maxPerPartition: 3);
            foreach (var g in initialBatch.GroupBy(x => x.consumer.ConsumerId))
                Console.WriteLine($"  {g.Key} polled {g.Count()} messages");

            // Commit progress
            foreach (var g in initialBatch.GroupBy(x => x.consumer.ConsumerId))
                g.Key.StartsWith("w") // always true
                    .ToString(); // no-op: just grouping for clarity
            var consumed = initialBatch
                .GroupBy(x => (x.consumer, x.msg.Partition))
                .ToList();
            foreach (var g in consumed)
                g.Key.consumer.CommitAll("payments", g.Select(x => x.msg));

            // 3rd consumer joins → rebalance
            Console.WriteLine("\nworker-3 joins the group → REBALANCE");
            group.AddConsumer("worker-3");
            Console.WriteLine("New assignment (3 consumers, 4 partitions):");
            PrintAssignment(group);

            // 4th consumer joins → each gets exactly 1 partition
            Console.WriteLine("\nworker-4 joins the group → REBALANCE");
            group.AddConsumer("worker-4");
            Console.WriteLine("New assignment (4 consumers, 4 partitions — perfect 1:1):");
            PrintAssignment(group);

            // 5th consumer joins → one is idle (no partition to assign)
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

            // Simulate a stream of user profile updates
            var rawLog = new List<Message>
            {
                new Message("user:1", "{name:Alice, age:30}"),
                new Message("user:2", "{name:Bob, age:25}"),
                new Message("user:3", "{name:Carol, age:35}"),
                new Message("user:1", "{name:Alice, age:31}"),    // update
                new Message("user:2", "{name:Robert, age:25}"),   // name change
                new Message("user:4", "{name:Dave, age:28}"),
                new Message("user:1", "{name:Alice, age:32}"),    // another update
                new Message("user:3", null),                       // TOMBSTONE: delete user:3
                new Message("user:2", "{name:Robert, age:26}"),   // birthday
            };

            // Assign fake offsets for display
            for (int i = 0; i < rawLog.Count; i++) rawLog[i].Offset = i;

            Console.WriteLine($"Raw log ({rawLog.Count} messages):");
            foreach (var m in rawLog)
                Console.WriteLine($"  offset {m.Offset}: key={m.Key,-8} value={m.Value ?? "(tombstone)"}");

            var compacted = LogCompactor.Compact(rawLog);

            Console.WriteLine($"\nAfter compaction ({compacted.Count} messages — only latest per key):");
            foreach (var m in compacted)
                Console.WriteLine($"  offset {m.Offset}: key={m.Key,-8} value={m.Value}");

            Console.WriteLine("\nSpace saved: " +
                $"{rawLog.Count} → {compacted.Count} messages " +
                $"({(rawLog.Count - compacted.Count) * 100 / rawLog.Count}% reduction)");
            Console.WriteLine("user:3 deleted (tombstone), user:1 has only latest age=32, user:2 has latest name+age.");
        }

        static void PrintAssignment(ConsumerGroup group)
        {
            foreach (var kv in group.GetAssignment().OrderBy(x => x.Key))
            {
                string status = kv.Value.Count == 0 ? " (IDLE — no partition)" : $" → partitions [{string.Join(", ", kv.Value)}]";
                Console.WriteLine($"  {kv.Key}{status}");
            }
        }
    }
}
