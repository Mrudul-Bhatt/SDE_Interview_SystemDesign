# Performance Concepts

---

## 5. What is Caching? Where would you place a cache in a system?

### What is Caching?

Caching is storing the result of an **expensive operation** in a faster storage layer so future requests can be served without repeating that operation.

```
Without Cache:
User → App Server → Database (slow, ~10-100ms) → App Server → User

With Cache:
User → App Server → Cache (fast, ~1ms) → App Server → User
                    ↑
         Database only hit on cache miss
```

The fundamental trade-off: **speed vs. freshness**. A cache is always a copy — it can become stale.

---

### Why Caching Works — Locality of Reference

**Temporal locality** — recently accessed data is likely to be accessed again soon.

**Spatial locality** — data near recently accessed data is likely to be accessed soon.

In most real-world systems, **~80% of traffic hits ~20% of data** (Pareto principle). Caching that 20% eliminates most of your database load.

---

### Cache Hit vs. Cache Miss

```
Cache HIT:  Request → Cache → Found! → Return data (fast)
Cache MISS: Request → Cache → Not found → Fetch from DB → Store in Cache → Return data
```

**Hit rate** = (cache hits) / (total requests). A good hit rate is **90%+**.

---

### Where to Place a Cache in a System

```
[Client Browser]          ← Layer 1: Client-side cache
       ↓
[CDN / Edge Cache]        ← Layer 2: CDN cache
       ↓
[Load Balancer]
       ↓
[App Server]
   [In-Process Cache]     ← Layer 3: Application-level cache
       ↓
[Distributed Cache]       ← Layer 4: Shared cache (Redis/Memcached)
       ↓
[Database]
   [Query Cache]          ← Layer 5: DB-level cache
       ↓
[Disk / Storage]
   [OS Page Cache]        ← Layer 6: OS-level cache (automatic)
```

---

#### Layer 1: Client-Side Cache (Browser Cache)

```
Cache-Control: max-age=3600    → browser caches for 1 hour
ETag: "abc123"                 → fingerprint for conditional requests

Browser → "I have this, ETag abc123, still valid?"
Server  → 304 Not Modified → browser uses cached copy
```

**Best for:** Static assets (CSS, JS, images, fonts).

---

#### Layer 2: CDN Cache (Edge Cache)

CDN PoPs cache content close to users globally.

**Best for:** Static assets, public API responses, media files.

---

#### Layer 3: Application-Level Cache (In-Process)

Cache lives **inside the application server's memory**. Extremely fast — no network hop.

```python
_cache = {}

def get_user(user_id):
    if user_id in _cache:
        return _cache[user_id]
    user = db.query("SELECT * FROM users WHERE id = ?", user_id)
    _cache[user_id] = user
    return user
```

**Problem:** Each server has its own cache — inconsistency between servers.

**Best for:** Rarely changing data, config, feature flags.

---

#### Layer 4: Distributed Cache (Redis / Memcached)

A **shared cache** all app servers talk to. One copy regardless of how many servers.

```
[App Server 1] ↘
[App Server 2] → [Redis Cluster] → [Database]
[App Server 3] ↗
```

```python
def get_user(user_id):
    cached = redis.get(f"user:{user_id}")
    if cached:
        return json.loads(cached)
    user = db.query("SELECT * FROM users WHERE id = ?", user_id)
    redis.setex(f"user:{user_id}", 3600, json.dumps(user))
    return user
```

**Redis vs Memcached:**

| | Redis | Memcached |
|-|-------|-----------|
| Data structures | Strings, Lists, Sets, Hashes, Sorted Sets | Strings only |
| Persistence | Optional (RDB/AOF) | None |
| Replication | Yes | No |
| Pub/Sub | Yes | No |
| Use case | Sessions, leaderboards, rate limiting | Pure cache |

---

#### Layer 5: Database Query Cache

**Materialized views** — precomputed query results stored as a table.

```sql
CREATE MATERIALIZED VIEW top_products AS
SELECT product_id, COUNT(*) as order_count
FROM orders
GROUP BY product_id
ORDER BY order_count DESC
LIMIT 100;

REFRESH MATERIALIZED VIEW top_products;
```

