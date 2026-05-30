// Driver — profile + ranking signals.
//
// Rating and AcceptanceRate live here because they're slow-changing per-driver
// attributes used by Dispatch ranking. Status and live Location are the FAST-
// changing parts; in production they live in Redis (DriverStateStore +
// DriverLocationStore here) and are NOT kept in sync with this object — the
// stores are authoritative for real-time state.

public class Driver
{
    public string       DriverId       { get; set; }
    public string       Name           { get; set; }
    public DriverStatus Status         { get; set; }
    public GeoPoint     Location       { get; set; }
    public double       Rating         { get; set; }
    public double       AcceptanceRate { get; set; }  // 0–1
    public string       PendingTripId  { get; set; }  // trip offered but not yet accepted
}
