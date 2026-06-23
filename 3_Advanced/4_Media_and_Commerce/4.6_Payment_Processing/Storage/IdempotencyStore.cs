// IdempotencyStore — remembers the result of each (merchantId, key) so retries don't double-charge.
//
// THE BIG IDEA:
// This is the store behind the idempotency guarantee. The first Charge with a given key writes
// its outcome here; any later Charge with the same key reads it back via TryGet instead of
// charging again. (See IdempotencyEntry for the why.) Charge calls TryGet FIRST, before any side
// effect — that ordering is what makes "send-exactly-once" work.
//
// WHY THE COMPOSITE "merchantId:key": the key is merchant-chosen ("order-1002"), so scoping it by
// merchant stops one merchant's keys from colliding with another's.
//
// WHY THE 24h TTL: TryGet treats an expired entry as absent, so the same key reused the next day
// is correctly handled as a brand-new charge.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 2: charge order-1002 twice):
//
//   Call                              | _store / return
//   ----------------------------------|------------------------------------------
//   TryGet(merchant1, order-1002)     | null  (nothing stored yet -> proceed to charge)
//   Store(merchant1, order-1002, ...) | { "merchant1:order-1002" -> entry(pay_X, +24h) }
//   TryGet(merchant1, order-1002)     | the entry -> Charge returns pay_X, no second charge

using System;
using System.Collections.Generic;

public class IdempotencyStore
{
    // "merchantId:key" -> cached result. Production: Redis (hot path) + Postgres fallback.
    private readonly Dictionary<string, IdempotencyEntry> _store = [];
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Returns the cached entry only if present AND not past its TTL; otherwise null (= new request).
    public IdempotencyEntry TryGet(string merchantId, string key)
    {
        var compositeKey = $"{merchantId}:{key}";
        if (_store.TryGetValue(compositeKey, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry;
        return null;
    }

    // Records the outcome of a charge under its key, stamped with a 24h expiry.
    public void Store(string merchantId, string key, string paymentId, string result)
    {
        _store[$"{merchantId}:{key}"] = new IdempotencyEntry
        {
            Key = key,
            MerchantId = merchantId,
            PaymentId = paymentId,
            Result = result,
            ExpiresAt = DateTime.UtcNow.Add(Ttl)
        };
    }
}
