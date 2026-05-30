// DriverLocationStore — simulates Redis Geo (GEOADD / GEORADIUS) for live
// driver positions.
//
// Two design choices encode real-world constraints:
//
// 1. TTL on every entry. If a driver's app crashes, their GPS pings stop —
//    after 30 seconds we treat them as offline automatically. No explicit
//    "I am offline" message is required, which is robust against silent
//    network drops.
//
// 2. FindNearby takes an isAvailable predicate so we filter by status BEFORE
//    distance-sorting. This prevents us from "finding" a driver who's already
//    on a trip — the candidate list is automatically clean.

using System;
using System.Collections.Generic;
using System.Linq;

public class DriverLocationStore
{
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
