# Distributed Message Queue — Beginner Summary

## What is this project?

A **Distributed Message Queue** (like Apache Kafka) is a system where **producers** write messages and **consumers** read them — independently and at their own pace. Think of it as a conveyor belt in a factory: the belt keeps moving, workers (consumers) pick items off at whatever speed they can manage, and if a worker is slow, items just queue up on the belt until that worker catches up.

The "distributed" part means the belt is split into parallel lanes (**partitions**) across multiple machines. Different consumers process different lanes simultaneously — parallelism without coordination.

---

## The Big Challenges

1. **Ordering — how do you guarantee Alice's events are processed in sequence?** If "order created" and "order delivered" for user Alice land on different servers, a consumer might process "delivered" before "created".
2. **Parallelism without duplicates — how do multiple consumers share the work?** If two consumers both read from the same stream, they'd process the same message twice.
3. **Falling behind — what happens when consumers can't keep up with producers?** You need to replay from where you left off without losing messages.
4. **Infinite storage — logs grow forever.** For changelog-style data (user profile updates), you only care about the *current* state, not all 1000 historical versions.

Every file in this project solves one of these problems.

---

## The Files — What Each One Does

### `Models/Message.cs` — The Unit of Data

Every message travelling through the queue is a `Message`:

| Field | Example | Meaning |
|---|---|---|
| `Key` | `"user:Alice"` | Who this message belongs to — drives which partition it lands on |
| `Value` | `"order:101 created"` | The actual payload |
| `Offset` | `42` | Position in the partition log — assigned by the log, not the producer |
| `Partition` | `1` | Which lane of the conveyor belt this message sits on |
| `Timestamp` | `2026-05-31 10:00` | Wall-clock time of production |
| `Headers` | `{ "source": "checkout" }` | Optional metadata — routing, tracing, schema version |

**Why the log assigns the offset (not the producer):** If producers could set their own offsets, two producers writing simultaneously could claim the same offset — corrupting the ordering guarantee. The `PartitionLog` assigns `offset = current log length` under a lock, so offsets are always unique and strictly increasing.

---

### `Models/ProduceResult.cs` — The Write Receipt

After a successful write, the broker returns a `ProduceResult`:

```
ProduceResult { Topic="orders", Partition=1, Offset=42 }
```

This `(topic, partition, offset)` triple is the **globally unique address** of the message. It's like a tracking number for a parcel.

**Why this matters:** If the network drops and the producer doesn't receive a result, it can safely retry the write. A retry with the same content landing at a new offset is fine — the consumer will read both. The key insight is: **no result received ≠ message not written**. Producers should retry on timeout; they should not assume the write failed.

---

### `Core/PartitionLog.cs` — The Append-Only Ordered Log

This is the core storage unit — one `PartitionLog` per partition per topic. It's a list that only ever grows.

**Why append-only?** Sequential writes (append to the end of a file) are the fastest possible disk operation — 10-100× faster than random writes (updating a record in the middle of a file). Kafka's on-disk segments are just files you append to and never rewrite. This is why Kafka can sustain millions of writes per second on ordinary hardware.

**Reading with `ReadFrom(offset, maxCount)`:**

```
Log:  [msg0] [msg1] [msg2] [msg3] [msg4] [msg5]

ReadFrom(offset=2, maxCount=3) → [msg2, msg3, msg4]
```

The offset is simply the index. Reading from offset 0 replays the full history. Reading from offset 20 skips the first 20 messages. This is how a crashed consumer restarts: it remembers where it left off (the committed offset) and resumes from there — no data lost.

**`LatestOffset`** tells a consumer how far the producer has written. The gap between a consumer's committed offset and `LatestOffset` is the **lag** — how far behind the consumer is.

---

### `Core/Topic.cs` — The Named Stream

A `Topic` is the logical name for a stream (e.g. `"orders"`, `"events"`). It owns a fixed array of `PartitionLog` instances.

**The key → partition mapping:**

```
Produce("orders", key="user:Alice", value="order created")
   ↓
partition = MD5(key) % partitionCount
           = MD5("user:Alice") % 3
           = 1   ← always 1 for "user:Alice", on any machine, forever
   ↓
PartitionLog[1].Append(message)
```

**Why MD5 (not `GetHashCode`):** `GetHashCode` in .NET is randomised per process restart — different each time. MD5 gives the same number for the same string on every machine, every restart, every language. If the broker restarts, "user:Alice" must still map to partition 1. If a Go client and a C# client both produce Alice's events, they must both route to partition 1. MD5 (used just for its stability, not cryptographic security) guarantees this.

