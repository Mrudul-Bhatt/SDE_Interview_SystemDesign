// MessageBusRedis — in-process pub/sub (simulates Redis Pub/Sub for cross-server routing).
//
// THE BIG IDEA:
// Think of this like a walkie-talkie dispatch center. Each chat server tunes into
// a set of frequencies — one per connected user — when that user's WebSocket opens.
// When Server1 needs to deliver a message to Bob (whose WebSocket is on Server3),
// it broadcasts on Bob's frequency. Server3 is already listening on that frequency
// and relays it down Bob's socket. Server1 never needs to know Server3's address.
//
// The full cross-server delivery flow:
//
//   1. Bob connects → Server3 calls Subscribe("user:bob", handler)
//   2. Alice sends to Bob → Server1 calls Publish("user:bob", message)
//   3. MessageBusRedis (Redis in prod) routes the message to Server3's handler
//   4. Server3's handler pushes it down Bob's WebSocket
//
// In production, this class is replaced by a Redis Pub/Sub client. Every chat server
// subscribes its own users on startup (and on each new WebSocket connection) and
// publishes to Redis when routing messages cross-server. The channel naming convention
// is "user:{userId}" — predictable, cheap to construct, and globally unique per user.
//
// WHY PUB/SUB INSTEAD OF DIRECT SERVER-TO-SERVER CALLS:
// Direct calls (Server1 calling Server3's REST API) would require Server1 to know
// Server3's address. But servers come and go — auto-scaling adds new nodes, rolling
// deployments restart old ones. Maintaining a live mesh of server addresses means
// each server needs a service-discovery client, health checks, and retry logic.
// With pub/sub, servers are anonymous: they only need to know the channel name
// ("user:bob"), not who is listening. A new server joining the fleet just subscribes
// to its users' channels on Redis and immediately starts receiving messages — no
// topology changes, no address registration.
//
// WHY PUBLISH RETURNS BOOL (not void):
// False means "nobody is subscribed to this channel right now." In the chat context
// that means Bob is offline — no server is holding his WebSocket. ChatServer treats
// false as the branch condition to fall back to PushNotificationService (APNs/FCM).
// This is a normal routing outcome, not an error. Returning void would force callers
// to call IsOnline separately before Publish — two round trips to Redis instead of one.
//
// WHY A LIST OF HANDLERS PER CHANNEL (not a single handler):
// A user can be logged in on multiple devices simultaneously — phone and laptop both
// open. Each WebSocket registers its own handler via Subscribe. A single Publish call
// fans out to all of them in one shot. In production, each subscriber is typically on
// a different server, and Redis Pub/Sub delivers to each subscribed server independently.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class MessageBusRedis
    {
        // Channel name → list of handlers registered for that channel.
        // In the chat system, channel names follow the "user:{userId}" convention.
        // Multiple handlers per channel support multi-device sessions (see header).
        //
        // ── RUNTIME SNAPSHOT (Scenario 1: Alice on Server1, Bob on Server2) ──
        //   _subscribers = {
        //       "user:alice" → [ λ msg → Server1.Deliver("alice", msg) ],
        //       "user:bob"   → [ λ msg → Server2.Deliver("bob",   msg) ]
        //   }
        //   Each λ (lambda) is the handler registered by ConnectUser. Calling
        //   Publish("user:bob", msg) walks Bob's list and invokes that one handler,
        //   which pushes the message down Bob's WebSocket on Server2.
        //
        //   Multi-device case — Bob on phone AND laptop (two servers subscribe him):
        //   "user:bob" → [ λ→Server2.Deliver, λ→Server4.Deliver ]   ← one Publish hits both
        //
        //   Offline case — nobody subscribed "user:dave":
        //   "user:dave" key is ABSENT → Publish returns false → ChatServer sends a push.
        private readonly Dictionary<string, List<Action<ChatMessage>>> _subscribers = [];

        // Registers a handler on the given channel. Called by ChatServer when a
        // WebSocket connection opens — "I am now responsible for delivering to this user."
        // In production this maps to a Redis SUBSCRIBE command on the server's Redis client.
        public void Subscribe(string channel, Action<ChatMessage> handler)
        {
            if (!_subscribers.ContainsKey(channel))
                _subscribers[channel] = [];
            _subscribers[channel].Add(handler);
        }

        // Delivers message to all handlers on the channel.
        // Returns true if at least one handler was invoked (user is online on some server).
        // Returns false if no subscriber exists (user offline) — the signal for ChatServer
        // to route to PushNotificationService instead. In production this is a Redis PUBLISH,
        // which returns the number of subscribers who received the message; false maps to 0.
        public bool Publish(string channel, ChatMessage message)
        {
            if (!_subscribers.TryGetValue(channel, out var handlers) || handlers.Count == 0)
                return false;
            foreach (var h in handlers) h(message);
            return true;
        }
    }
}
