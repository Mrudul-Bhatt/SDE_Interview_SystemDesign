// PaymentService — the orchestrator that runs the charge -> capture -> settle -> refund flow.
//
// THE BIG IDEA:
// This is the brain that wires every other component together in the right order. The hard part
// of payments isn't any single step — it's doing them in a sequence that stays correct even when
// requests are retried or run concurrently. Three invariants carry the whole design:
//   - Idempotency: a retried Charge never charges twice (check first, store result last).
//   - Optimistic locking: concurrent Capture/Refund can't double-spend (Version CAS).
//   - Balanced ledger: every call posts debits that equal its credits.
//
// WHY THE CHARGE ORDER IS FIXED (and idempotency is stored LAST):
//   1 idempotency check  -> return cached result if we've seen this key (before any side effect)
//   2 validate token     -> reject junk before doing real work
//   3 fraud score        -> cheap local Block before the slow bank call
//   4 authorize (bank)   -> the slow remote leg
//   5 persist Payment    -> durable record in Authorized state
//   6 write ledger       -> balanced entries for the hold
//   7 store idempotency  -> LAST, only after side effects are committed. If we stored it earlier
//                           and then crashed, a retry would get "already done" for a charge that
//                           never finished. Storing last means a mid-flight crash leaves no key,
//                           so the retry safely redoes the charge.
//   8 notify webhook
//
// WHY CAPTURE/REFUND CAPTURE Version BEFORE MUTATING: they read expectedVersion, mutate, bump it,
// and PaymentStore.Update commits only if no one else advanced the row — the loser retries.
// Refund is additionally bounded by RemainingRefundable so it can't exceed what was captured.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 1: $100 charge -> capture -> settle; fee 2.9% = $2.90):
//
//   Call                | Payment after        | ledger entries posted (balanced)
//   --------------------|----------------------|-----------------------------------------
//   Charge(order-1001)  | Authorized, v1       | DEBIT customer 10000 / CREDIT suspense 10000
//   Capture(pay)        | Captured, v2         | DEBIT suspense 10000 /
//                       |   CapturedCents=10000|   CREDIT merchant 9710 + CREDIT platform 290
//   Settle(pay)         | Settled, v3          | DEBIT merchant 9710 / CREDIT bank 9710
//
//   Fraud Block (Scen 3) / decline (Scen 4) short-circuit: Payment saved Blocked/Failed,
//   idempotency stored, failure webhook queued, no authorization ledger entries written.

using System;

public class PaymentService
{
    private readonly IdempotencyStore    _idem;
    private readonly CardVault           _vault;
    private readonly FraudScorer         _fraud;
    private readonly CardNetworkGateway  _network;
    private readonly PaymentStore        _payments;
    private readonly LedgerService       _ledger;
    private readonly WebhookService      _webhooks;

    private const double PlatformFeeRate = 0.029;  // 2.9% platform fee, taken at capture

    public PaymentService(IdempotencyStore idem, CardVault vault, FraudScorer fraud,
        CardNetworkGateway network, PaymentStore payments, LedgerService ledger, WebhookService webhooks)
    {
        _idem     = idem;
        _vault    = vault;
        _fraud    = fraud;
        _network  = network;
        _payments = payments;
        _ledger   = ledger;
        _webhooks = webhooks;
    }

