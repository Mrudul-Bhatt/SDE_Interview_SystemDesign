// UrlRecord — the row stored in the database (simulates a PostgreSQL row).
// One record per short code; custom aliases have Id = 0 (no numeric ID).

using System;

namespace AdvancedDesigns
{
    public class UrlRecord
    {
        public long Id { get; set; }
        public string ShortCode { get; set; }
        public string LongUrl { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }

        // Nullable: null means the link never expires (permanent).
        // TTL links set this to CreatedAt + ttlDays.
        public DateTime? ExpiresAt { get; set; }

        // Soft-delete flag: deactivated links return 410 instead of being removed
        // from the DB so analytics history is preserved.
        public bool IsActive { get; set; } = true;

        // Distinguishes human-readable custom aliases ("annual-report") from
        // auto-generated Base62 codes ("0001abc") for validation and logging.
        public bool IsCustom { get; set; }

        // Computed: checks expiry at read time rather than storing a stale boolean.
        // UtcNow avoids DST edge cases that DateTime.Now can produce.
        public bool IsExpired =>
            ExpiresAt.HasValue && DateTime.UtcNow >= ExpiresAt.Value;
    }
}