**Best for:** Expensive aggregations that don't need real-time accuracy.

---

### Caching Strategies

#### Cache-Aside (Lazy Loading)
Application manages the cache manually. Only load on a miss.

```python
def get_user(user_id):
    user = cache.get(f"user:{user_id}")
    if not user:
        user = db.get_user(user_id)
        cache.set(f"user:{user_id}", user, ttl=3600)
    return user

def update_user(user_id, data):
    db.update_user(user_id, data)
    cache.delete(f"user:{user_id}")   # invalidate, not update
```

**Pro:** Cache only contains requested data
**Con:** First request always slow (cold start)

---

#### Write-Through
Every write goes to cache **and** database simultaneously.

```python
def update_user(user_id, data):
    db.update_user(user_id, data)
    cache.set(f"user:{user_id}", data)
```

**Pro:** Cache always fresh
**Con:** Write latency increases, cache fills with unread data

---

#### Write-Behind (Write-Back)
Write to cache immediately, flush to DB **asynchronously**.

**Pro:** Very fast writes
**Con:** Risk of data loss if cache crashes before flush

---

#### Read-Through
Cache handles misses automatically — app always talks to cache.

```
App → Cache → (miss) → Cache fetches from DB → returns to App
```

**Pro:** Simpler application code
**Con:** Cache provider must know how to fetch from DB

---

### Cache Eviction Policies

| Policy | How it works | Best for |
|--------|-------------|----------|
| **LRU** (Least Recently Used) | Evict entry not accessed for longest time | General purpose — most common |
| **LFU** (Least Frequently Used) | Evict entry accessed fewest times | When popularity matters more than recency |
| **FIFO** | Evict oldest entry | Simple, predictable |
| **TTL-based** | Evict after set time period | Time-sensitive data |
| **Random** | Evict a random entry | Approximates LRU with less overhead |

Redis default is **LRU** (configurable via `maxmemory-policy`).

---

### Cache Invalidation — The Hard Problem

> "There are only two hard things in Computer Science: cache invalidation and naming things." — Phil Karlton

**TTL (Time To Live)** — let entries expire automatically.
```
redis.setex("user:1", 300, data)  # expires in 5 minutes
```

**Event-driven invalidation** — publish event on write → cache deletes affected key.

**Versioned keys** — embed version in key; changing version orphans old entries.
```
cache key: "user:1:v42" → after update → "user:1:v43"
```

---

### Cache Failure Modes

#### Cache Stampede (Thundering Herd)
Popular cache entry expires → thousands of requests hit DB simultaneously.

**Solutions:** Mutex/lock, probabilistic early expiration, stale-while-revalidate.

#### Cache Avalanche
Many entries expire simultaneously → massive DB load.

**Solution:** Add random jitter to TTLs.
```python
ttl = 3600 + random.randint(-300, 300)  # 60min ± 5min jitter
```

#### Cache Penetration
Requests for non-existent data — cache always misses, DB always queried.

**Solutions:** Cache null results with short TTL, Bloom filter.

---

### What to Cache

| Good to cache | Bad to cache |
|--------------|--------------|
| User profile data | Financial balances (must be real-time) |
| Product catalog | Inventory counts (risk of oversell) |
| Rendered HTML fragments | User-specific private data (security risk) |
| API responses from third parties | Frequently updated data |
| Aggregated metrics / leaderboards | One-time requests |
| Configuration / feature flags | Unpredictable access patterns |

---

### Decision Framework

```
1. Is this data read frequently? → Good cache candidate
2. Is this data expensive to compute/fetch? → Good cache candidate
3. Can the system tolerate slightly stale data? → Safe to cache
4. Does it change per user? → Cache with user-scoped key or skip
5. Is it financial/inventory data? → Think twice — consistency matters
6. How often does it change? → Set TTL accordingly
```

---

## 23. What is the difference between Latency and Throughput?

### Definitions

**Latency** — time it takes for a **single request** to complete.

**Throughput** — number of requests a system handles **per unit of time**.

