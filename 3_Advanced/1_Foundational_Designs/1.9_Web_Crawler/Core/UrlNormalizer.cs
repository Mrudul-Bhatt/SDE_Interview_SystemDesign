// UrlNormalizer — converts raw URLs to a canonical form before deduplication.
//
// Why normalization matters:
//   These all point to the same page but look different:
//     https://Example.COM/Products/     ← uppercase host, trailing slash
//     https://example.com/products      ← canonical
//     https://example.com/products?utm_source=email  ← tracking param
//     https://example.com/products?a=1&b=2  vs  ?b=2&a=1  ← param order
//   Without normalization the Bloom filter treats them as 4 different URLs → re-crawl.
//
// Steps applied:
//   1. Resolve relative URLs against the page's base URL
//   2. Lowercase scheme and host
//   3. Remove trailing slash from path
//   4. Strip fragment (#section) — same page content
//   5. Remove tracking query parameters (utm_*, ref, fbclid, …)
//   6. Sort remaining query parameters alphabetically

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public static class UrlNormalizer
    {
        // Known tracking parameters that carry no content identity.
        // Stripping them before hashing prevents "same page, different campaign" duplicates.
        private static readonly HashSet<string> _trackingParams = new(StringComparer.OrdinalIgnoreCase)
        {
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
            "ref", "fbclid", "gclid", "sessionid", "session_id", "sid"
        };

        public static string Normalize(string rawUrl, string baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return null;

            // Resolve relative paths (/about → https://example.com/about) using
            // the Uri constructor that merges base + relative.
            if (!rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (baseUrl == null) return null;
                try { rawUrl = new Uri(new Uri(baseUrl), rawUrl).ToString(); }
                catch { return null; }
            }

            Uri uri;
            try { uri = new Uri(rawUrl); }
            catch { return null; }

            // Reject non-web schemes (ftp://, mailto:, javascript:, etc.)
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return null;

            string host = uri.Host.ToLowerInvariant();
            // TrimEnd('/') makes /products/ and /products the same canonical path.
            string path = uri.AbsolutePath.ToLowerInvariant().TrimEnd('/');

            // Filter tracking params, then sort the rest alphabetically so
            // ?a=1&b=2 and ?b=2&a=1 produce the same canonical string.
            var cleanParams = ParseQueryString(uri.Query)
                .Where(kv => !_trackingParams.Contains(kv.Key))
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}")
                .ToList();

            string query = cleanParams.Count > 0 ? "?" + string.Join("&", cleanParams) : "";

            // Fragment (#anchor) is omitted — it identifies a position within the page,
            // not a different page, so it must not affect deduplication.
            return $"{scheme}://{host}{path}{query}";
        }

        private static IEnumerable<(string Key, string Value)> ParseQueryString(string query)
        {
            if (string.IsNullOrEmpty(query)) yield break;
            string q = query.TrimStart('?');
            foreach (string pair in q.Split('&'))
            {
                int eq = pair.IndexOf('=');
                // eq < 0 handles boolean flags with no value (e.g. "?dark" → key="dark", value="").
                if (eq < 0) yield return (pair, "");
                else yield return (pair.Substring(0, eq), pair.Substring(eq + 1));
            }
        }
    }
}
