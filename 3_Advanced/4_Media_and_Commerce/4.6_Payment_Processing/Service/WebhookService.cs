// WebhookService — at-least-once delivery of payment events to merchants, with backoff + signing.
//
// THE BIG IDEA:
// When a payment changes state, the merchant needs to know (to fulfill the order). Their endpoint
// may be down, so we persist the event first, then retry delivery on an exponential backoff until
// it succeeds or we give up. "At-least-once" means a merchant may receive an event more than once
// — they dedupe on EventId.
//
// WHY ENQUEUE PERSISTS BEFORE ANY HTTP: if we attempted the POST first and crashed before recording
// the event, the merchant would silently never learn the payment changed. Persist-then-deliver
// guarantees no event is ever lost — only possibly delivered late or twice.
//
// WHY EXPONENTIAL BACKOFF (10s -> 1m -> 5m -> 30m -> 2h -> 12h -> 24h): aggressive early retries
// ride out transient blips; widening gaps avoid hammering an endpoint that's down for hours. After
// 7 attempts (~3 days) the event is FAILED and the merchant can replay it from their dashboard.
//
// WHY HMAC-SHA256 SIGNING: the signature proves the webhook really came from us. Without it an
// attacker could forge a "payment.captured" to trick a merchant into shipping an unpaid order.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 6: merchant2's endpoint is failing):
//
//   ProcessDue() picks events with NextRetry <= now, then per event:
//   Event for         | outcome   | Status    | AttemptCount | NextRetry
//   ------------------|-----------|-----------|--------------|--------------------
//   merchant1 (ok)    | delivered | DELIVERED | 1            | (done)
//   merchant2 (down)  | failed    | PENDING   | 1            | now + 10s (1st backoff)
//
//   merchant2's event stays PENDING and is retried on later ProcessDue calls, widening each time,
//   until it delivers or hits attempt 7 -> FAILED.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public class WebhookService
{
    private readonly List<WebhookEvent> _events = [];
    private readonly Dictionary<string, string> _merchantSecrets;   // merchantId -> HMAC signing key
    private readonly HashSet<string> _failingMerchants;             // simulation: these always fail

    // Backoff after attempt N (seconds). Length also caps the retry count (7 attempts).
    private static readonly int[] BackoffSeconds = [10, 60, 300, 1800, 7200, 43200, 86400];

    public WebhookService(Dictionary<string, string> merchantSecrets, params string[] failingMerchants)
    {
        _merchantSecrets  = merchantSecrets;
        _failingMerchants = [.. failingMerchants];
    }

    // Persist the event as PENDING, due immediately. Delivery happens later in ProcessDue.
    public void Enqueue(string merchantId, string merchantUrl, string payload)
    {
        _events.Add(new WebhookEvent
        {
            EventId      = "evt_" + Guid.NewGuid().ToString("N")[..8],
            MerchantId   = merchantId,
            MerchantUrl  = merchantUrl,
            Payload      = payload,
            Status       = "PENDING",
            AttemptCount = 0,
            NextRetry    = DateTime.UtcNow,   // due now
            CreatedAt    = DateTime.UtcNow
        });
    }

    // Background worker tick: try every event whose retry time has arrived.
    public void ProcessDue()
    {
        var due = _events.Where(e => e.Status == "PENDING" && e.NextRetry <= DateTime.UtcNow).ToList();
        foreach (var evt in due)
        {
            // Sign every attempt (the merchant verifies this). Delivery is simulated by the set.
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
                // Exhausted all retries -> give up; merchant must replay manually.
                evt.Status = "FAILED";
                Console.WriteLine($"  [Webhook] {evt.EventId} PERMANENTLY FAILED after {evt.AttemptCount} attempts");
            }
            else
            {
                // Schedule the next attempt further out.
                int backoff    = BackoffSeconds[evt.AttemptCount - 1];
                evt.NextRetry  = DateTime.UtcNow.AddSeconds(backoff);
                Console.WriteLine($"  [Webhook] {evt.EventId} FAILED (attempt {evt.AttemptCount}), retry in {backoff}s");
            }
        }
    }

    public List<WebhookEvent> GetByStatus(string status) =>
        _events.Where(e => e.Status == status).ToList();

    // HMAC-SHA256 over the payload with the merchant's secret — the authenticity proof.
    private static string Sign(string payload, string secret)
    {
        var key  = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLower();
    }
}
