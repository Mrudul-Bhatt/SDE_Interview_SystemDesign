// IdempotencyEntry — the cached result of a previously-seen request.
//
// Keyed by (MerchantId, Key) so two different merchants can use the same
// human-readable key ("order-1001") without collisions. ExpiresAt enforces the
// 24-hour TTL convention used by Stripe et al. — after that window, the same
// key is treated as a new request.

using System;

public class IdempotencyEntry
{
    public string Key        { get; set; }
    public string MerchantId { get; set; }
    public string Result     { get; set; }  // serialized response
    public string PaymentId  { get; set; }
    public DateTime ExpiresAt { get; set; }
}
