// Consumer — reads from a partition starting at its committed offset.
//
// THE BIG IDEA:
// Think of the Consumer as a person reading a very long newspaper that keeps
// getting new pages added to the end. They have a bookmark (committed offset)
// that marks where they stopped last time. Each Poll() picks up from the
// bookmark and reads the next batch of pages. After processing each batch,
// they advance the bookmark (Commit) so a crash never causes them to miss
// a page — or read the same page twice.
//
// WHY PER-(TOPIC, PARTITION) OFFSET TRACKING:
// A single consumer may be assigned to multiple partitions across multiple
// topics (especially inside a ConsumerGroup). Each partition is an independent
// tape running at its own speed — partition 0 of "orders" might be at offset
// 500 while partition 1 is at offset 12. Collapsing all of these into one
// shared offset counter would make no sense: "offset 500" means something
// completely different in each partition. The composite key (topic, partition)
// gives each tape its own independent bookmark.
//
// WHY COMMIT STORES offset+1 (not the offset that was just read):
// The committed offset means "the next message I want to read." If a consumer
// processes offset 42 and commits 42, on restart it would re-fetch and
// re-process offset 42 — a duplicate. Committing 43 means "I'm done with 42;
// give me 43 next time." This is the standard Kafka committed-offset contract.
// The trade-off: if the consumer crashes after processing but before committing,
// it will re-process the last batch. That's "at-least-once" delivery — the
// safe default. Consumers must be idempotent to handle the rare duplicate.
//
// WHY LAG MATTERS MORE THAN ABSOLUTE OFFSET:
// An offset of 10,000 tells you nothing by itself. Lag = latestOffset -
// committedOffset tells you how far behind the consumer is. Lag of 0 means
// fully caught up. A lag that grows over time means the consumer is too slow
// and will fall further behind indefinitely — alert on that, not on offset size.

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class Consumer
    {
        // Unique identifier for this consumer — used by ConsumerGroup to assign
        // partitions and track which consumer is responsible for each one.
        public string ConsumerId { get; }

        private readonly Broker _broker;

        // The bookmark dictionary: one committed offset per (topic, partition) pair.
        // Missing entry means this consumer has never read from that partition —
        // GetCommittedOffset returns 0, so the first Poll starts from the beginning.
        private readonly Dictionary<(string topic, int partition), long> _committedOffsets = [];

        public Consumer(string consumerId, Broker broker)
        {
            ConsumerId = consumerId;
            _broker = broker;
        }

        // Fetches the next batch of messages from where the bookmark left off.
        // Returns an empty list if the consumer is fully caught up (no new messages).
        // maxMessages caps memory: a consumer thousands of offsets behind won't
        // pull the entire backlog into memory in one call.
        public List<Message> Poll(string topicName, int partition, int maxMessages = 10)
        {
            long fromOffset = GetCommittedOffset(topicName, partition);
            return _broker.GetTopic(topicName).GetPartition(partition).ReadFrom(fromOffset, maxMessages);
        }

        // Advances the bookmark to offset+1 (the NEXT message to read).
        // Call this only after the message has been fully processed and any
        // side effects are durable (e.g. written to a database). Committing
        // before processing risks losing a message if the consumer crashes mid-work.
        public void Commit(string topicName, int partition, long offset)
            => _committedOffsets[(topicName, partition)] = offset + 1;

        // Convenience overload: commits the highest offset seen in each partition
        // within a batch returned by Poll. Groups by partition because a single
        // Poll call can return messages from multiple partitions (e.g. in tests),
        // and each partition needs its own bookmark advanced independently.
        public void CommitAll(string topicName, IEnumerable<Message> messages)
        {
            foreach (var group in messages.GroupBy(m => m.Partition))
                Commit(topicName, group.Key, group.Max(m => m.Offset));
        }

        // Returns the offset that the next Poll will start from.
        // Defaults to 0 (start of log) if this consumer has never committed
        // for this (topic, partition) — TryGetValue sets offset to its default
        // value (0L) when the key is missing, which is exactly what we want.
        public long GetCommittedOffset(string topicName, int partition)
        {
            _committedOffsets.TryGetValue((topicName, partition), out long offset);
            return offset;
        }

        // Lag = how many messages are waiting to be read.
        // latestOffset is the position the next Append will use (i.e. total messages written).
        // committedOffset is where this consumer will read next.
        // Their difference is the number of unread messages — zero means fully caught up.
        public long GetLag(string topicName, int partition)
        {
            long latest = _broker.GetTopic(topicName).GetPartition(partition).LatestOffset;
            long committed = GetCommittedOffset(topicName, partition);
            return latest - committed;
        }
    }
}
