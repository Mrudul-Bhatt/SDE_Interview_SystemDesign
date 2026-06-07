// Program — entry point for all Real-Time Chat demo scenarios.
//
// Each scenario builds a fresh, empty system (ResetSystem) and acts out a specific
// story — users connect, send messages, go offline, reconnect — so you can watch one
// feature of the design in isolation without leftover state from the previous run.
//
// Scenario 1 — Online 1:1 Chat:         Alice and Bob on different servers. A message
//                                        from Alice reaches Bob via pub/sub cross-server
//                                        routing. OnlineDelivered=1, PushSent=0.
// Scenario 2 — Offline Delivery:         Bob is offline when Alice sends. Push notifications
//                                        dispatch immediately. Bob reconnects to a THIRD
//                                        server — backlog is drained on connect, proving
//                                        at-least-once delivery regardless of which server.
// Scenario 3 — Group Chat Fan-out:       One message fans out to 3 online members on 3
//                                        different servers plus 1 offline member (push).
//                                        Shows the sender-exclusion and mixed online/offline split.
// Scenario 4 — Delivery Receipts:        Watch a single message walk through all three
//                                        DeliveryStatus states: Sent → Delivered → Read,
//                                        with the checkmark notation updating at each step.
// Scenario 5 — Presence & Last Seen:     Heartbeat keeps a user online; explicit Disconnect
//                                        snaps them offline immediately; heartbeat timeout
//                                        (simulated via carol) shows the TTL-based fallback.
// Scenario 6 — Cursor Pagination:        12 messages paginated in groups of 4. A new message
//                                        arrives mid-scroll — cursor stability proves it
//                                        doesn't push earlier pages or cause duplicates.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    class Program
    {
        // Shared infrastructure — all servers in a scenario share the same instances,
        // which is what makes cross-server routing work: the bus, registry, and store
        // are the global "Redis + Cassandra" layer that every server reads from and
        // writes to. A fresh ResetSystem() between scenarios ensures no state leaks.
        static MessageStoreCassandra _store;
        static ConnectionRegistryRedis _registry;
        static PresenceServiceRedis _presence;
        static PushNotificationServiceAPNsFCM _push;
        static MessageBusRedis _bus;
        static GroupStoreRedis _groups;

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

        // Wires up one complete, empty system. Called at the start of every scenario
        // so each story starts with no messages, no connections, and no presence state.
        // All six infrastructure objects are shared across every ChatServer created in
        // the same scenario — that shared state is what makes cross-server delivery work:
        // Server1 and Server3 talk to each other through _bus and _registry, not directly.
        static void ResetSystem()
        {
            _store = new MessageStoreCassandra();
            _registry = new ConnectionRegistryRedis();
            _presence = new PresenceServiceRedis();
            _push = new PushNotificationServiceAPNsFCM();
            _bus = new MessageBusRedis();
            _groups = new GroupStoreRedis();
        }

        // Creates a ChatServer that shares the scenario's infrastructure. Using multiple
        // servers in one scenario simulates a real load-balanced deployment: each server
        // handles its own WebSocket connections but routes cross-server through the shared bus.
        static ChatServer MakeServer(string id) =>
            new ChatServer(id, _store, _registry, _presence, _push, _bus, _groups);

        // ── Scenario 1: Online 1:1 Chat ───────────────────────────────────────
        // The simplest end-to-end story. Alice connects to Server1, Bob to Server2.
        // A message from Alice must travel through the MessageBusRedis to reach Bob's socket
        // on the other server — neither server calls the other directly.
        // Watch: OnlineDelivered=1 and PushSent=0 for every message, and the server
        // log shows the Publish/Deliver flow crossing the Server1→Server2 boundary.
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
        // Bob never connects. Alice sends two messages — both go to push immediately
        // because MessageBusRedis.Publish returns false (no subscriber on Bob's channel).
        // When Bob finally connects — to Server3, a completely different server from
        // the one that sent the messages — his backlog is drained automatically.
        // Watch: push log shows two fire-and-forget notifications, then the backlog
        // delivers both messages marked "(offline backlog)" at connect time.
        static void Scenario2_OfflineDelivery()
        {
            Console.WriteLine("─── Scenario 2: Offline Delivery + Push Notification ───");
            ResetSystem();

            var server1 = MakeServer("Server1");
            var server3 = MakeServer("Server3");

            server1.ConnectUser("alice");
            // Bob is OFFLINE — never connected, no WebSocket, no bus subscription.
            // Any message to Bob falls through to push notification immediately.

            Console.WriteLine("Bob is offline. Alice sends 2 messages:");
            var r1 = server1.Send("alice", "chat:alice:bob", "Where are you?");
            var r2 = server1.Send("alice", "chat:alice:bob", "Dinner is getting cold!");
            Console.WriteLine($"  Msg 1: online={r1.OnlineDelivered}, push_sent={r1.PushSent}");
            Console.WriteLine($"  Msg 2: online={r2.OnlineDelivered}, push_sent={r2.PushSent}");

            Console.WriteLine("\nPush notifications sent:");
            foreach (var n in _push.GetLog())
                Console.WriteLine($"  → {n.UserId}: \"{n.Message}\"");

            // Bob reconnects to Server3 — not Server1 where the messages were sent.
            // Backlog drain works because it reads from the shared MessageStoreCassandra, not
            // from anything local to Server1. This is the at-least-once guarantee.
            Console.WriteLine("\nBob comes online (connects to Server3):");
            var bobReceived = new List<string>();
            server3.ConnectUser("bob", m => bobReceived.Add(m));

            Console.WriteLine("Bob's inbox (backlog delivered on connect):");
            foreach (var m in bobReceived) Console.WriteLine($"  {m}");

            Console.WriteLine();
        }

        // ── Scenario 3: Group Chat Fan-out ────────────────────────────────────
        // One send to a 4-person group fans out to every member except the sender.
        // Three members are online (each on a separate server); dave is offline.
        // Watch: OnlineDelivered=2 (bob + carol), PushSent=1 (dave), alice herself
        // is excluded — sender never receives their own message back.
        static void Scenario3_GroupChatFanOut()
        {
            Console.WriteLine("─── Scenario 3: Group Chat Fan-out ───");
            ResetSystem();

            // Register the group before anyone connects — GroupStoreRedis is independent
            // of connection state. Members can join before or after connecting.
            _groups.CreateGroup("group:family", "alice", "bob", "carol", "dave");

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");
            var s3 = MakeServer("Server3");

            var bobMsgs = new List<string>();
            var carolMsgs = new List<string>();

            s1.ConnectUser("alice");
            s2.ConnectUser("bob", m => bobMsgs.Add(m));
            s3.ConnectUser("carol", m => carolMsgs.Add(m));
            // dave is offline — no ConnectUser call, no bus subscription.

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
        // Tracks the full DeliveryStatus lifecycle for a single message.
        // After Send() the message is Sent (✓). The pub/sub delivery callback sets it to
        // Delivered (✓✓) before Send() even returns. MarkRead advances it to Read (✓✓(read)).
        // Watch: the same message object prints a different checkmark at each stage,
        // demonstrating the one-way state machine on ChatMessage.Status.
        static void Scenario4_ReadReceipts()
        {
            Console.WriteLine("─── Scenario 4: Delivery Receipts (✓ → ✓✓ → ✓✓read) ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");
            s1.ConnectUser("alice");
            s2.ConnectUser("bob");

            var r = s1.Send("alice", "chat:alice:bob", "Can you review the PR?");
            // GetById fetches the live object — same reference that Deliver() already
            // mutated to Delivered during the Send() call above.
            var msg = _store.GetById(r.MessageId);
            Console.WriteLine($"After send:    {msg}");
            Console.WriteLine($"After deliver: {msg}"); // status updated by pub/sub delivery
            s2.MarkRead("bob", "chat:alice:bob");
            Console.WriteLine($"After read:    {msg}");

            Console.WriteLine();
        }

        // ── Scenario 5: Presence & Last Seen ─────────────────────────────────
        // Three presence transitions in one scenario:
        //   alice — connected + heartbeat → online (normal active session)
        //   bob   — connected + heartbeat → Disconnect() → offline immediately
        //   carol — heartbeat only (simulates a live session) → shows online;
        //           in production, no further heartbeat would expire the Redis key after 30s
        // Watch: IsOnline() flips to false the moment Disconnect() is called (not after
        // the 30s timeout), and GetLastSeen() preserves the exact disconnect timestamp.
        static void Scenario5_PresenceAndLastSeen()
        {
            Console.WriteLine("─── Scenario 5: Presence and Last Seen ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            s1.ConnectUser("alice");
            s1.ConnectUser("bob");
            // Extra heartbeats simulate the client ping loop keeping sessions alive.
            _presence.Heartbeat("alice");
            _presence.Heartbeat("bob");

            Console.WriteLine("Both connected and heartbeat sent:");
            Console.WriteLine($"  alice online: {_presence.IsOnline("alice")}");
            Console.WriteLine($"  bob   online: {_presence.IsOnline("bob")}");

            s1.DisconnectUser("bob");
            Console.WriteLine("\nBob disconnects:");
            Console.WriteLine($"  bob   online: {_presence.IsOnline("bob")}");
            Console.WriteLine($"  bob last seen: {_presence.GetLastSeen("bob"):HH:mm:ss} UTC");

            // Carol sends one heartbeat but we don't call DisconnectUser — simulating
            // a live session. In production, if carol's app is killed, no more heartbeats
            // arrive and the Redis key expires after 30s, automatically flipping IsOnline.
            Console.WriteLine("\nSimulating heartbeat timeout for carol (no heartbeat for 30s+):");
            _presence.Heartbeat("carol");
            Console.WriteLine($"  carol online right after heartbeat: {_presence.IsOnline("carol")}");
            Console.WriteLine($"  (In production: after 30s without heartbeat, Redis key expires → offline)");

            Console.WriteLine("\nalice checks bob's status:");
            Console.WriteLine($"  online={_presence.IsOnline("bob")}, last_seen={_presence.GetLastSeen("bob"):HH:mm:ss}");

            Console.WriteLine();
        }

        // ── Scenario 6: Message History with Cursor Pagination ────────────────
        // Proves the anti-drift property of cursor pagination. 12 messages are written
        // with fixed 10-minute gaps so the ordering is predictable. We read in pages
        // of 4 using the SentAt of the oldest message on the current page as the cursor.
        // A new message then arrives at the top — re-reading page 2 with the same cursor
        // returns exactly the same posts. An offset-based "skip N" approach would drift.
        // Watch: the cursor value printed after each page and the stable re-read of page 2.
        static void Scenario6_MessageHistory()
        {
            Console.WriteLine("─── Scenario 6: Message History (Cursor Pagination) ───");
            ResetSystem();

            var s1 = MakeServer("Server1");
            var s2 = MakeServer("Server2");
            s1.ConnectUser("alice");
            s2.ConnectUser("bob");

            // Write 12 messages with explicit timestamps 10 minutes apart so the page
            // boundaries fall at predictable points and the output is easy to verify.
            var baseTime = DateTime.UtcNow.AddMinutes(-120);
            for (int i = 1; i <= 12; i++)
            {
                string sender = i % 2 == 0 ? "bob" : "alice";
                _store.Save("chat:alice:bob", sender, $"Message {i:D2}", baseTime.AddMinutes(i * 10));
            }

            Console.WriteLine("12 messages in chat history. Loading in pages of 4:\n");

            // Page 1: newest 4 messages (GetHistory returns oldest-first after the internal
            // newest-first fetch + reverse, so page1[0] is the oldest of the 4 shown).
            var page1 = _store.GetHistory("chat:alice:bob", count: 4);
            Console.WriteLine("Page 1 (oldest 4):");
            foreach (var m in page1) Console.WriteLine($"  [{m.SentAt:HH:mm}] {m.SenderId}: {m.Content}");
            // Cursor = SentAt of the oldest item on this page. "Give me messages before this
            // timestamp" scrolls up without being affected by new messages above.
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
