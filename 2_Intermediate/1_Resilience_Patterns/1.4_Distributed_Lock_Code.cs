// Q2. Implement a Distributed Lock
//
// Simulate Redis SET NX EX in-memory to demonstrate how a distributed lock
// prevents race conditions across multiple servers. Show acquire, release,
// TTL auto-expiry, stale-release prevention, and concurrent booking.
//
// Core mechanism
// ───────────────
// SET lock_key <uuid> NX EX <ttl>
//   NX  → only set if key does Not eXist (atomic check-and-set)
//   EX  → expire after TTL seconds (prevents deadlock on holder crash)
//   uuid→ random token so only the holder can release (ownership proof)
//
// Release (atomic Lua equivalent):
//   if GET(key) == my_uuid: DEL(key)   ← must be atomic
//   else: do nothing                   ← someone else holds it now
//
// Why this matters
// ─────────────────
// Without distributed lock: two servers both read "seats=1",
//   both decrement, stock goes negative → data corruption
// With distributed lock: only one server enters the critical section,
//   the other either waits or fails fast
//
// Complexity: TryAcquire O(1), Acquire O(retries), Release O(1)

using System;
using System.Collections.Generic;
using System.Threading;

namespace ResiliencePatterns
{
    // -------------------------------------------------------------------------
    // RedisSimulator
    // -------------------------------------------------------------------------
    // Mimics the subset of Redis used for distributed locking:
    //   SET key value NX EX ttl  →  SetNxEx()
    //   Lua: if GET(key)==val: DEL(key)  →  DeleteIfOwner()
    //
    // The internal _lock on every method simulates Redis's single-threaded
    // command execution model — in real Redis, every command is atomic because
    // Redis runs on a single thread. Multiple callers here share one lock, so
    // SetNxEx is an indivisible check-and-set, just like real Redis.
    public class RedisSimulator
    {
        private class Entry
        {
            public string Value;
            public DateTime ExpiresAt; // DateTime.MaxValue if no TTL
        }

        private readonly Dictionary<string, Entry> _store = [];

        // Single lock for all operations — simulates Redis single-threaded atomicity.
        // Without this, two threads calling SetNxEx simultaneously could both pass
        // the "key doesn't exist" check and both believe they acquired the lock.
        private readonly object _lock = new();

        // SET key value NX EX ttlMs
        // Returns true if key was set (i.e. we acquired the lock).
        // Returns false if key already existed and had not expired.
        public bool SetNxEx(string key, string value, int ttlMs)
        {
            lock (_lock)
            {
                // Treat expired entries as non-existent — same as Redis TTL behaviour.
                if (_store.TryGetValue(key, out Entry existing) && DateTime.UtcNow < existing.ExpiresAt)
                {
                    return false; // key still alive — someone else holds the lock
                }

                _store[key] = new Entry
                {
                    Value = value,
                    ExpiresAt = DateTime.UtcNow.AddMilliseconds(ttlMs)
                };
                return true; // lock acquired
            }
        }

        // Atomic check-then-delete: the Lua script equivalent.
        // Only deletes if the stored value matches — prevents releasing another
        // holder's lock after our TTL expired and they reacquired it.
        // In real Redis this runs as a single Lua script (no interleaving possible).
        public bool DeleteIfOwner(string key, string value)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(key, out Entry entry))
                    return false; // already expired and removed

                if (DateTime.UtcNow >= entry.ExpiresAt)
                {
                    _store.Remove(key); // clean up expired entry
                    return false;       // our lock expired — do NOT release what we don't hold
                }

                if (entry.Value != value)
                    return false; // a different client holds the lock now

