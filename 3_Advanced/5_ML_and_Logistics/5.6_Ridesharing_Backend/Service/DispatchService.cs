// DispatchService — finds the best driver for a trip via sequential offers.
//
// The flow:
//   1. GEORADIUS-style query for up to 20 nearby available drivers.
//   2. Rank by score = -(ETA × 0.7 - rating × 0.3) — closer is better, higher
//      rated is better. ETA dominates because riders care most about pickup.
//   3. For each ranked candidate:
//        a. TryClaim atomically — if another dispatch grabbed them first, skip
//           and move on. Returns false if not Available.
//        b. Simulate offer accept/decline (15s window in production).
//        c. On accept: transition to OnTrip, return.
//        d. On decline: Release them back to Available, continue.
//   4. If no driver found in the initial radius, expand by 2km and retry.
//   5. Give up at MaxRadiusKm — return NO_DRIVERS_AVAILABLE.
//
// Sequential (not broadcast) is the right model here: it ensures only ONE
// driver is engaged with each trip at a time, avoiding race conditions and
// driver spam.

using System;
using System.Collections.Generic;
using System.Linq;

public class DispatchService
{
    private readonly DriverLocationStore _locations;
    private readonly DriverStateStore    _states;
    private readonly Dictionary<string, Driver> _drivers;

    private const double InitialRadiusKm = 3.0;
    private const double MaxRadiusKm     = 8.0;

    public DispatchService(DriverLocationStore locations, DriverStateStore states,
        Dictionary<string, Driver> drivers)
    {
        _locations = locations;
        _states    = states;
        _drivers   = drivers;
    }

    public DispatchResult Dispatch(Trip trip, bool simulateDeclines = false, int simulateDeclineCount = 0)
    {
        double radius  = InitialRadiusKm;
        int    attempt = 0;

        while (radius <= MaxRadiusKm)
        {
            var candidates = _locations.FindNearby(
                trip.PickupLocation, radius,
                id => _states.IsAvailable(id));

            // Rank by ETA + rating
            var ranked = candidates
                .Select(c =>
                {
                    var driverLoc = _locations.GetLocation(c.driverId);
                    double eta    = driverLoc != null
                        ? EtaService.EstimateMinutes(driverLoc, trip.PickupLocation)
                        : double.MaxValue;
                    double rating = _drivers.TryGetValue(c.driverId, out var d) ? d.Rating : 3.0;
                    double score  = -(eta * 0.7 - rating * 0.3);  // lower ETA + higher rating = better
                    return (c.driverId, eta, score);
                })
                .OrderByDescending(x => x.score)
                .ToList();

            foreach (var (driverId, eta, _) in ranked)
            {
                attempt++;

                // Atomic claim (SET NX simulation)
                bool claimed = _states.TryClaim(driverId, trip.TripId);
                if (!claimed)
                {
                    Console.WriteLine($"  [Dispatch] Driver {driverId} already claimed by another trip — skipping");
                    continue;
                }

                // Simulate driver decision: decline first N drivers if requested
                bool accepted = !(simulateDeclines && attempt <= simulateDeclineCount);

                if (accepted)
                {
                    _states.SetOnTrip(driverId, trip.TripId);
                    Console.WriteLine($"  [Dispatch] Driver {driverId} accepted (attempt {attempt}, ETA={eta:F1}min)");
                    return new DispatchResult { Success = true, DriverId = driverId, EtaMinutes = eta, AttemptsUsed = attempt };
                }
                else
                {
                    _states.Release(driverId);
                    Console.WriteLine($"  [Dispatch] Driver {driverId} declined (attempt {attempt})");
                }
            }

            // No driver found at this radius — expand
            radius += 2.0;
            Console.WriteLine($"  [Dispatch] No driver found within {radius - 2}km, expanding to {radius}km");
        }

        return new DispatchResult { Success = false, Error = "NO_DRIVERS_AVAILABLE", AttemptsUsed = attempt };
    }
}
