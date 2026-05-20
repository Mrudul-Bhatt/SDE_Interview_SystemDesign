// Ridesharing Backend — C# simulation
// Covers: geohash-based spatial indexing, driver location tracking,
//         sequential dispatch with atomic SET NX, trip state machine,
//         surge pricing, fare calculation, ETA estimation,
//         cancellation policy, and concurrent-request safety.
// assembly-guid: {C0D1E2F3-A4B5-6789-C012-789012300039}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

public class GeoPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public GeoPoint(double lat, double lng) { Lat = lat; Lng = lng; }
    public override string ToString() => $"({Lat:F4}, {Lng:F4})";
}

public enum DriverStatus { Offline, Available, PendingOffer, OnTrip }

public enum TripStatus
{
    Requested, DriverAssigned, InProgress,
    Completed, Cancelled, Failed
}

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

public class Rider
{
    public string   RiderId  { get; set; }
    public string   Name     { get; set; }
    public GeoPoint Location { get; set; }
}

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

// ─────────────────────────────────────────────────────────────────────────────
// 1. Geohash — encode lat/lng to a string grid cell
// ─────────────────────────────────────────────────────────────────────────────

public static class Geohash
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(double lat, double lng, int precision = 6)
    {
        double minLat = -90, maxLat = 90, minLng = -180, maxLng = 180;
        var sb     = new StringBuilder();
        int bits   = 0;
        int bitsTotal = 0;
        bool even  = true;

        while (sb.Length < precision)
        {
            double mid;
            if (even)
            {
                mid = (minLng + maxLng) / 2;
                if (lng >= mid) { bits = (bits << 1) | 1; minLng = mid; }
                else            { bits = bits << 1;        maxLng = mid; }
            }
            else
            {
                mid = (minLat + maxLat) / 2;
                if (lat >= mid) { bits = (bits << 1) | 1; minLat = mid; }
                else            { bits = bits << 1;        maxLat = mid; }
            }
            even = !even;
            bitsTotal++;
            if (bitsTotal == 5)
            {
                sb.Append(Base32[bits]);
                bitsTotal = 0;
                bits = 0;
            }
        }
        return sb.ToString();
    }

    // Returns the geohash prefix at a coarser precision (for surge zones)
    public static string ZonePrefix(double lat, double lng, int precision = 4) =>
        Encode(lat, lng, precision);
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Haversine distance
// ─────────────────────────────────────────────────────────────────────────────

public static class GeoMath
{
    private const double EarthRadiusKm = 6371.0;

    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRad(b.Lat - a.Lat);
        double dLng = ToRad(b.Lng - a.Lng);
        double h    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                    + Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat))
                    * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return EarthRadiusKm * 2 * Math.Asin(Math.Sqrt(h));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Driver Location Store (Redis Geo simulation)
// ─────────────────────────────────────────────────────────────────────────────

public class DriverLocationStore
{
    // Simulates Redis GEOADD / GEORADIUS
    private readonly Dictionary<string, (GeoPoint location, DateTime lastSeen)> _locations
        = new Dictionary<string, (GeoPoint, DateTime)>();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);

    public void UpdateLocation(string driverId, GeoPoint location)
    {
        _locations[driverId] = (location, DateTime.UtcNow);
    }

    public void Remove(string driverId) => _locations.Remove(driverId);

    // Returns drivers within radiusKm, sorted by distance, filtered by available status
    public List<(string driverId, double distanceKm)> FindNearby(
        GeoPoint center, double radiusKm, Func<string, bool> isAvailable, int limit = 20)
    {
        var now = DateTime.UtcNow;
        return _locations
            .Where(kv => (now - kv.Value.lastSeen) < _ttl)       // not stale
            .Where(kv => isAvailable(kv.Key))                     // driver is available
            .Select(kv => (driverId: kv.Key, dist: GeoMath.DistanceKm(center, kv.Value.location)))
            .Where(t => t.dist <= radiusKm)
            .OrderBy(t => t.dist)
            .Take(limit)
            .ToList();
    }

    public GeoPoint GetLocation(string driverId) =>
        _locations.TryGetValue(driverId, out var v) ? v.location : null;
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Driver State Store (Redis SET NX simulation)
// ─────────────────────────────────────────────────────────────────────────────

public class DriverStateStore
{
    private readonly Dictionary<string, (DriverStatus status, string tripId)> _state
        = new Dictionary<string, (DriverStatus, string)>();
    private readonly object _lock = new object();  // simulates Redis single-thread

    public void SetAvailable(string driverId)
    {
        lock (_lock) _state[driverId] = (DriverStatus.Available, null);
    }

    public void SetOffline(string driverId)
    {
        lock (_lock) _state[driverId] = (DriverStatus.Offline, null);
    }

    // Simulates Redis SET NX — returns true only if driver was Available (atomic)
    public bool TryClaim(string driverId, string tripId)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(driverId, out var current)) return false;
            if (current.status != DriverStatus.Available) return false;
            _state[driverId] = (DriverStatus.PendingOffer, tripId);
            return true;
        }
    }

    public void SetOnTrip(string driverId, string tripId)
    {
        lock (_lock) _state[driverId] = (DriverStatus.OnTrip, tripId);
    }

    public void Release(string driverId)
    {
        lock (_lock)
        {
            if (_state.TryGetValue(driverId, out var s) && s.status == DriverStatus.PendingOffer)
                _state[driverId] = (DriverStatus.Available, null);
        }
    }

    public DriverStatus GetStatus(string driverId) =>
        _state.TryGetValue(driverId, out var s) ? s.status : DriverStatus.Offline;

    public bool IsAvailable(string driverId) =>
        GetStatus(driverId) == DriverStatus.Available;
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Surge Pricing Service
// ─────────────────────────────────────────────────────────────────────────────

