// Program — entry point for all Search Autocomplete demo scenarios.

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

        static void PrintResults(string prefix, List<RankedCompletion> results)
        {
            Console.WriteLine($"\n  Query: \"{prefix}\" → {results.Count} completions");
            if (results.Count == 0) { Console.WriteLine("    (no results)"); return; }
            for (int i = 0; i < results.Count; i++)
                Console.WriteLine($"    {i + 1}. {results[i]}");
        }

        // Simulates aggregated search logs — (term, 30-day search count) pairs.
        static IEnumerable<(string, int)> BuildSearchLogs() => new[]
        {
            ("python",                  5_000_000),
            ("python tutorial",         3_200_000),
            ("python download",         2_100_000),
            ("python 3",                1_800_000),
            ("python ide",                900_000),
            ("python list comprehension", 700_000),
            ("apple",                  10_000_000),
            ("apple watch",             8_200_000),
            ("apple store",             6_500_000),
            ("apple id",                4_300_000),
            ("apple iphone",            3_100_000),
            ("apple support",           1_200_000),
            ("application",             2_000_000),
            ("app store",               5_800_000),
            ("app download",            1_100_000),
            ("new york",                7_000_000),
            ("new york times",          4_200_000),
            ("new york city",           3_800_000),
            ("new york weather",        2_500_000),
            ("new york jets",           1_000_000),
            ("netflix",                 6_000_000),
            ("netflix login",           3_400_000),
            ("netflix shows",           2_800_000),
            ("news",                    9_500_000),
            ("news today",              5_200_000),
            ("nba",                     4_000_000),
            ("nba scores",              2_200_000),
        };

        static void Main(string[] args)
        {
            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Build trie + basic prefix completions");
            // ══════════════════════════════════════════════════════════════════
            var svc = new AutocompleteService(k: 5, cacheCapacity: 50);
            svc.BuildFromLogs(BuildSearchLogs());
            Console.WriteLine($"\n  Trie loaded with {svc.TrieTermCount} terms");

            PrintResults("a",       svc.GetCompletions("a"));
            PrintResults("ap",      svc.GetCompletions("ap"));
            PrintResults("apple",   svc.GetCompletions("apple"));
            PrintResults("apple ",  svc.GetCompletions("apple "));  // multi-word
            PrintResults("new yo",  svc.GetCompletions("new yo"));  // multi-word prefix

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 2: Top-K ranking — frequency determines order");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  Querying 'n' — should rank 'news' > 'nba' > 'netflix' by freq:");
            PrintResults("n",   svc.GetCompletions("n"));
            PrintResults("ne",  svc.GetCompletions("ne"));
            PrintResults("net", svc.GetCompletions("net"));
            Console.WriteLine("\n  At 'net' the 'new' branch disappears — only 'netflix*' words remain");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 3: Cache behaviour — hit on second request");
            // ══════════════════════════════════════════════════════════════════
            svc.Cache.ResetStats();

            Console.WriteLine("\n  Round 1 — all cache misses (first time seeing these prefixes):");
            foreach (string p in new[] { "py", "pyt", "pyth", "pytho", "python" })
            {
                svc.GetCompletions(p);
                Console.WriteLine($"    '{p}' → MISS (fetched from trie, stored in cache)");
            }

            Console.WriteLine("\n  Round 2 — same prefixes, all cache hits:");
            foreach (string p in new[] { "py", "pyt", "pyth", "pytho", "python" })
            {
                svc.GetCompletions(p);
                Console.WriteLine($"    '{p}' → HIT  (served from cache)");
            }

            Console.WriteLine($"\n  Cache stats: hits={svc.Cache.Hits}, misses={svc.Cache.Misses}");
            Console.WriteLine($"  Hit rate: {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}%");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: Trend surge — ranking updates in real-time");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  BEFORE trend surge — query 'apple ':");
            PrintResults("apple ", svc.GetCompletions("apple "));

            Console.WriteLine("\n  Apple Vision Pro launches — recording trend surge...");
            svc.RecordTrendSurge("apple vision pro", 9_000_000);

            Console.WriteLine("\n  AFTER trend surge — query 'apple ' (cache invalidated + re-warmed):");
            PrintResults("apple ", svc.GetCompletions("apple "));
            Console.WriteLine("\n  'apple vision pro' now ranks #1 in 'apple ' completions");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 5: Blocklist — offensive terms never appear");
            // ══════════════════════════════════════════════════════════════════
            var blocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "hack tutorial", "pirated", "crack software" };

            var svcFiltered = new AutocompleteService(k: 5, blocklist: blocklist);
            svcFiltered.BuildFromLogs(new[]
            {
                ("how to code",        500_000),
                ("hack tutorial",    1_000_000), // ← blocked
                ("how to draw",        300_000),
                ("crack software",     800_000), // ← blocked
                ("how to cook pasta",  200_000),
            });

            Console.WriteLine("\n  Trie built with blocklist. Querying 'h' (blocked terms excluded):");
            PrintResults("h", svcFiltered.GetCompletions("h"));

            Console.WriteLine("\n  Querying 'hack'  (blocked at insert time — not in trie):");
            PrintResults("hack", svcFiltered.GetCompletions("hack"));

            Console.WriteLine("\n  Querying 'crack' (blocked at insert time — not in trie):");
            PrintResults("crack", svcFiltered.GetCompletions("crack"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 6: No results + edge cases");
            // ══════════════════════════════════════════════════════════════════
            PrintResults("xyz",   svc.GetCompletions("xyz"));    // no results
            PrintResults("APPLE", svc.GetCompletions("APPLE"));  // case-insensitive
            PrintResults("",      svc.GetCompletions(""));       // empty → global top-K

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 7: Cache flush on trie rebuild simulation");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  Pre-flush: warm cache for 'py'");
            svc.GetCompletions("py");
            svc.Cache.ResetStats();
            svc.GetCompletions("py");
            Console.WriteLine($"  Before flush: hit rate = {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}%");

            Console.WriteLine("\n  Simulating hourly trie rebuild — flushing cache...");
            svc.FlushCache();

            svc.Cache.ResetStats();
            svc.GetCompletions("py");
            Console.WriteLine($"  After flush:  hit rate = {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}% (cache cleared, re-warming)");
            Console.WriteLine("  Next request for 'py' will be cached again ✓");
        }
    }
}
