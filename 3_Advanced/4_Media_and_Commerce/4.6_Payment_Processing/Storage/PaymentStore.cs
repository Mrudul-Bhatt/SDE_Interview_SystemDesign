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
}
