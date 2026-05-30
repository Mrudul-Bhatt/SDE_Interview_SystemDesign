// LedgerService — append-only double-entry ledger.
//
// Record takes a batch of entries that MUST sum to zero across debits/credits
// (callers are responsible for the math; IsBalanced verifies the global
// invariant). Entries are never updated or deleted — to "undo" something, you
// post an offsetting entry. This is what makes the ledger auditable: the full
// history of every cent that has ever moved is in here, in order.
//
// In production this lives in Postgres with strong consistency and synchronous
// replication. The "always balanced" invariant is the canary that catches bugs
// in the payment service before money is lost.

using System;
using System.Collections.Generic;
using System.Linq;

public class LedgerService
{
    private readonly List<LedgerEntry> _entries = new List<LedgerEntry>();

    public void Record(string paymentId, (string account, string type, long cents, string desc)[] entries)
    {
        foreach (var (account, type, cents, desc) in entries)
        {
            _entries.Add(new LedgerEntry
            {
                EntryId     = Guid.NewGuid().ToString("N")[..8],
                PaymentId   = paymentId,
                AccountId   = account,
                EntryType   = type,
                AmountCents = cents,
                Description = desc,
                CreatedAt   = DateTime.UtcNow
            });
        }
    }

    // Verify ledger invariant: total debits == total credits
    public bool IsBalanced()
    {
        long totalDebit  = _entries.Where(e => e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        long totalCredit = _entries.Where(e => e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        return totalDebit == totalCredit;
    }

    public long GetBalance(string accountId)
    {
        long credits = _entries.Where(e => e.AccountId == accountId && e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        long debits  = _entries.Where(e => e.AccountId == accountId && e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        return credits - debits;
    }

    public List<LedgerEntry> GetPaymentEntries(string paymentId) =>
        _entries.Where(e => e.PaymentId == paymentId).ToList();

    public List<LedgerEntry> AllEntries => _entries;
}
