// ConnectionRegistry — maps userId → chatServerId (mirrors a Redis hash).
//
// Why a central registry: when Server1 needs to deliver a message to a user
// connected on Server2, it looks up the registry to find which server holds
// that user's WebSocket, then publishes to that server's channel via the bus.
// Without this, every server would have to fan-out to all peers on every message.
//
// In production: stored in Redis so all chat servers share one view. The entry
// is set on connect and deleted on disconnect (or TTL-expired on crash).

namespace AdvancedDesigns
{
    public class ConnectionRegistry
    {
        private readonly Dictionary<string, string> _userToServer = new();

        public void Register(string userId, string serverId)   => _userToServer[userId] = serverId;
        public void Deregister(string userId)                  => _userToServer.Remove(userId);

        public string GetServer(string userId) =>
            _userToServer.TryGetValue(userId, out var s) ? s : null;

        public bool IsOnline(string userId) => _userToServer.ContainsKey(userId);
    }
}
