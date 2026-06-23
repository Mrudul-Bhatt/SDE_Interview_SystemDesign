// Enums — the payment lifecycle states and the fraud decision buckets.
//
// PaymentStatus is the state machine every charge moves through. The happy path is
// Pending -> Authorized -> Captured -> Settled; the others are terminal off-ramps.
//
//   Authorized  - bank put a hold on the funds (money not moved yet)
//   Captured    - we told the bank to actually take the held funds
//   Settled     - funds wired to the merchant's bank account
//   Failed      - bank declined the authorization
//   Blocked     - our fraud scorer stopped it before the bank was contacted
//   Cancelled   - authorized but voided before capture
//   Refunded / PartiallyRefunded - some or all captured money returned to the customer
//
// Refunds never overwrite the capture: they post offsetting ledger entries and flip the status
// based on cumulative RefundedCents (PartiallyRefunded until it reaches the full captured amount).
//
// FraudDecision is bucketed from FraudScorer's 0-100 risk score:
//   score < 60  -> Allow    (proceed to authorize)
//   60..79      -> Review   (hold for manual / 24h review)
//   score >= 80 -> Block    (reject before contacting the bank)

public enum PaymentStatus
{
    Pending, Authorized, Captured, Settled,
    Failed, Blocked, Cancelled,
    Refunded, PartiallyRefunded
}

public enum FraudDecision { Allow, Review, Block }
