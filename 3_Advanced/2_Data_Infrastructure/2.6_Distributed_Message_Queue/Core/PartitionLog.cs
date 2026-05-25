// PartitionLog — append-only ordered log for one partition of a topic.
//
// Why append-only: sequential writes are the fastest possible disk operation.
// Kafka segments are just files you fsync and never rewrite. Random writes
// (updating a record in-place) would destroy throughput at scale.
//
// Why lock on _log: Append and ReadFrom can be called concurrently from
// multiple producer/consumer threads. A simple lock is sufficient because
// contention here is very low — real Kafka uses per-partition leaders to
// avoid any cross-thread contention entirely.

namespace AdvancedDesigns
{
    public class PartitionLog
    {
        private readonly List<Message> _log  = new();
        private readonly object        _lock = new();

        public int    Index     { get; }
        public string TopicName { get; }

        public PartitionLog(string topicName, int index)
        {
            TopicName = topicName;
            Index     = index;
        }

        public long Append(Message msg)
        {
            lock (_lock)
            {
                msg.Partition = Index;
                msg.Offset    = _log.Count; // offset = position in the log
                _log.Add(msg);
                return msg.Offset;
            }
        }

        public List<Message> ReadFrom(long fromOffset, int maxCount = 100)
        {
            lock (_lock)
            {
                if (fromOffset >= _log.Count) return new List<Message>();
                int take = Math.Min(_log.Count - (int)fromOffset, maxCount);
                return _log.GetRange((int)fromOffset, take);
            }
        }

        public long LatestOffset
        {
            get { lock (_lock) return _log.Count; }
        }
    }
}
