// Payment — the durable record of one charge.
//
// Version exists for optimistic locking: every state transition increments it,
// and PaymentStore.Update rejects writes whose expected version doesn't match.
// This prevents two concurrent captures from both succeeding (and over-capturing
// the authorization).
//
// CapturedCents and RefundedCents are tracked separately so partial captures
// and partial refunds compose cleanly. RemainingRefundable is the invariant
// the refund flow checks before contacting the card network.

using System;

public class Payment
{
    public string PaymentId      { get; set; }
    public string MerchantId     { get; set; }
    public string CustomerId     { get; set; }
    public string CardToken      { get; set; }
    public long   AmountCents    { get; set; }
    public string Currency       { get; set; }
    public PaymentStatus Status  { get; set; }
    public long   CapturedCents  { get; set; }
    public long   RefundedCents  { get; set; }
    public long   RemainingRefundable => CapturedCents - RefundedCents;
    public int    Version        { get; set; }  // optimistic lock
    public DateTime CreatedAt    { get; set; }
    public string AuthReference  { get; set; }  // card network ref
}
