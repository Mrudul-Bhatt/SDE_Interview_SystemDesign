// Q2. Implement the Fan-Out Pattern (SNS + SQS Style)
//
// One published message fans out to multiple isolated queues — one per subscriber
// service. Each service processes from its own queue independently. A slow or
// crashed service does not block or affect any other service.
//
// Why Fan-Out + Isolated Queues Beats Direct Pub/Sub
// ────────────────────────────────────────────────────
// Pure Pub/Sub problem:
//   Broker pushes to all subscribers simultaneously (synchronous)
//   Slow Email Service → it blocks the broker thread for all others
//   Crashed Inventory Service → that message is lost forever
//
// Fan-Out + Queue solution:
//   Broker enqueues in each service's own queue instantly (fast, O(1))
//   Each service drains its queue at its own pace (async, independent)
//   Crashed service  → messages buffer in its queue, no loss
//   Slow service     → its queue grows, others are completely unaffected
//
//   AWS pattern:
//   [SNS Topic] → [SQS Queue: email]     → [Email Workers]
//               → [SQS Queue: inventory] → [Inventory Workers]
//               → [SQS Queue: analytics] → [Analytics Workers]
//   Each set of workers can scale independently based on queue depth.
//
// Fan-Out vs Pure Pub/Sub — When to Use Each
// ────────────────────────────────────────────
// Pure Pub/Sub (8.4):
//   → All handlers are fast, reliable, in-process
//   → Message loss on crash is acceptable
//   → Low-volume real-time: live dashboards, stock tickers
//
// Fan-Out + Queues (this file):
//   → Subscribers have different processing speeds
//   → No message loss on crash (at-least-once delivery)
//   → Independent scaling per subscriber
//   → Production AWS: order processing, notifications, ETL pipelines
//
// Complexity: Publish O(subscribers), Consume O(1),
//             Space O(subscribers × queued messages)

using System;
using System.Collections.Generic;

namespace PubSubAndAsync
{
    // -------------------------------------------------------------------------
    // OrderEvent — the concrete message type flowing through the fan-out system
    // -------------------------------------------------------------------------
    // In production (AWS SNS/SQS) this would be serialized to JSON and sent as
    // the SQS message body. Every service receives a copy of the same payload.
    //
    // Real-world example stored in each SQS queue after an order is placed:
    //   {
    //     "MessageId":  "msg-a1b2c3",
    //     "Topic":      "order.placed",
    //     "OrderId":    1001,
    //     "CustomerId": "cust-42",
    //     "Total":      99.99,
    //     "Items":      [{ "Sku": "SHOE-XL", "Qty": 2 }],
    //     "PlacedAt":   "2026-05-21T10:15:00Z"
    //   }
    //
    // email-service    reads OrderId + CustomerId + Total  → sends "Your order is confirmed"
    // inventory-service reads OrderId + Items              → decrements SHOE-XL stock by 2
    // analytics-service reads OrderId + Total + PlacedAt  → updates revenue dashboard
    public class OrderEvent
    {
        public string MessageId { get; set; }   // e.g. "msg-a1b2c3"  (SQS MessageId in prod)
        public string Topic { get; set; }   // e.g. "order.placed"
        public int OrderId { get; set; }   // e.g. 1001
        public string CustomerId { get; set; }   // e.g. "cust-42"
        public double Total { get; set; }   // e.g. 99.99  (USD)
        public string PlacedAt { get; set; }   // ISO-8601 timestamp

        public override string ToString() =>
            $"OrderEvent {{ MessageId={MessageId}, Topic={Topic}, OrderId={OrderId}, " +
            $"Customer={CustomerId}, Total=${Total:F2}, PlacedAt={PlacedAt} }}";
    }

    // -------------------------------------------------------------------------
    // FanOutBroker
    // -------------------------------------------------------------------------
    public class FanOutBroker
    {
        // _queues["email-service"]     → Queue<OrderEvent> holding messages email workers will process
        // _queues["inventory-service"] → Queue<OrderEvent> holding messages inventory workers will process
        // _queues["analytics-service"] → Queue<OrderEvent> holding messages analytics workers will process
        //
        // After 3 orders are published, each queue looks like:
        //   Queue<OrderEvent> [
        //     OrderEvent { OrderId=1001, Customer=cust-42, Total=$99.99  },
        //     OrderEvent { OrderId=1002, Customer=cust-17, Total=$49.99  },
        //     OrderEvent { OrderId=1003, Customer=cust-88, Total=$149.99 }
        //   ]
        //
        // Isolation guarantee: if email-service's queue grows to 100k messages,
        // inventory-service's queue is at 0 and completely unaffected.
        // In AWS each Queue<> maps to a physical SQS queue with its own
        // throughput limits, visibility timeouts, and dead-letter queue.
        private readonly Dictionary<string, Queue<OrderEvent>> _queues = [];

        // One lock protecting _queues. Publish and Consume must not run
        // concurrently — Consume dequeuing while Publish is enqueuing could
        // corrupt the Queue<> internal state (not thread-safe on its own).
        private readonly object _lock = new();

