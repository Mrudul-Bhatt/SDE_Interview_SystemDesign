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

## Cross-cutting concerns not modelled in this project

The sections above replace specific classes. The concerns below are global — they don't map to a single file but every production system must address them.

### 1. Observability — metrics, tracing, logging

**Problem:** With dozens of nodes serving millions of ops/sec, you cannot diagnose a slow read by reading logs. You need quantitative signals and request-level traces.

**Metrics (Prometheus / OpenTelemetry):**

```
# Per-node histograms
kvstore_read_latency_seconds{quantile="0.99"}    p99 read latency
kvstore_write_latency_seconds{quantile="0.99"}   p99 write latency
kvstore_memtable_bytes                            current MemTable size
kvstore_sstables_total{level="L0"}                count per level
kvstore_compaction_pending_bytes                  compaction backlog
kvstore_bloom_filter_false_positive_ratio         FP rate (target < 1%)
kvstore_cache_hit_ratio{cache="block"}            block cache hit ratio
kvstore_quorum_failures_total                     writes that missed W
kvstore_read_repair_total                         how often stale replicas seen
```

Dashboards alert when p99 latency > SLO, compaction backlog grows unboundedly, or cache hit ratio drops (signals working set exceeds RAM).

**Distributed tracing (Jaeger / Zipkin / Honeycomb):**

```
Trace ID: abc-123  (propagated across all RPCs)
  Span 1: Coordinator.Get(user:1)             8ms
    Span 2: NodeA.Get RPC                     3ms
      Span 3: MemTable lookup                 0.1ms
      Span 4: L0 SSTable check (Bloom miss)   0.05ms
      Span 5: L1 SSTable read (block cache hit) 0.2ms
    Span 6: NodeB.Get RPC                     4ms
      ...
```

A single request becomes a tree of spans across nodes. When a tail-latency alert fires, you find the exact slow span and the exact node responsible.

**Structured logging:**

JSON logs with consistent fields (`request_id`, `node_id`, `key_hash`, `latency_ms`) so they can be queried in Elasticsearch / Splunk / Datadog. Plain-text logs are unsearchable at scale.

---

### 2. Security — TLS, authentication, encryption at rest

**Problem in this project:** Nodes accept any incoming RPC with no auth. The disk format is plaintext. A stolen disk = stolen data.

**Inter-node TLS (mTLS):**

```
NodeA ↔ NodeB:
  Both present X.509 certs signed by the cluster CA.
  Both verify the peer cert before exchanging any data.
  All traffic encrypted via TLS 1.3.
```

Without mTLS, anyone who can reach the gRPC port can join the cluster, read everything, and corrupt state. Issue per-node certs via Vault or cert-manager (Kubernetes).

**Client authentication:**

```
Client → Coordinator
  Authorization: Bearer eyJhbGc...   ← short-lived JWT issued by IDP
  → coordinator validates signature + expiry
  → extracts tenant_id, role
  → enforces per-tenant key namespace and rate limits
```

**Authorization (per-key ACLs):**

In multi-tenant systems, key prefixes encode ownership:

```
tenant_a:user:42  → readable/writable only by tenant_a
tenant_b:user:42  → completely isolated, even though the same suffix
```

The coordinator rejects requests where the JWT tenant claim doesn't match the key's prefix.

**Encryption at rest:**

Each SSTable block is encrypted with AES-256-GCM before being written to disk. Keys come from a KMS (AWS KMS, Vault Transit). Lost disk → unreadable ciphertext. Required for SOC 2, HIPAA, PCI-DSS.

**Audit log:**

Every admin action (node add/remove, schema change, key deletion) is appended to a tamper-evident audit log (often a separate append-only WAL signed with HMAC). Forensic requirement for compliance.

---

### 3. Backup & disaster recovery

**Problem in this project:** No backup mechanism. A corrupted SSTable or a `DELETE` from a buggy client is permanent and unrecoverable.

**Snapshots — point-in-time copies:**

```
1. Trigger snapshot at time T
2. Each node hard-links all current SSTables into snapshots/snap_T/
   (hard links: zero extra disk space until compaction touches the original)
3. Hard-link the WAL position
4. Upload snapshot to S3 / GCS in the background
```

SSTables are immutable — hard-linking is the magic trick that makes snapshots cheap. Compaction creates NEW files; the snapshot still references the old ones.

**Point-in-time recovery (PITR):**

```
Restore = snapshot_T0 + replay WAL from T0 to target_time
```

The WAL is shipped to object storage in real time. To restore to "5 minutes ago", load the most recent snapshot and replay WAL entries up to that timestamp.

**Cross-region replication:**

```
Primary region (us-east-1)  → SSTable + WAL → S3 cross-region replication
                                                       ↓
                                              Standby region (us-west-2)
                                              (warm — can take over in minutes)
```

For active-active multi-region, see the geo-replication section below.

---

### 4. Multi-region / geo-replication

