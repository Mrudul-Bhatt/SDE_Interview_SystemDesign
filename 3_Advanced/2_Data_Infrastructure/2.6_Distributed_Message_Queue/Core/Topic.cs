// Topic — named stream split into a fixed number of partitions.
//
// Why MD5 for key→partition routing: stable across processes and machines,
// so every broker independently computes the same partition for a given key
// without coordination. GetHashCode() would give different values per process.
//
// Why fixed partition count: repartitioning is expensive (all consumers must
// reshuffle). Choose a count that allows future parallelism (e.g. 12 allows
// 1, 2, 3, 4, 6, or 12 consumers with no idle partitions).

using System.Security.Cryptography;
using System.Text;

namespace AdvancedDesigns
{
    public class Topic
    {
        public string Name           { get; }
        public int    PartitionCount { get; }
        public bool   IsCompacted    { get; }

        private readonly PartitionLog[] _partitions;

        public Topic(string name, int partitionCount, bool compacted = false)
        {
            Name           = name;
            PartitionCount = partitionCount;
            IsCompacted    = compacted;
            _partitions    = new PartitionLog[partitionCount];
            for (int i = 0; i < partitionCount; i++)
                _partitions[i] = new PartitionLog(name, i);
        }

        // Deterministic hash: same key always → same partition index.
        public int GetPartitionIndex(string key)
        {
            if (key == null) return 0;
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
            return Math.Abs(BitConverter.ToInt32(hash, 0)) % PartitionCount;
        }

        public PartitionLog GetPartition(int index) => _partitions[index];

        public long[] GetLatestOffsets() => _partitions.Select(p => p.LatestOffset).ToArray();
    }
}
