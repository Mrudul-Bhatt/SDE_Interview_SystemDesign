// Geohash — encode (lat, lng) to a base32 string where similar prefixes mean
// nearby locations on the globe.
//
// Algorithm: interleave bits of lat and lng, packing 5 bits per output
// character. Each character refines the bounding rectangle by a factor of 4
// (alternating lng and lat subdivision).
//
// Precision guide:
//   6 chars ≈ 1.2km × 0.6km — good for "nearby drivers" search
//   4 chars ≈ 39km × 19km   — coarse zone for surge pricing aggregation
//   8 chars ≈ 38m × 19m     — sub-block precision (not used here)
//
// Why geohash not raw lat/lng? Because Redis Geo (and FAISS-style indices)
// can do range queries on the string prefix in O(log N), which is what makes
// "find drivers in this neighborhood" sub-millisecond at scale.

using System.Text;

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
