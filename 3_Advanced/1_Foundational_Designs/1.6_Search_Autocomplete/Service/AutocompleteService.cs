// AutocompleteService — orchestrates Trie + PrefixCache.
// This is the only class callers interact with.
// Read path:   cache → trie (O(L) either way, but cache avoids even the trie walk)
// Write path:  trie update → targeted cache invalidation for affected prefixes

using System.Collections.Generic;

namespace AdvancedDesigns
{
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

        // Bulk-load from aggregated search logs (run once at startup or after rebuild).
        // In production this reads from a batch job output: (term, 30-day-count) pairs.
        public void BuildFromLogs(IEnumerable<(string Term, int Frequency)> logs)
        {
            foreach (var (term, freq) in logs)
                _trie.Insert(term, freq);
        }

        // Hot path: called on every keystroke.
        // Cache-first so the trie is only hit on the first occurrence of each prefix.
        public List<RankedCompletion> GetCompletions(string prefix)
        {
            prefix = (prefix ?? "").ToLowerInvariant().Trim();

            if (_cache.TryGet(prefix, out var cached))
                return cached;

            var results = _trie.Search(prefix);

            // Store in cache so the next identical prefix is O(1) from Redis,
            // not O(L) from the trie.
            _cache.Put(prefix, results);
            return results;
        }

        // Handle a viral trend: one term's frequency spikes (e.g. "apple vision pro"
        // after a product launch). Update the trie and surgically invalidate the cache
        // only for prefixes of that term — not a full flush.
        public void RecordTrendSurge(string term, int newFrequency)
        {
            _trie.UpdateFrequency(term, newFrequency);

            // Invalidate and re-warm cache for every prefix of the updated term.
            // Example: "apple vision pro" → invalidate "a", "ap", "app", ..., "apple vision pro"
            // Re-warming immediately (rather than lazy-miss) keeps latency consistent.
            for (int i = 1; i <= term.Length; i++)
            {
                string prefix = term.Substring(0, i);
                _cache.Put(prefix, _trie.Search(prefix));
            }
        }

        // After a full trie rebuild, flush the entire cache.
        // All cached TopK lists are stale — the new trie may have completely different rankings.
        public void FlushCache() => _cache.Flush();

        public PrefixCache Cache    => _cache;
        public int TrieTermCount    => _trie.TotalTerms;
    }
}
