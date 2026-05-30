// Trip — durable record of one ride through its lifecycle.
//
// SurgeMultiplier is captured at REQUEST TIME and frozen on the trip — the
// rider was shown that multiplier before confirming, so we charge with that
// multiplier even if the surge has dropped by the time the trip completes.
//
// EstimatedFare is what the rider sees up front; FinalFare is computed from
// the ACTUAL distance/duration on completion. The two can differ if the route
// or traffic ended up different from the estimate, but the surge stays locked.

using System;

public class Trip
{
    public string     TripId           { get; set; }
    public string     RiderId          { get; set; }
    public string     DriverId         { get; set; }
    public GeoPoint   PickupLocation   { get; set; }
    public GeoPoint   DropoffLocation  { get; set; }
    public TripStatus Status           { get; set; }
    public DateTime   RequestedAt      { get; set; }
    public DateTime?  AssignedAt       { get; set; }
    public DateTime?  StartedAt        { get; set; }
    public DateTime?  CompletedAt      { get; set; }
    public double     SurgeMultiplier  { get; set; }
    public double     EstimatedFare    { get; set; }
    public double     FinalFare        { get; set; }
    public double     DistanceKm       { get; set; }
    public double     DurationMinutes  { get; set; }
    public int        DriverRating     { get; set; }
    public int        RiderRating      { get; set; }
}
