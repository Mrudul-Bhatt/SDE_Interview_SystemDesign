// ConsumerGroup — assigns partitions across consumers and coordinates polling.
//
// THE BIG IDEA:
// A ConsumerGroup is a team of workers sharing a conveyor belt. The belt has
// fixed lanes (partitions); each worker owns one or more lanes exclusively
// and processes items from their lanes in order. When a worker joins or leaves
// the team, the lanes are redistributed (rebalance) so no lane is left
// unattended and no two workers share the same lane.
//
// This model gives you horizontal scaling for free: doubling the consumers
// halves the per-consumer workload, up to the partition count ceiling.
// Beyond that, extra consumers sit idle as hot standbys.
//
// WHY ONE PARTITION → AT MOST ONE CONSUMER:
// Ordering is guaranteed within a single partition (messages arrive in the
// order they were appended). If two consumers both read partition 3, they
// would interleave their processing — consumer A handles offset 10, consumer B
// handles offset 11, but A finishes after B — producing out-of-order side
// effects. Exclusive ownership means only one consumer ever reads a partition
// at a time, so the order guarantee holds end-to-end.
//
// WHY REBALANCE TRIGGERS ON EVERY JOIN/LEAVE:
// The partition→consumer map becomes stale the instant the consumer count
// changes. A joining consumer owns nothing until Rebalance redistributes lanes;
// a leaving consumer's lanes would go unread forever without it. In real Kafka,
// the group coordinator (a special broker role) detects membership changes via
// heartbeat timeouts and drives the rebalance protocol across all members.
// Here we call Rebalance() directly for simplicity.
//
// WHY IDLE CONSUMERS (more consumers than partitions) ARE STILL VALUABLE:
// An idle consumer holds no partitions but remains in the group. If a peer
// crashes, the next Rebalance immediately assigns the orphaned partitions to
// the idle consumer — near-instant failover with no manual intervention.
// This is why Kafka recommends having at least as many partitions as your
// peak consumer count, with a few spare consumers as hot standbys.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class ConsumerGroup
    {
        // Logical name for this group — shared across all consumers in the team.
        // The broker (in real Kafka, the group coordinator) uses GroupId to track
        // committed offsets separately from other groups reading the same topic.
        // Two groups with different IDs can both read a topic fully and independently.
        public string GroupId { get; }

        private readonly Broker _broker;
        private readonly List<Consumer> _consumers = [];

        // The current lane assignment: partition index → the Consumer that owns it.
        // Rebuilt from scratch on every Rebalance — no partial updates.
        private readonly Dictionary<int, Consumer> _partitionAssignment = [];

        // The single topic this group is consuming. A group subscribes to one
        // topic at a time in this demo; real Kafka allows multi-topic subscriptions.
        private string _subscribedTopic;

        public ConsumerGroup(string groupId, Broker broker)
        {
            GroupId = groupId;
            _broker = broker;
        }

        // Creates a new Consumer and immediately triggers Rebalance if the group
        // is already subscribed, so the new worker gets lanes assigned right away.
        // Returns the Consumer so the caller can use it for manual commits or lag checks.
        public Consumer AddConsumer(string consumerId)
        {
            var consumer = new Consumer(consumerId, _broker);
            _consumers.Add(consumer);
            if (_subscribedTopic != null) Rebalance();
            return consumer;
        }

        // Removes the consumer and triggers Rebalance so its orphaned partitions
        // are handed off to the remaining workers immediately.
        public void RemoveConsumer(string consumerId)
        {
            _consumers.RemoveAll(c => c.ConsumerId == consumerId);
            if (_subscribedTopic != null) Rebalance();
        }

        // Records which topic this group is reading and immediately distributes
        // its partitions across the current consumer roster.
        public void Subscribe(string topicName)
        {
            _subscribedTopic = topicName;
            Rebalance();
        }

        // Redistributes partitions evenly across all consumers using round-robin:
        // partition i → consumer[i % consumerCount].
        //
        // Example with 6 partitions and 2 consumers:
        //   partition 0,2,4 → consumer[0]
        //   partition 1,3,5 → consumer[1]
        //
        // Example with 6 partitions and 8 consumers:
        //   partitions 0-5 each go to one consumer; consumers 6 and 7 sit idle.
        //
        // Clears the map first so no stale assignment survives from the previous roster.
        // Kafka's default RangeAssignor produces a slightly different split for
        // unequal partition counts, but round-robin is simpler and equally fair here.
        public void Rebalance()
        {
            if (_subscribedTopic == null || _consumers.Count == 0) return;
            Topic topic = _broker.GetTopic(_subscribedTopic);
            _partitionAssignment.Clear();
            for (int p = 0; p < topic.PartitionCount; p++)
                _partitionAssignment[p] = _consumers[p % _consumers.Count];
        }

        // Returns a human-readable view of the assignment: consumerId → [partition indices].
        // Inverts the internal partition→consumer map for display and diagnostics.
        // Useful for verifying that the rebalance distributed load as expected.
        public Dictionary<string, List<int>> GetAssignment()
        {
            var result = new Dictionary<string, List<int>>();
            foreach (var kv in _partitionAssignment)
            {
                string id = kv.Value.ConsumerId;
                if (!result.ContainsKey(id)) result[id] = [];
                result[id].Add(kv.Key);
            }
            return result;
        }

        // Polls every assigned partition in one call — each partition is polled
        // by the Consumer that owns it, using that consumer's committed offset.
        // Returns a flat list of (owner, message) pairs so the caller knows
        // which consumer processed which message and can commit per-consumer.
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

        // Aggregates lag across all assigned partitions.
        // totalLag is the headline metric: if it is growing over time, the group
        // as a whole is falling behind and needs more consumers or faster processing.
        // perPartitionLag pinpoints which specific lane is the bottleneck —
        // useful when one partition has a hot key producing far more messages than others.
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
