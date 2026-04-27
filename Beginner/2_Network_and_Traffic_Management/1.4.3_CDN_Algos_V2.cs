// Q1. Implement a Generic LRU Cache
// Design a cache with fixed capacity. On miss, fetch and store. When full, evict Least Recently Used.
// All operations must be O(1). Generic version — works with any key/value type.
// Complexity: Get O(1), Put O(1), Space O(capacity)

// Q2. Implement a Generic LFU Cache
// Evict the Least Frequently Used item when full.
// On a tie in frequency, evict the least recently used among those tied items.
// Complexity: Get O(1), Put O(1) — achieved with three dictionaries + per-freq linked lists.

// Q3. Implement a Generic TTL Cache
// Cache entries expire after a TTL. Expired entries must not be returned and should be cleaned up.
// Models how CDN edge nodes respect Cache-Control: max-age headers.

// Q4. Find the Nearest CDN Edge Server
// Given a user's GPS location (lat, lon) and a list of CDN edge nodes, return the closest one.
// Uses the Haversine formula — the standard great-circle distance algorithm.
// Complexity: GetNearestServer O(n), AddServer O(1)

using System;
using System.Collections.Generic;
using System.Threading; // Thread.Sleep for TTL demo

// ---------------------------------------------------------------------------
// Q1 — Generic LRU Cache
// ---------------------------------------------------------------------------

// TKey : notnull — the key type must not be nullable (required by Dictionary).
// WHY generic: the original LRUCache<string,string> is a special case; a generic version
// works for any (TKey, TValue) pair — e.g. LRUCache<int, byte[]> for image data.
public class LRUCache<TKey, TValue> where TKey : notnull
{
    // Maximum number of entries before eviction kicks in.
    private readonly int _capacity;

    // Hash map: key → the linked-list node holding (Key, Value).
    // WHY store the node directly (not just the value): LinkedList.Remove(node) is O(1)
    // because we hand it the node reference. LinkedList.Remove(value) would scan — O(n).
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();

    // Doubly-linked list maintains access order: MRU at front, LRU at back.
    // WHY doubly-linked: both AddFirst and RemoveLast are O(1) only on a doubly-linked list.
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();

    public LRUCache(int capacity)
    {
        _capacity = capacity;
    }

    // Returns the cached value for key, or default (null for reference types) on a miss.
    // O(1): one dictionary lookup + two pointer updates (remove node, add to front).
    public TValue? Get(TKey key)
    {
        // TryGetValue avoids a double lookup (ContainsKey + indexer) — single hash probe.
        if (!_map.TryGetValue(key, out var node))
            return default; // cache miss — caller must fetch from origin

        // Promote to MRU: remove from its current position, then prepend to the front.
        // WHY remove-then-add: LinkedList has no MoveToFront operation.
        _list.Remove(node);   // O(1) — we have the exact node reference
        _list.AddFirst(node); // O(1) — prepend to head

        return node.Value.Value; // node.Value is the (Key, Value) tuple; .Value is the TValue
    }

    // Inserts or updates a key-value pair, evicting the LRU entry if at capacity.
    // O(1) amortized: dictionary ops + linked-list head/tail operations are all O(1).
    public void Put(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            // Key already present — remove old node so we can reinsert at the front.
            // WHY not update in-place: LinkedListNode.Value is readonly in C#; must recreate.
            _list.Remove(existing);
            _map.Remove(key);
        }
        else if (_map.Count == _capacity)
        {
            // Cache is full and this is a new key — evict the LRU item (tail of the list).
            var lru = _list.Last!; // ! suppresses nullable warning; list is non-empty here
            _list.RemoveLast();    // O(1) — doubly-linked list tracks the tail
            _map.Remove(lru.Value.Key); // remove the evicted key from the map too
        }

        // Insert the new entry at the front — it is now the Most Recently Used item.
        // AddFirst returns the new node so we can store it in the map for O(1) future access.
        var node = _list.AddFirst((key, value));
        _map[key] = node;
    }

    // O(1) membership check without affecting LRU order (does not count as an access).
    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    // Prints the cache from MRU (front) to LRU (back) — for debugging and demos.
    public void PrintCache()
    {
        Console.Write("  Cache (MRU→LRU): ");
        foreach (var (k, v) in _list)
            Console.Write($"[{k}:{v}] ");
        Console.WriteLine();
    }
}

