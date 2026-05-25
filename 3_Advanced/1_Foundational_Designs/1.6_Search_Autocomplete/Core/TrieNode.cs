// TrieNode — one character in the trie, plus pre-computed top-K for its subtree.
//
// Key design: each node caches its own TopK list instead of computing it on demand.
// This trades memory for query speed:
//   Without TopK cache: query walks to the prefix node, then DFS the entire subtree → O(L + subtree)
//   With TopK cache:    query walks to the prefix node, reads TopK list → O(L)
// At Google scale (1B searches/day), shaving O(subtree) per query matters enormously.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class TrieNode
    {
        // One child per character. Dictionary (not array[26]) because terms can contain
        // spaces, digits, and hyphens — not just lowercase letters.
        public Dictionary<char, TrieNode> Children   { get; } = new();

        // True only at the final character of a fully inserted term.
        public bool                       IsEndOfWord { get; set; }

        // The search frequency of the term ending at this node (0 if not a word end).
        public int                        Frequency   { get; set; }

        // Pre-sorted top-K completions for this node's entire subtree.
        // Updated on every Insert() so reads are always O(1) — no sorting at query time.
        public List<RankedCompletion>     TopK        { get; set; } = new();
    }
}
