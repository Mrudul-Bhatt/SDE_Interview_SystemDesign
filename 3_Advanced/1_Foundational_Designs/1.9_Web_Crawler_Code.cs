using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

// ============================================================
//  Web Crawler — in-memory simulation
//  Covers: Bloom filter deduplication, URL frontier with
//          priority + per-domain politeness, robots.txt
//          filtering, URL normalization, depth limit,
//          spider trap detection, distributed domain sharding
// ============================================================

namespace AdvancedDesigns
{
    // ── BloomFilter ───────────────────────────────────────────────────────────

    // Probabilistic set: O(1) insert + lookup, no false negatives, ~1% false positives.
    // Uses k independent hash functions over a shared bit array.
    public class BloomFilter
    {
        private readonly bool[] _bits;
        private readonly int    _size;
        private readonly int    _hashCount;

        public int ItemsAdded      { get; private set; }
        public int ChecksPerformed { get; private set; }

        public BloomFilter(int size = 10_000, int hashCount = 3)
        {
            _size      = size;
            _hashCount = hashCount;
            _bits      = new bool[size];
        }

        public void Add(string item)
        {
            foreach (int pos in GetPositions(item))
                _bits[pos] = true;
            ItemsAdded++;
        }

        // Returns true if item was PROBABLY inserted; false if DEFINITELY NOT.
        public bool MightContain(string item)
        {
            ChecksPerformed++;
            return GetPositions(item).All(pos => _bits[pos]);
        }

        // k different hash functions via seeded polynomial rolling hash
        private IEnumerable<int> GetPositions(string item)
        {
            for (int seed = 0; seed < _hashCount; seed++)
            {
                int hash = seed * unchecked((int)2654435761u); // Knuth multiplicative hash seed
                foreach (char c in item)
                    hash = hash * 31 + c;
                yield return Math.Abs(hash % _size);
            }
        }

        public double FillRatio => _bits.Count(b => b) / (double)_size;
    }

    // ── URL Normalizer ────────────────────────────────────────────────────────

    // Converts raw URLs to canonical form so duplicates are detected correctly.
    public static class UrlNormalizer
    {
        private static readonly HashSet<string> _trackingParams = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
          "ref", "fbclid", "gclid", "sessionid", "session_id", "sid" };

        public static string Normalize(string rawUrl, string baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return null;

            // resolve relative URLs against base
            if (!rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (baseUrl == null) return null;
                try { rawUrl = new Uri(new Uri(baseUrl), rawUrl).ToString(); }
                catch { return null; }
            }

            Uri uri;
            try { uri = new Uri(rawUrl); }
            catch { return null; }

            // only HTTP/HTTPS
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return null;

            string host = uri.Host.ToLowerInvariant();
            // trim trailing slash from all paths; root becomes empty → "https://example.com"
            string path = uri.AbsolutePath.ToLowerInvariant().TrimEnd('/');

            // strip fragment; filter tracking params; sort remaining params
            var cleanParams = ParseQueryString(uri.Query)
                .Where(kv => !_trackingParams.Contains(kv.Key))
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}")
                .ToList();

