// Question 1: LRU Cache (most commonly asked, directly related to CDN)
// "Implement an LRU Cache with get and put in O(1)."
// This is the core data structure inside every CDN edge node. Each edge node has limited SSD space.
// When it fills up it must evict something — it evicts the Least Recently Used item (LRU).

// Question 2: Cache with TTL Expiry
// "Extend your cache so entries expire after a TTL."
// CDN content has a max-age (e.g. 86400 seconds). After that the edge node re-fetches from origin.

// Question 3: URL Shortener with CDN caching
// "Design bit.ly. How does CDN play a role?"
// Short URL redirects are immutable and globally requested — a perfect CDN cache candidate.

using System;                       // DateTime, TimeSpan, Console
using System.Collections.Generic;   // Dictionary<T,T>, LinkedList<T>
using System.Threading;             // Thread (background TTL cleanup demo)

// ---------------------------------------------------------------------------
// Question 1 — LRU Cache
// ---------------------------------------------------------------------------
public class LRUCache
{
    // Maximum number of items the cache can hold before it starts evicting.
    // WHY readonly: capacity is fixed at construction time and must never change.
    private readonly int _capacity;

    // Hash map: key → the LinkedListNode that holds (key, value).
    // WHY store the node (not just the value): we need O(1) node removal from the linked list.
    // LinkedList.Remove(node) is O(1), but LinkedList.Remove(value) requires a scan — O(n).
    // Storing the node reference lets us jump straight to it and remove in O(1).
    private readonly Dictionary<string, LinkedListNode<(string key, string value)>> _map;

    // Doubly-linked list maintains access order: most-recently-used at the front, LRU at the back.
    // WHY doubly-linked (not singly): RemoveLast() and Remove(node) are O(1) only on a doubly-linked list.
    // WHY not a sorted list or heap: those give O(log n) for updates; we need O(1) here.
    private readonly LinkedList<(string key, string value)> _list;

    // Constructor: set capacity and initialize both data structures.
    public LRUCache(int capacity)
    {
        _capacity = capacity;

        // Pre-size the dictionary to avoid rehashing on early inserts.
        _map = new Dictionary<string, LinkedListNode<(string, string)>>();
        _list = new LinkedList<(string, string)>();
    }

    // Retrieves a value by key. Returns null on a cache miss.
    // O(1): dictionary lookup + two linked-list pointer updates (remove + add-front).
    public string Get(string key)
    {
        // Cache miss — key not present. Return null so caller knows to fetch from origin.
        if (!_map.ContainsKey(key)) return null;

        // Retrieve the node from the map in O(1).
        var node = _map[key];

        // Move this node to the front of the list, marking it as Most Recently Used.
        // WHY remove-then-add: LinkedList has no MoveToFront operation; this achieves it in O(1).
        _list.Remove(node);   // O(1) because we have the node reference directly
        _list.AddFirst(node); // O(1) — prepend to front

        // Return the cached value.
        return node.Value.value;
    }

    // Inserts or updates a key-value pair.
    // O(1): dictionary lookup/insert + linked-list add-front + optional tail removal.
    public void Put(string key, string value)
    {
        if (_map.ContainsKey(key))
        {
            // Key already exists — remove the old node from the list so we can re-insert it
            // at the front (marking it MRU). WHY not update in-place: LinkedListNode values
            // are readonly in C#; we must remove and re-add.
            var existing = _map[key];
            _list.Remove(existing); // O(1) via direct node reference
            _map.Remove(key);       // Remove from map before re-inserting to keep them in sync
        }

        if (_map.Count >= _capacity)
        {
            // Cache is full — evict the Least Recently Used item (the tail of the list).
            var lru = _list.Last;   // O(1) — LinkedList tracks the tail directly
            _list.RemoveLast();     // O(1) — unlinks the tail node
            _map.Remove(lru.Value.key); // Remove the evicted key from the map too
        }

        // Insert new item at the front — it is the Most Recently Used item.
        // AddFirst returns the new LinkedListNode so we can store it in the map.
        var node = _list.AddFirst((key, value)); // O(1)
        _map[key] = node;                         // O(1) amortized
    }

    // Prints the current cache state from MRU (front) to LRU (back) — useful for demos.
    public void PrintCache()
    {
        Console.Write("  Cache (MRU→LRU): ");
        foreach (var (k, v) in _list)
            Console.Write($"[{k}={v}] ");
        Console.WriteLine();
    }
}

// ---------------------------------------------------------------------------
// Question 2 — Cache with TTL Expiry
// ---------------------------------------------------------------------------
public class TTLCache
{
    // Stores each entry alongside its absolute expiry timestamp.
    // WHY (value, DateTime) tuple: we need both the value and the expiry together on each lookup.
    // Storing them as a tuple keeps the dictionary access to a single key lookup.
    private readonly Dictionary<string, (string value, DateTime expiry)> _store = new();

