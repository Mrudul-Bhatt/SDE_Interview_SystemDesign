// Broker — central registry that owns all Topics and routes data-plane calls to them.
//
// THE BIG IDEA:
// Think of the Broker as a post office building. The building itself doesn't read
// or write letters — it just holds the mailboxes (Topics) and makes sure every
// sender can find the right box by name. Producers and Consumers walk up to the
// Broker, ask "give me the 'orders' mailbox", then talk directly to that mailbox.
//
// WHY ADMIN-PLANE VS DATA-PLANE SEPARATION:
// CreateTopic is an admin operation: it sets up the schema (partition count,
// compaction flag) that all producers and consumers must agree on. Produce and
// Consume are data operations: they just push or pull bytes. Keeping these
// concerns separate means you can change a topic's configuration (e.g. add
// retention policy) without touching any producer or consumer code.
//
// WHY A SINGLE BROKER HERE (vs real Kafka's cluster):
// In production, a Kafka cluster runs many broker nodes. Each broker hosts a
// subset of partition leaders (the primary copy that accepts writes) and
// followers (replicas for fault tolerance). Producers write to the leader;
// followers replicate asynchronously. If the leader dies, a follower is elected.
// Here, one Broker holds every topic to keep the demo single-process and
// observable without networking or replication complexity.
//
// WHY THROW ON MISSING TOPIC (not return null):
// A producer or consumer calling GetTopic with a wrong name is almost certainly
// a programming error — a typo or a missing CreateTopic call. Returning null
// would push the failure into Producer/Consumer code as a NullReferenceException
// with a confusing stack trace. An explicit throw here gives a clear message
// ("Topic 'orders' does not exist") right at the call site.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class Broker
    {
        // All Topics keyed by name. The Broker is the single source of truth for
        // which topics exist — producers and consumers must go through it to get
        // a Topic reference rather than constructing one themselves.
        private readonly Dictionary<string, Topic> _topics = [];

        // Admin-plane: creates the mailbox. Must be called before any producer
        // or consumer tries to use this topic name. Calling it twice with the
        // same name silently replaces the topic — intentional for demo resets,
        // but in production this would be guarded by a "topic already exists" error.
        public void CreateTopic(string name, int partitions, bool compacted = false)
            => _topics[name] = new Topic(name, partitions, compacted);

        // Data-plane entry point: returns the Topic so callers can reach its
        // PartitionLogs directly. Throws rather than returning null so a missing
        // CreateTopic call surfaces immediately with a clear error message.
        public Topic GetTopic(string name)
        {
            if (!_topics.TryGetValue(name, out Topic topic))
                throw new InvalidOperationException($"Topic '{name}' does not exist");
            return topic;
        }

        // Guard used by Producer to avoid a redundant CreateTopic if the topic
        // was already set up by another service at startup.
        public bool TopicExists(string name) => _topics.ContainsKey(name);
    }
}
