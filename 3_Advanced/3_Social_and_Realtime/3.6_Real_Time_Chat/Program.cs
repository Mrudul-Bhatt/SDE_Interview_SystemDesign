// Program — entry point for all Real-Time Chat demo scenarios.

namespace AdvancedDesigns
{
    class Program
    {
        static MessageStore            _store;
        static ConnectionRegistry      _registry;
        static PresenceService         _presence;
        static PushNotificationService _push;
        static MessageBus              _bus;
        static GroupStore              _groups;

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
            _store    = new MessageStore();
            _registry = new ConnectionRegistry();
            _presence = new PresenceService();
            _push     = new PushNotificationService();
            _bus      = new MessageBus();
            _groups   = new GroupStore();
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
            var bobReceived   = new List<string>();

            server1.ConnectUser("alice", m => aliceReceived.Add(m));
            server2.ConnectUser("bob",   m => bobReceived.Add(m));
            Console.WriteLine("Alice and Bob both online on different servers");

            var r1 = server1.Send("alice", "chat:alice:bob", "Hey Bob, what's up?");
            Console.WriteLine($"\nalice sends: \"Hey Bob, what's up?\"");
            Console.WriteLine($"  → delivered online to {r1.OnlineDelivered} user, push to {r1.PushSent}");
            Console.WriteLine($"  Bob receives: {bobReceived.LastOrDefault()}");

            var r2 = server1.Send("alice", "chat:alice:bob", "Are you free tonight?");
            Console.WriteLine($"\nalice sends: \"Are you free tonight?\"");
            Console.WriteLine($"  Bob receives: {bobReceived.LastOrDefault()}");

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
            // Bob is OFFLINE — never connected

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

            var bobMsgs   = new List<string>();
            var carolMsgs = new List<string>();

            s1.ConnectUser("alice");
            s2.ConnectUser("bob",   m => bobMsgs.Add(m));
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

            var r   = s1.Send("alice", "chat:alice:bob", "Can you review the PR?");
            var msg = _store.GetById(r.MessageId);
            Console.WriteLine($"After send:    {msg}");
            Console.WriteLine($"After deliver: {msg}"); // status updated by pub/sub delivery
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

            s1.DisconnectUser("bob");
            Console.WriteLine("\nBob disconnects:");
            Console.WriteLine($"  bob   online: {_presence.IsOnline("bob")}");
            Console.WriteLine($"  bob last seen: {_presence.GetLastSeen("bob"):HH:mm:ss} UTC");

            Console.WriteLine("\nSimulating heartbeat timeout for carol (no heartbeat for 30s+):");
            _presence.Heartbeat("carol");
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

            var baseTime = DateTime.UtcNow.AddMinutes(-120);
            for (int i = 1; i <= 12; i++)
            {
                string sender = i % 2 == 0 ? "bob" : "alice";
                _store.Save("chat:alice:bob", sender, $"Message {i:D2}", baseTime.AddMinutes(i * 10));
            }

            Console.WriteLine("12 messages in chat history. Loading in pages of 4:\n");

            var page1 = _store.GetHistory("chat:alice:bob", count: 4);
            Console.WriteLine("Page 1 (oldest 4):");
            foreach (var m in page1) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");
            DateTime cursor = page1.First().SentAt;

            var page2 = _store.GetHistory("chat:alice:bob", count: 4, before: cursor);
            Console.WriteLine($"\nPage 2 (next 4, before cursor={cursor:HH:mm}):");
            foreach (var m in page2) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");

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
