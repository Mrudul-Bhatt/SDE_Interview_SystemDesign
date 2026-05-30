// IdempotencyStore — the cache of (merchantId, key) → result.
//
// In production this is Redis (fast read path) with a Postgres fallback (for
// keys that age out of Redis but might still be retried). 24-hour TTL is the
// industry convention — long enough for any reasonable retry window, short
// enough that legitimate same-key reuse the next day is treated as new.
//
// The composite key prevents one merchant from accidentally (or maliciously)
// colliding with another merchant's keys.

using System;
using System.Collections.Generic;

public class IdempotencyStore
{
    private readonly Dictionary<string, IdempotencyEntry> _store = new Dictionary<string, IdempotencyEntry>();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Returns existing entry if found and not expired
    public IdempotencyEntry TryGet(string merchantId, string key)
    {
        var compositeKey = $"{merchantId}:{key}";
        if (_store.TryGetValue(compositeKey, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry;
        return null;
    }

    public void Store(string merchantId, string key, string paymentId, string result)
    {
        _store[$"{merchantId}:{key}"] = new IdempotencyEntry
        {
            Key        = key,
            MerchantId = merchantId,
            PaymentId  = paymentId,
            Result     = result,
            ExpiresAt  = DateTime.UtcNow.Add(Ttl)
        };
    }
}
