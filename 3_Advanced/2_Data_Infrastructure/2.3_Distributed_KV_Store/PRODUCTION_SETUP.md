# Distributed KV Store — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system.

---

## `Core/BloomFilter.cs` — Custom in-memory Bloom filter

**Problem in production:** Three issues:
1. Hand-rolled implementation — untested edge cases, no performance tuning
2. Lives only in RAM — if the process restarts, every SSTable loses its Bloom filter and must be rebuilt
3. A basic bit array is not CPU cache-friendly — modern CPUs fetch memory in 64-byte cache lines, and random bit accesses thrash the cache

**Production replacement: Blocked Bloom filter, serialized to disk alongside SSTable**

Real LSM databases (RocksDB, LevelDB, Cassandra) embed the Bloom filter in the SSTable file itself:

```
SSTable file layout:
  ┌─────────────────────┐
  │  Data blocks        │  ← compressed key-value pairs (4KB blocks)
  │  Index block        │  ← one entry per data block for binary search
  │  Bloom filter block │  ← loaded into RAM on SSTable open
  │  Footer             │  ← offsets to each section
  └─────────────────────┘
```

**Blocked Bloom filter** — splits the filter into 64-byte chunks aligned to CPU cache lines. All k bit positions for a single key fall within one chunk → one cache line load per lookup instead of k random cache misses. 2–5× faster than a flat bit array.

**Bits-per-key tuning:** Production systems configure bits-per-key (default 10 in RocksDB) based on the false positive rate target:
```
bits_per_key = 10  → ~1% false positive rate
bits_per_key = 14  → ~0.1% false positive rate  (more RAM, fewer disk reads)
bits_per_key =  6  → ~4% false positive rate  (less RAM, more disk reads)
```
The right value depends on your read/write ratio and available RAM.

---

## `Core/ConsistentHashRing.cs` — Single-process hash ring

**Problem in production:** The ring lives inside one process. In a real cluster, 50 servers each need to independently know the ring state — and when a node joins or leaves, all 50 must update simultaneously and agree on the new ring.

**Production replacement: Distributed ring state via ZooKeeper or etcd**

```
Node joins cluster
    ↓
Registers itself in ZooKeeper / etcd
    ↓
All other nodes receive a watch notification
    ↓
Each node updates its local ring view
    ↓
Cluster converges to new ring within ~100ms
```

**Dynamic virtual node count:** In production, nodes have different hardware. A node with 64GB RAM gets more virtual nodes than one with 16GB — it receives proportionally more keys. The ring is capacity-weighted, not uniform.

**Alternative: Jump Consistent Hash (Google, 2014)**
```csharp
long JumpHash(ulong key, int numBuckets)
{
    long b = -1, j = 0;
    while (j < numBuckets) { b = j; key = key * 2862933555777941757 + 1; j = (long)((b + 1) * (1L << 31) / ((double)(key >> 33) + 1)); }
    return b;
}
```
- No virtual nodes needed — mathematically balanced by design
- O(ln N) lookup vs O(log N) for the sorted dictionary ring
- Adding a server only moves 1/N of keys — same property as consistent hashing, simpler math
- Used in Google's Spanner and several internal systems

---

## `Models/StorageEntry.cs` — Plain C# class

**Problem in production:** C# objects cannot be written directly to disk or sent over the network. Serializing them to JSON is too slow and too large for a hot write path.

**Production replacement: Protocol Buffers (Protobuf) binary encoding**

```protobuf
message StorageEntry {
    string value      = 1;
    int64  timestamp  = 2;
    int64  expires_at = 3;  // Unix ms, 0 = no expiry
    bool   tombstone  = 4;
}
```

- Protobuf: 3–10× smaller than JSON, 5–10× faster to serialize
- Language-neutral: C#, Java, Go, Rust nodes all read the same binary format
- Versioned schema: adding a new field (e.g., field 5) is backward compatible — old nodes ignore unknown fields

**Vector clocks instead of a single timestamp (for true distributed conflict resolution):**

A single logical clock works when one coordinator assigns timestamps. In a leaderless cluster (Dynamo-style), two clients can write to two different nodes simultaneously — both get the same "latest" timestamp. Vector clocks track *which node* made each write:

```
Node A writes user:1=Alice  → vc = {A:1, B:0, C:0}
Node B writes user:1=Bob    → vc = {A:0, B:1, C:0}  (concurrent — conflict!)

Compare: neither dominates the other → actual conflict
Resolution: last-write-wins (by wall clock), or expose conflict to client
```

DynamoDB and Cassandra both use this approach for conflict detection.

---

## `Storage/MemTable.cs` — In-memory SortedDictionary, lost on crash

