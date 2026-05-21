// Q3. Implement Kafka-Style Consumer Groups
//
// A topic is an append-only log. Multiple consumer groups each read the entire
// log independently. Within a group, messages are distributed across instances
// (load sharing). Across groups, every group receives every message (fan-out).
//
// The Consumer Group Model
// ──────────────────────────
// Topic: order.placed  [msg0, msg1, msg2, msg3, msg4, msg5]
//                        ↑ offset (each message has a fixed offset forever)
//
// Consumer Group "email-service" — 2 instances:
//   Instance email-1 → gets offset 0, 2, 4  (round-robin within group)
//   Instance email-2 → gets offset 1, 3, 5
//
// Consumer Group "analytics-service" — 1 instance:
//   Instance analytics-1 → gets offset 0, 1, 2, 3, 4, 5  (all of them)
//
// Key insight:
//   → Groups are independent: analytics reads at offset 0 while email is at 6
//   → Within a group: each message goes to exactly ONE instance (competing)
//   → Across groups: each group receives every message (broadcast)
//
// What Real Kafka Adds on Top
// ────────────────────────────
// 1. Partitions — parallelism within a topic:
//    Topic has N partitions → up to N consumers in a group run in parallel
//    Partition key = user_id → all orders from the same user → same partition
//    → Per-user ordering is preserved; parallelism is across users
//
// 2. Offsets — each group tracks its own read position independently:
//    Group A at offset 500 → processing recent messages
//    Group B at offset 0   → replaying all history (backfill, audit)
//    → Groups never block each other; no coordination needed
//
// 3. Retention — messages are NOT deleted after consumption:
//    Stored on disk for a configurable period (days / weeks / forever)
//    A new consumer group can replay from offset 0 — e.g. to train an ML model
//    → Kafka is an event store, not just a transport
//
// 4. Consumer group rebalance — instances joining or leaving:
//    Partitions are reassigned across surviving instances automatically
//    Kafka's GroupCoordinator handles this via heartbeat protocol
//
// Complexity: Publish O(1) (append to log),
//             Poll O(1) (index into log by offset),
//             Space O(log size) — shared across all groups

using System;
using System.Collections.Generic;

namespace PubSubAndAsync
{
    // -------------------------------------------------------------------------
    // OrderEvent — the concrete message appended to the Kafka-style log
    // -------------------------------------------------------------------------
    // In production this is serialized to bytes and written to a Kafka partition.
    // The log retains every record permanently; consumers index into it by offset.
    //
    // After 6 orders are published, the log looks like:
    //   offset 0 → OrderEvent { OrderId=1001, Customer=cust-42, Total=$10.00 }
    //   offset 1 → OrderEvent { OrderId=1002, Customer=cust-17, Total=$20.00 }
    //   offset 2 → OrderEvent { OrderId=1003, Customer=cust-88, Total=$30.00 }
    //   offset 3 → OrderEvent { OrderId=1004, Customer=cust-05, Total=$40.00 }
    //   offset 4 → OrderEvent { OrderId=1005, Customer=cust-33, Total=$50.00 }
    //   offset 5 → OrderEvent { OrderId=1006, Customer=cust-71, Total=$60.00 }
    //
    // email-service (2 instances) splits the log by round-robin:
    //   email-1 → offsets 0, 2, 4  (orders 1001, 1003, 1005)
    //   email-2 → offsets 1, 3, 5  (orders 1002, 1004, 1006)
    //
    // analytics-service (1 instance) reads every offset 0–5 independently.
    //
    // A late-joining audit-service starts at offset 0 and replays the full history.
    public class OrderEvent
    {
        public string MessageId { get; set; }   // e.g. "msg-a1b2c3"
        public int OrderId { get; set; }   // e.g. 1001
        public string CustomerId { get; set; }   // e.g. "cust-42"
        public double Total { get; set; }   // e.g. 10.00 (USD)
        public string PlacedAt { get; set; }   // ISO-8601 timestamp

        public override string ToString() =>
            $"OrderEvent {{ OrderId={OrderId}, Customer={CustomerId}, Total=${Total:F2}, PlacedAt={PlacedAt} }}";
    }

    // -------------------------------------------------------------------------
    // KafkaStyleTopic
    // -------------------------------------------------------------------------
    public class KafkaStyleTopic
    {
        // Append-only log: messages are never removed. Each position is a
        // permanent, stable offset. This is what makes replay possible — a queue
        // that deletes on consume can never re-deliver to a late-joining group.
        //
        // _log[0] = OrderEvent { OrderId=1001, ... }
        // _log[1] = OrderEvent { OrderId=1002, ... }
        // _log[2] = OrderEvent { OrderId=1003, ... }  ← offset 2, never deleted
        private readonly List<OrderEvent> _log = [];

        // GroupState tracks each consumer group's independent read position.
        // GroupOffset: the next offset this group will read.
        // Instances:   the list of instance IDs within the group (for round-robin).
        private struct GroupState
        {
            public List<string> Instances;
            public int GroupOffset; // next offset to read
        }

        private readonly Dictionary<string, GroupState> _groups = [];

        // One lock for both _log and _groups. Publish appends to _log; Poll reads
        // _log by index — they must not run concurrently or GroupOffset could get
        // ahead of the log length, causing an IndexOutOfRangeException.
        private readonly object _lock = new();

