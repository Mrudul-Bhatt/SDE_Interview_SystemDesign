// Program — entry point for all URL Shortener demo scenarios.
// Each scenario is self-contained and exercises one feature of the system.

using System;
using System.Collections.Generic;
using System.Linq;

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

        // Prints one redirect result line: status code, destination URL, and cache/DB source.
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
            long[] sampleIds = { 100_001, 100_062, 200_000, 1_000_000, 3_521_614_606_207L };
            Console.WriteLine($"\n  {"ID",-20} {"Base62 Code",-12} {"Decoded back"}");
            Console.WriteLine($"  {new string('─', 50)}");
            foreach (long id in sampleIds)
            {
                string code   = Base62.Encode(id);
                long decoded  = Base62.Decode(code);
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

            string code1 = url1.Split('/').Last();
            string code2 = url2.Split('/').Last();

            Console.WriteLine("\n  First access (pre-warmed on creation → cache hit):");
            PrintRedirect(code1, svc.Redirect(code1));
            PrintRedirect(code2, svc.Redirect(code2));

            Console.WriteLine("\n  Second access (still in cache):");
            PrintRedirect(code1, svc.Redirect(code1));
            PrintRedirect(code2, svc.Redirect(code2));

            Console.WriteLine($"\n  Cache state: {svc.Cache.Count} entries, " +
                              $"hits={svc.Cache.Hits}, misses={svc.Cache.Misses}");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 3: Custom alias + conflict detection");
            // ══════════════════════════════════════════════════════════════════
            var (aliasUrl, _)  = svc.Shorten("https://mycompany.com/annual-report-2024.pdf", customAlias: "annual-report");
            Console.WriteLine($"\n  Custom alias created: {aliasUrl}");

            var (_, err2) = svc.Shorten("https://other.com/report.pdf", customAlias: "annual-report");
            Console.WriteLine($"  Duplicate alias attempt  → Error: {err2}");

            var (_, err3) = svc.Shorten("https://example.com", customAlias: "admin");
            Console.WriteLine($"  Reserved alias attempt   → Error: {err3}");

            var (_, err4) = svc.Shorten("https://example.com", customAlias: "bad alias!");
            Console.WriteLine($"  Invalid alias attempt    → Error: {err4}");

            Console.WriteLine("\n  Resolving custom alias:");
            PrintRedirect("annual-report", svc.Redirect("annual-report"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: URL expiry — TTL enforcement");
            // ══════════════════════════════════════════════════════════════════
            var svc2 = new UrlShortenerService();
            var (shortUrl, _) = svc2.Shorten("https://flash-sale.example.com/promo",
                                              customAlias: "flash-sale", ttlDays: 1);
            Console.WriteLine($"\n  Created with TTL: {shortUrl}");

            Console.WriteLine("  Access before expiry:");
            PrintRedirect("flash-sale", svc2.Redirect("flash-sale"));

            // Inject an already-expired record directly into cache to test 410 handling.
            svc2.Cache.Put("expired", new UrlRecord
            {
                Id        = 999,
                ShortCode = "expired",
                LongUrl   = "https://old-promo.example.com",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddSeconds(-1), // already past TTL
                IsActive  = true
            });

            Console.WriteLine("\n  Access after expiry:");
            PrintRedirect("expired", svc2.Redirect("expired"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 5: Analytics — click tracking by country");
            // ══════════════════════════════════════════════════════════════════
            var svc3 = new UrlShortenerService();
            var (promoUrl, _) = svc3.Shorten("https://store.example.com/summer-sale",
                                             customAlias: "summer-sale");
            Console.WriteLine($"\n  Promo URL: {promoUrl}");
            Console.WriteLine("  Simulating 12 clicks from different countries...");

            string[] countries = { "US", "US", "US", "US", "UK", "UK", "DE", "DE", "IN", "IN", "CA", "AU" };
            foreach (string country in countries)
                svc3.Redirect("summer-sale", country);

            var (total, byCountry) = svc3.GetStats("summer-sale");
            Console.WriteLine($"\n  Total clicks: {total}");
            Console.WriteLine("  By country:");
            foreach (var kv in byCountry.OrderByDescending(x => x.Value))
                Console.WriteLine($"    {kv.Key}: {kv.Value} clicks");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 6: Deactivation + 404 for unknown code");
            // ══════════════════════════════════════════════════════════════════
            var svc4 = new UrlShortenerService();
            var (link, _)  = svc4.Shorten("https://content.example.com/secret-doc");
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
            var svc5  = new UrlShortenerService(cacheCapacity: 3);
            var codes = new List<string>();

            Console.WriteLine("\n  Creating 5 URLs (cache capacity = 3):");
            for (int i = 1; i <= 5; i++)
            {
                var (u, _) = svc5.Shorten($"https://example.com/page-{i}");
                string c   = u.Split('/').Last();
                codes.Add(c);
                Console.WriteLine($"    {c} → cached (cache size: {svc5.Cache.Count})");
            }

            Console.WriteLine($"\n  Cache holds last 3 created: {codes[2]}, {codes[3]}, {codes[4]}");
            Console.WriteLine($"  Evicted during creation: {codes[0]}, {codes[1]}");

            Console.WriteLine("\n  Accessing 3 hot codes (still in cache) → all HITs:");
            svc5.Cache.ResetStats();
            foreach (string c in codes.Skip(2))
            {
                var r = svc5.Redirect(c);
                Console.WriteLine($"    {c}: {(r.FromCache ? "HIT " : "MISS")} → {r.LongUrl}");
            }
            Console.WriteLine($"  Hit rate: {svc5.Cache.Hits * 100.0 / (svc5.Cache.Hits + svc5.Cache.Misses):F0}% (3/3)");

            Console.WriteLine("\n  Accessing 2 evicted codes → cache MISSes, DB fallback:");
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
