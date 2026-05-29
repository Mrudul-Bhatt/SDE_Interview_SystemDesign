// FraudScorer — rule-based fraud scoring with a 0-100 risk score.
//
// Rules add to the score; the score buckets the decision:
//   < 60  → Allow
//   60-79 → Review (queue for manual / 24h hold)
//   ≥ 80  → Block
//
// Important: this returns a generic Block/Review/Allow decision. We deliberately
// do NOT surface the specific reasons to the customer in production — telling a
// fraudster "you failed AVS" helps them tune their next attempt. Reasons are
// for internal logs only.
//
// Real systems stack an ML model on top of these rules for the gray zone; this
// implementation keeps it deterministic for clarity.

using System.Collections.Generic;

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

        score = System.Math.Min(score, 100);

        FraudDecision decision = score < 60 ? FraudDecision.Allow
                               : score < 80 ? FraudDecision.Review
                               :              FraudDecision.Block;

        return (decision, score, reasons);
    }
}