    public ChargeResult Charge(ChargeRequest req)
    {
        // Step 1: idempotency FIRST — a retry returns the cached result, never a second charge.
        var existing = _idem.TryGet(req.MerchantId, req.IdempotencyKey);
        if (existing != null)
        {
            var cachedPayment = _payments.Get(existing.PaymentId);
            Console.WriteLine($"  [Payment] Idempotency HIT for key={req.IdempotencyKey} → returning cached result");
            return new ChargeResult { Success = true, PaymentId = existing.PaymentId,
                                      Status = cachedPayment.Status, WasIdempotent = true };
        }

        // Step 2: reject an invalid/unknown card token before doing real work.
        if (!_vault.IsValid(req.CardToken))
            return Fail(req, "INVALID_CARD_TOKEN");

        // Step 3: fraud score — a local Block here avoids the slow bank call entirely.
        var (decision, score, reasons) = _fraud.Score(req.FraudCtx);
        Console.WriteLine($"  [Fraud] score={score} decision={decision}" +
                          (reasons.Count > 0 ? $" reasons=[{string.Join(", ", reasons)}]" : ""));

        if (decision == FraudDecision.Block)
        {
            var blockedId = CreatePayment(req, PaymentStatus.Blocked);
            _idem.Store(req.MerchantId, req.IdempotencyKey, blockedId, "BLOCKED");
            Notify(req, blockedId, "payment.blocked");
            return new ChargeResult { Success = false, PaymentId = blockedId,
                                      Status = PaymentStatus.Blocked, Error = "PAYMENT_DECLINED" };
        }

        // Step 4: authorize with the card network (the slow remote leg).
        var pan = _vault.Detokenize(req.CardToken);
        var (authOk, authRef, authErr) = _network.Authorize(pan, req.AmountCents);

        if (!authOk)
        {
            var failedId = CreatePayment(req, PaymentStatus.Failed);
            _idem.Store(req.MerchantId, req.IdempotencyKey, failedId, "FAILED");
            Notify(req, failedId, "payment.failed");
            return new ChargeResult { Success = false, PaymentId = failedId,
                                      Status = PaymentStatus.Failed, Error = authErr };
        }

        // Step 5: persist the authorized payment.
        var paymentId = CreatePayment(req, PaymentStatus.Authorized, authRef);

        // Step 6: balanced ledger entries for the hold (customer owes; suspense holds it).
        _ledger.Record(paymentId, new[]
        {
            ("customer:" + req.CustomerId, "DEBIT",  req.AmountCents, "Authorization hold"),
            ("suspense",                   "CREDIT", req.AmountCents, "Authorization hold")
        });

        // Step 7: store the idempotency result LAST — after all side effects are committed.
        _idem.Store(req.MerchantId, req.IdempotencyKey, paymentId, "AUTHORIZED");

        Notify(req, paymentId, "payment.authorized");
        Console.WriteLine($"  [Payment] {paymentId} AUTHORIZED for ${req.AmountCents / 100.0:F2} {req.Currency}");

        return new ChargeResult { Success = true, PaymentId = paymentId,
                                  Status = PaymentStatus.Authorized };
    }

    // Turn the hold into a real charge. Guarded by status + optimistic lock.
    public (bool ok, string error) Capture(string paymentId)
    {
        var payment = _payments.Get(paymentId);
        if (payment == null) return (false, "PAYMENT_NOT_FOUND");
        if (payment.Status != PaymentStatus.Authorized) return (false, $"INVALID_STATUS:{payment.Status}");

        var (ok, captureErr) = _network.Capture(payment.AuthReference, payment.AmountCents);
        if (!ok) return (false, captureErr);

        // Capture expectedVersion BEFORE mutating, so the Update below is a compare-and-swap.
        int expectedVersion = payment.Version;
        long fee       = (long)(payment.AmountCents * PlatformFeeRate);
        long netAmount = payment.AmountCents - fee;

        payment.Status        = PaymentStatus.Captured;
        payment.CapturedCents = payment.AmountCents;
        payment.Version++;

        if (!_payments.Update(payment, expectedVersion))
            return (false, "CONCURRENT_UPDATE — retry");

        // Release the hold and split the money: merchant gets net, platform keeps the fee.
        _ledger.Record(paymentId, new[]
        {
            ("suspense",                        "DEBIT",  payment.AmountCents, "Capture — release hold"),
            ("merchant:" + payment.MerchantId,  "CREDIT", netAmount,           "Capture — merchant net"),
            ("platform:revenue",                "CREDIT", fee,                 "Capture — platform fee")
        });

        Notify(null, paymentId, "payment.captured");
        Console.WriteLine($"  [Payment] {paymentId} CAPTURED — merchant nets ${netAmount / 100.0:F2}, fee ${fee / 100.0:F2}");
        return (true, null);
    }

    // Wire the merchant's net to their bank account.
    public (bool ok, string error) Settle(string paymentId)
    {
        var payment = _payments.Get(paymentId);
        if (payment == null) return (false, "PAYMENT_NOT_FOUND");
        if (payment.Status != PaymentStatus.Captured) return (false, $"INVALID_STATUS:{payment.Status}");

        long netAmount = payment.CapturedCents - (long)(payment.CapturedCents * PlatformFeeRate);

        int expectedVersion = payment.Version;
        payment.Status = PaymentStatus.Settled;
        payment.Version++;
        _payments.Update(payment, expectedVersion);

        _ledger.Record(paymentId, new[]
        {
            ("merchant:" + payment.MerchantId, "DEBIT",  netAmount, "Settlement wire"),
            ("bank:settlement",                "CREDIT", netAmount, "Settlement wire")
        });

        Notify(null, paymentId, "payment.settled");
        Console.WriteLine($"  [Payment] {paymentId} SETTLED — ${netAmount / 100.0:F2} wired to bank");
        return (true, null);
    }

