// WebhookService — at-least-once webhook delivery with exponential backoff.
//
// Enqueue ALWAYS persists the event first (in production: durable write to DB
// before any HTTP attempt). This is critical: if we crashed between the HTTP
// call and the write, the merchant would never know the payment status changed.
//
// Retry schedule (10s → 1min → 5min → 30min → 2h → 12h → 24h): aggressive at
// first to handle transient blips, then slow exponential growth. After 7
// attempts (~3 days) the event is marked FAILED — merchants can manually
// replay from their dashboard.
//
// HMAC-SHA256 over the payload lets merchants verify the webhook really came
// from us. Without it, an attacker could forge "payment.captured" events to
// trigger order fulfillment for unpaid orders.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public class WebhookService
{
    private readonly List<WebhookEvent> _events = new List<WebhookEvent>();
    private readonly Dictionary<string, string> _merchantSecrets;
    private readonly HashSet<string> _failingMerchants;  // simulation hook

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
