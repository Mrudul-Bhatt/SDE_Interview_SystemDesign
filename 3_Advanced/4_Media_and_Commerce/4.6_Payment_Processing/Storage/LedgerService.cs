// LedgerService — the append-only, double-entry money ledger.
//
// THE BIG IDEA:
// Every cent that moves is recorded as immutable entries whose debits equal credits. Nothing is
// ever updated or deleted — to undo, you post an offsetting entry. The complete, ordered history
// of all movements lives here, which is exactly what makes it auditable. (See LedgerEntry.)
//
// WHY IsBalanced IS THE CANARY: across the whole ledger, total debits must always equal total
// credits. If a bug in PaymentService ever posts a lopsided set of entries, IsBalanced flips
// false — catching the error before money is actually lost. Callers do the per-transaction math;
// this verifies the global invariant.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 1: authorize + capture a $100 charge):
//
//   Record call (entries)                          | running totals
//   -----------------------------------------------|----------------------------------
//   authorize: DEBIT customer 10000 /              | debits 10000 == credits 10000
//              CREDIT suspense 10000               |   IsBalanced = true
//   capture:   DEBIT suspense 10000 /              | debits 20000 == credits 20000
//              CREDIT merchant 9710 + platform 290 |   IsBalanced = true
//
//   GetBalance("platform:revenue") -> 290 (credits 290 - debits 0).
//   GetPaymentEntries(payId) -> all 5 entries above, for that one payment.

using System;
using System.Collections.Generic;
using System.Linq;

public class LedgerService
{
    // Append-only log of every entry, in insertion order. Production: Postgres, strongly consistent.
    private readonly List<LedgerEntry> _entries = [];

    // Appends a batch of entries for one payment. The caller must pass a set whose debits equal
    // its credits (IsBalanced verifies this globally afterwards).
    public void Record(string paymentId, (string account, string type, long cents, string desc)[] entries)
    {
        foreach (var (account, type, cents, desc) in entries)
        {
            _entries.Add(new LedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N")[..8],
                PaymentId = paymentId,
                AccountId = account,
                EntryType = type,
                AmountCents = cents,
                Description = desc,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    // The global invariant: total debits == total credits. Should be true after every Record.
    public bool IsBalanced()
    {
        long totalDebit = _entries.Where(e => e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        long totalCredit = _entries.Where(e => e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        return totalDebit == totalCredit;
    }

    // Net balance of one account = its credits minus its debits.
    public long GetBalance(string accountId)
    {
        long credits = _entries.Where(e => e.AccountId == accountId && e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        long debits = _entries.Where(e => e.AccountId == accountId && e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        return credits - debits;
    }

    // All entries for one payment (the audit trail for a single charge).
    public List<LedgerEntry> GetPaymentEntries(string paymentId) => _entries.Where(e => e.PaymentId == paymentId).ToList();

    public List<LedgerEntry> AllEntries => _entries;

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT _entries HOLDS AT RUNTIME (snapshot after all of Program.cs has run):
    //
    // An append-only log in insertion order. Each charge appends a balanced batch (debits ==
    // credits). Shown grouped by payment for readability; amounts are in cents.
    //
    //   _entries (in order) = [
    //
    //      // Scen 1 (pay_1a2b3c): authorize -> capture -> settle      (7 entries)
    //      DEBIT   customer:customer-alice   10000   Authorization hold
    //      CREDIT  suspense                  10000   Authorization hold
    //      DEBIT   suspense                  10000   Capture — release hold
    //      CREDIT  merchant:merchant1         9710   Capture — merchant net
    //      CREDIT  platform:revenue            290   Capture — platform fee
    //      DEBIT   merchant:merchant1         9710   Settlement wire
    //      CREDIT  bank:settlement            9710   Settlement wire
    //
    //
    //      // Scen 2 (pay_4d5e6f): authorized only, never captured     (2 entries)
    //      DEBIT   customer:customer-bob      5000   Authorization hold
    //      CREDIT  suspense                   5000   Authorization hold
    //
    //
    //      // Scen 3 (blocked) and Scen 4 (declined): NO entries — no money ever moved
    //
    //
    //      // Scen 5 (pay_3m4n5o): authorize -> capture -> two $50 refunds   (13 entries)
    //      DEBIT   customer:customer-dave    15000   Authorization hold
    //      CREDIT  suspense                  15000   Authorization hold
    //      DEBIT   suspense                  15000   Capture — release hold
    //      CREDIT  merchant:merchant1        14565   Capture — merchant net
    //      CREDIT  platform:revenue            435   Capture — platform fee
    //      DEBIT   merchant:merchant1         5000   Refund — debit merchant (full)
    //      CREDIT  customer:customer-dave     5000   Refund — credit customer
    //      DEBIT   platform:revenue            145   Refund — reverse fee
    //      CREDIT  merchant:merchant1          145   Refund — return fee to merchant
    //      DEBIT   merchant:merchant1         5000   Refund — debit merchant (full)
    //      CREDIT  customer:customer-dave     5000   Refund — credit customer
    //      DEBIT   platform:revenue            145   Refund — reverse fee
    //      CREDIT  merchant:merchant1          145   Refund — return fee to merchant
    //   ]
    //
    // Nothing is ever updated or deleted — corrections are new offsetting entries. Across the whole
    // log total DEBITs == total CREDITs, so IsBalanced() stays true. Blocked/declined charges add
    // nothing because no money moved. This full, ordered history is what makes the ledger auditable.
    // ──────────────────────────────────────────────────────────────────────────────────
}
