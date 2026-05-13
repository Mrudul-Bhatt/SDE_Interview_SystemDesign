// Q5. Implement an In-Memory Chat Room (WebSocket Message Broadcaster)
// Multiple clients connect to a chat room.  When one client sends a message, all OTHER
// connected clients receive it.  Handle join, leave, and message-history replay.
//
// In a real WebSocket server the Queue<string> inbox is replaced with WebSocket.SendAsync().

using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// ChatRoom — in-memory pub/sub broadcaster (simulates a WebSocket chat server)
// ---------------------------------------------------------------------------
public class ChatRoom
{
    // Human-readable room name shown in every log line (e.g. "general", "dev-team").
    private readonly string _roomId;

    // Maps clientId -> their private inbox queue.
    // WHY Queue: messages are consumed in broadcast order (FIFO).
    // In real WebSocket code the value would be a WebSocket handle and you'd call
    // WebSocket.SendAsync() instead of Enqueue() — the pattern is identical.
    private readonly Dictionary<string, Queue<string>> _clients = new();

    // Rolling message history replayed to late joiners so they see recent context
    // without a separate "fetch history" API call.
    private readonly List<string> _history = new();

    // Cap on history length — bounds memory regardless of how long the room stays open.
    private readonly int _historyLimit;

    // Single lock guards _clients and _history from concurrent Join/Leave/Send calls.
    // All public methods lock the same object so no two threads corrupt the collections.
    private readonly object _lock = new();

    // historyLimit — how many recent messages to replay when a new client joins.
    public ChatRoom(string roomId, int historyLimit = 50)
    {
        _roomId = roomId;
        _historyLimit = historyLimit;
    }

    // Adds a client and immediately replays recent history into their inbox.
    // Replay avoids a separate "load history" round-trip for the new client.
    public void Join(string clientId)
    {
        lock (_lock)
        {
            // Create an empty inbox; overwriting is safe (idempotent re-join).
            _clients[clientId] = new Queue<string>();
            Console.WriteLine($"[{_roomId}] {clientId} joined ({_clients.Count} online)");

            // Enqueue history so the new client sees recent messages immediately.
            foreach (var msg in _history)
                _clients[clientId].Enqueue(msg);
        }
    }

    // Removes the client and broadcasts a system "left" notification to everyone still online.
    public void Leave(string clientId)
    {
        lock (_lock)
        {
            _clients.Remove(clientId);
            Console.WriteLine($"[{_roomId}] {clientId} left ({_clients.Count} online)");

            // Broadcast AFTER Remove so the leaver does not receive their own departure notice.
            Broadcast("system", $"{clientId} has left the room");
        }
    }

    // Fans a message out to all clients except the sender.
    // The sender already knows what they typed; echoing back wastes bandwidth.
    public void SendMessage(string senderId, string text)
    {
        lock (_lock)
        {
            // Guard: reject messages from clients who have not called Join().
            if (!_clients.ContainsKey(senderId))
            {
                Console.WriteLine($"[ERROR] {senderId} is not in room {_roomId}");
                return;
            }

            // Timestamp each message so clients display correct chronological order
            // even if network reordering causes slightly out-of-order delivery on the wire.
            string message = $"[{DateTime.UtcNow:HH:mm:ss}] {senderId}: {text}";

            // Append to history; evict the oldest entry if we have hit the cap.
            // RemoveAt(0) is O(n) — acceptable because _historyLimit is small (50 by default).
            _history.Add(message);
            if (_history.Count > _historyLimit)
                _history.RemoveAt(0);

            // Fan-out: push to every connected client except the sender.
            foreach (var (clientId, inbox) in _clients)
                if (clientId != senderId)
                    inbox.Enqueue(message);

            Console.WriteLine($"[SENT] {message}");
        }
    }

    // Delivers to ALL currently connected clients including the "sender".
    // Used for system events (join/leave) where there is no logical sender to exclude.
    private void Broadcast(string sender, string text)
    {
        string message = $"[{DateTime.UtcNow:HH:mm:ss}] {sender}: {text}";
        foreach (var (_, inbox) in _clients)
            inbox.Enqueue(message);
    }

    // Drains and returns all queued messages for one client (simulates a WebSocket push).
    // After this call the inbox is empty; next read only returns newly arrived messages.
    public List<string> ReadMessages(string clientId)
    {
        lock (_lock)
        {
            var messages = new List<string>();
            if (!_clients.TryGetValue(clientId, out var inbox)) return messages;
            while (inbox.Count > 0) messages.Add(inbox.Dequeue());
            return messages;
        }
    }
}

// ---------------------------------------------------------------------------
// Entry point — demo
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        Console.WriteLine("╔═══════════════════════════════════════╗");
        Console.WriteLine("║  Q5: WebSocket Chat Room Broadcaster  ║");
        Console.WriteLine("╚═══════════════════════════════════════╝\n");

        var room = new ChatRoom("general");

        // Three clients connect; each gets the current history replayed (empty here).
        room.Join("alice");
        room.Join("bob");
        room.Join("charlie");
        Console.WriteLine();

        // Alice sends -> bob and charlie receive; alice does NOT get her own message back.
        room.SendMessage("alice", "Hey everyone!");
        // Bob sends -> alice and charlie receive.
        room.SendMessage("bob", "Hi Alice!");

        // Bob reads his inbox: only Alice's message (not his own).
        var bobMessages = room.ReadMessages("bob");
        Console.WriteLine("\nBob's inbox:");
        foreach (var msg in bobMessages) Console.WriteLine($"  {msg}");

        // Charlie leaves -> alice and bob receive the system departure notice.
        Console.WriteLine();
        room.Leave("charlie");

        // Alice's inbox: Bob's "Hi Alice!" + the system "charlie has left" notice.
        var aliceMessages = room.ReadMessages("alice");
        Console.WriteLine("\nAlice's inbox:");
        foreach (var msg in aliceMessages) Console.WriteLine($"  {msg}");

        Console.WriteLine("\n--- Scaling beyond a single server ---");
        Console.WriteLine("  Single server  : all clients in-memory -> simple, ~10k connections max");
        Console.WriteLine("  Multi-server   : Alice on S1, Bob on S2 -> S1 cannot push to Bob directly");
        Console.WriteLine("  Redis Pub/Sub  : S1 publishes to channel 'room:general'");
        Console.WriteLine("                   S2 (subscribed) receives it and pushes to Bob");
        Console.WriteLine("  [Alice]->[S1]->Redis->[S2]->[Bob]");
        Console.WriteLine("                       ->[S3]->[Charlie]");
    }
}
