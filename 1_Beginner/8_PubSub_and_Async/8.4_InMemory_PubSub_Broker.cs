// Q1. Implement an In-Memory Pub/Sub Broker
//
// Publishers send messages to named topics. All subscribers of that topic
// receive a copy. Subscribers can unsubscribe at any time. The publisher
// has no knowledge of who is subscribed — complete decoupling.
//
// Key Difference from a Message Queue
// ─────────────────────────────────────
// Message Queue:  one message → one consumer (competing consumers)
// Pub/Sub:        one message → ALL subscribers (fan-out broadcast)
//
// Queue analogy:  task list — one worker picks up each task
// Pub/Sub analogy: radio broadcast — every listener hears every transmission
//
// Why Pub/Sub Enables Open/Closed Systems
// ─────────────────────────────────────────
// Without Pub/Sub — Order Service must know every downstream service:
//   order.placed →
//     send_confirmation_email(order)
//     decrement_inventory(order)
//     record_analytics(order)    ← must modify Order Service every time
//     run_fraud_check(order)       a new service needs order events
//
// With Pub/Sub — Order Service publishes once and never changes:
//   broker.Publish("order.placed", order)
//   New Loyalty Service just subscribes — Order Service is untouched.
//   This is the Open/Closed Principle at the system level.
//
// Complexity: Subscribe O(1), Unsubscribe O(n), Publish O(subscribers)

using System;
using System.Collections.Generic;
using System.Linq;

namespace PubSubAndAsync
{
    // -------------------------------------------------------------------------
    // PubSubBroker
    // -------------------------------------------------------------------------
    public class PubSubBroker
    {
        // topic → list of (subscriberId, handler)
        // Each entry in the list is a named subscriber and its callback.
        // The subscriber ID lets us deduplicate on re-subscribe and find the
        // right entry to remove on unsubscribe — without it we'd need reference
        // equality on the delegate, which is unreliable for lambda captures.
        private readonly Dictionary<string, List<(string Id, Action<string, object> Handler)>>
            _subscribers = new Dictionary<string, List<(string Id, Action<string, object> Handler)>>();

        // Protects _subscribers from concurrent Subscribe/Unsubscribe/Publish calls.
        // We release the lock before invoking handlers (see snapshot pattern below)
        // so handlers can themselves call Subscribe/Unsubscribe without deadlocking.
        private readonly object _lock = new object();

        public void Subscribe(string topic, string subscriberId, Action<string, object> handler)
        {
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(topic))
                    _subscribers[topic] = new List<(string, Action<string, object>)>();

                // Idempotent: a service that restarts and re-subscribes should not
                // end up with two entries. Duplicate entries would deliver each
                // message twice to the same handler — almost always a bug.
                if (_subscribers[topic].Any(s => s.Id == subscriberId))
                {
                    Console.WriteLine($"[BROKER] {subscriberId} already subscribed to '{topic}'");
                    return;
                }

                _subscribers[topic].Add((subscriberId, handler));
                Console.WriteLine($"[BROKER] {subscriberId} subscribed to '{topic}'  " +
                                  $"(total: {_subscribers[topic].Count})");
            }
        }

        public void Unsubscribe(string topic, string subscriberId)
        {
            lock (_lock)
            {
                if (!_subscribers.TryGetValue(topic, out var subs)) return;
                int removed = subs.RemoveAll(s => s.Id == subscriberId);
                if (removed > 0)
                    Console.WriteLine($"[BROKER] {subscriberId} unsubscribed from '{topic}'");
            }
        }

        // Fan-out: deliver to ALL current subscribers of the topic.
        // Returns the number of subscribers notified.
        public int Publish(string topic, object message)
        {
            List<(string Id, Action<string, object> Handler)> snapshot;

            lock (_lock)
            {
                if (!_subscribers.TryGetValue(topic, out var subs) || subs.Count == 0)
                {
                    Console.WriteLine($"[BROKER] Published to '{topic}' — no subscribers");
                    return 0;
                }

                // Copy the subscriber list before releasing the lock.
                // We MUST NOT call handlers while holding the lock — a handler that
                // calls Subscribe/Unsubscribe would deadlock trying to acquire _lock.
                // The snapshot is a shallow copy of the list (not the handlers themselves),
                // so new subscribers added after Publish starts don't receive this message.
                snapshot = new List<(string, Action<string, object>)>(subs);
            }

            Console.WriteLine($"[BROKER] Publishing to '{topic}' → {snapshot.Count} subscriber(s)");

            foreach (var (id, handler) in snapshot)
            {
                try
                {
                    handler(topic, message);
                }
                catch (Exception ex)
                {
                    // One failing handler must not prevent others from receiving the message.
                    // In production, log the exception and route to a dead-letter queue or
                    // alerting system. Do NOT re-throw — that would skip remaining subscribers.
                    Console.WriteLine($"  [ERROR] {id} handler threw: {ex.Message}");
                }
            }

            return snapshot.Count;
        }

        public void PrintTopics()
        {
            lock (_lock)
            {
                Console.WriteLine("\n[BROKER] Active topics:");
                foreach (var (topic, subs) in _subscribers)
                    Console.WriteLine($"  '{topic}': [{string.Join(", ", subs.Select(s => s.Id))}]");
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
            var broker = new PubSubBroker();

            // =================================================================
            // Scenario 1 — Four services subscribe; Order Service publishes once
            // Publisher knows nothing about subscribers — it just calls Publish.
            // All four handlers fire independently in the same thread (synchronous
            // delivery). In production (Kafka, SNS) delivery is asynchronous.
            // =================================================================
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: 4 subscribers — publish fires all handlers   ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            broker.Subscribe("order.placed", "email-service", (t, m) =>
                Console.WriteLine($"  [email-service]     Sending confirmation for: {m}"));
            broker.Subscribe("order.placed", "inventory-service", (t, m) =>
                Console.WriteLine($"  [inventory-service] Reserving stock for: {m}"));
            broker.Subscribe("order.placed", "analytics-service", (t, m) =>
                Console.WriteLine($"  [analytics-service] Recording sale: {m}"));
            broker.Subscribe("order.placed", "fraud-service", (t, m) =>
                Console.WriteLine($"  [fraud-service]     Running fraud check on: {m}"));

            broker.PrintTopics();

            Console.WriteLine("\n  Order Service publishes (knows nothing about subscribers):");
            broker.Publish("order.placed", new { OrderId = 1001, Total = 99.99, UserId = 42 });

            // =================================================================
            // Scenario 2 — New service subscribes; zero changes to Order Service
            // This is the Open/Closed Principle: Order Service is open for extension
            // (new subscribers) but closed for modification (its code never changes).
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: New service joins without touching publisher  ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            broker.Subscribe("order.placed", "loyalty-service", (t, m) =>
                Console.WriteLine($"  [loyalty-service]   Adding points for: {m}"));

            Console.WriteLine("\n  Same Publish call — now 5 subscribers receive it:");
            broker.Publish("order.placed", new { OrderId = 1002, Total = 49.99, UserId = 7 });

            // =================================================================
            // Scenario 3 — A service unsubscribes; remaining services are unaffected
            // Shows dynamic subscriber management: the broker delivers only to
            // currently active subscribers — no stale entries receive messages.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Service unsubscribes — others unaffected      ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            broker.Unsubscribe("order.placed", "fraud-service");
            Console.WriteLine("\n  Publish after fraud-service unsubscribes:");
            broker.Publish("order.placed", new { OrderId = 1003, Total = 199.99, UserId = 15 });
        }
    }

} // namespace PubSubAndAsync