// ---------------------------------------------------------------------------
// Q2 — Generic LFU Cache
// ---------------------------------------------------------------------------

// LFU evicts the key accessed the FEWEST total times.
// Tie-breaking rule: among equal-frequency keys, evict the LEAST RECENTLY USED one.
// WHY three dictionaries: each one answers a different O(1) question:
//   _keyMap  → "what is key X's current value and frequency?"
//   _freqMap → "which keys have exactly frequency F, in insertion order?"
//   _nodeMap → "what is the linked-list node for key X in its frequency bucket?"
public class LFUCache<TKey, TValue> where TKey : notnull
{
    // Maximum number of entries before eviction.
    private readonly int _capacity;

    // Tracks the current minimum frequency across all cached keys.
    // WHY track it: EvictLFU needs to know which frequency bucket to evict from in O(1).
    // If we didn't track this, we'd have to scan all frequency buckets — O(n).
    private int _minFreq = 0;

    // Maps each key to its (Value, Frequency) pair.
    // WHY store frequency here: IncrementFreq needs to know the key's old frequency in O(1).
    private readonly Dictionary<TKey, (TValue Value, int Freq)> _keyMap = new();

    // Maps each frequency to an ordered list of keys at that frequency.
    // WHY LinkedList (not HashSet): we need insertion order so we can evict the LRU key
    // among all keys at the minimum frequency (ties broken by recency).
    // New keys are added to the BACK (AddLast); evictions take from the FRONT (RemoveFirst) → FIFO order = LRU.
    private readonly Dictionary<int, LinkedList<TKey>> _freqMap = new();

    // Maps each key to its node in its current frequency bucket's linked list.
    // WHY store the node: lets us remove the key from its old freq bucket in O(1).
    // Without this, we'd need to scan the old bucket to find the key — O(n).
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodeMap = new();

    public LFUCache(int capacity)
    {
        _capacity = capacity;
    }

    // Returns the value for key and increments its access frequency. O(1).
    public TValue? Get(TKey key)
    {
        // Miss — key not in cache.
        if (!_keyMap.TryGetValue(key, out var entry))
            return default;

        // Promote the key to the next frequency bucket.
        IncrementFreq(key, entry.Freq);
        return entry.Value;
    }

    // Inserts or updates a key-value pair. Evicts LFU entry if at capacity. O(1).
    public void Put(TKey key, TValue value)
    {
        // A zero-capacity cache is a no-op — nothing can ever be stored.
        if (_capacity <= 0) return;

        if (_keyMap.TryGetValue(key, out var existing))
        {
            // Key already exists — update the value in-place, then bump its frequency.
            // WHY update value first: IncrementFreq reads from _keyMap; the value must be
            // up-to-date before we overwrite the _keyMap entry inside IncrementFreq.
            _keyMap[key] = (value, existing.Freq);
            IncrementFreq(key, existing.Freq);
            return;
        }

        // New key — evict LFU if at capacity.
        if (_keyMap.Count == _capacity)
            EvictLFU();

        // Insert the new key with frequency 1 (first access).
        _keyMap[key] = (value, 1);

        // Ensure the freq=1 bucket exists.
        if (!_freqMap.ContainsKey(1))
            _freqMap[1] = new LinkedList<TKey>();

        // Add to the BACK of the freq=1 bucket (most recently added at back = LRU at front).
        var node = _freqMap[1].AddLast(key);
        _nodeMap[key] = node;

        // Any new insertion resets minFreq to 1 — the new key is always the least frequent.
        _minFreq = 1;
    }

