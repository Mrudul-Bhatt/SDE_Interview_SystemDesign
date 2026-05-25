// Consumer — reads from a partition starting at its committed offset.
//
// Why per-(topic, partition) offset tracking: a consumer may read from multiple
// partitions of multiple topics independently. Collapsing to a single offset
// would break if the consumer subscribes to more than one topic.
//
// Why commit stores offset+1 (the NEXT message to read): storing the last
// consumed offset would re-deliver that message on restart. Kafka's committed
// offset semantics mean "next fetch starts here."
//
// Lag = latestOffset - committedOffset: a growing lag means the consumer
// is falling behind the producer. Alert on lag, not on absolute offset.

namespace AdvancedDesigns
{
    public class Consumer
    {
        public string ConsumerId { get; }

        private readonly Broker _broker;
        private readonly Dictionary<(string topic, int partition), long> _committedOffsets = new();

        public Consumer(string consumerId, Broker broker)
        {
            ConsumerId = consumerId;
            _broker    = broker;
        }

        public List<Message> Poll(string topicName, int partition, int maxMessages = 10)
        {
            long fromOffset = GetCommittedOffset(topicName, partition);
            return _broker.GetTopic(topicName).GetPartition(partition).ReadFrom(fromOffset, maxMessages);
        }

        public void Commit(string topicName, int partition, long offset)
            => _committedOffsets[(topicName, partition)] = offset + 1;

        public void CommitAll(string topicName, IEnumerable<Message> messages)
        {
            foreach (var group in messages.GroupBy(m => m.Partition))
                Commit(topicName, group.Key, group.Max(m => m.Offset));
        }

        public long GetCommittedOffset(string topicName, int partition)
        {
            _committedOffsets.TryGetValue((topicName, partition), out long offset);
            return offset; // defaults to 0 (read from beginning) if never committed
        }

        public long GetLag(string topicName, int partition)
        {
            long latest    = _broker.GetTopic(topicName).GetPartition(partition).LatestOffset;
            long committed = GetCommittedOffset(topicName, partition);
            return latest - committed;
        }
    }
}
