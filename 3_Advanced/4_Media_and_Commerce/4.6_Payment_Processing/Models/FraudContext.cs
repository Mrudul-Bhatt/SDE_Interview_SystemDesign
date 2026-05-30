// FraudContext — the signal bundle passed to FraudScorer.
//
// Most of these fields come from sources OUTSIDE the payment flow itself:
//   - FailedAttempts: from a separate velocity-tracking service (Redis counters)
//   - AvgOrderCents: from the customer's historical spend
//   - AvsMatch / CvvMatch: returned by the card network during pre-auth check
//   - IpAddress / Country: from the API request headers
//
// Keeping fraud inputs in a dedicated context type lets FraudScorer evolve
// independently without touching the ChargeRequest API.

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