    // Moves a key from its current frequency bucket to the next one. O(1).
    private void IncrementFreq(TKey key, int freq)
    {
        // Step 1: remove the key from its current frequency bucket.
        var node = _nodeMap[key];      // O(1) — direct node reference
        _freqMap[freq].Remove(node);   // O(1) — direct node removal

        if (_freqMap[freq].Count == 0)
        {
            // Bucket is now empty — remove it to keep the map clean.
            _freqMap.Remove(freq);

            // If this was the minimum-frequency bucket, the new minimum is freq+1.
            // WHY safe to just increment: the next non-empty bucket must be freq+1 because
            // we only ever promote a key up by exactly 1 frequency at a time.
            if (_minFreq == freq) _minFreq++;
        }

        // Step 2: add the key to the (freq+1) bucket.
        int newFreq = freq + 1;
        if (!_freqMap.ContainsKey(newFreq))
            _freqMap[newFreq] = new LinkedList<TKey>();

        // Add to the BACK — within each bucket, back = MRU, front = LRU.
        var newNode = _freqMap[newFreq].AddLast(key);
        _nodeMap[key] = newNode;

        // Update the key's recorded frequency.
        _keyMap[key] = (_keyMap[key].Value, newFreq);
    }

    // Evicts the LRU key in the lowest-frequency bucket. O(1).
    private void EvictLFU()
    {
        var bucket = _freqMap[_minFreq];

        // The FRONT of the bucket is the LRU key among those with _minFreq accesses.
        // WHY front = LRU: we always AddLast on insert/promote, so the oldest is at the front.
        var lruKey = bucket.First!.Value;
        bucket.RemoveFirst(); // O(1)

        // Clean up the empty bucket.
        if (bucket.Count == 0) _freqMap.Remove(_minFreq);

        // Remove from both supporting maps — key is fully evicted.
        _keyMap.Remove(lruKey);
        _nodeMap.Remove(lruKey);
    }
}

// ---------------------------------------------------------------------------
// Q3 — Generic TTL Cache
// ---------------------------------------------------------------------------
public class TTLCache<TKey, TValue> where TKey : notnull
{
    // Inner class groups a cached value with its expiry timestamp.
    // WHY a nested class (not a tuple): `IsExpired` is a computed property — it reads
    // DateTime.UtcNow at call time, so it always reflects the current moment.
    // A tuple would force callers to recompute expiry logic everywhere.
    private class CacheEntry
    {
        // The cached payload — init means it can only be set in the object initializer.
        public TValue Value { get; init; } = default!;

        // Absolute UTC timestamp when this entry becomes invalid.
        // WHY UTC: avoids DST clock jumps — a 1-hour TTL is always exactly 1 hour in UTC.
        public DateTime ExpiresAt { get; init; }

        // Computed on every call — checks the current time against the stored expiry.
        // WHY a property (not a field): expiry is time-dependent; it must re-evaluate each time.
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }

    // The backing store: key → CacheEntry.
    private readonly Dictionary<TKey, CacheEntry> _store = new();

    // Lock for thread safety — Put, Get, and Cleanup all modify _store.
    // WHY needed: multiple request threads hit the cache concurrently on a real CDN edge node.
    private readonly object _lock = new();

    // Stores a key-value pair that expires after `ttl` from the current moment.
    public void Put(TKey key, TValue value, TimeSpan ttl)
    {
        lock (_lock)
        {
            _store[key] = new CacheEntry
            {
                Value = value,
                // Calculate the absolute expiry time from now.
                // WHY UtcNow + ttl (not just ttl): we need an absolute timestamp to compare
                // against DateTime.UtcNow on each subsequent Get call.
                ExpiresAt = DateTime.UtcNow + ttl
            };
        }
    }

    // Returns the cached value if present and not expired, otherwise default (null for classes).
    public TValue? Get(TKey key)
    {
        lock (_lock)
        {
            // Single dictionary probe — TryGetValue avoids a second lookup.
            if (!_store.TryGetValue(key, out var entry))
                return default; // key never existed

            if (entry.IsExpired)
            {
                // LAZY EVICTION: remove the expired entry now that we've discovered it.
                // WHY lazy (not pre-scheduled): simpler and sufficient for low-churn caches.
                // Downside: entries that are never re-read after expiry stay in memory
                // until Cleanup() is called — solved by the periodic Cleanup() method below.
                _store.Remove(key);
                return default; // treat as a cache miss
            }

            return entry.Value;
        }
    }

    // O(1) check that accounts for expiry — returns false if the key has expired.
    // WHY call Get (not _map.ContainsKey): ContainsKey ignores TTL; Get enforces it.
    public bool ContainsKey(TKey key) => Get(key) is not null;