**Problem in production:** Two critical issues:
1. If the process crashes while data is in the MemTable (before flush), all unwritten data is lost forever
2. The single `lock` in `KvNode` means only one write can happen at a time — a bottleneck under concurrent load

**Production replacement: Write-Ahead Log (WAL) + concurrent skip list**

**Write-Ahead Log — crash recovery:**
```
Every Put/Delete:
    1. Append to WAL file on disk  ← sequential disk write, very fast
    2. Write to MemTable in RAM

On crash + restart:
    1. Read WAL file
    2. Replay each entry into a fresh MemTable
    3. MemTable is fully recovered — no data lost
```
The WAL is append-only (sequential writes), so it's nearly as fast as writing to RAM. RocksDB, LevelDB, and Cassandra all use WAL for durability.

**Concurrent skip list — lock-free concurrent writes:**

A `SortedDictionary` requires a full lock on every write. A **concurrent skip list** allows multiple writers simultaneously using compare-and-swap CPU instructions:

```
Thread 1: Put("user:1", "Alice")  ─┐
Thread 2: Put("user:2", "Bob")    ─┤─ all run concurrently, no blocking
Thread 3: Put("user:3", "Carol")  ─┘
```

Java's `ConcurrentSkipListMap` is the canonical implementation. RocksDB's MemTable uses a lock-free skip list internally.

**Two MemTable pattern (active + immutable):**
```
Active MemTable   → accepts new writes
Immutable MemTable → being flushed to SSTable (read-only)
```
When the active MemTable fills up, it becomes immutable and a new active MemTable opens. Writes never pause waiting for a flush to complete.

---

## `Storage/SSTable.cs` — In-memory simulation, not actual disk files

**Problem in production:** This is the biggest gap — `SSTable` is simulated as an in-memory `SortedDictionary`. In reality, an SSTable must be a real file on disk. If the process restarts, all SSTables vanish and all data is gone.

**Production replacement: Real file-based SSTable with block compression + compaction**

**File format (RocksDB-style):**
```
level0_000001.sst:
  ┌──────────────────────┐
  │  Data block 1        │  4KB, Snappy-compressed key-value pairs
  │  Data block 2        │  4KB, Snappy-compressed
  │  ...                 │
  │  Index block         │  one entry per data block: (first_key, block_offset)
  │  Bloom filter block  │  loaded into RAM, kept in block cache
  │  Metaindex block     │  offsets to index + filter
  │  Footer (48 bytes)   │  magic number + metaindex offset
  └──────────────────────┘
```

**Block cache:** A shared LRU cache (default 8GB in production) holds recently accessed SSTable blocks in RAM. Frequently read keys are served from cache without touching disk at all — effectively the same as the MemTable approach but for cold data.

**Compaction — the critical background process missing from this project:**

L0 SSTables can have overlapping key ranges (multiple files may contain `user:1`). Every read must check all L0 files. As L0 grows, reads slow down.

Compaction merges SSTables and removes obsolete entries:

```
L0 (overlapping, newest-first):    [A-Z] [A-Z] [A-Z] [A-Z]   ← 4 files, slow reads
        ↓ compaction merges + sorts
L1 (non-overlapping, 10MB files):  [A-G] [H-N] [O-Z]         ← 3 files, one binary search
        ↓ compaction when L1 full
L2 (non-overlapping, 100MB files): [A-C] [D-F] ... [X-Z]     ← fast reads at any level
```

After compaction, finding any key requires checking at most **one file per level** — read amplification drops from O(L0 files) to O(levels) = O(7) in a typical setup.

**Compression:** LZ4 or Zstd compresses each 4KB block. Real-world text data compresses 3–5× — a 1TB dataset becomes 200–300GB on disk.

---

## `Service/KvNode.cs` — Single-threaded node, no WAL, no compaction

**Problem in production:** Three gaps:
1. One `lock` serializes all reads and writes — no concurrency
2. No WAL — MemTable data is lost on crash
3. No compaction — L0 SSTables grow forever, reads get slower over time

**Production replacement: RocksDB (or build your own with these components)**

