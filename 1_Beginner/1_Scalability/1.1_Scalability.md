# Scalability

---

## 1. What is Consistent Hashing, and why is it useful?

### The Problem It Solves

Imagine you have 3 servers and you distribute keys using simple modulo hashing:

```
server = hash(key) % N   (N = number of servers)
```

If you add or remove a server, **N changes**, and almost every key remaps to a different server. In a cache, this means a **cache avalanche** — sudden massive cache misses hitting your database.

### How Consistent Hashing Works

Instead of mapping keys to servers directly, you map **both keys and servers onto a circular ring** (0 to 2³²-1).

```
        0
        |
   S3 --+-- K1
  /              \
K3                K2
  \              /
   S1 ---------- S2
        |
       K4
```

**Rule:** A key is assigned to the **first server encountered clockwise** on the ring.

- `K1` → `S3`
- `K2` → `S2`
- `K3` → `S1`
- `K4` → `S1`

### Adding/Removing a Server

**Add S4** between S3 and S2 → only keys that fall between S3 and S4 move. Everything else is **untouched**.

**Remove S1** → only S1's keys move to S2. No other keys are affected.

> With N servers, adding/removing one only remaps **~1/N keys** instead of almost all keys.

### Virtual Nodes

A problem with basic consistent hashing is **uneven distribution** — servers may end up with very different loads depending on where they land on the ring.

**Solution:** Each physical server gets multiple positions on the ring (virtual nodes).

```
S1 → [S1_v1, S1_v2, S1_v3]
S2 → [S2_v1, S2_v2, S2_v3]
S3 → [S3_v1, S3_v2, S3_v3]
```

This spreads load more evenly and also handles heterogeneous servers — a stronger server can have more virtual nodes.

### Where It's Used
- **Distributed caches:** Memcached, Redis Cluster
- **Distributed databases:** Cassandra, DynamoDB
- **Load balancers** distributing requests across backend nodes

### Trade-offs

| Pro | Con |
|-----|-----|
| Minimal key remapping on topology change | More complex to implement than modulo hashing |
| Scales well with virtual nodes | Virtual node count needs tuning |
| Handles node failures gracefully | Still needs rebalancing logic |

---

## 2. Vertical Scaling vs. Horizontal Scaling

### Vertical Scaling (Scale Up)

Add more resources to a **single machine** — more CPU, RAM, faster storage.

```
Before:          After:
[  2 CPU  ]  →  [  16 CPU  ]
[  8 GB   ]     [  64 GB   ]
[  Server ]     [  Server  ]
```

**Real example:** Upgrading your PostgreSQL server from 8 cores/32GB RAM to 64 cores/256GB RAM.

**When it works well:**
- Relational databases (hard to distribute)
- Legacy monolithic applications
- When simplicity matters — no code changes needed

**Limits:**
- Hardware has a ceiling — you can't infinitely upgrade one machine
- Single point of failure — if it goes down, everything goes down
- Downtime often required during upgrades
- Expensive at the high end (non-linear cost curve)

---

### Horizontal Scaling (Scale Out)

Add **more machines** to distribute the load.

```
Before:              After:
                     [Server 1]
[  Server  ]  →      [Server 2]
                     [Server 3]
```

**Real example:** Adding more web server instances behind a load balancer as traffic grows.

**When it works well:**
- Stateless services (web servers, API servers)
- Systems designed for distribution (NoSQL databases)
- When you need high availability
- Cloud environments (easy to spin up instances)

**Challenges:**
- Applications must be **stateless** or handle distributed state
- Requires load balancers, service discovery
- Data consistency becomes harder
- More operational complexity

---

### Side-by-Side Comparison

| Dimension | Vertical | Horizontal |
|-----------|----------|------------|
| Cost | Expensive at scale | Cheaper commodity hardware |
| Availability | Single point of failure | Fault tolerant |
| Complexity | Simple — no code changes | Complex — distributed systems problems |
| Ceiling | Hard hardware limit | Theoretically unlimited |
| Speed to scale | Requires downtime often | Can be dynamic/automatic |
| Best for | Databases, stateful systems | Web servers, caches, stateless APIs |

