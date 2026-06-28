// PaymentStore — the durable store of Payment records, with optimistic concurrency control.
//
// THE BIG IDEA:
// Just a keyed store of payments — except Update is guarded by a version check. Two requests
// might try to capture the same authorization at once; if both succeeded, the auth would be
// double-spent. Instead, each caller reads the current Version, mutates, bumps it, and Update
// only commits if no one else got there first. The loser is told to retry against fresh state.
//
// WHY THE expectedVersion+1 CHECK: it's a compare-and-swap. The write commits only if the
// record is still at the version the caller based its change on. In production this is literally
// "UPDATE payments SET ... WHERE version = expectedVersion" — the affected-row count is the CAS.
//
// HOW IT BEHAVES AT RUNTIME (two concurrent captures of pay_X, currently Version=1):
//
//   Caller   | reads Version | sets Version | Update result
//   ---------|---------------|--------------|----------------------------------
//   A        | 1             | 2            | ok (1+1 == 2)   -> committed
//   B (race) | 1             | 2            | rejected (db now at 2, not 1) -> "CONCURRENT_UPDATE"
//
//   B re-reads (now Version=2, status=Captured) and sees the capture already happened.

using System.Collections.Generic;
using System.Linq;

public class PaymentStore
{
    // paymentId -> record. Production: a relational table keyed by payment_id.
    private readonly Dictionary<string, Payment> _db = [];

    // First write of a new payment (always Version=1). No version guard needed on insert.
    public void Save(Payment p) => _db[p.PaymentId] = p;

    public Payment Get(string paymentId) => _db.TryGetValue(paymentId, out var p) ? p : null;

    // Compare-and-swap update: commits only if the incoming Version is exactly expectedVersion+1,
    // i.e. nobody else has advanced the record since the caller read it. Returns false on conflict.
    public bool Update(Payment updated, int expectedVersion)
    {
        if (!_db.ContainsKey(updated.PaymentId)) return false;
        if (updated.Version != expectedVersion + 1) return false;  // someone else won the race
        _db[updated.PaymentId] = updated;
        return true;
    }

    // Used by reconciliation (Scenario 7) to pull all Settled payments to match against the bank.
    public List<Payment> GetByStatus(PaymentStatus status) => _db.Values.Where(p => p.Status == status).ToList();

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT _db HOLDS AT RUNTIME (snapshot after all of Program.cs has run):
    //
    //   _db (paymentId -> Payment) = {
    //     
    //      // Scen 1: charged -> captured -> settled
    //     
    //      "pay_1a2b3c"  ->  { 
    //                         Status=Settled,            
    //                         Amount=10000, 
    //                         Captured=10000, 
    //                         Refunded=0,
    //                         Version=3, 
    //                         Merchant="merchant1", 
    //                         Customer="customer-alice",
    //                         AuthRef="AUTH_..." 
    //                       }      
    //     
    // 
    //
    //      // Scen 2: charged once; the 2nd identical request hit idempotency, no new row
    //
    //      "pay_4d5e6f"  ->  {
    //                         Status=Authorized,
    //                         Amount=5000,
    //                         Captured=0,
    //                         Refunded=0,
    //                         Version=1,
    //                         Merchant="merchant1",
    //                         Customer="customer-bob"
    //                       }
    //
    //
    //      // Scen 3: fraud blocked
    //
    //      "pay_7g8h9i"  ->  {
    //                         Status=Blocked,
    //                         Amount=200000,
    //                         Captured=0,
    //                         Refunded=0,
    //                         Version=1,
    //                         Customer="customer-fraudster"
    //                       }
    //
    //
    //      // Scen 4: card declined
    //
    //      "pay_0j1k2l"  ->  {
    //                         Status=Failed,
    //                         Amount=3000,
    //                         Captured=0,
    //                         Refunded=0,
    //                         Version=1,
    //                         Customer="customer-carol"
    //                       }
    //
    //
    //      // Scen 5: captured, two $50 refunds; Version bumped once per state change
    //
    //      "pay_3m4n5o"  ->  {
    //                         Status=PartiallyRefunded,
    //                         Amount=15000,
    //                         Captured=15000,
    //                         Refunded=10000,
    //                         Version=4,
    //                         Customer="customer-dave"
    //                       }
    //   }
    //
    // Reading the rows tells the whole story: Status is where each payment ended up, Version counts
    // how many transitions it went through, and Captured/Refunded show how much money actually moved.
    // Note there is NO row for the 2nd Scenario-2 charge — idempotency means one key = one Payment.
    // ──────────────────────────────────────────────────────────────────────────────────
}
