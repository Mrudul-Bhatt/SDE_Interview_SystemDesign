// Producer — writes messages to a topic, choosing the target partition.
//
// Key-present: partition = hash(key) % N → ordering guarantee per key.
// Key-absent:  partition = round-robin   → even load across partitions.
//
// Why Interlocked.Increment for round-robin: the producer may be called from
// multiple threads. Interlocked avoids a lock on every null-key produce while
// still preventing the counter from being read/written simultaneously.

namespace AdvancedDesigns
{
    public class Producer
    {
        private readonly Broker _broker;
        private int _roundRobinIndex;

        public Producer(Broker broker) => _broker = broker;

        public ProduceResult Produce(string topicName, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);
            int partition = key != null
                ? topic.GetPartitionIndex(key)
                : Interlocked.Increment(ref _roundRobinIndex) % topic.PartitionCount;

            var msg    = new Message(key, value);
            long offset = topic.GetPartition(partition).Append(msg);
            return new ProduceResult(topicName, partition, offset);
        }

        // Allows a caller to target a specific partition directly (e.g. for testing).
        public ProduceResult ProduceToPartition(string topicName, int partition, string key, string value)
        {
            Topic topic = _broker.GetTopic(topicName);
            var msg     = new Message(key, value);
            long offset  = topic.GetPartition(partition).Append(msg);
            return new ProduceResult(topicName, partition, offset);
        }
    }
}
