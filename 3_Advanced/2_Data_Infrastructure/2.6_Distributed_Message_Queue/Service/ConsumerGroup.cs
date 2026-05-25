// ConsumerGroup — assigns partitions across consumers and coordinates polling.
//
// Why one partition → at most one consumer: two consumers in the same group
// reading the same partition would process messages out of order and duplicate
// work. The group contract guarantees each partition is owned by exactly one
// consumer at a time.
//
// Why rebalance triggers: any join or leave changes the consumer count, so the
// partition→consumer mapping must be recomputed. In real Kafka the group
// coordinator (a broker) drives this; here Rebalance() is called directly.
//
// Idle consumers (more consumers than partitions): they exist in the group but
// receive no partitions — they become standby for failover if a peer crashes.

namespace AdvancedDesigns
{
    public class ConsumerGroup
    {
        public string GroupId { get; }

        private readonly Broker             _broker;
        private readonly List<Consumer>     _consumers           = new();
        private readonly Dictionary<int, Consumer> _partitionAssignment = new();
        private string _subscribedTopic;

        public ConsumerGroup(string groupId, Broker broker)
        {
            GroupId = groupId;
            _broker = broker;
        }

        public Consumer AddConsumer(string consumerId)
        {
            var consumer = new Consumer(consumerId, _broker);
            _consumers.Add(consumer);
            if (_subscribedTopic != null) Rebalance();
            return consumer;
        }

        public void RemoveConsumer(string consumerId)
        {
            _consumers.RemoveAll(c => c.ConsumerId == consumerId);
            if (_subscribedTopic != null) Rebalance();
        }

        public void Subscribe(string topicName)
        {
            _subscribedTopic = topicName;
            Rebalance();
        }

        // Round-robin assignment: partition i → consumer[i % consumerCount].
        // Simple and fair; Kafka's default assignor (RangeAssignor) is slightly
        // different but produces the same balance for equal partition counts.
        public void Rebalance()
        {
            if (_subscribedTopic == null || _consumers.Count == 0) return;
            Topic topic = _broker.GetTopic(_subscribedTopic);
            _partitionAssignment.Clear();
            for (int p = 0; p < topic.PartitionCount; p++)
                _partitionAssignment[p] = _consumers[p % _consumers.Count];
        }

        public Dictionary<string, List<int>> GetAssignment()
        {
            var result = new Dictionary<string, List<int>>();
            foreach (var kv in _partitionAssignment)
            {
                string id = kv.Value.ConsumerId;
                if (!result.ContainsKey(id)) result[id] = new List<int>();
                result[id].Add(kv.Key);
            }
            return result;
        }

        public List<(Consumer consumer, Message msg)> PollAll(int maxPerPartition = 5)
        {
            var results = new List<(Consumer, Message)>();
            if (_subscribedTopic == null) return results;

            foreach (var kv in _partitionAssignment)
            {
                var msgs = kv.Value.Poll(_subscribedTopic, kv.Key, maxPerPartition);
                foreach (var msg in msgs)
                    results.Add((kv.Value, msg));
            }
            return results;
        }

        public (long totalLag, Dictionary<int, long> perPartitionLag) GetLag()
        {
            if (_subscribedTopic == null) return (0, new Dictionary<int, long>());
            var perPartition = new Dictionary<int, long>();
            long total = 0;
            foreach (var kv in _partitionAssignment)
            {
                long lag = kv.Value.GetLag(_subscribedTopic, kv.Key);
                perPartition[kv.Key] = lag;
                total += lag;
            }
            return (total, perPartition);
        }
    }
}
