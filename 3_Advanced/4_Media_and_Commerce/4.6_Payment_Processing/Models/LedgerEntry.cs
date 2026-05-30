// LedgerEntry — one half of a double-entry transaction.
//
// Every money movement is recorded as a pair (or more) of entries whose debits
// sum to credits. Entries are append-only and immutable — corrections happen
// via new offsetting entries, never via update/delete. This is what makes the
// ledger auditable.

using System;

public class LedgerEntry
{
    public string EntryId      { get; set; }
    public string PaymentId    { get; set; }
    public string AccountId    { get; set; }
    public string EntryType    { get; set; }  // DEBIT or CREDIT
    public long   AmountCents  { get; set; }
    public string Description  { get; set; }
    public DateTime CreatedAt  { get; set; }
}
