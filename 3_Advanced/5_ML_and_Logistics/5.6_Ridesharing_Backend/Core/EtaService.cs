// EtaService — simple distance/speed ETA approximation.
//
// In production this is a machine-learned model per H3 cell × time-of-day,
// fed by a live traffic data stream. This stub uses the simple formula:
//   eta = (haversine_distance × road_factor) / average_speed
//
// RoadFactor (1.3×) inflates straight-line distance to account for the fact
// that roads aren't straight. This is the standard "Manhattan adjustment" —
// real urban grids add about 30% to crow-fly distance.
//
// Two speed regimes (city / rush) is the bare minimum; real systems have
// dozens of speed buckets by hour of day and day of week.

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
