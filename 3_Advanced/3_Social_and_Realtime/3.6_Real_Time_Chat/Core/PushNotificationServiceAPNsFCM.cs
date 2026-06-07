// PushNotificationServiceAPNsFCM — delivers notifications to offline users (simulates APNs/FCM).
//
// THE BIG IDEA:
// Think of this like a postal service for users who aren't home. When Alice sends Bob
// a message and Bob's WebSocket is closed (he's offline), the message is already safely
// stored in MessageStore — it won't be lost. But Bob has no way to know it arrived.
// A push notification knocks on Bob's phone; the OS wakes the app, which reconnects
// its WebSocket and pulls the waiting messages. The result: delivery feels instant
// even though Bob was offline when Alice hit send.
//
// In production this class is a thin wrapper around a third-party push gateway:
//
//   iOS  → Apple APNs  (Apple Push Notification service)
//   Android → Google FCM  (Firebase Cloud Messaging)
//
// The app registers a device token with the chat server on first launch. ChatServer
// stores token → userId in a device registry. When Send() is called, the real
// implementation looks up Bob's device token and fires an HTTPS request to APNs/FCM.
// APNs/FCM queues the notification and delivers it when Bob's device is reachable.
//
// WHY PUSH IS FIRE-AND-FORGET (no ACK from the gateway):
// APNs/FCM accept the notification and return a 200 — that confirms the gateway
// received it, NOT that the device did. The device could be off, in airplane mode,
// or the user may have disabled notifications for the app. There is no reliable
// callback when the notification is actually displayed. This is why push supplements
// but never replaces the WebSocket + MessageStore path: the message is durable in
// storage regardless of whether the push arrives. When Bob reconnects, he fetches
// missed messages directly from MessageStore — the push is just the wake-up call.
//
// WHY THE LOG EXISTS (demo only):
// In production, ChatServer fires Send() and moves on — there is no local record.
// APNs/FCM provide their own delivery dashboards and error callbacks for failed
// tokens (uninstalled apps, expired tokens). The _log here lets demo output and
// tests inspect exactly which push notifications were dispatched and when, without
// needing a real gateway.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class PushNotificationServiceAPNsFCM
    {
        // Demo-only record of every dispatched notification. In production, ChatServer
        // fires Send() and trusts the gateway — nothing is stored locally. The log
        // exists solely so tests can assert "Bob received a push for message X."
        //
        // ── RUNTIME SNAPSHOT (Scenario 2: Bob offline, Alice sends 2 messages) ──
        //   _log = [
        //       ("bob", "New message from alice: \"Where are you?\"",        15:42:01),
        //       ("bob", "New message from alice: \"Dinner is getting cold!\"", 15:42:02)
        //   ]
        //   Each tuple = one fire-and-forget push to APNs/FCM. Note the message
        //   PREVIEW is truncated to 20 chars by ChatServer before it lands here.
        //   The list only grows — it's an append-only audit trail, never read back
        //   for routing (the real messages live durably in MessageStore).
        private readonly List<(string UserId, string Message, DateTime SentAt)> _log = [];

        // Dispatches a push notification to the given user's device(s).
        // In production: look up the user's APNs/FCM device token, POST to the gateway,
        // and return immediately — do not wait for device delivery confirmation (see header).
        // Here: appends to _log so demo output shows the fallback path was taken.
        public void Send(string userId, string notification) => _log.Add((userId, notification, DateTime.UtcNow));

        // Returns the full push log for test assertions and demo output.
        // Read-only so callers can inspect without accidentally mutating the record.
        public IReadOnlyList<(string UserId, string Message, DateTime SentAt)> GetLog() => _log;
    }
}
