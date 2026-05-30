// BankSettlementRecord — one row from the bank's settlement report.
//
// In production this comes in as a CSV or API feed at end-of-day from the
// acquiring bank. Reconciliation matches RefId against our internal PaymentId
// and flags any rows where the bank's amount doesn't match our ledger's.

public class BankSettlementRecord
{
    public string RefId        { get; set; }
    public long   AmountCents  { get; set; }
    public string Currency     { get; set; }
}
