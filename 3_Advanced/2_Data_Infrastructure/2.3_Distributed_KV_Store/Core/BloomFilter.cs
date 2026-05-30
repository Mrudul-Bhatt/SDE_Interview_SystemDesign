// BloomFilter — probabilistic set membership check used by each SSTable.
//
// Why per-SSTable: before scanning an SSTable's sorted data (expensive disk I/O),
// the Bloom filter answers "does this key DEFINITELY not exist?" in O(1).
// A false positive just means we do a needless SSTable scan — acceptable.
// A false negative is impossible by design (only false positives, never false negatives).
//
// Why multiple hash seeds: k independent hash functions reduce false positive rate.
// Standard rule of thumb: k = (m/n) * ln(2), where m=bits, n=expected items.

namespace AdvancedDesigns
{
    public class BloomFilter
    {
        private readonly bool[] _bits;
        private readonly int    _size;
        private readonly int    _hashCount;

        public BloomFilter(int size = 10000, int hashCount = 7)
        {
            _size      = size;
            _hashCount = hashCount;
            _bits      = new bool[size];
        }

        public void Add(string item)
        {
            foreach (int pos in GetPositions(item))
                _bits[pos] = true;
        }

        // "Definitely not" = any bit position is unset → item was never added.
        // "Probably yes"   = all positions set → item was added, OR a hash collision.
        // This asymmetry is the core Bloom filter guarantee: no false negatives, only false positives.
        public bool MightContain(string item)
            => GetPositions(item).All(pos => _bits[pos]);

        private IEnumerable<int> GetPositions(string item)
        {
            for (int seed = 0; seed < _hashCount; seed++)
            {
                // Knuth multiplicative hash with a per-seed offset keeps
                // the k positions spread across the bit array independently.
                int hash = seed * unchecked((int)2654435761u);
                foreach (char c in item) hash = hash * 31 + c;
                yield return Math.Abs(hash % _size);
            }
        }
    }
}
