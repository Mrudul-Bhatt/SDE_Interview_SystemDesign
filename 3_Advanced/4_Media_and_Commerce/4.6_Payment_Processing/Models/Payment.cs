// Payment — the durable record of one charge, and its source of truth for money owed.
//
// THE BIG IDEA:
// One Payment row tracks a charge through its whole life: authorized -> captured -> settled,
// and possibly refunded. The three money fields (AmountCents, CapturedCents, RefundedCents)
// are kept separate so partial captures and partial refunds compose without losing history.
//
// WHY Version (optimistic lock): two concurrent captures must not both succeed and
// over-capture the authorization. Every state change bumps Version, and PaymentStore.Update
// rejects a write whose expected Version doesn't match — the loser retries against fresh state.
//
// WHY RemainingRefundable: the refund flow checks this BEFORE calling the card network, so we
// can never refund more than was captured. It's the invariant that keeps balances non-negative.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 5: $150 charge, captured, then two $50 refunds):
//
//   After              | Status            | Captured | Refunded | Refundable | Version
//   -------------------|-------------------|----------|----------|------------|--------
//   Charge (authorize) | Authorized        |        0 |        0 |          0 |       1
//   Capture            | Captured          |    15000 |        0 |      15000 |       2
//   Refund $50         | PartiallyRefunded |    15000 |     5000 |      10000 |       3
//   Refund $50         | PartiallyRefunded |    15000 |    10000 |       5000 |       4
//   Over-refund $100   | rejected: 10000 > 5000 remaining (Version unchanged)

using System;

public class Payment
{
    public string PaymentId { get; set; }
    public string MerchantId { get; set; }
    public string CustomerId { get; set; }
    public string CardToken { get; set; }   // vault token, never the raw card number

    // The authorized amount. CapturedCents/RefundedCents move within this ceiling.
    public long AmountCents { get; set; }
    public string Currency { get; set; }
    public PaymentStatus Status { get; set; }

    // Tracked separately from AmountCents so partial capture/refund compose cleanly.
    public long CapturedCents { get; set; }
    public long RefundedCents { get; set; }

    // The refund guard: what's been captured but not yet refunded. Checked before every refund.
    public long RemainingRefundable => CapturedCents - RefundedCents;

    // Optimistic lock — incremented on every transition; Update rejects a stale expected value.
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }

    // Card network reference from Authorize; reused by Capture and Refund to target the same auth.
    public string AuthReference { get; set; }
}
