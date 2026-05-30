// KvNode — single storage node with a full LSM write path.
//
// Read path (newest-first): MemTable → L0 SSTables (newest → oldest).
// This order guarantees the most recent write wins without merging every layer.
//
// Logical clock: every Put and Delete increments _logicalClock to get a strictly
// monotonically increasing timestamp. The outer lock makes the full
// Put-then-maybe-flush sequence atomic; Interlocked.Increment is used as a
// clear signal that the clock increment is intentionally thread-safe.

namespace AdvancedDesigns
{
    public class KvNode
    {
        public string NodeId { get; }

        private MemTable       _memTable   = new(flushThresholdBytes: 1024); // small for demo
        private readonly List<SSTable> _l0SSTables = new();
        private long           _logicalClock;
        private readonly object _lock = new();

        public KvNode(string nodeId) => NodeId = nodeId;

        public void Put(string key, string value, int? ttlSeconds = null)
        {
            lock (_lock)
            {
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Put(key, value, ts, ttlSeconds);
                if (_memTable.ShouldFlush) FlushMemTable();
            }
        }

        public void Delete(string key)
        {
            lock (_lock)
            {
                long ts = Interlocked.Increment(ref _logicalClock);
                _memTable.Delete(key, ts);
            }
        }

        public (bool found, string value, long timestamp) Get(string key)
        {
            lock (_lock)
            {
                // MemTable is checked first — it holds the freshest writes.
                if (_memTable.TryGet(key, out StorageEntry entry))
                {
                    if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                    if (entry.IsExpired) return (false, null, 0);
                    return (true, entry.Value, entry.Timestamp);
                }

                // Walk L0 SSTables newest→oldest; first hit wins.
                for (int i = _l0SSTables.Count - 1; i >= 0; i--)
                {
                    if (_l0SSTables[i].TryGet(key, out entry))
                    {
                        if (entry is TombstoneEntry || entry.IsTombstone) return (false, null, entry.Timestamp);
                        if (entry.IsExpired) return (false, null, 0);
                        return (true, entry.Value, entry.Timestamp);
                    }
                }

                return (false, null, 0);
            }
        }

        public void ForceFlush()
        {
            lock (_lock) FlushMemTable();
        }

        public (int memTableCount, int l0SSTables) GetStats()
        {
            lock (_lock) return (_memTable.Count, _l0SSTables.Count);
        }

        // Converts the current MemTable into an immutable L0 SSTable and resets
        // the MemTable for new writes. Called automatically when ShouldFlush is true,
        // or explicitly via ForceFlush() in tests.
        private void FlushMemTable()
        {
            _l0SSTables.Add(new SSTable(_memTable.GetSortedEntries(), level: 0));
            _memTable.Clear();
        }
    }
}
