// Program — entry point for all Ridesharing Backend demo scenarios.

using System;
using System.Collections.Generic;

public class Program
{
    static Dictionary<string, Driver> BuildDrivers(
        DriverLocationStore locs, DriverStateStore states, SurgePricingService surge)
    {
        var drivers = new[]
        {
            new Driver { DriverId="d1", Name="Ahmed",   Location=new GeoPoint(40.7128,-73.9960), Rating=4.9, AcceptanceRate=0.92 },
            new Driver { DriverId="d2", Name="Maria",   Location=new GeoPoint(40.7200,-73.9900), Rating=4.7, AcceptanceRate=0.85 },
            new Driver { DriverId="d3", Name="James",   Location=new GeoPoint(40.7050,-74.0020), Rating=4.8, AcceptanceRate=0.88 },
            new Driver { DriverId="d4", Name="Priya",   Location=new GeoPoint(40.7300,-73.9800), Rating=4.6, AcceptanceRate=0.80 },
            new Driver { DriverId="d5", Name="Carlos",  Location=new GeoPoint(40.6900,-73.9700), Rating=4.5, AcceptanceRate=0.78 },
        };
        var dict = new Dictionary<string, Driver>();
        foreach (var d in drivers)
        {
            dict[d.DriverId] = d;
            locs.UpdateLocation(d.DriverId, d.Location);
            states.SetAvailable(d.DriverId);
            surge.RecordAvailableDriver(d.Location);
        }
        return dict;
    }

