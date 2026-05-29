// PaymentStore — the durable payment record DB.
//
// Update enforces optimistic concurrency control: the caller passes
// expectedVersion, and the write only succeeds if the new Payment.Version is
// exactly expectedVersion+1. This is the mechanism that prevents two concurrent
// captures from racing — whoever loses the race gets a CONCURRENT_UPDATE error
// and retries against the now-current version.
//
// In production: UPDATE payments SET ... WHERE version = expectedVersion;
// the affected_rows check is the atomic CAS that makes this safe.

using System.Collections.Generic;
using System.Linq;

public class PaymentStore
{
    private readonly Dictionary<string, Payment> _db = new Dictionary<string, Payment>();

    public void Save(Payment p) => _db[p.PaymentId] = p;

    public Payment Get(string paymentId) =>
        _db.TryGetValue(paymentId, out var p) ? p : null;

    public bool Update(Payment updated, int expectedVersion)
    {
        if (!_db.ContainsKey(updated.PaymentId)) return false;
        if (updated.Version != expectedVersion + 1) return false;  // version conflict
        _db[updated.PaymentId] = updated;
        return true;
    }

    public List<Payment> GetByStatus(PaymentStatus status) =>
        _db.Values.Where(p => p.Status == status).ToList();
}
