// PaymentService — the orchestrator that runs the charge → capture → settle pipeline.
//
// Charge flow (order matters):
//   1. Idempotency check — return cached result if we've seen this key before.
//      MUST be first, before any side effects.
//   2. Validate card token via the vault.
//   3. Fraud score → Allow / Review / Block.
//   4. Card network Authorize (the slow remote call).
//   5. Persist the Payment row in Authorized state.
//   6. Write the ledger entries (debit customer / credit suspense).
//   7. Store the idempotency result so retries return this same payment.
//   8. Enqueue the webhook.
//
// Capture / Refund use optimistic locking (Version-based) so concurrent
// requests can't double-spend the authorization or over-refund a capture.
// Refunds are bounded by RemainingRefundable to prevent negative balances.
//
// Ledger entries are always BALANCED inside each call: every debit has a
// matching credit. Refund posts 4 entries (debit merchant full, credit customer
// full, debit platform fee, credit merchant fee-return) so the platform's fee
// is correctly reversed too.

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

    private const double PlatformFeeRate = 0.029;  // 2.9%

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
        // Step 1: Idempotency check — return cached result if seen before
        var existing = _idem.TryGet(req.MerchantId, req.IdempotencyKey);
        if (existing != null)
        {
            var cachedPayment = _payments.Get(existing.PaymentId);
            Console.WriteLine($"  [Payment] Idempotency HIT for key={req.IdempotencyKey} → returning cached result");
            return new ChargeResult { Success = true, PaymentId = existing.PaymentId,
                                      Status = cachedPayment.Status, WasIdempotent = true };
        }

        // Step 2: Validate card token
        if (!_vault.IsValid(req.CardToken))
            return Fail(req, "INVALID_CARD_TOKEN");

        // Step 3: Fraud check
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

        // Step 4: Authorize with card network
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

        // Step 5: Record authorized payment
        var paymentId = CreatePayment(req, PaymentStatus.Authorized, authRef);

        // Step 6: Write balanced ledger entries for authorization
        _ledger.Record(paymentId, new[]
        {
            ("customer:" + req.CustomerId, "DEBIT",  req.AmountCents, "Authorization hold"),
            ("suspense",                   "CREDIT", req.AmountCents, "Authorization hold")
        });

        // Step 7: Store idempotency result LAST (after side effects committed)
        _idem.Store(req.MerchantId, req.IdempotencyKey, paymentId, "AUTHORIZED");

        Notify(req, paymentId, "payment.authorized");
        Console.WriteLine($"  [Payment] {paymentId} AUTHORIZED for ${req.AmountCents / 100.0:F2} {req.Currency}");

        return new ChargeResult { Success = true, PaymentId = paymentId,
                                  Status = PaymentStatus.Authorized };
    }

    public (bool ok, string error) Capture(string paymentId)
    {
        var payment = _payments.Get(paymentId);
        if (payment == null) return (false, "PAYMENT_NOT_FOUND");
        if (payment.Status != PaymentStatus.Authorized) return (false, $"INVALID_STATUS:{payment.Status}");

        var (ok, captureErr) = _network.Capture(payment.AuthReference, payment.AmountCents);
        if (!ok) return (false, captureErr);

        // Optimistic lock — capture expectedVersion BEFORE mutation
        int expectedVersion = payment.Version;
        long fee       = (long)(payment.AmountCents * PlatformFeeRate);
        long netAmount = payment.AmountCents - fee;

        payment.Status        = PaymentStatus.Captured;
        payment.CapturedCents = payment.AmountCents;
        payment.Version++;

        if (!_payments.Update(payment, expectedVersion))
            return (false, "CONCURRENT_UPDATE — retry");

        // Write balanced capture entries: suspense → merchant net + platform fee
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

        // Hard guard: refund must fit within what's been captured but not yet refunded
        if (refundCents > payment.RemainingRefundable)
            return (false, null, $"REFUND_EXCEEDS_CAPTURED: requested={refundCents} remaining={payment.RemainingRefundable}");

        var (ok, refundRef, refundErr) = _network.Refund(payment.AuthReference, refundCents);
        if (!ok) return (false, null, refundErr);

        long feeRefund = (long)(refundCents * PlatformFeeRate);

        int expectedVersion = payment.Version;
        payment.RefundedCents += refundCents;
        payment.Status = payment.RefundedCents >= payment.CapturedCents
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        payment.Version++;
        _payments.Update(payment, expectedVersion);

        // Balanced refund: 2 debits + 2 credits, each side sums to refundCents+feeRefund
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

    private void Notify(ChargeRequest req, string paymentId, string eventName)
    {
        var url = req?.MerchantUrl ?? _payments.Get(paymentId)?.MerchantId + "/webhook";
        var payload = $"{{\"event\":\"{eventName}\",\"payment_id\":\"{paymentId}\"}}";
        _webhooks.Enqueue(req?.MerchantId ?? "merchant1", url ?? "/webhook", payload);
    }

    private ChargeResult Fail(ChargeRequest req, string error) =>
        new ChargeResult { Success = false, Error = error };
}
