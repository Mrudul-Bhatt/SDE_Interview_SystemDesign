// MessageStoreCassandra — durable message persistence (simulates Cassandra).
//
// THE BIG IDEA:
// MessageStoreCassandra is the single source of truth for every message ever sent.
// Everything else in the chat system is ephemeral — WebSocket connections drop,
// Redis pub/sub channels disappear when servers restart, push notifications may
// never arrive. MessageStoreCassandra is what makes the system reliable: a message written
// here is never lost, and any user who reconnects can always fetch what they missed.
//
// The store is organised by chatId — all messages for a given conversation live
// together. The primary access pattern is "give me the last N messages in chat X
// before time T," which maps to a single-partition range scan in Cassandra:
//
//   Partition key  = chatId   → routes the query to exactly one Cassandra node
//   Clustering key = sentAt DESC → rows are stored newest-first on disk
//
// GetHistory fetches the newest N rows first (one seek, no full scan), then
// reverses them for chronological display. This keeps read latency O(count),
// not O(total messages in the chat).
//
// WHY CASSANDRA (not a relational DB):
// Chat history is append-only and never updated (only Status changes in place).
// Cassandra is optimised for exactly this: high-throughput sequential writes and
// fast time-range reads on a known partition key. A SQL database would require
// an index on (chatId, sentAt) and would struggle under the write volume of a
// busy chat system — millions of messages per second across thousands of chats.
//
// WHY INTERLOCKED.INCREMENT (not a lock or Guid):
// Save() is called concurrently — one call per incoming WebSocket message, and
// multiple users can send at the same millisecond. Interlocked.Increment is an
// atomic CPU instruction: it increments _idCounter and returns the new value in
// one uninterruptible step, so two concurrent calls can never get the same number.
// A plain ++ would have a read-modify-write race. A lock around the whole method
// would serialise all saves unnecessarily. A Guid would be unique but not
// sortable — the zero-padded counter gives IDs that sort chronologically within
// a session, useful for debugging and demo output.
//
// WHY GETUNDELIVERED SCANS ALL CHATS (and what the real fix is):
// To find messages waiting for a user who just reconnected, the demo checks every
// chat's message list and filters by chatId.Contains(userId). This is O(total messages)
// — fine for a demo with a handful of chats, completely unacceptable in production.
// Real systems maintain a separate inbox index: a Redis SET or Cassandra row keyed
// on userId listing every chatId they belong to. GetUndelivered would then look up
// the index first (O(1)) and scan only those chats — O(missed messages), not O(all).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AdvancedDesigns
{
    public class MessageStoreCassandra
    {
        // chatId → ordered list of messages, appended in arrival order.
        // Key presence means the chat has at least one message; absence means
        // the chat is new or never had messages written through this store instance.
        //
        // ── RUNTIME SNAPSHOT (Scenario 1: 3 messages between Alice and Bob) ──
        //   _chats = {
        //       "chat:alice:bob" → [
        //           { msg:0001  alice→ "Hey Bob, what's up?"   seq=0  Status=Delivered },
        //           { msg:0002  alice→ "Are you free tonight?" seq=0  Status=Delivered },
        //           { msg:0003  bob→   "Hey! Yes, totally free." seq=0 Status=Delivered }
        //       ]
        //   }
        //   After a group send (Scenario 3) a SECOND partition appears alongside it:
        //       "group:family" → [ { msg:0004  alice→ "Dinner at 7 tonight?" } ]
        //
        //   GetHistory("chat:alice:bob", count:2) → newest 2, reversed to oldest-first:
        //       [ msg:0002, msg:0003 ]   (one partition scan, no cross-chat work)
        private readonly Dictionary<string, List<ChatMessage>> _chats = [];

        // Monotonically increasing counter used to generate sequential message IDs.
        // Must be accessed only via Interlocked.Increment — never read or written directly
        // (see header for why a plain ++ would cause duplicate IDs under concurrency).
        //
        // ── RUNTIME SNAPSHOT ──  After 3 saves: _idCounter = 3.
        //   The NEXT Save() does Interlocked.Increment → 4 → formats "msg:0004" (D4 = 4 digits).
        //   So the value is always "how many messages this store instance has ever saved."
        private long _idCounter;

        // Persists a new message and returns the fully constructed ChatMessage.
        // This is the only write path — every message in the system enters through here.
        // The returned object is what ChatServer fans out to recipients; callers should
        // not construct ChatMessage directly, as Save assigns the canonical MessageId.
        public ChatMessage Save(string chatId, string senderId, string content,
            DateTime? sentAt = null, MessageType type = MessageType.Text, string mediaUrl = null)
        {
            // Zero-padded counter keeps IDs lexicographically sortable for debug output.
            // In production this is a Snowflake ID generated by the client (see ChatMessage).
            string msgId = $"msg:{Interlocked.Increment(ref _idCounter):D4}";
            var msg = new ChatMessage(msgId, chatId, senderId, content, sentAt ?? DateTime.UtcNow, type, mediaUrl);
            if (!_chats.ContainsKey(chatId)) _chats[chatId] = [];
            _chats[chatId].Add(msg);
            return msg;
        }

        // Returns up to `count` messages from the chat, in chronological (oldest-first)
        // order for display. The `before` cursor enables pagination: pass the SentAt of
        // the oldest message currently on screen to load the next page upward.
        //
        // Internally fetches newest-first then reverses — this mirrors the Cassandra
        // clustering key order (sentAt DESC) where the most recent rows are read first
        // with minimal disk seeks, then the result set is flipped for the UI.
        public List<ChatMessage> GetHistory(string chatId, int count = 50, DateTime? before = null)
        {
            if (!_chats.TryGetValue(chatId, out var msgs)) return [];
            IEnumerable<ChatMessage> query = msgs.OrderByDescending(m => m.SentAt);
            if (before.HasValue) query = query.Where(m => m.SentAt < before.Value);
            return query.Take(count).OrderBy(m => m.SentAt).ToList();
        }

        // Looks up a single message by its ID across all chats.
        // Used for delivery-receipt updates: when a recipient ACKs a message,
        // ChatServer calls GetById to fetch the object and advance its Status.
        // O(total messages) in this demo — in production, MessageId is the Cassandra
        // row key within a partition, making this an O(1) lookup given the chatId.
        public ChatMessage GetById(string messageId)
        {
            foreach (var msgs in _chats.Values)
            {
                var msg = msgs.FirstOrDefault(m => m.MessageId == messageId);
                if (msg != null) return msg;
            }
            return null;
        }

        // Returns all messages addressed to userId that are still undelivered (Status=Sent).
        // Called when a user reconnects after being offline — their device fetches the
        // missed messages, ACKs each one, and ChatServer advances Status to Delivered.
        // SenderId != userId excludes the user's own outbound messages from the result.
        //
        // The chatId.Contains(userId) heuristic works only because demo chatIds are
        // formatted as "chat:alice:bob". In production, replace with an inbox index
        // lookup (see header) — scanning every message in the store does not scale.
        public List<ChatMessage> GetUndelivered(string userId)
        {
            return _chats.Values.SelectMany(msgs => msgs)
                .Where(m => m.ChatId.Contains(userId)
                         && m.Status == DeliveryStatus.Sent
                         && m.SenderId != userId)
                .OrderBy(m => m.SentAt)
                .ToList();
        }
    }
}
