// FraudScorer — rule-based risk scoring that gates every charge before the bank is contacted.
//
// THE BIG IDEA:
// Each suspicious signal adds points to a 0-100 risk score; the total buckets into a decision.
// Cheap, deterministic, and explainable — it runs before the slow card-network call, so obvious
// fraud is stopped without ever touching the bank. Real systems stack an ML model on top for the
// gray zone; these hard rules stay as the fast, predictable floor.
//
//   score < 60  -> Allow    (proceed to authorize)
//   60..79      -> Review   (hold for manual / 24h review)
//   score >= 80 -> Block    (reject immediately)
//
// WHY REASONS ARE INTERNAL-ONLY: the customer is told a generic "declined", never "you failed
// CVV". Telling a fraudster exactly which check tripped just helps them tune the next attempt.
// Reasons are returned for our logs, not the API response.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 3: the fraudster — every rule fires):
//
//   Signal                          | rule                   | + score
//   --------------------------------|------------------------|--------
//   CvvMatch=false                  | CVV mismatch           |  +40
//   AvsMatch=false                  | AVS mismatch           |  +20
//   Country="XX"                    | high-risk country      |  +30
//   FailedAttempts=6                | velocity (5+ in 10min) |  +50
//   Amount 200000 > 10x avg 8000    | amount anomaly         |  +25
//   --------------------------------|------------------------|--------
//                                   | sum 165 -> capped 100  | Block (>= 80)
//
//   By contrast Scenario 1 (AVS+CVV ok, US, 0 failures, normal amount) scores 0 -> Allow.

using System.Collections.Generic;

public class FraudScorer
{
    // Simulated high-risk list; a real one holds actual ISO country codes tuned from fraud data.
    private static readonly HashSet<string> HighRiskCountries = ["XX", "ZZ"];

    public (FraudDecision decision, int score, List<string> reasons) Score(FraudContext ctx)
    {
        int score = 0;
        var reasons = new List<string>();

        // Verification failures — the card network's AVS/CVV checks. CVV weighs most because a
        // mismatch strongly suggests the card isn't physically present / not the real holder.
        if (!ctx.CvvMatch) { score += 40; reasons.Add("CVV mismatch"); }
        if (!ctx.AvsMatch) { score += 20; reasons.Add("AVS mismatch"); }
        if (HighRiskCountries.Contains(ctx.Country)) { score += 30; reasons.Add("High-risk country"); }

        // Velocity — many recent failures on this card is the classic "card testing" pattern.
        if (ctx.FailedAttempts >= 5) { score += 50; reasons.Add("Velocity: 5+ failures in 10min"); }

        // Amount anomaly — a charge wildly above the customer's norm is a takeover red flag.
        if (ctx.AvgOrderCents > 0 && ctx.AmountCents > ctx.AvgOrderCents * 10)
        {
            score += 25;
            reasons.Add($"Amount 10x customer average (${ctx.AmountCents / 100.0:F2} vs avg ${ctx.AvgOrderCents / 100.0:F2})");
        }

        score = System.Math.Min(score, 100);  // cap so one charge can't exceed the scale

        FraudDecision decision = score < 60 ? FraudDecision.Allow
                               : score < 80 ? FraudDecision.Review
                               : FraudDecision.Block;

        return (decision, score, reasons);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT THIS CLASS HOLDS AT RUNTIME:
    //
    // Almost nothing — FraudScorer is STATELESS. Its only field is a fixed lookup of high-risk
    // countries; it stores no per-charge data and remembers nothing between calls:
    //
    //   HighRiskCountries (static set) = { "XX", "ZZ" }
    //
    // Each Score(ctx) call computes a fresh result purely from the FraudContext passed in and
    // returns it — same input always gives the same output (deterministic). Nothing is mutated.
    //
    // Example return for Scenario 3 (the fraudster):
    //   ( decision = Block,
    //     score    = 100,                                  // 40+20+30+50+25 = 165, capped at 100
    //     reasons  = [ "CVV mismatch", "AVS mismatch",
    //                  "High-risk country",
    //                  "Velocity: 5+ failures in 10min",
    //                  "Amount 10x customer average ..." ] )
    //
    // Holding no state is why one shared FraudScorer can score every charge concurrently and why
    // it's trivially safe to scale out — there's nothing to keep in sync.
    // ──────────────────────────────────────────────────────────────────────────────────
}
