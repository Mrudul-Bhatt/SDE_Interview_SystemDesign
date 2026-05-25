// DistributedKvStore — coordinates multiple KvNodes with quorum reads/writes.
//
// Quorum rule: W + R > N guarantees at least one node in every read overlaps
// with the write quorum, so the latest version is always visible.
// Default: N=3, W=2, R=2 → strong consistency (AP with tunable C).
//
// Read repair: after a quorum read, any replica that returned an older
// timestamp is silently brought up to date. This converges replicas without
// a dedicated anti-entropy process, trading a small write overhead on reads.
//
// Hinted handoff (not implemented): in a real system, writes to a down node
// are buffered on a healthy node and replayed when the down node recovers.
// Here we simply skip down nodes and rely on quorum to absorb the gap.

namespace AdvancedDesigns
{
    public class DistributedKvStore
    {
        private readonly Dictionary<string, KvNode> _nodes = new();
        private readonly ConsistentHashRing         _ring;
        private readonly int                        _replicationFactor;
        private readonly int                        _writeQuorum;
        private readonly int                        _readQuorum;
        private readonly HashSet<string>            _downNodes = new();

        public DistributedKvStore(int replicationFactor = 3, int writeQuorum = 2, int readQuorum = 2)
        {
            _replicationFactor = replicationFactor;
            _writeQuorum       = writeQuorum;
            _readQuorum        = readQuorum;
            _ring              = new ConsistentHashRing(virtualNodes: 50);
        }

        public void AddNode(string nodeId)
        {
            _nodes[nodeId] = new KvNode(nodeId);
            _ring.AddNode(nodeId);
        }

        public void SimulateNodeDown(string nodeId) => _downNodes.Add(nodeId);
        public void SimulateNodeUp(string nodeId)   => _downNodes.Remove(nodeId);

        public bool Put(string key, string value, int? ttlSeconds = null)
        {
            int acks = 0;
            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                _nodes[nodeId].Put(key, value, ttlSeconds);
                if (++acks >= _writeQuorum) break; // stop once quorum is satisfied
            }
            return acks >= _writeQuorum;
        }

        public (bool found, string value) Get(string key)
        {
            var responses = new List<(string nodeId, bool found, string value, long timestamp)>();

            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                var (found, value, ts) = _nodes[nodeId].Get(key);
                responses.Add((nodeId, found, value, ts));
                if (responses.Count >= _readQuorum) break;
            }

            if (responses.Count < _readQuorum) return (false, null);

            var best = responses.OrderByDescending(r => r.timestamp).First();
            if (!best.found) return (false, null);

            // Read repair: silently update any replica that is behind.
            foreach (var r in responses)
            {
                if (r.timestamp < best.timestamp && !_downNodes.Contains(r.nodeId))
                    _nodes[r.nodeId].Put(key, best.value);
            }

            return (true, best.value);
        }

        public bool Delete(string key)
        {
            int acks = 0;
            foreach (string nodeId in _ring.GetNodes(key, _replicationFactor))
            {
                if (_downNodes.Contains(nodeId)) continue;
                _nodes[nodeId].Delete(key);
                if (++acks >= _writeQuorum) break;
            }
            return acks >= _writeQuorum;
        }

        public List<string>             GetResponsibleNodes(string key)  => _ring.GetNodes(key, _replicationFactor);
        public Dictionary<string, int>  GetRingDistribution()            => _ring.GetLoadDistribution();
    }
}