**Why fixed partition count:** Changing the number of partitions would remap keys to different partitions — "user:Alice" might jump from partition 1 to partition 0 — breaking the ordering guarantee. Choose a partition count that allows future parallelism: 12 is popular because it divides evenly by 1, 2, 3, 4, 6, or 12 consumers with no idle partitions.

**The ordering guarantee derived:**

```
All "user:Alice" events → always partition 1
Partition 1 → owned by exactly one consumer at a time (enforced by ConsumerGroup)
Therefore: Alice's events are always processed in the order they were written. ✓
```

---

### `Core/Broker.cs` — The Topic Registry

The `Broker` is the admin interface — it knows which topics exist. Creating a topic is a separate "admin-plane" operation from reading and writing ("data-plane"). This separation means:
- A new consumer can read a topic without knowing how it was created
- Changing partition count is a deliberate, controlled operation — not something that accidentally happens during a normal write

In real Kafka, a cluster has dozens of broker nodes. Each broker hosts a subset of **partition leaders** (which accept writes) and **partition followers** (which replicate for fault tolerance). Here, one in-process `Broker` holds everything to keep the demo simple.

---

### `Core/LogCompactor.cs` — Shrinking the Log

By default, a topic keeps every message forever. For **changelog-style topics** (e.g. user profile updates), that means:

```
Raw log (9 messages):
  offset 0: user:1 → {name:Alice, age:30}
  offset 1: user:2 → {name:Bob, age:25}
  offset 3: user:1 → {name:Alice, age:31}   ← overwrite
  offset 6: user:1 → {name:Alice, age:32}   ← overwrite again
  offset 7: user:3 → (tombstone)             ← DELETE
  offset 8: user:2 → {name:Robert, age:26}  ← overwrite
```

Compaction keeps only the latest message per key and drops tombstones:

```
Compacted log (3 messages):
  user:1 → {name:Alice, age:32}   ← only the newest
  user:2 → {name:Robert, age:26}  ← only the newest
  user:4 → {name:Dave, age:28}    ← untouched
  (user:3 deleted — tombstone removed entirely)
```

**The tombstone trick:** Writing `value=null` signals "this key is deleted". During compaction, any key whose latest entry is a tombstone is dropped entirely. A new consumer starting from the beginning will never see the deleted entity — correct semantics for a deleted user.

**The win:** Storage shrinks from O(all writes ever) to O(number of distinct keys currently alive). For a user profile topic with 10 million users who each update their profile 100 times, storage drops from 1 billion messages to 10 million.

---

### `Service/Producer.cs` — Writing Messages

The `Producer` decides which partition each message goes to:

```
key != null → partition = hash(key) % N   ← same key, same partition, always
key == null → partition = round-robin     ← spread load evenly
```

```
Produce("orders", "user:Alice", "order created")  → always P1
Produce("orders", "user:Alice", "order shipped")  → always P1   ← ordering preserved
Produce("orders", null,         "admin event")    → P0, P1, P2, P0... ← spread
```

**Why `Interlocked.Increment` for round-robin:** The `Producer` may be shared across multiple threads. Without synchronisation, two threads could read the same `_roundRobinIndex` and both route to the same partition — defeating the load-spreading purpose. `Interlocked.Increment` does the read-and-increment atomically with no lock overhead.

---

### `Service/Consumer.cs` — Reading Messages

A `Consumer` tracks one **committed offset** per (topic, partition) pair. The committed offset is the position it will read from next.

**The offset+1 convention:**

```
Consumer reads messages at offsets 0, 1, 2, 3
Consumer calls Commit("orders", partition=1, offset=3)
   → stores committedOffset = 3 + 1 = 4   ← NEXT message to read
Consumer restarts
   → polls from offset 4 (not 3) → no re-delivery ✓
```

Storing the *last consumed* offset would re-deliver the last message on every restart. Storing `offset + 1` (the *next* offset to read) means restart = continue seamlessly.

**Lag — the health metric:**

```
latestOffset = 20   (producer is here)
committedOffset = 15 (consumer is here)
lag = 20 - 15 = 5   (5 messages behind)
```

A growing lag means the consumer can't keep up with the producer. Alert on lag — don't wait until the consumer crashes. In practice, Kafka monitors alert when lag exceeds a threshold (e.g. "alert if lag > 10 000 messages").

---

### `Service/ConsumerGroup.cs` — Sharing Work Across Consumers

