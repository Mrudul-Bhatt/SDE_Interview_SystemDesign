// TripService — the trip state machine.
//
// Request:  capture surge multiplier (FROZEN on this trip), record demand
//           for surge math, persist trip in Requested state, then hand off
//           to Dispatch. If dispatch fails → Failed terminal state.
//
// StartTrip:  guard transitions Requested → InProgress only from
//             DriverAssigned. In production this is also gated by GPS
//             proximity (driver must be within 100m of pickup) to prevent
//             mileage fraud.
//
// CompleteTrip: compute final fare from ACTUAL distance/duration, release
//               the driver back to Available.
//
// CancelTrip: cancellation policy in effect — free before driver assigned or
//             within 2 minutes after, $5 fee after that. Driver returns to
//             Available regardless.
//
// RateTrip: ratings only allowed on Completed trips. Drivers and riders rate
//           each other; per-driver rating averages feed back into the
//           Dispatch ranking score.

using System;

public class TripService
{
    private readonly TripStore           _trips;
    private readonly DispatchService     _dispatch;
    private readonly SurgePricingService _surge;
    private readonly DriverStateStore    _driverState;
    private readonly DriverLocationStore _driverLoc;

    public TripService(TripStore trips, DispatchService dispatch, SurgePricingService surge,
        DriverStateStore driverState, DriverLocationStore driverLoc)
    {
        _trips       = trips;
        _dispatch    = dispatch;
        _surge       = surge;
        _driverState = driverState;
        _driverLoc   = driverLoc;
    }

    public Trip Request(string riderId, GeoPoint pickup, GeoPoint dropoff,
        bool simulateDeclines = false, int declineCount = 0)
    {
        // Record demand for surge
        _surge.RecordRequest(pickup);

        double surgeMultiplier = _surge.GetSurge(pickup);
        double estimated       = PricingService.Estimate(pickup, dropoff, surgeMultiplier);

        var trip = new Trip
        {
            TripId          = "trip_" + Guid.NewGuid().ToString("N")[..8],
            RiderId         = riderId,
            PickupLocation  = pickup,
            DropoffLocation = dropoff,
            Status          = TripStatus.Requested,
            RequestedAt     = DateTime.UtcNow,
            SurgeMultiplier = surgeMultiplier,
            EstimatedFare   = estimated
        };
        _trips.Save(trip);
        Console.WriteLine($"  [Trip] {trip.TripId} REQUESTED by {riderId}  surge={surgeMultiplier:F1}×  est=${estimated:F2}");

        // Dispatch
        var result = _dispatch.Dispatch(trip, simulateDeclines, declineCount);
        if (!result.Success)
        {
            trip.Status = TripStatus.Failed;
            _trips.Save(trip);
            Console.WriteLine($"  [Trip] {trip.TripId} FAILED — {result.Error}");
            return trip;
        }

        trip.DriverId   = result.DriverId;
        trip.Status     = TripStatus.DriverAssigned;
        trip.AssignedAt = DateTime.UtcNow;
        _trips.Save(trip);
        Console.WriteLine($"  [Trip] {trip.TripId} DRIVER_ASSIGNED → {result.DriverId}  pickup_ETA={result.EtaMinutes:F1}min");
        return trip;
    }

    public bool StartTrip(string tripId)
    {
        var trip = _trips.Get(tripId);
        if (trip == null || trip.Status != TripStatus.DriverAssigned) return false;

        trip.Status    = TripStatus.InProgress;
        trip.StartedAt = DateTime.UtcNow;
        _trips.Save(trip);
        Console.WriteLine($"  [Trip] {tripId} IN_PROGRESS — rider picked up");
        return true;
    }

    public Trip CompleteTrip(string tripId, double actualDistanceKm, double actualDurationMin)
    {
        var trip = _trips.Get(tripId);
        if (trip == null || trip.Status != TripStatus.InProgress) return trip;

        trip.DistanceKm     = actualDistanceKm;
        trip.DurationMinutes = actualDurationMin;
        trip.FinalFare      = PricingService.Calculate(actualDistanceKm, actualDurationMin, trip.SurgeMultiplier);
        trip.Status         = TripStatus.Completed;
        trip.CompletedAt    = DateTime.UtcNow;
        _trips.Save(trip);

        // Driver goes back to available
        _driverState.SetAvailable(trip.DriverId);
        Console.WriteLine($"  [Trip] {tripId} COMPLETED  dist={actualDistanceKm:F1}km  dur={actualDurationMin:F0}min  fare=${trip.FinalFare:F2}");
        return trip;
    }

    public bool CancelTrip(string tripId, string cancelledBy)
    {
        var trip = _trips.Get(tripId);
        if (trip == null) return false;
        if (trip.Status == TripStatus.Completed || trip.Status == TripStatus.Cancelled) return false;

        // Late cancel rule: rider cancelling more than 2 minutes after driver assigned
        bool isLateCancel = trip.DriverId != null &&
                            (DateTime.UtcNow - trip.AssignedAt.GetValueOrDefault()).TotalMinutes > 2;
        double fee = (cancelledBy == "rider" && isLateCancel) ? 5.00 : 0;

        trip.Status    = TripStatus.Cancelled;
        trip.FinalFare = fee;
        _trips.Save(trip);

        if (trip.DriverId != null)
            _driverState.SetAvailable(trip.DriverId);

        Console.WriteLine($"  [Trip] {tripId} CANCELLED by {cancelledBy}  cancellation_fee=${fee:F2}");
        return true;
    }

    public void RateTrip(string tripId, int driverRating, int riderRating)
    {
        var trip = _trips.Get(tripId);
        if (trip == null || trip.Status != TripStatus.Completed) return;
        trip.DriverRating = driverRating;
        trip.RiderRating  = riderRating;
        _trips.Save(trip);
        Console.WriteLine($"  [Trip] {tripId} rated — driver={driverRating}★  rider={riderRating}★");
    }
}