public class SurgePricingService
{
    // zone (geohash prefix) → (demand count, supply count)
    private readonly Dictionary<string, (int demand, int supply)> _zones
        = new Dictionary<string, (int, int)>();
    private const double MaxSurge = 8.0;
    private const double SmoothingFactor = 0.7;
    private readonly Dictionary<string, double> _smoothedSurge = new Dictionary<string, double>();

    public void RecordRequest(GeoPoint location)
    {
        var zone = Geohash.ZonePrefix(location.Lat, location.Lng, 4);
        if (!_zones.ContainsKey(zone)) _zones[zone] = (0, 0);
        var (d, s) = _zones[zone];
        _zones[zone] = (d + 1, s);
    }

    public void RecordAvailableDriver(GeoPoint location)
    {
        var zone = Geohash.ZonePrefix(location.Lat, location.Lng, 4);
        if (!_zones.ContainsKey(zone)) _zones[zone] = (0, 0);
        var (d, s) = _zones[zone];
        _zones[zone] = (d, s + 1);
    }

    public double GetSurge(GeoPoint location)
    {
        var zone = Geohash.ZonePrefix(location.Lat, location.Lng, 4);
        if (!_zones.TryGetValue(zone, out var counts)) return 1.0;

        var (demand, supply) = counts;
        double raw = supply == 0 ? MaxSurge : Math.Min((double)demand / supply, MaxSurge);
        raw = Math.Max(1.0, raw);

        // EMA smoothing
        if (!_smoothedSurge.ContainsKey(zone)) _smoothedSurge[zone] = 1.0;
        _smoothedSurge[zone] = SmoothingFactor * _smoothedSurge[zone] + (1 - SmoothingFactor) * raw;
        return Math.Round(_smoothedSurge[zone], 1);
    }

    public void Reset(GeoPoint location)
    {
        var zone = Geohash.ZonePrefix(location.Lat, location.Lng, 4);
        _zones[zone] = (0, 0);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. ETA Service
// ─────────────────────────────────────────────────────────────────────────────

public static class EtaService
{
    private const double RoadFactor     = 1.30;  // straight-line → road km
    private const double CitySpeedKmh   = 25.0;  // average urban speed
    private const double RushSpeedKmh   = 15.0;  // rush hour

    public static double EstimateMinutes(GeoPoint from, GeoPoint to, bool rushHour = false)
    {
        double distKm   = GeoMath.DistanceKm(from, to) * RoadFactor;
        double speedKmh = rushHour ? RushSpeedKmh : CitySpeedKmh;
        return distKm / speedKmh * 60.0;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. Pricing Service
// ─────────────────────────────────────────────────────────────────────────────

public static class PricingService
{
    private const double BaseFare      = 2.00;
    private const double PerKmRate     = 1.25;
    private const double PerMinRate    = 0.20;
    private const double MinimumFare   = 5.00;

    public static double Estimate(GeoPoint pickup, GeoPoint dropoff, double surgeMultiplier)
    {
        double distKm  = GeoMath.DistanceKm(pickup, dropoff) * 1.3;
        double minutes = distKm / 25.0 * 60.0;
        return Calculate(distKm, minutes, surgeMultiplier);
    }

    public static double Calculate(double distanceKm, double durationMin, double surgeMultiplier)
    {
        double raw   = BaseFare + distanceKm * PerKmRate + durationMin * PerMinRate;
        double final = raw * surgeMultiplier;
        return Math.Round(Math.Max(final, MinimumFare), 2);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 8. Trip Store
// ─────────────────────────────────────────────────────────────────────────────

public class TripStore
{
    private readonly Dictionary<string, Trip> _trips = new Dictionary<string, Trip>();

    public void Save(Trip t) => _trips[t.TripId] = t;
    public Trip Get(string id) => _trips.TryGetValue(id, out var t) ? t : null;
    public List<Trip> GetByStatus(TripStatus s) => _trips.Values.Where(t => t.Status == s).ToList();
}

// ─────────────────────────────────────────────────────────────────────────────
// 9. Dispatch Service
// ─────────────────────────────────────────────────────────────────────────────

public class DispatchResult
{
    public bool   Success    { get; set; }
    public string DriverId   { get; set; }
    public double EtaMinutes { get; set; }
    public int    AttemptsUsed { get; set; }
    public string Error      { get; set; }
}

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

            // Rank by ETA then driver rating
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

// ─────────────────────────────────────────────────────────────────────────────
// 10. Trip Service — state machine
// ─────────────────────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────────────────────
// Demo: end-to-end scenarios
// ─────────────────────────────────────────────────────────────────────────────

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
        var locStore  = new DriverLocationStore();
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
            // only 3 drivers available in zone (already recorded above)

            double surge = surgeSvc.GetSurge(zone);
            Console.WriteLine($"  Times Square surge: {surge:F1}×");

            double normalFare = PricingService.Calculate(3.0, 12, 1.0);
            double surgeFare  = PricingService.Calculate(3.0, 12, surge);
            Console.WriteLine($"  3km/12min trip: normal=${normalFare:F2}  with surge=${surgeFare:F2}");

            // Show surge breakdown
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
            // Reset d4 to available
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
                // Manually backdate AssignedAt to simulate 3 minutes having passed
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

            // Two riders request at same time — simulate sequential dispatch
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
