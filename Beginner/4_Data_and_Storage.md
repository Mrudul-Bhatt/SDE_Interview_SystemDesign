# Data & Storage

---

## 8. When would you choose SQL vs. NoSQL?

### What is SQL?

SQL databases are **relational** — data is stored in tables with rows and columns, and relationships between tables are enforced via foreign keys. You query them with structured SQL.

```
Users Table:
| id | name    | email           |
|----|---------|-----------------|
| 1  | Alice   | alice@email.com  |
| 2  | Bob     | bob@email.com    |

Orders Table:
| id | user_id | total  |
|----|---------|--------|
| 1  | 1       | $50.00 |
| 2  | 1       | $30.00 |

SELECT u.name, o.total
FROM users u JOIN orders o ON u.id = o.user_id
WHERE u.id = 1;
```

**Examples:** PostgreSQL, MySQL, SQLite, Microsoft SQL Server, Oracle

---

### What is NoSQL?

NoSQL databases store data in formats other than relational tables. "NoSQL" is an umbrella term covering several very different data models.

#### Document Store
Stores data as JSON-like documents. Each document is self-contained.

```json
{
  "id": 1,
  "name": "Alice",
  "email": "alice@email.com",
  "orders": [
    { "id": 1, "total": "$50.00" },
    { "id": 2, "total": "$30.00" }
  ]
}
```
**Examples:** MongoDB, CouchDB, Firestore

---

#### Key-Value Store
Simplest model — a key maps to a value (string, JSON, binary). Extremely fast.

```
SET user:1:name "Alice"
GET user:1:name → "Alice"
```
**Examples:** Redis, DynamoDB, Memcached

---

#### Wide-Column Store
Tables with rows and dynamic columns. Each row can have different columns. Optimized for massive write throughput.

```
Row Key: user:1
  Columns: name=Alice, email=alice@email.com, last_login=2024-01-01

Row Key: user:2
  Columns: name=Bob, phone=555-1234   ← different columns, no problem
```
**Examples:** Cassandra, HBase, Google Bigtable

---

#### Graph Database
Stores entities as nodes and relationships as edges. Optimized for traversing relationships.

```
(Alice) --[FRIENDS_WITH]--> (Bob)
(Alice) --[PURCHASED]--> (Product: iPhone)
(Bob)   --[REVIEWED]--> (Product: iPhone)
```
**Examples:** Neo4j, Amazon Neptune

---

### Core Differences

