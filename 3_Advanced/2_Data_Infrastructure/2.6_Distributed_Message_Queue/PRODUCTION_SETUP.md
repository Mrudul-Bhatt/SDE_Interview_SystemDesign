# Distributed Message Queue — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on Apache Kafka).

---

## `Models/Message.cs` — Plain C# object with string Key/Value

**Problem in production:** Three issues:
1. C# objects can't be written to disk or sent over the network as-is
2. `string` values can't carry binary payloads (images, Avro/Protobuf records)
3. No schema — a consumer has no idea how to deserialize a value it receives

**Production replacement: Binary record format + Schema Registry**

Kafka's on-wire record is a compact binary structure, not a C# object:

```
Record:
  ┌────────────────────────┐
  │ length (varint)        │
  │ attributes (1 byte)    │  compression, timestamp type
  │ timestampDelta (varint)│  relative to batch base timestamp
  │ offsetDelta (varint)   │  relative to batch base offset
  │ keyLength + key bytes  │  raw bytes, not a string
  │ valueLength + value    │  raw bytes (Avro/Protobuf/JSON)
  │ headers []             │  key-value metadata
  └────────────────────────┘
```

**Records are batched, not sent one-by-one** — a producer accumulates records into a `RecordBatch`, compresses the whole batch (LZ4/Zstd/Snappy), and sends it as one request. This amortizes network and disk overhead across hundreds of messages.

**Schema Registry (Confluent / Apicurio):** Values are serialized with Avro/Protobuf, and the schema is stored centrally:

```
Producer: serialize User → Avro bytes, prefix with schema ID (4 bytes)
Consumer: read schema ID → fetch schema from registry → deserialize correctly
```

This enables **schema evolution**: add an optional field, and old consumers keep working (forward/backward compatibility is enforced by the registry before the producer is even allowed to publish).

---

## `Models/ProduceResult.cs` — Simple (topic, partition, offset) receipt

**Problem in production:** The receipt is correct but incomplete. Production producers need to know more, and the write needs durability guarantees this object implies but doesn't enforce.

**Production replacement: RecordMetadata + acks + idempotent producer**

Kafka's `RecordMetadata` adds timestamp and serialized sizes, but the real upgrade is the **durability semantics behind the receipt**:

```
acks=0   → fire-and-forget, no receipt waited for      (fastest, can lose data)
acks=1   → leader wrote it to its log                   (default, loses data if leader dies before replication)
acks=all → leader + all in-sync replicas wrote it       (strongest, survives broker loss)
```

A `ProduceResult` should only be returned after `acks=all` for any topic you can't afford to lose.

**Idempotent producer (exactly-once for retries):**

```
Producer gets a Producer ID (PID) + per-partition sequence number.
Broker tracks the last sequence it accepted per (PID, partition).
A retried message with a sequence it already saw is silently deduplicated.
```

This turns the at-least-once retry described in the code comments into **exactly-once** delivery for the produce path — without the duplicate the comment warns about. Enabled with one config flag (`enable.idempotence=true`) in modern Kafka.

---

## `Core/PartitionLog.cs` — In-memory `List<Message>`, lost on crash

**Problem in production:** This is the biggest gap — the log is a `List<Message>` in RAM. If the process restarts, every message is gone. A message queue's entire job is *durable* storage.

**Production replacement: Segmented append-only files on disk + page cache**

A Kafka partition is a directory of **segment files**, not an in-memory list:

```
/orders-0/
  ┌──────────────────────────┐
  │ 00000000000000000000.log │  ← segment: records from offset 0
  │ 00000000000000000000.index│ ← sparse offset → byte-position index
  │ 00000000000000000000.timeindex│ ← timestamp → offset index
  │ 00000000000000170531.log │  ← next segment rolls at 1GB (or time limit)
  │ 00000000000000170531.index│
  └──────────────────────────┘
```

**Why segments:** Old segments can be deleted (retention) or compacted independently without touching the active segment being written.

**Zero-copy reads (`sendfile`):** When a consumer fetches, Kafka does *not* copy bytes into the JVM and back out. It calls `sendfile()` — the OS streams bytes directly from the page cache to the network socket, skipping user space entirely. This is why Kafka can saturate a NIC with a single thread.