    // Inserts a key-value pair that expires after `ttl` from now.
    public void Put(string key, string value, TimeSpan ttl)
    {
        // DateTime.UtcNow.Add(ttl) calculates the absolute expiry time.
        // WHY UTC: avoids daylight-saving-time jumps — a 1-hour TTL should always be exactly 1 hour,
        // regardless of clock changes.
        _store[key] = (value, DateTime.UtcNow.Add(ttl));
    }

    // Returns the value if the key exists and has not expired. Returns null otherwise.
    public string Get(string key)
    {
        // TryGetValue avoids a double lookup (ContainsKey + indexer) — single dictionary probe.
        if (!_store.TryGetValue(key, out var entry))
            return null; // Key never existed — cache miss.

        // LAZY EXPIRY: we only check expiry on read, not on a background timer.
        // WHY lazy: simpler to implement; no background thread needed.
        // DOWNSIDE: expired entries sit in memory until someone reads them — memory can bloat
        // if keys are written frequently but never read again.
        if (DateTime.UtcNow > entry.expiry)
        {
            // Entry has expired — remove it now that we've discovered it.
            _store.Remove(key);
            return null; // Treat as a cache miss — caller must re-fetch from origin.
        }

        return entry.value;
    }

    // ACTIVE EXPIRY (the Redis approach): a background thread periodically scans the store
    // and removes all expired entries, preventing unbounded memory growth.
    // WHY needed alongside lazy expiry: keys that are never read again would leak forever
    // without active cleanup.
    public void StartBackgroundCleanup(TimeSpan interval)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(interval); // Wait for the next cleanup cycle.

                var now = DateTime.UtcNow;
                // Collect expired keys first — we can't remove from a Dictionary while iterating it.
                var expired = new List<string>();

                foreach (var (key, entry) in _store)
                    if (now > entry.expiry)
                        expired.Add(key);

                // Remove all expired keys in a second pass.
                foreach (var key in expired)
                    _store.Remove(key);

                if (expired.Count > 0)
                    Console.WriteLine($"  [TTL Cleanup] Evicted {expired.Count} expired key(s).");
            }
        });

        // IsBackground = true: this thread will not prevent the process from exiting when Main() ends.
        // WHY background: a foreground thread would keep the program alive forever.
        thread.IsBackground = true;
        thread.Start();
    }
}

// ---------------------------------------------------------------------------
// Question 3 — URL Shortener with CDN Caching
// ---------------------------------------------------------------------------

// Simple HTTP response model — represents what a real web framework returns.
// WHY a separate class (not just a string): HTTP responses carry both a status code and headers.
// The Cache-Control header is what tells the CDN how long to cache the redirect.
public class HttpResponse
{
    // HTTP status code: 301 = permanent redirect (CDN caches forever or until max-age),
    // 302 = temporary redirect (CDN typically does not cache).
    public int StatusCode { get; set; }

    // HTTP headers as key-value strings.
    // WHY Dictionary: headers are looked up by name (e.g. "Cache-Control", "Location").
    public Dictionary<string, string> Headers { get; set; } = new();

    // Prints the response in a human-readable format for the demo.
    public void Print()
    {
        Console.WriteLine($"  HTTP {StatusCode}");
        foreach (var (name, val) in Headers)
            Console.WriteLine($"  {name}: {val}");
    }
}

public class UrlShortenerService
{
    // In-memory "database" mapping short codes to original URLs.
    // In production this would be PostgreSQL, DynamoDB, etc.
    // WHY a dictionary here: O(1) lookup by short code — same complexity as any key-value store.
    private readonly Dictionary<string, string> _db = new();

    // LRU cache sits in front of the database — avoids a DB hit for popular short codes.
    // WHY LRU specifically: the most popular links (e.g. from viral tweets) get hammered;
    // LRU naturally keeps those in cache and evicts the long-tail rarely-clicked ones.
    private readonly LRUCache _cache;

    public UrlShortenerService(int cacheCapacity)
    {
        _cache = new LRUCache(cacheCapacity);
    }

    // Stores a new short code → original URL mapping in the backing database.
    public void Shorten(string shortCode, string originalUrl)
    {
        // Write goes to the DB only — not the cache.
        // WHY not cache on write: the entry will be cached on first read (read-through pattern).
        _db[shortCode] = originalUrl;
        Console.WriteLine($"  Shortened: /{shortCode} → {originalUrl}");
    }

