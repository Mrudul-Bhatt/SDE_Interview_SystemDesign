// Payment lifecycle and fraud decisions.
//
// PaymentStatus is the state machine for every charge:
//   Pending → Authorized → Captured → Settled
//   (or Failed/Blocked/Cancelled at the start; Refunded/PartiallyRefunded at the end)
//
// Refunds never overwrite the original capture — they create offsetting ledger
// entries, and the payment status flips to PartiallyRefunded or Refunded based
// on the cumulative refunded amount.

public enum PaymentStatus
{
    Pending, Authorized, Captured, Settled,
    Failed, Blocked, Cancelled,
    Refunded, PartiallyRefunded
}

public enum FraudDecision { Allow, Review, Block }
