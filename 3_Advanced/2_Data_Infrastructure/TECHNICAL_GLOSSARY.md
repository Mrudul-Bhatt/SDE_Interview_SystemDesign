# Technical Glossary — Data Infrastructure

Terms found across the **Distributed KV Store** and **Distributed Message Queue** projects.

These are deeper, more production-oriented than the Foundational Designs terms — most relate to how real databases (Cassandra, RocksDB, DynamoDB) and message brokers (Kafka) work internally.

---

## Storage Engine (LSM Tree) — *Distributed KV Store*

| Term | Where | One-line meaning |
|---|---|---|
| **LSM Tree (Log-Structured Merge Tree)** | KvNode | Write strategy: buffer in RAM, flush to immutable sorted files, merge in the background |
| **MemTable** | MemTable | The in-RAM write buffer — the first place every write lands; sorted for fast flushing |
| **SSTable (Sorted String Table)** | SSTable | An immutable, sorted, on-disk file created when the MemTable fills up |
| **Flush** | KvNode, MemTable | Converting a full MemTable into a new immutable SSTable and clearing the buffer |
| **Compaction** | SSTable, StorageEntry | Background merging of SSTables to keep the newest value per key and drop tombstones |
| **Levels (L0, L1, L2…)** | SSTable | Tiers of SSTables; L0 may overlap, L1+ are non-overlapping so reads probe fewer files |
| **Tombstone** | StorageEntry (TombstoneEntry) | A deletion marker — you can't erase from an immutable file, so you write "this is dead" |
| **Write-Ahead Log (WAL)** | SSTable (comment) | Append-only crash-recovery log that replays writes lost from the MemTable on restart |
| **Write Amplification** | SSTable | One logical write causing multiple physical writes (compaction rewrites data repeatedly) |
| **Sparse Index** | SSTable (comment) | An in-memory index of every Nth key, so you binary-search to the right disk block |
| **Bloom Filter** | BloomFilter | Probabilistic bit array — answers "definitely not here" in O(1), skipping needless disk reads |
| **Lazy Expiry** | StorageEntry (IsExpired) | Don't delete expired keys on a timer; just hide them on read until compaction cleans up |

---

## Distributed Systems — *Distributed KV Store*

| Term | Where | One-line meaning |
|---|---|---|
| **Consistent Hashing / Hash Ring** | ConsistentHashRing | Map keys to nodes on a ring so adding/removing a node moves minimal data |
| **Virtual Nodes (vnodes)** | ConsistentHashRing | Many ring positions per physical node, spreading load evenly and avoiding hot spots |
| **Replication Factor (RF / N)** | DistributedKvStore | How many nodes store a copy of each key (default N=3) |
| **Quorum (W + R > N)** | DistributedKvStore | If write-copies + read-copies exceed total copies, every read sees the latest write |
| **Write Quorum (W) / Read Quorum (R)** | DistributedKvStore | Minimum nodes that must ack a write / respond to a read before it counts as success |
| **Read Repair** | DistributedKvStore | After a quorum read, silently update any replica that returned a stale value |
| **Hinted Handoff** | DistributedKvStore (comment) | Buffer writes for a down node on a healthy node, replay them when it recovers |
| **Last-Writer-Wins (LWW)** | StorageEntry | Conflict resolution where the value with the highest timestamp wins |
| **Logical Clock** | KvNode | A monotonically increasing counter (not wall-clock) used to order writes |
| **Hybrid Logical Clock (HLC)** | StorageEntry (comment) | Combines wall-clock + counter so same-millisecond writes still get a strict order |
| **Network Partition** | DistributedKvStore | When nodes can't reach each other; simulated here via SimulateNodeDown/Up |
| **Gossip Protocol** | DistributedKvStore (comment) | Nodes periodically exchange state to detect failures without a central coordinator |
| **MD5 (stable hashing)** | ConsistentHashRing | Used instead of GetHashCode() because it's identical across machines and processes |

---

## Messaging & Streaming — *Distributed Message Queue*

