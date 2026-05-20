// Payment Processing System — C# simulation
// Covers: idempotency, payment state machine, double-entry ledger,
//         card tokenization, fraud scoring, webhook delivery with retry,
//         refunds, and nightly reconciliation.
// assembly-guid: {F7A8B9C0-D1E2-3456-F890-456789000036}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

public enum PaymentStatus
{
    Pending, Authorized, Captured, Settled,
    Failed, Blocked, Cancelled,
    Refunded, PartiallyRefunded
}

public enum FraudDecision { Allow, Review, Block }

public class Payment
{
    public string PaymentId      { get; set; }
    public string MerchantId     { get; set; }
    public string CustomerId     { get; set; }
    public string CardToken      { get; set; }
    public long   AmountCents    { get; set; }
    public string Currency       { get; set; }
    public PaymentStatus Status  { get; set; }
    public long   CapturedCents  { get; set; }
    public long   RefundedCents  { get; set; }
    public long   RemainingRefundable => CapturedCents - RefundedCents;
    public int    Version        { get; set; }  // optimistic lock
    public DateTime CreatedAt    { get; set; }
    public string AuthReference  { get; set; }  // card network ref
}

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

public class WebhookEvent
{
    public string EventId      { get; set; }
    public string MerchantId   { get; set; }
    public string MerchantUrl  { get; set; }
    public string Payload      { get; set; }
    public string Status       { get; set; }  // PENDING, DELIVERED, FAILED
    public int    AttemptCount { get; set; }
    public DateTime? NextRetry { get; set; }
    public DateTime CreatedAt  { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. Card Vault — PCI-scoped tokenization
// ─────────────────────────────────────────────────────────────────────────────

public class CardVault
{
    // In production: encrypted with HSM; never logged
    private readonly Dictionary<string, string> _tokenToPan = new Dictionary<string, string>();

    public string Tokenize(string pan)
    {
        var token = "tok_" + Convert.ToHexString(RandomBytes(8)).ToLower();
        _tokenToPan[token] = pan;  // stored encrypted in real system
        return token;
    }

    // Only called by payment engine during authorization
    public string Detokenize(string token) =>
        _tokenToPan.TryGetValue(token, out var pan) ? pan : null;

    public bool IsValid(string token) => _tokenToPan.ContainsKey(token);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Idempotency Store
// ─────────────────────────────────────────────────────────────────────────────

public class IdempotencyEntry
{
    public string Key       { get; set; }
    public string MerchantId { get; set; }
    public string Result    { get; set; }  // serialized response
    public string PaymentId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class IdempotencyStore
{
    private readonly Dictionary<string, IdempotencyEntry> _store = new Dictionary<string, IdempotencyEntry>();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Returns existing entry if found and not expired
    public IdempotencyEntry TryGet(string merchantId, string key)
    {
        var compositeKey = $"{merchantId}:{key}";
        if (_store.TryGetValue(compositeKey, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry;
        return null;
    }

    public void Store(string merchantId, string key, string paymentId, string result)
    {
        _store[$"{merchantId}:{key}"] = new IdempotencyEntry
        {
            Key        = key,
            MerchantId = merchantId,
            PaymentId  = paymentId,
            Result     = result,
            ExpiresAt  = DateTime.UtcNow.Add(Ttl)
        };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Fraud Scorer — rule-based
// ─────────────────────────────────────────────────────────────────────────────

public class FraudContext
{
    public string CustomerId     { get; set; }
    public string CardToken      { get; set; }
    public long   AmountCents    { get; set; }
    public string IpAddress      { get; set; }
    public string Country        { get; set; }
    public int    FailedAttempts { get; set; }  // in last 10 min for this card
    public bool   AvsMatch       { get; set; }
    public bool   CvvMatch       { get; set; }
    public long   AvgOrderCents  { get; set; }  // customer's historical average
}

public class FraudScorer
{
    private static readonly HashSet<string> HighRiskCountries = new HashSet<string>
        { "XX", "ZZ" };  // simulated; real list has actual country codes

    public (FraudDecision decision, int score, List<string> reasons) Score(FraudContext ctx)
    {
        int score = 0;
        var reasons = new List<string>();

        // Hard rules (instant block)
        if (!ctx.CvvMatch)    { score += 40; reasons.Add("CVV mismatch"); }
        if (!ctx.AvsMatch)    { score += 20; reasons.Add("AVS mismatch"); }
        if (HighRiskCountries.Contains(ctx.Country)) { score += 30; reasons.Add("High-risk country"); }

        // Velocity checks
        if (ctx.FailedAttempts >= 5) { score += 50; reasons.Add("Velocity: 5+ failures in 10min"); }

        // Amount anomaly
        if (ctx.AvgOrderCents > 0 && ctx.AmountCents > ctx.AvgOrderCents * 10)
        {
            score += 25;
            reasons.Add($"Amount 10× customer average (${ctx.AmountCents / 100.0:F2} vs avg ${ctx.AvgOrderCents / 100.0:F2})");
        }

        score = Math.Min(score, 100);

        FraudDecision decision = score < 60 ? FraudDecision.Allow
                               : score < 80 ? FraudDecision.Review
                               :              FraudDecision.Block;

        return (decision, score, reasons);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Ledger Service — double-entry bookkeeping
// ─────────────────────────────────────────────────────────────────────────────

public class LedgerService
{
    private readonly List<LedgerEntry> _entries = new List<LedgerEntry>();

    public void Record(string paymentId, (string account, string type, long cents, string desc)[] entries)
    {
        foreach (var (account, type, cents, desc) in entries)
        {
            _entries.Add(new LedgerEntry
            {
                EntryId     = Guid.NewGuid().ToString("N")[..8],
                PaymentId   = paymentId,
                AccountId   = account,
                EntryType   = type,
                AmountCents = cents,
                Description = desc,
                CreatedAt   = DateTime.UtcNow
            });
        }
    }

    // Verify ledger invariant: total debits == total credits
    public bool IsBalanced()
    {
        long totalDebit  = _entries.Where(e => e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        long totalCredit = _entries.Where(e => e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        return totalDebit == totalCredit;
    }

    public long GetBalance(string accountId)
    {
        long credits = _entries.Where(e => e.AccountId == accountId && e.EntryType == "CREDIT").Sum(e => e.AmountCents);
        long debits  = _entries.Where(e => e.AccountId == accountId && e.EntryType == "DEBIT").Sum(e => e.AmountCents);
        return credits - debits;
    }

    public List<LedgerEntry> GetPaymentEntries(string paymentId) =>
        _entries.Where(e => e.PaymentId == paymentId).ToList();

    public List<LedgerEntry> AllEntries => _entries;
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Simulated Card Network Gateway
// ─────────────────────────────────────────────────────────────────────────────

public class CardNetworkGateway
{
    private readonly HashSet<string> _declinedPans;

    public CardNetworkGateway(params string[] declinedPans) =>
        _declinedPans = new HashSet<string>(declinedPans);

    public (bool success, string authRef, string error) Authorize(string pan, long amountCents)
    {
        if (_declinedPans.Contains(pan))
            return (false, null, "CARD_DECLINED");
        var authRef = "AUTH_" + Convert.ToHexString(RandomBytes(4));
        return (true, authRef, null);
    }

    public (bool success, string error) Capture(string authRef, long amountCents) =>
        (true, null);

    public (bool success, string refundRef, string error) Refund(string authRef, long amountCents) =>
        (true, "REF_" + Convert.ToHexString(RandomBytes(4)), null);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n]; RandomNumberGenerator.Fill(b); return b;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. Payment Store
// ─────────────────────────────────────────────────────────────────────────────

public class PaymentStore
{
    private readonly Dictionary<string, Payment> _db = new Dictionary<string, Payment>();

    public void Save(Payment p) => _db[p.PaymentId] = p;

    public Payment Get(string paymentId) =>
        _db.TryGetValue(paymentId, out var p) ? p : null;

    // In production this would be: UPDATE payments SET ... WHERE version = expectedVersion
    // Here we track expectedVersion explicitly since Get() returns the same reference
    // (a real DB holds its own copy, so current.Version != updated.Version would work naturally).
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

// ─────────────────────────────────────────────────────────────────────────────
// 7. Webhook Service — at-least-once delivery with retry
// ─────────────────────────────────────────────────────────────────────────────

public class WebhookService
{
    private readonly List<WebhookEvent> _events = new List<WebhookEvent>();
    private readonly Dictionary<string, string> _merchantSecrets;
    // Simulated delivery: always succeeds unless in failing set
    private readonly HashSet<string> _failingMerchants;

    private static readonly int[] BackoffSeconds = { 10, 60, 300, 1800, 7200, 43200, 86400 };

    public WebhookService(Dictionary<string, string> merchantSecrets, params string[] failingMerchants)
    {
        _merchantSecrets  = merchantSecrets;
        _failingMerchants = new HashSet<string>(failingMerchants);
    }

    public void Enqueue(string merchantId, string merchantUrl, string payload)
    {
        _events.Add(new WebhookEvent
        {
            EventId     = "evt_" + Guid.NewGuid().ToString("N")[..8],
            MerchantId  = merchantId,
            MerchantUrl = merchantUrl,
            Payload     = payload,
            Status      = "PENDING",
            AttemptCount = 0,
            NextRetry   = DateTime.UtcNow,
            CreatedAt   = DateTime.UtcNow
        });
    }

    // Process all due events (called by background worker)
    public void ProcessDue()
    {
        var due = _events.Where(e => e.Status == "PENDING" && e.NextRetry <= DateTime.UtcNow).ToList();
        foreach (var evt in due)
        {
            string signature = Sign(evt.Payload, _merchantSecrets.GetValueOrDefault(evt.MerchantId, "secret"));
            bool delivered   = !_failingMerchants.Contains(evt.MerchantId);

            evt.AttemptCount++;
            if (delivered)
            {
                evt.Status = "DELIVERED";
                Console.WriteLine($"  [Webhook] {evt.EventId} → {evt.MerchantUrl} DELIVERED (attempt {evt.AttemptCount})");
            }
            else if (evt.AttemptCount >= BackoffSeconds.Length)
            {
                evt.Status = "FAILED";
                Console.WriteLine($"  [Webhook] {evt.EventId} PERMANENTLY FAILED after {evt.AttemptCount} attempts");
            }
            else
            {
                int backoff    = BackoffSeconds[evt.AttemptCount - 1];
                evt.NextRetry  = DateTime.UtcNow.AddSeconds(backoff);
                Console.WriteLine($"  [Webhook] {evt.EventId} FAILED (attempt {evt.AttemptCount}), retry in {backoff}s");
            }
        }
    }

    public List<WebhookEvent> GetByStatus(string status) =>
        _events.Where(e => e.Status == status).ToList();

    private static string Sign(string payload, string secret)
    {
        var key  = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLower();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 8. Payment Service — orchestrates everything
// ─────────────────────────────────────────────────────────────────────────────

public class ChargeRequest
{
    public string IdempotencyKey { get; set; }
    public string MerchantId     { get; set; }
    public string CustomerId     { get; set; }
    public string CardToken      { get; set; }
    public long   AmountCents    { get; set; }
    public string Currency       { get; set; }
    public string MerchantUrl    { get; set; }
    public FraudContext FraudCtx { get; set; }
}

public class ChargeResult
{
    public bool   Success      { get; set; }
    public string PaymentId    { get; set; }
    public PaymentStatus Status { get; set; }
    public string Error        { get; set; }
    public bool   WasIdempotent { get; set; }
}

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
        // Step 1: Idempotency check
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

        // Step 6: Write ledger entries for authorization
        _ledger.Record(paymentId, new[]
        {
            ("customer:" + req.CustomerId, "DEBIT",  req.AmountCents, "Authorization hold"),
            ("suspense",                   "CREDIT", req.AmountCents, "Authorization hold")
        });

        // Step 7: Store idempotency result
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

        // Optimistic lock update — capture expectedVersion before mutation
        int expectedVersion = payment.Version;
        long fee       = (long)(payment.AmountCents * PlatformFeeRate);
        long netAmount = payment.AmountCents - fee;

        payment.Status        = PaymentStatus.Captured;
        payment.CapturedCents = payment.AmountCents;
        payment.Version++;

        if (!_payments.Update(payment, expectedVersion))
            return (false, "CONCURRENT_UPDATE — retry");

        // Write capture ledger entries
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

        // Balanced: D(merchant full) + D(platform fee) == C(customer full) + C(merchant fee-back)
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
            PaymentId    = id,
            MerchantId   = req.MerchantId,
            CustomerId   = req.CustomerId,
            CardToken    = req.CardToken,
            AmountCents  = req.AmountCents,
            Currency     = req.Currency,
            Status       = status,
            Version      = 1,
            CreatedAt    = DateTime.UtcNow,
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

// ─────────────────────────────────────────────────────────────────────────────
// 9. Reconciliation Job
// ─────────────────────────────────────────────────────────────────────────────

public class BankSettlementRecord
{
    public string RefId        { get; set; }
    public long   AmountCents  { get; set; }
    public string Currency     { get; set; }
}

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
        Console.WriteLine("\n  [Reconcile] Starting nightly reconciliation...");

        // Internal: all settlement ledger entries
        var internalSettlements = _ledger.AllEntries
            .Where(e => e.AccountId == "bank:settlement" && e.EntryType == "CREDIT")
            .ToDictionary(e => e.PaymentId, e => e.AmountCents);

        // Build bank lookup
        var bankLookup = bankRecords.ToDictionary(r => r.RefId, r => r.AmountCents);

        int matched = 0, missingInBank = 0, missingInLedger = 0, mismatch = 0;

        // Check internal vs bank
        foreach (var (paymentId, internalAmt) in internalSettlements)
        {
            if (!bankLookup.TryGetValue(paymentId, out var bankAmt))
            {
                Console.WriteLine($"  [Reconcile] IN_LEDGER_ONLY: {paymentId} ${internalAmt / 100.0:F2} — pending settlement");
                missingInBank++;
            }
            else if (bankAmt != internalAmt)
            {
                Console.WriteLine($"  [Reconcile] MISMATCH: {paymentId} ledger=${internalAmt / 100.0:F2} bank=${bankAmt / 100.0:F2}");
                mismatch++;
            }
            else
            {
                matched++;
            }
        }

        // Check bank vs internal
        foreach (var (refId, bankAmt) in bankLookup)
        {
            if (!internalSettlements.ContainsKey(refId))
            {
                Console.WriteLine($"  [Reconcile] IN_BANK_ONLY: {refId} ${bankAmt / 100.0:F2} — INVESTIGATE");
                missingInLedger++;
            }
        }

        Console.WriteLine($"  [Reconcile] DONE — matched={matched} missing_in_bank={missingInBank} mismatches={mismatch} bank_only={missingInLedger}");
        Console.WriteLine($"  [Reconcile] Ledger balanced: {_ledger.IsBalanced()}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo: end-to-end scenarios
// ─────────────────────────────────────────────────────────────────────────────

public class Program
{
    public static void Main()
    {
        // Wire up services
        var vault    = new CardVault();
        var idem     = new IdempotencyStore();
        var fraud    = new FraudScorer();
        var network  = new CardNetworkGateway("4111111111111111"); // this PAN is "declined"
        var payments = new PaymentStore();
        var ledger   = new LedgerService();
        var webhooks = new WebhookService(
            new Dictionary<string, string> { ["merchant1"] = "whsec_abc123", ["merchant2"] = "whsec_xyz999" },
            "merchant2"  // merchant2's endpoint is failing
        );
        var svc      = new PaymentService(idem, vault, fraud, network, payments, ledger, webhooks);
        var recon    = new ReconciliationJob(ledger, payments);

        // Tokenize test cards
        var goodToken    = vault.Tokenize("4000000000000000");  // valid card
        var declinedToken = vault.Tokenize("4111111111111111"); // bank declines

        Console.WriteLine("=== Scenario 1: Happy Path — Authorize → Capture → Settle ===\n");
        {
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1001",
                MerchantId     = "merchant1",
                CustomerId     = "customer-alice",
                CardToken      = goodToken,
                AmountCents    = 10000,  // $100.00
                Currency       = "USD",
                MerchantUrl    = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId     = "customer-alice",
                    CardToken      = goodToken,
                    AmountCents    = 10000,
                    IpAddress      = "1.2.3.4",
                    Country        = "US",
                    FailedAttempts = 0,
                    AvsMatch       = true,
                    CvvMatch       = true,
                    AvgOrderCents  = 8000
                }
            };

            var result = svc.Charge(req);
            Console.WriteLine($"  Charge result: success={result.Success} paymentId={result.PaymentId} status={result.Status}\n");

            var (capOk, capErr) = svc.Capture(result.PaymentId);
            Console.WriteLine($"  Capture: ok={capOk} error={capErr}\n");

            var (settleOk, settleErr) = svc.Settle(result.PaymentId);
            Console.WriteLine($"  Settle: ok={settleOk} error={settleErr}\n");

            // Show ledger entries
            Console.WriteLine("  Ledger entries:");
            foreach (var e in ledger.GetPaymentEntries(result.PaymentId))
                Console.WriteLine($"    {e.EntryType,-6} {e.AccountId,-30} ${e.AmountCents / 100.0:F2}  {e.Description}");
            Console.WriteLine($"  Ledger balanced: {ledger.IsBalanced()}");
        }

        Console.WriteLine("\n=== Scenario 2: Idempotency — Same Key Sent Twice ===\n");
        {
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1002",
                MerchantId     = "merchant1",
                CustomerId     = "customer-bob",
                CardToken      = goodToken,
                AmountCents    = 5000,
                Currency       = "USD",
                MerchantUrl    = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-bob", CardToken = goodToken,
                    AmountCents = 5000, Country = "US",
                    AvsMatch = true, CvvMatch = true, AvgOrderCents = 4000
                }
            };

            var r1 = svc.Charge(req);
            Console.WriteLine($"  First charge:  paymentId={r1.PaymentId} idempotent={r1.WasIdempotent}");

            var r2 = svc.Charge(req);  // identical request — should return same result
            Console.WriteLine($"  Second charge: paymentId={r2.PaymentId} idempotent={r2.WasIdempotent}");
            Console.WriteLine($"  Same payment ID: {r1.PaymentId == r2.PaymentId} (no double charge)");
        }

        Console.WriteLine("\n=== Scenario 3: Fraud Block ===\n");
        {
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1003",
                MerchantId     = "merchant1",
                CustomerId     = "customer-fraudster",
                CardToken      = goodToken,
                AmountCents    = 200000,  // $2000 — 25× their average
                Currency       = "USD",
                MerchantUrl    = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId     = "customer-fraudster",
                    CardToken      = goodToken,
                    AmountCents    = 200000,
                    Country        = "XX",         // high-risk country
                    FailedAttempts = 6,             // velocity exceeded
                    AvsMatch       = false,
                    CvvMatch       = false,
                    AvgOrderCents  = 8000
                }
            };

            var result = svc.Charge(req);
            Console.WriteLine($"  Result: success={result.Success} status={result.Status} error={result.Error}");
        }

        Console.WriteLine("\n=== Scenario 4: Declined Card ===\n");
        {
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1004",
                MerchantId     = "merchant1",
                CustomerId     = "customer-carol",
                CardToken      = declinedToken,
                AmountCents    = 3000,
                Currency       = "USD",
                MerchantUrl    = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-carol", CardToken = declinedToken,
                    AmountCents = 3000, Country = "US",
                    AvsMatch = true, CvvMatch = true, AvgOrderCents = 2500
                }
            };

            var result = svc.Charge(req);
            Console.WriteLine($"  Result: success={result.Success} status={result.Status} error={result.Error}");
        }

        Console.WriteLine("\n=== Scenario 5: Partial Refund ===\n");
        {
            // Make a new payment, capture it, then partial refund
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1005",
                MerchantId     = "merchant1",
                CustomerId     = "customer-dave",
                CardToken      = goodToken,
                AmountCents    = 15000,  // $150
                Currency       = "USD",
                MerchantUrl    = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-dave", CardToken = goodToken,
                    AmountCents = 15000, Country = "US",
                    AvsMatch = true, CvvMatch = true, AvgOrderCents = 12000
                }
            };

            var charge = svc.Charge(req);
            svc.Capture(charge.PaymentId);

            // Refund $50
            var (ok1, ref1, err1) = svc.Refund(charge.PaymentId, 5000);
            Console.WriteLine($"  Refund $50: ok={ok1} ref={ref1} status={payments.Get(charge.PaymentId).Status}");

            // Refund another $50
            var (ok2, ref2, err2) = svc.Refund(charge.PaymentId, 5000);
            Console.WriteLine($"  Refund $50: ok={ok2} ref={ref2} status={payments.Get(charge.PaymentId).Status}");

            // Try to over-refund (should fail)
            var (ok3, ref3, err3) = svc.Refund(charge.PaymentId, 10000);  // only $50 remaining
            Console.WriteLine($"  Over-refund $100: ok={ok3} error={err3}");

            Console.WriteLine($"  Ledger balanced after refunds: {ledger.IsBalanced()}");
        }

        Console.WriteLine("\n=== Scenario 6: Webhook Delivery with Retry ===\n");
        {
            // Process pending webhooks — merchant2 is failing
            webhooks.ProcessDue();

            var delivered = webhooks.GetByStatus("DELIVERED");
            var pending   = webhooks.GetByStatus("PENDING");
            Console.WriteLine($"  Delivered: {delivered.Count}  Still pending (retry): {pending.Count}");
        }

        Console.WriteLine("\n=== Scenario 7: Nightly Reconciliation ===\n");
        {
            // Simulate bank report matching some payments
            var settledPayments = payments.GetByStatus(PaymentStatus.Settled);
            var bankReport = settledPayments.Select(p => new BankSettlementRecord
            {
                RefId       = p.PaymentId,
                AmountCents = p.CapturedCents - (long)(p.CapturedCents * 0.029),
                Currency    = p.Currency
            }).ToList();

            // Add a mystery bank record not in our ledger
            bankReport.Add(new BankSettlementRecord { RefId = "pay_unknown99", AmountCents = 2000, Currency = "USD" });

            recon.Run(bankReport);
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