    // Proactively removes ALL expired entries.
    // Call this on a periodic background timer (e.g. every 30 seconds) to bound memory growth.
    // WHY two passes (collect then delete): you cannot remove from a Dictionary while iterating it.
    // Returns the number of entries that were evicted — useful for metrics/logging.
    public int Cleanup()
    {
        lock (_lock)
        {
            // First pass: collect the keys of expired entries.
            var expired = new List<TKey>();
            foreach (var (key, entry) in _store)
                if (entry.IsExpired) expired.Add(key);

            // Second pass: remove all expired entries.
            foreach (var key in expired)
                _store.Remove(key);

            return expired.Count;
        }
    }
}

// ---------------------------------------------------------------------------
// Q4 — CDN Router: Route User to Nearest Edge Server
// ---------------------------------------------------------------------------
public class CDNRouter
{
    // Value type representing a CDN edge server with a geographic position.
    // WHY record: positional equality + immutability — two EdgeServer records with the same
    // (Name, Lat, Lon) are equal by value, not by reference. Clean and self-documenting.
    public record EdgeServer(string Name, double Lat, double Lon);

    // List of all registered edge servers.
    // WHY List (not Dictionary): we iterate over all servers on every routing call to find
    // the minimum distance — random access by key is not needed here.
    private readonly List<EdgeServer> _servers = new();

    // Registers a new edge server at the given GPS coordinates. O(1) amortized.
    public void AddServer(string name, double lat, double lon)
    {
        // Build an immutable EdgeServer record and append to the list.
        _servers.Add(new EdgeServer(name, lat, lon));
    }

    // Returns the edge server geographically closest to the user. O(n) linear scan.
    // WHY O(n) is acceptable: n = number of edge servers (tens to hundreds), not user count.
    // This runs once per new user connection, not once per HTTP request.
    public EdgeServer? GetNearestServer(double userLat, double userLon)
    {
        // No servers registered — return null so the caller can handle gracefully.
        if (_servers.Count == 0) return null;

        EdgeServer? nearest = null;
        double minDistance = double.MaxValue; // start at infinity so the first server always wins

        foreach (var server in _servers)
        {
            // Real-world surface distance from user to this server's datacenter.
            double dist = HaversineDistanceKm(userLat, userLon, server.Lat, server.Lon);

            if (dist < minDistance)
            {
                minDistance = dist;
                nearest = server;
            }
        }

        return nearest;
    }

    // Haversine formula — great-circle distance between two GPS points on Earth's surface.
    // WHY Haversine (not Euclidean): lat/lon are angles on a sphere, not Cartesian coordinates.
    // Euclidean distance is meaningless across thousands of km; Haversine gives the true path length.
    private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Earth's mean radius in km

        // Convert degree differences to radians — required by Math.Sin/Cos.
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);

        // `a` = square of half the chord length between the two points (core Haversine term).
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        // `c` = central angle between the two points in radians.
        // Atan2 is numerically stable for both tiny and very large distances.
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        // Surface distance = radius * central angle.
        return R * c;
    }

    // Converts degrees to radians — Haversine requires radian inputs.
    private static double ToRad(double deg) => deg * Math.PI / 180;

    // Prints the distance from the user to every registered server.
    public void PrintDistances(double userLat, double userLon)
    {
        Console.WriteLine($"  User at ({userLat}, {userLon}):");
        foreach (var server in _servers)
        {
            double dist = HaversineDistanceKm(userLat, userLon, server.Lat, server.Lon);
            Console.WriteLine($"    {server.Name,-12}: {dist,6:F0} km");
        }
    }
}





