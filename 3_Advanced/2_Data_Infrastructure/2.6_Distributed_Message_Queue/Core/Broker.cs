// Broker — registry of topics. In a real Kafka cluster, each broker hosts a
// subset of partition leaders and followers. Here one Broker holds everything
// to keep the demo single-process.
//
// Topic creation is the admin-plane operation; Produce/Consume are data-plane.
// Separating them means topic schema changes don't touch producer/consumer code.

namespace AdvancedDesigns
{
    public class Broker
    {
        private readonly Dictionary<string, Topic> _topics = new();

        public void CreateTopic(string name, int partitions, bool compacted = false)
            => _topics[name] = new Topic(name, partitions, compacted);

        public Topic GetTopic(string name)
        {
            if (!_topics.TryGetValue(name, out Topic topic))
                throw new InvalidOperationException($"Topic '{name}' does not exist");
            return topic;
        }

        public bool TopicExists(string name) => _topics.ContainsKey(name);
    }
}
