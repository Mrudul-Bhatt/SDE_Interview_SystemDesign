// UrlShortenerService
// Orchestrates all components: ID generation, Base62 encoding, DB writes,
// LRU cache reads, TTL enforcement, analytics recording, and alias validation.
// This is the only class that callers (controllers, CLI) interact with directly.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class UrlShortenerService
    {
        // Reserved aliases that must never be handed to users — they collide with
        // routes on the same domain (sho.rt/api, sho.rt/admin, etc.).
        // OrdinalIgnoreCase: "Admin" and "admin" are both blocked.
        private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
            { "api", "admin", "stats", "health", "login", "shorten", "dashboard" };

        private readonly IdGenerator _idGen;
        private readonly UrlRepository _db;
        private readonly LruCache<string, UrlRecord> _cache;
        private readonly ClickAnalytics _analytics;
        private const string BaseUrl = "https://sho.rt/";

        public UrlShortenerService(int cacheCapacity = 1000)
        {
            _idGen = new IdGenerator();
            _db = new UrlRepository();
            _cache = new LruCache<string, UrlRecord>(cacheCapacity);
            _analytics = new ClickAnalytics();
        }

        // Exposes the cache for demo/test inspection (hit/miss stats, capacity pressure).
        public LruCache<string, UrlRecord> Cache => _cache;

        // ── Shorten ───────────────────────────────────────────────────────────────
        // Returns (shortUrl, error). error is null on success.
        public (string ShortUrl, string Error) Shorten(
            string longUrl,
            string customAlias = null,
            int? ttlDays = null,
            string createdBy = "anonymous")
        {
            if (string.IsNullOrWhiteSpace(longUrl) ||
                !longUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (null, "Invalid URL: must start with http/https");

            string shortCode;
            bool isCustom = false;

            if (customAlias != null)
            {
                if (!IsValidAlias(customAlias))
                    return (null, $"Invalid alias '{customAlias}': use only [a-zA-Z0-9_-], max 20 chars");

                // Block reserved slugs before checking the DB — avoids a DB roundtrip
                // for a request we know we'll reject.
                if (_reserved.Contains(customAlias))
                    return (null, $"Alias '{customAlias}' is reserved");

                if (_db.Find(customAlias) != null)
                    return (null, $"Alias '{customAlias}' is already taken");

                shortCode = customAlias;
                isCustom = true;
            }
            else
            {
                // Auto-generate: increment global counter, encode to Base62.
                long id = _idGen.NextId();
                shortCode = Base62.Encode(id);
            }

            var record = new UrlRecord
            {
                Id = isCustom ? 0 : Base62.Decode(shortCode),
                ShortCode = shortCode,
                LongUrl = longUrl,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = ttlDays.HasValue
                            ? DateTime.UtcNow.AddDays(ttlDays.Value)
                            : (DateTime?)null,
                IsCustom = isCustom
            };

            // TryInsert returns false on a race-condition duplicate (two requests
            // generating the same ID simultaneously — extremely rare but possible).
            if (!_db.TryInsert(record))
                return (null, "Collision: short code already exists");

            // Pre-warm the cache on creation so the very first redirect is a cache hit.
            _cache.Put(shortCode, record);

            return (BaseUrl + shortCode, null);
        }

        // ── Redirect ──────────────────────────────────────────────────────────────
        public RedirectResult Redirect(string shortCode, string country = "US")
        {
            // 1. Cache-first: most redirects should be served without a DB read.
            if (_cache.TryGet(shortCode, out UrlRecord cached))
            {
                if (!cached.IsActive || cached.IsExpired)
                    return new RedirectResult { StatusCode = 410, Note = "URL expired or deactivated" };

                // Record click asynchronously in production; synchronously here for simplicity.
                _analytics.Record(shortCode, country);
                return new RedirectResult { StatusCode = 302, LongUrl = cached.LongUrl, FromCache = true };
            }

            var record = _db.Find(shortCode);
            if (record == null)
                return new RedirectResult { StatusCode = 404, Note = "Short code not found" };

            if (!record.IsActive || record.IsExpired)
                return new RedirectResult { StatusCode = 410, Note = "URL expired or deactivated" };

            // Populate cache so subsequent redirects for this code are fast.
            _cache.Put(shortCode, record);
            _analytics.Record(shortCode, country);

            return new RedirectResult { StatusCode = 302, LongUrl = record.LongUrl, FromCache = false };
        }

        // ── Deactivate ────────────────────────────────────────────────────────────
        public bool Deactivate(string shortCode)
        {
            // Invalidate cache first so in-flight requests that already have a
            // cache reference don't continue to serve the deactivated URL.
            _cache.Remove(shortCode);
            return _db.Deactivate(shortCode);
        }

        // ── Stats ─────────────────────────────────────────────────────────────────
        public (long Total, IReadOnlyDictionary<string, long> ByCountry) GetStats(string shortCode)
            => (_analytics.TotalClicks(shortCode), _analytics.ByCountry(shortCode));

        // ── Validation ────────────────────────────────────────────────────────────
        private static bool IsValidAlias(string alias)
        {
            // Length 1–20, only alphanumeric, hyphen, underscore.
            // Disallow spaces and special chars to keep URLs clean and shell-safe.
            if (alias.Length < 1 || alias.Length > 20) return false;
            foreach (char c in alias)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            return true;
        }
    }
}
