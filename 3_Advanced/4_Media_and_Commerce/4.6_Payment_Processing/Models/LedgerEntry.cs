// LedgerEntry — one line of a double-entry bookkeeping transaction.
//
// THE BIG IDEA:
// Every money movement is recorded as a set of entries whose DEBITs sum to its CREDITs — money
// is never created or destroyed, only moved between accounts. Entries are append-only and
// immutable: a mistake is fixed with a new offsetting entry, never an update or delete. That
// permanent, balanced trail is what makes the ledger auditable (and is why banks work this way).
//
// WHY APPEND-ONLY: an auditor must be able to replay every entry and arrive at today's balances.
// Editing or deleting a past entry would break that replay and hide what really happened.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 1 capture of a $100 charge; fee = 2.9% = $2.90):
//
//   EntryType | AccountId           | AmountCents | meaning
//   ----------|---------------------|-------------|------------------------
//   DEBIT     | suspense            |       10000 | release the auth hold
//   CREDIT    | merchant:merchant1  |        9710 | merchant's net payout
//   CREDIT    | platform:revenue    |         290 | platform's 2.9% fee
//
//   Debits 10000 == Credits 9710 + 290  -> balanced (LedgerService.IsBalanced() stays true).

using System;

public class LedgerEntry
{
    public string EntryId { get; set; }
    public string PaymentId { get; set; }   // groups all entries for one charge
    public string AccountId { get; set; }   // e.g. "merchant:m1", "platform:revenue", "suspense"
    public string EntryType { get; set; }   // "DEBIT" or "CREDIT"
    public long AmountCents { get; set; }
    public string Description { get; set; }   // human-readable reason, for audit
    public DateTime CreatedAt { get; set; }
}
