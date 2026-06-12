# Distributed Message Queue — High-Level Design (System Architecture)

This is the **system-level** view: the production architecture behind a distributed
message queue (think Apache Kafka or AWS Kinesis). Two orthogonal concerns drive the
whole design: **where** a message lands (deterministic hash routing to a stable partition)
and **how** it is stored efficiently (append-only partition log — write in offset order,
read by seeking to any offset). For the class-level view see [LLD.md](LLD.md); for the
storage schema see [DB_DESIGN.md](DB_DESIGN.md).

> **How to view the diagrams below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If they don't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). They also render automatically on GitHub.

---

## System Architecture

```mermaid
flowchart TB
    CLIENT["🖥️ Client Application\nProduce events · Subscribe consumer groups"]

    PROD["Producer\nhash(key) % N → same partition forever\nnull key → atomic round-robin\nreturns ProduceResult(topic, partition, offset)"]

    subgraph BROKER["Broker — single name registry"]
        TA["Topic 'orders'\npartitionCount=4 · compacted=false"]
        TB["Topic 'profiles'\npartitionCount=4 · compacted=true"]
    end

    subgraph PLOGS["PartitionLog tier — one append-only tape per partition"]
        P0["orders / P0\nOffset 0…N"]
        P1["orders / P1\nOffset 0…N"]
        P2["orders / P2\nOffset 0…N"]
        P3["orders / P3\nOffset 0…N"]
    end

    subgraph CGBOX["ConsumerGroup 'billing'  (4 partitions, 3 consumers)"]
        CA["Consumer A\nowned: p0, p3\ncommitted: 8, 11"]
        CB["Consumer B\nowned: p1\ncommitted: 5"]
        CC["Consumer C\nidle — hot standby"]
    end

    COMP["LogCompactor  (background · compacted topics only)\nPass 1: latest[key] wins · Pass 2: strip tombstones\nresult: one message per key, deleted keys absent"]

    CLIENT -->|"Produce(topicName, key, value)"| PROD
    PROD -->|"hash(key) → partition → Append(msg)"| TA
    PROD -->|"hash(key) → partition → Append(msg)"| TB
    TA --> P0 & P1 & P2 & P3
    P0 & P1 & P2 & P3 -->|"ReadFrom(committedOffset, maxCount)"| CA & CB
    TB -.->|"trigger after writes"| COMP
    CLIENT -->|"Subscribe / AddConsumer"| CGBOX

    classDef client  fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a;
    classDef router  fill:#ede9fe,stroke:#8b5cf6,color:#4c1d95;
    classDef storage fill:#fef3c7,stroke:#f59e0b,color:#78350f;
    classDef consumer fill:#dcfce7,stroke:#22c55e,color:#14532d;
    classDef bg      fill:#f1f5f9,stroke:#94a3b8,color:#334155;

    class CLIENT client;
    class PROD router;
    class TA,TB,P0,P1,P2,P3 storage;
    class CA,CB,CC consumer;
    class COMP bg;
```

---

## ① Produce path — `Produce("orders", "user:Alice", "order:101 created")`

```mermaid
sequenceDiagram
    participant C  as Client
    participant PR as Producer
    participant BR as Broker
    participant TP as Topic "orders"
    participant PL as PartitionLog[2]

    C->>PR: Produce("orders", "user:Alice", "order:101 created")
    PR->>BR: GetTopic("orders")
    BR-->>PR: Topic { partitionCount=4, compacted=false }

    PR->>TP: GetPartitionIndex("user:Alice")
    Note over TP: MD5("user:Alice") → bytes → ToInt32 → Math.Abs % 4 = 2
    TP-->>PR: 2  ← stable on every machine, every process, forever

    PR->>PL: Append(new Message("user:Alice", "order:101 created"))
    Note over PL: lock  →  msg.Partition = 2\n             msg.Offset    = _log.Count  → 7\n             _log.Add(msg)\n             return 7
    PL-->>PR: offset = 7

    PR-->>C: ProduceResult { Topic="orders", Partition=2, Offset=7 }
    Note over C: store (Partition=2, Offset=7) in your DB alongside the order row\nfor point-in-time audit seeks — no scanning needed
```