**Problem in this project:** All nodes assumed to be in one datacenter with sub-ms latency. Cross-region latency is 60–200ms — a synchronous quorum across regions would make every write painful.

**Replication strategy per consistency need:**

```
Region-local strong consistency (recommended default):
  RF=3 in us-east-1     (synchronous W=2, R=2)
  RF=3 in us-west-2     (synchronous W=2, R=2)
  Cross-region: ASYNC replication via streaming log

  Pro:    fast local reads/writes (single-DC latency)
  Con:    cross-region read sees up to ~seconds of staleness
```

**Conflict resolution across regions:**

Two regions can independently write the same key. Resolution options:
- **Last-write-wins** by Hybrid Logical Clock (HLC) — simple, may lose writes
- **CRDTs** — counters, sets, OR-maps merge deterministically without conflict
- **Multi-version with client reconciliation** — return all conflicting versions, client picks

DynamoDB Global Tables use LWW. Riak and AntidoteDB use CRDTs. Cosmos DB exposes all four.

**Datacenter-aware placement:**

Modify the ring so that the N=3 replicas are spread across 3 racks (or 3 AZs). A whole-rack failure still leaves data available:

```
Key "user:1" → [rack1.NodeA, rack2.NodeB, rack3.NodeC]
              ↑ not [rack1.NodeA, rack1.NodeB, rack1.NodeC] ←
              because losing rack1 = total data loss for this key
```

Cassandra calls this `NetworkTopologyStrategy`.

---

### 5. Client SDK & connection management

**Problem in this project:** Clients are imaginary. There is no SDK, no retry policy, no connection pool.

**Production client SDK responsibilities:**

```csharp
var client = KvClient.Connect(
    seeds: new[] { "kv-1.prod:9000", "kv-2.prod:9000" },  // bootstrap nodes
    apiKey: "...",
    pool:    new PoolConfig { Min=4, Max=32, IdleSec=60 },
    retry:   RetryPolicy.ExponentialBackoff(maxAttempts: 3, jitterMs: 50),
    timeout: TimeSpan.FromMilliseconds(200)
);

await client.PutAsync("user:1", "Alice", consistency: Consistency.Quorum);
```

**Key SDK features:**

1. **Topology discovery** — bootstrap from a few seed nodes, then learn the full ring from gossip. No DNS round-robin required.
2. **Token-aware routing** — SDK hashes the key locally and contacts the primary node directly, skipping the coordinator hop. Cuts one network round-trip.
3. **Connection pooling** — persistent HTTP/2 multiplexed connections per node. Opening TCP per request would dominate latency.
4. **Retry with backoff + jitter** — retry transient failures (network blips, transient quorum failures), give up on permanent ones (auth failure). Jitter prevents thundering-herd retries.
5. **Idempotency tokens** — for `PUT`, include a client-generated UUID. If the request is retried, the server dedups based on the token within a TTL window (5 min). Prevents double-write on network timeouts.
6. **Per-request consistency override** — `Consistency.One` for cache-style fast reads, `Consistency.Quorum` for strong reads, `Consistency.All` for the strongest possible guarantee.

---

### 6. Operational tooling

**Problem in this project:** Adding a node just inserts it into a `Dictionary`. Real cluster ops are complex.

**Adding a node (bootstrapping):**

```
1. New node joins gossip as STATE=joining
2. Computes which key ranges it now owns (from updated ring)
3. Streams those ranges from current owners (the "bootstrap stream")
   → ~hours for a 1TB node — throttled to avoid impacting live traffic
4. When all ranges are caught up, transitions to STATE=normal
5. Ring now serves reads from the new node
```

The streaming uses a separate low-priority network channel so live reads/writes aren't starved.

**Decommissioning a node:**

```
1. Mark node as STATE=leaving in gossip
2. Stream the node's data to the successors that will take over
3. When complete, remove from ring
4. Other nodes drop replicas of keys that no longer belong to them
```

Never just kill a node — its replicas may be the only surviving copies of keys whose other replicas are also down.

**Rebalancing:**

If load distribution becomes skewed (some nodes 80% full, others 30%), redistribute virtual nodes to even out ownership. Cassandra calls this `nodetool repair --rebalance`.

**Schema/format migrations:**

```
Old SSTable format → new SSTable format:
  Strategy 1: read-time compatibility (read both formats, write only new)
  Strategy 2: rolling rewrite (background process rewrites old SSTables)
  Strategy 3: forced compaction with new format (write amplification spike)
```

Never break the old format — old SSTables still on disk would become unreadable.

---

### 7. Rate limiting & hot-key handling