                _store.Remove(key);
                return true; // released successfully
            }
        }

        public string Get(string key)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(key, out Entry entry)) return null;
                if (DateTime.UtcNow >= entry.ExpiresAt) { _store.Remove(key); return null; }
                return entry.Value;
            }
        }

        // Shows what is actually stored in Redis right now — useful for understanding
        // the lock lifecycle. In production you'd use "redis-cli KEYS lock:*" + TTL.
        //
        // Example output while Server 1 holds the booking lock:
        //   [Redis state]
        //     lock:booking:seat-A5  →  token=3f2a1b...  expires=10:15:03.210  (2847 ms left)
        //
        // After release:
        //   [Redis state]  (empty — key deleted)
        public void PrintState(string label = "Redis state")
        {
            lock (_lock)
            {
                Console.WriteLine($"\n  [{label}]");
                bool any = false;
                foreach (var (key, entry) in _store)
                {
                    double msLeft = (entry.ExpiresAt - DateTime.UtcNow).TotalMilliseconds;
                    if (msLeft <= 0) continue; // skip expired
                    Console.WriteLine($"    {key}");
                    Console.WriteLine($"      token   = {entry.Value[..8]}...  (ownership proof)");
                    Console.WriteLine($"      expires = {entry.ExpiresAt:HH:mm:ss.fff}  ({msLeft:F0} ms left)");
                    any = true;
                }
                if (!any) Console.WriteLine("    (empty — no locks held)");
            }
        }
    }

    // -------------------------------------------------------------------------
    // DistributedLock
    // -------------------------------------------------------------------------
    public class DistributedLock : IDisposable
    {
        private readonly RedisSimulator _redis;
        private readonly string _lockKey;
        private readonly int _ttlMs;
        private readonly int _retryIntervalMs;
        private readonly int _maxRetries;

        // Our ownership token for this lock acquisition.
        // Null means we do not currently hold the lock.
        // A UUID so no two clients ever share a token — even if they acquire
        // the same key at different times, their tokens are distinct.
        private string _token = null;

        public bool IsHeld => _token != null;

        public DistributedLock(
            RedisSimulator redis,
            string lockKey,
            int ttlMs = 5000,
            int retryIntervalMs = 50,
            int maxRetries = 20)
        {
            _redis = redis;
            _lockKey = lockKey;
            _ttlMs = ttlMs;
            _retryIntervalMs = retryIntervalMs;
            _maxRetries = maxRetries;
        }

        // Non-blocking: attempt once, return false immediately if lock is held.
        // Use when you want to skip work rather than wait (e.g. cron dedup:
        // if another server is already running the job, this one does nothing).
        public bool TryAcquire()
        {
            // Guid.NewGuid gives a cryptographically unique token.
            // Using the server hostname or PID is not unique enough — two processes
            // on different servers could share a PID, causing false ownership matches.
            string token = Guid.NewGuid().ToString();

            if (_redis.SetNxEx(_lockKey, token, _ttlMs))
            {
                _token = token;
                return true;
            }
            return false;
        }

        // Blocking: retry until acquired or timeout exceeded.
        // Use when work MUST happen (e.g. booking — we must process the request,
        // just not at the same time as another server).
        public bool Acquire()
        {
            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                if (TryAcquire()) return true;

                // Small random jitter added to base retry interval prevents
                // "thundering herd": if 50 threads all retry at the exact same
                // millisecond, one wins and 49 immediately retry in lockstep.
                // Jitter spreads them out so the winner gets a head start.
                int jitter = new Random().Next(0, 20);
                Thread.Sleep(_retryIntervalMs + jitter);
            }
            return false; // timed out
        }

        // Release the lock. Returns false if we no longer own it (TTL expired).
        // Callers should check the return value — false means the critical section
        // may have been entered by another holder while we were still working.
        public bool Release()
        {
            if (_token == null) return false; // never acquired or already released

            bool released = _redis.DeleteIfOwner(_lockKey, _token);
            _token = null; // clear regardless — we no longer hold (or claim) the lock
            return released;
        }

        // IDisposable: ensures Release() is called even if an exception is thrown
        // inside the critical section. Use with `using` for automatic cleanup.
        public void Dispose() => Release();
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        private static readonly RedisSimulator Redis = new();

        // Shared state — represents a seat booking database row
        private static int _seatsAvailable = 1;
        private static string _bookedBy = null;
        private static int _doubleBookings = 0;
        private static int _successfulBookings = 0;

        public static void Main()
        {
            // =================================================================
            // Scenario 1 — Normal acquire, do work, release
            // Shows the basic flow: acquire lock → enter critical section →
            // release. IDisposable (using) guarantees release even on exception.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Normal acquire → work → release                 ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var lock1 = new DistributedLock(Redis, "lock:booking:seat-A5", ttlMs: 3000);

            bool acquired = lock1.Acquire();
            Console.WriteLine($"\n  Server 1 acquired: {acquired}");
            Redis.PrintState("Redis — lock held by Server 1");

            Console.WriteLine("\n  Server 1: reading seat status... booking seat A5...");
            Thread.Sleep(50); // simulate DB work
            Console.WriteLine("  Server 1: done. Releasing lock.");
            bool released = lock1.Release();
            Console.WriteLine($"  Server 1 released: {released}");
            Redis.PrintState("Redis — after release");

            // =================================================================
            // Scenario 2 — Two servers compete for the same lock
            // Server 1 holds the lock while Server 2 tries to acquire it.
            // Server 2's TryAcquire returns false immediately — it does not wait.
            // After Server 1 releases, Server 2 can acquire.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Two servers compete — only one wins              ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var server1Lock = new DistributedLock(Redis, "lock:inventory:item-99", ttlMs: 3000);
            var server2Lock = new DistributedLock(Redis, "lock:inventory:item-99", ttlMs: 3000);

            server1Lock.Acquire();
            Console.WriteLine($"\n  Server 1 acquired: {server1Lock.IsHeld}");
            Redis.PrintState("Redis — Server 1 holds lock:inventory:item-99");

            bool server2Got = server2Lock.TryAcquire(); // non-blocking — returns immediately
            Console.WriteLine($"\n  Server 2 TryAcquire (non-blocking): {server2Got}  ← false, lock held");
            Console.WriteLine("  (Server 2 reads same key, sees a live token → NX fails → returns false immediately)");

            server1Lock.Release();
            Console.WriteLine("\n  Server 1 released lock.");
            Redis.PrintState("Redis — after Server 1 releases");

            server2Got = server2Lock.TryAcquire();
            Console.WriteLine($"\n  Server 2 TryAcquire after release: {server2Got}  ← now succeeds");
            Redis.PrintState("Redis — Server 2 now holds the lock");
            server2Lock.Release();

            // =================================================================
            // Scenario 3 — TTL auto-expiry (holder crashes mid-work)
            // Server 1 acquires the lock but never releases (simulates a crash).
            // After TTL expires, Server 2 can acquire — no manual intervention.
            // This is why TTL is mandatory: without it a crash = permanent deadlock.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Holder crashes — TTL auto-releases the lock      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            // Short TTL (200ms) to avoid a long sleep in the demo
            var crashLock = new DistributedLock(Redis, "lock:job:nightly-report", ttlMs: 200);
            var recoveryLock = new DistributedLock(Redis, "lock:job:nightly-report", ttlMs: 3000);

            crashLock.Acquire();
            Console.WriteLine($"\n  Server 1 acquired (TTL=200ms). Simulating crash — never releases.");
            Redis.PrintState("Redis — Server 1 holds lock:job:nightly-report (200ms TTL)");

            bool immediateRetry = recoveryLock.TryAcquire();
            Console.WriteLine($"\n  Server 2 immediate retry: {immediateRetry}  ← lock still alive");

            Console.WriteLine("  Waiting 250ms for TTL to expire...");
            Thread.Sleep(250);

            Redis.PrintState("Redis — after TTL expiry (key auto-deleted by Redis)");
            bool afterExpiry = recoveryLock.TryAcquire();
            Console.WriteLine($"\n  Server 2 after TTL expiry: {afterExpiry}  ← lock acquired, no deadlock");
            Redis.PrintState("Redis — Server 2 holds the lock (Server 1 gone forever)");
            recoveryLock.Release();

            // =================================================================
            // Scenario 4 — Concurrent booking: 20 threads race for the last seat
            // Every thread tries to book seat B7. Only one should succeed.
            // The lock serialises access: exactly one thread enters the critical
            // section at a time. All others wait (Acquire retry loop) or fail.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: 20 concurrent threads — only 1 books last seat   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            _seatsAvailable = 1;
            _bookedBy = null;
            _doubleBookings = 0;
            _successfulBookings = 0;

            var threads = new Thread[20];
            for (int i = 0; i < 20; i++)
            {
                int userId = i + 1;
                threads[i] = new Thread(() => BookSeat($"user-{userId}"));
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            Console.WriteLine($"\n  Seat booked by:      {_bookedBy ?? "nobody"}");
            Console.WriteLine($"  Successful bookings: {_successfulBookings}  ← must be 1");
            Console.WriteLine($"  Double bookings:     {_doubleBookings}      ← must be 0");

            // =================================================================
            // Scenario 5 — Stale release: expired lock, wrong UUID, safe rejection
            // Server 1's lock expires. Server 2 acquires. Server 1 resumes and
            // tries to release — but the UUID check stops it from releasing
            // Server 2's lock. Without this check, Server 2's work would be
            // unprotected.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Stale release — UUID check prevents wrong delete  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var staleHolder = new DistributedLock(Redis, "lock:order:99", ttlMs: 200);
            var newHolder = new DistributedLock(Redis, "lock:order:99", ttlMs: 5000);

            staleHolder.Acquire();
            Console.WriteLine($"\n  Server 1 acquired lock (TTL=200ms). Simulating long GC pause...");
            Redis.PrintState("Redis — Server 1 token written (token-A)");

            Thread.Sleep(250); // lock expires during this pause

            newHolder.Acquire();
            Console.WriteLine($"\n  Server 2 acquired lock after Server 1's TTL expired.");
            Redis.PrintState("Redis — Server 2 wrote a NEW token (token-B), Server 1's token-A is gone");
            Console.WriteLine($"  Server 2 is now in critical section.");

            // Server 1 "wakes up" and tries to release — its old token-A no longer matches token-B
            bool staleRelease = staleHolder.Release();
            Console.WriteLine($"\n  Server 1 wakes up, attempts release: {staleRelease}  ← false (token-A ≠ token-B, safe rejection)");
            Console.WriteLine($"  Server 2 lock still held: {newHolder.IsHeld}  ← true, protected");
            Redis.PrintState("Redis — Server 2's lock intact after Server 1's stale release attempt");

            bool validRelease = newHolder.Release();
            Console.WriteLine($"\n  Server 2 releases normally: {validRelease}  ← true");
            Redis.PrintState("Redis — all locks released");
        }

        // Critical section: read seat count, book if available, check for double-booking
        private static void BookSeat(string userId)
        {
            // Each thread gets its own DistributedLock instance but they all
            // compete on the same key — only one wins the Redis SET NX at a time.
            using var lk = new DistributedLock(
                Redis,
                lockKey: "lock:booking:seat-B7",
                ttlMs: 3000,
                retryIntervalMs: 30,
                maxRetries: 50);

            if (!lk.Acquire())
            {
                Console.WriteLine($"  {userId}: could not acquire lock — giving up");
                return;
            }

            // ── Critical section ─────────────────────────────────────────────
            // Only ONE thread runs this block at a time.
            int snapshot = _seatsAvailable;
            Thread.Sleep(5); // simulate DB read latency

            if (snapshot > 0 && _bookedBy == null)
            {
                _seatsAvailable--;
                _bookedBy = userId;
                Interlocked.Increment(ref _successfulBookings);
                Console.WriteLine($"  {userId}: BOOKED seat B7 ✓");
            }
            else
            {
                // Seat already taken — no double booking because we hold the lock
                if (_bookedBy != null && _bookedBy != userId)
                    Interlocked.Increment(ref _doubleBookings); // should stay 0
                Console.WriteLine($"  {userId}: seat already taken");
            }
            // ── End critical section ─────────────────────────────────────────

            // using (IDisposable) calls Release() automatically here
        }
    }

} // namespace ResiliencePatterns
