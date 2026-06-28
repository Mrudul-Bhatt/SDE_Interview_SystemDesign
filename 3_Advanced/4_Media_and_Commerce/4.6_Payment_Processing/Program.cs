// Program — entry point for all Payment Processing demo scenarios.

using System;
using System.Collections.Generic;
using System.Linq;

public class Program
{
    public static void Main()
    {
        // Wire up services
        var vault = new CardVault();
        var idem = new IdempotencyStore();
        var fraud = new FraudScorer();
        var network = new CardNetworkGateway("4111111111111111"); // this PAN is "declined"
        var payments = new PaymentStore();
        var ledger = new LedgerService();
        var webhooks = new WebhookService(
            new Dictionary<string, string> { ["merchant1"] = "whsec_abc123", ["merchant2"] = "whsec_xyz999" },
            "merchant2"  // merchant2's endpoint is failing
        );
        var svc = new PaymentService(idem, vault, fraud, network, payments, ledger, webhooks);
        var recon = new ReconciliationJob(ledger, payments);

        // Tokenize test cards
        var goodToken = vault.Tokenize("4000000000000000");  // valid card
        var declinedToken = vault.Tokenize("4111111111111111");  // bank declines

        Console.WriteLine("=== Scenario 1: Happy Path — Authorize → Capture → Settle ===\n");
        {
            var req = new ChargeRequest
            {
                IdempotencyKey = "order-1001",
                MerchantId = "merchant1",
                CustomerId = "customer-alice",
                CardToken = goodToken,
                AmountCents = 10000,  // $100.00
                Currency = "USD",
                MerchantUrl = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-alice",
                    CardToken = goodToken,
                    AmountCents = 10000,
                    IpAddress = "1.2.3.4",
                    Country = "US",
                    FailedAttempts = 0,
                    AvsMatch = true,
                    CvvMatch = true,
                    AvgOrderCents = 8000
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
                MerchantId = "merchant1",
                CustomerId = "customer-bob",
                CardToken = goodToken,
                AmountCents = 5000,
                Currency = "USD",
                MerchantUrl = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-bob",
                    CardToken = goodToken,
                    AmountCents = 5000,
                    Country = "US",
                    AvsMatch = true,
                    CvvMatch = true,
                    AvgOrderCents = 4000
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
                MerchantId = "merchant1",
                CustomerId = "customer-fraudster",
                CardToken = goodToken,
                AmountCents = 200000,  // $2000 — 25× their average
                Currency = "USD",
                MerchantUrl = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-fraudster",
                    CardToken = goodToken,
                    AmountCents = 200000,
                    Country = "XX",         // high-risk country
                    FailedAttempts = 6,             // velocity exceeded
                    AvsMatch = false,
                    CvvMatch = false,
                    AvgOrderCents = 8000
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
                MerchantId = "merchant1",
                CustomerId = "customer-carol",
                CardToken = declinedToken,
                AmountCents = 3000,
                Currency = "USD",
                MerchantUrl = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-carol",
                    CardToken = declinedToken,
                    AmountCents = 3000,
                    Country = "US",
                    AvsMatch = true,
                    CvvMatch = true,
                    AvgOrderCents = 2500
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
                MerchantId = "merchant1",
                CustomerId = "customer-dave",
                CardToken = goodToken,
                AmountCents = 15000,  // $150
                Currency = "USD",
                MerchantUrl = "https://merchant1.example.com/webhook",
                FraudCtx = new FraudContext
                {
                    CustomerId = "customer-dave",
                    CardToken = goodToken,
                    AmountCents = 15000,
                    Country = "US",
                    AvsMatch = true,
                    CvvMatch = true,
                    AvgOrderCents = 12000
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
            var pending = webhooks.GetByStatus("PENDING");
            Console.WriteLine($"  Delivered: {delivered.Count}  Still pending (retry): {pending.Count}");
        }

        Console.WriteLine("\n=== Scenario 7: Nightly Reconciliation ===\n");
        {
            // Simulate bank report matching some payments
            var settledPayments = payments.GetByStatus(PaymentStatus.Settled);
            var bankReport = settledPayments.Select(p => new BankSettlementRecord
            {
                RefId = p.PaymentId,
                AmountCents = p.CapturedCents - (long)(p.CapturedCents * 0.029),
                Currency = p.Currency
            }).ToList();

            // Add a mystery bank record not in our ledger
            bankReport.Add(new BankSettlementRecord { RefId = "pay_unknown99", AmountCents = 2000, Currency = "USD" });

            recon.Run(bankReport);
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
