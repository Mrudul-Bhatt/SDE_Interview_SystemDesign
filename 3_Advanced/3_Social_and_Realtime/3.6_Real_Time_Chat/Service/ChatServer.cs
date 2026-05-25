// ChatServer — handles WebSocket connections and orchestrates the send pipeline.
//
// Send pipeline (order matters for correctness):
//   1. Persist to MessageStore FIRST — durability before delivery.
//      If delivery fails, the message is still safe; retry is possible.
//   2. Publish to MessageBus per recipient — routed to whichever server holds
//      the recipient's WebSocket via Redis pub/sub.
//   3. If Publish returns false (no subscriber = offline), send push notification.
//
// ConnectUser subscribes this server to the user's pub/sub channel and drains
// any undelivered offline messages — ensuring at-least-once delivery.
//
// GetRecipients distinguishes group chats (looked up in GroupStore) from 1:1 chats
// (decoded from the chatId format "chat:alice:bob"). Sender is always excluded.

namespace AdvancedDesigns
{
    public class ChatServer
    {
        public string ServerId { get; }

        private readonly MessageStore               _store;
        private readonly ConnectionRegistry         _registry;
        private readonly PresenceService            _presence;
        private readonly PushNotificationService    _push;
        private readonly MessageBus                 _bus;
        private readonly GroupStore                 _groups;
        private readonly Dictionary<string, Action<string>> _deliveryCallbacks = new();
        private readonly List<string>               _log = new();

        public ChatServer(string serverId, MessageStore store, ConnectionRegistry registry,
            PresenceService presence, PushNotificationService push, MessageBus bus, GroupStore groups)
        {
            ServerId  = serverId;
            _store    = store;
            _registry = registry;
            _presence = presence;
            _push     = push;
            _bus      = bus;
            _groups   = groups;
        }

        public void ConnectUser(string userId, Action<string> onReceive = null)
        {
            _registry.Register(userId, ServerId);
            _presence.Heartbeat(userId);
            if (onReceive != null) _deliveryCallbacks[userId] = onReceive;

            // Subscribe so this server receives messages published to this user's channel.
            _bus.Subscribe($"user:{userId}", msg => Deliver(userId, msg));
            Log($"[{ServerId}] {userId} connected");

            // Drain offline backlog — deliver any messages that arrived while user was offline.
            foreach (var msg in _store.GetUndelivered(userId))
            {
                msg.Status = DeliveryStatus.Delivered;
                onReceive?.Invoke($"(offline backlog) {msg}");
                Log($"[{ServerId}] Delivered backlog message to {userId}: {msg.MessageId}");
            }
        }

        public void DisconnectUser(string userId)
        {
            _registry.Deregister(userId);
            _presence.Disconnect(userId);
            _deliveryCallbacks.Remove(userId);
            Log($"[{ServerId}] {userId} disconnected");
        }

        public SendResult Send(string senderId, string chatId, string content,
            MessageType type = MessageType.Text, string mediaUrl = null)
        {
            // Step 1: persist before attempting delivery (durability guarantee).
            var msg = _store.Save(chatId, senderId, content, type: type, mediaUrl: mediaUrl);
            Log($"[{ServerId}] Persisted {msg.MessageId} from {senderId}");

            int delivered = 0, pushed = 0;

            foreach (string recipientId in GetRecipients(chatId, senderId))
            {
                bool online = _bus.Publish($"user:{recipientId}", msg);
                if (online)
                {
                    delivered++;
                }
                else
                {
                    string preview = content.Length > 20 ? content[..20] + "..." : content;
                    _push.Send(recipientId, $"New message from {senderId}: \"{preview}\"");
                    pushed++;
                    Log($"[{ServerId}] Push notification sent to offline user {recipientId}");
                }
            }

            return new SendResult(msg.MessageId, delivered, pushed);
        }

        public void MarkRead(string userId, string chatId)
        {
            foreach (var msg in _store.GetHistory(chatId).Where(m => m.SenderId != userId))
                msg.Status = DeliveryStatus.Read;
            Log($"[{ServerId}] {userId} marked {chatId} as read");
        }

        public List<ChatMessage> GetHistory(string chatId, int count = 20, DateTime? before = null)
            => _store.GetHistory(chatId, count, before);

        public IReadOnlyList<string> GetLog() => _log;

        private void Deliver(string userId, ChatMessage msg)
        {
            msg.Status = DeliveryStatus.Delivered;
            Log($"[{ServerId}] Delivered {msg.MessageId} to {userId}");
            if (_deliveryCallbacks.TryGetValue(userId, out var cb)) cb(msg.ToString());
        }

        private IEnumerable<string> GetRecipients(string chatId, string senderId)
        {
            // Group chat: look up member list in GroupStore.
            var members = _groups.GetMembers(chatId);
            if (members != null) return members.Where(m => m != senderId);

            // 1:1 chat: decode recipients from ID format "chat:alice:bob".
            return chatId.Split(':').Skip(1).Where(u => u != senderId);
        }

        private void Log(string msg) => _log.Add(msg);
    }
}