```
Latency:    [Request] ────────────────→ [Response]
                      ←── 200ms ──────→

Throughput: [Req][Req][Req][Req][Req] → processed per second
             ←──────── 1,000 req/s ────────────→
```

---

### The Analogy — Highway

```
Latency   = how long it takes one car to drive from A to B
Throughput = how many cars pass point B per hour
```

A wider highway increases throughput but doesn't make any single car faster. A higher speed limit reduces latency. They are independent dimensions.

---

### Latency in Detail

```
Total Latency = Network + Processing + Queue wait + DB query

Client → [Network: 20ms] → [Queue: 5ms] → [App: 30ms] → [DB: 50ms] → [Back: 20ms]
Total: ~125ms
```

**Real numbers to know:**
```
L1 cache hit:            ~1ns
RAM access:              ~100ns
SSD random read:         ~100μs
Network same datacenter: ~0.5ms
HDD seek:                ~10ms
Network cross-region:    ~50-150ms
Network cross-continent: ~100-300ms
```

#### Percentiles — The Right Way to Measure

```
p50  (median): 50ms
p95:           200ms
p99:           800ms   ← tail latency — matters most
p99.9:         2000ms
```

**p99 tail latency** matters most in distributed systems. One request fanning out to 10 services has latency equal to the slowest service's response.

---

### Throughput in Detail

Measured in RPS, TPS, messages/sec, bytes/sec.

**Little's Law:**
```
L = λ × W
L = concurrent requests in-flight
λ = throughput (req/s)
W = latency (seconds)

Example: latency=0.1s, throughput=1000 req/s → need 100 concurrent requests in-flight
```

---

### The Latency-Throughput Trade-off

**Batching** increases throughput, increases latency:
```
Without: 1 record → 5ms → throughput: 200/sec
With:    batch 100 records → 15ms → throughput: ~6,000/sec
```

**Caching** reduces latency, reduces throughput pressure on DB.

---

### When Each Matters

| Scenario | Optimize For |
|----------|-------------|
| User-facing API | Latency (p99) |
| Background jobs | Throughput |
| Real-time (trading, gaming) | Latency |
| Data pipelines, ETL | Throughput |
| Database OLTP | Both |
| Video streaming | Throughput (bandwidth) |

---

## 24. What is the Circuit Breaker Pattern, and why is it useful?

### The Problem — Cascading Failures

```
[Service A] ──calls──→ [Service B: slow/down]
     ↑
Thread pool fills up → Service A starts timing out too
     ↓
[Service C] → [Service A: failing] → [Service D] → [Service C: failing]

One failing service takes down the entire system
```

---

### What is a Circuit Breaker?

Wraps a remote call and monitors failures. When failures exceed a threshold, **opens** — stops making calls and returns an error immediately.

```
[Service A] → [Circuit Breaker] → [Service B]
                    ↑
             Monitors failures, trips when threshold exceeded
```

---

### The Three States

**Closed (Normal):** Requests flow through. Failures counted.

**Open (Failing):** All requests fail immediately — no calls to downstream service. Returns cached/default response.

**Half-Open (Testing):** After timeout, lets one request through. If it succeeds → Closed. If it fails → Open again.

```
                 failure rate > threshold
    CLOSED ──────────────────────────────→ OPEN
       ↑                                     │
       │                                     │ timeout expires
       └──────────────────────────────── HALF-OPEN
              probe request succeeds
```

---

### Implementation Example

```python
class CircuitBreaker:
    def __init__(self, failure_threshold=5, timeout=60):
        self.failure_count = 0
        self.failure_threshold = failure_threshold
        self.timeout = timeout
        self.state = "CLOSED"
        self.last_failure_time = None

    def call(self, func, *args, **kwargs):
        if self.state == "OPEN":
            if time.time() - self.last_failure_time > self.timeout:
                self.state = "HALF-OPEN"
            else:
                raise Exception("Circuit OPEN — service unavailable")
        try:
            result = func(*args, **kwargs)
            self.failure_count = 0
            self.state = "CLOSED"
            return result
        except Exception as e:
            self.failure_count += 1
            self.last_failure_time = time.time()
            if self.failure_count >= self.failure_threshold:
                self.state = "OPEN"
            raise e
```

