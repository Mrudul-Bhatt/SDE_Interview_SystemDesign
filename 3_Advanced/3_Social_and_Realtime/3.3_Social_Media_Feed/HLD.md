# Social Media Feed — High-Level Design (System Architecture)

This is the **system-level** view: the production infrastructure behind the hybrid fan-out
model (Kafka fan-out workers, Redis sorted sets, Cassandra). For the class-level view see
[LLD.md](LLD.md).

> **How to view the diagrams below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If they don't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). They also render automatically on GitHub.

---

## System Architecture

```mermaid
flowchart TB
    Client["🌐 Clients (web / mobile)"]
    GW["API Gateway / Load Balancer<br/>auth · rate-limit · routing"]

    subgraph SVC["Stateless Service Tier"]
        PS["Post Service<br/>(write path)"]
        FS["Feed Service<br/>(read path)"]
        RANK["Ranking Service<br/>engagement × time-decay × affinity"]
    end

    subgraph ASYNC["Async Fan-out Pipeline"]
        K["Kafka<br/>topic: posts"]
        FW["Fan-out Workers<br/>IsCelebrity? skip : push to N feeds"]
    end

    subgraph CACHE["Redis (in-memory)"]
        FC["Feed Cache<br/>ZSET feed:userId<br/>(postId → score)"]
        FG["Follow Graph<br/>followers:id · following:id (SETs)"]
    end

    subgraph DUR["Durable Stores"]
        CASS["Cassandra (PostStore)<br/>posts_by_id · posts_by_author"]
        ML["ML / Affinity Store<br/>per-user author affinity"]
    end

    Client -->|HTTPS| GW
    GW --> PS
    GW --> FS

    PS -->|"① INSERT post"| CASS
    PS -->|"② emit post-created"| K
    K --> FW
    FW -->|"who follows author?"| FG
    FW -->|"③ push (regular authors)"| FC

    FS -->|"④ read pre-built feed"| FC
    FS -->|"which follows are celebs?"| FG
    FS -->|"⑥ pull celeb posts + hydrate"| CASS
    FS -->|"⑤ rank merged set"| RANK
    RANK -->|affinity boosts| ML

    classDef edge fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a;
    classDef svc fill:#ede9fe,stroke:#8b5cf6,color:#4c1d95;
    classDef async fill:#fed7aa,stroke:#ea580c,color:#7c2d12;
    classDef cache fill:#fef3c7,stroke:#f59e0b,color:#78350f;
    classDef dur fill:#dcfce7,stroke:#22c55e,color:#14532d;

    class Client,GW edge;
    class PS,FS,RANK svc;
    class K,FW async;
    class FC,FG cache;
    class CASS,ML dur;
```

---

## ① Publish a post (write path) — `POST /posts`

```mermaid
sequenceDiagram
    participant C as Client
    participant PS as Post Service
    participant CASS as Cassandra
    participant K as Kafka
    participant FW as Fan-out Worker
    participant FG as Follow Graph (Redis)
    participant FC as Feed Cache (Redis)

    C->>PS: POST /posts { content }
    PS->>CASS: INSERT (posts_by_id, posts_by_author)
    CASS-->>PS: OK (durable)
    PS-)K: emit "post created" (async)
    PS-->>C: 201 Created (returns immediately)

    Note over K,FC: fan-out happens in the background
    K->>FW: consume post event
    FW->>FG: IsCelebrity(author)?
    alt regular author
        FG-->>FW: false
        FW->>FG: GetFollowers(author)
        loop each follower
            FW->>FC: ZADD feed:follower score postId
        end
    else celebrity
        FG-->>FW: true
        Note over FW: skip — pulled at read time instead
    end
```

## ② Open the feed (read path) — `GET /feed`

```mermaid
sequenceDiagram
    participant C as Client
    participant FS as Feed Service
    participant FC as Feed Cache (Redis)
    participant FG as Follow Graph (Redis)
    participant CASS as Cassandra
    participant RANK as Ranking Service

    C->>FS: GET /feed?cursor=...
    FS->>FC: ZREVRANGEBYSCORE feed:user (regular follows)
    FC-->>FS: post IDs
    FS->>CASS: GetByIds(...) — hydrate to Posts
    FS->>FG: GetCelebrityFollows(user)
    loop each celebrity
        FS->>CASS: GetRecentByAuthor(celeb)
    end
    FS->>FS: merge + de-dupe (by PostId)
    alt algorithmic
        FS->>RANK: Rank(posts, affinity)
        RANK-->>FS: ranked posts
    else chronological
        Note over FS: sort by CreatedAt desc
    end
    FS-->>C: FeedPage { posts, nextCursor }
```

---

## Why each component exists

| Component | Role | Maps to in code |
|-----------|------|-----------------|
| **API Gateway / LB** | Auth, rate-limit, route to services | *(prod-only)* |
| **Post Service** | Handles writes; persists + emits event | `FanOutService.OnPost` |
| **Feed Service** | Assembles a read page (merge + rank) | `FeedService.GetFeed` |
| **Kafka** | Decouples posting from fan-out; absorbs spikes | *(`OnPost` → worker boundary)* |
| **Fan-out Workers** | Async consumers; push to regular feeds, skip celebs | `FanOutService` push loop |
| **Redis — Feed Cache** | One sorted set per user; instant reads | `FeedCacheRedis` |
| **Redis — Follow Graph** | `followers:` / `following:` SETs; celebrity gate | `FollowGraphRedis` |
| **Cassandra** | Durable posts; by-id + by-author indexes | `PostStoreCassandra` |
| **Ranking Service** | Engagement × time-decay × affinity scoring | `FeedRanker` |
| **ML / Affinity Store** | Per-user author-affinity boosts | `authorAffinity` param |

## Key HLD design decisions

- **Hybrid fan-out** — push for the many (regular authors), pull for the few (celebrities).
  Avoids both the celebrity write-storm (millions of feed writes for one post) *and* the slow
  "compute feed on every read" problem.
- **Fan-out is async via Kafka** — `POST /posts` returns the instant the post is durable;
  spreading it to millions of feeds happens in the background. A slow fan-out never blocks the author.
- **Feed cache stores IDs only** (sorted sets of `postId` + `score`); content is hydrated from
  Cassandra at read time. Keeps millions of feeds in RAM.
- **Cursor pagination via score** — `ZREVRANGEBYSCORE … < cursor` is immune to new posts arriving
  at the top (no skips/duplicates), unlike offset paging.
- **Cap feeds at ~1000 entries** — nobody scrolls past a few hundred; trim the tail on every write
  so memory stays bounded.
- **Ranking decoupled** — chronological vs algorithmic is just a different sort over the same merged
  set; switching modes needs no storage change.

## Capacity sketch (back-of-envelope)

| Metric | Estimate |
|--------|----------|
| Users | ~500 M DAU |
| Posts | ~500 M/day → ~5,800 writes/sec |
| Feed reads | ~50 B/day → ~580 K reads/sec (100:1 read-heavy) |
| Avg fan-out | ~500 followers → ~2.9 M feed-writes/sec (regular authors) |
| Feed cache | 1000 entries × ~30 B × 500 M users ≈ 15 TB Redis (sharded) |
