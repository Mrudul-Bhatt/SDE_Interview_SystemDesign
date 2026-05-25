// PresenceService — tracks online/offline status via heartbeat TTL.
//
// Why heartbeat + TTL (not just connect/disconnect): if a server crashes, it
// can't send a disconnect event. The heartbeat TTL (30s) bounds how long a
// user appears online after their server dies — Redis key expiry handles this
// automatically in production without any cleanup code.
//
// LastSeen is updated on both heartbeat and disconnect so "last seen 5 min ago"
// shows correctly even if the user's session ended without an explicit disconnect.

namespace AdvancedDesigns
{
    public class PresenceService
    {
        private readonly Dictionary<string, DateTime> _heartbeats = new();
        private readonly Dictionary<string, DateTime> _lastSeen   = new();
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        public void Heartbeat(string userId)
        {
            _heartbeats[userId] = DateTime.UtcNow;
            _lastSeen[userId]   = DateTime.UtcNow;
        }

        public void Disconnect(string userId)
        {
            _lastSeen[userId] = DateTime.UtcNow;
            _heartbeats.Remove(userId); // stops IsOnline from returning true
        }

        public bool IsOnline(string userId) =>
            _heartbeats.TryGetValue(userId, out var last) && DateTime.UtcNow - last < _timeout;

        public DateTime? GetLastSeen(string userId) =>
            _lastSeen.TryGetValue(userId, out var ts) ? ts : (DateTime?)null;
    }
}
