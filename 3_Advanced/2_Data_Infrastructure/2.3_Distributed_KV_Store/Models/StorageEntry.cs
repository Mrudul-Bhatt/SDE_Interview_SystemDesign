// StorageEntry — value wrapper that carries timestamp, TTL, and tombstone flag.
// TombstoneEntry — subclass used by MemTable.Delete() so the type system
// distinguishes a deletion record from a normal null-value entry.

namespace AdvancedDesigns
{
    public class StorageEntry
    {
        public string    Value      { get; }
        public long      Timestamp  { get; }
        public DateTime? ExpiresAt  { get; }
        public bool      IsTombstone { get; }

        public StorageEntry(string value, long timestamp, int? ttlSeconds = null)
        {
            Value      = value;
            Timestamp  = timestamp;
            ExpiresAt  = ttlSeconds.HasValue
                ? DateTime.UtcNow.AddSeconds(ttlSeconds.Value)
                : (DateTime?)null;
            IsTombstone = false;
        }

        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    }

    // Explicit subclass so MemTable.Delete() writes a typed deletion marker.
    // Using a subtype (rather than a boolean flag) means callers can pattern-match
    // with `is TombstoneEntry` instead of checking IsTombstone every time.
    public class TombstoneEntry : StorageEntry
    {
        public TombstoneEntry(long timestamp) : base(null, timestamp) { }
        public new bool IsTombstone => true;
    }
}