| Dimension | SQL | NoSQL |
|-----------|-----|-------|
| Schema | Fixed, enforced upfront | Flexible, dynamic |
| Relationships | First-class via JOINs | Application-level or denormalized |
| Query language | Standardized SQL | Varies by database |
| ACID transactions | Strong, built-in | Varies (some support, some don't) |
| Scaling | Primarily vertical | Primarily horizontal |
| Consistency | Strong by default | Often eventual |
| Maturity | Decades old, battle-tested | Newer, varies by system |

---

### ACID vs BASE

SQL databases guarantee **ACID**:

- **Atomicity** — a transaction either fully completes or fully rolls back
- **Consistency** — data always moves from one valid state to another
- **Isolation** — concurrent transactions don't interfere with each other
- **Durability** — committed data survives crashes

```sql
BEGIN;
  UPDATE accounts SET balance = balance - 100 WHERE id = 1;
  UPDATE accounts SET balance = balance + 100 WHERE id = 2;
COMMIT;
-- Either both updates happen, or neither does
```

NoSQL databases often follow **BASE**:

- **Basically Available** — system stays available
- **Soft state** — data may change over time without input (replication)
- **Eventually consistent** — system converges to consistency over time

---

### When to Choose SQL

**1. Relationships between data are important**
If your data has complex, many-to-many relationships that you query from different angles, relational model + JOINs are far cleaner than managing this in application code.

**2. ACID transactions are required**
Financial systems, booking systems, inventory — anywhere a partial write is catastrophic.

```sql
-- Bank transfer: both updates must succeed or both must fail
BEGIN;
  UPDATE accounts SET balance = balance - 500 WHERE id = 1;
  UPDATE accounts SET balance = balance + 500 WHERE id = 2;
COMMIT;
```

**3. Your schema is stable and well-defined**
If you know your data structure upfront and it won't change dramatically, a rigid schema is a feature — it enforces correctness.

**4. Complex queries and reporting**
Ad-hoc SQL queries, aggregations, GROUP BY, window functions — SQL is decades ahead of NoSQL for analytical queries.

**5. Team is familiar with relational model**
SQL is universal knowledge. Every developer knows it.

**Good fits for SQL:** E-commerce platforms, banking, ERP systems, CRM systems, any system with structured, relational data.

---

### When to Choose NoSQL

**1. Massive scale with simple access patterns**
If you're doing mostly key lookups or simple queries at billions of rows, NoSQL's horizontal scaling wins.

```
Get user profile by ID → Key-Value or Document store
Write 1M events/second → Wide-column (Cassandra)
```

**2. Flexible or evolving schema**
Early-stage products where the data model changes frequently. Document stores let you add fields without migrations.

```json
// v1 of user document
{ "id": 1, "name": "Alice" }

// v2 — just add the field, no ALTER TABLE needed
{ "id": 1, "name": "Alice", "preferences": { "theme": "dark" } }
```

**3. Hierarchical or nested data**
Data that naturally fits a document model — no need to split across multiple tables and JOIN them back.

```json
{
  "post_id": 1,
  "title": "Hello World",
  "author": { "id": 1, "name": "Alice" },
  "comments": [
    { "user": "Bob", "text": "Great post!" },
    { "user": "Carol", "text": "Thanks for sharing." }
  ]
}
```

**4. High write throughput**
Cassandra and HBase are built for write-heavy workloads — time-series data, event logging, IoT sensor data.

**5. Graph relationships**
Social networks, recommendation engines, fraud detection — traversing relationships is what graph databases do best.

```
"Find all friends of friends who also bought Product X"
→ Graph DB: 2 hops, trivial
→ SQL: 3-way JOIN, expensive at scale
```

---

### The Spectrum of Trade-offs

```
         SQL                                NoSQL
          |                                   |
    Strong schema                      Flexible schema
    Complex queries                    Simple access patterns
    ACID transactions                  High throughput
    Vertical scale                     Horizontal scale
    Slower writes at scale             Fast writes
    Harder to shard                    Built for distribution
```

---

### Misconceptions to Avoid in Interviews

**"NoSQL is faster than SQL"**
Not inherently true. Redis is faster than PostgreSQL for key lookups, but PostgreSQL with proper indexing beats MongoDB for complex queries. It depends on the workload.

**"NoSQL doesn't have transactions"**
MongoDB has multi-document ACID transactions since v4.0. FaunaDB, CockroachDB (NewSQL) offer full ACID at distributed scale.

**"SQL can't scale"**
Google Spanner, CockroachDB, and PlanetScale are distributed SQL databases that scale horizontally. Instagram ran on PostgreSQL until very recently at massive scale.

**"I should use NoSQL for new projects because it's modern"**
Most startups are better served starting with PostgreSQL. It's incredibly capable, and you avoid premature complexity.

---

### NewSQL — The Best of Both Worlds?

A newer category that aims to combine SQL's consistency with NoSQL's horizontal scalability:

| Database | Description |
|----------|-------------|
| CockroachDB | Distributed SQL, globally consistent |
| Google Spanner | Planet-scale SQL with TrueTime |
| PlanetScale | MySQL-compatible, horizontal sharding |
| YugabyteDB | PostgreSQL-compatible, distributed |

---

### Decision Framework for Interviews

```
1. Does the data have complex relationships? → SQL
2. Do I need ACID transactions? → SQL
3. Is the schema well-defined and stable? → SQL
4. Do I need massive horizontal write scale? → NoSQL (Cassandra/DynamoDB)
5. Is the data document-like / hierarchical? → NoSQL (MongoDB)
6. Is it graph data? → Graph DB (Neo4j)
7. Do I need sub-millisecond key lookups? → NoSQL (Redis)
8. Am I unsure? → Start with PostgreSQL, migrate later
```

---

### Real-World: Using Both Together

Most large systems use **both SQL and NoSQL** for different purposes:

```
[User Service]         → PostgreSQL   (relational, ACID)
[Session Store]        → Redis        (fast key-value)
[Product Catalog]      → MongoDB      (flexible documents)
[Activity Feed]        → Cassandra    (high write throughput)
[Recommendation Graph] → Neo4j        (graph traversal)
[Search]               → Elasticsearch (full-text search)
```

The key insight: **choose the right tool for each problem**, not one database for everything.

---

## 15. What is Database Indexing, and what are the trade-offs of over-indexing?

### The Problem Without Indexes

Imagine a `users` table with 10 million rows and you run:

```sql
SELECT * FROM users WHERE email = 'alice@email.com';
```

Without an index, the database does a **full table scan** — reads every single row until it finds a match.

```
Full Table Scan:
[Row 1] → not alice
[Row 2] → not alice
...
[Row 4,823,419] → found alice!  (read ~5M rows on average)
```

This is O(n) — linear with table size.

---

### What is an Index?

An index is a **separate data structure** that the database maintains alongside your table. It stores a subset of columns in a way that makes lookups fast.

```
Without index: scan all 10M rows → O(n)
With index:    look up in B-tree → O(log n) → ~23 comparisons for 10M rows
```

---

### How Indexes Work — B-Tree

The most common index type is a **B-Tree (Balanced Tree)**. It keeps data sorted and allows searches, insertions, and deletions in O(log n).

```
                    [50]
                   /    \
              [25]        [75]
             /    \      /    \
          [10]  [30]  [60]  [90]
```

For `WHERE email = 'alice@email.com'`:
1. Start at root
2. Compare → go left or right
3. Reach leaf node → get row pointer (page + offset)
4. Jump directly to that row in the table

**3-4 comparisons instead of 10 million reads.**

---

### Types of Indexes

#### Primary Index (Clustered Index)
The table data is **physically stored** in the order of this index. There can only be one per table.

```sql
CREATE TABLE users (
  id INT PRIMARY KEY,   -- clustered index on id
  name VARCHAR(100),
  email VARCHAR(100)
);
```

#### Secondary Index (Non-Clustered)
A separate structure that points back to the primary key. Multiple allowed per table.

```sql
CREATE INDEX idx_email ON users(email);
```

```
idx_email B-Tree:
"alice@email.com" → id=4823419
"bob@email.com"   → id=1204

Lookup: find "alice@email.com" → get id=4823419 → fetch row
```

#### Composite Index
Index on multiple columns together.

```sql
CREATE INDEX idx_last_first ON users(last_name, first_name);
```

**Left-prefix rule:** Helps queries filtering on `last_name` alone, or `last_name + first_name`. Does **not** help queries filtering only on `first_name`.

```sql
-- Uses the index:
WHERE last_name = 'Smith'
WHERE last_name = 'Smith' AND first_name = 'Alice'

-- Does NOT use the index:
WHERE first_name = 'Alice'
```

#### Covering Index
Contains **all the columns** a query needs — the database never touches the actual table.

```sql
CREATE INDEX idx_covering ON orders(user_id, status, total);

-- Served entirely from the index:
SELECT status, total FROM orders WHERE user_id = 1;
```

#### Hash Index
O(1) exact lookups but **cannot** do range queries.

```sql
-- Great for: WHERE id = 42
-- Useless for: WHERE id > 42 or BETWEEN
```

#### Full-Text Index
Tokenizes words for text search.

```sql
CREATE FULLTEXT INDEX idx_content ON articles(title, body);
SELECT * FROM articles WHERE MATCH(title, body) AGAINST('machine learning');
```

---

### When Indexes Help

```sql
-- WHERE clause filtering:
SELECT * FROM orders WHERE user_id = 1;

-- JOIN conditions:
SELECT * FROM orders o JOIN users u ON o.user_id = u.id;

-- ORDER BY (avoid sorting):
SELECT * FROM orders ORDER BY created_at DESC;

-- GROUP BY:
SELECT user_id, COUNT(*) FROM orders GROUP BY user_id;

-- Range queries:
SELECT * FROM orders WHERE created_at > '2024-01-01';
```

---

### The Trade-offs of Over-Indexing

**1. Write overhead**
Every `INSERT`, `UPDATE`, or `DELETE` must update **all indexes** on the table.

```
INSERT INTO users (id, name, email, city, age) VALUES (...);
Must update: Primary index + idx_email + idx_city + idx_age + ...
```

**2. Storage cost**
Each index takes disk space — a table with many indexes can have 3-5x its own size in index storage.

**3. Query planner confusion**
Too many indexes → optimizer may choose the wrong one → worse performance.

**4. Maintenance cost**
Indexes fragment over time and need periodic rebuilding (`VACUUM`, `REINDEX`).

---

### Signs of Over-Indexing

```sql
-- PostgreSQL: find unused indexes
SELECT indexrelname, idx_scan
FROM pg_stat_user_indexes
WHERE idx_scan = 0;   -- never used
```

---

### Indexing Best Practices

```
1. Index columns used in WHERE, JOIN, ORDER BY, GROUP BY
2. Use composite indexes for multi-column filters (follow left-prefix rule)
3. Don't index low-cardinality columns (e.g., boolean — only 2 values)
4. Use covering indexes for read-heavy, performance-critical queries
5. Avoid indexing write-heavy tables aggressively
6. Periodically audit and drop unused indexes
7. Index foreign keys — JOINs need them
```

---

## 16. Object Storage vs. Block Storage vs. File Storage

### Block Storage

Data is stored as fixed-size **blocks**. Raw storage the OS treats like a hard drive. The file system sits on top.

```
[Block 0][Block 1][Block 2]...[Block N]
   raw bytes, no structure, no metadata
```

**Characteristics:** Sub-millisecond latency, single-server attachment, no sharing between servers.

**Use cases:** Database data files, VM boot volumes.

**Cloud examples:** AWS EBS, Google Persistent Disk, Azure Managed Disks

---

### File Storage (Network File System)

Data stored as **files and directories**. Multiple servers share the same file system over a network.

```
/
├── documents/
│   ├── report.pdf
│   └── notes.txt
└── images/
    └── photo.jpg
```

**Characteristics:** Familiar hierarchy, shared access, higher latency than block (network overhead).

**Use cases:** Shared config, enterprise home directories, legacy apps expecting a filesystem.

**Cloud examples:** AWS EFS, Azure Files, Google Filestore

---

### Object Storage

Data stored as **objects** — flat key-value pairs, accessed via HTTP API. No true folder hierarchy.

```
Key: "users/avatars/alice.jpg"    → binary data + metadata
Key: "videos/2024/intro.mp4"      → binary data + metadata
Key: "reports/q4-2024.pdf"        → binary data + metadata
```

**Characteristics:** Unlimited scale, HTTP API access, globally accessible, higher latency (50-200ms), cheap at scale, immutable by default.

**Use cases:** Static assets, media files, backups, data lakes, ML datasets.

**Cloud examples:** AWS S3, Google Cloud Storage, Azure Blob Storage

```python
import boto3
s3 = boto3.client('s3')
s3.upload_file('photo.jpg', 'my-bucket', 'users/alice/avatar.jpg')
```

---

### Side-by-Side Comparison

| Dimension | Block | File | Object |
|-----------|-------|------|--------|
| Structure | Raw blocks | Files & folders | Flat key-value |
| Access method | Mounted as disk | NFS/SMB mount | HTTP API |
| Sharing | Single server | Multiple servers | Anyone with URL |
| Latency | Lowest (<1ms) | Medium (1-10ms) | Higher (50-200ms) |
| Scale | Limited (TB) | Limited (TB) | Unlimited (EB) |
| Cost | Highest | Medium | Lowest at scale |
| Mutability | In-place edits | In-place edits | Immutable (replace) |
| Best for | Databases, boot volumes | Shared file systems | Media, backups, data lakes |
| Cloud example | AWS EBS | AWS EFS | AWS S3 |

---

### How They're Used Together

```
[Web Servers]     → boot from Block Storage (EBS)
[App Servers]     → share config via File Storage (EFS)
[User Uploads]    → stored in Object Storage (S3)
[Database]        → uses Block Storage (EBS) for data files
[Static Assets]   → served from Object Storage (S3) via CDN
[Backups]         → archived to Object Storage (S3 Glacier)
```

---

## 17. Explain ACID Properties in Databases

### What is ACID?

ACID is a set of four properties that guarantee **database transactions are processed reliably**.

---

### A — Atomicity

A transaction is **all or nothing**. Either every operation succeeds, or none of them do.

```sql
BEGIN TRANSACTION;
  UPDATE accounts SET balance = balance - 500 WHERE id = 1;  -- debit Alice
  UPDATE accounts SET balance = balance + 500 WHERE id = 2;  -- credit Bob
COMMIT;
```

Without atomicity: Alice loses $500, Bob never gets it if the system crashes mid-transaction.
With atomicity: ROLLBACK restores Alice's $500 — as if nothing happened.

**How it's implemented:** Write-Ahead Log (WAL) — changes logged before applied. On crash, DB replays or rolls back.

---

### C — Consistency

A transaction brings the database from one **valid state** to another. Constraints always enforced.

```sql
ALTER TABLE accounts ADD CONSTRAINT balance_positive CHECK (balance >= 0);

BEGIN TRANSACTION;
  UPDATE accounts SET balance = balance - 1000 WHERE id = 1;
  -- Alice only has $500 → constraint violated → REJECTED
ROLLBACK;
```

---

### I — Isolation

Concurrent transactions don't interfere with each other.

```
Transaction A: reads balance ($500)...
Transaction B: withdraws $300 → balance = $200
Transaction A: withdraws $400 based on old read → balance = $100 ?? (dirty read)
```

#### Isolation Levels

```
Isolation Level    | Dirty Read | Non-Repeatable Read | Phantom Read
-------------------|------------|--------------------|--------------
Read Uncommitted   | Possible   | Possible           | Possible
Read Committed     | Prevented  | Possible           | Possible
Repeatable Read    | Prevented  | Prevented          | Possible
Serializable       | Prevented  | Prevented          | Prevented
```

Most systems use **Read Committed** as a practical balance.

---

### D — Durability

Once committed, data survives crashes.

```
t=0: COMMIT transaction
t=1ms: Server power fails
t=10min: Server restarts
Result: Bob's $500 is still there
```

Changes are written to disk (WAL/redo log) before the commit is acknowledged.

---

### ACID vs BASE

| ACID | BASE |
|------|------|
| Strong consistency | Eventual consistency |
| Isolation between transactions | No isolation guarantees |
| Rollback on failure | No rollback |
| Lower throughput | Higher throughput |
| SQL databases | Cassandra, DynamoDB, MongoDB |

---

## 18. What is a Message Queue, and why would you use one?

### The Problem: Tight Coupling

```
[Order Service] ——HTTP——→ [Email Service]
                    ↑
         Order service blocked until email service responds
```

If Email Service is slow or down, Order Service fails too.

---

### What is a Message Queue?

A **buffer** that sits between services. Producers write messages; consumers read independently.

```
[Order Service] → [Queue] → [Email Service]
                      ↓      [SMS Service]
                      ↓      [Analytics Service]
```

Producer and consumer are fully **decoupled**.

---

### How It Works

```python
# Producer — returns immediately
queue.send({ "type": "order_placed", "order_id": 12345, "total": 99.99 })

# Consumer — processes at its own pace
while True:
    message = queue.receive()
    send_confirmation_email(message)
    queue.delete(message)   # acknowledge
```

---

### Core Properties

**Persistence** — messages survive consumer crashes, stay in queue until processed.

**At-least-once delivery** — if consumer crashes before acknowledging, message is retried. Consumers must be **idempotent**.

```python
def process_order(message):
    if already_processed(message['order_id']):
        return
    send_email(message['order_id'])
    mark_processed(message['order_id'])
```

**Backpressure handling** — if consumers are slow, messages buffer in the queue instead of crashing the producer.

---

### Why Use a Message Queue?

**1. Decoupling** — adding SMS Service requires zero changes to Order Service, just subscribe to queue.

**2. Async processing** — user sees "Order confirmed!" immediately; email/inventory/warehouse updates happen in background.

**3. Load leveling** — Black Friday spike: 50,000 orders buffered in queue, email service processes at a steady rate.

**4. Retry on failure** — message stays in queue if processing fails, retried automatically.

**5. Fan-out** — one message consumed by Email, SMS, Inventory, Analytics, Warehouse services.

---

### Message Queue vs. Pub/Sub

| | Message Queue | Pub/Sub |
|-|--------------|---------|
| Delivery | One consumer gets the message | All subscribers get a copy |
| Use case | Task distribution, work queues | Event broadcasting, fan-out |

---

### Dead Letter Queue (DLQ)

Messages that fail repeatedly (e.g., 3 retries) are moved to a DLQ instead of blocking the queue forever.

```
Message fails 3 times → moved to DLQ
                              ↓
              Engineers inspect → fix bug → replay
```

---

### Popular Systems

| System | Type | Best for |
|--------|------|----------|
| **RabbitMQ** | Queue + Pub/Sub | Traditional queuing, complex routing |
| **Apache Kafka** | Distributed log | High-throughput streaming, event sourcing |
| **AWS SQS** | Queue | Simple queuing, serverless |
| **AWS SNS** | Pub/Sub | Fan-out notifications |
| **Redis Streams** | Queue + Pub/Sub | Lightweight, already using Redis |

**Kafka vs. RabbitMQ:**

| | Kafka | RabbitMQ |
|-|-------|----------|
| Model | Distributed log (messages retained) | Traditional queue (deleted after consume) |
| Throughput | Millions of messages/sec | Tens of thousands/sec |
| Message replay | Yes | No |
| Use case | Event streaming, data pipelines | Task queues, RPC |

---

### When to Use a Message Queue

```
Use when:
✓ Work can be done asynchronously
✓ You need to decouple producers from consumers
✓ Traffic is bursty
✓ Multiple services react to the same event
✓ Tasks are long-running (video encoding, PDF generation)

Don't use when:
✗ You need an immediate response
✗ Operations must be transactional with the DB
✗ Simplicity matters — queues add operational complexity
```

---

### Real-World Example

```
[User uploads video]
        ↓
[Upload Service] → saves to S3 → publishes "video_uploaded"
        ↓
[Transcoding Service] → transcodes → publishes "transcoding_complete"
        ↓
[Notification Service] → sends "Your video is ready!"
        ↓
[CDN Invalidation Service] → purges old cache
```
