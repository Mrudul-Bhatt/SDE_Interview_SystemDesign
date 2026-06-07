// ChatMessage — the atomic unit of a conversation.
//
// THE BIG IDEA:
// Think of a ChatMessage like a physical letter in the postal system. Once it's
// written and sealed (created), the letter's content never changes — but the
// envelope gets stamped as it moves through the system: "handed to post office"
// (Sent), "in recipient's mailbox" (Delivered), "recipient opened it" (Read).
//
// Every message belongs to exactly one Chat (a 1-on-1 or group thread) and is
// stored partitioned by ChatId. When Bob opens a conversation with Alice, the
// system does one fast lookup — "give me all messages where ChatId = X" — and
// gets them back sorted by SequenceNumber. No scatter-gather across shards.
//
// WHY SEQUENCENUMBER ALONGSIDE TIMESTAMP:
// Two phones can send messages at the exact same millisecond (especially rapid-
// fire typing). Wall-clock time is also not reliable across distributed servers —
// clocks drift. SequenceNumber is assigned by the chat server in a single atomic
// counter per Chat, so it's the authoritative ordering: no ties, no ambiguity.
// Timestamps are still kept because they're what users actually see ("3:42 PM"),
// but ordering is always done on SequenceNumber.
//
// WHY DELIVERYSTATUS IS ONE-WAY (Sent → Delivered → Read):
// Read receipts can only move forward — you can't "unread" a message once you've
// seen it. The one-way constraint lets any server safely apply status updates out
// of order: receiving a Read receipt when Status is already Read is a no-op, and
// you never risk rolling back from Read to Delivered due to a late-arriving packet.
// A later Read receipt also implicitly covers Delivered — no separate step needed.
//
// WHY MEDIAURL IS SEPARATE FROM CONTENT:
// Media is stored once in a blob store (S3 / CDN) and only the URL travels with
// the message. This keeps chat message rows tiny (a few hundred bytes), which
// matters when you're storing billions of them. It also means the same image can
// be forwarded to 100 chats by copying just the URL — not the bytes.

using System;

namespace AdvancedDesigns
{
    // One-way state machine — transitions are Sent → Delivered → Read, never backwards.
    // Enforced by convention: callers only ever advance status, never retreat it.
    public enum DeliveryStatus { Sent, Delivered, Read }

    // Determines whether Content carries the payload or MediaUrl does.
    // A Text message always has Content; Image/Video/File always have a MediaUrl.
    public enum MessageType { Text, Image, Video, File }

    public class ChatMessage
    {
        // ── RUNTIME SNAPSHOT — what one populated instance holds ──
        //
        //   A text message (Scenario 1), after pub/sub delivery advanced its status:
        //       MessageId      = "msg:0001"
        //       ChatId         = "chat:alice:bob"
        //       SenderId       = "alice"
        //       Content        = "Hey Bob, what's up?"
        //       MediaUrl       = null                 ← Text type → no media
        //       Type           = MessageType.Text
        //       SentAt         = 2026-06-07 15:42:00
        //       Status         = DeliveryStatus.Delivered   (Sent → Delivered → Read)
        //       SequenceNumber = 0                    ← stays 0 in this demo; server-assigned in prod
        //       ToString() → [msg:0001] alice→chat:alice:bob: "Hey Bob, what's up?" ✓✓
        //
        //   An image message would instead carry:
        //       Content = ""   Type = MessageType.Image   MediaUrl = "https://cdn/img/42.jpg"
        //       ToString() → [...] alice→...: "" [media:https://cdn/img/42.jpg] ✓
        //
        // Globally unique message identifier. In production this is a Snowflake ID
        // (timestamp + machine + counter) — unique, time-ordered, and generated
        // client-side without any central coordination. Also used for deduplication:
        // if the same message arrives twice (retry after a dropped ACK), the server
        // discards the duplicate by recognizing the already-stored MessageId.
        public string MessageId { get; }

        // Which conversation this message belongs to. All messages in a chat share
        // this key, so "load the last 50 messages for chat X" is a single partition
        // scan in the storage layer — no expensive cross-partition queries.
        public string ChatId { get; }

        // Who wrote the message. Used to attribute the bubble on the receiver's screen,
        // and to skip fan-out back to the sender (they already have the message locally).
        public string SenderId { get; }

        // The visible text body. Empty string (not null) for pure-media messages so
        // callers never need a null check just to display a caption area.
        public string Content { get; }

        // CDN or blob-store URL pointing to the media file. Null for Text messages.
        // Storing a URL (not raw bytes) keeps message rows small and allows the same
        // media to be referenced by multiple chats without duplicating the upload.
        public string MediaUrl { get; }

        // Immutable — the type of a message is set at creation and never changes.
        // (You can't turn a sent photo into a text message after the fact.)
        public MessageType Type { get; }

        // Client-reported send time — what the user sees as "sent at 3:42 PM".
        // Not used for ordering (SequenceNumber handles that) because client clocks
        // can be skewed or manually set. Stored as-is for display purposes only.
        public DateTime SentAt { get; }

        // Updated by the server as delivery events arrive from the recipient's device.
        // Starts at Sent (client-side); the chat server moves it to Delivered once the
        // recipient's device ACKs receipt, and to Read once they open the conversation.
        public DeliveryStatus Status { get; set; }

        // Server-assigned, monotonically increasing counter scoped to this ChatId.
        // This is the canonical sort key — SequenceNumber 42 always comes before 43,
        // regardless of what the SentAt timestamps say. Set to 0 until the server
        // confirms the message and assigns a real position in the conversation.
        public int SequenceNumber { get; set; }

        public ChatMessage(
            string messageId, string chatId, string senderId,
            string content, DateTime sentAt, MessageType type = MessageType.Text,
            string mediaUrl = null   // omitted for Text; required for Image/Video/File
        )
        {
            MessageId = messageId;
            ChatId = chatId;
            SenderId = senderId;
            Content = content;
            SentAt = sentAt;
            Type = type;
            MediaUrl = mediaUrl;
            // All messages begin as Sent — the server hasn't confirmed delivery yet.
            // Status will be advanced to Delivered / Read by incoming receipt events.
            Status = DeliveryStatus.Sent;
        }

        // Short human-readable form for demo output.
        // Checkmark style mirrors WhatsApp: ✓ = sent, ✓✓ = delivered, ✓✓(read) = read,
        // so you can see at a glance which stage each message is at in the demo output.
        public override string ToString()
        {
            string statusIcon = Status == DeliveryStatus.Read ? "✓✓(read)"
                              : Status == DeliveryStatus.Delivered ? "✓✓"
                              : "✓";
            string media = MediaUrl != null ? $" [media:{MediaUrl}]" : "";
            return $"[{MessageId}] {SenderId}→{ChatId}: \"{Content}\"{media} {statusIcon}";
        }
    }
}