        public void AddSubscriberQueue(string serviceName)
        {
            lock (_lock)
            {
                _queues[serviceName] = new Queue<OrderEvent>();
                Console.WriteLine($"[FAN-OUT] Queue created for: {serviceName}");
            }
        }

        // Fan-out: enqueue a COPY of the message reference into every queue.
        // "Copy" here means the same object reference goes into each queue —
        // each queue entry is independent, but they all point to the same
        // immutable message object. If messages were mutable, each service
        // would need a deep-cloned copy to avoid cross-service data corruption.
        public void Publish(string topic, OrderEvent message)
        {
            lock (_lock)
            {
                Console.WriteLine($"[FAN-OUT] '{topic}' → fanning out to {_queues.Count} queue(s)");
                foreach (var (service, queue) in _queues)
                {
                    queue.Enqueue(message);
                    Console.WriteLine($"  → Enqueued in [{service}] queue  (depth: {queue.Count})");
                }
            }
        }

        // Non-blocking poll: returns the next message or null if empty.
        // We return null rather than blocking because each service runs its
        // own consumer loop at its own pace — a blocking call would couple
        // the consumer's thread to the queue, defeating the async isolation.
        // In production (SQS), this maps to ReceiveMessage with WaitTimeSeconds=0.
        public OrderEvent Consume(string serviceName)
        {
            lock (_lock)
            {
                if (!_queues.TryGetValue(serviceName, out var queue) || queue.Count == 0)
                    return null;
                return queue.Dequeue();
            }
        }

        public int QueueDepth(string serviceName)
        {
            lock (_lock)
            {
                return _queues.TryGetValue(serviceName, out var q) ? q.Count : 0;
            }
        }

        public void PrintStatus()
        {
            lock (_lock)
            {
                Console.WriteLine("\n[FAN-OUT] Queue depths:");
                foreach (var (svc, q) in _queues)
                    Console.WriteLine($"  {svc,-22}: {q.Count} message(s) pending");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            var fanOut = new FanOutBroker();

            // =================================================================
            // Scenario 1 — 3 orders published; each lands in all 3 service queues
            // Publish returns immediately after enqueuing — it does not wait for
            // any service to process. Queue depth shows messages are buffered.
            // =================================================================
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: 3 orders → each enqueued in 3 service queues ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            fanOut.AddSubscriberQueue("email-service");
            fanOut.AddSubscriberQueue("inventory-service");
            fanOut.AddSubscriberQueue("analytics-service");

            Console.WriteLine();
            fanOut.Publish("order.placed", new OrderEvent { MessageId = "msg-a1b2c3", Topic = "order.placed", OrderId = 1001, CustomerId = "cust-42", Total = 99.99, PlacedAt = "2026-05-21T10:15:00Z" });
            fanOut.Publish("order.placed", new OrderEvent { MessageId = "msg-d4e5f6", Topic = "order.placed", OrderId = 1002, CustomerId = "cust-17", Total = 49.99, PlacedAt = "2026-05-21T10:16:30Z" });
            fanOut.Publish("order.placed", new OrderEvent { MessageId = "msg-g7h8i9", Topic = "order.placed", OrderId = 1003, CustomerId = "cust-88", Total = 149.99, PlacedAt = "2026-05-21T10:17:45Z" });

            fanOut.PrintStatus();

            // =================================================================
            // Scenario 2 — email-service processes its queue; others are untouched
            // Each service drains its own queue independently. Consuming from
            // email-service has no effect on inventory-service or analytics-service.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: email-service drains its queue independently  ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            OrderEvent msg;
            while ((msg = fanOut.Consume("email-service")) != null)
                Console.WriteLine($"  [email-service] Processed: {msg}");

            fanOut.PrintStatus(); // others still at depth 3

            // =================================================================
            // Scenario 3 — analytics-service crashes; queue buffers its messages
            // Inventory continues processing. Messages for analytics are held
            // safely in the queue — no data loss. This is the key advantage over
            // pure Pub/Sub where a crashed subscriber loses messages permanently.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: analytics-service crashes — queue holds msgs  ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine($"\n  analytics-service depth: {fanOut.QueueDepth("analytics-service")}  ← buffered, not lost");
            Console.WriteLine($"  inventory-service depth: {fanOut.QueueDepth("inventory-service")}  ← completely unaffected");

            Console.WriteLine("\n  inventory-service continues processing normally:");
            while ((msg = fanOut.Consume("inventory-service")) != null)
                Console.WriteLine($"  [inventory-service] Processed: {msg}");

            // =================================================================
            // Scenario 4 — analytics-service recovers and drains its backlog
            // When the service comes back up it finds its queue intact and can
            // process all buffered messages in order — no messages were lost.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: analytics-service recovers — drains backlog   ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            while ((msg = fanOut.Consume("analytics-service")) != null)
                Console.WriteLine($"  [analytics-service] Recovered: {msg}");

            Console.WriteLine($"\n  analytics-service depth after drain: {fanOut.QueueDepth("analytics-service")}  ← fully caught up");
        }
    }

} // namespace PubSubAndAsync
