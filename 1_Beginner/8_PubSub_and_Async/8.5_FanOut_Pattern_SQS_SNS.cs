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
    // FanOutBroker
    // -------------------------------------------------------------------------
    public class FanOutBroker
    {
        // Each subscriber service gets its own isolated Queue<object>.
        // Isolation is the core guarantee: if email-service's queue grows to
        // 100k messages, inventory-service's queue is at 0 and unaffected.
        // In AWS, each Queue<> maps to a physical SQS queue with its own
        // throughput limits, visibility timeouts, and dead-letter queue.
        private readonly Dictionary<string, Queue<object>> _queues
            = new Dictionary<string, Queue<object>>();

        // One lock protecting _queues. Publish and Consume must not run
        // concurrently — Consume dequeuing while Publish is enqueuing could
        // corrupt the Queue<> internal state (not thread-safe on its own).
        private readonly object _lock = new object();

        public void AddSubscriberQueue(string serviceName)
        {
            lock (_lock)
            {
                _queues[serviceName] = new Queue<object>();
                Console.WriteLine($"[FAN-OUT] Queue created for: {serviceName}");
            }
        }

        // Fan-out: enqueue a COPY of the message reference into every queue.
        // "Copy" here means the same object reference goes into each queue —
        // each queue entry is independent, but they all point to the same
        // immutable message object. If messages were mutable, each service
        // would need a deep-cloned copy to avoid cross-service data corruption.
        public void Publish(string topic, object message)
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
        public object Consume(string serviceName)
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
            fanOut.Publish("order.placed", new { OrderId = 1001, Total = 99.99 });
            fanOut.Publish("order.placed", new { OrderId = 1002, Total = 49.99 });
            fanOut.Publish("order.placed", new { OrderId = 1003, Total = 149.99 });

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
            object msg;
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