**Problem in this project:** A single malicious or buggy client can hammer one node into oblivion. One hot key (e.g., a celebrity's profile) can saturate its 3 replicas while other nodes idle.

**Per-tenant rate limiting:**

Token bucket per (tenant_id, operation) at the coordinator:

```
tenant_a: 10 000 ops/sec, burst 50 000
  → token bucket refills at 10k/s, cap 50k
  → exceeded → return 429 Too Many Requests
```

Prevents one tenant from starving others (the "noisy neighbour" problem in multi-tenant systems).

**Hot-key detection:**

Maintain a top-K counter (Count-Min Sketch) at each node:

```
Every 60s, report top 10 keys by access count to the coordinator.
If a key receives > 10× the median load → flag as HOT.
```

**Hot-key mitigation:**

1. **Replica fan-out** — temporarily increase the read-replica count for the hot key (RF=10 instead of 3) so reads spread across more nodes.
2. **Edge caching** — push hot keys to a CDN-like edge cache with short TTL (1–5s). Eliminates the underlying storage hit entirely.
3. **Client-side caching** — coordinator hints to clients "this key is hot, cache locally for 1s". DynamoDB DAX works this way.

**Write-side backpressure:**

If MemTable flushes can't keep up with write rate, the WAL grows unboundedly. Apply backpressure:

```
MemTable size > 80% of flush threshold → throttle writes (delay 1ms each)
MemTable size > 95% of flush threshold → reject writes (503 Service Unavailable)
```

Better to slow down or reject than to OOM and crash.

---

### 8. Capacity planning & sizing

**Sizing rules of thumb for an LSM KV store:**

```
RAM per node = MemTable + block cache + Bloom filters + OS page cache
  MemTable        =  2 × flush_threshold (active + immutable)   ~256 MB
  Block cache     = ~30% of node RAM                            ~10 GB on 32GB node
  Bloom filters   = ~1.25 GB per 1B keys at 10 bits/key         (kept resident)
  OS page cache   = remainder                                   ~15 GB

Disk per node:
  Working SSTable space        = ~2 × raw data (compaction overhead)
  WAL retention                = ~1 GB rolling
  Snapshots                    = ~1 × raw data
  Headroom for compaction     = ~30% free at all times (else compaction stalls)

Network per node:
  Replication traffic = (RF - 1) × write_rate
    e.g. 100 MB/s writes × (3-1) = 200 MB/s replication outbound
  Read repair       = ~5–10% of read traffic
  Gossip            = constant ~10 KB/s per node-pair
```

**Watch for these capacity smells:**

- Compaction backlog growing → disk I/O bottleneck, will eventually stall writes
- Block cache hit ratio < 80% → working set exceeds RAM, latency will climb
- Free disk < 30% → compaction starves, writes stall
- Cross-region replication lag > 10s → async replication can't keep up with write rate

---

## The Full Production Picture

```
Client SDK (token-aware, connection-pooled, retries with jitter)
      ↓ mTLS + JWT auth, idempotency token, consistency level
Coordinator (gRPC server)
  → Rate-limit check (per-tenant token bucket)
  → ConsistentHashRing.GetNodes("user:1", RF=3) = [NodeA, NodeB, NodeC]
  → gRPC: NodeA.Put(), NodeB.Put()  (W=2 quorum)
  → NodeC is down → store hint on NodeD (hinted handoff)
        ↓ each node:
      WAL append  ← crash-safe, sequential disk write
      Concurrent MemTable write (lock-free skip list)
      MemTable full? → flush to L0 SSTable (real file on disk, AES-256 encrypted)
      Background compaction: L0→L1→L2 (merge, sort, LZ4-compressed)

Client GET "user:1"
      ↓
  → gRPC: NodeA.Get(), NodeB.Get()  (R=2 quorum)
        ↓ each node:
      Check MemTable (RAM)
      Check L0 SSTables (Bloom filter → block cache → disk)
      Check L1, L2... (one file per level, binary search)
  → Compare timestamps (HLC) → return latest
  → Read repair: update any stale replica

Background processes (always running, every node):
  Gossip (SWIM)        → cluster membership, failure detection
  Compaction           → merge SSTables, reclaim space
  Anti-entropy (Merkle)→ reconcile replica drift
  Hint replay          → deliver buffered writes to recovered nodes
  TTL cleanup          → remove expired entries during compaction
  Snapshot + WAL ship  → continuous backup to object storage
  Cross-region stream  → async replication to standby regions
  Hot-key detection    → top-K counter, escalate replica fan-out

Cluster-wide observability (always-on):
  Prometheus metrics   → latency p50/p99, cache hit ratio, compaction lag
  Distributed tracing  → per-request trace across all hops
  Structured logs      → request_id, tenant_id, key_hash for searchability
  Audit log            → tamper-evident record of every admin action

Operator workflows (occasional, controlled):
  Node add/remove      → ring update + bootstrap stream
  Rebalance            → vnode redistribution
  Schema migration     → rolling format upgrade
  PITR restore         → snapshot + WAL replay to target timestamp
```

The core logic (LSM tree, consistent hashing, quorum reads/writes, Bloom filters, tombstones) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with network I/O, disk persistence, fault tolerance, security, observability, and operability.
