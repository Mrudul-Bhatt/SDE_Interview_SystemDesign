// Topic — named stream split into a fixed number of partitions.
//
// THE BIG IDEA:
// Think of a Topic as a highway with multiple lanes. All traffic with the
// same destination (key) is steered into the same lane (partition) — so
// events for "user:Alice" always arrive in order. Vehicles with no fixed
// destination (null key) just take lane 0.
//
// Each lane is an independent PartitionLog: a separate append-only tape
// that consumers read at their own pace. Adding more lanes lets you add
// more consumers to process traffic in parallel.
//
// WHY MD5 FOR KEY → PARTITION ROUTING (not GetHashCode):
// GetHashCode() is not guaranteed to be stable across processes, .NET
// versions, or machines. Two brokers hashing the same key could route it
// to different partitions — breaking the ordering guarantee entirely.
// MD5 is deterministic everywhere, so every broker independently arrives
// at the same partition index without any coordination call.
//
// WHY FIXED PARTITION COUNT (set at creation, never changed):
// Repartitioning is expensive: every consumer group must stop, recalculate
// which partitions it owns, and re-seek. More importantly, changing partition
// count breaks the key→partition mapping — messages that used to go to
// partition 3 might now go to partition 7, so ordering for a given key
// is lost across the boundary. Choose a count with many divisors upfront
// (e.g. 12 supports 1, 2, 3, 4, 6, or 12 consumers with no idle partitions).
//
// WHY IsCompacted:
// A compacted topic keeps only the latest value per key — older values for
// the same key are deleted during LogCompactor.Compact(). Used for "current
// state" topics (e.g. user profile cache) where you only care about the
// most recent version, not the full history.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AdvancedDesigns
{
    public class Topic
    {
        // The human-readable stream name (e.g. "orders", "payments").
        // Used by the Broker to look up this Topic and by PartitionLog for diagnostics.
        public string Name { get; }

        // How many parallel partitions (lanes) this topic has. Fixed at creation.
        // Determines maximum consumer parallelism — you can't have more active
        // consumers than partitions; extras sit idle waiting for a partition to free up.
        public int PartitionCount { get; }

        // When true, LogCompactor will prune this topic to keep only the
        // most recent message per key. When false, all messages are retained
        // (standard append-only history — good for event sourcing and auditing).
        public bool IsCompacted { get; }

        // One PartitionLog per lane. Indexed 0..PartitionCount-1.
        // Array (not List) because the partition count never changes after construction.
        private readonly PartitionLog[] _partitions;

        public Topic(string name, int partitionCount, bool compacted = false)
        {
            Name = name;
            PartitionCount = partitionCount;
            IsCompacted = compacted;
            _partitions = new PartitionLog[partitionCount];
            for (int i = 0; i < partitionCount; i++)
                _partitions[i] = new PartitionLog(name, i);
        }

        // Maps a message key to a partition index deterministically.
        // Same key → same index on every call, on every machine, forever.
        // Null key defaults to partition 0 (keyless messages have no ordering guarantee).
        public int GetPartitionIndex(string key)
        {
            if (key == null) return 0;
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
            // ToInt32 can be negative for high-bit hashes; Math.Abs keeps the index valid.
            return Math.Abs(BitConverter.ToInt32(hash, 0)) % PartitionCount;
        }

        // Direct access to a partition by its index — used by Producer to append
        // and by Consumer to read. Callers derive the index via GetPartitionIndex
        // (keyed) or round-robin (keyless load-balanced producers).
        public PartitionLog GetPartition(int index) => _partitions[index];

        // Snapshot of the latest offset in each partition. ConsumerGroup uses this
        // to detect lag: (LatestOffset - consumerOffset) = number of unread messages.
        public long[] GetLatestOffsets() => _partitions.Select(p => p.LatestOffset).ToArray();
    }
}