---

### In Practice — Use Both

Most real systems use **both** strategies together:

```
Internet
    ↓
[Load Balancer]          ← horizontal (multiple LBs)
    ↓
[Web Servers x10]        ← horizontal scaling
    ↓
[Cache Cluster]          ← horizontal scaling
    ↓
[Primary DB]             ← vertical scaling (+ read replicas horizontal)
```

A common pattern: **scale out** your stateless layers horizontally, **scale up** your database until it hurts, then introduce sharding or read replicas.

---

## 3. What is Sharding, and how does it help with scalability?

### The Problem

A single database node has limits:
- Storage capacity (can't store 100TB on one disk)
- Write throughput (one machine can only handle so many writes/sec)
- Query performance degrades as table size grows

**Replication** (covered separately) helps with read scale but **not write scale** — all writes still go to one primary.

### What is Sharding?

Sharding is **horizontal partitioning** of data across multiple database nodes. Each node (shard) holds a **subset of the data** and is fully independent.

```
Without Sharding:          With Sharding:

[   All Users   ]    →    [Shard 1: Users 1-10M  ]
[  100M records ]         [Shard 2: Users 10M-20M]
[  Single Node  ]         [Shard 3: Users 20M-30M]
```

Each shard handles its own reads **and** writes — so write throughput scales linearly.

---

### Sharding Strategies

#### 1. Range-Based Sharding
Partition by a range of values.

```
Shard 1: user_id 1        → 10,000,000
Shard 2: user_id 10000001 → 20,000,000
Shard 3: user_id 20000001 → 30,000,000
```

**Pro:** Range queries are efficient (scan one shard)  
**Con:** Hot spots — if new users always get highest IDs, Shard 3 gets all writes (**hotspot problem**)

#### 2. Hash-Based Sharding
```
shard = hash(user_id) % num_shards
```

**Pro:** Even distribution, no hot spots  
**Con:** Range queries are inefficient — must hit all shards. Resharding is expensive.

#### 3. Directory-Based Sharding
A lookup service maps each key to a shard.

```
[Lookup Table]
user_id 1001 → Shard 2
user_id 1002 → Shard 1
```

**Pro:** Flexible, can move data between shards easily  
**Con:** Lookup service becomes a bottleneck and single point of failure

#### 4. Geographic Sharding
Partition by location — US users on US shard, EU users on EU shard.

**Pro:** Low latency for users, data residency compliance  
**Con:** Uneven load if regions have different traffic volumes

---

### The Problems Sharding Introduces

Sharding is powerful but makes several things much harder:

**1. Cross-shard queries / joins**
```sql
-- This is easy on a single DB:
SELECT u.name, o.total 
FROM users u JOIN orders o ON u.id = o.user_id

-- With sharding: users and their orders may be on different shards
-- You need application-level joins or denormalization
```

**2. Cross-shard transactions**
ACID transactions across shards require distributed transactions (2-phase commit), which are complex and slow.

**3. Rebalancing**
When you add a new shard, you need to move data around. With hash-based sharding, this is disruptive. Consistent hashing helps minimize this.

**4. Schema changes**
Running a migration across 10 shards requires coordination.

---

### Sharding vs. Other Approaches

| Approach | Solves Read Scale | Solves Write Scale | Complexity |
|----------|------------------|--------------------|------------|
| Read Replicas | Yes | No | Low |
| Vertical Scaling | Partially | Partially | Low |
| Caching | Yes (partially) | No | Medium |
| Sharding | Yes | Yes | High |

---

### When to Shard

Sharding is a **last resort** — it adds enormous complexity. Before sharding, exhaust:

1. Vertical scaling
2. Read replicas + caching
3. Query optimization and indexing

Shard when you have a genuine write bottleneck or data volume that no single machine can handle. Companies like Instagram, Pinterest, and Uber spent years before introducing sharding.
