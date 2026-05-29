// UrlTask — a single unit of work in the crawl frontier.
// Carries all metadata the frontier needs for priority ordering and politeness.

using System;

namespace AdvancedDesigns
{
    // Lower integer value = higher urgency.
    // High: seed URLs, freshness-critical pages (news, stock prices).
    // Medium: pages discovered during crawl (standard links).
    // Low: low-value pages (archives, paginated results beyond page 3).
    public enum CrawlPriority { High = 0, Medium = 1, Low = 2 }

    public class UrlTask
    {
        public string Url { get; set; }

        // Pre-extracted domain avoids repeated URI parsing in the frontier's
        // hot path — TryDequeue() checks domain throttling on every dequeue.
        public string Domain { get; set; }

        // Depth from the seed URL. Used to enforce maxDepth (spider-trap defence).
        // Spider traps generate infinite URL sequences (e.g. /calendar?date=next →
        // /calendar?date=next&next → …). A depth cap breaks the cycle.
        public int Depth { get; set; }

        public CrawlPriority Priority { get; set; }

        // Extracted once at task creation, not repeated at dequeue time.
        public static string ExtractDomain(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return "unknown"; }
        }
    }
}
