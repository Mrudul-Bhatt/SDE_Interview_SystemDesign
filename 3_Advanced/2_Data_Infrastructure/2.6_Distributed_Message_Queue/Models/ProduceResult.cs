// ProduceResult — receipt returned to the producer after a successful append.
// The (topic, partition, offset) triple is the globally unique address of the message.
// Producers can use this to implement at-least-once delivery by retrying only
// if they don't receive a result (no duplicate risk if the broker is idempotent).

namespace AdvancedDesigns
{
    public class ProduceResult
    {
        public string Topic     { get; }
        public int    Partition { get; }
        public long   Offset    { get; }

        public ProduceResult(string topic, int partition, long offset)
        {
            Topic     = topic;
            Partition = partition;
            Offset    = offset;
        }
    }
}
