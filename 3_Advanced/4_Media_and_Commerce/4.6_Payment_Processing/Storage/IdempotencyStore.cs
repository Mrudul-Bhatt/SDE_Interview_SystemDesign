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

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT _store HOLDS AT RUNTIME (snapshot after all of Program.cs has run):
    //
    //   _store ("merchantId:key" -> IdempotencyEntry) = {
    //
    //      // Scen 1: authorized charge
    //
    //      "merchant1:order-1001"  ->  {
    //                                   Key="order-1001",
    //                                   PaymentId="pay_1a2b3c",
    //                                   Result="AUTHORIZED",
    //                                   ExpiresAt=now+24h
    //                                 }
    //
    //
    //      // Scen 2: charged once; the identical 2nd request READ this entry instead of charging
    //
    //      "merchant1:order-1002"  ->  {
    //                                   Key="order-1002",
    //                                   PaymentId="pay_4d5e6f",
    //                                   Result="AUTHORIZED",
    //                                   ExpiresAt=now+24h
    //                                 }
    //
    //
    //      // Scen 3: even a blocked charge is cached — a retry returns BLOCKED, never re-runs fraud
    //
    //      "merchant1:order-1003"  ->  {
    //                                   Key="order-1003",
    //                                   PaymentId="pay_7g8h9i",
    //                                   Result="BLOCKED",
    //                                   ExpiresAt=now+24h
    //                                 }
    //
    //
    //      // Scen 4: failed charges are cached too — a retry returns FAILED without re-hitting the bank
    //
    //      "merchant1:order-1004"  ->  {
    //                                   Key="order-1004",
    //                                   PaymentId="pay_0j1k2l",
    //                                   Result="FAILED",
    //                                   ExpiresAt=now+24h
    //                                 }
    //
    //
    //      // Scen 5: authorized charge (later captured + refunded, but the cached result is the charge)
    //
    //      "merchant1:order-1005"  ->  {
    //                                   Key="order-1005",
    //                                   PaymentId="pay_3m4n5o",
    //                                   Result="AUTHORIZED",
    //                                   ExpiresAt=now+24h
    //                                 }
    //   }
    //
    // Every charge — success, block, OR failure — leaves exactly one entry here, keyed by merchant
    // + idempotency key. That's what makes a retry safe: it returns this stored outcome instead of
    // charging again. Entries self-expire after 24h, after which the same key counts as a new request.
    // ──────────────────────────────────────────────────────────────────────────────────
}
