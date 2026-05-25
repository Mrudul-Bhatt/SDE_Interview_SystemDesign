// ChatMessage — the atomic unit of a conversation.
//
// Why SequenceNumber alongside timestamp: clocks on distributed servers can skew.
// A monotonically increasing sequence number per chat guarantees ordering even when
// two messages have the same millisecond timestamp (e.g. rapid-fire sends).
//
// DeliveryStatus progresses one-way: Sent → Delivered → Read.
// It never goes backwards; a later Read receipt implicitly means Delivered too.

namespace AdvancedDesigns
{
    public enum DeliveryStatus { Sent, Delivered, Read }
    public enum MessageType    { Text, Image, Video, File }

    public class ChatMessage
    {
        public string         MessageId      { get; }
        public string         ChatId         { get; }
        public string         SenderId       { get; }
        public string         Content        { get; }
        public string         MediaUrl       { get; }
        public MessageType    Type           { get; }
        public DateTime       SentAt         { get; }
        public DeliveryStatus Status         { get; set; }
        public int            SequenceNumber { get; set; }

        public ChatMessage(string messageId, string chatId, string senderId,
            string content, DateTime sentAt,
            MessageType type = MessageType.Text, string mediaUrl = null)
        {
            MessageId = messageId;
            ChatId    = chatId;
            SenderId  = senderId;
            Content   = content;
            SentAt    = sentAt;
            Type      = type;
            MediaUrl  = mediaUrl;
            Status    = DeliveryStatus.Sent;
        }

        public override string ToString()
        {
            string statusIcon = Status == DeliveryStatus.Read      ? "✓✓(read)"
                              : Status == DeliveryStatus.Delivered ? "✓✓"
                              : "✓";
            string media = MediaUrl != null ? $" [media:{MediaUrl}]" : "";
            return $"[{MessageId}] {SenderId}→{ChatId}: \"{Content}\"{media} {statusIcon}";
        }
    }
}
