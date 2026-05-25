// ClickAnalytics
// Tracks click counts per short code, broken down by country.
// Simulates Redis counters (total clicks) + ClickHouse aggregation (by country).
// In production, Record() would publish to a Kafka topic and be consumed
// asynchronously — keeping it off the redirect hot path.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class ClickAnalytics
    {
        private readonly Dictionary<string, long> _total = new();

        // Two-level dictionary: shortCode → country → count.
        // Avoids a composite key string (e.g. "abc123:US") — cleaner to query by code first.
        private readonly Dictionary<string, Dictionary<string, long>> _byCountry = new();

        // Lock because multiple redirect threads call Record() concurrently.
        // In production you'd use Redis INCR (atomic) instead of in-process locking.
        private readonly object _lock = new();

        public void Record(string shortCode, string country = "US")
        {
            lock (_lock)
            {
                // Increment total — TryGetValue avoids KeyNotFoundException on first click.
                _total[shortCode] = _total.TryGetValue(shortCode, out long t) ? t + 1 : 1;

                // Ensure the inner country-map exists before incrementing.
                if (!_byCountry.TryGetValue(shortCode, out var map))
                    _byCountry[shortCode] = map = new Dictionary<string, long>();

                map[country] = map.TryGetValue(country, out long c) ? c + 1 : 1;
            }
        }

        public long TotalClicks(string shortCode)
            => _total.TryGetValue(shortCode, out long v) ? v : 0;

        public IReadOnlyDictionary<string, long> ByCountry(string shortCode)
            => _byCountry.TryGetValue(shortCode, out var map)
               ? map
               : new Dictionary<string, long>();
    }
}
