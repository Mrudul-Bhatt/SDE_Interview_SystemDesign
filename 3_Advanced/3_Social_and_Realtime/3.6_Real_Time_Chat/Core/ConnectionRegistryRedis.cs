// ConnectionRegistryRedis — maps userId → chatServerId (mirrors a Redis hash).
//
// THE BIG IDEA:
// Think of this as an airport arrivals board. When a user opens the app, their
// name goes up on the board next to the gate (server) where their WebSocket
// landed. When a message needs to find them, someone checks the board.
//
// In a single-server setup this would be unnecessary — the server already knows
// its own connections. The registry exists because real deployments run dozens
// of chat servers behind a load balancer. Alice's WebSocket is on Server1;
// Bob's is on Server3. When Alice sends to Bob:
//
//   1. Server1 receives Alice's message.
//   2. Server1 asks the registry: "which server is Bob connected to?"
//   3. Registry answers: "Server3".
//   4. Server1 publishes to Server3's channel on the message bus.
//   5. Server3 pushes the message down Bob's WebSocket.
//
// Without this lookup, Server1 would have to broadcast the message to ALL
// servers and let each one check whether Bob is theirs — O(servers) wasted
// work on every single message. The registry makes delivery O(1) targeted.
//
// WHY ENTRIES MUST BE DELETED ON DISCONNECT:
// A stale entry (user disconnected but entry still present) would cause Server1
// to keep publishing to a channel that nobody is reading — the message silently
// disappears instead of falling back to push notification. Deregister() is
// called on clean disconnect. In production the entry also carries a short TTL
// so a crashed server (no clean disconnect) doesn't leave ghost entries forever.
//
// WHY GetServer RETURNS NULL INSTEAD OF THROWING:
// An offline user is a normal, expected code path — not an error. ChatServer
// uses null as the explicit branch condition: "not found → fall back to push
// notification via APNs/FCM." Throwing an exception here would force every
// caller to wrap it in try/catch for a routine routing decision.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class ConnectionRegistryRedis
    {
        // In-memory mirror of a Redis hash: userId → serverId.
        // Key presence means the user is currently online on that server.
        // Absence means offline — route to push notification instead.
        //
        // ── RUNTIME SNAPSHOT (Scenario 3: alice/bob/carol online, dave offline) ──
        //   _userToServer = {
        //       "alice" → "Server1",   ← Alice's WebSocket landed on Server1
        //       "bob"   → "Server2",
        //       "carol" → "Server3"
        //   }                          ← "dave" is ABSENT → he's offline → push fallback
        //
        //   GetServer("bob")   returns "Server2"   (publish to Server2's bus channel)
        //   GetServer("dave")  returns null        (the signal: send a push instead)
        //   After Deregister("bob") → the "bob" key vanishes → GetServer("bob") = null
        private readonly Dictionary<string, string> _userToServer = [];

        // Called when a WebSocket handshake completes. Overwrites any prior entry
        // for the same user — handles reconnects (user switched from WiFi to mobile)
        // without needing an explicit Deregister call first.
        public void Register(string userId, string serverId) => _userToServer[userId] = serverId;

        // Called when the WebSocket closes cleanly (user closes app, navigates away).
        // Must be called promptly — a stale entry means messages get routed to a dead
        // channel instead of triggering push notifications. In production, a Redis TTL
        // handles the case where Deregister is never called (server crash, lost connection).
        public void Deregister(string userId) => _userToServer.Remove(userId);

        // Returns the serverId where the user's active WebSocket lives, or null if offline.
        // Null is the signal to ChatServer to fall back to PushNotificationService — it is
        // a normal return value, not an error condition.
        public string GetServer(string userId) => _userToServer.TryGetValue(userId, out var s) ? s : null;

        // Convenience check that avoids null handling at call sites that only need
        // a yes/no answer (e.g., PresenceService reporting who is online in a chat).
        // Equivalent to GetServer(userId) != null but reads more clearly at the call site.
        public bool IsOnline(string userId) => _userToServer.ContainsKey(userId);
    }
}
