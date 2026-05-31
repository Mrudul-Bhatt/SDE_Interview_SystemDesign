// MemTable — sorted in-memory write buffer (the first tier of the LSM tree).
//
// THE BIG IDEA:
// Think of the MemTable as a scratch pad on your desk. Every write goes here
// first — it's fast because RAM is fast. Once the scratch pad is full, you
// photocopy it into a permanent notebook (SSTable on disk) and wipe the pad
// clean for the next batch.
//
// The three-tier journey of a write:
//   1. PUT "name=Alice" → lands in MemTable (this file). Done in microseconds.
//   2. MemTable fills up → flushed to an SSTable file on disk. MemTable cleared.
//   3. Many SSTables accumulate → compaction merges them, removing old versions
//      and tombstones. (See SsTableStore.cs for steps 2-3.)
//
// WHY SORTED (SortedDictionary, not plain Dictionary)?
// When we flush to disk, the SSTable needs entries in alphabetical key order so
// readers can binary-search for a key in O(log N) instead of scanning the whole
// file. If we used an unsorted structure, we'd pay O(N log N) to sort at flush
// time on every flush. By keeping the MemTable sorted at all times, the flush
// is just a sequential write — the cheapest possible disk I/O.
//
// WHY A SIZE THRESHOLD (not time-based flush)?
// We flush when the in-memory buffer exceeds a byte limit, not on a timer.
// This keeps memory usage bounded regardless of write rate. Real systems use
// 64 MB (Cassandra default); the demo uses 1 KB so a flush happens quickly
// during the demo scenarios.
//
// READ PATH:
// Every read checks the MemTable first. If the key is found here, we return
// immediately — no disk I/O. If not found, we fall through to SSTables.
// This "newest data lives in RAM" property is what makes LSM reads competitive
// with B-tree indexes for write-heavy workloads.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class MemTable
    {
        // SortedDictionary keeps keys in ascending order at all times.
        // Cost: O(log N) per insert (vs O(1) for plain Dictionary).
        // Benefit: GetSortedEntries() is O(N) and already ordered — flush is free.
        private readonly SortedDictionary<string, StorageEntry> _data = [];

        // Running byte count — incremented on every Put/Delete so we don't have
        // to iterate the whole dictionary to decide if it's time to flush.
        private int _sizeBytes;
        private readonly int _flushThresholdBytes;

        // True when the MemTable has accumulated enough data to justify flushing
        // to disk. The KvStore checks this after every write and triggers a flush.
        public bool ShouldFlush => _sizeBytes >= _flushThresholdBytes;

        // Exposed for diagnostics / tests — how many keys (including tombstones)
        // currently live in the MemTable.
        public int Count => _data.Count;

        public MemTable(int flushThresholdBytes = 64 * 1024 * 1024)
        {
            _flushThresholdBytes = flushThresholdBytes;
        }

        // Write a value into the MemTable. If the key already exists, the new
        // StorageEntry simply overwrites the old one — later timestamp wins.
        // The old entry becomes unreachable and will be cleaned up by GC;
        // the permanent cleanup happens during SSTable compaction on disk.
        public void Put(string key, string value, long timestamp, int? ttlSeconds = null)
        {
            _data[key] = new StorageEntry(value, timestamp, ttlSeconds);

            // +32 approximates per-entry overhead: object header, two dictionary
            // node pointers (left child, right child in the red-black tree), and
            // StorageEntry field storage. This is intentionally rough — we just
            // need "close enough" to trigger flushes at roughly the right size.
            _sizeBytes += key.Length + (value?.Length ?? 0) + 32;
        }

        // Mark a key as deleted by writing a TombstoneEntry (not by removing the
        // dictionary entry). This is essential: if we simply called _data.Remove(key),
        // the deletion would be invisible to SSTables already flushed to disk that
        // still hold the old value. The tombstone propagates through compaction and
        // eventually causes all older versions of the key to be discarded.
        public void Delete(string key, long timestamp)
        {
            _data[key] = new TombstoneEntry(timestamp);
            _sizeBytes += key.Length + 32;  // no value bytes — tombstone has no payload
        }

        // Fast O(log N) lookup — returns the entry regardless of whether it's a
        // live value or a tombstone. The caller (KvStore.Get) is responsible for
        // checking entry.IsTombstone and entry.IsExpired before returning to the user.
        public bool TryGet(string key, out StorageEntry entry) => _data.TryGetValue(key, out entry);

        // Iterates in ascending key order — the exact order an SSTable file needs.
        // Because _data is a SortedDictionary, this is a simple in-order traversal
        // with no extra sorting step.
        public IEnumerable<KeyValuePair<string, StorageEntry>> GetSortedEntries() => _data;

        // Called immediately after the MemTable is flushed to disk. Resetting
        // _sizeBytes to zero is critical: without it, ShouldFlush would fire again
        // on the very next write, causing an infinite flush loop.
        public void Clear()
        {
            _data.Clear();
            _sizeBytes = 0;
        }
    }
}
