# Distributed Key-Value Store — Beginner Summary

## What is this project?

A **Distributed Key-Value Store** is a database that works like a giant dictionary: you store a value under a key (`Put("user:1", "Alice")`), and you can retrieve it later (`Get("user:1")`). The "distributed" part means the data is spread across multiple servers — like DynamoDB, Redis Cluster, or Apache Cassandra.

---

## The Big Challenges

1. **Where do you store each key?** With 10 servers, which server holds `user:1`?
2. **What if a server crashes?** You can't lose data when a machine goes down.
3. **How do you write fast?** Disk writes are slow — how do you handle millions per second?
4. **What if replicas get out of sync?** Two servers might have different values for the same key.

Every file in this project solves one of these problems.

---

## The Files — What Each One Does

### `Core/ConsistentHashRing.cs` — Which Server Gets Which Key?

**The problem:** You have 3 servers (A, B, C) and millions of keys. How do you decide which server stores which key — and how do you add a 4th server without moving all the data?

**Naive approach (bad):** `serverIndex = hash(key) % 3`. Works fine with 3 servers. Add a 4th server → `% 4` now gives different results for almost every key → you have to move ~75% of all data. Catastrophic.

**Consistent hashing (this file):** Imagine a clock face numbered 0 to 4 billion. Every server gets placed at several positions on the clock (called **virtual nodes**). Every key also gets a position. A key's server is the first server you encounter going clockwise from the key's position.

```
Clock:   0 ────── A ─── B ──── C ───── A ─── B ── C → 4B
Key "user:1" lands at position 1500 → next clockwise is B → stored on B
```

**Adding a 4th server D:** D takes positions on the clock. Only the keys between D's new positions and the previous server's positions move to D — roughly 25% of data, not 75%.

**Virtual nodes (150 per server):** Each server gets 150 positions on the clock instead of 1. This spreads the load evenly — without virtual nodes, one server might end up with a huge arc and get 80% of the traffic.

`GetNodes("user:1", 3)` returns **3 servers** — for replication. The same key is stored on 3 different machines so if one crashes, the data still exists on 2 others.

---

### `Models/StorageEntry.cs` — One Stored Value

Every stored value is wrapped in a `StorageEntry`:

| Field | Example | Meaning |
|---|---|---|
| `Value` | `"Alice"` | The actual data |
| `Timestamp` | `42` | When it was written (logical clock tick) |
| `ExpiresAt` | `2026-06-01 12:00` | Optional TTL — auto-expires after this time |
| `IsTombstone` | `false` | Is this a deletion marker? |

**Tombstone** is the clever part. When you delete `user:1`, the system doesn't erase anything — it writes a special `TombstoneEntry` that says "this key was deleted." This is needed because:
- The data might already be flushed to an immutable file on disk
- Other servers might have copies — you need the deletion to propagate
- A tombstone travelling through the system overwrites any older value it encounters

---

### `Storage/MemTable.cs` — The Fast Write Buffer

**The problem:** Disk writes are 1000× slower than RAM writes. If you write every key directly to disk, you can only handle ~1,000 writes/second. Real databases need millions.

**Solution:** Write to RAM first (the MemTable), then flush to disk in one big batch.

The MemTable is a `SortedDictionary` in RAM. Every write goes here first — extremely fast. When it reaches a size threshold (1 KB in the demo, 64 MB in production), it's flushed to disk as an immutable SSTable file.

**Why sorted?** When flushing to disk, keys must be written in alphabetical order so the SSTable file can be searched quickly (binary search). If you stored them unsorted and sorted at flush time, that's O(N log N) extra work. A `SortedDictionary` stays sorted automatically — flush is just a sequential write.

---

### `Storage/SSTable.cs` — The Immutable Disk File

When the MemTable fills up, it's frozen into an **SSTable** (Sorted String Table) — a sorted, immutable file that never changes.

**Why immutable?** No locks needed for concurrent reads — readers never race with writers because the file never changes. This is the key performance win. Multiple threads can read the same SSTable simultaneously without any coordination.

