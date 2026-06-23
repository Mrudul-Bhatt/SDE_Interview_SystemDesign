// BankSettlementRecord — one row from the acquiring bank's end-of-day settlement report.
//
// THE BIG IDEA:
// Our ledger says what we THINK happened; the bank's report says what ACTUALLY moved. Nightly
// reconciliation matches the two: each bank row's RefId is matched to our PaymentId, and any
// mismatch in amount — or a row on one side with no match on the other — is flagged. This is the
// safety net that catches dropped captures, double-settlements, and bank-side errors.
//
// WHY SO FEW FIELDS: in production this arrives as a CSV/API feed with many columns; only these
// three matter for the match (which payment, how much, what currency).
//
// HOW IT BEHAVES AT RUNTIME (Scenario 7: nightly reconciliation):
//
//   Bank row                                      | Reconciliation result
//   ----------------------------------------------|---------------------------------
//   { RefId="pay_X", AmountCents=9710, "USD" }    | matches our settled net -> OK
//   { RefId="pay_unknown99", 2000, "USD" }        | no matching payment -> FLAGGED
//
//   (9710 = $100 capture minus the 2.9% platform fee — the net the bank actually wired.)

public class BankSettlementRecord
{
    public string RefId { get; set; }   // the bank's reference; matched to our PaymentId
    public long AmountCents { get; set; }   // what the bank says moved; compared to our ledger
    public string Currency { get; set; }
}
