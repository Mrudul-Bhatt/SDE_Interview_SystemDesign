using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

// ============================================================
//  URL Shortener — in-memory simulation
//  Covers: Base62 encoding, auto-increment ID generation,
//          LRU cache, custom aliases, TTL expiry, analytics,
//          alias conflict detection, cache hit/miss tracking
// ============================================================

namespace AdvancedDesigns
{
    // ── Base62 Encoder ─────────────────────────────────────────────────────────

    // Maps a numeric ID to a short alphanumeric string and back.
    // 62 chars (0-9, a-z, A-Z); 7 chars = 62^7 ≈ 3.5 trillion unique codes.
    public static class Base62
    {
        private const string Alphabet =
            "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const int Base   = 62;
        private const int Length = 7; // fixed-width codes

        public static string Encode(long id)
        {
            var chars = new char[Length];
            for (int i = Length - 1; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(id % Base)];
                id /= Base;
            }
            return new string(chars);
        }

        public static long Decode(string code)
        {
            long result = 0;
            foreach (char c in code)
                result = result * Base + Alphabet.IndexOf(c);
            return result;
        }
    }

    // ── ID Generator ──────────────────────────────────────────────────────────

    // Simulates Redis INCR: atomic counter, each call returns a unique ID.
    // Starting at 100_000 so Base62 codes are always 7 chars (≥ 62^4).
    public class IdGenerator
    {
        private long _counter = 100_000;
        public long NextId() => Interlocked.Increment(ref _counter);
    }

    // ── UrlRecord ─────────────────────────────────────────────────────────────

    public class UrlRecord
    {
        public long     Id         { get; set; }
        public string   ShortCode  { get; set; }
        public string   LongUrl    { get; set; }
        public string   CreatedBy  { get; set; }
        public DateTime CreatedAt  { get; set; }
        public DateTime? ExpiresAt { get; set; } // null = never expires
        public bool     IsActive   { get; set; } = true;
        public bool     IsCustom   { get; set; } // true for custom aliases

        public bool IsExpired =>
            ExpiresAt.HasValue && DateTime.UtcNow >= ExpiresAt.Value;
    }

    // ── LRU Cache ─────────────────────────────────────────────────────────────

    // Capacity-bounded cache: evicts least-recently-used entry when full.
    // Simulates Redis with allkeys-lru eviction policy.
    public class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _list;

        public int Hits   { get; private set; }
        public int Misses { get; private set; }

        public LruCache(int capacity)
        {
            _capacity = capacity;
            _map      = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity);
            _list     = new LinkedList<(TKey, TValue)>();
        }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // move to front (most recently used)
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                Hits++;
                return true;
            }
            value = default;
            Misses++;
            return false;
        }

        public void Put(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                // evict LRU (tail of list)
                var lru = _list.Last;
                _list.RemoveLast();
                _map.Remove(lru.Value.Key);
            }

            var node = _list.AddFirst((key, value));
            _map[key] = node;
        }

        public void Remove(TKey key)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _map.Remove(key);
            }
        }

        public int Count => _map.Count;

        public void ResetStats() { Hits = 0; Misses = 0; }
    }

    // ── ClickAnalytics ────────────────────────────────────────────────────────

    // Simulates Redis counters + ClickHouse aggregation.
    public class ClickAnalytics
    {
        private readonly Dictionary<string, long>                       _total  = new Dictionary<string, long>();
        private readonly Dictionary<string, Dictionary<string, long>>   _byCountry = new Dictionary<string, Dictionary<string, long>>();
        private readonly object _lock = new object();

        public void Record(string shortCode, string country = "US")
        {
            lock (_lock)
            {
                _total[shortCode] = _total.TryGetValue(shortCode, out long t) ? t + 1 : 1;

                if (!_byCountry.TryGetValue(shortCode, out var map))
                    _byCountry[shortCode] = map = new Dictionary<string, long>();
                map[country] = map.TryGetValue(country, out long c) ? c + 1 : 1;
            }
        }

        public long TotalClicks(string shortCode)
            => _total.TryGetValue(shortCode, out long v) ? v : 0;

        public IReadOnlyDictionary<string, long> ByCountry(string shortCode)
            => _byCountry.TryGetValue(shortCode, out var map)
               ? map
               : new Dictionary<string, long>();
    }

    // ── UrlRepository ─────────────────────────────────────────────────────────

    // Simulates PostgreSQL: primary store with unique constraint on short_code.
    public class UrlRepository
    {
        private readonly Dictionary<string, UrlRecord> _store
            = new Dictionary<string, UrlRecord>(StringComparer.Ordinal);

        public bool TryInsert(UrlRecord record)
        {
            if (_store.ContainsKey(record.ShortCode)) return false;
            _store[record.ShortCode] = record;
            return true;
        }

        public UrlRecord Find(string shortCode)
            => _store.TryGetValue(shortCode, out var r) ? r : null;

        public bool Deactivate(string shortCode)
        {
            if (_store.TryGetValue(shortCode, out var r))
            { r.IsActive = false; return true; }
            return false;
        }

        public int Count => _store.Count;
    }

    // ── Redirect result ───────────────────────────────────────────────────────

    public class RedirectResult
    {
        public int    StatusCode { get; set; }  // 302, 404, 410
        public string LongUrl    { get; set; }
        public string Note       { get; set; }
        public bool   FromCache  { get; set; }
    }

    // ── UrlShortenerService ───────────────────────────────────────────────────

    // Orchestrates ID generation, encoding, DB writes, cache, and analytics.
    public class UrlShortenerService
    {
        private static readonly HashSet<string> _reserved = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        { "api", "admin", "stats", "health", "login", "shorten", "dashboard" };

        private readonly IdGenerator    _idGen;
        private readonly UrlRepository  _db;
        private readonly LruCache<string, UrlRecord> _cache;
        private readonly ClickAnalytics _analytics;
        private const string BaseUrl = "https://sho.rt/";

        public UrlShortenerService(int cacheCapacity = 1000)
        {
            _idGen     = new IdGenerator();
            _db        = new UrlRepository();
            _cache     = new LruCache<string, UrlRecord>(cacheCapacity);
            _analytics = new ClickAnalytics();
        }

        // ── Shorten ───────────────────────────────────────────────────────────

        // Returns (shortUrl, error). error is null on success.
        public (string ShortUrl, string Error) Shorten(
            string longUrl,
            string customAlias = null,
            int?   ttlDays     = null,
            string createdBy   = "anonymous")
        {
            if (string.IsNullOrWhiteSpace(longUrl) ||
                !longUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (null, "Invalid URL: must start with http/https");

            string shortCode;
            bool   isCustom = false;

            if (customAlias != null)
            {
                // validate alias format
                if (!IsValidAlias(customAlias))
                    return (null, $"Invalid alias '{customAlias}': use only [a-zA-Z0-9_-], max 20 chars");

                if (_reserved.Contains(customAlias))
                    return (null, $"Alias '{customAlias}' is reserved");

                // check uniqueness against DB
                if (_db.Find(customAlias) != null)
                    return (null, $"Alias '{customAlias}' is already taken");

                shortCode = customAlias;
                isCustom  = true;
            }
            else
            {
                long id = _idGen.NextId();
                shortCode = Base62.Encode(id);
            }

            var record = new UrlRecord
            {
                Id        = isCustom ? 0 : Base62.Decode(shortCode),
                ShortCode = shortCode,
                LongUrl   = longUrl,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = ttlDays.HasValue
                            ? DateTime.UtcNow.AddDays(ttlDays.Value)
                            : (DateTime?)null,
                IsCustom  = isCustom
            };

            if (!_db.TryInsert(record))
                return (null, "Collision: short code already exists"); // race condition guard

            // pre-warm cache on creation
            _cache.Put(shortCode, record);

            return (BaseUrl + shortCode, null);
        }

        // ── Redirect ──────────────────────────────────────────────────────────

        public RedirectResult Redirect(string shortCode, string country = "US")
        {
            // 1. Check cache first
            if (_cache.TryGet(shortCode, out UrlRecord cached))
            {
                if (!cached.IsActive || cached.IsExpired)
                    return new RedirectResult { StatusCode = 410, Note = "URL expired or deactivated" };

                _analytics.Record(shortCode, country); // async in real system
                return new RedirectResult
                {
                    StatusCode = 302,
                    LongUrl    = cached.LongUrl,
                    FromCache  = true
                };
            }

            // 2. Cache miss — go to DB
            var record = _db.Find(shortCode);
            if (record == null)
                return new RedirectResult { StatusCode = 404, Note = "Short code not found" };

            if (!record.IsActive || record.IsExpired)
                return new RedirectResult { StatusCode = 410, Note = "URL expired or deactivated" };

            // populate cache
            _cache.Put(shortCode, record);
            _analytics.Record(shortCode, country);

            return new RedirectResult
            {
                StatusCode = 302,
                LongUrl    = record.LongUrl,
                FromCache  = false
            };
        }

        // ── Deactivate ────────────────────────────────────────────────────────

        public bool Deactivate(string shortCode)
        {
            _cache.Remove(shortCode);          // invalidate cache immediately
            return _db.Deactivate(shortCode);
        }

        // ── Stats ─────────────────────────────────────────────────────────────

        public (long Total, IReadOnlyDictionary<string, long> ByCountry) GetStats(string shortCode)
            => (_analytics.TotalClicks(shortCode), _analytics.ByCountry(shortCode));

        public LruCache<string, UrlRecord> Cache => _cache;

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsValidAlias(string alias)
        {
            if (alias.Length < 1 || alias.Length > 20) return false;
            foreach (char c in alias)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            return true;
        }
    }

    // ── Program ───────────────────────────────────────────────────────────────

    class UrlShortenerProgram
    {
        static void Banner(string title)
        {
            Console.WriteLine("\n╔" + new string('═', 62) + "╗");
            Console.WriteLine("║  " + title.PadRight(60) + "║");
            Console.WriteLine("╚" + new string('═', 62) + "╝");
        }

        static void PrintRedirect(string code, RedirectResult r)
        {
            string cache = r.FromCache ? " [CACHE HIT]" : " [CACHE MISS → DB]";
            if (r.StatusCode == 302)
                Console.WriteLine($"  GET /{code} → {r.StatusCode} {r.LongUrl}{cache}");
            else
                Console.WriteLine($"  GET /{code} → {r.StatusCode} ({r.Note})");
        }

        static void Main(string[] args)
        {
            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Base62 encoding — IDs to short codes");
            // ══════════════════════════════════════════════════════════════════
            // Show how auto-increment IDs map to 7-char codes
            long[] sampleIds = { 100_001, 100_062, 200_000, 1_000_000, 3_521_614_606_207L };
            Console.WriteLine($"\n  {"ID",-20} {"Base62 Code",-12} {"Decoded back"}");
            Console.WriteLine($"  {new string('─', 50)}");
            foreach (long id in sampleIds)
            {
                string code    = Base62.Encode(id);
                long   decoded = Base62.Decode(code);
                Console.WriteLine($"  {id,-20} {code,-12} {decoded}  {(decoded == id ? "✓" : "✗")}");
            }
            Console.WriteLine($"\n  62^7 = {Math.Pow(62, 7):N0} unique codes " +
                              $"≈ {Math.Pow(62, 7) / 100_000_000 / 365:N0} years at 100M/day");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 2: Basic shorten + redirect + cache behaviour");
            // ══════════════════════════════════════════════════════════════════
            var svc = new UrlShortenerService(cacheCapacity: 5);

            var (url1, _) = svc.Shorten("https://www.example.com/very/long/path?param=value");
            var (url2, _) = svc.Shorten("https://github.com/user/repo/blob/main/README.md");
            Console.WriteLine($"\n  Created: {url1}");
            Console.WriteLine($"  Created: {url2}");

            // extract codes
            string code1 = url1.Split('/').Last();
            string code2 = url2.Split('/').Last();

            Console.WriteLine("\n  First access (cache miss — just created, so pre-warmed):");
            PrintRedirect(code1, svc.Redirect(code1));
            PrintRedirect(code2, svc.Redirect(code2));

            Console.WriteLine("\n  Second access (cache hit):");
            PrintRedirect(code1, svc.Redirect(code1));
            PrintRedirect(code2, svc.Redirect(code2));

            Console.WriteLine($"\n  Cache state: {svc.Cache.Count} entries, " +
                              $"hits={svc.Cache.Hits}, misses={svc.Cache.Misses}");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 3: Custom alias + conflict detection");
            // ══════════════════════════════════════════════════════════════════
            var (aliasUrl, err1) = svc.Shorten(
                "https://mycompany.com/annual-report-2024.pdf",
                customAlias: "annual-report");
            Console.WriteLine($"\n  Custom alias created: {aliasUrl}");

            // second attempt with same alias → conflict
            var (_, err2) = svc.Shorten(
                "https://other.com/report.pdf",
                customAlias: "annual-report");
            Console.WriteLine($"  Duplicate alias attempt → Error: {err2}");

            // reserved alias
            var (_, err3) = svc.Shorten("https://example.com", customAlias: "admin");
            Console.WriteLine($"  Reserved alias attempt  → Error: {err3}");

            // invalid alias
            var (_, err4) = svc.Shorten("https://example.com", customAlias: "bad alias!");
            Console.WriteLine($"  Invalid alias attempt   → Error: {err4}");

            // confirm custom alias resolves
            Console.WriteLine("\n  Resolving custom alias:");
            PrintRedirect("annual-report", svc.Redirect("annual-report"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: URL expiry — TTL enforcement");
            // ══════════════════════════════════════════════════════════════════
            // Simulate an already-expired URL by setting a negative TTL offset
            var svc2 = new UrlShortenerService();
            var (shortUrl, _) = svc2.Shorten(
                "https://flash-sale.example.com/promo",
                customAlias: "flash-sale",
                ttlDays: 1);
            Console.WriteLine($"\n  Created with TTL: {shortUrl}");

            // normal access — not yet expired
            Console.WriteLine("  Access before expiry:");
            PrintRedirect("flash-sale", svc2.Redirect("flash-sale"));

            // manually expire by creating a record that's already past its TTL
            var expiredRecord = new UrlRecord
            {
                Id        = 999,
                ShortCode = "expired",
                LongUrl   = "https://old-promo.example.com",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddSeconds(-1), // already expired
                IsActive  = true
            };
            // reach into cache to demonstrate 410 handling
            svc2.Cache.Put("expired", expiredRecord);

            Console.WriteLine("\n  Access after expiry:");
            PrintRedirect("expired", svc2.Redirect("expired"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 5: Analytics — click tracking by country");
            // ══════════════════════════════════════════════════════════════════
            var svc3 = new UrlShortenerService();
            var (promoUrl, _) = svc3.Shorten(
                "https://store.example.com/summer-sale",
                customAlias: "summer-sale");
            Console.WriteLine($"\n  Promo URL: {promoUrl}");
            Console.WriteLine("  Simulating 12 clicks from different countries...");

            string[] countries = { "US","US","US","US","UK","UK","DE","DE","IN","IN","CA","AU" };
            foreach (string country in countries)
                svc3.Redirect("summer-sale", country);

            var (total, byCountry) = svc3.GetStats("summer-sale");
            Console.WriteLine($"\n  Total clicks: {total}");
            Console.WriteLine($"  By country:");
            foreach (var kv in byCountry.OrderByDescending(x => x.Value))
                Console.WriteLine($"    {kv.Key}: {kv.Value} clicks");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 6: Deactivation + 404 for unknown code");
            // ══════════════════════════════════════════════════════════════════
            var svc4 = new UrlShortenerService();
            var (link, _) = svc4.Shorten("https://content.example.com/secret-doc");
            string linkCode = link.Split('/').Last();

            Console.WriteLine($"\n  Created: {link}");
            Console.WriteLine("  Access before deactivation:");
            PrintRedirect(linkCode, svc4.Redirect(linkCode));

            svc4.Deactivate(linkCode);
            Console.WriteLine("  Access after deactivation:");
            PrintRedirect(linkCode, svc4.Redirect(linkCode));

            Console.WriteLine("\n  Access to unknown code:");
            PrintRedirect("notexist", svc4.Redirect("notexist"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 7: LRU cache eviction under capacity pressure");
            // ══════════════════════════════════════════════════════════════════
            // Cache capacity = 3; create 5 URLs → oldest 2 evicted
            var svc5 = new UrlShortenerService(cacheCapacity: 3);
            var codes = new List<string>();

            Console.WriteLine("\n  Creating 5 URLs (cache capacity = 3):");
            for (int i = 1; i <= 5; i++)
            {
                var (u, _) = svc5.Shorten($"https://example.com/page-{i}");
                string c = u.Split('/').Last();
                codes.Add(c);
                Console.WriteLine($"    {c} → cached (cache size: {svc5.Cache.Count})");
            }

            Console.WriteLine($"\n  Cache holds last 3 created: {codes[2]}, {codes[3]}, {codes[4]}");
            Console.WriteLine($"  Evicted during creation: {codes[0]}, {codes[1]}");

            // Access only the 3 codes still in cache → all hits
            Console.WriteLine("\n  Accessing the 3 hot codes (still in cache) → all HITs:");
            svc5.Cache.ResetStats();
            foreach (string c in codes.Skip(2))
            {
                var r = svc5.Redirect(c);
                Console.WriteLine($"    {c}: {(r.FromCache ? "HIT " : "MISS")} → {r.LongUrl}");
            }
            Console.WriteLine($"  Hit rate: {svc5.Cache.Hits * 100.0 / (svc5.Cache.Hits + svc5.Cache.Misses):F0}% (3/3)");

            // Now access the 2 evicted codes → cache misses, go to DB
            Console.WriteLine("\n  Accessing the 2 evicted codes → cache MISSes, DB fallback:");
            svc5.Cache.ResetStats();
            foreach (string c in codes.Take(2))
            {
                var r = svc5.Redirect(c);
                Console.WriteLine($"    {c}: {(r.FromCache ? "HIT " : "MISS")} → {r.LongUrl}");
            }
            Console.WriteLine($"  Hit rate: {svc5.Cache.Hits * 100.0 / (svc5.Cache.Hits + svc5.Cache.Misses):F0}% (0/2) — working set evicted by newer entries");
        }
    }
}