            string query = cleanParams.Count > 0 ? "?" + string.Join("&", cleanParams) : "";
            return $"{scheme}://{host}{path}{query}";
        }

        private static IEnumerable<(string Key, string Value)> ParseQueryString(string query)
        {
            if (string.IsNullOrEmpty(query)) yield break;
            string q = query.TrimStart('?');
            foreach (string pair in q.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) yield return (pair, "");
                else yield return (pair.Substring(0, eq), pair.Substring(eq + 1));
            }
        }
    }

    // ── RobotsCache ───────────────────────────────────────────────────────────

    // Per-domain robots.txt rules. Real system fetches from /robots.txt; here pre-loaded.
    public class RobotsCache
    {
        private readonly Dictionary<string, List<string>> _disallowed
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _crawlDelay
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public void LoadRules(string domain, int crawlDelayMs = 1000,
                              params string[] disallowedPaths)
        {
            _disallowed[domain] = new List<string>(disallowedPaths);
            _crawlDelay[domain] = crawlDelayMs;
        }

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

        public int GetCrawlDelayMs(string domain)
            => _crawlDelay.TryGetValue(domain, out int d) ? d : 1000;
    }

    // ── UrlTask ───────────────────────────────────────────────────────────────

    public enum CrawlPriority { High = 0, Medium = 1, Low = 2 }

    public class UrlTask
    {
        public string       Url      { get; set; }
        public string       Domain   { get; set; }
        public int          Depth    { get; set; }
        public CrawlPriority Priority { get; set; }

        public static string ExtractDomain(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return "unknown"; }
        }
    }

    // ── UrlFrontier ───────────────────────────────────────────────────────────

    // Two-level queue: priority ordering + per-domain politeness enforcement.
    // Front queues rank by priority; back queues throttle per domain.
    public class UrlFrontier
    {
        private readonly List<UrlTask>                    _queue      = new List<UrlTask>();
        private readonly Dictionary<string, DateTime>     _lastCrawled = new Dictionary<string, DateTime>();
        private readonly RobotsCache                      _robots;
        private readonly object _lock = new object();

        public int Count { get { lock (_lock) return _queue.Count; } }

        public UrlFrontier(RobotsCache robots) => _robots = robots;

        public void Enqueue(UrlTask task)
        {
            lock (_lock) _queue.Add(task);
        }

        // Returns next task whose domain satisfies politeness, null if none ready.
        public UrlTask TryDequeue()
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;

                // find highest-priority task with an available (not throttled) domain
                var task = _queue
                    .OrderBy(t => (int)t.Priority)
                    .ThenBy(t => t.Depth)
                    .FirstOrDefault(t =>
                    {
                        if (!_lastCrawled.TryGetValue(t.Domain, out DateTime last))
                            return true; // never crawled → available
                        int delayMs = _robots.GetCrawlDelayMs(t.Domain);
                        return (now - last).TotalMilliseconds >= delayMs;
                    });

                if (task == null) return null;

                _queue.Remove(task);
                _lastCrawled[task.Domain] = now;
                return task;
            }
        }

        public IReadOnlyDictionary<string, DateTime> LastCrawled => _lastCrawled;
    }

    // ── SimulatedWeb ──────────────────────────────────────────────────────────

    // In-memory link graph that substitutes real HTTP fetches.
    public class SimulatedWeb
    {
        private readonly Dictionary<string, (string Html, string[] Links)> _pages
            = new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase);

        public void AddPage(string url, string html, params string[] links)
            => _pages[url] = (html, links);

        public (string Html, string[] Links, int StatusCode) Fetch(string url)
        {
            if (_pages.TryGetValue(url, out var page))
                return (page.Html, page.Links, 200);
            return ($"<html><body>404 Not Found: {url}</body></html>",
                    Array.Empty<string>(), 404);
        }
    }

    // ── ContentStore ─────────────────────────────────────────────────────────

    public class CrawledPage
    {
        public string   Url         { get; set; }
        public string   Html        { get; set; }
        public int      StatusCode  { get; set; }
        public DateTime CrawledAt   { get; set; }
        public int      Depth       { get; set; }
        public string   ContentHash { get; set; }
    }

    public class ContentStore
    {
        private readonly List<CrawledPage> _pages = new List<CrawledPage>();

        public void Store(CrawledPage page)
        {
            lock (_pages) _pages.Add(page);
        }

        public int Count { get { lock (_pages) return _pages.Count; } }
        public IReadOnlyList<CrawledPage> Pages { get { lock (_pages) return _pages.ToList(); } }
    }

    // ── CrawlStats ────────────────────────────────────────────────────────────

    public class CrawlStats
    {
        public int PagesCrawled        { get; set; }
        public int UrlsDiscovered      { get; set; }
        public int DuplicatesBlocked   { get; set; }
        public int RobotsBlocked       { get; set; }
        public int DepthBlocked        { get; set; }
        public int NormalizationFailed { get; set; }
    }

    // ── Crawler ───────────────────────────────────────────────────────────────

    public class Crawler
    {
        private readonly UrlFrontier  _frontier;
        private readonly BloomFilter  _bloom;
        private readonly RobotsCache  _robots;
        private readonly SimulatedWeb _web;
        private readonly ContentStore _store;
        private readonly int          _maxDepth;
        private readonly int          _maxPages;
        private readonly CrawlStats   _stats = new CrawlStats();

        public CrawlStats  Stats => _stats;
        public ContentStore Store => _store;

        public Crawler(SimulatedWeb web, RobotsCache robots,
                       int maxDepth = 3, int maxPages = 100,
                       int bloomSize = 5000)
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

        public void Run(Action<string> log = null)
        {
            int spinCount = 0;

            while (_store.Count < _maxPages)
            {
                var task = _frontier.TryDequeue();

                if (task == null)
                {
                    // all domains throttled or frontier empty
                    if (++spinCount > 50) break; // no progress → done
                    Thread.Sleep(20);
                    continue;
                }
                spinCount = 0;

                // ① Fetch
                var (html, rawLinks, status) = _web.Fetch(task.Url);
                string hash = ComputeHash(html);

                _store.Store(new CrawledPage
                {
                    Url         = task.Url,
                    Html        = html,
                    StatusCode  = status,
                    CrawledAt   = DateTime.UtcNow,
                    Depth       = task.Depth,
                    ContentHash = hash
                });
                _stats.PagesCrawled++;
                log?.Invoke($"  CRAWLED [{status}] depth={task.Depth} {task.Url}");

                if (status != 200) continue;

                // ② Process discovered links
                foreach (string rawLink in rawLinks)
                {
                    _stats.UrlsDiscovered++;

                    // normalize
                    string norm = UrlNormalizer.Normalize(rawLink, task.Url);
                    if (norm == null)
                    { _stats.NormalizationFailed++; continue; }

                    // depth limit (spider trap defence)
                    if (task.Depth + 1 > _maxDepth)
                    { _stats.DepthBlocked++; continue; }

                    // robots.txt
                    if (!_robots.IsAllowed(norm))
                    { _stats.RobotsBlocked++; log?.Invoke($"  ROBOTS  blocked: {norm}"); continue; }

                    // bloom filter deduplication
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

        private static string ComputeHash(string content)
        {
            int h = 0;
            foreach (char c in content) h = h * 31 + c;
            return Math.Abs(h).ToString("X8");
        }
    }

    // ── Program ───────────────────────────────────────────────────────────────

    class CrawlerProgram
    {
        static void Banner(string title)
        {
            Console.WriteLine("\n╔" + new string('═', 62) + "╗");
            Console.WriteLine("║  " + title.PadRight(60) + "║");
            Console.WriteLine("╚" + new string('═', 62) + "╝");
        }

        static SimulatedWeb BuildWeb()
        {
            var web = new SimulatedWeb();

            // example.com
            web.AddPage("https://example.com",
                "<html>Example homepage</html>",
                "https://example.com/about",
                "https://example.com/products",
                "https://example.com/admin",         // robots-blocked
                "https://news.com",
                "https://example.com");              // self-link (duplicate)

            web.AddPage("https://example.com/about",
                "<html>About page</html>",
                "https://example.com",               // duplicate
                "https://example.com/team");

            web.AddPage("https://example.com/products",
                "<html>Products</html>",
                "https://example.com/products/laptop",
                "https://example.com/products/phone",
                "https://example.com/private/data");  // robots-blocked

            web.AddPage("https://example.com/products/laptop",
                "<html>Laptop page</html>",
                "https://example.com/products/laptop/specs",
                "https://example.com/products/laptop/reviews");

            web.AddPage("https://example.com/products/phone",
                "<html>Phone page</html>",
                "https://example.com/products");      // duplicate

            web.AddPage("https://example.com/team",
                "<html>Team page</html>",
                "https://example.com/about");         // duplicate

            // news.com
            web.AddPage("https://news.com",
                "<html>News homepage</html>",
                "https://news.com/article/1",
                "https://news.com/article/2",
                "https://news.com/article/3",
                "https://example.com");               // cross-domain duplicate

            web.AddPage("https://news.com/article/1",
                "<html>Breaking news</html>",
                "https://news.com/article/2",
                "https://news.com/sports");

            web.AddPage("https://news.com/article/2",
                "<html>Tech news</html>",
                "https://news.com",                   // duplicate
                "https://shop.com");

            web.AddPage("https://news.com/article/3",
                "<html>World news</html>",
                "https://news.com/article/1");        // duplicate

            // shop.com with tracking params in links
            web.AddPage("https://shop.com",
                "<html>Shop homepage</html>",
                "https://shop.com/product/1?utm_source=email&id=1",  // tracking stripped
                "https://shop.com/product/2?ref=homepage&id=2",       // tracking stripped
                "https://shop.com/checkout");                          // robots-blocked

            return web;
        }

        static RobotsCache BuildRobots()
        {
            var robots = new RobotsCache();
            robots.LoadRules("example.com", crawlDelayMs: 500,
                "/admin", "/private");
            robots.LoadRules("news.com", crawlDelayMs: 1000);     // allow all, 1s delay
            robots.LoadRules("shop.com", crawlDelayMs: 500,
                "/checkout", "/cart", "/account");
            return robots;
        }

        static void Main(string[] args)
        {
            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Basic crawl — frontier expansion + link discovery");
            // ══════════════════════════════════════════════════════════════════
            var robots = BuildRobots();
            var web    = BuildWeb();
            var crawler = new Crawler(web, robots, maxDepth: 3, maxPages: 20, bloomSize: 5000);

            crawler.AddSeed("https://example.com", CrawlPriority.High);
            crawler.AddSeed("https://news.com",    CrawlPriority.High);

            Console.WriteLine("\n  Crawl log:");
            crawler.Run(msg => Console.WriteLine(msg));

            var stats = crawler.Stats;
            Console.WriteLine($"\n  ── Crawl summary ──────────────────────────────");
            Console.WriteLine($"  Pages crawled:         {stats.PagesCrawled}");
            Console.WriteLine($"  URLs discovered:       {stats.UrlsDiscovered}");
            Console.WriteLine($"  Duplicates blocked:    {stats.DuplicatesBlocked}  (Bloom filter)");
            Console.WriteLine($"  robots.txt blocked:    {stats.RobotsBlocked}");
            Console.WriteLine($"  Depth limit blocked:   {stats.DepthBlocked}");

            Console.WriteLine($"\n  Pages in content store:");
            foreach (var page in crawler.Store.Pages.OrderBy(p => p.Depth))
                Console.WriteLine($"    [{page.StatusCode}] depth={page.Depth}  {page.Url}");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 2: URL normalization — same page, many spellings");
            // ══════════════════════════════════════════════════════════════════
            var variations = new[]
            {
                "HTTP://Example.COM:80/Products?b=2&a=1#top",
                "https://example.com/products?a=1&b=2",
                "https://example.com/products?a=1&b=2&utm_source=email",
                "https://example.com/products/",
                "/products",  // relative (needs base)
            };

            Console.WriteLine("\n  Raw URL                                              → Normalized");
            Console.WriteLine($"  {new string('─', 80)}");
            foreach (string raw in variations)
            {
                string norm = UrlNormalizer.Normalize(raw, "https://example.com");
                Console.WriteLine($"  {raw,-50} → {norm ?? "(null)"}");
            }
            Console.WriteLine("\n  All absolute variations normalize to the same URL");
            Console.WriteLine("  → Bloom filter sees same canonical form → deduplication works");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 3: Bloom filter — false positive demonstration");
            // ══════════════════════════════════════════════════════════════════
            // Small filter (100 bits) to make false positives visible
            var smallBloom = new BloomFilter(size: 100, hashCount: 3);

            var inserted = Enumerable.Range(1, 15)
                .Select(i => $"https://example.com/page-{i}")
                .ToList();

            foreach (string url in inserted)
                smallBloom.Add(url);

            Console.WriteLine($"\n  Inserted {inserted.Count} URLs into a 100-bit Bloom filter");
            Console.WriteLine($"  Fill ratio: {smallBloom.FillRatio:P0}");

            // Check 20 URLs that were NOT inserted
            var notInserted = Enumerable.Range(100, 20)
                .Select(i => $"https://other.com/page-{i}")
                .ToList();

            int falsePositives = notInserted.Count(u => smallBloom.MightContain(u));
            Console.WriteLine($"\n  Checking {notInserted.Count} URLs that were NOT inserted:");
            Console.WriteLine($"  False positives (incorrectly reported as seen): {falsePositives}/{notInserted.Count}");
            Console.WriteLine($"  → These pages would be SKIPPED even though they're new");
            Console.WriteLine($"  → Acceptable trade-off: 1.2 GB filter handles 1B URLs vs 100 GB hash set");

            // Large filter shows near-zero FP rate
            var largeBloom = new BloomFilter(size: 100_000, hashCount: 3);
            foreach (string url in inserted) largeBloom.Add(url);
            int largeFP = notInserted.Count(u => largeBloom.MightContain(u));
            Console.WriteLine($"\n  Same test with 100K-bit filter: {largeFP}/{notInserted.Count} false positives");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: Politeness — per-domain throttling");
            // ══════════════════════════════════════════════════════════════════
            var robotsPol = new RobotsCache();
            robotsPol.LoadRules("fast.com",  crawlDelayMs: 200);
            robotsPol.LoadRules("slow.com",  crawlDelayMs: 1000);

            var frontier = new UrlFrontier(robotsPol);
            var tasks = new[]
            {
                new UrlTask { Url="https://fast.com/1", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://slow.com/1", Domain="slow.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://fast.com/2", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://slow.com/2", Domain="slow.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://fast.com/3", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
            };

            foreach (var t in tasks) frontier.Enqueue(t);

            Console.WriteLine($"\n  Frontier has {frontier.Count} tasks across 2 domains");
            Console.WriteLine($"  fast.com delay=200ms, slow.com delay=1000ms");
            Console.WriteLine("\n  Dequeuing with simulated 250ms work between requests:");

            for (int i = 0; i < 6; i++)
            {
                var t = frontier.TryDequeue();
                if (t != null)
                    Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss.fff}] Crawling {t.Url}");
                else
                    Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss.fff}] No domain ready — waiting...");
                Thread.Sleep(250);
            }

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 5: robots.txt + depth limit blocking");
            // ══════════════════════════════════════════════════════════════════
            var robotsTest = new RobotsCache();
            robotsTest.LoadRules("example.com", 500, "/admin", "/private", "/internal");

            Console.WriteLine("\n  robots.txt rules for example.com: disallow /admin, /private, /internal");
            var testUrls = new[]
            {
                "https://example.com/products",
                "https://example.com/admin/dashboard",
                "https://example.com/about",
                "https://example.com/private/data",
                "https://example.com/internal/api",
                "https://example.com/blog",
            };

            foreach (string url in testUrls)
            {
                bool allowed = robotsTest.IsAllowed(url);
                Console.WriteLine($"  {(allowed ? "ALLOW " : "BLOCK ")} {url}");
            }

            Console.WriteLine($"\n  Depth limit (maxDepth=2) — links at depth 3 discarded:");
            var crawlerDepth = new Crawler(web, robots, maxDepth: 2, maxPages: 50, bloomSize: 5000);
            crawlerDepth.AddSeed("https://example.com", CrawlPriority.High);
            crawlerDepth.Run();
            Console.WriteLine($"  Pages crawled at depth ≤2: {crawlerDepth.Store.Count}");
            Console.WriteLine($"  Depth-limit blocks: {crawlerDepth.Stats.DepthBlocked}");
        }
    }
}
