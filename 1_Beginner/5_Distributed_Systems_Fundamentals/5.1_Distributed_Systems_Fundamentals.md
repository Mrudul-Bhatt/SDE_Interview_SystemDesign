# Distributed Systems Fundamentals

---

## 4. Explain the CAP Theorem with Examples

### What is the CAP Theorem?

The CAP theorem states that a distributed system can only **guarantee 2 out of these 3 properties** at the same time:

- **C — Consistency:** Every read receives the most recent write (or an error)
- **A — Availability:** Every request receives a response (not necessarily the most recent data)
- **P — Partition Tolerance:** The system continues operating even if network communication between nodes breaks down

```
           Consistency
               /\
              /  \
             /    \
            / CP   \
           /        \
          /____CA____\
    Availability   Partition
                  Tolerance
         AP
```

---

### Why You Can't Have All Three

In a distributed system, **network partitions are inevitable** — hardware fails, cables get cut, packets get dropped. So in practice, **P is non-negotiable**. The real trade-off is always:

> **When a partition occurs — do you stay consistent or stay available?**

---

### Breaking Down Each Trade-off

#### CP — Consistency + Partition Tolerance (sacrifice Availability)

When a partition happens, the system **refuses to respond** rather than risk returning stale data.

```
[Node A] ←✗→ [Node B]   (network partition)

User reads from Node B:
→ Node B: "I can't confirm I have the latest data, returning ERROR"
```

**Real-world example:** **ZooKeeper, HBase, etcd**

**When to choose CP:**
- Financial transactions — you cannot show a user a wrong balance
- Inventory systems — overselling is worse than showing "unavailable"
- Any system where correctness is more critical than uptime

---

#### AP — Availability + Partition Tolerance (sacrifice Consistency)

When a partition happens, the system **keeps responding** but may return stale data.

```
[Node A] ←✗→ [Node B]   (network partition)

User writes "X=5" to Node A
User reads from Node B:
→ Node B: "X=3"  (old value, but system stays up)
```

**Real-world example:** **Cassandra, DynamoDB, CouchDB**

**When to choose AP:**
- Social media feeds — showing a slightly old post count is fine
- Shopping cart — better to let users add items than show an error
- DNS — returns possibly stale records but never goes down

---

#### CA — Consistency + Availability (sacrifice Partition Tolerance)

Only possible on a **single node** or within a single data center with a reliable network — not truly distributed. Traditional relational databases (single node PostgreSQL, MySQL) fall here.

Once you distribute across multiple nodes, you must accept partition tolerance, so CA doesn't practically exist in distributed systems.

---

### CAP in Practice — Real Systems

| System | Type | Reason |
|--------|------|--------|
| Cassandra | AP | Returns best available data, eventual consistency |
| DynamoDB | AP (tunable) | Can configure consistency per-request |
| MongoDB | CP | Primary returns error if can't confirm writes |
| ZooKeeper | CP | Refuses reads during partition |
| Redis (cluster) | AP | Continues serving possibly stale data |
| PostgreSQL (single) | CA | Not distributed, no partition tolerance |

---

### The PACELC Extension

CAP only describes behavior **during a partition**. PACELC extends it to also cover **normal operation**:

```
If Partition → choose between A and C
Else (normal) → choose between L (Latency) and C (Consistency)
```

Even without failures, there's a trade-off: strong consistency requires coordination between nodes (higher latency), while eventual consistency is faster but may be stale.

---

### Common Interview Mistake

Many candidates say "I'll pick CP" or "AP" as if it's a binary global choice. In reality:

- Many systems are **tunable** — DynamoDB lets you choose consistency per read
- Different **parts of the same system** can have different trade-offs (user profile = AP, payment = CP)
- The trade-off only matters **during a partition**, which is hopefully rare

---

## 9. Replication and its Types

### What is Replication?

Replication means **keeping copies of the same data on multiple nodes**. It serves two purposes:

1. **High Availability** — if one node dies, others take over
2. **Read Scaling** — distribute read traffic across multiple replicas