---

## ② Consume path — `Poll` → process → `Commit`

```mermaid
sequenceDiagram
    participant CA as Consumer A
    participant PL as PartitionLog[2]
    participant DS as Downstream (e.g. database)

    CA->>CA: fromOffset = GetCommittedOffset("orders", 2)  → 5
    CA->>PL: ReadFrom(5, maxCount=10)
    Note over PL: lock  →  take = min(_log.Count−5, 10) = 3\n          return _log.GetRange(5, 3)
    PL-->>CA: [msg@5, msg@6, msg@7]

    loop for each message (in offset order)
        CA->>DS: write side effect (insert / update)
        DS-->>CA: ok (durable)
    end

    CA->>CA: CommitAll("orders", [msg@5, msg@6, msg@7])
    Note over CA: max offset in batch = 7\n_committedOffsets[("orders",2)] = 7 + 1 = 8

    Note over CA,PL: crash BEFORE commit  →  re-process batch on restart (at-least-once)\ncrash AFTER  commit  →  batch skipped on restart  ✓\nconsumer must be idempotent to handle the rare duplicate
```

---

## ③ Consumer group rebalance — new consumer joins

```mermaid
sequenceDiagram
    participant APP as Application
    participant CG  as ConsumerGroup "billing"
    participant CA  as Consumer A
    participant CB  as Consumer B
    participant CC  as Consumer C  (joining)

    APP->>CG: Subscribe("orders")
    Note over CG: roster: [A, B]  ·  4 partitions
    CG->>CG: Rebalance()
    Note over CG: p0 → A    p1 → B\np2 → A    p3 → B

    APP->>CG: AddConsumer("consumer-C")
    CG->>CC: new Consumer("consumer-C", broker)
    Note over CG: roster: [A, B, C]  ·  4 partitions
    CG->>CG: Rebalance()
    Note over CG: p=0 → c[0%3]=A    p=1 → c[1%3]=B\np=2 → c[2%3]=C    p=3 → c[3%3]=A

    CG-->>CA: assigned [p0, p3]
    CG-->>CB: assigned [p1]   ← lost p3 to A
    CG-->>CC: assigned [p2]   ← starts polling from committedOffset(p2) = 0

    Note over CC: new consumer starts at offset 0 (beginning of log)\nif topic had 10K messages, C replays all of them before catching up\nin production: "auto.offset.reset=latest" skips history for new groups
```

---

## ④ Log compaction — `profiles` topic, compacted

```mermaid
sequenceDiagram
    participant PR  as Producer
    participant PL  as PartitionLog[0]  ("profiles")
    participant LC  as LogCompactor
    participant CON as Consumer (new — replays from offset 0)

    PR->>PL: Append(key="user:Alice", value="v1")  → offset 0
    PR->>PL: Append(key="user:Bob",   value="v1")  → offset 1
    PR->>PL: Append(key="user:Alice", value="v2")  → offset 2
    PR->>PL: Append(key="user:Bob",   value=null)  → offset 3  (tombstone — delete Bob)

    Note over LC: compaction triggered (background job or manual call)
    LC->>PL: ReadFrom(0, int.MaxValue)
    PL-->>LC: [msg@0(Alice,v1), msg@1(Bob,v1), msg@2(Alice,v2), msg@3(Bob,null)]

    LC->>LC: Pass 1 — iterate in offset order, last write wins per key
    Note over LC: latest["user:Alice"] = msg@0  →  overwritten by msg@2\nlatest["user:Bob"]   = msg@1  →  overwritten by msg@3 (tombstone)

    LC->>LC: Pass 2 — strip tombstones (Value == null)
    Note over LC: msg@3.Value == null  →  drop\nresult = [msg@2]  (ordered by original offset)

    LC-->>CON: compacted log = [msg@2 (Alice=v2)]
    Note over CON: replaying from offset 0:\n  Alice = "v2"  ✓  (latest value)\n  Bob   = absent  ✓  (tombstone erased it)\nfull current state reconstructed from one pass
```

