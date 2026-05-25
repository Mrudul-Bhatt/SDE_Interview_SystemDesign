// Message — the unit of data flowing through the queue.
//
// Key drives partitioning: all messages with the same key land on the same
// partition, guaranteeing ordering for that key (e.g. all events for user:Alice
// are processed in the sequence they were produced).
//
// Offset is assigned by the PartitionLog at append time, not by the producer,
// so the log owns the ordering guarantee — producers can't inject false order.

namespace AdvancedDesigns
{
    public class Message
    {
        public string   Key       { get; }
        public string   Value     { get; }
        public long     Offset    { get; set; }
        public int      Partition { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public Dictionary<string, string> Headers { get; } = new();

        public Message(string key, string value)
        {
            Key   = key;
            Value = value;
        }

        public override string ToString() =>
            $"[P{Partition}@{Offset}] key={Key ?? "(null)"} value={Value ?? "(tombstone)"}";
    }
}