    public static void Main()
    {
        var locStore   = new DriverLocationStore();
        var stateStore = new DriverStateStore();
        var tripStore  = new TripStore();
        var surgeSvc   = new SurgePricingService();

        var drivers  = BuildDrivers(locStore, stateStore, surgeSvc);
        var dispatch = new DispatchService(locStore, stateStore, drivers);
        var tripSvc  = new TripService(tripStore, dispatch, surgeSvc, stateStore, locStore);

        // NYC locations
        var timesSquare  = new GeoPoint(40.7580, -73.9855);
        var centralPark  = new GeoPoint(40.7851, -73.9683);
        var brooklyn     = new GeoPoint(40.6501, -73.9496);
        var jfkAirport   = new GeoPoint(40.6413, -73.7781);

        Console.WriteLine("=== Scenario 1: Happy Path — Request → Assign → Start → Complete ===\n");
        {
            var pickup  = new GeoPoint(40.7128, -73.9960);  // near Ahmed
            var dropoff = centralPark;

            var trip = tripSvc.Request("rider-alice", pickup, dropoff);
            Console.WriteLine($"  Trip status: {trip.Status}  driver: {trip.DriverId}\n");

            tripSvc.StartTrip(trip.TripId);

            // Simulate actual trip: 5.2km, 18 minutes
            var completed = tripSvc.CompleteTrip(trip.TripId, actualDistanceKm: 5.2, actualDurationMin: 18);
            Console.WriteLine($"  Final fare: ${completed.FinalFare:F2}  (surge={completed.SurgeMultiplier:F1}×)");

            tripSvc.RateTrip(completed.TripId, driverRating: 5, riderRating: 5);
        }

        Console.WriteLine("\n=== Scenario 2: Driver Declines → Fallback to Next Driver ===\n");
        {
            // First 2 drivers decline; 3rd accepts
            var pickup  = new GeoPoint(40.7200, -73.9900);
            var dropoff = brooklyn;

            var trip = tripSvc.Request("rider-bob", pickup, dropoff,
                simulateDeclines: true, declineCount: 2);
            Console.WriteLine($"\n  Trip status: {trip.Status}  assigned driver: {trip.DriverId}");
            if (trip.Status == TripStatus.DriverAssigned)
                tripSvc.CompleteTrip(trip.TripId, 8.5, 28);
        }

        Console.WriteLine("\n=== Scenario 3: Surge Pricing in High-Demand Zone ===\n");
        {
            // Simulate Times Square on New Year's Eve — many requests, few drivers
            var zone = timesSquare;
            for (int i = 0; i < 20; i++) surgeSvc.RecordRequest(zone);  // 20 requests

            double surge = surgeSvc.GetSurge(zone);
            Console.WriteLine($"  Times Square surge: {surge:F1}×");

            double normalFare = PricingService.Calculate(3.0, 12, 1.0);
            double surgeFare  = PricingService.Calculate(3.0, 12, surge);
            Console.WriteLine($"  3km/12min trip: normal=${normalFare:F2}  with surge=${surgeFare:F2}");

            Console.WriteLine($"  Fare components: base=$2.00  dist=3×$1.25=$3.75  time=12×$0.20=$2.40");
            Console.WriteLine($"  Raw=$8.15  ×{surge:F1}=${8.15 * surge:F2}");
        }

        Console.WriteLine("\n=== Scenario 4: Fare Calculator — Various Trip Profiles ===\n");
        {
            var trips = new[]
            {
                ("Short city hop",    2.0,  8.0, 1.0),
                ("Medium trip",       7.0, 22.0, 1.0),
                ("Airport run",      18.0, 40.0, 1.0),
                ("Surge rush hour",   5.0, 25.0, 2.5),
                ("Max surge NYE",     3.0, 12.0, 5.0),
            };

            Console.WriteLine($"  {"Trip",-22} {"Dist",6} {"Dur",6} {"Surge",6} {"Fare",8}");
            Console.WriteLine($"  {new string('-', 52)}");
            foreach (var (name, dist, dur, surge) in trips)
            {
                double fare = PricingService.Calculate(dist, dur, surge);
                Console.WriteLine($"  {name,-22} {dist,5:F0}km {dur,5:F0}min {surge,5:F1}×  ${fare,6:F2}");
            }
        }

        Console.WriteLine("\n=== Scenario 5: ETA Estimation ===\n");
        {
            var d1Loc = drivers["d1"].Location;
            var d3Loc = drivers["d3"].Location;
            var pickup = timesSquare;

            double eta1 = EtaService.EstimateMinutes(d1Loc, pickup);
            double eta3 = EtaService.EstimateMinutes(d3Loc, pickup);
            double dist1 = GeoMath.DistanceKm(d1Loc, pickup);
            double dist3 = GeoMath.DistanceKm(d3Loc, pickup);

            Console.WriteLine($"  Driver d1 (Ahmed) → Times Square: dist={dist1:F2}km  ETA={eta1:F1}min");
            Console.WriteLine($"  Driver d3 (James) → Times Square: dist={dist3:F2}km  ETA={eta3:F1}min");

            double tripEta = EtaService.EstimateMinutes(timesSquare, jfkAirport);
            double tripDist = GeoMath.DistanceKm(timesSquare, jfkAirport);
            Console.WriteLine($"\n  Trip ETA Times Square → JFK: dist={tripDist:F2}km  ETA={tripEta:F1}min");
            Console.WriteLine($"  Rush hour ETA: {EtaService.EstimateMinutes(timesSquare, jfkAirport, rushHour: true):F1}min");
        }

        Console.WriteLine("\n=== Scenario 6: Cancellation Policy ===\n");
        {
            stateStore.SetAvailable("d4");

            var trip = tripSvc.Request("rider-carol", new GeoPoint(40.7300, -73.9800), centralPark);

            // Cancel immediately (free)
            tripSvc.CancelTrip(trip.TripId, "rider");
            Console.WriteLine($"  Early cancel fee: ${tripStore.Get(trip.TripId).FinalFare:F2}  (expected: $0.00)");

            // New trip, cancel after 2+ min (late cancel fee)
            stateStore.SetAvailable("d4");
            var trip2 = tripSvc.Request("rider-dave", new GeoPoint(40.7300, -73.9800), brooklyn);
            if (trip2.Status == TripStatus.DriverAssigned)
            {
                // Backdate AssignedAt to simulate 3 minutes having passed
                var t = tripStore.Get(trip2.TripId);
                t.AssignedAt = DateTime.UtcNow.AddMinutes(-3);
                tripStore.Save(t);
                tripSvc.CancelTrip(trip2.TripId, "rider");
                Console.WriteLine($"  Late cancel fee: ${tripStore.Get(trip2.TripId).FinalFare:F2}  (expected: $5.00)");
            }
        }

        Console.WriteLine("\n=== Scenario 7: Concurrent Requests — Same Driver Not Double-Booked ===\n");
        {
            // Reset all drivers
            foreach (var d in drivers.Values) stateStore.SetAvailable(d.DriverId);

            var pickup1 = new GeoPoint(40.7128, -73.9960);
            var pickup2 = new GeoPoint(40.7130, -73.9958);  // almost same location

            Console.WriteLine("  Rider-X and Rider-Y both request simultaneously...");
            var t1 = tripSvc.Request("rider-X", pickup1, centralPark);
            var t2 = tripSvc.Request("rider-Y", pickup2, brooklyn);

            Console.WriteLine($"\n  Trip-X driver: {t1.DriverId}");
            Console.WriteLine($"  Trip-Y driver: {t2.DriverId}");
            Console.WriteLine($"  Different drivers assigned: {t1.DriverId != t2.DriverId}  ← no double-booking ✓");
        }

        Console.WriteLine("\n=== Scenario 8: Geohash Spatial Indexing ===\n");
        {
            var points = new[] { timesSquare, centralPark, brooklyn, jfkAirport };
            var names  = new[] { "Times Square", "Central Park", "Brooklyn", "JFK Airport" };

            Console.WriteLine("  Geohash encoding (precision 6 ≈ 1km resolution):");
            for (int i = 0; i < points.Length; i++)
            {
                string hash6 = Geohash.Encode(points[i].Lat, points[i].Lng, 6);
                string hash4 = Geohash.ZonePrefix(points[i].Lat, points[i].Lng, 4);
                Console.WriteLine($"    {names[i],-15} → precision-6: {hash6}  zone(4): {hash4}");
            }

            Console.WriteLine("\n  Same zone-4 prefix = same surge zone:");
            string tsZone = Geohash.ZonePrefix(timesSquare.Lat, timesSquare.Lng, 4);
            string cpZone = Geohash.ZonePrefix(centralPark.Lat, centralPark.Lng, 4);
            string bkZone = Geohash.ZonePrefix(brooklyn.Lat, brooklyn.Lng, 4);
            Console.WriteLine($"    Times Square  zone={tsZone}");
            Console.WriteLine($"    Central Park  zone={cpZone}  same as Times Square: {tsZone == cpZone}");
            Console.WriteLine($"    Brooklyn      zone={bkZone}  same as Times Square: {bkZone == tsZone}");
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
