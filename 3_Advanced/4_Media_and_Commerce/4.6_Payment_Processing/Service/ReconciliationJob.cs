// ReconciliationJob — nightly check that our books match the bank's.
//
// Three discrepancy classes to handle:
//   IN_LEDGER_ONLY:  we recorded a settlement, bank hasn't reported it yet.
//                    Usually benign (next-day timing); flag if it lingers.
//   IN_BANK_ONLY:    bank reports money we have no record of. ALWAYS investigate
//                    — could be a missed capture event or, worse, a duplicate.
//   MISMATCH:        amounts disagree. Likely an FX rounding error or a fee
//                    calculation drift. Auto-correct small deltas, alert on big.
//
// In production this also reconciles processor fees, interchange splits, and
// per-currency conversion rates. Auditors live or die by this report.

using System.Collections.Generic;
using System.Linq;

public class ReconciliationJob
{
    private readonly LedgerService _ledger;
    private readonly PaymentStore  _payments;

    public ReconciliationJob(LedgerService ledger, PaymentStore payments)
    {
        _ledger   = ledger;
        _payments = payments;
    }

    public void Run(List<BankSettlementRecord> bankRecords)
    {
        System.Console.WriteLine("\n  [Reconcile] Starting nightly reconciliation...");

        // Internal: all settlement ledger entries (credit side = money sent to bank)
        var internalSettlements = _ledger.AllEntries
            .Where(e => e.AccountId == "bank:settlement" && e.EntryType == "CREDIT")
            .ToDictionary(e => e.PaymentId, e => e.AmountCents);

        var bankLookup = bankRecords.ToDictionary(r => r.RefId, r => r.AmountCents);

        int matched = 0, missingInBank = 0, missingInLedger = 0, mismatch = 0;

        // Pass 1: every ledger settlement should have a bank counterpart
        foreach (var (paymentId, internalAmt) in internalSettlements)
        {
            if (!bankLookup.TryGetValue(paymentId, out var bankAmt))
            {
                System.Console.WriteLine($"  [Reconcile] IN_LEDGER_ONLY: {paymentId} ${internalAmt / 100.0:F2} — pending settlement");
                missingInBank++;
            }
            else if (bankAmt != internalAmt)
            {
                System.Console.WriteLine($"  [Reconcile] MISMATCH: {paymentId} ledger=${internalAmt / 100.0:F2} bank=${bankAmt / 100.0:F2}");
                mismatch++;
            }
            else
            {
                matched++;
            }
        }

        // Pass 2: anything in the bank report we don't recognize is suspicious
        foreach (var (refId, bankAmt) in bankLookup)
        {
            if (!internalSettlements.ContainsKey(refId))
            {
                System.Console.WriteLine($"  [Reconcile] IN_BANK_ONLY: {refId} ${bankAmt / 100.0:F2} — INVESTIGATE");
                missingInLedger++;
            }
        }

        System.Console.WriteLine($"  [Reconcile] DONE — matched={matched} missing_in_bank={missingInBank} mismatches={mismatch} bank_only={missingInLedger}");
        System.Console.WriteLine($"  [Reconcile] Ledger balanced: {_ledger.IsBalanced()}");
    }
}
