// Q3. Prevent Cache Stampede with a Mutex Lock
// When a popular cache key expires, thousands of requests simultaneously miss the
// cache and hammer the database. Implement a mutex that lets only the first request
// fetch from DB while others wait and then read the freshly populated cache entry.
//
// The Problem (Cache Stampede / Thundering Herd):
//   t=0: Cache key "leaderboard" expires (TTL hit)
//   t=0: 10,000 concurrent requests all get a cache miss
//   t=0: All 10,000 hit the database simultaneously
//        → DB overwhelmed → latency spikes → cascading failure
//
// Three prevention strategies:
//
// 1. Mutex lock (this implementation):
//    → Only 1 thread fetches; others wait, then read from cache
//    → Pro: simple, exact — exactly 1 DB call per key expiry
//    → Con: waiting threads add latency equal to fetch time
//
// 2. Probabilistic early expiration (PER):
//    → Before key actually expires, some requests start refreshing early
//    → Formula: refresh if (ttl - elapsed) < -log(random()) × beta
//    → Pro: no locking, zero wait time
//    → Con: slightly more DB calls (staggered instead of one stampede)
//
// 3. Background refresh (stale-while-revalidate):
//    → Serve stale data while a background job refreshes the cache
//    → Same idea as the "stale-while-revalidate" HTTP Cache-Control directive
//    → Pro: zero latency impact, no lock needed
//    → Con: clients briefly see stale data
//
// Complexity: O(1) on cache hit, O(fetch time) on miss — only 1 fetcher per key

using System.Collections.Concurrent;

namespace PerformanceConcepts
{

    // ---------------------------------------------------------------------------
    // StampedeProtectedCache<TKey, TValue>
    // ---------------------------------------------------------------------------
    public class StampedeProtectedCache<TKey, TValue> where TKey : notnull
    {
        private class CacheEntry
        {
            public TValue Value { get; init; } = default!;
            public DateTime ExpiresAt { get; init; }

            // UtcNow avoids DST edge cases; expiry is a point in absolute time.
            public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        }

        // ConcurrentDictionary for the cache so cache hits (fast path) need no lock at all.
        // Regular Dictionary + lock would serialize even HIT reads, wasting throughput.
        private readonly ConcurrentDictionary<TKey, CacheEntry> _cache = new();

        // One SemaphoreSlim per key so threads waiting for "leaderboard" don't block
        // threads waiting for "profile:42". A single global lock would serialize all misses.
        // SemaphoreSlim(1,1) = binary semaphore = only 1 thread inside at a time.
        // We use SemaphoreSlim over lock because SemaphoreSlim supports async await,
        // while lock requires synchronous blocking — bad in async/await code.
        private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

        public async Task<TValue> GetOrFetchAsync(TKey key, Func<Task<TValue>> fetch, TimeSpan ttl)
        {
            // ── Fast path: cache hit ──────────────────────────────────────────
            // Check WITHOUT acquiring any lock — ConcurrentDictionary.TryGetValue is
            // thread-safe for reads. The vast majority of requests land here.
            if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                Console.WriteLine($"  [CACHE] HIT  key={key}");
                return entry.Value;
            }

            // ── Slow path: cache miss — acquire per-key semaphore ─────────────
            // GetOrAdd is atomic: if two threads miss at the same instant, only one
            // SemaphoreSlim is created and both threads share it.
            var keyLock = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            // WaitAsync yields the thread rather than blocking it — essential in
            // async code where blocking would deadlock the thread pool under load.
            await keyLock.WaitAsync();
            try
            {
                // ── Double-check after acquiring the lock ─────────────────────
                // While we were waiting, the thread that held the lock may have
                // already fetched and stored the value. Without this check, every
                // thread that was queued behind the fetcher would each do their own
                // DB call — defeating the entire point of the lock.
                if (_cache.TryGetValue(key, out entry) && !entry.IsExpired)
                {
                    Console.WriteLine($"  [CACHE] HIT  key={key} (populated while waiting for lock)");
                    return entry.Value;
                }

                // We are the designated fetcher for this key — exactly one thread
                // reaches this line per cache miss (all others short-circuit above).
                Console.WriteLine($"  [DB]    FETCH key={key} (cache miss — fetching from source)");
                TValue value = await fetch();

                // Store before releasing the lock so that when we release, the double-check
                // above in waiting threads immediately finds a valid entry.
                _cache[key] = new CacheEntry { Value = value, ExpiresAt = DateTime.UtcNow + ttl };
                return value;
            }
            finally
            {
                // Always release — even if fetch() threw an exception — so waiting
                // threads aren't permanently blocked. In a production system you'd
                // also want to NOT cache on exception (no stale errors in cache).
                keyLock.Release();
            }
        }

        // Force-remove a key (e.g. after a write invalidation) so the next read
        // fetches fresh data. Does not remove the per-key semaphore — that's harmless.
        public void Invalidate(TKey key) => _cache.TryRemove(key, out _);
    }

    // ---------------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------------
    public class Program
    {
        public static async Task Main()
        {
            var cache = new StampedeProtectedCache<string, string>();

            // Simulates a slow DB query — 100ms latency, counts how many times it runs.
            int dbCallCount = 0;
            async Task<string> FetchLeaderboardFromDB()
            {
                dbCallCount++;
                await Task.Delay(100); // simulate DB query time
                return "Alice:100, Bob:80, Charlie:60";
            }

            // ===================================================================
            // Scenario 1 — 10 concurrent requests, cold cache
            // Only 1 should reach the DB; the other 9 should wait and read cache.
            // ===================================================================
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Cold cache — 10 concurrent     ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            var tasks = Enumerable.Range(1, 10).Select(async i =>
            {
                string result = await cache.GetOrFetchAsync(
                    "leaderboard",
                    FetchLeaderboardFromDB,
                    ttl: TimeSpan.FromSeconds(30)
                );
                Console.WriteLine($"  Request {i:00}: {result}");
            });
            await Task.WhenAll(tasks);

            Console.WriteLine($"\n  DB calls made: {dbCallCount}  ← should be 1, not 10!");

            // ===================================================================
            // Scenario 2 — 10 more requests, warm cache
            // All should hit cache; DB call count stays at 1.
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Warm cache — 10 concurrent     ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            int prevDbCalls = dbCallCount;
            await Task.WhenAll(Enumerable.Range(1, 10).Select(async i =>
            {
                string result = await cache.GetOrFetchAsync(
                    "leaderboard",
                    FetchLeaderboardFromDB,
                    TimeSpan.FromSeconds(30)
                );
                Console.WriteLine($"  Request {i:00}: {result}");
            }));
            Console.WriteLine($"\n  DB calls this round: {dbCallCount - prevDbCalls}  ← 0, all from cache");

            // ===================================================================
            // Scenario 3 — Invalidate then re-fetch
            // After a write, invalidate the key; next request fetches fresh data.
            // ===================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Invalidate then re-fetch       ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            cache.Invalidate("leaderboard");
            Console.WriteLine("  Cache invalidated (e.g. after a score update)");

            int beforeInvalidate = dbCallCount;
            await cache.GetOrFetchAsync("leaderboard", FetchLeaderboardFromDB, TimeSpan.FromSeconds(30));
            Console.WriteLine($"  DB calls after invalidation: {dbCallCount - beforeInvalidate}  ← 1, fetched fresh");
        }
    }

} // namespace PerformanceConcepts