---

## Why each component exists

| Component | Role | Maps to in code |
|-----------|------|-----------------|
| **Producer** | Routes messages to the correct partition (hash or round-robin); returns a receipt | `Producer` |
| **Broker** | Single name registry; throws on unknown topic so a missing `CreateTopic` surfaces immediately | `Broker` |
| **Topic** | Groups partitions under a name; owns the key→partition mapping via MD5 | `Topic` |
| **PartitionLog** | Append-only tape; `offset = _log.Count` before append — offsets are unique, sequential, and lock-free to read | `PartitionLog` |
| **Message.Key** | Routing address; same key → same partition → processing order guaranteed end-to-end | `Message.Key` |
| **Message.Value = null** | Tombstone deletion signal for compacted topics; tells compactor and consumers to remove the key | `Message.Value` |
| **Message.Offset** | Assigned by `PartitionLog` under lock — `(topic, partition, offset)` is globally unique forever | `PartitionLog.Append` |
| **Message.Headers** | Out-of-band metadata (schema version, correlation ID, source service) without touching the payload | `Message.Headers` |
| **Consumer** | Bookmark-based reader; committed offset = next offset to read; isolated per `(topic, partition)` pair | `Consumer` |
| **ConsumerGroup** | Round-robin partition assignment; one partition → one consumer enforces ordering; idle consumers are hot standbys | `ConsumerGroup` |
| **Rebalance** | Rebuilds the assignment map from scratch on every roster change — no stale assignments possible | `ConsumerGroup.Rebalance` |
| **LogCompactor** | Reduces a compacted partition to one message per key; tombstones cancel earlier values before being removed | `LogCompactor` |
| **MD5 for routing** | Stable across .NET versions, machines, and processes; `GetHashCode()` is not guaranteed deterministic | `Topic.GetPartitionIndex` |
| **`Interlocked.Increment`** | Lock-free atomic round-robin counter; no monitor contention on null-key writes | `Producer._roundRobinIndex` |
| **`Math.Abs` on MD5 bytes** | `BitConverter.ToInt32` can produce negative values; `Math.Abs` keeps the partition index valid without branching | `Topic.GetPartitionIndex` |

---

## Key HLD design decisions

- **Append-only log instead of in-place updates (write performance + replay).** Random
  writes on disk require seeks — capped at ~1 K IOPS on spinning disk, ~50 K on NVMe.
  Sequential appends saturate disk bandwidth (hundreds of MB/s). More importantly,
  immutability enables arbitrary replay: any consumer can seek to offset 0 and re-read
  the entire history without affecting any other consumer. In-place updates destroy the
  history that makes event sourcing, auditing, and consumer catch-up possible.

- **`offset = _log.Count` before append (no separate counter).** A `List<T>` in C# always
  has `Count == number of items appended`. Assigning `msg.Offset = _log.Count` *before*
  `_log.Add(msg)` gives 0-based offsets that exactly match list indices — no off-by-one,
  no extra counter to keep in sync, and `_log[offset]` is always valid. The lock that
  guards this assignment also prevents two concurrent appends from claiming the same slot.

- **Fixed partition count at topic creation (key→partition stability).** `hash(key) % N`
  produces a different result when N changes. If partition count could grow, "user:Alice"
  might route to partition 2 today and partition 5 tomorrow — messages written before and
  after the resize would end up in different logs, and a consumer processing partition 2
  would never see the post-resize events. Fixing the count at creation time means the
  key→partition mapping is an invariant that every producer, consumer, and test can rely on.

- **MD5 instead of `GetHashCode` (cross-process determinism).** The .NET specification
  explicitly does not guarantee that `GetHashCode()` returns the same value across
  different processes, app domains, or .NET versions. Two broker replicas hashing the same
  key could independently route it to different partitions — silently breaking ordering.
  MD5 is a fixed algorithm that produces the same bytes everywhere. Any other stable
  hash (SHA-256, xxHash) would work equally well; MD5 is fast and available without
  external dependencies.

