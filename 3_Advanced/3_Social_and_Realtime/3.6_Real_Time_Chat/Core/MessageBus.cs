// MessageBus — in-process pub/sub (simulates Redis Pub/Sub for cross-server routing).
//
// Why pub/sub for cross-server delivery: each chat server subscribes to channels
// named "user:{userId}" for every connected user. When Server1 needs to deliver
// to a user on Server2, it publishes to that channel — Redis broadcasts it to
// whichever server is subscribed, without Server1 needing to know Server2's address.
//
// Publish returns false when no subscriber is active — callers use this to detect
// that the recipient is offline and fall back to push notification.

namespace AdvancedDesigns
{
    public class MessageBus
    {
        private readonly Dictionary<string, List<Action<ChatMessage>>> _subscribers = new();

        public void Subscribe(string channel, Action<ChatMessage> handler)
        {
            if (!_subscribers.ContainsKey(channel))
                _subscribers[channel] = new List<Action<ChatMessage>>();
            _subscribers[channel].Add(handler);
        }

        // Returns true if at least one subscriber received the message.
        public bool Publish(string channel, ChatMessage message)
        {
            if (!_subscribers.TryGetValue(channel, out var handlers) || handlers.Count == 0)
                return false;
            foreach (var h in handlers) h(message);
            return true;
        }
    }
}
