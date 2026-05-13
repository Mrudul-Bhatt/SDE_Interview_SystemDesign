// Q2. Implement an Idempotency Key Store
// Clients send a unique key with each request. If the server has already processed
// that key, return the cached result instead of processing again. Keys expire after
// 24 hours. This prevents duplicate charges on payment retries.
//
// Key design points:
//
// 1. Key generation — client's responsibility:
//    → Use UUID v4: Guid.NewGuid().ToString()
//    → Or hash of (user_id + amount + timestamp_minute)
//    → Must be unique per logical request and stable across retries
//
// 2. TTL — how long to keep keys:
//    → Stripe: 24 hours
//    → AWS SQS: 5 minutes (MessageDeduplicationId)
//    → Rule: TTL >= your client's retry window
//
// 3. Storage — where to store keys:
//    → Redis with SETNX (set if not exists) — atomic, built-in TTL, distributed
//    → Database with unique constraint on idempotency_key column
//    → In-memory (this impl) — simple demo, lost on restart
//
// 4. Database pattern (production):
//    INSERT INTO idempotency_keys (key, result, expires_at)
//    VALUES (@key, @result, @expires)
//    ON CONFLICT (key) DO NOTHING;
//    -- Then SELECT result WHERE key = @key
//
// Complexity: Execute O(1) with hash map, O(1) with Redis SETNX

using System.Collections.Generic;
using System.Linq;

namespace PerformanceConcepts
{

    // ---------------------------------------------------------------------------
    // IdempotencyStore<TResult> — deduplicates requests by key, caches results
    // ---------------------------------------------------------------------------
    public class IdempotencyStore<TResult>
    {
        // Private inner class groups key metadata together.
        // init-only properties make entries immutable after creation — once a result
        // is stored for a key it should never change (idempotency guarantee).
        private class Entry
        {
            public TResult Result { get; init; } = default!;
            public DateTime ExpiresAt { get; init; }

            // Computed property so expiry check is always relative to UtcNow.
            // We use UtcNow (not Now) to avoid daylight-saving-time edge cases.
            public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        }

        // Dictionary provides O(1) key lookup — critical because every inbound
        // request must check the store before doing any real work.
        private readonly Dictionary<string, Entry> _store = new();

        private readonly TimeSpan _ttl;

        // Lock needed because multiple HTTP threads may call Execute() concurrently
        // for different idempotency keys. Without it, two threads could both read
        // "key not found", both execute the action, and both store a result —
        // defeating the entire purpose.
        private readonly object _lock = new();

        public IdempotencyStore(TimeSpan? ttl = null)
        {
            // Default 24h matches Stripe's idempotency key lifetime.
            // Override for shorter-lived operations (e.g. 5 min for job deduplication).
            _ttl = ttl ?? TimeSpan.FromHours(24);
        }

        // Returns (result, wasDuplicate).
        // wasDuplicate=true means the action was NOT called — the cached result was returned.
        // wasDuplicate=false means the action ran and its result was stored for future duplicates.
        public (TResult Result, bool WasDuplicate) Execute(string key, Func<TResult> action)
        {
            // Entire check-then-act is inside the lock so no two threads can both
            // conclude "key is new" and both run the action simultaneously.
            lock (_lock)
            {
                if (_store.TryGetValue(key, out var existing))
                {
                    if (!existing.IsExpired)
                    {
                        // Key exists and is still within TTL — return cached result.
                        // The action (e.g. charging a card) is NOT called again.
                        Console.WriteLine($"  [IDEM] Key '{key}' already processed — returning cached result");
                        return (existing.Result, WasDuplicate: true);
                    }

                    // Key expired — remove it so the next block can treat this as a fresh request.
                    // This handles the case where a client retries after 25 hours: the old key
                    // is gone, so the new attempt is processed as a legitimate new request.
                    _store.Remove(key);
                }

                // Key is genuinely new (or just expired) — execute the action.
                Console.WriteLine($"  [IDEM] Key '{key}' is new — processing...");
                TResult result = action();

                // Store the result NOW so any concurrent or subsequent retry with
                // the same key will hit the cache and skip the action.
                _store[key] = new Entry
                {
                    Result = result,
                    ExpiresAt = DateTime.UtcNow + _ttl
                };

                return (result, WasDuplicate: false);
            }
        }

        // Count of non-expired keys — useful for monitoring how full the store is.
        // In production this would be a Redis DBSIZE or a database COUNT query.
        public int ActiveKeys => _store.Count(kv => !kv.Value.IsExpired);
    }

    // ---------------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            Console.WriteLine("╔══════════════════════════════════════╗");
            Console.WriteLine("║  Idempotency Key Store Demo          ║");
            Console.WriteLine("╚══════════════════════════════════════╝");

            // Short TTL (10s) so we can demo expiry without waiting 24 hours.
            var store = new IdempotencyStore<string>(ttl: TimeSpan.FromSeconds(10));

            // Simulates a payment processor — each real charge gets a unique txn ID.
            // chargeCount lets us verify the action ran exactly once despite retries.
            int chargeCount = 0;
            string ChargeCard(string amount)
            {
                chargeCount++;
                return $"txn_{chargeCount:000} — charged {amount}";
            }

            // ===================================================================
            // Scenario 1 — First request succeeds, retry is safely deduplicated
            // ===================================================================
            Console.WriteLine("\n=== First request (client generates idempotency key) ===");
            string key = "client-req-550e8400";
            var (result1, dup1) = store.Execute(key, () => ChargeCard("$99.99"));
            Console.WriteLine($"  Result: {result1}  (duplicate: {dup1})");

            Console.WriteLine("\n=== Network failure — client retries with SAME key ===");
            var (result2, dup2) = store.Execute(key, () => ChargeCard("$99.99"));
            Console.WriteLine($"  Result: {result2}  (duplicate: {dup2})");
            Console.WriteLine($"  Charge count: {chargeCount}  ← still 1, no double charge!");

            // ===================================================================
            // Scenario 2 — Different key = genuinely different request
            // ===================================================================
            Console.WriteLine("\n=== Different key = different logical request ===");
            var (result3, dup3) = store.Execute("client-req-661f9511", () => ChargeCard("$49.99"));
            Console.WriteLine($"  Result: {result3}  (duplicate: {dup3})");
            Console.WriteLine($"  Charge count: {chargeCount}  ← now 2, correct");

            // ===================================================================
            // Scenario 3 — Many retries, all safely absorbed
            // ===================================================================
            Console.WriteLine("\n=== Same key retried 5 more times (safe) ===");
            for (int i = 0; i < 5; i++)
                store.Execute(key, () => ChargeCard("$99.99"));
            Console.WriteLine($"  Charge count after 5 retries: {chargeCount}  ← still 2!");
            Console.WriteLine($"  Active keys in store: {store.ActiveKeys}");

            // ===================================================================
            // Scenario 4 — Key expires, next retry is treated as a new request
            // ===================================================================
            Console.WriteLine("\n=== Waiting 11s for TTL to expire... ===");
            Thread.Sleep(11000);

            Console.WriteLine("\n=== Retry after TTL expiry — treated as new request ===");
            var (result4, dup4) = store.Execute(key, () => ChargeCard("$99.99"));
            Console.WriteLine($"  Result: {result4}  (duplicate: {dup4})");
            Console.WriteLine($"  Charge count: {chargeCount}  ← now 3 (TTL expired, re-processed)");
        }
    }

} // namespace PerformanceConcepts
