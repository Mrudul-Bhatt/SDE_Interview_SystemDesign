// DispatchResult — outcome of one dispatch attempt.
//
// AttemptsUsed is useful for telemetry — if it's frequently > 5, our candidate
// ranking is poor (drivers we're offering to are declining a lot) or surge is
// too low to attract drivers. EtaMinutes is the predicted pickup ETA, shown to
// the rider as "Sam is 4 minutes away."

public class DispatchResult
{
    public bool   Success      { get; set; }
    public string DriverId     { get; set; }
    public double EtaMinutes   { get; set; }
    public int    AttemptsUsed { get; set; }
    public string Error        { get; set; }
}
