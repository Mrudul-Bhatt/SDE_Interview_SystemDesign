// ReconciliationJob — the nightly check that our ledger matches what the bank actually moved.
//
// THE BIG IDEA:
// Our ledger records what we THINK happened; the bank's settlement report records what REALLY
// moved. They can drift — a dropped capture, a duplicate, an FX rounding error. Reconciliation
// cross-checks the two and flags every discrepancy. This is the safety net auditors depend on.
//
// Three discrepancy classes (each handled differently):
//   IN_LEDGER_ONLY  - we recorded a settlement the bank hasn't reported yet. Usually benign
//                     next-day timing; only worrying if it lingers.
//   IN_BANK_ONLY    - the bank moved money we have no record of. ALWAYS investigate — a missed
//                     capture event, or worse, a duplicate.
//   MISMATCH        - both sides have it but the amounts differ. Likely fee/FX drift; auto-fix
//                     small deltas, alert on large ones.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 7: one settled $100 payment + a mystery bank row):
//
//   Source comparison                                  | classification
//   ---------------------------------------------------|------------------------------
//   ledger bank:settlement pay_X = 9710  vs bank 9710  | matched
//   bank row pay_unknown99 = 2000, no ledger entry     | IN_BANK_ONLY -> INVESTIGATE
//
//   Result: matched=1  missing_in_bank=0  mismatches=0  bank_only=1  (ledger still balanced)
//   (9710 = $100 capture minus the 2.9% fee — the net actually wired, so the amounts agree.)

using System.Collections.Generic;
using System.Linq;

public class ReconciliationJob
{
    private readonly LedgerService _ledger;

    public ReconciliationJob(LedgerService ledger, PaymentStore payments)
    {
        _ledger = ledger;
    }

    public void Run(List<BankSettlementRecord> bankRecords)
    {
        System.Console.WriteLine("\n  [Reconcile] Starting nightly reconciliation...");

        // Our side: every settlement we recorded (the CREDIT to bank:settlement = money sent out).
        var internalSettlements = _ledger.AllEntries
            .Where(e => e.AccountId == "bank:settlement" && e.EntryType == "CREDIT")
            .ToDictionary(e => e.PaymentId, e => e.AmountCents);

        // Their side: keyed by RefId for O(1) lookup.
        var bankLookup = bankRecords.ToDictionary(r => r.RefId, r => r.AmountCents);

        int matched = 0, missingInBank = 0, missingInLedger = 0, mismatch = 0;

        // Pass 1: every ledger settlement should have a matching bank row with the same amount.
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

        // Pass 2: anything in the bank report we don't recognize is suspicious — investigate.
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

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT THIS CLASS HOLDS AT RUNTIME:
    //
    // Nothing persistent — ReconciliationJob is a STATELESS batch job. Its only fields are two
    // injected references:
    //
    //   _ledger    ->  LedgerService   (our record of money sent to the bank)
    //   _payments  ->  PaymentStore    (injected for extension; Run currently reads only _ledger)
    //
    // All working data is LOCAL to a single Run() call and discarded when it returns. For the
    // Scenario 7 bank report, those locals look like:
    //
    //   internalSettlements (paymentId -> cents) = {
    //                                                "pay_1a2b3c" -> 9710   // only Scen 1 settled
    //                                              }
    //   bankLookup          (refId     -> cents) = {
    //                                                "pay_1a2b3c"     -> 9710,   // matches ours
    //                                                "pay_unknown99"  -> 2000    // no match -> flag
    //                                              }
    //   counters: matched=1, missingInBank=0, mismatches=0, bankOnly=1
    //
    // Keeping no state between runs is what lets the nightly job run on a fresh worker each night.
    // ──────────────────────────────────────────────────────────────────────────────────
}
