// UrlRepository
// Simulates a PostgreSQL table with a unique constraint on short_code.
// In production: INSERT ... ON CONFLICT DO NOTHING, SELECT by short_code.
// StringComparer.Ordinal is used because short codes are case-sensitive
// ("abc" and "ABC" are different codes) and Ordinal is faster than the default
// culture-aware comparer for ASCII strings.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class UrlRepository
    {
        private readonly Dictionary<string, UrlRecord> _store = new(StringComparer.Ordinal);

        // Returns false if the short code already exists — mirrors a DB unique constraint
        // violation (duplicate key). The caller handles the conflict rather than overwriting.
        public bool TryInsert(UrlRecord record)
        {
            if (_store.ContainsKey(record.ShortCode)) return false;
            _store[record.ShortCode] = record;
            return true;
        }

        public UrlRecord Find(string shortCode)
            => _store.TryGetValue(shortCode, out var r) ? r : null;

        // Soft delete: marks IsActive=false instead of removing the row.
        // Preserves analytics history and returns 410 (Gone) instead of 404 (Not Found).
        public bool Deactivate(string shortCode)
        {
            if (_store.TryGetValue(shortCode, out var r))
            {
                r.IsActive = false;
                return true;
            }
            return false;
        }

        public int Count => _store.Count;
    }
}