    public (bool ok, string refundId, string error) Refund(string paymentId, long refundCents, string idempotencyKey = null)
    {
        var payment = _payments.Get(paymentId);
        if (payment == null) return (false, null, "PAYMENT_NOT_FOUND");
        if (payment.Status != PaymentStatus.Captured &&
            payment.Status != PaymentStatus.Settled  &&
            payment.Status != PaymentStatus.PartiallyRefunded)
            return (false, null, $"INVALID_STATUS:{payment.Status}");

        // Hard guard: never refund more than what's captured-but-not-yet-refunded.
        if (refundCents > payment.RemainingRefundable)
            return (false, null, $"REFUND_EXCEEDS_CAPTURED: requested={refundCents} remaining={payment.RemainingRefundable}");

        var (ok, refundRef, refundErr) = _network.Refund(payment.AuthReference, refundCents);
        if (!ok) return (false, null, refundErr);

        long feeRefund = (long)(refundCents * PlatformFeeRate);

        int expectedVersion = payment.Version;
        payment.RefundedCents += refundCents;
        // Fully refunded once cumulative refunds reach the captured amount; partial until then.
        payment.Status = payment.RefundedCents >= payment.CapturedCents
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        payment.Version++;
        _payments.Update(payment, expectedVersion);

        // Balanced refund (4 entries): return the money AND reverse the platform fee proportionally.
        _ledger.Record(paymentId, new[]
        {
            ("merchant:" + payment.MerchantId, "DEBIT",  refundCents,"Refund — debit merchant (full)"),
            ("customer:" + payment.CustomerId, "CREDIT", refundCents,"Refund — credit customer"),
            ("platform:revenue",               "DEBIT",  feeRefund,  "Refund — reverse fee"),
            ("merchant:" + payment.MerchantId, "CREDIT", feeRefund,  "Refund — return fee to merchant")
        });

        Notify(null, paymentId, "payment.refunded");
        Console.WriteLine($"  [Payment] {paymentId} REFUND ${refundCents / 100.0:F2} → status={payment.Status}");
        return (true, refundRef, null);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Mints a payment id and saves the row at Version=1.
    private string CreatePayment(ChargeRequest req, PaymentStatus status, string authRef = null)
    {
        var id = "pay_" + Guid.NewGuid().ToString("N")[..8];
        _payments.Save(new Payment
        {
            PaymentId     = id,
            MerchantId    = req.MerchantId,
            CustomerId    = req.CustomerId,
            CardToken     = req.CardToken,
            AmountCents   = req.AmountCents,
            Currency      = req.Currency,
            Status        = status,
            Version       = 1,
            CreatedAt     = DateTime.UtcNow,
            AuthReference = authRef
        });
        return id;
    }

    // Queues a status-change webhook for the merchant (delivery handled by WebhookService).
    private void Notify(ChargeRequest req, string paymentId, string eventName)
    {
        var url = req?.MerchantUrl ?? _payments.Get(paymentId)?.MerchantId + "/webhook";
        var payload = $"{{\"event\":\"{eventName}\",\"payment_id\":\"{paymentId}\"}}";
        _webhooks.Enqueue(req?.MerchantId ?? "merchant1", url ?? "/webhook", payload);
    }

    private ChargeResult Fail(ChargeRequest req, string error) =>
        new ChargeResult { Success = false, Error = error };

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT THIS CLASS HOLDS AT RUNTIME:
    //
    // Nothing of its own — PaymentService is a STATELESS orchestrator. Its only fields are the
    // collaborators it coordinates, plus one constant. The real per-payment state lives in the
    // stores it writes to, NOT here:
    //
    //   _idem      ->  IdempotencyStore    (the retry cache)
    //   _vault     ->  CardVault           (token <-> PAN)
    //   _fraud     ->  FraudScorer         (risk scoring; itself stateless)
    //   _network   ->  CardNetworkGateway  (the bank)
    //   _payments  ->  PaymentStore        (durable payment rows)   <- charge state lives here
    //   _ledger    ->  LedgerService       (the money trail)        <- and here
    //   _webhooks  ->  WebhookService      (merchant notifications)
    //   PlatformFeeRate = 0.029  (const, the 2.9% platform fee)
    //
    // Every Charge/Capture/Settle/Refund reads and writes those stores and keeps NO per-payment
    // state between calls. That's why many PaymentService workers can share the same stores and
    // behave identically — scaling out is safe because the state isn't in this object.
    // ──────────────────────────────────────────────────────────────────────────────────
}