        // Publish is O(1): List.Add is amortized O(1). Kafka achieves the same
        // with a sequential write to a memory-mapped segment file — sequential
        // disk I/O is often faster than random-access reads.
        public void Publish(OrderEvent message)
        {
            lock (_lock)
            {
                _log.Add(message);
                Console.WriteLine($"[TOPIC]  Published offset={_log.Count - 1}: {message}");
            }
        }

        // Each group has its own set of consumer instances and starts at offset 0.
        // Registering AFTER messages are published means the group can read from
        // offset 0 and replay history — impossible with a traditional queue.
        public void RegisterGroup(string groupName, List<string> instanceIds)
        {
            lock (_lock)
            {
                _groups[groupName] = new GroupState
                {
                    Instances = [.. instanceIds],
                    GroupOffset = 0
                };
                Console.WriteLine($"[GROUP]  '{groupName}' registered with " +
                                  $"{instanceIds.Count} instance(s): " +
                                  $"[{string.Join(", ", instanceIds)}]");
            }
        }

        // Poll: return the next (instanceId, message) for the group, or (null, null)
        // if the group has consumed all published messages (fully caught up).
        public (string InstanceId, OrderEvent Message) Poll(string groupName)
        {
            lock (_lock)
            {
                if (!_groups.TryGetValue(groupName, out GroupState group))
                    return (null, null);

                // Fully caught up — no new messages since last Poll.
                if (group.GroupOffset >= _log.Count)
                    return (null, null);

                OrderEvent message = _log[group.GroupOffset];

                // Round-robin dispatch within the group:
                // GroupOffset % Instances.Count maps each successive message to the
                // next instance in sequence. When the group has 2 instances,
                // offsets 0,2,4 → instance[0]; offsets 1,3,5 → instance[1].
                // This gives even load distribution with zero coordination overhead.
                string instanceId = group.Instances[group.GroupOffset % group.Instances.Count];

                // Advance this group's offset. Other groups are not affected —
                // each has its own GroupOffset field.
                _groups[groupName] = new GroupState
                {
                    Instances = group.Instances,
                    GroupOffset = group.GroupOffset + 1
                };

                return (instanceId, message);
            }
        }

        // Drain all pending messages for a group — convenience wrapper around Poll.
        public void DrainGroup(string groupName)
        {
            Console.WriteLine($"\n[DRAIN]  Group '{groupName}' consuming:");
            while (true)
            {
                var (instanceId, message) = Poll(groupName);
                if (message == null) break;
                Console.WriteLine($"  [{instanceId,-14}] offset consumed: {message}");
            }
            Console.WriteLine($"  Group '{groupName}' is now fully caught up.");
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            var topic = new KafkaStyleTopic();

            // =================================================================
            // Scenario 1 — Register two groups; publish 6 messages; drain both
            // email-service has 2 instances → messages split between them (load share)
            // analytics-service has 1 instance → receives every message
            // Both groups read the same log independently.
            // =================================================================
            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Two groups — each receives all 6 messages     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            topic.RegisterGroup("email-service", new List<string> { "email-1", "email-2" });
            topic.RegisterGroup("analytics-service", new List<string> { "analytics-1" });

            string[] customers1 = { "cust-42", "cust-17", "cust-88", "cust-05", "cust-33", "cust-71" };
            Console.WriteLine("\n  Publishing 6 orders:");
            for (int i = 1; i <= 6; i++)
                topic.Publish(new OrderEvent { MessageId = $"msg-{i:D3}", OrderId = 1000 + i, CustomerId = customers1[i - 1], Total = i * 10.0, PlacedAt = $"2026-05-21T10:{14 + i:D2}:00Z" });

            // email-service: 2 instances split the load (each handles 3 messages)
            // analytics-service: 1 instance gets all 6
            topic.DrainGroup("email-service");
            topic.DrainGroup("analytics-service");

            // =================================================================
            // Scenario 2 — Publish 3 more; groups drain independently
            // email-service resumes from offset 6; analytics from offset 6.
            // Each group's progress is tracked separately — neither blocks the other.
            // =================================================================
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: 3 more messages — groups resume independently  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            string[] customers2 = ["cust-14", "cust-56", "cust-29"];
            Console.WriteLine("\n  Publishing orders 7–9:");
            for (int i = 7; i <= 9; i++)
                topic.Publish(new OrderEvent { MessageId = $"msg-{i:D3}", OrderId = 1000 + i, CustomerId = customers2[i - 7], Total = i * 10.0, PlacedAt = $"2026-05-21T10:{14 + i:D2}:00Z" });

            // email-service picks up from offset 6 — not from 0
            topic.DrainGroup("email-service");
            topic.DrainGroup("analytics-service");

            // =================================================================
            // Scenario 3 — New group registered AFTER messages; replays from offset 0
            // This is Kafka's replay capability. A new audit-service can read ALL
            // historical events from the beginning — no data was discarded.
            // A traditional queue would have no messages left to deliver here.
            // =================================================================
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Late-joining group replays all 9 messages      ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            topic.RegisterGroup("audit-service", new List<string> { "audit-1" });

            // audit-service starts at offset 0 — reads the entire history
            topic.DrainGroup("audit-service");
        }
    }

} // namespace PubSubAndAsync