    // Handles a redirect request for a given short code.
    // Returns an HttpResponse that CDN edge nodes will cache and serve from the edge.
    public HttpResponse Redirect(string shortCode)
    {
        // 1. Check the in-process LRU cache first (fastest — memory lookup, no network).
        var originalUrl = _cache.Get(shortCode);

        if (originalUrl == null)
        {
            // Cache miss — fall through to the database.
            if (!_db.TryGetValue(shortCode, out originalUrl))
            {
                // Short code not found anywhere — return 404.
                return new HttpResponse { StatusCode = 404 };
            }

            // Populate the cache for future requests (read-through caching).
            // WHY populate on miss (not upfront): we don't know which short codes will be popular.
            _cache.Put(shortCode, originalUrl);
            Console.WriteLine($"  [{shortCode}] Cache MISS — fetched from DB, now cached.");
        }
        else
        {
            Console.WriteLine($"  [{shortCode}] Cache HIT — served from memory.");
        }

        // Return a 301 (permanent redirect) with Cache-Control headers.
        return new HttpResponse
        {
            StatusCode = 301, // Permanent redirect — CDN and browsers cache this aggressively.

            Headers = new Dictionary<string, string>
            {
                // max-age=86400: CDN edge nodes cache this redirect for 1 day (86,400 seconds).
                // WHY 301 + Cache-Control: once a CDN edge caches this, every subsequent request
                // for /abc123 from that region is served by the edge — the origin server is never called.
                // This is how bit.ly handles billions of redirects without origin overload.
                ["Cache-Control"] = "public, max-age=86400",

                // Location tells the client (and CDN) where to redirect to.
                ["Location"] = originalUrl
            }
        };
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
        Console.WriteLine("║  Q1: LRU Cache                       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var cache = new LRUCache(capacity: 3);

        Console.WriteLine("\n=== Fill cache to capacity ===");
        cache.Put("img1.png", "data_A"); cache.PrintCache();
        cache.Put("img2.png", "data_B"); cache.PrintCache();
        cache.Put("img3.png", "data_C"); cache.PrintCache();

        Console.WriteLine("\n=== Access img1.png (moves to MRU front) ===");
        Console.WriteLine($"  Get img1.png → {cache.Get("img1.png")}");
        cache.PrintCache(); // img1 should now be at front; img2 is now LRU

        Console.WriteLine("\n=== Insert img4.png — cache full, evicts LRU (img2) ===");
        cache.Put("img4.png", "data_D"); cache.PrintCache();

        Console.WriteLine("\n=== img2.png was evicted — cache miss ===");
        Console.WriteLine($"  Get img2.png → {cache.Get("img2.png") ?? "null (cache miss)"}");

        // ===================================================================
        // Q2 DEMO — TTL Cache
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: TTL Cache                       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var ttlCache = new TTLCache();

        Console.WriteLine("\n=== Put entries with different TTLs ===");
        ttlCache.Put("home.html", "<html>Home</html>", TimeSpan.FromSeconds(2));
        ttlCache.Put("logo.png", "binary_data", TimeSpan.FromHours(24));
        ttlCache.Put("breaking.txt", "Breaking news!", TimeSpan.FromMilliseconds(300));

        Console.WriteLine("  Immediately after Put:");
        Console.WriteLine($"  home.html    → {ttlCache.Get("home.html") ?? "null"}");
        Console.WriteLine($"  logo.png     → {ttlCache.Get("logo.png") ?? "null"}");
        Console.WriteLine($"  breaking.txt → {ttlCache.Get("breaking.txt") ?? "null"}");

        // Start background cleanup (runs every 1 second)
        ttlCache.StartBackgroundCleanup(TimeSpan.FromSeconds(1));

        Console.WriteLine("\n=== Wait 500ms — breaking.txt expired, others still live ===");
        Thread.Sleep(500);
        Console.WriteLine($"  home.html    → {ttlCache.Get("home.html") ?? "null (expired)"}");
        Console.WriteLine($"  breaking.txt → {ttlCache.Get("breaking.txt") ?? "null (expired)"}");

        Console.WriteLine("\n=== Wait 2 more seconds — home.html expires, logo.png still live ===");
        Thread.Sleep(2000);
        Console.WriteLine($"  home.html → {ttlCache.Get("home.html") ?? "null (expired)"}");
        Console.WriteLine($"  logo.png  → {ttlCache.Get("logo.png") ?? "null (expired)"}");

        // ===================================================================
        // Q3 DEMO — URL Shortener with CDN caching
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: URL Shortener + CDN             ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var shortener = new UrlShortenerService(cacheCapacity: 2);

        Console.WriteLine("\n=== Register short URLs ===");
        shortener.Shorten("abc123", "https://example.com/very/long/path");
        shortener.Shorten("xyz789", "https://github.com/user/repo");

        Console.WriteLine("\n=== First redirect (cache miss — hits DB) ===");
        shortener.Redirect("abc123").Print();

        Console.WriteLine("\n=== Second redirect (cache hit — served from memory) ===");
        shortener.Redirect("abc123").Print();

        Console.WriteLine("\n=== Unknown short code ===");
        var notFound = shortener.Redirect("unknown");
        Console.WriteLine($"  HTTP {notFound.StatusCode}");
    }
}
