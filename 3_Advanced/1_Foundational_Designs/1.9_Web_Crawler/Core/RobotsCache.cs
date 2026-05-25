// RobotsCache — per-domain robots.txt rules and crawl-delay enforcement.
//
// In production: fetches /robots.txt on first visit to each domain, caches the
// parsed rules for the session. Here we pre-load rules to keep the demo in-memory.
//
// robots.txt disallow paths prevent crawlers from indexing private/admin areas.
// Crawl-delay is a courtesy to the server — exceeding it risks IP bans.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class RobotsCache
    {
        // Maps domain → list of disallowed path prefixes.
        // StringComparer.OrdinalIgnoreCase: "Example.COM" and "example.com" share rules.
        private readonly Dictionary<string, List<string>> _disallowed
            = new(StringComparer.OrdinalIgnoreCase);

        // Maps domain → minimum milliseconds between consecutive crawl requests.
        private readonly Dictionary<string, int> _crawlDelay
            = new(StringComparer.OrdinalIgnoreCase);

        public void LoadRules(string domain, int crawlDelayMs = 1000,
                              params string[] disallowedPaths)
        {
            _disallowed[domain] = new List<string>(disallowedPaths);
            _crawlDelay[domain] = crawlDelayMs;
        }

        // Returns false if the URL's path starts with any disallowed prefix for its domain.
        // Unknown domains default to allowed (no rules loaded = crawl everything).
        public bool IsAllowed(string normalizedUrl)
        {
            Uri uri;
            try { uri = new Uri(normalizedUrl); }
            catch { return false; }

            string domain = uri.Host.ToLowerInvariant();
            string path   = uri.AbsolutePath.ToLowerInvariant();

            if (_disallowed.TryGetValue(domain, out var rules))
                return !rules.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));

            return true;
        }

        // Default 1000ms if no rule loaded — conservative courtesy rate.
        public int GetCrawlDelayMs(string domain)
            => _crawlDelay.TryGetValue(domain, out int d) ? d : 1000;
    }
}
