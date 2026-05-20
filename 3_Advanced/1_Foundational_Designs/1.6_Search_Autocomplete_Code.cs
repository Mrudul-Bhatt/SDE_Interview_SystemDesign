using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// ============================================================
//  Search Autocomplete (Typeahead) — in-memory simulation
//  Covers: Trie with pre-computed top-K at every node (O(L) lookup),
//          LRU prefix cache, frequency-based ranking, real-time
//          frequency updates, blocklist filtering, multi-word prefixes
// ============================================================

namespace AdvancedDesigns
{
    // ── RankedCompletion ───────────────────────────────────────────────────────

    public class RankedCompletion
    {
        public string Term      { get; set; }
        public int    Frequency { get; set; }

        public override string ToString() => $"\"{Term}\" (freq={Frequency:N0})";
    }

    // ── TrieNode ──────────────────────────────────────────────────────────────

    // Each node stores the top-K completions for its entire subtree.
    // This trades memory for O(L) query time instead of O(L + subtree).
    public class TrieNode
    {
        public Dictionary<char, TrieNode> Children    { get; } = new Dictionary<char, TrieNode>();
        public bool                       IsEndOfWord  { get; set; }
        public int                        Frequency    { get; set; }
        public List<RankedCompletion>     TopK         { get; set; } = new List<RankedCompletion>();
    }

    // ── Trie ──────────────────────────────────────────────────────────────────

    public class Trie
    {
        private readonly TrieNode        _root     = new TrieNode();
        private readonly int             _k;
        private readonly HashSet<string> _blocklist;

        public int TotalTerms { get; private set; }

        public Trie(int k = 5, HashSet<string> blocklist = null)
        {
            _k         = k;
            _blocklist = blocklist
                         ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Insert a term with its search frequency.
        // Updates topK at every ancestor node so lookups are O(prefix_length).
        public void Insert(string term, int frequency)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            term = term.ToLowerInvariant().Trim();
            if (_blocklist.Contains(term)) return;

            var completion = new RankedCompletion { Term = term, Frequency = frequency };

            var node = _root;
            UpdateTopK(node, completion); // root holds global top-K

            foreach (char c in term)
            {
                if (!node.Children.TryGetValue(c, out var child))
                    node.Children[c] = child = new TrieNode();
                node = child;
                UpdateTopK(node, completion); // every ancestor updated
            }

            node.IsEndOfWord = true;
            node.Frequency   = frequency;
            TotalTerms++;
        }

        // Walk to prefix node in O(L); return its pre-computed topK.
        public List<RankedCompletion> Search(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return _root.TopK; // empty prefix → global top-K

            prefix = prefix.ToLowerInvariant().Trim();
            var node = _root;
            foreach (char c in prefix)
            {
                if (!node.Children.TryGetValue(c, out var child))
                    return new List<RankedCompletion>(); // prefix not in trie
                node = child;
            }
            return node.TopK;
        }

