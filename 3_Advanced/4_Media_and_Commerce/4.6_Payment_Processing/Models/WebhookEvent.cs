// WebhookEvent — one notification queued for delivery to a merchant.
//
// EventId is what the merchant uses to dedupe on their side (since delivery is
// at-least-once). NextRetry + AttemptCount drive the exponential backoff retry
// schedule managed by WebhookService.

using System;

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
