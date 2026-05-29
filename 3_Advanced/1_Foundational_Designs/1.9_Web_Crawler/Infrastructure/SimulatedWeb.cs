// SimulatedWeb — in-memory link graph that substitutes real HTTP fetches.
// Allows the crawler to run deterministically in tests without network calls.
// Maps URL → (HTML content, outbound links, HTTP status code).

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class SimulatedWeb
    {
        // OrdinalIgnoreCase so "https://Example.COM" and "https://example.com"
        // resolve to the same page — mirrors real DNS case-insensitivity.
        private readonly Dictionary<string, (string Html, string[] Links)> _pages = new(StringComparer.OrdinalIgnoreCase);

        public void AddPage(string url, string html, params string[] links) => _pages[url] = (html, links);

        // Returns 200 + page content for known URLs, 404 for unknown ones.
        // Real crawler would issue an HTTP GET here; we skip the network call.
        public (string Html, string[] Links, int StatusCode) Fetch(string url)
        {
            if (_pages.TryGetValue(url, out var page))
                return (page.Html, page.Links, 200);

            return ($"<html><body>404 Not Found: {url}</body></html>", Array.Empty<string>(), 404);
        }
    }
}
