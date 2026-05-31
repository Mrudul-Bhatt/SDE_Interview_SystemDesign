// SSTable (Sorted String Table) — immutable sorted file, the second tier of the LSM tree.
//
// THE BIG IDEA:
// When the MemTable (in-memory scratch pad) fills up, we "photocopy" it into an
// SSTable and clear the pad. The SSTable is like a printed book: once published,
// it never changes. You can read it from many threads simultaneously without any
// locking, because nobody is ever writing to it.
//
// Here we simulate the file with a SortedDictionary in memory. In a real system
// (LevelDB, RocksDB, Cassandra) this would be a file on disk with:
//   - A data block: sorted key-value pairs, compressed
//   - An index block: sparse index so you can binary-search to the right data block
//   - A Bloom filter block: the same probabilistic filter we keep in _bloomFilter
//   - A footer: offsets to each block
//
// WHY IMMUTABLE?
// "Never modify" is the entire reason LSM trees are fast for writes:
//   - No random disk writes — every flush is a sequential append to a new file.
//     Sequential writes are 10-100× faster than random writes on spinning disks,
//     and they also avoid SSD write amplification.
//   - No locks needed for concurrent reads — readers never race with writers.
//   - Crash-safe by construction — a partially written SSTable is simply discarded
//     on recovery; the MemTable WAL (write-ahead log) replays the missing writes.
//
// The downside of immutability: if you update the same key many times, multiple
// SSTables each hold one version of it. That's why compaction exists — it merges
// overlapping SSTables, keeping only the newest version of each key and discarding
// tombstones whose older versions have already been swept away.
//
// WHY A BLOOM FILTER PER SSTABLE?
// Without filters, a key lookup must probe EVERY SSTable in reverse-creation order
// until it finds the key (or runs out of SSTables). With dozens of SSTables, that's
// dozens of disk reads for a single GET — terrible latency. The Bloom filter answers
// "does this SSTable DEFINITELY NOT contain the key?" in O(1) with zero disk I/O.
// It can have false positives (say "maybe" when the key isn't there), but never
// false negatives (it never says "no" when the key IS there). So a "no" from the
// Bloom filter means skip this SSTable entirely — huge read win.
//
// LEVELS:
// SSTables are organised into levels (L0, L1, L2, ...).
//   - L0: freshly flushed from MemTable. Can have overlapping key ranges between files.
//   - L1+: compaction merges and sorts files so each level's files are non-overlapping.
// A key lookup checks all L0 SSTables (possible overlap) but only ONE L1 file, ONE L2
// file, etc. — because the key can only be in one range-sorted file per level >= 1.
// Fewer probes = faster reads as the store grows.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class SSTable
    {
        // The actual data — sorted key → StorageEntry. In a real implementation this
        // lives on disk and is accessed via a sparse in-memory index + binary search.
        private readonly SortedDictionary<string, StorageEntry> _data;

        // One Bloom filter per SSTable. Built once at creation time, never updated
        // (immutability again). Size=100000 bits with 7 hash functions gives roughly
        // a 1% false-positive rate at 10k keys — good enough to skip ~99% of
        // "definitely missing" SSTable probes with near-zero memory cost.
        private readonly BloomFilter _bloomFilter;

        // Which compaction level this SSTable lives at. Level 0 = freshly flushed,
        // higher levels = older, more compacted. Used by SsTableStore to decide
        // search order (newest first) and compaction priority.
        public int Level { get; }

        // Wall-clock creation time — used as a tiebreaker when two L0 SSTables both
        // contain the same key. The one created later (higher CreatedAt) wins.
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        // Constructor is the only write path. Takes the already-sorted stream from
        // MemTable.GetSortedEntries() and materialises it into _data + Bloom filter.
        // After this returns, the SSTable is sealed — no further modifications allowed.
        public SSTable(IEnumerable<KeyValuePair<string, StorageEntry>> entries, int level)
        {
            Level = level;
            _data = [];
            // 100 000-bit filter with 7 hash functions: optimal for up to ~10k keys.
            // (Optimal hash count = (bits/keys) × ln2 ≈ 10 × 0.693 ≈ 7.)
            _bloomFilter = new BloomFilter(size: 100000, hashCount: 7);

            foreach (var kv in entries)
            {
                _data[kv.Key] = kv.Value;
                _bloomFilter.Add(kv.Key);  // register every key so lookups can skip safely
            }
        }

        // Two-step read:
        //   Step 1 — Bloom filter: "does this SSTable POSSIBLY have the key?"
        //     If NO  → return false immediately. Zero simulated disk I/O.
        //     If YES → proceed (might be a false positive, but we can't skip).
        //   Step 2 — Actual dictionary lookup (simulates binary search in a real file).
        //
        // Callers (SsTableStore) still need to check the returned entry for
        // IsTombstone and IsExpired — this method returns whatever was written,
        // including deletion markers.
        public bool TryGet(string key, out StorageEntry entry)
        {
            entry = null;
            if (!_bloomFilter.MightContain(key)) return false;  // definite miss — skip
            return _data.TryGetValue(key, out entry);
        }

        // Used by compaction: streams all entries in sorted order to the merger.
        // Because _data is a SortedDictionary, this is already in key order —
        // the compaction merge-sort just interleaves multiple sorted streams.
        public IEnumerable<KeyValuePair<string, StorageEntry>> GetAllEntries() => _data;

        // Number of entries including tombstones — useful for estimating SSTable
        // size and deciding when a level has too many files and needs compaction.
        public int Count => _data.Count;
    }
}