| Term | Where | One-line meaning |
|---|---|---|
| **Topic** | Topic | A named stream of messages, split into partitions |
| **Partition** | PartitionLog, Topic | One parallel lane of a topic; ordering is guaranteed *within* a partition only |
| **Append-Only Log** | PartitionLog | Storage you only ever write to the end of — the fastest possible disk pattern |
| **Offset** | Message, PartitionLog | A message's permanent 0-based position within its partition |
| **Broker** | Broker | The server (here, one process) that holds topics and routes produce/consume calls |
| **Producer** | Producer | Client that writes messages, choosing a partition by key-hash or round-robin |
| **Consumer** | Consumer | Client that reads messages from a partition starting at its committed offset |
| **Consumer Group** | ConsumerGroup | A team of consumers that splits a topic's partitions among themselves |
| **Partition Rebalance** | ConsumerGroup | Recomputing partition→consumer ownership when a consumer joins or leaves |
| **Committed Offset** | Consumer | The "bookmark" — the next offset a consumer will read; stored as last-read + 1 |
| **Consumer Lag** | Consumer, ConsumerGroup | How many messages a consumer is behind: latestOffset − committedOffset |
| **Key-Based Partitioning** | Producer, Topic | hash(key) % N — same key always → same partition → ordering per key |
| **Round-Robin Partitioning** | Producer | Null-key messages rotate across partitions for even load (no ordering) |
| **Log Compaction** | LogCompactor | Keep only the latest message per key; shrinks the log to O(distinct keys) |
| **Pub/Sub** | Program (Scenario 1) | Publish/Subscribe — producers publish, multiple consumer groups subscribe independently |
| **Hot Standby** | ConsumerGroup | An idle consumer (more consumers than partitions) ready to take over on failover |
| **Partition Leader / Follower** | Broker (comment) | Real Kafka: leader accepts writes, followers replicate for fault tolerance |
| **Group Coordinator** | ConsumerGroup (comment) | The broker role that detects membership changes and drives rebalances in real Kafka |

---

## Delivery Guarantees — *Distributed Message Queue*

| Term | Where | One-line meaning |
|---|---|---|
| **At-Least-Once Delivery** | ProduceResult, Consumer | Retry until acknowledged; a message may be delivered twice but never lost |
| **Exactly-Once Semantics** | ProduceResult (comment) | Each message lands exactly once via producer ID + sequence dedup (costs latency) |
| **Idempotency** | ProduceResult, Consumer | Processing the same message twice has the same effect as once — handles duplicates safely |
| **Tombstone (null value)** | Message, LogCompactor | A null-valued message signalling "delete this key" during log compaction |

---

## Concurrency (shared by both projects)

| Term | Where | One-line meaning |
|---|---|---|
| **Atomic Operation** | Producer (`Interlocked.Increment`) | A single uninterruptible CPU step — no torn reads, no lock needed |
| **Lock / Mutex** | KvNode, PartitionLog | Forces one thread at a time into a critical section |
| **Immutability for thread-safety** | SSTable, StorageEntry | Read-only objects can be shared across threads with no locking at all |
| **Torn Read** | Producer (comment) | A half-updated value seen when two threads touch the same field without synchronisation |

---

## Production Infrastructure (mentioned in comments)

| Term | Where | One-line meaning |
|---|---|---|
| **Kafka** | Message Queue (throughout) | The industry-standard distributed log/message broker this project models |
| **Cassandra / DynamoDB** | KV Store (comments) | Production distributed key-value stores built on LSM trees + consistent hashing |
| **RocksDB / LevelDB** | SSTable, StorageEntry | Embedded LSM-tree storage engines used inside larger databases |
| **ZooKeeper / etcd** | KV Store (production notes) | Coordination services that store cluster/ring state reliably |
| **Protobuf** | KV Store (production notes) | Binary serialization format, 3–10× smaller than JSON |
| **fsync** | PartitionLog (comment) | The OS call that forces buffered writes to actually hit the disk |
