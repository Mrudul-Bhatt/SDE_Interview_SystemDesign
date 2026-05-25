// SSTable — immutable sorted file (simulated in-memory here).
//
// Why immutable: once flushed from the MemTable, an SSTable is never modified.
// Updates and deletes produce new SSTables; a background compaction process
// merges them and removes obsolete entries. Immutability means no locks needed
// for concurrent reads, which is the key throughput win of LSM trees.
//
// Why Bloom filter per SSTable: a key lookup must probe every L0 SSTable in
// reverse chronological order until it finds the key. The Bloom filter skips
// SSTables that definitely don't contain the key, avoiding unnecessary scans.

namespace AdvancedDesigns
{
    public class SSTable
    {
        private readonly SortedDictionary<string, StorageEntry> _data;
        private readonly BloomFilter _bloomFilter;

        public int      Level     { get; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public SSTable(IEnumerable<KeyValuePair<string, StorageEntry>> entries, int level)
        {
            Level        = level;
            _data        = new SortedDictionary<string, StorageEntry>();
            _bloomFilter = new BloomFilter(size: 100000, hashCount: 7);

            foreach (var kv in entries)
            {
                _data[kv.Key] = kv.Value;
                _bloomFilter.Add(kv.Key);
            }
        }

        public bool TryGet(string key, out StorageEntry entry)
        {
            entry = null;
            if (!_bloomFilter.MightContain(key)) return false; // definite miss — skip disk scan
            return _data.TryGetValue(key, out entry);
        }

        public IEnumerable<KeyValuePair<string, StorageEntry>> GetAllEntries() => _data;
        public int Count => _data.Count;
    }
}
