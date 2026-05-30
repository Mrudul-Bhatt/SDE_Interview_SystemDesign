// TripStore — durable trip records (Cassandra in production).
//
// Production schema would partition by city_id with clustering by requested_at
// DESC so "all trips in NYC over the last hour" is a single fast scan. This
// in-memory dictionary just stands in by trip_id.

using System.Collections.Generic;
using System.Linq;

public class TripStore
{
    private readonly Dictionary<string, Trip> _trips = new Dictionary<string, Trip>();

    public void Save(Trip t) => _trips[t.TripId] = t;
    public Trip Get(string id) => _trips.TryGetValue(id, out var t) ? t : null;
    public List<Trip> GetByStatus(TripStatus s) => _trips.Values.Where(t => t.Status == s).ToList();
}
