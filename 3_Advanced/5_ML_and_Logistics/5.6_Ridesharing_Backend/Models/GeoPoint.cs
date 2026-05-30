// GeoPoint — a lat/lng coordinate.
//
// Used everywhere: driver location, pickup, dropoff, surge zone center.
// We store as plain doubles rather than a struct/tuple because the system
// frequently needs to mutate a driver's location and pass it through async
// hops where reference semantics avoid copying.

public class GeoPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public GeoPoint(double lat, double lng) { Lat = lat; Lng = lng; }
    public override string ToString() => $"({Lat:F4}, {Lng:F4})";
}
