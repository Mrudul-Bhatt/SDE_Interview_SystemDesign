// Trie — prefix tree with pre-computed top-K at every node.
//
// Query complexity: O(L) where L = prefix length.
// Insert complexity: O(L * K) — walks L nodes, updates a sorted K-list at each.
//
// Trade-off vs brute-force:
//   Brute force: scan all terms for matching prefix → O(N) per query
//   Trie:        walk L nodes → O(L) per query, but O(L*K) per insert
//   At read-heavy scale (10K QPS vs 1 write/min), O(L) reads win decisively.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class Trie
    {
        private readonly TrieNode _root = new();
        private readonly int _k;
        private readonly HashSet<string> _blocklist;

        public int TotalTerms { get; private set; }

        public Trie(int k = 5, HashSet<string> blocklist = null)
        {
            _k = k;
            // OrdinalIgnoreCase: blocklist lookups are case-insensitive so "Hack" and
            // "hack" are both blocked without duplicating entries.
            _blocklist = blocklist ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Insert a term with its aggregated search frequency.
        // Also called by UpdateFrequency — re-inserting the same term with a new
        // frequency overwrites the old entry in every ancestor's TopK list.
        public void Insert(string term, int frequency)
        {
            if (string.IsNullOrWhiteSpace(term)) return;

            // Normalise to lowercase so "Apple" and "apple" are the same term.
            // All queries are also lowercased, so casing never affects results.
            term = term.ToLowerInvariant().Trim();

            // Drop blocked terms at insert time so they never enter the trie.
            // Filtering at query time is weaker — it would still waste memory storing them.
            if (_blocklist.Contains(term)) return;

            var completion = new RankedCompletion { Term = term, Frequency = frequency };

            var node = _root;
            UpdateTopK(node, completion); // root holds the global top-K (empty-prefix query)

            foreach (char c in term)
            {
                if (!node.Children.TryGetValue(c, out var child))
                    node.Children[c] = child = new TrieNode();
                node = child;

                // Update every ancestor so any prefix of this term reflects the new frequency.
                // Without this, a query for "ap" would miss updates to "apple" made after build time.
                UpdateTopK(node, completion);
            }

            node.IsEndOfWord = true;
            node.Frequency = frequency;
            TotalTerms++;
        }

        // Walk to the prefix node in O(L), then return its already-sorted TopK list.
        public List<RankedCompletion> Search(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return _root.TopK; // empty prefix → return global top-K

            prefix = prefix.ToLowerInvariant().Trim();
            var node = _root;
            foreach (char c in prefix)
            {
                if (!node.Children.TryGetValue(c, out var child))
                    return new List<RankedCompletion>(); // prefix has no entries in trie
                node = child;
            }
            return node.TopK;
        }

        // Re-insert with a new frequency — all ancestor TopK lists are updated automatically.
        public void UpdateFrequency(string term, int newFrequency)
            => Insert(term, newFrequency);

        // Maintains a sorted, capacity-bounded TopK list at one node.
        // RemoveAll first handles re-insertion: removes the stale entry for the same term
        // before adding the updated one, so a term never appears twice in the list.
        private void UpdateTopK(TrieNode node, RankedCompletion completion)
        {
            node.TopK.RemoveAll(c => c.Term == completion.Term);
            node.TopK.Add(completion);

            // Sort descending by frequency, then cap at K.
            // Sorting after every insert is O(K log K) — acceptable because K is tiny (5–10).
            node.TopK.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));
            if (node.TopK.Count > _k)
                node.TopK.RemoveRange(_k, node.TopK.Count - _k);
        }
    }
}
