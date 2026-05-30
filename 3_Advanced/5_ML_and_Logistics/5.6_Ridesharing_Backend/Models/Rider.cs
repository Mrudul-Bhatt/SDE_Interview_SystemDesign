// Rider — profile + current location for trip requests.
//
// Riders move much less than drivers (they're stationary while waiting), so we
// don't bother with a Redis Geo index for them — their location is just a
// snapshot at request time stored on the Trip.

public class Rider
{
    public string   RiderId  { get; set; }
    public string   Name     { get; set; }
    public GeoPoint Location { get; set; }
}
