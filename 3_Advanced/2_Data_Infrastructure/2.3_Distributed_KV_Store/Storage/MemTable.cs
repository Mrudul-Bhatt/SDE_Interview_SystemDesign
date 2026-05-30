// MemTable — sorted in-memory write buffer (first tier of the LSM tree).
//
// Why sorted (SortedDictionary): when flushed to an SSTable, entries must be
// written in key order so the SSTable supports binary search / range scans.
// Sorting at flush time on an unsorted structure would be O(n log n) extra work.
//
// Why a size threshold: keeping unbounded data in memory risks OOM.
// The threshold triggers a flush to an immutable SSTable on disk (here simulated).
// Real systems use 64 MB; we use 1 KB so the demo shows a flush quickly.

namespace AdvancedDesigns
{
    public class MemTable
    {
        private readonly SortedDictionary<string, StorageEntry> _data = new();
        private int _sizeBytes;
        private readonly int _flushThresholdBytes;

        public bool ShouldFlush => _sizeBytes >= _flushThresholdBytes;
        public int  Count       => _data.Count;

        public MemTable(int flushThresholdBytes = 64 * 1024 * 1024)
        {
            _flushThresholdBytes = flushThresholdBytes;
        }

        public void Put(string key, string value, long timestamp, int? ttlSeconds = null)
        {
            _data[key]   = new StorageEntry(value, timestamp, ttlSeconds);
            // +32 approximates the per-entry overhead: object header, dictionary node
            // pointers, and StorageEntry fields — keeps the flush threshold realistic.
            _sizeBytes  += key.Length + (value?.Length ?? 0) + 32;
        }

        public void Delete(string key, long timestamp)
        {
            // TombstoneEntry marks the key as deleted so subsequent reads
            // stop searching older SSTables rather than returning a stale value.
            _data[key]  = new TombstoneEntry(timestamp);
            _sizeBytes += key.Length + 32;
        }

        public bool TryGet(string key, out StorageEntry entry)
            => _data.TryGetValue(key, out entry);

        // Returns entries already in sorted key order — ready for SSTable flush.
        public IEnumerable<KeyValuePair<string, StorageEntry>> GetSortedEntries()
            => _data;

        public void Clear()
        {
            _data.Clear();
            _sizeBytes = 0;
        }
    }
}
