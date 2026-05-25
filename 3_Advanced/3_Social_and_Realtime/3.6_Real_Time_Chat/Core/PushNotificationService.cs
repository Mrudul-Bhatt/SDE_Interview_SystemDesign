// PushNotificationService — delivers notifications to offline users (simulates APNs/FCM).
//
// Push is the fallback path: if a user has no active WebSocket connection,
// the message is already persisted in the MessageStore (durability is guaranteed),
// but the user won't see it until they open the app. A push notification wakes
// the app so delivery feels real-time even when the user is not active.
//
// The log here is for demo inspection; real systems fire-and-forget to a
// third-party push gateway (Apple APNs, Google FCM) and don't store the log locally.

namespace AdvancedDesigns
{
    public class PushNotificationService
    {
        private readonly List<(string UserId, string Message, DateTime SentAt)> _log = new();

        public void Send(string userId, string notification)
            => _log.Add((userId, notification, DateTime.UtcNow));

        public IReadOnlyList<(string UserId, string Message, DateTime SentAt)> GetLog() => _log;
    }
}
