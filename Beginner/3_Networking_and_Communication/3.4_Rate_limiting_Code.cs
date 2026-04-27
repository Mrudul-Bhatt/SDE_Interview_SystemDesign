// Q1. Implement a Token Bucket Rate Limiter
// Allow up to N requests per second per client. Requests beyond the limit are rejected.
// The bucket refills at a steady rate, allowing short bursts up to the bucket capacity.
// Bucket capacity = 10 tokens | Refill rate = 10 tokens/second (1 token every 100ms)

// Q2. Implement a Sliding Window Rate Limiter
// Stricter than token bucket — counts requests in the last N seconds exactly.
// Rejects if count exceeds the limit. No burst allowance beyond the window limit.

using System;
using System.Collections.Generic;
using System.Threading; // Thread.Sleep

// ---------------------------------------------------------------------------
// Q1 — Token Bucket Rate Limiter
// ---------------------------------------------------------------------------
public class TokenBucketRateLimiter
{
    // Per-client bucket: tracks current token count and the last time it was refilled.
    private class Bucket
    {
        public double Tokens { get; set; } // current token count (fractional, e.g. 4.7)
        public DateTime LastRefill { get; set; } // when tokens were last added
    }

    private readonly double _capacity;   // max tokens the bucket can hold (= max burst size)
    private readonly double _refillRate; // tokens added per second (= sustained req/sec limit)

    // One bucket per client ID — clients are isolated from each other.
    private readonly Dictionary<string, Bucket> _buckets = new();

    // Single lock because Allow() reads and writes the bucket atomically.
    // Without this, two concurrent requests could both see tokens >= 1 and both be allowed.
    private readonly object _lock = new();

    public TokenBucketRateLimiter(double capacity, double refillPerSecond)
    {
        _capacity = capacity;
        _refillRate = refillPerSecond;
    }

    // Returns true if the request is allowed; false if rate-limited.
    public bool Allow(string clientId)
    {
        lock (_lock)
        {
            // First request from this client — create a full bucket.
            if (!_buckets.TryGetValue(clientId, out var bucket))
            {
                bucket = new Bucket { Tokens = _capacity, LastRefill = DateTime.UtcNow };
                _buckets[clientId] = bucket;
            }

            // Refill: calculate how many tokens to add since the last refill.
            // elapsed * _refillRate = tokens earned during idle time.
            // Math.Min clamps to capacity so the bucket never overflows.
            double elapsed = (DateTime.UtcNow - bucket.LastRefill).TotalSeconds;
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed * _refillRate);
            bucket.LastRefill = DateTime.UtcNow;

            // Consume one token if available.
            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens--;
                return true;  // request allowed
            }

            return false; // bucket empty — request rejected
        }
    }
}

// ---------------------------------------------------------------------------
// Q2 — Sliding Window Rate Limiter
// ---------------------------------------------------------------------------

// Token Bucket vs Sliding Window:
//   Token Bucket  (capacity=5, rate=5/s): can burst 5 at t=0, then 4 more at t=0.9s → 9 in 1s
//   Sliding Window (limit=5 per second):  strictly ≤5 in any rolling 1-second window

public class SlidingWindowRateLimiter
{
    private readonly int _limit;  // max requests allowed in the window
    private readonly TimeSpan _window; // the rolling time window (e.g. 1 second)

    // Per-client queue of request timestamps, oldest at front (FIFO).
    // WHY Queue: we only ever add to the back and evict from the front — O(1) for both.
    private readonly Dictionary<string, Queue<DateTime>> _timestamps = new();
    private readonly object _lock = new();

    public SlidingWindowRateLimiter(int requestLimit, TimeSpan window)
    {
        _limit = requestLimit;
        _window = window;
    }

    // Returns true if the request falls within the limit for the rolling window.
    public bool Allow(string clientId)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            DateTime windowStart = now - _window; // oldest timestamp still inside the window

            if (!_timestamps.TryGetValue(clientId, out var timestamps))
            {
                timestamps = new Queue<DateTime>();
                _timestamps[clientId] = timestamps;
            }

            // Evict timestamps that have slid out of the window — they no longer count.
            while (timestamps.Count > 0 && timestamps.Peek() < windowStart)
                timestamps.Dequeue();

            // If we're under the limit, record this request and allow it.
            if (timestamps.Count < _limit)
            {
                timestamps.Enqueue(now);
                return true;
            }

            return false; // at or over the limit — reject
        }
    }

    // Returns how many requests this client has made in the current window.
    public int GetRequestCount(string clientId)
    {
        lock (_lock)
        {
            if (!_timestamps.TryGetValue(clientId, out var timestamps)) return 0;
            DateTime windowStart = DateTime.UtcNow - _window;
            while (timestamps.Count > 0 && timestamps.Peek() < windowStart)
                timestamps.Dequeue();
            return timestamps.Count;
        }
    }
}

// ---------------------------------------------------------------------------
// Entry point — demos for both questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Token Bucket (capacity=5, refill=5/s)
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Token Bucket Rate Limiter       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var tokenLimiter = new TokenBucketRateLimiter(capacity: 5, refillPerSecond: 5);

        Console.WriteLine("\n=== Burst: 7 instant requests (bucket holds 5) ===");
        for (int i = 0; i < 7; i++)
        {
            bool allowed = tokenLimiter.Allow("user:alice");
            Console.WriteLine($"  Request {i + 1}: {(allowed ? "ALLOWED" : "DENIED")}");
        }
        // Requests 1-5: ALLOWED (drains the bucket)
        // Requests 6-7: DENIED  (bucket empty)

        Console.WriteLine("\n=== Wait 1 second → bucket refills to 5 ===");
        Thread.Sleep(1000);
        Console.WriteLine($"  Request after 1s: {(tokenLimiter.Allow("user:alice") ? "ALLOWED" : "DENIED")}");

        // ===================================================================
        // Q2 DEMO — Sliding Window (3 requests per 2-second window)
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Sliding Window Rate Limiter     ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var windowLimiter = new SlidingWindowRateLimiter(requestLimit: 3, window: TimeSpan.FromSeconds(2));

        Console.WriteLine("\n=== 4 instant requests (limit=3) ===");
        Console.WriteLine($"  Request 1: {(windowLimiter.Allow("user:bob") ? "ALLOWED" : "DENIED")}"); // count: 1
        Console.WriteLine($"  Request 2: {(windowLimiter.Allow("user:bob") ? "ALLOWED" : "DENIED")}"); // count: 2
        Console.WriteLine($"  Request 3: {(windowLimiter.Allow("user:bob") ? "ALLOWED" : "DENIED")}"); // count: 3
        Console.WriteLine($"  Request 4: {(windowLimiter.Allow("user:bob") ? "ALLOWED" : "DENIED")}"); // DENIED — at limit

        Console.WriteLine($"\n  In-window count: {windowLimiter.GetRequestCount("user:bob")}");

        Console.WriteLine("\n=== Wait 2.1 seconds → window slides past all timestamps ===");
        Thread.Sleep(2100);

        Console.WriteLine($"  Request after 2.1s: {(windowLimiter.Allow("user:bob") ? "ALLOWED" : "DENIED")}"); // ALLOWED
        Console.WriteLine($"  In-window count: {windowLimiter.GetRequestCount("user:bob")}");
    }
}
