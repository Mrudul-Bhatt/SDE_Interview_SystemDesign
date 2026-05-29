// DriverStateStore — simulates Redis SET NX for atomic driver claiming.
//
// This is the linchpin that prevents two riders from being assigned the same
// driver. TryClaim is the atomic compare-and-swap: it succeeds ONLY if the
// driver was Available, and atomically transitions to PendingOffer. The lock
// here stands in for Redis's single-threaded command processing — in
// production, the equivalent is:
//
//   SET driver:42:state "PENDING_OFFER:trip_abc" NX EX 15
//
// where NX ("set if not exists") is the atomic part and EX 15 auto-releases
// the lock after 15 seconds if the driver never responds.
//
// Release returns a PendingOffer driver back to Available (used when the
// driver declines the offer); SetOnTrip is the terminal acceptance state.

using System.Collections.Generic;

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