A `ConsumerGroup` coordinates multiple consumers so they **divide up the partitions** without any overlap.

**The cardinal rule: one partition → at most one consumer in the group.**

Without this rule, consumers-1 and consumer-2 could both read partition 1 — processing the same messages twice. Duplicate order fulfilments, duplicate payments. The group enforces exclusive ownership.

**Partition assignment (round-robin):**

```
4 partitions, 2 consumers:
  P0 → consumer-1   (0 % 2 = 0)
  P1 → consumer-2   (1 % 2 = 1)
  P2 → consumer-1   (2 % 2 = 0)
  P3 → consumer-2   (3 % 2 = 1)

4 partitions, 4 consumers (perfect 1:1):
  P0 → worker-1
  P1 → worker-2
  P2 → worker-3
  P3 → worker-4

4 partitions, 5 consumers (worker-5 is idle):
  P0 → worker-1  ...  P3 → worker-4
  worker-5 → no partitions (standby for failover)
```

**Rebalance:** Any time a consumer joins or leaves, the partition→consumer map is recomputed from scratch. During a rebalance, all consumers in the group briefly stop processing (the "stop-the-world rebalance" — Kafka's biggest operational pain point in production). After rebalance, each consumer picks up from its committed offsets, so no messages are lost or skipped.

**Idle consumers** (more consumers than partitions) are not wasted — they become hot standbys. If `worker-2` crashes, the next rebalance reassigns its partitions to active consumers including `worker-5`.

---

### `Program.cs` — The Demo

Runs 6 scenarios:

| Scenario | What it tests |
|---|---|
| 1 | Basic pub/sub — produce, poll, commit offset, poll again from committed position |
| 2 | Key-based partitioning — same user key always routes to same partition, ordering verified |
| 3 | Consumer groups — two independent groups (analytics, billing) each get all messages |
| 4 | Consumer lag — producer writes 20 messages; consumer catches up in batches of 5 |
| 5 | Rebalance — add workers 3, 4, 5 to a group; partition map redrawn each time |
| 6 | Log compaction — 9 raw messages compact to 3; tombstone removes user:3 entirely |

---

## The Big Picture — How It All Fits Together

```
PRODUCE:

Producer.Produce("orders", key="user:Alice", value="order created")
      ↓
Topic.GetPartitionIndex("user:Alice")
  → MD5("user:Alice") % 3 = partition 1   (stable, deterministic)
      ↓
PartitionLog[1].Append(message)
  → offset = 7 (current log length)
  → ProduceResult { topic="orders", partition=1, offset=7 }

CONSUME (ConsumerGroup):

ConsumerGroup("payment-processor")
  → 4 partitions, 4 workers → P0→w1, P1→w2, P2→w3, P3→w4
      ↓ each worker independently:
  Consumer.Poll("orders", partition=P_mine, maxMessages=5)
    → PartitionLog[P].ReadFrom(committedOffset)
    → returns next batch of messages
  Process messages...
  Consumer.CommitAll(messages)
    → committedOffset = lastOffset + 1
    → crash here and restart → picks up from committedOffset, no re-delivery

REBALANCE (worker-5 joins):

ConsumerGroup.AddConsumer("worker-5")
  → Rebalance(): P0→w1, P1→w2, P2→w3, P3→w4, w5=(idle standby)
  All consumers resume from their committed offsets — no messages lost

LOG COMPACTION (nightly, for compacted topics):

LogCompactor.Compact(partitionLog.ReadFrom(0))
  → keep only latest message per key
  → drop tombstones (null values)
  → write compacted segment back to disk
  → 9 messages → 3 messages, 66% space saved
```

---

## Why This Design Is Used Everywhere

- **Append-only log** is why Kafka can handle 1 million writes/second — sequential I/O is as fast as storage gets
- **Offset-based consumption** is why consumers can freely replay history — rewind to offset 0 to reprocess all data, or seek to "1 hour ago" for debugging
- **Consumer groups** are the foundation of scalable stream processing — Kafka Streams, Apache Flink, and Spark Structured Streaming all build on top of this exact group model
- **Key-based partitioning** is how Kafka guarantees exactly the ordering developers need — not global order (expensive), but per-key order (free, since it's just routing)
- **Log compaction** is what makes Kafka usable as a database — the compacted topic is a complete, up-to-date snapshot of every key's current state, readable by new consumers without replaying history
- **Independent consumer groups** is why one Kafka topic can feed an analytics pipeline, a billing system, and a fraud detector simultaneously — each reads at its own pace with its own offsets, completely decoupled
