// PartitionLog — append-only ordered log for one partition of a topic.
//
// THE BIG IDEA:
// Think of PartitionLog as a cassette tape. You can only record new audio
// at the end (Append) — you never rewind and overwrite the middle. Consumers
// hold a "tape head position" (their current offset) and can ask:
//   "Play me everything from position 42 onward" (ReadFrom).
//
// This design means any consumer can independently replay from any point in
// history. Two consumers with different offsets into the same log are like
// two people listening to the same tape starting from different spots —
// they don't interfere with each other, and the tape is never altered.
//
// WHY APPEND-ONLY:
// Sequential disk writes saturate I/O bandwidth far better than random writes.
// Real Kafka segments are plain files you only ever append to and fsync.
// If you updated records in-place you'd need random seeks, which kill
// throughput at scale and make snapshots and replication much harder.
//
// WHY OFFSET = LOG LENGTH (not a separate counter):
// A List<T> in C# grows by appending. After N appends, Count == N, and the
// last element sits at index N-1. So the *next* available slot is always
// _log.Count before the append. Assigning offset = _log.Count before the
// Add gives us 0-based offsets that match the list index exactly — no
// off-by-one, no extra counter to keep in sync.
//
// WHY A SINGLE LOCK (not ConcurrentQueue or lock-free):
// Producers and consumers hit different methods (Append vs ReadFrom), but
// both need a consistent view of _log.Count. A single lock is the simplest
// way to guarantee that. Contention is low because each topic partition is
// a separate PartitionLog instance — producers and consumers for different
// partitions never compete. Real Kafka removes even this contention by
// routing all writes/reads for a partition to a single leader thread.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class PartitionLog
    {
        // The actual storage: a list of messages in arrival order.
        // List<T> gives O(1) append and O(1) index access (GetRange is a slice, not a scan).
        // We never remove or reorder entries — immutability is what makes replay safe.
        private readonly List<Message> _log = [];

        // Guards both Append and ReadFrom. Without it, a concurrent Append
        // could change _log.Count mid-way through a ReadFrom slice calculation,
        // returning a range that doesn't exist yet and throwing ArgumentException.
        private readonly object _lock = new();

        // This partition's position in the topic (0-based). Stamped into every
        // message on Append so the message knows its own address without the caller
        // having to track which partition it came from.
        public int Index { get; }

        // The topic this partition belongs to. Stored here for logging and
        // diagnostics — lets you identify a PartitionLog in isolation without
        // walking back up to the Broker.
        public string TopicName { get; }

        public PartitionLog(string topicName, int index)
        {
            TopicName = topicName;
            Index = index;
        }

        public long Append(Message msg)
        {
            lock (_lock)
            {
                // Stamp the message with its permanent address before adding it.
                // After this point the message knows exactly where it lives in the cluster:
                // (TopicName, Index, msg.Offset) is globally unique and never changes.
                msg.Partition = Index;
                msg.Offset = _log.Count; // next slot index == current length
                _log.Add(msg);
                return msg.Offset;
            }
        }

        public List<Message> ReadFrom(long fromOffset, int maxCount = 100)
        {
            lock (_lock)
            {
                // Offset is past the end of the log — consumer is caught up, nothing to return.
                // This is the normal "no new messages" signal, not an error.
                if (fromOffset >= _log.Count) return [];

                // Cap the slice so we don't return more than maxCount messages
                // even if the consumer is far behind. This prevents a single
                // slow consumer from pulling the entire log into memory in one shot.
                int take = Math.Min(_log.Count - (int)fromOffset, maxCount);
                return _log.GetRange((int)fromOffset, take);
            }
        }

        // Returns the offset a new consumer should start from if it wants
        // only future messages (i.e. "tail the log"). Equal to _log.Count,
        // not Count-1, because the next message will land at offset=Count.
        // Passing this value to ReadFrom immediately returns an empty list
        // until the next Append — the correct "nothing new" response.
        public long LatestOffset
        {
            get { lock (_lock) return _log.Count; }
        }
    }
}
