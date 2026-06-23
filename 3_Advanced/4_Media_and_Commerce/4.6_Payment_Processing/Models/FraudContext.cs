// FraudContext — the bundle of signals handed to FraudScorer for one charge.
//
// THE BIG IDEA:
// Fraud decisions need more than the charge amount — they need behavioural and verification
// signals, most gathered OUTSIDE the payment flow: velocity counters (Redis), the customer's
// historical spend, and the card network's AVS/CVV check results. Bundling them in a dedicated
// type lets FraudScorer evolve its rules without changing the ChargeRequest API.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 3: a fraudster, $2000 charge — every rule fires):
//
//   Field in this context     | Rule it triggers       | + score
//   --------------------------|------------------------|--------
//   CvvMatch = false          | CVV mismatch           |  +40
//   AvsMatch = false          | AVS mismatch           |  +20
//   Country = "XX"            | high-risk country      |  +30
//   FailedAttempts = 6        | velocity (5+ in 10min) |  +50
//   AmountCents 200000 > 10x  | amount anomaly         |  +25
//     AvgOrderCents 8000      |                        |
//   --------------------------|------------------------|--------
//                             | total 165 -> capped 100 | Block (>= 80)

public class FraudContext
{
    public string CustomerId { get; set; }
    public string CardToken { get; set; }
    public long AmountCents { get; set; }
    public string IpAddress { get; set; }   // from request headers
    public string Country { get; set; }   // high-risk list adds score
    public int FailedAttempts { get; set; }   // recent failures for this card (velocity service)
    public bool AvsMatch { get; set; }   // address check from the card network
    public bool CvvMatch { get; set; }   // CVV check from the card network
    public long AvgOrderCents { get; set; }   // customer's historical average — flags anomalies
}
