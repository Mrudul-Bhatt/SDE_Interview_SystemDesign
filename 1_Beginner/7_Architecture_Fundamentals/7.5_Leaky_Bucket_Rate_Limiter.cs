// Q2. Implement a Leaky Bucket Rate Limiter
//
// Requests enter a queue (the "bucket"). A background drain removes entries at
// a fixed rate. If the bucket is full, new requests are rejected immediately.
// This produces a perfectly smooth output rate regardless of bursty input.
//
// Leaky Bucket vs Token Bucket
// ─────────────────────────────
// Token Bucket:  tokens accumulate over time; a burst consumes saved tokens instantly
//                → Output is bursty up to capacity — mirrors spiky client behaviour
//
// Leaky Bucket:  every request queues behind the previous one; drain is fixed
//                → Output is always smooth — no bursts reach the downstream service
//
// Choose Leaky when protecting a service that simply cannot handle bursts:
//   → Database connection pool (max 100 concurrent queries)
//   → Third-party API with a hard rate ceiling (Stripe: 100 req/s)
//   → Video transcoding pipeline (3 concurrent jobs max)
//   → Any fixed-capacity processor: print queue, task queue, etc.
//
// In practice a message queue (RabbitMQ, SQS) IS a leaky bucket:
//   → Producer publishes at any rate
//   → Consumer drains at its own fixed rate
//   → Queue buffers the difference
//   → Back-pressure kicks in when the queue fills
//
// Complexity: Allow O(1) amortized (drain is bounded by toRemove ≤ elapsed×rate),
//             Space O(clients × bucketCapacity)

using System;
using System.Collections.Generic;
using System.Threading;

namespace ArchitectureFundamentals
{
    // -------------------------------------------------------------------------
    // LeakyBucketRateLimiter
    // -------------------------------------------------------------------------
    public class LeakyBucketRateLimiter
    {
        private readonly int _bucketCapacity;        // max queued (in-flight) requests
        private readonly double _leakRatePerSecond;  // requests drained per second

        // BucketState bundles what we need per client into one value.
        // A struct (value type) inside the dictionary means the whole slot is
        // replaced when we update it — there is no shared mutable object to alias.
        private struct BucketState
        {
            public int Count;         // requests currently in the bucket
            public DateTime LastLeak; // timestamp of the last drain operation
        }

        // One entry per client ID. In production this would be Redis with atomic
        // Lua scripts so multiple API-server replicas share state. Dictionary here
        // keeps the demo dependency-free while still showing the algorithm.
        private readonly Dictionary<string, BucketState> _buckets = new Dictionary<string, BucketState>();

        // A single lock covering _buckets. The critical section is microsecond-fast
        // (arithmetic + dictionary ops), so contention is negligible in practice.
        // In production the Redis atomic primitives replace this lock entirely.
        private readonly object _lock = new object();

        public LeakyBucketRateLimiter(int bucketCapacity, double leakRatePerSecond)
        {
            _bucketCapacity = bucketCapacity;
            _leakRatePerSecond = leakRatePerSecond;
        }

        public bool Allow(string clientId)
        {
            // lock makes drain → count-check → enqueue a single atomic operation.
            // Without it: two threads could both read Count=4 (bucket not full),
            // both enqueue, and both return true — overflowing the bucket by 1.
            lock (_lock)
            {
                // DateTime.UtcNow instead of DateTime.Now: UtcNow is unaffected by
                // DST transitions. A "fall-back" DST shift makes Now go backwards
                // 1 hour, which would make elapsed negative and drain 0 requests
                // — the bucket would never leak. UtcNow is monotonically safe.
                DateTime now = DateTime.UtcNow;

                if (!_buckets.TryGetValue(clientId, out BucketState state))
                {
                    // First request from this client — open a fresh bucket.
                    // LastLeak = now so the first drain interval starts from this moment.
                    state = new BucketState { Count = 0, LastLeak = now };
                }

                // ── Drain step ────────────────────────────────────────────────
                // How many slots have "leaked" since the last time we checked?
                double elapsed = (now - state.LastLeak).TotalSeconds;
                int toRemove = (int)(elapsed * _leakRatePerSecond);
                // (int) truncates — we only count fully elapsed leak slots.
                // Partial slots stay in the bucket until the next call.

                if (toRemove > 0)
                {
                    // Cap at current count: can't drain more than what's there.
                    state.Count = Math.Max(0, state.Count - toRemove);
                    state.LastLeak = now; // reset the clock for the next drain interval
                }

                // ── Enqueue step ──────────────────────────────────────────────
                if (state.Count < _bucketCapacity)
                {
                    state.Count++;
                    _buckets[clientId] = state; // write back — BucketState is a value type
                    return true;
                }

                // Bucket is full — reject without modifying state.
                // We still write back because the drain above may have updated Count/LastLeak.
                _buckets[clientId] = state;
                return false;
            }
        }

