using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AdvancedDesigns
{
    // ─── Enums ─────────────────────────────────────────────────────────────────

    public enum DeliveryStatus { Sent, Delivered, Read }
    public enum MessageType { Text, Image, Video, File }

    // ─── Message ───────────────────────────────────────────────────────────────

    public class ChatMessage
    {
        public string MessageId { get; }
        public string ChatId { get; }
        public string SenderId { get; }
        public string Content { get; }
        public string MediaUrl { get; }
        public MessageType Type { get; }
        public DateTime SentAt { get; }
        public DeliveryStatus Status { get; set; }
        public int SequenceNumber { get; set; }

        public ChatMessage(string messageId, string chatId, string senderId,
            string content, DateTime sentAt, MessageType type = MessageType.Text, string mediaUrl = null)
        {
            MessageId = messageId;
            ChatId = chatId;
            SenderId = senderId;
            Content = content;
            SentAt = sentAt;
            Type = type;
            MediaUrl = mediaUrl;
            Status = DeliveryStatus.Sent;
        }

        public override string ToString()
        {
            string statusIcon = Status == DeliveryStatus.Read ? "✓✓(read)"
                              : Status == DeliveryStatus.Delivered ? "✓✓"
                              : "✓";
            string media = MediaUrl != null ? $" [media:{MediaUrl}]" : "";
            return $"[{MessageId}] {SenderId}→{ChatId}: \"{Content}\"{media} {statusIcon}";
        }
    }

    // ─── Message Store (simulates Cassandra) ───────────────────────────────────

    public class MessageStore
    {
        // chatId → list of messages sorted by sentAt
        private readonly Dictionary<string, List<ChatMessage>> _chats
            = new Dictionary<string, List<ChatMessage>>();
        private long _idCounter;

        public ChatMessage Save(string chatId, string senderId, string content,
            DateTime? sentAt = null, MessageType type = MessageType.Text, string mediaUrl = null)
        {
            string msgId = $"msg:{Interlocked.Increment(ref _idCounter):D4}";
            var msg = new ChatMessage(msgId, chatId, senderId, content, sentAt ?? DateTime.UtcNow, type, mediaUrl);

            if (!_chats.ContainsKey(chatId)) _chats[chatId] = new List<ChatMessage>();
            _chats[chatId].Add(msg);
            return msg;
        }

        public List<ChatMessage> GetHistory(string chatId, int count = 50, DateTime? before = null)
        {
            if (!_chats.TryGetValue(chatId, out var msgs)) return new List<ChatMessage>();
            IEnumerable<ChatMessage> query = msgs.OrderByDescending(m => m.SentAt);
            if (before.HasValue) query = query.Where(m => m.SentAt < before.Value);
            return query.Take(count).OrderBy(m => m.SentAt).ToList(); // return oldest-first for display
        }

        public ChatMessage GetById(string messageId)
        {
            foreach (var msgs in _chats.Values)
            {
                var msg = msgs.FirstOrDefault(m => m.MessageId == messageId);
                if (msg != null) return msg;
            }
            return null;
        }

        public List<ChatMessage> GetUndelivered(string userId)
        {
            // Messages addressed to a chat where userId is a member, not yet delivered
            return _chats.Values.SelectMany(msgs => msgs)
                .Where(m => m.ChatId.Contains(userId) && m.Status == DeliveryStatus.Sent
                         && m.SenderId != userId)
                .OrderBy(m => m.SentAt)
                .ToList();
        }
    }

    // ─── Connection Registry (simulates Redis user→server mapping) ────────────

    public class ConnectionRegistry
    {
        private readonly Dictionary<string, string> _userToServer = new Dictionary<string, string>();

        public void Register(string userId, string serverId) => _userToServer[userId] = serverId;
        public void Deregister(string userId) => _userToServer.Remove(userId);
        public string GetServer(string userId) =>
            _userToServer.TryGetValue(userId, out var s) ? s : null;
        public bool IsOnline(string userId) => _userToServer.ContainsKey(userId);
    }

    // ─── Presence Service (simulates Redis TTL heartbeat) ─────────────────────

    public class PresenceService
    {
        private readonly Dictionary<string, DateTime> _heartbeats = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _lastSeen = new Dictionary<string, DateTime>();
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        public void Heartbeat(string userId)
        {
            _heartbeats[userId] = DateTime.UtcNow;
            _lastSeen[userId] = DateTime.UtcNow;
        }

        public void Disconnect(string userId)
        {
            _lastSeen[userId] = DateTime.UtcNow;
            _heartbeats.Remove(userId);
        }

        public bool IsOnline(string userId) =>
            _heartbeats.TryGetValue(userId, out var last) && DateTime.UtcNow - last < _timeout;

        public DateTime? GetLastSeen(string userId) =>
            _lastSeen.TryGetValue(userId, out var ts) ? ts : (DateTime?)null;
    }

    // ─── Push Notification Service (simulates APNs / FCM) ────────────────────

    public class PushNotificationService
    {
        private readonly List<(string UserId, string Message, DateTime SentAt)> _log
            = new List<(string, string, DateTime)>();

        public void Send(string userId, string notification)
        {
            _log.Add((userId, notification, DateTime.UtcNow));
        }

        public IReadOnlyList<(string UserId, string Message, DateTime SentAt)> GetLog() => _log;
    }

    // ─── Message Bus (simulates Redis Pub/Sub for cross-server routing) ────────

    public class MessageBus
    {
        // Simulates pub/sub: subscriber callback per channel (userId)
        private readonly Dictionary<string, List<Action<ChatMessage>>> _subscribers
            = new Dictionary<string, List<Action<ChatMessage>>>();

        public void Subscribe(string channel, Action<ChatMessage> handler)
        {
            if (!_subscribers.ContainsKey(channel)) _subscribers[channel] = new List<Action<ChatMessage>>();
            _subscribers[channel].Add(handler);
        }

        public bool Publish(string channel, ChatMessage message)
        {
            if (!_subscribers.TryGetValue(channel, out var handlers) || handlers.Count == 0)
                return false;
            foreach (var h in handlers) h(message);
            return true;
        }
    }

    // ─── Chat Server (handles a set of WebSocket connections) ─────────────────

    public class ChatServer
    {
        public string ServerId { get; }
        private readonly MessageStore _store;
        private readonly ConnectionRegistry _registry;
        private readonly PresenceService _presence;
        private readonly PushNotificationService _push;
        private readonly MessageBus _bus;
        private readonly GroupStore _groups;
        private readonly Dictionary<string, Action<string>> _deliveryCallbacks
            = new Dictionary<string, Action<string>>();
        private readonly List<string> _log = new List<string>();

        public ChatServer(string serverId, MessageStore store, ConnectionRegistry registry,
            PresenceService presence, PushNotificationService push, MessageBus bus, GroupStore groups)
        {
            ServerId = serverId;
            _store = store;
            _registry = registry;
            _presence = presence;
            _push = push;
            _bus = bus;
            _groups = groups;
        }

        public void ConnectUser(string userId, Action<string> onReceive = null)
        {
            _registry.Register(userId, ServerId);
            _presence.Heartbeat(userId);
            if (onReceive != null) _deliveryCallbacks[userId] = onReceive;

            // Subscribe this server to messages for this user
            _bus.Subscribe($"user:{userId}", msg =>
            {
                Deliver(userId, msg);
            });

            Log($"[{ServerId}] {userId} connected");

            // Deliver any pending offline messages
            var pending = _store.GetUndelivered(userId);
            foreach (var msg in pending)
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
            // 1. Persist first (durability before delivery)
            var msg = _store.Save(chatId, senderId, content, type: type, mediaUrl: mediaUrl);
            Log($"[{ServerId}] Persisted {msg.MessageId} from {senderId}");

            // 2. Determine recipients
            var recipients = GetRecipients(chatId, senderId);

            int delivered = 0;
            int pushed = 0;

            foreach (string recipientId in recipients)
            {
                bool online = _bus.Publish($"user:{recipientId}", msg);
                if (online)
                    delivered++;
                else
                {
                    _push.Send(recipientId, $"New message from {senderId}: \"{(content.Length > 20 ? content[..20] + "..." : content)}\"");
                    pushed++;
                    Log($"[{ServerId}] Push notification sent to offline user {recipientId}");
                }
            }

            return new SendResult(msg.MessageId, delivered, pushed);
        }

        public void MarkRead(string userId, string chatId)
        {
            var history = _store.GetHistory(chatId);
            foreach (var msg in history.Where(m => m.SenderId != userId))
                msg.Status = DeliveryStatus.Read;
            Log($"[{ServerId}] {userId} marked {chatId} as read");
        }

        public List<ChatMessage> GetHistory(string chatId, int count = 20, DateTime? before = null)
            => _store.GetHistory(chatId, count, before);

        private void Deliver(string userId, ChatMessage msg)
        {
            msg.Status = DeliveryStatus.Delivered;
            Log($"[{ServerId}] Delivered {msg.MessageId} to {userId}");
            _deliveryCallbacks.TryGetValue(userId, out var cb);
            cb?.Invoke(msg.ToString());
        }

        private IEnumerable<string> GetRecipients(string chatId, string senderId)
        {
            // Group chat
            var members = _groups.GetMembers(chatId);
            if (members != null)
                return members.Where(m => m != senderId);

            // 1:1 chat — chat_id format: "chat:alice:bob"
            var parts = chatId.Split(':');
            return parts.Skip(1).Where(u => u != senderId);
        }

        public IReadOnlyList<string> GetLog() => _log;
        private void Log(string msg) => _log.Add(msg);
    }

    public class SendResult
    {
        public string MessageId { get; }
        public int OnlineDelivered { get; }
        public int PushSent { get; }

        public SendResult(string msgId, int online, int push)
        {
            MessageId = msgId;
            OnlineDelivered = online;
            PushSent = push;
        }
    }

    // ─── Group Store ───────────────────────────────────────────────────────────

    public class GroupStore
    {
        private readonly Dictionary<string, HashSet<string>> _groups
            = new Dictionary<string, HashSet<string>>();

        public void CreateGroup(string groupId, params string[] members)
        {
            _groups[groupId] = new HashSet<string>(members);
        }

        public void AddMember(string groupId, string userId)
        {
            if (_groups.ContainsKey(groupId)) _groups[groupId].Add(userId);
        }

        public HashSet<string> GetMembers(string chatId) =>
            _groups.TryGetValue(chatId, out var m) ? m : null;
    }

    // ─── Main Program ──────────────────────────────────────────────────────────

    class Program
    {
        static MessageStore _store;
        static ConnectionRegistry _registry;
        static PresenceService _presence;
        static PushNotificationService _push;
        static MessageBus _bus;
        static GroupStore _groups;

        static void Main(string[] args)
        {
            Console.WriteLine("=== Real-Time Chat Demo ===\n");

            Scenario1_Online1to1Chat();
            Scenario2_OfflineDelivery();
            Scenario3_GroupChatFanOut();
            Scenario4_ReadReceipts();
            Scenario5_PresenceAndLastSeen();
            Scenario6_MessageHistory();
        }

        static void ResetSystem()
        {
            _store = new MessageStore();
            _registry = new ConnectionRegistry();
            _presence = new PresenceService();
            _push = new PushNotificationService();
            _bus = new MessageBus();
            _groups = new GroupStore();
        }

        static ChatServer MakeServer(string id) =>
            new ChatServer(id, _store, _registry, _presence, _push, _bus, _groups);

        // ── Scenario 1: Online 1:1 Chat ───────────────────────────────────────

        static void Scenario1_Online1to1Chat()
        {
            Console.WriteLine("─── Scenario 1: Online 1:1 Chat (Alice → Bob) ───");
            ResetSystem();

            var server1 = MakeServer("Server1");
            var server2 = MakeServer("Server2");

            var aliceReceived = new List<string>();
            var bobReceived = new List<string>();

            server1.ConnectUser("alice", m => aliceReceived.Add(m));
            server2.ConnectUser("bob", m => bobReceived.Add(m));

            Console.WriteLine("Alice and Bob both online on different servers");

            // Alice sends to bob — chat ID is "chat:alice:bob"
            var r1 = server1.Send("alice", "chat:alice:bob", "Hey Bob, what's up?");
            Console.WriteLine($"\nalice sends: \"Hey Bob, what's up?\"");
            Console.WriteLine($"  → delivered online to {r1.OnlineDelivered} user, push to {r1.PushSent}");
            Console.WriteLine($"  Bob receives: {bobReceived.LastOrDefault()}");

            var r2 = server1.Send("alice", "chat:alice:bob", "Are you free tonight?");
            Console.WriteLine($"\nalice sends: \"Are you free tonight?\"");
            Console.WriteLine($"  Bob receives: {bobReceived.LastOrDefault()}");

            // Bob replies
            var r3 = server2.Send("bob", "chat:alice:bob", "Hey! Yes, totally free.");
            Console.WriteLine($"\nbob replies: \"Hey! Yes, totally free.\"");
            Console.WriteLine($"  Alice receives: {aliceReceived.LastOrDefault()}");

            Console.WriteLine($"\nServer routing log (cross-server delivery via pub/sub):");
            foreach (var line in server1.GetLog().Concat(server2.GetLog()))
                Console.WriteLine($"  {line}");

            Console.WriteLine();
        }

        // ── Scenario 2: Offline Delivery + Push Notification ─────────────────

        static void Scenario2_OfflineDelivery()
        {
            Console.WriteLine("─── Scenario 2: Offline Delivery + Push Notification ───");
            ResetSystem();

            var server1 = MakeServer("Server1");
            var server3 = MakeServer("Server3");

            server1.ConnectUser("alice");
            // Bob is OFFLINE (never connected)

            Console.WriteLine("Bob is offline. Alice sends 2 messages:");
            var r1 = server1.Send("alice", "chat:alice:bob", "Where are you?");
            var r2 = server1.Send("alice", "chat:alice:bob", "Dinner is getting cold!");
            Console.WriteLine($"  Msg 1: online={r1.OnlineDelivered}, push_sent={r1.PushSent}");
            Console.WriteLine($"  Msg 2: online={r2.OnlineDelivered}, push_sent={r2.PushSent}");

            Console.WriteLine("\nPush notifications sent:");
            foreach (var n in _push.GetLog())
                Console.WriteLine($"  → {n.UserId}: \"{n.Message}\"");

            Console.WriteLine("\nBob comes online (connects to Server3):");
            var bobReceived = new List<string>();
            server3.ConnectUser("bob", m => bobReceived.Add(m));

            Console.WriteLine("Bob's inbox (backlog delivered on connect):");
            foreach (var m in bobReceived) Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 3: Group Chat Fan-out ────────────────────────────────────

        static void Scenario3_GroupChatFanOut()
        {
            Console.WriteLine("─── Scenario 3: Group Chat Fan-out ───");
            ResetSystem();

            _groups.CreateGroup("group:family", "alice", "bob", "carol", "dave");

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");
            var s3 = MakeServer("Server3");

            var bobMsgs = new List<string>();
            var carolMsgs = new List<string>();

            s1.ConnectUser("alice");
            s2.ConnectUser("bob", m => bobMsgs.Add(m));
            s3.ConnectUser("carol", m => carolMsgs.Add(m));
            // dave is offline

            Console.WriteLine("Group 'family': alice, bob, carol online; dave offline");
            Console.WriteLine("alice sends: \"Dinner at 7 tonight?\"");

            var r = s1.Send("alice", "group:family", "Dinner at 7 tonight?");
            Console.WriteLine($"  Fan-out: {r.OnlineDelivered} online deliveries, {r.PushSent} push notifications");
            Console.WriteLine($"  Bob received:   {bobMsgs.LastOrDefault()}");
            Console.WriteLine($"  Carol received: {carolMsgs.LastOrDefault()}");

            Console.WriteLine("\nPush notifications (for offline dave):");
            foreach (var n in _push.GetLog())
                Console.WriteLine($"  → {n.UserId}: \"{n.Message}\"");

            Console.WriteLine();
        }

        // ── Scenario 4: Delivery Receipts (Sent → Delivered → Read) ──────────

        static void Scenario4_ReadReceipts()
        {
            Console.WriteLine("─── Scenario 4: Delivery Receipts (✓ → ✓✓ → ✓✓read) ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");

            s1.ConnectUser("alice");
            s2.ConnectUser("bob");

            // Alice sends — message becomes SENT immediately
            var r = s1.Send("alice", "chat:alice:bob", "Can you review the PR?");
            var msg = _store.GetById(r.MessageId);
            Console.WriteLine($"After send:    {msg}");

            // Delivery happens automatically via pub/sub
            // Message status updated to DELIVERED by the server that handled delivery
            Console.WriteLine($"After deliver: {msg}");

            // Bob explicitly reads
            s2.MarkRead("bob", "chat:alice:bob");
            Console.WriteLine($"After read:    {msg}");

            Console.WriteLine();
        }

        // ── Scenario 5: Presence & Last Seen ─────────────────────────────────

        static void Scenario5_PresenceAndLastSeen()
        {
            Console.WriteLine("─── Scenario 5: Presence and Last Seen ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            s1.ConnectUser("alice");
            s1.ConnectUser("bob");

            _presence.Heartbeat("alice");
            _presence.Heartbeat("bob");

            Console.WriteLine("Both connected and heartbeat sent:");
            Console.WriteLine($"  alice online: {_presence.IsOnline("alice")}");
            Console.WriteLine($"  bob   online: {_presence.IsOnline("bob")}");

            // Bob disconnects
            s1.DisconnectUser("bob");
            Console.WriteLine("\nBob disconnects:");
            Console.WriteLine($"  bob   online: {_presence.IsOnline("bob")}");
            var lastSeen = _presence.GetLastSeen("bob");
            Console.WriteLine($"  bob last seen: {lastSeen:HH:mm:ss} UTC");

            // Simulate heartbeat timeout (30+ seconds without heartbeat)
            // We simulate by using a stale heartbeat time
            Console.WriteLine("\nSimulating heartbeat timeout for carol (no heartbeat for 30s+):");
            // Manually set a stale heartbeat by calling presence directly
            _presence.Heartbeat("carol"); // registers carol
            // We can't actually wait 30s in a demo, so we check the concept
            Console.WriteLine($"  carol online right after heartbeat: {_presence.IsOnline("carol")}");
            Console.WriteLine($"  (In production: after 30s without heartbeat, Redis key expires → offline)");

            Console.WriteLine("\nalice checks bob's status:");
            Console.WriteLine($"  online={_presence.IsOnline("bob")}, last_seen={_presence.GetLastSeen("bob"):HH:mm:ss}");

            Console.WriteLine();
        }

        // ── Scenario 6: Message History with Cursor Pagination ────────────────

        static void Scenario6_MessageHistory()
        {
            Console.WriteLine("─── Scenario 6: Message History (Cursor Pagination) ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");
            s1.ConnectUser("alice");
            s2.ConnectUser("bob");

            // Produce 12 messages with spread-out timestamps
            var baseTime = DateTime.UtcNow.AddMinutes(-120);
            for (int i = 1; i <= 12; i++)
            {
                string sender = i % 2 == 0 ? "bob" : "alice";
                _store.Save("chat:alice:bob", sender, $"Message {i:D2}", baseTime.AddMinutes(i * 10));
            }

            Console.WriteLine("12 messages in chat history. Loading in pages of 4:\n");

            // Page 1 — most recent 4
            var page1 = _store.GetHistory("chat:alice:bob", count: 4);
            Console.WriteLine($"Page 1 (oldest 4):");
            foreach (var m in page1) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");
            DateTime cursor = page1.First().SentAt; // earliest in this batch

            // Page 2 — next 4 older
            var page2 = _store.GetHistory("chat:alice:bob", count: 4, before: cursor);
            Console.WriteLine($"\nPage 2 (next 4, before cursor={cursor:HH:mm}):");
            foreach (var m in page2) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");

            // Page 3
            if (page2.Count > 0)
            {
                DateTime cursor2 = page2.First().SentAt;
                var page3 = _store.GetHistory("chat:alice:bob", count: 4, before: cursor2);
                Console.WriteLine($"\nPage 3 (next 4, before cursor={cursor2:HH:mm}):");
                foreach (var m in page3) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");
            }

            Console.WriteLine("\n(Cursor = timestamp of oldest message on current page → stable under new messages)");
        }
    }
}
