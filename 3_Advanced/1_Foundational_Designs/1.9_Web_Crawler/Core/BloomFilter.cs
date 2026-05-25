// BloomFilter — probabilistic set for URL deduplication.
// O(1) insert and lookup, zero false negatives, ~1% false positives at correct sizing.
//
// Why Bloom filter instead of a HashSet?
//   1B URLs × 50 bytes each = 50 GB HashSet in RAM — impractical.
//   1B URLs in a Bloom filter with k=3, m=10B bits = 1.2 GB — fits in RAM.
//   Trade-off: ~1% of new URLs are falsely reported as "already seen" and skipped.
//   At web scale this is acceptable; missing 1% of pages beats OOM crashing.
//
// How k hash functions are simulated:
//   We use one polynomial rolling hash with k different seeds.
//   Each seed produces an independent bit position in the array.
//   All k positions must be set for MightContain to return true.

namespace AdvancedDesigns
{
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
        // "Definitely not" = at least one bit position is unset — the item was never added.
        // "Probably yes"   = all bit positions are set — could be the item OR a collision.
        public bool MightContain(string item)
        {
            ChecksPerformed++;
            return GetPositions(item).All(pos => _bits[pos]);
        }

        // k hash functions via seeded polynomial rolling hash.
        // Knuth's multiplicative constant (2654435761) spreads seeds far apart in the
        // hash space so the k positions are as independent as possible.
        private IEnumerable<int> GetPositions(string item)
        {
            for (int seed = 0; seed < _hashCount; seed++)
            {
                int hash = seed * unchecked((int)2654435761u);
                foreach (char c in item)
                    hash = hash * 31 + c;
                yield return Math.Abs(hash % _size);
            }
        }

        // Fill ratio approaching 50% is where false positive rate starts climbing fast.
        // Rule: size the filter so fill ratio stays under 50% for expected item count.
        public double FillRatio => _bits.Count(b => b) / (double)_size;
    }
}