- **Commit `offset + 1` (at-least-once delivery).** The committed offset means "the next
  message I want to read." Committing the offset that was just processed (N) would
  re-fetch N on restart — a duplicate. Committing N+1 skips N on restart — a loss. N+1
  is the correct contract: "I am done with N; give me N+1 next time." The cost is that
  a crash *before* the commit causes the batch to be re-processed. Consumers must be
  idempotent to handle the rare duplicate — a much easier constraint than recovering lost
  events.

- **One partition → one consumer (ordering guarantee).** If two consumers both read
  partition 3, they would race to process its messages: consumer A handles offset 10,
  consumer B handles offset 11, but A finishes last — downstream state is updated in the
  wrong order. Exclusive partition ownership means there is exactly one thread processing
  each partition at any time, so the append-order guarantee extends all the way to the
  side effect. The consequence is that partition count is the hard ceiling on consumer
  parallelism within a group.

- **Idle consumers as hot standbys (fast failover).** With N consumers and M partitions
  where N > M, the extra consumers own no partitions but remain in the group. When a peer
  crashes, the next `Rebalance()` immediately assigns the orphaned partitions to idle
  consumers — recovery time is one rebalance cycle (~seconds in Kafka, one method call
  here). This is more resilient than spinning up a new consumer on demand, which requires
  process startup and topic subscription before any messages can be processed.

- **Log compaction instead of TTL or infinite retention (state topics).** TTL-based expiry
  deletes data after a fixed age — a rarely-updated key (e.g. a user who hasn't logged in
  for 90 days) vanishes even though it represents valid current state. Infinite retention
  lets disk grow forever at O(all writes). Log compaction keeps disk proportional to
  O(distinct keys): each key's latest value always survives until an explicit delete
  (tombstone), so a new consumer can reconstruct the full current state of the world by
  replaying from offset 0, regardless of when it started.

---

## Consistency and delivery guarantees

```
Delivery semantics (tunable per producer):

  at-most-once   →  produce and forget; no retry on timeout
                    → possible loss on network failure; zero duplicates
                    → use for: metrics, low-value telemetry

  at-least-once  →  retry on timeout; commit offset AFTER processing  ← THIS DESIGN
                    → possible duplicate on crash-before-commit; zero loss
                    → use for: orders, payments (with idempotent consumers)

  exactly-once   →  producer ID + sequence number; broker deduplicates retries
                    → no loss, no duplicates; extra latency (~2×)
                    → use for: financial transfers, exactly-once aggregations

Ordering guarantees:

  Within one partition  →  strict append order; one consumer; fully ordered ✓
  Across partitions     →  no ordering guarantee; different consumers, different speeds
  Global across topics  →  no ordering guarantee; design around it with event correlation IDs

Partition count trade-offs:

  More partitions  →  higher parallelism ceiling (more consumers possible)
                   →  more open file handles on broker
                   →  longer rebalance time (more work per Rebalance call)

  Fewer partitions →  simpler; faster rebalance
                   →  hard parallelism ceiling — can't add consumers beyond partition count
                   →  recommendation: choose a count with many divisors (12, 24, 48)
                      so you can run 1, 2, 3, 4, 6, or 12 consumers with no idle partitions
```

---

## Capacity sketch

| Metric | Estimate |
|--------|----------|
| Write throughput (per partition) | ~500 K msgs/sec (RAM-bound, lock contention is per-partition not per-topic) |
| Read throughput (per consumer) | ~1 M msgs/sec (sequential list slice, no disk I/O in this demo) |
| Partition log size | Unbounded (no retention in demo); production: TTL-based segment deletion or compaction |
| Consumer lag | `latestOffset − committedOffset`; alert when lag grows monotonically over time |
| Rebalance time | O(partitionCount); ~microseconds in demo; ~seconds in real Kafka (network round-trips) |
| Maximum consumer parallelism | = partitionCount; extra consumers sit idle as hot standbys |
| Compaction savings | Depends on update frequency; a topic with 1 M writes but 10 K distinct keys compacts 100× |
| Ordering guarantee scope | Strict within one partition; none across partitions or topics |
| Duplicate window | One batch (between last process and last commit); at-most `maxMessages` messages |