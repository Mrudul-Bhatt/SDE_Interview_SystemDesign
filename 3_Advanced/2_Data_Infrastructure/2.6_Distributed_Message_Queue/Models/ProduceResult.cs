// ProduceResult — the receipt handed back to the producer after a successful write.
//
// THE BIG IDEA:
// When you drop a parcel at the post office, they hand you a tracking number.
// ProduceResult is that tracking number. The three fields together form a
// globally unique address for the message — no two messages in the entire
// cluster will ever share the same (Topic, Partition, Offset) triple.
//
// WHY THIS MATTERS — AT-LEAST-ONCE DELIVERY:
// Networks drop packets. A producer sends a write, the broker appends the message
// and goes to reply — then the network dies. The producer never receives a result.
// What should it do?
//
//   Option A — assume the write succeeded → dangerous: if it didn't, message is lost
//   Option B — assume the write failed → safe: retry. Worst case: two copies land.
//
// Option B ("at-least-once delivery") is the right default. Retry until you get
// a ProduceResult back. The consumer handles the rare duplicate by being idempotent
// (e.g. "process order:101" twice is harmless if the second attempt detects the
// order already exists).
//
// In production Kafka, the producer can also enable EXACTLY-ONCE semantics by
// attaching a producer ID + sequence number. The broker deduplicates retries and
// guarantees each message lands exactly once — but this costs extra latency and
// is only needed when duplicates are truly unacceptable (e.g. financial transfers).
//
// HOW PRODUCERS USE THE RECEIPT IN PRACTICE:
//   var result = producer.Produce("orders", "user:Alice", "order:101 created");
//   // result.Offset is now the permanent address of this message.
//   // Store result.Offset in your database alongside the order record.
//   // Later, if you need to audit "when exactly was order:101 written?",
//   // you can seek directly to (topic="orders", partition=result.Partition,
//   // offset=result.Offset) and read that exact message — no scanning needed.

namespace AdvancedDesigns
{
    public class ProduceResult
    {
        // The topic the message was written to (e.g. "orders", "payments").
        // Together with Partition and Offset, this forms the full address.
        public string Topic { get; }

        // Which partition lane the message landed on. Determined by hash(key) % N
        // for keyed messages, or round-robin for keyless ones. Stored here so the
        // producer knows exactly which partition to seek to during audit or replay.
        public int Partition { get; }

        // The message's permanent position within the partition. Assigned by the
        // PartitionLog under a lock — guaranteed unique and ever-increasing.
        // Offset 0 = first message ever written to this partition.
        // Consumers read "from offset N" to skip everything written before N.
        public long Offset { get; }

        public ProduceResult(string topic, int partition, long offset)
        {
            Topic = topic;
            Partition = partition;
            Offset = offset;
        }
    }
}
