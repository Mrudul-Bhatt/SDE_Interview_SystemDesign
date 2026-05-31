// BloomFilter — probabilistic membership tester that answers "is this key DEFINITELY absent?"
//
// THE BIG IDEA:
// Imagine a checklist of 100 000 boxes, all starting unchecked. When you add a key,
// you check 7 specific boxes (chosen by hashing the key 7 different ways). To ask
// "does this key exist?", you look at those same 7 boxes:
//   - Any box is UNCHECKED → the key was DEFINITELY never added. Return false.
//     No false negatives: a box only gets checked when a key is added, so an
//     unchecked box is a guarantee the key is absent.
//   - All 7 boxes are CHECKED → the key was PROBABLY added. Return true.
//     But wait — another key might have coincidentally checked the same 7 boxes
//     (a hash collision). So "all checked" means "possibly yes, but maybe no".
//     That's a false positive — an acceptable lie.
//
// WHY THIS MATTERS FOR SSTABLES:
// Without a Bloom filter, a GET for a missing key must scan EVERY SSTable on disk
// to confirm the key isn't there — expensive. With Bloom filters, a "definitely not"
// answer skips the SSTable scan entirely with zero disk I/O. In production systems
// (Cassandra, RocksDB, HBase), Bloom filters cut read latency for missing keys by
// 10-50× at the cost of a few KB of memory per SSTable.
//
// WHY MULTIPLE HASH FUNCTIONS (not just one)?
// With one hash function, two different keys map to the same bit position ~1/size
// of the time — high collision rate = high false-positive rate. With k independent
// hash functions, a false positive requires ALL k positions to collide simultaneously.
// The probability drops exponentially with each extra hash function added.
//
// The sweet spot: k = (m/n) × ln2 [ln2 => log 2]
//   m = bit array size (100 000 here)
//   n = expected number of keys (~10 000)
//   k = (100 000 / 10 000) × 0.693 ≈ 7   ← why we default to hashCount=7
//
// At this setting the false-positive rate is approximately (1/2)^7 ≈ 0.8%.
// Meaning: ~1 in 125 "maybe yes" answers is wrong (causes a needless SSTable scan).
// That's a fine trade-off — 124 out of 125 unnecessary scans are avoided.
//
// WHY NOT JUST USE A HASHSET?
// A HashSet<string> stores the actual keys — thousands of strings eating megabytes
// of memory per SSTable. A Bloom filter uses a FIXED number of bits regardless of
// how many keys are added. 100 000 bits = 12.5 KB for any number of keys.
// The trade-off: Bloom filter has false positives; HashSet never does. We accept
// the ~1% false-positive rate to save orders of magnitude of memory.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class BloomFilter
    {
        // The checklist: a fixed-size bit array. All bits start false (unchecked).
        // We use bool[] for clarity; a production implementation would use a BitArray
        // or even a byte[] to pack 8 bits per byte and halve the memory footprint.
        private readonly bool[] _bits;

        // Total number of bits in the array. Larger = lower false-positive rate,
        // but more memory. 100 000 bits ≈ 12.5 KB — a negligible per-SSTable cost.
        private readonly int _size;

        // How many different hash positions we set/check per key.
        // More hashes = lower false-positive rate, but slightly slower Add/MightContain.
        // 7 is optimal for our 100 000-bit array and ~10 000 expected keys.
        private readonly int _hashCount;

        public BloomFilter(int size = 10000, int hashCount = 7)
        {
            _size = size;
            _hashCount = hashCount;
            _bits = new bool[size];
        }

        // Record that a key exists: set all _hashCount bit positions for this key.
        // Once a bit is set to true it is NEVER reset — the filter is append-only.
        // (Clearing bits would accidentally un-register other keys that share the
        // same bit position, breaking the "no false negatives" guarantee.)
        public void Add(string item)
        {
            foreach (int pos in GetPositions(item))
                _bits[pos] = true;
        }

        // The core query. Two outcomes:
        //
        //   false = "DEFINITELY NOT in the set" — at least one bit position is 0,
        //           meaning no key has ever set that bit. Callers can skip this SSTable.
        //
        //   true  = "PROBABLY in the set" — all bit positions are 1, but collisions
        //           mean it's not a certainty. Callers must still do the real lookup.
        //
        // The asymmetry (certain miss, uncertain hit) is the entire value of Bloom filters.
        public bool MightContain(string item) => GetPositions(item).All(pos => _bits[pos]);

        // Produces _hashCount different bit positions for a given key by running the
        // same string through _hashCount independent hash variants (one per seed).
        //
        // How the independence is achieved:
        //   - Each seed starts with a different large prime offset (Knuth multiplicative
        //     constant × seed), scrambling the starting state before ingesting any chars.
        //   - The polynomial rolling hash (×31 per char) then fans out from that
        //     different starting point, producing a different final position per seed.
        //
        // The Knuth constant 2654435761 (≈ 2^32 / φ, where φ is the golden ratio)
        // has excellent bit-spreading properties — multiplying by it pushes input bits
        // into many output bits, reducing clustering in the bit array.
        private IEnumerable<int> GetPositions(string item)
        {
            for (int seed = 0; seed < _hashCount; seed++)
            {
                // Start from a different offset for each seed to get k independent positions.
                int hash = seed * unchecked((int)2654435761u);
                foreach (char c in item) hash = hash * 31 + c;
                yield return Math.Abs(hash % _size);
            }
        }
    }
}
