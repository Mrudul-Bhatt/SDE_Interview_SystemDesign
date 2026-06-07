// SendResult — the delivery receipt handed back after ChatServer.Send().
//
// THE BIG IDEA:
// When a message is sent, each recipient gets it through one of two very different
// channels depending on whether they are online right now. SendResult tells the
// caller how many recipients were reached by each path:
//
//   Recipient is ONLINE (app open, WebSocket connected):
//     Message pushed directly over the live socket — arrives in milliseconds.
//     OnlineDelivered is incremented.
//     The message's DeliveryStatus can immediately advance to Delivered because
//     the server received an ACK from the device.
//
//   Recipient is OFFLINE (app in background or closed):
//     A push notification is dispatched to APNs (Apple) or FCM (Google).
//     PushSent is incremented.
//     The message's DeliveryStatus stays at Sent — the OS has queued the notification
//     but we have no ACK that the device received or processed it.
//
// WHY TWO SEPARATE COUNTS INSTEAD OF ONE TOTAL:
// Online delivery and push delivery have fundamentally different reliability contracts.
// A WebSocket delivery is confirmed — the server knows the bytes arrived. A push
// notification is fire-and-forget: APNs/FCM accepts it and tries their best, but
// the device could be off, the user could have disabled notifications, or the OS
// could silently drop it. Collapsing both into one number would hide this distinction
// and make it impossible to reason about true delivery vs "maybe delivered."
// Keeping them separate lets monitoring dashboards catch, for example, a spike in
// PushSent with zero OnlineDelivered — a sign that a chat partner went offline.
//
// WHY THE MESSAGE IS ALREADY SAFE WHEN THIS IS RETURNED:
// SendResult is informational only. By the time ChatServer.Send() returns this
// object, the message has already been persisted to the chat store. Even if
// OnlineDelivered = 0 and PushSent = 0 (rare edge case: all recipients somehow
// unreachable), the message is not lost — the recipient will pull it on next
// reconnect. The counts describe what happened during this send attempt, not
// whether the message will ultimately be received.
//
// HOW CALLERS USE THIS:
// Tests assert that OnlineDelivered and PushSent match the expected online/offline
// setup of recipients. Monitoring tracks the ratio over time — a healthy group chat
// should see mostly OnlineDelivered during active hours. The demo prints both counts
// to show which delivery path each recipient took.

namespace AdvancedDesigns
{
    public class SendResult
    {
        // ── RUNTIME SNAPSHOT — what one instance holds ──
        //
        //   Scenario 3 (group "family": alice sends; bob+carol online, dave offline):
        //       MessageId       = "msg:0001"
        //       OnlineDelivered = 2     ← bob + carol reached over live WebSockets
        //       PushSent        = 1     ← dave was offline → one APNs/FCM push
        //   (alice, the sender, is excluded entirely — she never appears in either count.)
        //
        //   Scenario 1 (1:1, Bob online):  OnlineDelivered = 1, PushSent = 0
        //   Scenario 2 (1:1, Bob offline): OnlineDelivered = 0, PushSent = 1
        //
        // Which message this receipt is for. Used to correlate the result with the
        // original ChatMessage in logs, tests, and retry logic.
        public string MessageId { get; }

        // Number of recipients reached via a live WebSocket connection right now.
        // Each count here means the server got a delivery ACK — the device has the
        // message. This is what allows DeliveryStatus to advance to Delivered.
        public int OnlineDelivered { get; }

        // Number of offline recipients who were sent a push notification (APNs/FCM).
        // This is a best-effort count — it means the push was dispatched, not that
        // the device received it. DeliveryStatus stays at Sent until the recipient's
        // device comes online and sends an explicit ACK through the WebSocket.
        public int PushSent { get; }

        public SendResult(string msgId, int online, int push)
        {
            MessageId = msgId;
            OnlineDelivered = online;
            PushSent = push;
        }
    }
}
