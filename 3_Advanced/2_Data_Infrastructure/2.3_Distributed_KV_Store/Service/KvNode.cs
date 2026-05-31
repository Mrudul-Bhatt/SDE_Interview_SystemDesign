// KvNode — a single storage node that owns a slice of the key space.
//
// THE BIG IDEA:
// This is the whole LSM tree in one class. Every write lands in RAM first (MemTable),
// then spills to disk (SSTable) when the buffer fills up. Reads check RAM before disk.
// The design trades read complexity (check multiple layers) for write speed (always RAM-first).
//
// THE WRITE PATH — always fast, always RAM:
//   PUT "name=Alice"
//     → increment logical clock (ts=42)
//     → write to MemTable (in-memory, microseconds)
//     → if MemTable ≥ 1 KB → flush to a new L0 SSTable, clear MemTable
//
// THE READ PATH — newest layer first:
//   GET "name"
//     → check MemTable        (most recent writes live here)
//     → check L0 SSTables, newest → oldest  (each backed by Bloom filter)
//     → not found anywhere → return (false, null)
//
//   Why newest-first? If "name" was written then overwritten, BOTH the old and
//   new versions exist in different SSTables. By checking newest first, the first
//   hit we find is always the correct current value — no merge needed.
//
// LOGICAL CLOCK (not wall clock):
// We use a counter (_logicalClock) rather than DateTime.UtcNow for timestamps.
// Wall clocks on different machines drift — two servers might assign the same
// millisecond to writes that actually happened at different times, making
// "last writer wins" ambiguous. A logical clock is strictly monotone: every
// write on THIS node gets a unique, ever-increasing timestamp. When nodes
// coordinate (replication), they exchange and merge their clocks (vector clocks
// or Hybrid Logical Clocks in production). Here, single-node simplicity suffices.
//
// CONCURRENCY — one lock, coarse-grained:
// A single `_lock` guards the MemTable, the SSTable list, and the clock together.
// This makes Put-then-flush atomic: no reader can see a half-flushed state where
// the MemTable is cleared but the SSTable isn't yet in _l0SSTables. Production
// systems use more fine-grained synchronisation, but the coarse lock is
// correct and simple for a demo.

using System.Collections.Generic;
using System.Threading;

namespace AdvancedDesigns
{
    public class KvNode
    {
        // Identifies this node on the consistent-hash ring (e.g. "node-A", "node-B").
        // The ring uses this ID to assign key ownership.
        public string NodeId { get; }

        // In-memory write buffer. Threshold set to 1 KB for the demo so a flush
        // happens after just a few writes — in production this would be 64 MB.
        private readonly MemTable _memTable = new(flushThresholdBytes: 1024);

        // All L0 SSTables flushed from MemTable, in creation order (oldest first).
        // We search newest→oldest (reverse order) so the first hit is always the
        // latest version of a key.
        private readonly List<SSTable> _l0SSTables = [];

        // Monotonically increasing counter. Incremented once per write operation.
        // Guarantees every entry in this node has a unique timestamp, so "higher
        // timestamp wins" is always unambiguous.
        private long _logicalClock;

        // Single coarse lock. Makes Put-then-FlushMemTable atomic so readers never
        // observe an intermediate state (MemTable cleared but SSTable not yet added).
        private readonly object _lock = new();

        public KvNode(string nodeId) => NodeId = nodeId;

        public void Put(string key, string value, int? ttlSeconds = null)
        {
            lock (_lock)
            {
                // Increment clock first, then write — ensures this entry's timestamp
                // is strictly greater than every entry written before this call.
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Put(key, value, ts, ttlSeconds);

                // Auto-flush: if the scratch pad is full, freeze it into an SSTable
                // and give writes a fresh empty MemTable.
                if (_memTable.ShouldFlush) FlushMemTable();
            }
        }

        public void Delete(string key)
        {
            lock (_lock)
            {
                // A delete is just a Put with no value — a TombstoneEntry with a
                // timestamp higher than any previous version of this key. Readers
                // that find the tombstone first know to stop searching older layers.
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Delete(key, ts);
            }
        }

        public (bool found, string value, long timestamp) Get(string key)
        {
            lock (_lock)
            {
                // Layer 1: MemTable — always the freshest data.
                if (_memTable.TryGet(key, out StorageEntry entry))
                {
                    // A tombstone here means the key was deleted after the last flush.
                    // Return "not found" but carry the timestamp so the caller can
                    // compare against replica versions (replica might be even newer).
                    if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                    if (entry.IsExpired) return (false, null, 0);
                    return (true, entry.Value, entry.Timestamp);
                }

                // Layer 2: L0 SSTables, newest → oldest.
                // We iterate in reverse because _l0SSTables is append-only (newest last).
                // The first SSTable that returns a hit wins — that hit IS the current
                // version of the key. We never need to look deeper.
                for (int i = _l0SSTables.Count - 1; i >= 0; i--)
                {
                    if (_l0SSTables[i].TryGet(key, out entry))
                    {
                        if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                        if (entry.IsExpired) return (false, null, 0);
                        return (true, entry.Value, entry.Timestamp);
                    }
                    // TryGet returns false if the SSTable's Bloom filter says "definitely
                    // not here" — no scan performed, just move to the next SSTable.
                }

                return (false, null, 0);  // key genuinely does not exist on this node
            }
        }

        // Manually trigger a flush — used in tests to inspect SSTable state without
        // waiting for the size threshold to fire organically.
        public void ForceFlush()
        {
            lock (_lock) FlushMemTable();
        }

        // Snapshot of current node health: how many keys are in the live MemTable,
        // and how many L0 SSTables have accumulated. A rising SSTable count signals
        // that compaction should run (not implemented in this demo).
        public (int memTableCount, int l0SSTables) GetStats()
        {
            lock (_lock) return (_memTable.Count, _l0SSTables.Count);
        }

        // Seal the current MemTable into an immutable L0 SSTable, then wipe the
        // MemTable clean. Called inside the lock so no write can slip in between
        // "snapshot entries" and "clear", which would silently lose a write.
        private void FlushMemTable()
        {
            _l0SSTables.Add(new SSTable(_memTable.GetSortedEntries(), level: 0));
            _memTable.Clear();
        }
    }
}
