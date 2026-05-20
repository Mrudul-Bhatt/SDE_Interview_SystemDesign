// Q3. Implement Idempotent Payment Processing
//
// Demonstrate how an idempotency key prevents duplicate charges when
// clients retry failed requests. Show key-based deduplication, in-flight
// detection, TTL expiry, and concurrent retry safety.
//
// Core mechanism
// ───────────────
// Client generates UUID before sending → includes in every retry attempt
// Server:
//   SET key "processing" NX EX ttl
//     → success: first time seen → execute the operation
//     → failure: key exists
//         status=Processing → IN_FLIGHT (caller retries later)
//         status=Completed  → REPLAY   (return cached result, no re-execution)
//
// Why this matters
// ─────────────────
// Without idempotency: network timeout → client retries → double charge
// With idempotency: retry finds cached result → returns same response
//
// Complexity: Check O(1), Store O(1) — both Redis SET/GET operations

using System;
using System.Collections.Generic;
using System.Threading;

namespace ResiliencePatterns
{
    // -------------------------------------------------------------------------
    // IdempotencyStatus
    // -------------------------------------------------------------------------
    public enum IdempotencyStatus
    {
        Processing, // key claimed, operation in progress
        Completed   // operation done, result stored
    }

    // -------------------------------------------------------------------------
    // IdempotencyEntry
    // -------------------------------------------------------------------------
    public class IdempotencyEntry
    {
        public IdempotencyStatus Status;
        public string            Result;    // serialized response; null while Processing
        public DateTime          ExpiresAt;
    }

    // -------------------------------------------------------------------------
    // IdempotencyStore
    // -------------------------------------------------------------------------
    // Simulates a Redis key store (or a DB idempotency_keys table).
    //
    // TryBegin  →  SET key "processing" NX EX ttl
    //              returns true if key was freshly claimed (caller should execute)
    //              returns false if key already exists (caller checks TryGet)
    //
    // Complete  →  update entry from Processing to Completed with the result
    //
    // TryGet    →  GET key (returns null if missing or expired)
    //
    // The internal _lock simulates Redis single-threaded atomicity — without it,
    // two concurrent retries could both pass the "key absent" check and both
    // execute the payment.
    public class IdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyEntry> _store
            = new Dictionary<string, IdempotencyEntry>();
        private readonly object _lock = new object();

        public bool TryBegin(string key, int ttlMs)
        {
            lock (_lock)
            {
                if (_store.TryGetValue(key, out IdempotencyEntry existing) &&
                    DateTime.UtcNow < existing.ExpiresAt)
                {
                    return false; // key alive — do not overwrite
                }

                // Key absent or expired — claim it with "processing" status.
                _store[key] = new IdempotencyEntry
                {
                    Status    = IdempotencyStatus.Processing,
                    Result    = null,
                    ExpiresAt = DateTime.UtcNow.AddMilliseconds(ttlMs)
                };
                return true;
            }
        }

        public void Complete(string key, string result)
        {
            lock (_lock)
            {
                if (_store.TryGetValue(key, out IdempotencyEntry entry))
                {
                    entry.Status = IdempotencyStatus.Completed;
                    entry.Result = result;
                }
            }
        }