**The problem with many SSTables:** If you have 50 SSTable files and search for a key, you might need to check all 50 files before finding it (or concluding it doesn't exist). That's 50 disk reads.

**Solution: Bloom filter per SSTable.** Each SSTable has its own Bloom filter. Before opening and scanning an SSTable, the system asks the Bloom filter: "Does this key definitely not exist here?" If the answer is yes, skip this file entirely. In practice, most SSTable checks become instant skips.

---

### `Core/BloomFilter.cs` — Skip Unnecessary File Reads

Same concept as the Web Crawler's Bloom filter, but used here specifically to avoid reading SSTable files that don't contain a key.

The key difference from the Web Crawler version: here it uses **7 hash functions** (vs 3) because false positives cause expensive disk I/O (not just a missed web page). Fewer false positives = fewer unnecessary disk reads = faster reads.

---

### `Service/KvNode.cs` — One Server's Storage Engine

This represents a single server (Node A, Node B, etc.). It wires together MemTable + SSTables into a complete **LSM Tree** (Log-Structured Merge Tree).

**Write path:**
```
Put("user:1", "Alice")
    ↓
MemTable.Put()      ← fast, in RAM
    ↓ (when MemTable hits size limit)
Flush → new SSTable ← sorted, immutable, on disk
```

**Read path (newest-first):**
```
Get("user:1")
    ↓
1. Check MemTable      ← most recent writes are here
    ↓ not found
2. Check newest SSTable → Bloom filter says skip? → skip
3. Check next SSTable  → Bloom filter says maybe? → scan
4. Check next SSTable...
    ↓ first hit wins
Return value (or tombstone = "deleted")
```

The read always checks newest first because the most recent write wins. If `user:1` was written to the MemTable after an older value was flushed to an SSTable, the MemTable version is correct.

**Logical clock:** Every write gets an incrementing timestamp (1, 2, 3...). This isn't wall-clock time — it's a counter. It's used to determine which write is newer when replicas disagree.

---

### `Service/DistributedKvStore.cs` — The Coordinator Across Servers

This coordinates multiple `KvNode` instances. It uses **quorum reads and writes** to handle server failures without losing data.

**Quorum explained with a simple example:**

You have 3 servers (N=3). You set:
- Write quorum W=2: a write is successful when 2 out of 3 servers confirm it
- Read quorum R=2: a read contacts 2 out of 3 servers

The magic rule: **W + R > N** (2 + 2 > 3). This guarantees at least one server that received the write is always included in every read — so you always get the latest value.

```
Write "user:1 = Alice" → written to NodeA and NodeB (2/3 = quorum met ✓)
NodeC is slightly behind (not written yet)

Read "user:1" → asks NodeB and NodeC
  NodeB says: Alice (timestamp=5)
  NodeC says: (not found, timestamp=0)
  Highest timestamp wins → return Alice ✓
```

**Read repair:** After the read, the system silently updates NodeC with "Alice" — so next time, all 3 nodes agree. This happens automatically without a separate process.

**Node failure:** If NodeA goes down, writes still succeed if 2 of the remaining nodes (B and C) confirm. Reads still work from B and C. The system tolerates losing **any 1 node** with N=3, W=2, R=2.

---

### `Program.cs` — The Demo

Runs 5 scenarios:

| Scenario | What it tests |
|---|---|
| 1 | Consistent hashing — which server gets which key, load distribution, adding a 4th node |
| 2 | Single-node LSM — write, read, overwrite, delete, MemTable flush to SSTable |
| 3 | Quorum write + read repair — distributed write, stale replica, read repair |
| 4 | TTL expiry — rate-limit key expires after 1 second, permanent key stays |
| 5 | Node failure + recovery — take a node down, writes still succeed, node comes back |

---

## The Big Picture — How It All Fits Together

```
Put("user:1", "Alice")
      ↓
DistributedKvStore
  → ConsistentHashRing.GetNodes("user:1", RF=3)
      → [NodeA, NodeB, NodeC]
  → Write to NodeA, NodeB (W=2 quorum met)
        ↓ each node runs:
      KvNode.Put()
        → MemTable.Put()   ← fast RAM write
        → MemTable full? → flush to SSTable
              ↓ SSTable built with Bloom filter

Get("user:1")
      ↓
DistributedKvStore
  → Ask NodeA, NodeB (R=2)
  → Both return Alice, timestamps match
  → Return "Alice"
  → Read repair: update any stale replica silently

(NodeA crashes)
  → Write "user:1 = Bob" → NodeB + NodeC (still W=2 quorum ✓)
  → Read "user:1" → NodeB + NodeC return "Bob" ✓
  → System keeps working with 2 of 3 nodes
```

## Why This Design Is Used Everywhere

- **MemTable + SSTable (LSM Tree)** is how LevelDB, RocksDB, Cassandra, and HBase store data — it turns random writes into sequential disk writes, which are 10× faster
- **Consistent hashing** is used by DynamoDB, Cassandra, and Riak — adding/removing nodes moves minimal data
- **Quorum reads/writes** is the foundation of Cassandra's consistency model — you can tune W and R based on whether you need speed or consistency
- **Bloom filters per SSTable** is used in every real LSM implementation — without them, reads touching many SSTables would be impractically slow
