// IdempotencyEntry — the cached outcome of a request we've already processed.
//
// THE BIG IDEA:
// Networks retry. A client that times out will re-send the same charge — but the customer must
// be charged ONCE. The client attaches an IdempotencyKey; the first request stores its result
// here, and any later request with the same key returns this cached result instead of charging
// again. This is what turns an unreliable "send" into a safe "send-exactly-once".
//
// WHY KEYED BY (MerchantId, Key): the key is merchant-chosen and human-readable ("order-1001"),
// so two merchants can both use "order-1001" without colliding. The merchant id scopes it.
//
// WHY ExpiresAt (24h TTL): the same key is only "the same request" for a bounded window. After
// 24h (Stripe's convention) the key is free to mean a genuinely new charge.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 2: the same charge sent twice):
//
//   Operation                       | Store / Result
//   --------------------------------|------------------------------------------------
//   1st Charge(order-1002)          | stores { merchant1:order-1002 -> pay_X }; new charge
//   2nd Charge(order-1002) [same]   | TryGet hits -> returns pay_X, WasIdempotent=true
//   => same PaymentId both times    | customer charged once, not twice

using System;

public class IdempotencyEntry
{
    public string Key { get; set; }   // merchant-chosen, e.g. "order-1002"
    public string MerchantId { get; set; }   // scopes Key so merchants can't collide
    public string Result { get; set; }   // serialized response status ("AUTHORIZED", "BLOCKED"...)
    public string PaymentId { get; set; }   // the payment this key produced — returned on a repeat
    public DateTime ExpiresAt { get; set; }  // 24h TTL; after this the key is reusable
}
