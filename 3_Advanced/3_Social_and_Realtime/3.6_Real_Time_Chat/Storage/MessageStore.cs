// MessageStore — durable message persistence (simulates Cassandra).
//
// Cassandra fit: chat history is always queried as "messages in chat X before time T,
// newest first." Cassandra's partition key = chatId, clustering key = sentAt DESC
// makes this a single-partition range scan — O(1) partition lookup + O(count) read.
//
// Why Interlocked.Increment for message IDs: Save() may be called from multiple
// threads (one per connected user). Interlocked prevents two messages getting
// the same ID without the overhead of a lock around the whole method.
//
// GetUndelivered uses a simple string-contains check on chatId to find the user's
// chats. Real systems maintain a separate inbox index (userId → chatIds) to avoid
// scanning all chats.

namespace AdvancedDesigns
{
    public class MessageStore
    {
        private readonly Dictionary<string, List<ChatMessage>> _chats = new();
        private long _idCounter;

        public ChatMessage Save(string chatId, string senderId, string content,
            DateTime? sentAt = null, MessageType type = MessageType.Text, string mediaUrl = null)
        {
            string msgId = $"msg:{Interlocked.Increment(ref _idCounter):D4}";
            var msg = new ChatMessage(msgId, chatId, senderId, content,
                                      sentAt ?? DateTime.UtcNow, type, mediaUrl);
            if (!_chats.ContainsKey(chatId)) _chats[chatId] = new List<ChatMessage>();
            _chats[chatId].Add(msg);
            return msg;
        }

        // Returns messages in oldest-first order for display, fetched newest-first for efficiency.
        public List<ChatMessage> GetHistory(string chatId, int count = 50, DateTime? before = null)
        {
            if (!_chats.TryGetValue(chatId, out var msgs)) return new List<ChatMessage>();
            IEnumerable<ChatMessage> query = msgs.OrderByDescending(m => m.SentAt);
            if (before.HasValue) query = query.Where(m => m.SentAt < before.Value);
            return query.Take(count).OrderBy(m => m.SentAt).ToList();
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

        // Finds messages sent to the user while they were offline (status=Sent, not yet Delivered).
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
