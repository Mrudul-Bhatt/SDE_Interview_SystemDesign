// ChatServer — the orchestrator. Every user action (connect, send, read) flows through here.
//
// THE BIG IDEA:
// ChatServer is the conductor that wires the other components together. It never stores
// data itself — that's MessageStoreCassandra and GroupStoreRedis. It never routes cross-server itself
// — that's MessageBusRedis. It never tracks presence itself — that's PresenceServiceRedis. It
// just knows the ORDER in which to call them and what to do when each one responds.
//
// THE SEND PIPELINE (order is load-bearing — do not reorder):
//
//   1. Persist → MessageStoreCassandra.Save()
//      Write to durable storage FIRST, before touching any live connection.
//      If delivery fails at any later step, the message is already safe — a retry
//      can re-deliver it. If you deliver first and persist second, a crash between
//      the two steps loses the message forever.
//
//   2. Fan out → MessageBusRedis.Publish() per recipient
//      Each recipient gets a Publish call on "user:{recipientId}". If their WebSocket
//      is alive (on this server or any other), the bus routes the message there and
//      returns true. OnlineDelivered is incremented.
//
//   3. Push fallback → PushNotificationServiceAPNsFCM.Send() when Publish returns false
//      False means no server is subscribed to that channel — the user is offline.
//      A push notification wakes their device so they reconnect and drain the backlog.
//      PushSent is incremented. The message is already in MessageStoreCassandra; the push is
//      just the wake-up call.
//
// THE CONNECT FLOW:
//   1. Register with ConnectionRegistryRedis  — routing table entry ("I own this user")
//   2. Heartbeat PresenceServiceRedis         — mark the user as online
//   3. Subscribe on MessageBusRedis           — start receiving inbound messages for this user
//   4. Drain backlog from MessageStoreCassandra   — deliver any messages that arrived while offline
//      (at-least-once guarantee: the message was persisted; we now hand it to the user)
//
// WHY THE BACKLOG DRAIN HAPPENS ON CONNECT (not on push notification):
// A push notification is fire-and-forget — the device may be off, notifications may
// be disabled, or the OS may drop it. We can't rely on the push to trigger delivery.
// Instead, the contract is: every reconnect always drains the backlog. This guarantees
// that no matter how the user returns to the app (push, background refresh, manual open),
// they always receive all missed messages — exactly once per message, not zero times.
//
// WHY GETRECIPIENTS RETURNS NOTHING FOR THE SENDER:
// The sender already has the message locally — they typed it. Delivering it back over
// the bus would cause a duplicate in their own chat window. Sender exclusion keeps
// the fan-out list clean without needing the caller to filter.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class ChatServer
    {
        // This server's identity — registered in ConnectionRegistryRedis when a user connects
        // so other servers know which channel to publish to when routing to this server's users.
        public string ServerId { get; }

        private readonly MessageStoreCassandra _store;     // durable message persistence
        private readonly ConnectionRegistryRedis _registry;  // userId → serverId routing table
        private readonly PresenceServiceRedis _presence;  // heartbeat and last-seen tracking
        private readonly PushNotificationServiceAPNsFCM _push;      // offline fallback via APNs/FCM
        private readonly MessageBusRedis _bus;       // cross-server pub/sub delivery
        private readonly GroupStoreRedis _groups;    // group chat member lists

        // userId → callback that pushes a formatted message string to the user's WebSocket.
        // Populated by ConnectUser (when the client provides an onReceive handler) and
        // cleared by DisconnectUser. Only present for users connected to THIS server instance.
        //
        // ── RUNTIME SNAPSHOT (Scenario 3, this = Server2 which only holds Bob) ──
        //   Server2._deliveryCallbacks = {
        //       "bob" → λ str → bobMsgs.Add(str)   ← the client's onReceive handler
        //   }
        //   Note it holds ONLY this server's users. alice lives in Server1's dictionary,
        //   carol in Server3's. This is the KEY difference from ConnectionRegistryRedis, which
        //   holds the GLOBAL userId→serverId map shared by every server. The callback
        //   (how to reach the socket) is local; the routing table (which server) is global.
        private readonly Dictionary<string, Action<string>> _deliveryCallbacks = [];

        // Append-only event log for demo output and test assertions.
        // In production this would be structured logs shipped to a log aggregator.
        //
        // ── RUNTIME SNAPSHOT (Server1 after Alice connects and sends one message) ──
        //   _log = [
        //       "[Server1] alice connected",
        //       "[Server1] Persisted msg:0001 from alice",
        //       "[Server1] Delivered msg:0001 to bob"        ← (if bob is on Server1)
        //   ]
        private readonly List<string> _log = [];

        public ChatServer(string serverId, MessageStoreCassandra store, ConnectionRegistryRedis registry,
            PresenceServiceRedis presence, PushNotificationServiceAPNsFCM push, MessageBusRedis bus, GroupStoreRedis groups)
        {
            ServerId = serverId;
            _store = store;
            _registry = registry;
            _presence = presence;
            _push = push;
            _bus = bus;
            _groups = groups;
        }

        // Registers a user's WebSocket connection on this server. Four things must all
        // happen: routing entry, presence heartbeat, bus subscription, and backlog drain.
        // Skipping any one breaks a guarantee:
        //   No Register  → other servers can't route messages here
        //   No Heartbeat → user appears offline immediately
        //   No Subscribe → inbound messages from the bus are silently dropped
        //   No backlog   → messages sent while offline are never delivered
        public void ConnectUser(string userId, Action<string> onReceive = null)
        {
            _registry.Register(userId, ServerId);
            _presence.Heartbeat(userId);
            if (onReceive != null) _deliveryCallbacks[userId] = onReceive;

            // Subscribe so this server receives messages published to this user's channel.
            _bus.Subscribe($"user:{userId}", msg => Deliver(userId, msg));
            Log($"[{ServerId}] {userId} connected");

            // Drain offline backlog — deliver any messages that arrived while user was offline.
            // Status is advanced to Delivered immediately because we're handing the message
            // to the user's device right now. Ordered by SentAt so messages appear in the
            // correct chronological sequence on the client's screen.
            foreach (var msg in _store.GetUndelivered(userId))
            {
                msg.Status = DeliveryStatus.Delivered;
                onReceive?.Invoke($"(offline backlog) {msg}");
                Log($"[{ServerId}] Delivered backlog message to {userId}: {msg.MessageId}");
            }
        }

        // Tears down all state for this user's session. All three stores must be
        // cleaned up: Registry (or future messages route to a dead channel), Presence
        // (or the user shows as online until the heartbeat window expires), and
        // DeliveryCallbacks (or the Action reference keeps objects alive in memory).
        public void DisconnectUser(string userId)
        {
            _registry.Deregister(userId);
            _presence.Disconnect(userId);
            _deliveryCallbacks.Remove(userId);
            Log($"[{ServerId}] {userId} disconnected");
        }

        // Executes the full send pipeline: persist → fan out → push fallback.
        // Returns a SendResult counting how many recipients were reached online vs offline.
        // The message is durable before this method returns regardless of delivery outcome.
        public SendResult Send(string senderId, string chatId, string content,
            MessageType type = MessageType.Text, string mediaUrl = null)
        {
            // Step 1: persist before attempting delivery (durability guarantee).
            // If the process crashes after this line, the message is safe in MessageStoreCassandra
            // and will be delivered when any recipient reconnects via the backlog drain.
            var msg = _store.Save(chatId, senderId, content, type: type, mediaUrl: mediaUrl);
            Log($"[{ServerId}] Persisted {msg.MessageId} from {senderId}");

            int delivered = 0, pushed = 0;

            foreach (string recipientId in GetRecipients(chatId, senderId))
            {
                // Step 2: attempt real-time delivery via the message bus.
                // Publish routes to whichever server holds the recipient's WebSocket,
                // or returns false immediately if no server is subscribed (user offline).
                bool online = _bus.Publish($"user:{recipientId}", msg);
                if (online)
                {
                    delivered++;
                }
                else
                {
                    // Step 3: push fallback for offline recipients.
                    // Truncate the preview to 20 chars — push payloads have size limits
                    // (APNs: 4 KB total) and previews are user-visible in lock-screen notifications.
                    string preview = content.Length > 20 ? content[..20] + "..." : content;
                    _push.Send(recipientId, $"New message from {senderId}: \"{preview}\"");
                    pushed++;
                    Log($"[{ServerId}] Push notification sent to offline user {recipientId}");
                }
            }

            return new SendResult(msg.MessageId, delivered, pushed);
        }

        // Advances every inbound message in the chat to Read status on behalf of userId.
        // Only marks messages the user RECEIVED (SenderId != userId) — the sender's own
        // messages are not "unread" from their perspective and should not be touched.
        // In production this also broadcasts a read-receipt event back to the sender
        // so their ✓✓ checkmarks update in real time.
        public void MarkRead(string userId, string chatId)
        {
            foreach (var msg in _store.GetHistory(chatId).Where(m => m.SenderId != userId))
                msg.Status = DeliveryStatus.Read;
            Log($"[{ServerId}] {userId} marked {chatId} as read");
        }

        // Thin delegation to MessageStoreCassandra — ChatServer is the public API surface;
        // callers never touch the store directly. Keeps the dependency graph clean:
        // clients talk to ChatServer, ChatServer talks to storage.
        public List<ChatMessage> GetHistory(string chatId, int count = 20, DateTime? before = null)
            => _store.GetHistory(chatId, count, before);

        public IReadOnlyList<string> GetLog() => _log;

        // Sets the message as Delivered and fires the recipient's WebSocket callback.
        // Called by the MessageBusRedis subscription registered in ConnectUser — this is
        // the handler that executes when another server publishes to "user:{userId}".
        private void Deliver(string userId, ChatMessage msg)
        {
            msg.Status = DeliveryStatus.Delivered;
            Log($"[{ServerId}] Delivered {msg.MessageId} to {userId}");
            if (_deliveryCallbacks.TryGetValue(userId, out var cb)) cb(msg.ToString());
        }

        // Resolves the recipient list for a given chat, always excluding the sender.
        //
        // Group chat  → GroupStoreRedis returns a HashSet of member userIds; filter out sender.
        // 1:1 chat    → GroupStoreRedis returns null (no entry); decode participants from the
        //               chatId format "chat:alice:bob" by splitting on ':' and skipping
        //               the "chat" prefix. Sender is filtered by the same Where clause.
        //
        // This two-branch design keeps 1:1 chats free of GroupStoreRedis overhead while
        // reusing the same fan-out loop in Send for both chat types.
        private IEnumerable<string> GetRecipients(string chatId, string senderId)
        {
            var members = _groups.GetMembers(chatId);
            if (members != null) return members.Where(m => m != senderId);

            return chatId.Split(':').Skip(1).Where(u => u != senderId);
        }

        private void Log(string msg) => _log.Add(msg);
    }
}