        // Maintains a sorted top-K list at a node — replaces stale entry if same term.
        private void UpdateTopK(TrieNode node, RankedCompletion completion)
        {
            node.TopK.RemoveAll(c => c.Term == completion.Term);
            node.TopK.Add(completion);
            // keep sorted descending by frequency, capped at K
            if (node.TopK.Count > _k)
            {
                node.TopK.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));
                node.TopK.RemoveRange(_k, node.TopK.Count - _k);
            }
            else
            {
                node.TopK.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));
            }
        }

        // Update frequency for an existing term (simulates a trend surge).
        // Re-inserts with new frequency so all ancestor topK lists refresh.
        public void UpdateFrequency(string term, int newFrequency)
            => Insert(term, newFrequency); // insert is idempotent for same term
    }

    // ── PrefixCache ───────────────────────────────────────────────────────────

    // Simulates Redis: maps prefix → top-K results, capacity-bounded LRU.
    public class PrefixCache
    {
        private readonly Dictionary<string, LinkedListNode<(string Key, List<RankedCompletion> Value)>> _map
            = new Dictionary<string, LinkedListNode<(string, List<RankedCompletion>)>>(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, List<RankedCompletion> Value)> _list
            = new LinkedList<(string, List<RankedCompletion>)>();
        private readonly int _capacity;

        public int Hits   { get; private set; }
        public int Misses { get; private set; }

        public PrefixCache(int capacity = 200) => _capacity = capacity;

        public bool TryGet(string prefix, out List<RankedCompletion> results)
        {
            if (_map.TryGetValue(prefix, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                results = node.Value.Value;
                Hits++;
                return true;
            }
            results = null;
            Misses++;
            return false;
        }

        public void Put(string prefix, List<RankedCompletion> results)
        {
            if (_map.TryGetValue(prefix, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(prefix);
            }
            else if (_map.Count >= _capacity)
            {
                var lru = _list.Last;
                _list.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
            var n = _list.AddFirst((prefix, results));
            _map[prefix] = n;
        }

        // Called when trie is rebuilt — flush all stale cache entries
        public void Flush()
        {
            _map.Clear();
            _list.Clear();
        }

        public void ResetStats() { Hits = 0; Misses = 0; }
    }

    // ── AutocompleteService ───────────────────────────────────────────────────

    public class AutocompleteService
    {
        private readonly Trie        _trie;
        private readonly PrefixCache _cache;

        public AutocompleteService(int k = 5, int cacheCapacity = 200,
                                   HashSet<string> blocklist = null)
        {
            _trie  = new Trie(k, blocklist);
            _cache = new PrefixCache(cacheCapacity);
        }

        // Bulk-load from aggregated search log (term, frequency) pairs
        public void BuildFromLogs(IEnumerable<(string Term, int Frequency)> logs)
        {
            foreach (var (term, freq) in logs)
                _trie.Insert(term, freq);
        }

        public List<RankedCompletion> GetCompletions(string prefix)
        {
            prefix = (prefix ?? "").ToLowerInvariant().Trim();

            if (_cache.TryGet(prefix, out var cached))
                return cached;

            var results = _trie.Search(prefix);
            _cache.Put(prefix, results);
            return results;
        }

        // Simulate a trend surge: re-insert term with boosted frequency
        public void RecordTrendSurge(string term, int newFrequency)
        {
            _trie.UpdateFrequency(term, newFrequency);
            // Invalidate cache for all prefixes of this term
            for (int i = 1; i <= term.Length; i++)
                _cache.Put(term.Substring(0, i), _trie.Search(term.Substring(0, i)));
        }

        // Called after a full trie rebuild; flush Redis equivalent
        public void FlushCache() => _cache.Flush();

        public PrefixCache Cache => _cache;
        public int TrieTermCount  => _trie.TotalTerms;
    }

    // ── Program ───────────────────────────────────────────────────────────────

    class AutocompleteProgram
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

        // Simulates Google-scale search logs (term, aggregated frequency)
        static IEnumerable<(string, int)> BuildSearchLogs() => new[]
        {
            // tech / general
            ("python",                 5_000_000),
            ("python tutorial",        3_200_000),
            ("python download",        2_100_000),
            ("python 3",               1_800_000),
            ("python ide",               900_000),
            ("python list comprehension",700_000),
            // apple
            ("apple",                 10_000_000),
            ("apple watch",            8_200_000),
            ("apple store",            6_500_000),
            ("apple id",               4_300_000),
            ("apple iphone",           3_100_000),
            ("apple support",          1_200_000),
            ("application",            2_000_000),
            ("app store",              5_800_000),
            ("app download",           1_100_000),
            // new york
            ("new york",               7_000_000),
            ("new york times",         4_200_000),
            ("new york city",          3_800_000),
            ("new york weather",       2_500_000),
            ("new york jets",          1_000_000),
            // other
            ("netflix",                6_000_000),
            ("netflix login",          3_400_000),
            ("netflix shows",          2_800_000),
            ("news",                   9_500_000),
            ("news today",             5_200_000),
            ("nba",                    4_000_000),
            ("nba scores",             2_200_000),
        };

        static void Main(string[] args)
        {
            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Build trie + basic prefix completions");
            // ══════════════════════════════════════════════════════════════════
            var svc = new AutocompleteService(k: 5, cacheCapacity: 50);
            svc.BuildFromLogs(BuildSearchLogs());
            Console.WriteLine($"\n  Trie loaded with {svc.TrieTermCount} terms");

            PrintResults("a",      svc.GetCompletions("a"));
            PrintResults("ap",     svc.GetCompletions("ap"));
            PrintResults("apple",  svc.GetCompletions("apple"));
            PrintResults("apple ", svc.GetCompletions("apple "));  // multi-word
            PrintResults("new yo", svc.GetCompletions("new yo"));  // multi-word prefix

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 2: Top-K ranking — frequency determines order");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  Querying 'n' — should rank 'news' > 'nba' > 'netflix' by freq:");
            PrintResults("n",   svc.GetCompletions("n"));
            PrintResults("ne",  svc.GetCompletions("ne"));
            PrintResults("net", svc.GetCompletions("net"));
            Console.WriteLine("\n  Notice: at 'net' the 'new' branch disappears — only 'netflix*' words remain");

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
                Console.WriteLine($"    '{p}' → HIT  (served from Redis/cache)");
            }

            Console.WriteLine($"\n  Cache stats: hits={svc.Cache.Hits}, misses={svc.Cache.Misses}");
            Console.WriteLine($"  Hit rate: {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}%");

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: Trend surge — ranking updates in real-time");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  BEFORE trend surge — query 'apple ':");
            PrintResults("apple ", svc.GetCompletions("apple "));

            // "apple vision pro" launches; search volume spikes
            Console.WriteLine("\n  Apple Vision Pro launches — recording trend surge...");
            svc.RecordTrendSurge("apple vision pro", 9_000_000); // surpasses "apple watch"

            Console.WriteLine("\n  AFTER trend surge — query 'apple ' (cache invalidated):");
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

            Console.WriteLine("\n  Querying 'hack':  (blocked at insert time — not in trie)");
            PrintResults("hack", svcFiltered.GetCompletions("hack"));

            Console.WriteLine("\n  Querying 'crack': (blocked at insert time — not in trie)");
            PrintResults("crack", svcFiltered.GetCompletions("crack"));

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 6: No results + partial prefix edge cases");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  Edge cases on original service:");
            PrintResults("xyz",    svc.GetCompletions("xyz"));    // no results
            PrintResults("APPLE",  svc.GetCompletions("APPLE"));  // case-insensitive
            PrintResults("",       svc.GetCompletions(""));       // empty → global top-K

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 7: Cache flush on trie rebuild simulation");
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n  Pre-flush: 'py' is cached");
            svc.GetCompletions("py");
            svc.Cache.ResetStats();
            svc.GetCompletions("py"); // should be HIT
            Console.WriteLine($"  Before flush: hit rate = {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}%");

            Console.WriteLine("\n  Simulating hourly trie rebuild — flushing cache...");
            svc.FlushCache();

            svc.Cache.ResetStats();
            svc.GetCompletions("py"); // should be MISS after flush
            Console.WriteLine($"  After flush:  hit rate = {svc.Cache.Hits * 100.0 / (svc.Cache.Hits + svc.Cache.Misses):F0}% (cache cleared, re-warming)");
            Console.WriteLine("  Next request for 'py' will be cached again ✓");
        }
    }
}
