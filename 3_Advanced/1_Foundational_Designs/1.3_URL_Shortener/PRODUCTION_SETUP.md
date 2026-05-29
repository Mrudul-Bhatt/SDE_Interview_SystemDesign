# URL Shortener — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system.

---

## `Core/IdGenerator.cs` — In-memory atomic counter

**Problem in production:** The counter resets to 100,000 every time the server restarts. If you run 10 servers, each has its own counter — they'll generate the same IDs and collide.

**Production replacement: Twitter Snowflake IDs**
A 64-bit ID composed of:
```
| 41 bits timestamp | 10 bits machine ID | 13 bits sequence |
```
- Timestamp ensures IDs are always increasing
- Machine ID makes each server's IDs unique without coordination
- Sequence handles multiple IDs within the same millisecond

Alternatively: **Redis INCR** — a single Redis instance atomically increments a counter shared by all app servers. Survives restarts, no collisions across servers.

---

## `Core/Base62.cs` — Number to short string encoder

**Problem in production:** Sequential IDs produce sequential codes (`0001abc`, `0001abd`) — easy to enumerate and scrape all URLs in order.

**Production enhancement: Shuffled or salted alphabet**
Shuffle the 62-character alphabet using a secret key so codes appear random even though IDs are sequential. Alternatively use **Base58** (Bitcoin's encoding) which removes visually ambiguous characters: `0` vs `O`, `l` vs `1` vs `I` — reduces user copy-paste errors.

The encoding logic itself is production-ready. Only the alphabet ordering needs hardening.

---

## `Core/LruCache.cs` — In-memory LRU cache

**Problem in production:** Lives inside one server's process. If you have 20 app servers, each has its own separate cache — a popular URL gets cached 20 times independently, and cache evictions on one server don't affect the others. Also wiped on every deploy.

**Production replacement: Redis** with `maxmemory-policy allkeys-lru`
- One shared cache across all app servers
- ~0.5ms read latency
- Survives app server restarts
- Native TTL support (auto-expires records when `ExpiresAt` passes)
- Built-in replication and clustering for high availability

---

## `Models/UrlRecord.cs` — Plain data class

**Problem in production:** Just a C# object — no persistence, no validation, no indexing.

**Production enhancement: PostgreSQL table + ORM mapping**
```sql
CREATE TABLE url_records (
    id           BIGINT PRIMARY KEY,
    short_code   VARCHAR(20) UNIQUE NOT NULL,
    long_url     TEXT NOT NULL,
    created_by   VARCHAR(255),
    created_at   TIMESTAMPTZ DEFAULT NOW(),
    expires_at   TIMESTAMPTZ,
    is_active    BOOLEAN DEFAULT TRUE,
    is_custom    BOOLEAN DEFAULT FALSE
);
CREATE INDEX idx_short_code ON url_records(short_code);
CREATE INDEX idx_created_by ON url_records(created_by);
```
Mapped via **Entity Framework Core** or **Dapper**. The `short_code` unique index replaces the `TryInsert` collision check — the database enforces uniqueness at the hardware level.

---

## `Models/RedirectResult.cs` — Custom HTTP response model

**Problem in production:** The HTTP framework knows nothing about this class — it's just a C# object. A real browser redirect requires actual HTTP headers.

**Production replacement: ASP.NET Core `IActionResult`**
```csharp
return Redirect(longUrl);          // 302 with Location header
return NotFound();                 // 404
return StatusCode(410);            // 410 Gone
```
The `Location` header in the HTTP response is what actually tells the browser where to go. Without it, the browser just sees a 302 status code with no destination.

Additionally: add `Cache-Control: public, max-age=3600` on 302 responses so **CDN edges** (Cloudflare, Fastly) cache the redirect themselves — the most popular short URLs never even reach your servers.

---

## `Service/UrlShortenerService.cs` — Single orchestrator class

**Problem in production:** One class handles both writes (shorten) and reads (redirect). At scale, redirects outnumber shortens by 1000:1. Mixing them means you can't scale the two operations independently.

**Production enhancement: Split into two services (CQRS pattern)**

| Service | Responsibility | Scaling strategy |
|---|---|---|
| `ShortenService` (write) | Generate ID, validate, save to DB | Scale modestly — few writes |
| `RedirectService` (read) | Cache lookup → DB lookup → analytics | Scale aggressively — millions of reads |

Also add:
- `async/await` throughout — every DB and Redis call is I/O, blocking threads wastes memory
- **Rate limiting** — prevent one user from generating millions of short URLs
- **Authentication middleware** — validate API keys before reaching service logic
- **Circuit breaker** (Polly library) — if the DB is slow, fail fast instead of queuing requests

---

## `Storage/UrlRepository.cs` — In-memory dictionary

**Problem in production:** `Dictionary<string, UrlRecord>` lives in RAM, wiped on restart, single-server only, no durability guarantees.

**Production replacement: PostgreSQL + read replicas**
- Primary DB handles writes (shorten, deactivate)
- 2–3 read replicas handle redirect lookups — redirect traffic is read-only and can be distributed across replicas
- **Connection pooling** via PgBouncer — app servers share a pool of DB connections instead of opening a new connection per request

For extreme scale (billions of URLs): **DynamoDB or Cassandra** — distributed key-value stores where `short_code` is the partition key, giving sub-millisecond lookups at any scale with no single point of failure.

---

## `Storage/ClickAnalytics.cs` — In-memory counter with a lock

**Problem in production:** Three critical issues:
1. The `lock` blocks the redirect thread — a slow analytics write slows down every redirect
2. Data is lost on restart
3. Single-server only — counters on server 2 don't know about clicks on server 1

**Production replacement: Three-layer pipeline**

```
Redirect request
    ↓
Publish click event to Kafka topic   ← async, non-blocking, ~1ms
    ↓ (separate consumer process)
Redis INCR for real-time counters    ← live "clicks in last hour" dashboard
    ↓ (hourly batch job)
ClickHouse for analytics queries     ← "clicks by country last 30 days"
```

- **Kafka** decouples analytics from the redirect path entirely — a Kafka backlog during a traffic spike doesn't slow down redirects
- **Redis INCR** is atomic across all servers with no locks
- **ClickHouse** is a columnar database built for analytical queries like `GROUP BY country` over billions of rows in milliseconds

---

## The Full Production Picture

```
Browser → CDN (Cloudflare)
            ├─ Cache HIT  → return redirect instantly, never hits your servers
            └─ Cache MISS → App servers (ASP.NET Core, horizontal scale)
                                ├─ Redis (LRU cache, ~0.5ms)
                                │       ├─ HIT  → 302 redirect
                                │       └─ MISS → PostgreSQL read replica
                                │                       └─ 302 / 404 / 410
                                └─ Kafka → ClickHouse (async analytics)
```

The core code logic (Base62 encoding, LRU eviction, soft delete, status code semantics) doesn't change — only the infrastructure backing each class changes.
