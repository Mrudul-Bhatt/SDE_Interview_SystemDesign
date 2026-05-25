// Crawler — main orchestrator. Ties together all components.
//
// Per-URL pipeline:
//   1. Normalize URL        (UrlNormalizer)
//   2. Bloom filter check   (BloomFilter)     → skip if seen
//   3. Robots.txt check     (RobotsCache)     → skip if disallowed
//   4. Depth check                            → skip if too deep
//   5. Dequeue from frontier (UrlFrontier)    → respects per-domain delay
//   6. Fetch page            (SimulatedWeb)
//   7. Store result          (ContentStore)
//   8. Extract + enqueue links → back to step 1 for each link

using System;
using System.Threading;

namespace AdvancedDesigns
{
    public class Crawler
    {
        private readonly UrlFrontier  _frontier;
        private readonly BloomFilter  _bloom;
        private readonly RobotsCache  _robots;
        private readonly SimulatedWeb _web;
        private readonly ContentStore _store;
        private readonly int          _maxDepth;
        private readonly int          _maxPages;
        private readonly CrawlStats   _stats = new();

        public CrawlStats   Stats => _stats;
        public ContentStore Store => _store;

        public Crawler(SimulatedWeb web, RobotsCache robots,
                       int maxDepth = 3, int maxPages = 100, int bloomSize = 5000)
        {
            _web      = web;
            _robots   = robots;
            _store    = new ContentStore();
            _bloom    = new BloomFilter(bloomSize, hashCount: 3);
            _frontier = new UrlFrontier(robots);
            _maxDepth = maxDepth;
            _maxPages = maxPages;
        }

        public void AddSeed(string url, CrawlPriority priority = CrawlPriority.High)
        {
            string norm = UrlNormalizer.Normalize(url);
            if (norm == null || _bloom.MightContain(norm)) return;

            _bloom.Add(norm);
            _frontier.Enqueue(new UrlTask
            {
                Url      = norm,
                Domain   = UrlTask.ExtractDomain(norm),
                Depth    = 0,
                Priority = priority
            });
            _stats.UrlsDiscovered++;
        }

        // Runs until maxPages are crawled or frontier is exhausted.
        // log callback receives a line per crawled URL — pass Console.WriteLine for demos.
        public void Run(Action<string> log = null)
        {
            // spinCount guards against an infinite loop when all domains are throttled
            // but the frontier is not empty (all tasks waiting for their crawl-delay).
            int spinCount = 0;

            while (_store.Count < _maxPages)
            {
                var task = _frontier.TryDequeue();

                if (task == null)
                {
                    // No domain is ready yet — brief sleep lets crawl-delays elapse.
                    // After 50 consecutive empty polls, assume frontier is truly empty.
                    if (++spinCount > 50) break;
                    Thread.Sleep(20);
                    continue;
                }
                spinCount = 0;

                // ── Fetch ──────────────────────────────────────────────────────
                var (html, rawLinks, status) = _web.Fetch(task.Url);

                _store.Store(new CrawledPage
                {
                    Url         = task.Url,
                    Html        = html,
                    StatusCode  = status,
                    CrawledAt   = DateTime.UtcNow,
                    Depth       = task.Depth,
                    ContentHash = ComputeHash(html)
                });
                _stats.PagesCrawled++;
                log?.Invoke($"  CRAWLED [{status}] depth={task.Depth} {task.Url}");

                // Don't extract links from error pages — no useful links there.
                if (status != 200) continue;

                // ── Process discovered links ───────────────────────────────────
                foreach (string rawLink in rawLinks)
                {
                    _stats.UrlsDiscovered++;

                    string norm = UrlNormalizer.Normalize(rawLink, task.Url);
                    if (norm == null)
                    { _stats.NormalizationFailed++; continue; }

                    // Depth limit: key spider-trap defence. A spider trap generates
                    // infinite URL sequences (pagination, session tokens, calendars).
                    // maxDepth caps how far from the seed we'll follow links.
                    if (task.Depth + 1 > _maxDepth)
                    { _stats.DepthBlocked++; continue; }

                    if (!_robots.IsAllowed(norm))
                    { _stats.RobotsBlocked++; log?.Invoke($"  ROBOTS  blocked: {norm}"); continue; }

                    // Bloom filter: O(1) check before touching the frontier.
                    // MightContain returning true = skip (seen or false positive).
                    if (_bloom.MightContain(norm))
                    { _stats.DuplicatesBlocked++; continue; }

                    _bloom.Add(norm);
                    _frontier.Enqueue(new UrlTask
                    {
                        Url      = norm,
                        Domain   = UrlTask.ExtractDomain(norm),
                        Depth    = task.Depth + 1,
                        Priority = CrawlPriority.Medium
                    });
                }
            }
        }

        // Simple polynomial content hash for near-duplicate detection.
        // Real systems use SimHash (locality-sensitive) so near-identical pages
        // (same article, different sidebar ads) also hash close together.
        private static string ComputeHash(string content)
        {
            int h = 0;
            foreach (char c in content) h = h * 31 + c;
            return Math.Abs(h).ToString("X8");
        }
    }
}
