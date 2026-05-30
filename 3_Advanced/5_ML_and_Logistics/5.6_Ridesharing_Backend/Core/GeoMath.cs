// GeoMath — straight-line ("as-the-crow-flies") distance between two points.
//
// Haversine formula treats the Earth as a sphere (radius 6371 km). Accurate to
// within ~0.5% — good enough for ride matching where the road-factor multiplier
// dominates the inaccuracy anyway. For sub-meter accuracy you'd use Vincenty's
// formula (ellipsoidal Earth), but ridesharing doesn't need that precision.

using System;

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