**The OS page cache IS the read cache:** Kafka deliberately keeps almost nothing in application memory. Recently written messages are still in the page cache, so consumers reading the tail of the log (the common case) hit RAM without Kafka managing a cache at all.

**`ReadFrom` becomes a fetch with a byte budget:** Real consumers fetch by `max.bytes`, and the broker uses the `.index` file to seek directly to the byte offset for a given message offset — no scanning.

---

## `Core/Topic.cs` — Fixed partition array in one process

**Problem in production:** Three gaps:
1. All partitions live in one process — no distribution across machines
2. No replication — one disk failure loses the partition
3. `MD5` is computed but partitions can't actually move between brokers

**Production replacement: Partitions distributed + replicated across a broker cluster**

Each partition has a **leader** and **follower replicas** spread across different brokers:

```
Topic "orders", 3 partitions, replication.factor=3:

  Partition 0:  leader=Broker1  followers=[Broker2, Broker3]
  Partition 1:  leader=Broker2  followers=[Broker3, Broker1]
  Partition 2:  leader=Broker3  followers=[Broker1, Broker2]
```

**In-Sync Replicas (ISR):** The set of followers fully caught up to the leader. `acks=all` waits for all ISR members. If a follower falls behind, it drops out of the ISR; if the leader dies, a new leader is elected only from the ISR (guaranteeing no committed data is lost).

**Partition count is still a one-way decision** — exactly as the code comment notes. Increasing partitions changes `hash(key) % N`, breaking key→partition stability. Production teams over-provision partitions upfront (e.g., 30 partitions for a topic that needs 6 today) to leave room for consumer scaling.