        // How many requests are currently queued for this client.
        // Used in demos to visualise bucket depth after each request.
        public int QueueDepth(string clientId)
        {
            lock (_lock)
            {
                return _buckets.TryGetValue(clientId, out BucketState s) ? s.Count : 0;
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
            // Downstream DB can handle 3 queries/second; bucket holds at most 5.
            var limiter = new LeakyBucketRateLimiter(bucketCapacity: 5, leakRatePerSecond: 3);

            // =================================================================
            // Scenario 1 — Burst of 8 requests arrives instantly
            // First 5 fill the bucket; requests 6–8 are rejected (bucket full).
            // This is the key difference from Fixed Window: rejection is
            // proportional to remaining capacity, not a hard window reset.
            // =================================================================
            Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Burst of 8 requests — bucket capacity 5 ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

            for (int i = 1; i <= 8; i++)
            {
                bool allowed = limiter.Allow("service:order");
                int depth = limiter.QueueDepth("service:order");
                Console.WriteLine($"  Request {i:00}: {(allowed ? "ALLOWED " : "REJECTED")}  " +
                                  $"(queue depth: {depth})");
            }

            // =================================================================
            // Scenario 2 — Wait 1 second (leak drains 3 slots), then retry
            // After 1s at 3 req/s, 3 requests drain → depth drops from 5 to 2.
            // The bucket now has room for 3 more requests.
            // This shows the smooth output guarantee: exactly 3 leave per second
            // regardless of how bursty the input was.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: After 1 second — 3 drained, 2 remain    ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

            Console.WriteLine("\n  Sleeping 1 second to let the bucket drain...");
            Thread.Sleep(1000);

            int depthAfter = limiter.QueueDepth("service:order");
            Console.WriteLine($"  Queue depth after 1s: {depthAfter}  ← drained 3, 2 remain");

            bool next = limiter.Allow("service:order");
            Console.WriteLine($"  New request:          {(next ? "ALLOWED" : "REJECTED")}  " +
                              $"(queue depth: {limiter.QueueDepth("service:order")})");

            // =================================================================
            // Scenario 3 — Comparison: Leaky vs Token Bucket behaviour
            // Printed conceptually — shows WHY you pick one over the other.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Leaky vs Token Bucket (conceptual)       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

            Console.WriteLine(@"
  Setup: limit=5 req/s, capacity=5 tokens/slots

  ── Token Bucket ──────────────────────────────────────────────
  t=0s   5 tokens saved up.
         10 requests arrive → first 5 consume tokens instantly → ALLOWED
         Requests 6-10 → rejected (no tokens left)
         Downstream DB sees a burst of 5 queries at once.

  ── Leaky Bucket ──────────────────────────────────────────────
  t=0s   5 requests arrive → bucket fills (depth=5), ALLOWED
         Requests 6-10 → REJECTED immediately (bucket full)
  t=0.2s 1 request leaks out → DB receives 1 query
  t=0.4s 1 request leaks out → DB receives 1 query
  ... (one query every 333ms, perfectly metered)

  Key insight:
    Token Bucket → client controls burst shape up to capacity
    Leaky Bucket → downstream always sees a steady trickle, never a burst

  When to choose Leaky Bucket:
    → Protecting a connection pool (DB, Redis) that has a hard concurrency cap
    → Metering calls to a third-party API that charges per-second burst usage
    → Any pipeline where input variance must not reach the consumer");
        }
    }

} // namespace ArchitectureFundamentals
