// SurgePricingService — supply/demand ratio per geohash zone with EMA smoothing.
//
//   raw_surge = pending_requests / max(available_drivers, 1)
//   capped at MaxSurge (8×)
//
// EMA smoothing (smoothing_factor = 0.7):
//   smoothed = 0.7 × previous_smoothed + 0.3 × raw
//
// Without smoothing, surge would flap wildly between consecutive computations
// (1× → 4× → 1× → 5× every minute) as drivers move in and out of zones. The
// EMA gives a gradual ramp-up that riders perceive as fair. The trade-off is
// a slight lag in responding to real demand spikes — acceptable because the
// rider sees the multiplier BEFORE confirming, so they never get surprised by
// a surge that arrived mid-trip.

using System;
using System.Collections.Generic;

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

        // EMA smoothing — prevents surge from oscillating between consecutive computations
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
