# URL Shortener — High-Level Design (System Architecture)

This is the **system-level** view: the production infrastructure (load balancers, Redis,
Postgres, Kafka, CDN). For the class-level view see [LLD.md](LLD.md); for the
code-component view see [Architecture.excalidraw](Architecture.excalidraw).

> **How to view the diagrams below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If they don't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). They also render automatically on GitHub.

---

## System Architecture

```mermaid
flowchart TB
    Client["🌐 Clients<br/>browsers · mobile · API consumers"]

    CDN["CDN (CloudFront)<br/>caches 302 redirects at edge"]
    LB["Load Balancer (ALB / Nginx)<br/>TLS · health checks · rate limiting"]

    subgraph APP["Stateless App Tier — scales horizontally"]
        N1["API Node"]
        N2["API Node"]
        N3["API Node"]
    end

    subgraph CACHE["Caching & ID"]
        RID["Redis — INCR<br/>unique ID counter"]
        RC["Redis Cluster<br/>allkeys-lru cache"]
    end

    subgraph DB["Source of Truth"]
        PG["PostgreSQL primary<br/>short_code PK · long_url · ttl"]
        RR["Read Replicas (1..N)"]
    end

    subgraph ANALYTICS["Analytics (async, off hot path)"]
        K["Kafka<br/>click events"]
        CH["ClickHouse<br/>analytics warehouse"]
    end

    Client -->|HTTPS| CDN
    CDN -->|cache miss| LB
    LB --> N1 & N2 & N3

    N1 & N2 & N3 -->|"NextId()"| RID
    N1 & N2 & N3 -->|"GET / SET"| RC
    N1 & N2 & N3 -->|"INSERT (write)"| PG
    N1 & N2 & N3 -->|"SELECT (cache miss)"| RR
    N1 & N2 & N3 -.->|"click event"| K

    PG -->|async replication| RR
    K --> CH

    classDef edge fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a;
    classDef app fill:#ede9fe,stroke:#8b5cf6,color:#4c1d95;
    classDef cache fill:#fef3c7,stroke:#f59e0b,color:#78350f;
    classDef db fill:#dcfce7,stroke:#22c55e,color:#14532d;
    classDef analytics fill:#fce7f3,stroke:#ec4899,color:#831843;

    class Client,CDN,LB edge;
    class N1,N2,N3 app;
    class RID,RC cache;
    class PG,RR db;
    class K,CH analytics;
```

---

## ① Shorten (write path) — `POST /shorten`

```mermaid
sequenceDiagram
    participant C as Client
    participant LB as Load Balancer
    participant API as API Node
    participant RID as Redis (INCR)
    participant PG as PostgreSQL
    participant RC as Redis (cache)

    C->>LB: POST /shorten { longUrl }
    LB->>API: route request
    API->>RID: INCR counter
    RID-->>API: unique id (e.g. 100042)
    API->>API: Base62.Encode(id) → "q0Cs"
    API->>PG: INSERT (short_code, long_url, ttl)
    PG-->>API: OK (unique constraint held)
    API->>RC: SET short_code → record (pre-warm)
    API-->>C: 201 { https://sho.rt/q0Cs }
```

## ② Redirect (read path) — `GET /{code}`  ·  ~99% of traffic

```mermaid
sequenceDiagram
    participant C as Client
    participant CDN as CDN
    participant API as API Node
    participant RC as Redis (cache)
    participant RR as Postgres Replica
    participant K as Kafka

    C->>CDN: GET /q0Cs
    alt edge cache hit
        CDN-->>C: 302 → longUrl  (never touches backend)
    else edge miss
        CDN->>API: forward
        API->>RC: GET q0Cs
        alt cache hit
            RC-->>API: record
        else cache miss
            API->>RR: SELECT by short_code
            RR-->>API: record
            API->>RC: SET q0Cs (back-fill)
        end
        API-)K: click event (async, non-blocking)
        API-->>C: 302 → longUrl   (or 404 / 410)
    end
```

---

## Why each component exists

| Component | Role | Maps to in code |
|-----------|------|-----------------|
| **CDN** | Cache 302s at the edge; absorb read spikes for viral links | *(prod-only)* |
| **Load Balancer** | Spread load, TLS termination, rate-limit | *(prod-only)* |
| **API Nodes** | Stateless app servers; scale horizontally | `UrlShortenerService` |
| **Redis (ID gen)** | Atomic `INCR` for collision-free unique IDs | `IdGenerator` |
| **Redis (cache)** | `allkeys-lru`; serve hot redirects without a DB hit | `LruCache` |
| **PostgreSQL** | Durable source of truth; unique constraint on `short_code` | `UrlRepository` |
| **Read replicas** | Scale read throughput on cache miss | *(single store in demo)* |
| **Kafka → ClickHouse** | Async analytics, kept off the redirect hot path | `ClickAnalytics` |

## Key HLD design decisions

- **Read : Write ≈ 100 : 1** → optimize hard for reads: CDN + cache-first, replicas for the rest.
- **Cache-aside pattern** → app checks Redis, falls back to Postgres, back-fills cache.
  Pre-warmed on write so the very first redirect is already a hit.
- **Analytics is async** → clicks publish to Kafka and never block the 302. A redirect stays
  fast even if the analytics pipeline is down.
- **Centralized ID generation** (Redis `INCR`) → guarantees global uniqueness. At extreme
  scale, shard the counter or switch to a Snowflake-style generator.
- **Stateless app tier** → any node serves any request; all state lives in Redis / Postgres,
  so scaling out is just adding nodes behind the load balancer.

## Capacity sketch (back-of-envelope)

| Metric | Estimate |
|--------|----------|
| New URLs | ~100 M / day → ~1,160 writes/sec |
| Redirects | ~10 B / day → ~115 K reads/sec (100:1 ratio) |
| Code space | Base62, 7 chars = 62⁷ ≈ 3.5 trillion codes (~96 yrs of headroom) |
| Storage | ~500 bytes/row × 100 M/day ≈ 50 GB/day → cold-tier old links |