**The partition hashing** (the project's `GetPartitionIndex`) maps to Kafka's `DefaultPartitioner`, which uses **murmur2** (faster than MD5) over the key bytes.

---

## `Core/Broker.cs` — In-process topic registry (a `Dictionary`)

**Problem in production:** The "broker" is a `Dictionary<string, Topic>` in one process. A real broker is a network server, one of many in a cluster, and topic metadata must be shared cluster-wide.

**Production replacement: A cluster of broker servers + KRaft metadata quorum**

```
Kafka cluster:
  Broker 1 (hosts partition leaders + followers)
  Broker 2 (hosts partition leaders + followers)
  Broker 3 (hosts partition leaders + followers)
        ↕  all coordinate via...
  KRaft metadata quorum (controllers)
    → stores: topics, partitions, ISR, configs, ACLs
    → replaces the old ZooKeeper dependency (Kafka 3.x+)
```

**Controller role:** One broker is elected controller. It handles partition leader election, broker join/leave, and propagates metadata changes to all brokers. In KRaft mode this metadata is itself a replicated Raft log — eating Kafka's own dog food.

**Client bootstrap:** Producers/consumers connect to any `bootstrap.servers`, then receive the full cluster metadata (which broker leads which partition) and connect directly to the right leader thereafter. The "throw on missing topic" behaviour maps to `UNKNOWN_TOPIC_OR_PARTITION`, optionally combined with `auto.create.topics.enable`.

---

## `Core/LogCompactor.cs` — One-shot in-memory compaction

**Problem in production:** Three gaps:
1. Runs once, synchronously, over an in-memory list — real compaction is continuous and operates on disk segments
2. Loads the entire log into a `Dictionary` — won't fit in RAM for a TB-sized topic
3. No `delete.retention.ms` — tombstones are dropped immediately, so a consumer that was offline during the delete never learns the key was deleted

**Production replacement: Background log-cleaner threads (`cleanup.policy=compact`)**

```
Per partition, the log cleaner:
  1. Builds an offset map of key → latest offset (only for the "dirty" tail)
  2. Recopies segments, keeping only records at their latest offset
  3. Retains tombstones for delete.retention.ms (default 24h) so offline
     consumers still see the deletion before it's purged
  4. Swaps in the cleaned segments, deletes the originals
```

**Two retention policies (can combine):**

```
cleanup.policy=delete   → drop whole segments older than retention.ms / retention.bytes
cleanup.policy=compact  → keep latest value per key forever (changelog topics)
cleanup.policy=compact,delete → compact, but also age out very old keys
```

Compacted topics power **Kafka Streams state stores** and **CDC** (change-data-capture): the topic becomes a replayable snapshot of "current state per key" — exactly the use case in the code comment, at production scale.

---

## `Service/Producer.cs` — Synchronous, single-threaded, in-process append

**Problem in production:** Four gaps:
1. Writes are synchronous and in-process — no network, no batching
2. No retries, no acks — a failed write just throws
3. `Interlocked.Increment` round-robin is fine locally but production uses sticky batching for efficiency
4. No backpressure — an unbounded producer can OOM

**Production replacement: Async batching producer with an in-flight buffer**

```
producer.send(record)  →  returns a Future immediately
        ↓
  Record accumulator: groups records by partition into batches
        ↓
  Sender thread: drains full (or linger.ms-aged) batches
        ↓
  Compress batch (lz4) → send to partition leader → await acks
        ↓
  On success: complete the Future with RecordMetadata
  On retriable error: retry (respecting max.in.flight + idempotence for ordering)
```

**Key production configs:**

```
batch.size=16384         accumulate up to 16KB per partition before sending
linger.ms=5              wait up to 5ms to fill a batch (latency vs throughput trade)
compression.type=lz4     compress batches on the wire and on disk
acks=all                 durability
enable.idempotence=true  exactly-once produce, preserves ordering on retry
max.in.flight=5          pipelining depth (≤5 keeps ordering with idempotence)
buffer.memory=32MB       backpressure: send() blocks when this fills
```

**Sticky partitioner:** Modern Kafka (replacing naive round-robin) sticks keyless messages to one partition until the current batch fills, then switches. This produces fuller batches → fewer, larger requests → higher throughput, while still balancing load over time.

---

## `Service/Consumer.cs` — Manual offset dictionary, no group coordination

**Problem in production:** Four gaps:
1. Committed offsets live in an in-memory `Dictionary` — lost on restart, defeating the whole point of a bookmark
2. No automatic partition assignment — the consumer must be told which partition to read
3. `Poll` returns immediately — real consumers long-poll
4. No heartbeating — the group can't tell if this consumer died

**Production replacement: Group-coordinated consumer with durable offset commits**

**Offsets are committed to Kafka itself** (the internal `__consumer_offsets` compacted topic), keyed by `(group, topic, partition)`:

```
On restart, the consumer fetches its last committed offset from __consumer_offsets
  → resumes exactly where it left off, even on a different machine.
```

**Commit strategies:**

```
enable.auto.commit=true   → commit every auto.commit.interval.ms (5s). Simple,
                            but can re-deliver up to 5s of messages on crash.
Manual commitSync()       → commit after processing each batch. At-least-once,
                            tighter window.
Manual + external store   → commit offset transactionally with your DB write.
                            Enables exactly-once processing.
```

**Long-poll fetch:** `poll(Duration)` blocks up to `fetch.max.wait.ms` until `fetch.min.bytes` is available — no busy-waiting, low latency when data arrives, no wasted CPU when idle.

**Heartbeats:** A background thread sends heartbeats every `heartbeat.interval.ms`. If the coordinator misses them for `session.timeout.ms`, the consumer is declared dead and its partitions are reassigned (triggering a rebalance).

---

## `Service/ConsumerGroup.cs` — In-process round-robin assignment

**Problem in production:** The group logic is local — `Rebalance()` is a direct method call over an in-memory consumer list. In production, group members are processes on different machines that must coordinate through a broker.

**Production replacement: Group Coordinator + rebalance protocol**

```
One broker acts as the Group Coordinator for this group.

Join:    each consumer sends JoinGroup → coordinator picks a leader consumer
Assign:  the leader runs the assignment algorithm, sends the plan via SyncGroup
Commit:  members commit offsets to __consumer_offsets
Detect:  coordinator tracks heartbeats; a missed session triggers rebalance
```

**Smarter assignment strategies** (vs the project's `partition % consumerCount`):

```
RangeAssignor       → contiguous partition ranges per consumer (default)
RoundRobinAssignor  → spreads partitions evenly across all consumers
StickyAssignor      → minimizes partition movement on rebalance
CooperativeStickyAssignor → incremental rebalance: only reassigned partitions
                            pause; the rest keep consuming (no stop-the-world)
```

**Stop-the-world problem the project doesn't model:** A naive rebalance pauses *all* consumers while partitions are reassigned. Cooperative rebalancing (Kafka 2.4+) lets unaffected partitions keep flowing — critical for low-latency pipelines.

**Static membership (`group.instance.id`):** Gives each consumer a stable identity so a quick restart (deploy, pod reschedule) does *not* trigger a rebalance — the returning member reclaims its old partitions.

---

## `Program.cs` — Six sequential in-memory demo scenarios

**Problem in production:** It's a single-process demo that runs scenarios end to end. Production is many always-on producer and consumer services across a cluster.

**Production replacement: Deployed services + Kafka cluster + stream processing**

```
Producers:  microservices emitting events (order-service, payment-service…)
Cluster:    3+ brokers, replication.factor=3, min.insync.replicas=2
Consumers:  consumer-group services, each horizontally scaled to N pods
Streams:    Kafka Streams / Flink jobs for joins, aggregations, windowing
Connect:    Kafka Connect sink/source to databases, S3, Elasticsearch
```

The six scenarios map to real concerns: pub/sub (Connect + microservices), key partitioning (event ordering per entity), consumer groups (horizontal scaling), lag (the #1 operational metric), rebalance (deploys and autoscaling), and compaction (changelog/CDC topics).

---

## Cross-cutting concerns not modelled in this project

The sections above replace specific classes. The concerns below are global — they don't map to a single file but every production message queue must address them.

### 1. Replication & durability

**Problem in this project:** Zero replication. The in-memory log dies with the process.

```
replication.factor=3       each partition has 3 copies on 3 brokers
min.insync.replicas=2      a write needs ≥2 in-sync copies to be acknowledged
acks=all                   producer waits for all ISR

Together: survives the loss of ANY ONE broker with zero data loss.
```

**Unclean leader election:** Disabled by default (`unclean.leader.election.enable=false`) — Kafka would rather make a partition unavailable than elect an out-of-sync replica and lose committed messages.

---

### 2. Delivery semantics & transactions

**Problem in this project:** Only at-least-once (with manual dedup) is described.

**Exactly-once semantics (EOS):** Kafka combines three mechanisms:

```
1. Idempotent producer    → no duplicate writes on retry
2. Transactions           → atomic write across multiple partitions/topics
3. Read-process-write loop → consume + produce + commit offset in ONE transaction

  producer.beginTransaction()
  producer.send(outputTopic, transformed)
  producer.sendOffsetsToTransaction(consumedOffsets, groupId)
  producer.commitTransaction()   ← all-or-nothing
```

Consumers set `isolation.level=read_committed` to skip aborted transactions. This is how Kafka Streams achieves end-to-end exactly-once.

---

### 3. Observability — the lag-first mindset

**Problem in this project:** Lag is computed but there's no monitoring system.

**Consumer lag is the #1 metric:**

```
kafka_consumergroup_lag{group, topic, partition}   messages behind per partition
  → sum across partitions = total group lag
  → alert when lag grows monotonically (consumer can't keep up)
  → alert when lag spikes (consumer stalled / crashed)
```

**Broker & topic metrics (JMX → Prometheus):**

```
kafka_server_BrokerTopicMetrics_MessagesInPerSec     ingest rate
kafka_server_BrokerTopicMetrics_BytesInPerSec        throughput
kafka_server_ReplicaManager_UnderReplicatedPartitions  ← must be 0
kafka_server_ReplicaManager_OfflinePartitionsCount     ← must be 0
kafka_controller_ActiveControllerCount                 ← must be exactly 1
kafka_log_LogFlushRateAndTimeMs                        flush latency
request_handler_avg_idle_percent                       broker saturation
```

**Tools:** Burrow / Kafka Lag Exporter for lag, Cruise Control for cluster balancing, `kafka-consumer-groups.sh` for ad-hoc inspection.

---

### 4. Security

**Problem in this project:** Any caller can produce/consume anything. Data is plaintext.

```
Encryption in transit:  TLS 1.3 on all client↔broker and broker↔broker links
Authentication:         SASL (SCRAM, OAUTHBEARER, or mTLS client certs)
Authorization:          ACLs per (principal, operation, resource)
  e.g. User:order-svc  ALLOW WRITE  Topic:orders
       User:report-svc ALLOW READ   Group:reporting  Topic:orders
Encryption at rest:      disk-level (LUKS/KMS) or client-side payload encryption
Audit:                   authorizer logs every allow/deny decision
```

Multi-tenant clusters add **quotas** per principal to prevent a noisy tenant from saturating brokers.

---

### 5. Capacity planning & sizing

```
Partitions:
  target throughput / per-partition throughput = partition count
  e.g. 600 MB/s needed ÷ ~10 MB/s per partition = 60 partitions
  also: partition count ≥ max expected consumers in a group

Retention:
  disk per partition = throughput × retention.ms × replication.factor
  e.g. 10MB/s × 7 days × 3 = ~18 TB per partition-week (plan headroom)

Brokers:
  partitions per broker < ~4000 (metadata/leader-election overhead)
  keep disk < 70% full (compaction + segment churn need headroom)
  network: (RF-1) × ingest is replication traffic, plan NIC accordingly
```

**Capacity smells:** under-replicated partitions > 0, consumer lag trending up, request-handler idle % near 0, disk > 70%, ISR shrinking repeatedly (network or GC pauses).

---

### 6. Operational tooling

```
Add a broker:        new broker joins → partitions reassigned (kafka-reassign-partitions)
                     → throttled so live traffic isn't starved
Remove a broker:     move its partitions off first, then decommission
Rebalance load:      Cruise Control auto-balances partition/leader distribution
Topic config change: kafka-configs.sh (retention, compaction, partitions↑)
Tiered storage:      offload old segments to S3 (KIP-405) → cheap infinite retention
                     while brokers keep only hot recent data locally
```

**Never** decrease partitions — it's impossible without recreating the topic.

---

## The Full Production Picture

```
Producer service (async, batched, idempotent)
      ↓ TLS + SASL auth, acks=all, lz4 compression, idempotence on
Bootstrap → fetch cluster metadata → connect directly to partition LEADER
      ↓
Partition leader (Broker N)
  → append RecordBatch to active segment file (sequential disk write)
  → replicate to followers in the ISR
  → wait for min.insync.replicas acks   (acks=all)
  → return RecordMetadata (topic, partition, offset, timestamp)
        ↓ page cache holds the tail; sendfile() will serve it zero-copy

Consumer group (coordinated, horizontally scaled)
      ↓ JoinGroup → SyncGroup → partition assignment (cooperative-sticky)
  → long-poll fetch from each owned partition's leader
  → zero-copy stream from page cache → network
  → process batch
  → commit offsets to __consumer_offsets  (or transactionally with a DB)
  → heartbeat every interval; miss → rebalance

Background processes (always running):
  Replication (ISR)        → keep follower logs in sync, maintain durability
  Log retention            → delete segments past retention.ms / retention.bytes
  Log compaction           → keep latest value per key on compacted topics
  Leader election (KRaft)  → promote an ISR follower if a leader dies
  Rebalancing              → reassign partitions on membership change
  Tiered storage offload   → ship cold segments to S3

Cluster-wide observability (always-on):
  Consumer lag exporter    → per-group, per-partition lag (the headline metric)
  JMX → Prometheus         → throughput, under-replicated/offline partitions
  Distributed tracing      → producer → topic → consumer span propagation
  Audit log                → every ACL allow/deny

Stream processing layer (optional, on top):
  Kafka Streams / Flink    → joins, windowed aggregations, stateful processing
  Kafka Connect            → source/sink to DBs, S3, Elasticsearch
  Schema Registry          → Avro/Protobuf schemas + compatibility enforcement
```

The core logic (append-only partitioned log, offset-based consumption, key→partition hashing, consumer groups, log compaction, at-least-once/idempotent delivery) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with replication, disk persistence, network protocols, exactly-once semantics, security, observability, and operability.