RocksDB is an embeddable storage engine (C++ library, C# bindings available) that implements exactly this architecture with all gaps filled:

| This project | RocksDB equivalent |
|---|---|
| `MemTable` (SortedDictionary) | Concurrent skip list MemTable |
| Manual `lock` | Lock-free concurrent writes |
| No WAL | WAL with configurable sync mode |
| `List<SSTable>` | Leveled SSTable hierarchy (L0–L6) |
| No compaction | Background compaction thread pool |
| No block cache | Shared LRU block cache (configurable size) |
| Custom BloomFilter | Built-in blocked Bloom filter |

**Column families:** Production KV stores partition data into namespaces:
```
cf:users    → MemTable + SSTables for user data
cf:sessions → MemTable + SSTables for session data (shorter TTL, higher write rate)
cf:cache    → MemTable + SSTables for ephemeral data
```
Each column family has independent flush/compaction tuning — a high-write namespace can flush more aggressively without affecting a low-write namespace.

---

## `Service/DistributedKvStore.cs` — In-process coordinator, no real networking

**Problem in production:** Five critical gaps:
1. All nodes run in the same process — no actual network calls between machines
2. No cluster membership — nodes don't discover each other or detect failures automatically
3. Hinted handoff missing — writes to down nodes are silently dropped
4. No anti-entropy — replicas that drift apart are never reconciled
5. No sloppy quorum — during a network partition, writes fail instead of being rerouted

**Production replacement: gRPC + Gossip protocol + Merkle tree anti-entropy**

**gRPC for inter-node RPC:**
```protobuf
service KvNode {
    rpc Put(PutRequest)       returns (PutResponse);
    rpc Get(GetRequest)       returns (GetResponse);
    rpc Delete(DeleteRequest) returns (DeleteResponse);
    rpc Replicate(ReplicateRequest) returns (ReplicateResponse);
}
```
Each node runs a gRPC server. The coordinator calls `node.Put()` over the network, not in-process. Connections are persistent HTTP/2 streams — much lower overhead than REST.

**Gossip protocol (SWIM) for cluster membership:**
```
Every 1 second, each node randomly selects 3 peers
    → sends heartbeat: "I'm alive, here's my view of the cluster"
    → peers share back their view
    → within ~log(N) rounds, all nodes converge on who is up/down
```
No central coordinator needed — the cluster is self-healing. A node that stops sending heartbeats is marked suspect, then down, within ~10 seconds.

**Hinted handoff — no writes lost during failures:**
```
NodeA is down. Key "user:1" belongs to [NodeA, NodeB, NodeC].

Instead of skipping NodeA:
    Write to NodeB (primary replica)
    Write to NodeC (secondary replica)
    Write HINT to NodeD: "deliver {user:1=Alice} to NodeA when it recovers"

NodeA recovers:
    NodeD detects NodeA is up (via gossip)
    Replays hint: PUT user:1=Alice to NodeA
    NodeA is now fully caught up
```

**Merkle tree anti-entropy — reconcile replicas that drifted:**
```
NodeB and NodeC each build a Merkle tree of their data:
  Root hash = hash(all keys + values)

If root hashes match → replicas are identical, done
If root hashes differ → recurse into subtrees to find which key ranges differ
    → sync only the differing ranges, not the entire dataset
```
This detects and repairs replica drift without transferring all data. Cassandra calls this "repair" and runs it weekly by default.

**Sloppy quorum — availability during network partitions:**
```
Normal:     Write "user:1" → NodeA, NodeB, NodeC  (assigned replicas)

Partition:  NodeA and NodeB unreachable from coordinator
            → write to NodeC, NodeD, NodeE  (any 2 available nodes)
            → store with hint: "this belongs to NodeA and NodeB"
            → when partition heals, hand off to NodeA and NodeB
```
This is how DynamoDB achieves "always writable" — it never refuses a write due to node failures, trading strict consistency for availability.

---

## The Full Production Picture

```
Client PUT "user:1" = "Alice"
      ↓
Coordinator (gRPC server)
  → ConsistentHashRing.GetNodes("user:1", RF=3) = [NodeA, NodeB, NodeC]
  → gRPC: NodeA.Put(), NodeB.Put()  (W=2 quorum)
  → NodeC is down → store hint on NodeD
        ↓ each node:
      WAL append  ← crash-safe, sequential disk write
      Concurrent MemTable write (lock-free skip list)
      MemTable full? → flush to L0 SSTable (real file on disk)
      Background compaction: L0→L1→L2 (merge, sort, compress)

Client GET "user:1"
      ↓
  → gRPC: NodeA.Get(), NodeB.Get()  (R=2 quorum)
        ↓ each node:
      Check MemTable (RAM)
      Check L0 SSTables (Bloom filter → block cache → disk)
      Check L1, L2... (one file per level, binary search)
  → Compare timestamps → return latest
  → Read repair: update any stale replica

Background processes (always running):
  Gossip     → cluster membership, failure detection
  Compaction → merge SSTables, reclaim space
  Anti-entropy (Merkle tree) → reconcile replica drift
  Hint replay → deliver buffered writes to recovered nodes
  TTL cleanup → remove expired entries during compaction
```

The core logic (LSM tree, consistent hashing, quorum reads/writes, Bloom filters, tombstones) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with network I/O, disk persistence, and fault tolerance.