---

### Fallback Strategies

```python
def get_product(product_id):
    try:
        return cb.call(product_service.get, product_id)
    except CircuitOpenException:
        return cache.get(f"product:{product_id}")   # stale but better than error

def get_recommendations(user_id):
    try:
        return cb.call(recommendation_service.get, user_id)
    except CircuitOpenException:
        return get_popular_items()   # generic fallback
```

---

### Circuit Breaker vs. Retry

**Retry** handles transient failures. **Circuit Breaker** handles sustained failures.

```
1st-2nd failure → retry (might be transient)
5th failure     → circuit OPENS → fail fast, no more retries
After 30s       → circuit HALF-OPENS → test once
```

---

### Benefits

| Without | With |
|---------|------|
| Cascades to all callers | Failure isolated |
| Threads exhausted | Fails fast, frees resources |
| Retry storms | Service gets recovery time |
| System goes down | Degrades gracefully |

**Libraries:** Resilience4j (Java), Polly (.NET), pybreaker (Python), Istio/Linkerd (infrastructure level).

---

## 25. What is Idempotency, and why does it matter in distributed systems?

### What is Idempotency?

An operation is **idempotent** if performing it multiple times produces the **same result** as performing it once.

```
Idempotent:     SET x = 5 → x is 5 (run again → still 5)
Non-idempotent: INCREMENT x → x is 6 (run again → x is 7)
```

---

### Why It Matters

Networks fail. When a request times out, you can't tell if it failed **before or after** processing.

```
Client → [charge $100] → Server processes → DB updated
                              ↓
                    Response lost in network
                              ↓
Client retries → Server processes AGAIN → Customer charged $200 ✗
```

Without idempotency, retries cause duplicates. With idempotency, retrying is always safe.

---

### HTTP Methods and Idempotency

| Method | Idempotent | Notes |
|--------|-----------|-------|
| GET | Yes | Read-only |
| PUT | Yes | Replace — same result each time |
| DELETE | Yes | Already deleted = same state |
| POST | **No** | Creates new resource each time |
| PATCH | No (usually) | Depends on implementation |

---

### Implementing Idempotency Keys

Client generates a unique ID and sends it with the request. Server deduplicates.

```
POST /payments
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000

Server: processes payment → stores result keyed by idempotency key
Retry with same key → returns cached result → no duplicate charge
```

```python
def process_payment(amount, card, idempotency_key):
    existing = redis.get(f"idem:{idempotency_key}")
    if existing:
        return json.loads(existing)

    result = payment_gateway.charge(amount, card)
    redis.setex(f"idem:{idempotency_key}", 86400, json.dumps(result))
    return result
```

---

### Real-World Usage

**Stripe:** `Idempotency-Key` header — result stored 24 hours, retries return same response.

**AWS SQS:** `MessageDeduplicationId` — duplicates within 5 minutes discarded.

**Database upserts:**
```sql
INSERT INTO orders (id, user_id, total)
VALUES (99, 1, 100.00)
ON CONFLICT (id) DO NOTHING;   -- safe to run multiple times
```

---

### Idempotency vs. Delivery Guarantees

```
At-least-once delivery + Idempotent consumer = effectively exactly-once behavior
                                               (safe, practical, widely used)
```

---

### Design Checklist

```
1. What happens if this runs twice? → If bad, make it idempotent
2. Can clients safely retry? → Add idempotency keys
3. Does it modify a counter/balance? → Use conditional updates
4. Is this a queue consumer? → Always make it idempotent
5. Is this financial? → Idempotency keys are non-negotiable
```

---

### The Three Concepts Together

In interviews, latency, circuit breakers, and idempotency appear together in resilient system design:

```
User clicks "Pay":
  → Circuit Breaker checks if payment service is healthy
  → Calls service → timeout
  → Client retries with same idempotency key (safe!)
  → Circuit Breaker opens after too many timeouts → fallback shown
  → Service recovers → Half-Open → succeeds → Closed
  → p99 latency monitored → alert if SLA breached
```
