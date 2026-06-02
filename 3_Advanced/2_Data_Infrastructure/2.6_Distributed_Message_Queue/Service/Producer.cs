// Producer — writes messages to a topic, choosing the target partition.
//
// THE BIG IDEA:
// Think of the Producer as a sorting clerk at the post office. Every parcel
// that arrives either has a destination label (key) or it doesn't:
//
//   Key present  → look up the fixed mailbox slot for that label (hash routing).
//                  All parcels for "user:Alice" always land in slot 2 — forever.
//                  This guarantees that Alice's events are processed in order.
//
//   Key absent   → drop the parcel in whichever slot is next in rotation
//                  (round-robin). Load spreads evenly; no ordering guarantee.
//
// After choosing the slot, the clerk drops the parcel in and gets back a
// receipt (ProduceResult) stamped with the exact position — which the caller
// can store and use to seek directly to that message later.
//
// WHY HASH ROUTING GUARANTEES ORDERING:
// hash(key) % N always produces the same partition index for the same key
// (Topic uses MD5, which is stable across machines — see Topic.cs).
// One consumer owns each partition. Therefore, all writes for "user:Alice"
// land in partition 2, and the single consumer reading partition 2 sees them
// in the exact order they were appended — no race conditions, no interleaving.
//
// WHY ROUND-ROBIN FOR NULL KEYS (not always partition 0):
// Sending every keyless message to partition 0 would make one consumer do all
// the work while the others sit idle. Round-robin spreads writes evenly so
// every consumer gets roughly the same share of messages.
//
// WHY Interlocked.Increment (not lock or ++ ):
// _roundRobinIndex is a plain int. Two threads calling Produce simultaneously
// with null keys could both read the same value and increment separately,
// sending two messages to the same partition and skipping the next one.
// Interlocked.Increment is a single atomic CPU instruction — no lock needed,
// no torn read, and far cheaper than acquiring a Monitor on every null-key write.

using System.Threading;

namespace AdvancedDesigns
{
    public class Producer
    {
        private readonly Broker _broker;

        // Counter for null-key (keyless) messages. Wraps naturally on int overflow
        // — the modulo below always produces a valid partition index regardless.
        private int _roundRobinIndex;

        public Producer(Broker broker) => _broker = broker;

        public ProduceResult Produce(string topicName, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);

            // Two routing strategies depending on whether a key was provided.
            // The ternary keeps both paths visible side-by-side for easy comparison.
            int partition = key != null
                ? topic.GetPartitionIndex(key)                                    // stable hash → ordering
                : Interlocked.Increment(ref _roundRobinIndex) % topic.PartitionCount; // atomic rotate → even load

            var msg = new Message(key, value);
            long offset = topic.GetPartition(partition).Append(msg);
            // Return the receipt so the caller can store the exact address
            // (topic, partition, offset) for auditing or later direct seeks.
            return new ProduceResult(topicName, partition, offset);
        }

        // Bypasses the routing logic and writes directly to a named partition.
        // Useful in tests to control exactly which partition a message lands on,
        // and in advanced scenarios like partition-aware producers that maintain
        // their own routing tables outside of hash-based assignment.
        public ProduceResult ProduceToPartition(string topicName, int partition, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);
            var msg = new Message(key, value);
            long offset = topic.GetPartition(partition).Append(msg);
            return new ProduceResult(topicName, partition, offset);
        }
    }
}
