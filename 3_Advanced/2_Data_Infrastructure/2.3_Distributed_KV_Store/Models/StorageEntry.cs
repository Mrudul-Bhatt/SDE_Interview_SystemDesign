// StorageEntry — value wrapper stored in the MemTable and SSTables.
//
// THE BIG IDEA:
// When you write "name = Alice" to the KV store, we don't just store "Alice".
// We wrap it in a StorageEntry that also records:
//   - WHEN it was written (Timestamp) — needed to resolve conflicts: if two
//     replicas disagree on the value of "name", the one with the higher timestamp wins.
//   - WHEN it expires (ExpiresAt) — cache-style TTL. Redis does the same thing.
//     We don't delete immediately on expiry; we just check IsExpired on read and
//     pretend the key doesn't exist. Lazy expiry is much cheaper than background
//     scanning for expired keys.
//   - WHETHER it's a deletion marker (IsTombstone) — see TombstoneEntry below.
//
// WHY IMMUTABLE (get-only properties, no setters)?
// MemTable entries are written once and never changed. If you "update" a key,
// you write a NEW StorageEntry with a higher timestamp — the old one stays
// underneath until compaction sweeps it away. Immutability makes it safe to
// share entries across threads without locks.
//
// WHY TOMBSTONE (not just delete the key)?
// SSTables on disk are IMMUTABLE files — you cannot punch a hole in the middle
// of a file to remove a key. So instead of removing the key, we write a special
// StorageEntry that says "this key is dead". When SSTables are later merged
// (compaction), the tombstone signals the merger to discard all older versions
// of the key. This is the same pattern Cassandra, LevelDB, and RocksDB all use.

using System;

namespace AdvancedDesigns
{
    public class StorageEntry
    {
        // The actual stored value. Null only in TombstoneEntry (deletion marker).
        public string Value { get; }

        // Logical write time — used for conflict resolution (last-writer-wins).
        // In production this would be a hybrid logical clock (HLC) that combines
        // wall-clock time with a sequence counter to handle same-millisecond writes.
        public long Timestamp { get; }

        // Absolute expiry wall-clock time, derived from ttlSeconds at write time.
        // Null means "live forever". Checked lazily on read via IsExpired.
        public DateTime? ExpiresAt { get; }

        // True only for TombstoneEntry. Stored in the base class so that callers
        // reading a generic StorageEntry can check deletion without a type cast.
        public bool IsTombstone { get; }

        public StorageEntry(string value, long timestamp, int? ttlSeconds = null)
        {
            Value = value;
            Timestamp = timestamp;

            // Convert relative TTL (seconds from now) into an absolute wall-clock
            // instant. We store the deadline, not the duration, so IsExpired never
            // needs to know when the entry was created.
            ExpiresAt = ttlSeconds.HasValue
                ? DateTime.UtcNow.AddSeconds(ttlSeconds.Value)
                : null;

            IsTombstone = false;
        }

        // Lazy expiry check — called on every read. If true, callers treat the key
        // as missing. No background thread needed; expired entries are simply
        // invisible until the next compaction cleans them up.
        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    }

    // TombstoneEntry — the deletion record written when a key is deleted.
    //
    // WHY A SUBTYPE instead of just setting IsTombstone = true?
    // Two reasons:
    //   1. INTENT at the call site: MemTable.Delete() constructs a TombstoneEntry,
    //      which makes it impossible to accidentally create a deletion marker by
    //      passing value=null to the regular constructor.
    //   2. PATTERN MATCHING: readers can write `entry is TombstoneEntry` instead of
    //      `entry.IsTombstone`, which is more expressive and harder to forget.
    //
    // The tombstone has NO value (null) — it's a marker, not data. During compaction
    // when SSTables are merged, any key whose newest version is a TombstoneEntry
    // gets dropped entirely from the output SSTable, reclaiming the disk space.
    public class TombstoneEntry : StorageEntry
    {
        // Pass null as the value and the timestamp to the base class.
        // No TTL — tombstones live until compaction removes them.
        public TombstoneEntry(long timestamp) : base(null, timestamp) { }

        // Shadow the base-class property to always return true, regardless of
        // what the base constructor set. `new` (not `override`) because the base
        // property has no virtual keyword — this is intentional: the base class
        // can't be overridden, which means a StorageEntry is NEVER a tombstone
        // unless you explicitly constructed a TombstoneEntry.
        public new bool IsTombstone => true;
    }
}
