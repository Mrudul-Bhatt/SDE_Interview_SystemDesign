// WebhookEvent — one merchant notification queued for at-least-once delivery.
//
// THE BIG IDEA:
// After a payment changes state, we notify the merchant by POSTing to their URL. Their endpoint
// may be down, so delivery is retried with exponential backoff — meaning a merchant might receive
// the same event more than once (at-least-once). EventId is what they dedupe on to stay correct.
//
// WHY NextRetry + AttemptCount: together they drive the backoff schedule — each failed attempt
// bumps AttemptCount and pushes NextRetry further out, so a flapping endpoint isn't hammered.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 6: merchant2's endpoint is failing):
//
//   Outcome           | Status    | AttemptCount | NextRetry
//   ------------------|-----------|--------------|---------------------
//   delivered (m1)    | DELIVERED | 1            | null (done)
//   failed once (m2)  | PENDING   | 1            | now + backoff (will retry)
//
//   ProcessDue() only picks up events whose NextRetry is in the past, so retries are paced.

using System;

public class WebhookEvent
{
    public string EventId { get; set; }   // stable id; the merchant dedupes on this
    public string MerchantId { get; set; }
    public string MerchantUrl { get; set; }
    public string Payload { get; set; }   // JSON body, e.g. {"event":"payment.authorized",...}
    public string Status { get; set; }   // PENDING, DELIVERED, FAILED
    public int AttemptCount { get; set; }   // delivery tries so far; grows the backoff
    public DateTime? NextRetry { get; set; }   // when to try next; null once DELIVERED/FAILED
    public DateTime CreatedAt { get; set; }
}