// ---------------------------------------------------------------------------
// Entry point — demos for all 3 questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — LRU Cache
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Generic LRU Cache               ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var lru = new LRUCache<string, string>(capacity: 3);

        Console.WriteLine("\n=== Fill to capacity ===");
        lru.Put("homepage.html", "<html>home</html>"); lru.PrintCache();
        lru.Put("logo.png", "binary_logo"); lru.PrintCache();
        lru.Put("style.css", "body{margin:0}"); lru.PrintCache();

        Console.WriteLine("\n=== Access homepage.html → moves to MRU front ===");
        lru.Get("homepage.html");
        lru.PrintCache(); // homepage.html should be at front; logo.png is now LRU

        Console.WriteLine("\n=== Insert video.mp4 — full, evicts LRU (logo.png) ===");
        lru.Put("video.mp4", "binary_video");
        lru.PrintCache();

        Console.WriteLine($"\n=== logo.png evicted → {lru.Get("logo.png") ?? "null (cache miss)"} ===");

        // ===================================================================
        // Q2 DEMO — LFU Cache
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Generic LFU Cache               ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var lfu = new LFUCache<int, string>(capacity: 3);

        Console.WriteLine("\n=== Insert 3 keys (all freq=1) ===");
        lfu.Put(1, "homepage"); // freq: {1→[1]}
        lfu.Put(2, "logo");     // freq: {1→[1,2]}
        lfu.Put(3, "style");    // freq: {1→[1,2,3]}

        Console.WriteLine("=== Access keys to build up frequencies ===");
        lfu.Get(1); // freq: {1→[2,3],  2→[1]}
        lfu.Get(1); // freq: {1→[2,3],  3→[1]}
        lfu.Get(2); // freq: {1→[3],    2→[2],  3→[1]}

        Console.WriteLine("=== Insert key 4 — full, evicts LFU (key 3, freq=1) ===");
        lfu.Put(4, "video"); // evicts key 3 (only key remaining at freq=1)

        Console.WriteLine($"  Get(3) → {lfu.Get(3)?.ToString() ?? "null (evicted)"}"); // evicted
        Console.WriteLine($"  Get(1) → {lfu.Get(1)}");  // homepage
        Console.WriteLine($"  Get(4) → {lfu.Get(4)}");  // video

        // ===================================================================
        // Q3 DEMO — TTL Cache
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Generic TTL Cache               ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var ttl = new TTLCache<string, string>();

        Console.WriteLine("\n=== Put entries with different TTLs ===");
        ttl.Put("homepage.html", "<html>...</html>", TimeSpan.FromSeconds(2));
        ttl.Put("logo.png", "binary_data", TimeSpan.FromSeconds(10));

        Console.WriteLine("  Immediately after Put:");
        Console.WriteLine($"  homepage.html → {ttl.Get("homepage.html") ?? "null"}");
        Console.WriteLine($"  logo.png      → {ttl.Get("logo.png") ?? "null"}");

        Console.WriteLine("\n=== Wait 3 seconds — homepage.html (TTL=2s) expires ===");
        Thread.Sleep(3000);

        Console.WriteLine($"  homepage.html → {ttl.Get("homepage.html") ?? "null (expired)"}");
        Console.WriteLine($"  logo.png      → {ttl.Get("logo.png") ?? "null (expired)"}");

        int cleaned = ttl.Cleanup();
        Console.WriteLine($"\n  Proactive cleanup evicted {cleaned} expired entries.");

        // ===================================================================
        // Q4 DEMO -- CDN Router: nearest edge server
        // ===================================================================
        Console.WriteLine("\n\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        Console.WriteLine("\u2551  Q4: CDN Router (Nearest Edge Server) \u2551");
        Console.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");

        var router = new CDNRouter();
        router.AddServer("US-East", lat: 39.0, lon: -77.0); // Virginia
        router.AddServer("US-West", lat: 37.3, lon: -121.9); // California
        router.AddServer("EU-Central", lat: 50.1, lon: 8.7); // Frankfurt
        router.AddServer("AP-South", lat: 19.0, lon: 72.8); // Mumbai

        Console.WriteLine("\n=== User in London (51.5, -0.1) ===");
        router.PrintDistances(51.5, -0.1);
        var nearest = router.GetNearestServer(51.5, -0.1);
        Console.WriteLine($"  Routed to: {nearest?.Name}"); // EU-Central

        Console.WriteLine("\n=== User in Tokyo (35.7, 139.7) ===");
        router.PrintDistances(35.7, 139.7);
        var nearestTokyo = router.GetNearestServer(35.7, 139.7);
        Console.WriteLine($"  Routed to: {nearestTokyo?.Name}"); // AP-South
    }
}