        public IdempotencyEntry TryGet(string key)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(key, out IdempotencyEntry entry))
                    return null;
                if (DateTime.UtcNow >= entry.ExpiresAt)
                {
                    _store.Remove(key);
                    return null;
                }
                return entry;
            }
        }
    }

    // -------------------------------------------------------------------------
    // PaymentResult
    // -------------------------------------------------------------------------
    public class PaymentResult
    {
        public bool    Success;
        public string  ChargeId;
        public decimal Amount;
        public string  Message;

        public override string ToString() =>
            $"{{ success={Success}, chargeId={ChargeId}, amount=${Amount:F2}, msg=\"{Message}\" }}";
    }

    // -------------------------------------------------------------------------
    // PaymentService
    // -------------------------------------------------------------------------
    // Wraps the payment gateway with idempotency semantics.
    //
    // ProcessPayment returns three possible outcomes:
    //   EXECUTED  → first call; payment made; result stored
    //   REPLAY    → retry; cached result returned; no payment made
    //   IN_FLIGHT → concurrent duplicate; caller should back off and retry
    public class PaymentService
    {
        private readonly IdempotencyStore _store;
        private          int              _chargeCounter = 0;

        // Short TTL for demo; production uses 24 hours (Stripe standard).
        private const int TtlMs = 2000;

        public int TotalChargesExecuted => _chargeCounter;

        public PaymentService(IdempotencyStore store) => _store = store;

        public (PaymentResult result, string outcome) ProcessPayment(
            string  userId,
            decimal amount,
            string  idempotencyKey,
            int     simulatedGatewayDelayMs = 20)
        {
            // ── Step 1: check whether we have already seen this key ───────────
            IdempotencyEntry existing = _store.TryGet(idempotencyKey);
            if (existing != null)
            {
                if (existing.Status == IdempotencyStatus.Processing)
                {
                    // Another request is mid-flight for this exact key.
                    // Return 409-equivalent: "try again shortly".
                    return (null, "IN_FLIGHT");
                }
                // Completed: return the stored result without touching the gateway.
                return (Deserialize(existing.Result), "REPLAY");
            }

            // ── Step 2: atomically claim the key before executing ─────────────
            // Claiming BEFORE execution means: if we crash between claim and
            // Complete(), the next retry finds status=Processing → IN_FLIGHT →
            // the client retries later and gets the final result once we recover.
            // (Alternatively, a watchdog can expire stuck "processing" entries.)
            if (!_store.TryBegin(idempotencyKey, TtlMs))
            {
                // A concurrent thread just claimed it between our TryGet and TryBegin.
                return (null, "IN_FLIGHT");
            }

            // ── Step 3: call the payment gateway ─────────────────────────────
            Thread.Sleep(simulatedGatewayDelayMs); // simulate network latency
            int chargeNum = Interlocked.Increment(ref _chargeCounter);

            PaymentResult result = new PaymentResult
            {
                Success  = true,
                ChargeId = $"ch_{chargeNum:D4}",
                Amount   = amount,
                Message  = $"Charged ${amount:F2} to {userId}"
            };

            // ── Step 4: store result so every subsequent retry gets the cache ─
            _store.Complete(idempotencyKey, Serialize(result));
            return (result, "EXECUTED");
        }

        // Minimal pipe-delimited serialization — avoids any external dependency.
        private static string Serialize(PaymentResult r) =>
            $"{r.Success}|{r.ChargeId}|{r.Amount}|{r.Message}";

        private static PaymentResult Deserialize(string s)
        {
            string[] p = s.Split('|');
            return new PaymentResult
            {
                Success  = bool.Parse(p[0]),
                ChargeId = p[1],
                Amount   = decimal.Parse(p[2]),
                Message  = p[3]
            };
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            var store   = new IdempotencyStore();
            var payment = new PaymentService(store);

            // =================================================================
            // Scenario 1 — Normal payment: first call executes, result stored
            // Shows the happy path: key not seen → execute → cache result.
            // Outcome: EXECUTED; TotalChargesExecuted = 1.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: First call — payment executed and cached         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            string key1 = Guid.NewGuid().ToString();
            var (r1, outcome1) = payment.ProcessPayment("user-42", 99.99m, key1);
            Console.WriteLine($"\n  Outcome : {outcome1}");
            Console.WriteLine($"  Result  : {r1}");
            Console.WriteLine($"  Total charges executed: {payment.TotalChargesExecuted}  ← must be 1");

            // =================================================================
            // Scenario 2 — Network timeout retry: same key, no second charge
            // Client timed out and resends the request with the same key.
            // Server finds the key (Completed) and returns the cached result.
            // No second call to the payment gateway — TotalChargesExecuted stays 1.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Retry with same key — cached, no second charge   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            int chargesBefore = payment.TotalChargesExecuted;

            // Same key1 — simulates client retry after timeout
            var (r2, outcome2) = payment.ProcessPayment("user-42", 99.99m, key1);
            Console.WriteLine($"\n  Outcome : {outcome2}  ← REPLAY");
            Console.WriteLine($"  Result  : {r2}");
            Console.WriteLine($"  ChargeId matches first call: {r1.ChargeId == r2.ChargeId}  ← true");
            Console.WriteLine($"  New charges executed: {payment.TotalChargesExecuted - chargesBefore}  ← must be 0");

            // =================================================================
            // Scenario 3 — In-flight detection: concurrent duplicate request
            // Two threads submit the same payment simultaneously (double-click).
            // Thread 1 claims the key and starts processing (slow gateway).
            // Thread 2 arrives while Thread 1 is still in-flight → IN_FLIGHT.
            // Thread 2 then retries after Thread 1 completes → REPLAY.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Concurrent duplicate — in-flight detection        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            string key3 = Guid.NewGuid().ToString();

            string  thread2Outcome = null;
            string  thread1Outcome = null;
            PaymentResult thread2Result = null;

            // Thread 1: slow gateway (150ms) to give Thread 2 time to arrive
            var t1 = new Thread(() =>
            {
                var (res, out3) = payment.ProcessPayment("user-7", 50.00m, key3, simulatedGatewayDelayMs: 150);
                thread1Outcome = out3;
            });

            // Thread 2: arrives 30ms after Thread 1 starts — while Thread 1 is in-flight
            var t2 = new Thread(() =>
            {
                Thread.Sleep(30); // let Thread 1 claim the key first
                var (res, out3) = payment.ProcessPayment("user-7", 50.00m, key3);
                thread2Outcome = out3;
                thread2Result  = res;
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            Console.WriteLine($"\n  Thread 1 outcome: {thread1Outcome}  ← EXECUTED");
            Console.WriteLine($"  Thread 2 outcome: {thread2Outcome}  ← IN_FLIGHT (key claimed, not done yet)");

            // Thread 2 now retries after Thread 1 has completed
            var (r3Retry, outcome3Retry) = payment.ProcessPayment("user-7", 50.00m, key3);
            Console.WriteLine($"  Thread 2 retry  : {outcome3Retry}  ← REPLAY (now completed)");
            Console.WriteLine($"  Thread 2 result : {r3Retry}");

            // =================================================================
            // Scenario 4 — Different keys, same payload: both execute
            // User legitimately pays twice (two separate orders, same amount).
            // Each request carries a different idempotency key → both execute.
            // This shows keys scope the dedup to a single logical operation,
            // not to a payload hash — same payload ≠ same request.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Different keys, same payload — both execute       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            string keyA = Guid.NewGuid().ToString();
            string keyB = Guid.NewGuid().ToString();

            int chargesBeforeS4 = payment.TotalChargesExecuted;
            var (r4a, o4a) = payment.ProcessPayment("user-5", 25.00m, keyA);
            var (r4b, o4b) = payment.ProcessPayment("user-5", 25.00m, keyB);

            Console.WriteLine($"\n  Request A (key={keyA[..8]}...): {o4a}  → {r4a.ChargeId}");
            Console.WriteLine($"  Request B (key={keyB[..8]}...): {o4b}  → {r4b.ChargeId}");
            Console.WriteLine($"  New charges: {payment.TotalChargesExecuted - chargesBeforeS4}  ← 2 (intentional, different operations)");

            // =================================================================
            // Scenario 5 — TTL expiry: key expires, next request re-executes
            // The idempotency key TTL is short (2s in demo; 24h in production).
            // After expiry the store forgets the key — the next call is treated
            // as a brand new request and re-executes. This prevents the store
            // from growing unboundedly while still protecting the retry window.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: TTL expiry — key forgotten, re-execution allowed  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            string key5 = Guid.NewGuid().ToString();
            var (r5first, o5first) = payment.ProcessPayment("user-9", 10.00m, key5);
            Console.WriteLine($"\n  First call : {o5first} → {r5first.ChargeId}");

            Console.WriteLine("  Waiting 2200ms for TTL to expire...");
            Thread.Sleep(2200);

            // Same key, but the store has expired it — treated as a new request
            var (r5second, o5second) = payment.ProcessPayment("user-9", 10.00m, key5);
            Console.WriteLine($"  After expiry: {o5second}  ← EXECUTED (not REPLAY — key was gone)");
            Console.WriteLine($"  New chargeId: {r5second.ChargeId}  ← different from first ({r5first.ChargeId})");
            Console.WriteLine($"\n  Total charges across all scenarios: {payment.TotalChargesExecuted}");
        }
    }

} // namespace ResiliencePatterns
