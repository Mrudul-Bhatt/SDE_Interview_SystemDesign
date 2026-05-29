// Program — entry point for all Web Crawler demo scenarios.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AdvancedDesigns
{
    class Program
    {
        static void Banner(string title)
        {
            Console.WriteLine("\n╔" + new string('═', 62) + "╗");
            Console.WriteLine("║  " + title.PadRight(60) + "║");
            Console.WriteLine("╚" + new string('═', 62) + "╝");
        }

        // Builds a small in-memory web graph with cross-domain links,
        // duplicate links, robots-blocked paths, and tracking-param URLs.
        static SimulatedWeb BuildWeb()
        {
            var web = new SimulatedWeb();

            web.AddPage("https://example.com",
                "<html>Example homepage</html>",
                "https://example.com/about",
                "https://example.com/products",
                "https://example.com/admin",          // robots-blocked
                "https://news.com",
                "https://example.com");               // self-link (duplicate)

            web.AddPage("https://example.com/about",
                "<html>About page</html>",
                "https://example.com",                // duplicate
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

            web.AddPage("https://shop.com",
                "<html>Shop homepage</html>",
                "https://shop.com/product/1?utm_source=email&id=1",  // tracking stripped
                "https://shop.com/product/2?ref=homepage&id=2",       // tracking stripped
                "https://shop.com/checkout");                          // robots-blocked

            return web;
        }

        // Loads per-domain robots.txt rules used across all demo scenarios.
        static RobotsCache BuildRobots()
        {
            var robots = new RobotsCache();
            robots.LoadRules("example.com", crawlDelayMs: 500, "/admin", "/private");
            robots.LoadRules("news.com", crawlDelayMs: 1000); // allow all, 1s delay
            robots.LoadRules("shop.com", crawlDelayMs: 500, "/checkout", "/cart", "/account");
            return robots;
        }

        static void Main(string[] args)
        {
            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Basic crawl — frontier expansion + link discovery");
            // ══════════════════════════════════════════════════════════════════
            var robots = BuildRobots();
            var web = BuildWeb();
            var crawler = new Crawler(web, robots, maxDepth: 3, maxPages: 20, bloomSize: 5000);

            crawler.AddSeed("https://example.com", CrawlPriority.High);
            crawler.AddSeed("https://news.com", CrawlPriority.High);

            Console.WriteLine("\n  Crawl log:");
            crawler.Run(msg => Console.WriteLine(msg));

            var stats = crawler.Stats;
            Console.WriteLine($"\n  ── Crawl summary ──────────────────────────");
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
                "/products",
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
            var smallBloom = new BloomFilter(size: 100, hashCount: 3);
            var inserted = Enumerable.Range(1, 15)
                .Select(i => $"https://example.com/page-{i}").ToList();

            foreach (string url in inserted) smallBloom.Add(url);

            Console.WriteLine($"\n  Inserted {inserted.Count} URLs into a 100-bit Bloom filter");
            Console.WriteLine($"  Fill ratio: {smallBloom.FillRatio:P0}");

            var notInserted = Enumerable.Range(100, 20)
                .Select(i => $"https://other.com/page-{i}").ToList();
            int falsePositives = notInserted.Count(u => smallBloom.MightContain(u));

            Console.WriteLine($"\n  Checking {notInserted.Count} URLs that were NOT inserted:");
            Console.WriteLine($"  False positives: {falsePositives}/{notInserted.Count}");
            Console.WriteLine($"  → These pages would be SKIPPED even though they're new");
            Console.WriteLine($"  → Acceptable trade-off: 1.2 GB filter handles 1B URLs vs 100 GB HashSet");

            var largeBloom = new BloomFilter(size: 100_000, hashCount: 3);
            foreach (string url in inserted) largeBloom.Add(url);
            int largeFP = notInserted.Count(u => largeBloom.MightContain(u));
            Console.WriteLine($"\n  Same test with 100K-bit filter: {largeFP}/{notInserted.Count} false positives");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: Politeness — per-domain throttling");
            // ══════════════════════════════════════════════════════════════════
            var robotsPol = new RobotsCache();
            robotsPol.LoadRules("fast.com", crawlDelayMs: 200);
            robotsPol.LoadRules("slow.com", crawlDelayMs: 1000);

            var frontier = new UrlFrontier(robotsPol);
            foreach (var t in new[]
            {
                new UrlTask { Url="https://fast.com/1", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://slow.com/1", Domain="slow.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://fast.com/2", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://slow.com/2", Domain="slow.com", Priority=CrawlPriority.High, Depth=0 },
                new UrlTask { Url="https://fast.com/3", Domain="fast.com", Priority=CrawlPriority.High, Depth=0 },
            }) frontier.Enqueue(t);

            Console.WriteLine($"\n  Frontier: {frontier.Count} tasks | fast.com=200ms, slow.com=1000ms");
            Console.WriteLine("\n  Dequeuing with 250ms work between requests:");

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

            Console.WriteLine("\n  robots.txt disallows: /admin, /private, /internal");
            foreach (string url in new[]
            {
                "https://example.com/products",
                "https://example.com/admin/dashboard",
                "https://example.com/about",
                "https://example.com/private/data",
                "https://example.com/internal/api",
                "https://example.com/blog",
            })
            {
                bool allowed = robotsTest.IsAllowed(url);
                Console.WriteLine($"  {(allowed ? "ALLOW" : "BLOCK")}  {url}");
            }

            Console.WriteLine($"\n  Depth limit (maxDepth=2) — links at depth 3 discarded:");
            var crawlerDepth = new Crawler(web, robots, maxDepth: 2, maxPages: 50, bloomSize: 5000);
            crawlerDepth.AddSeed("https://example.com", CrawlPriority.High);
            crawlerDepth.Run();
            Console.WriteLine($"  Pages crawled at depth ≤2: {crawlerDepth.Store.Count}");
            Console.WriteLine($"  Depth-limit blocks:        {crawlerDepth.Stats.DepthBlocked}");
        }
    }
}
