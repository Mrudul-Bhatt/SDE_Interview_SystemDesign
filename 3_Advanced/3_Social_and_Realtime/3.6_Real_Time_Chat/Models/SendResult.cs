// SendResult — delivery receipt returned after ChatServer.Send().
// Lets callers distinguish between online delivery (via WebSocket/pub-sub)
// and offline delivery (via push notification), which have different latency
// and reliability guarantees.

namespace AdvancedDesigns
{
    public class SendResult
    {
        public string MessageId       { get; }
        public int    OnlineDelivered { get; } // recipients reached via WebSocket
        public int    PushSent        { get; } // offline recipients notified via APNs/FCM

        public SendResult(string msgId, int online, int push)
        {
            MessageId       = msgId;
            OnlineDelivered = online;
            PushSent        = push;
        }
    }
}