```
Without Replication:        With Replication:

[Primary DB]                [Primary DB]
     ↑                      /     |     \
  All reads              [R1]   [R2]   [R3]
  + writes               reads  reads  reads
  Single SPOF            Primary handles writes only
```

---

### Master-Slave Replication (Primary-Replica)

One node is the **master (primary)** — it accepts all writes. Changes are **replicated** to one or more **slave (replica)** nodes which only serve reads.

```
        Writes
          ↓
      [Master]
     /    |    \
  [S1]  [S2]  [S3]   ← Reads only
```

#### How Replication Works

The master writes changes to a **replication log** (WAL in PostgreSQL, binlog in MySQL). Slaves consume this log and apply the same changes.

```
Master: INSERT user (id=5, name="Alice")
   → writes to binlog
   → S1 reads binlog → applies INSERT
   → S2 reads binlog → applies INSERT
   → S3 reads binlog → applies INSERT
```

#### Synchronous vs Asynchronous Replication

**Synchronous:** Master waits for at least one slave to confirm before acknowledging the write to the client.
```
Client → Master writes → waits for S1 ACK → responds to client
```
- Guarantees no data loss on master failure
- Higher write latency (waiting for network round trip to slave)

**Asynchronous:** Master acknowledges write immediately, replication happens in background.
```
Client → Master writes → responds to client immediately
                      → replicates to slaves later
```
- Lower write latency
- Risk of data loss if master crashes before replication completes (replication lag)

---

#### Replication Lag

The delay between a write on master and it appearing on replicas is called **replication lag**.

```
t=0: User updates profile picture (write to master)
t=0: Master ACKs to user
t=2ms: User refreshes page → reads from replica → sees OLD picture
t=50ms: Replica catches up → now shows new picture
```

This is a form of **eventual consistency**. Solutions:
- Read your own writes from master for a short window after writing
- Route reads to master for latency-sensitive operations
- Use synchronous replication for critical data

---

#### Failure Handling in Master-Slave

**Slave fails:** No problem — other slaves handle reads. Dead slave rejoins and catches up from replication log.

**Master fails:**
1. Detect failure (via heartbeat)
2. Elect a new master from existing slaves (**failover**)
3. Point other slaves and clients to new master
4. When old master recovers, it becomes a slave

Failover can be **manual** (operator promotes a slave) or **automatic** (tools like MHA, Orchestrator, Patroni for PostgreSQL).

---

#### Pros and Cons

| Pro | Con |
|-----|-----|
| Simple to understand and operate | Master is write bottleneck |
| Great for read-heavy workloads | Replication lag causes stale reads |
| Automatic failover possible | Failover has complexity and brief downtime risk |
| Slaves can be used for backups | All writes go through one node |

---

### Master-Master Replication (Multi-Primary)

Both (or all) nodes accept **reads and writes**. Changes from each master are replicated to the other.

```
Writes ↓    ↓ Writes
    [Master A] ←→ [Master B]
    Reads ↑    ↑ Reads
```

#### The Core Problem: Write Conflicts

If two users update the same record on different masters simultaneously:

```
t=0: User A updates email to "a@x.com" on Master A
t=0: User B updates email to "b@x.com" on Master B
t=1ms: Masters replicate → CONFLICT — which value wins?
```

**Conflict resolution strategies:**
- **Last Write Wins (LWW):** Timestamp-based — most recent write wins (risk: clock skew)
- **Application-level resolution:** App defines merge logic (used in CRDTs)
- **Manual resolution:** Flag conflicts, let user resolve (used in Dynamo-style systems)

---

#### When to Use Master-Master

- **Multi-region active-active:** Each region writes to its local master, replicates globally
- **High write availability:** If one master goes down, other keeps accepting writes with no failover needed
- **Geographic write locality:** Users in US write to US master, EU users to EU master

```
US Users → [Master US] ←——replication——→ [Master EU] ← EU Users
```

---

#### Pros and Cons

| Pro | Con |
|-----|-----|
| No write single point of failure | Conflict resolution is complex |
| Better write throughput | Risk of data inconsistency |
| Active-active multi-region possible | Harder to reason about data state |
| No failover needed for writes | Not all databases support it well |

---

### Master-Slave vs Master-Master

