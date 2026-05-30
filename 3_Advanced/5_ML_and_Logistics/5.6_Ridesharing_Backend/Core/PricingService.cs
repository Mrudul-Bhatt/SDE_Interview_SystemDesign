// PricingService — fare formula.
//
//   fare = max(MinimumFare, (BaseFare + dist × PerKmRate + dur × PerMinRate) × surge)
//
// Both distance AND time are charged so the driver isn't penalized when they
// hit heavy traffic (the trip pays them for sitting in it). Surge multiplies
// the WHOLE raw amount in this simplified model; real Uber applies surge only
// to the base + booking fee, not to the per-km rate, so the same trip costs
// less proportionally during deep surges.
//
// Estimate is what the rider sees up front; Calculate is run on completion
// with actual measurements.

using System;

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