| Dimension | Master-Slave | Master-Master |
|-----------|-------------|---------------|
| Write nodes | 1 | 2+ |
| Read nodes | Many | Many |
| Conflict risk | None | High |
| Complexity | Low | High |
| Write availability | Single point | High |
| Use case | Read-heavy apps | Multi-region active-active |
| Examples | MySQL with replicas | Cassandra, CockroachDB, Galera |

---

## 10. Eventual Consistency vs. Strong Consistency

### Strong Consistency

After a write completes, **every subsequent read** from any node returns that write's value. The system behaves as if there's only one copy of the data.

```
t=0: Write X=5 (completes)
t=1: Read X from any node → always returns 5
```

**How it's achieved:** Reads and writes go through a single node (master), or a **quorum** of nodes must agree before any read/write completes.

**Real examples:** Traditional RDBMS, ZooKeeper, etcd, Google Spanner

**Cost:** Coordination overhead — nodes must communicate to agree on a value, adding latency. During a partition, the system may refuse requests rather than return stale data.

---

### Eventual Consistency

If no new writes happen, **all nodes will eventually converge** to the same value — but reads during the convergence window may return stale data.

```
t=0: Write X=5 to Node A
t=0: Read X from Node B → returns X=3  (old value)
t=50ms: Node B replicates from Node A
t=50ms: Read X from Node B → returns X=5  (converged)
```

**How it's achieved:** Writes succeed on one node and replicate asynchronously. No coordination required.

**Real examples:** DNS, Cassandra, DynamoDB (default), S3

**Cost:** Stale reads are possible. Application must be designed to tolerate this.

---

### The Spectrum Between Them

Consistency is not binary — it's a spectrum:

```
Eventual ←————————————————————————————→ Strong
Consistency                         Consistency

[Eventual]  [Monotonic Read]  [Read-Your-Writes]  [Linearizable]
   |               |                  |                  |
Stale reads    Once you read      After your own    Global ordering,
possible       a value, you       write, you         single copy
               won't see older    always see it      semantics
```

#### Monotonic Read Consistency
You will never read older data after having read newer data.
```
Read X=5 → subsequent reads will never return X=3
```

#### Read-Your-Writes Consistency
After you write a value, you will always read your own write back.
```
User updates profile picture → user always sees their new picture
Other users may still see old picture temporarily
```

#### Causal Consistency
Causally related operations are seen in order by all nodes.
```
User posts comment → User sees their comment appear
Other users may see it slightly later, but always after the post it responds to
```

---

### Quorum — The Middle Ground

Many systems (Cassandra, DynamoDB) let you **tune consistency per operation** using quorums.

With N replicas:
- **W** = number of nodes that must confirm a write
- **R** = number of nodes that must respond to a read

**Rule for strong consistency:** `W + R > N`

```
N=3 replicas

Eventual:  W=1, R=1  → fast, but stale reads possible
Quorum:    W=2, R=2  → W+R=4 > 3, strong consistency guaranteed
Strong:    W=3, R=1  → all nodes confirm write, any read is current
```

This lets you dial between performance and consistency based on the operation:
- Payment processing → `W=3, R=3` (strong)
- Social feed reads → `W=1, R=1` (eventual, fast)

---

### Choosing Between Them

| Use Strong Consistency When | Use Eventual Consistency When |
|----------------------------|-------------------------------|
| Financial transactions | Social media feeds |
| Inventory / stock levels | User profile views |
| Authentication tokens | Shopping cart (add to cart) |
| Leader election | DNS resolution |
| Booking systems (no double-booking) | View/like counts |

---

### Real-World Mental Model

**Strong consistency** = asking a single librarian for a book. They check one authoritative shelf — guaranteed correct, but they're a bottleneck.

**Eventual consistency** = asking any of 10 librarians. Most of the time you get the right answer fast, but occasionally one hasn't shelved the latest return yet.

---

### Key Takeaway for Interviews

The question interviewers are really asking: **"Do you understand that you cannot have both correctness and speed/availability in a distributed system, and can you make the right trade-off for the problem at hand?"**

Always answer with: *"It depends on the business requirement — here's what I'd choose and why."*
