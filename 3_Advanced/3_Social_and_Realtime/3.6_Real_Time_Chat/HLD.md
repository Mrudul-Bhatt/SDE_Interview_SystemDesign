# Real-Time Chat — High-Level Design (System Architecture)

This is the **system-level** view: the production infrastructure behind the WebSocket + pub/sub
design (WebSocket gateways, Redis Pub/Sub, Cassandra, APNs/FCM). For the class-level view see
[LLD.md](LLD.md).

> **How to view the diagrams below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If they don't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). They also render automatically on GitHub.

---

## System Architecture

```mermaid
flowchart TB
    CA["📱 Client A<br/>(persistent WebSocket)"]
    CB["📱 Client B<br/>(persistent WebSocket)"]
    CC["📱 Client C<br/>(offline — no socket)"]

    LB["Load Balancer (WS-aware, sticky sessions)<br/>pins each socket to one server for its lifetime"]

    subgraph TIER["Stateful Chat Server Tier — each holds its own live WebSockets"]
        S1["Chat Server 1<br/>(holds A's socket)"]
        S2["Chat Server 2<br/>(holds B's socket)"]
        S3["Chat Server 3<br/>(spare / reconnect)"]
    end

    subgraph REDIS["Redis (shared, in-memory)"]
        BUS["Pub/Sub — MessageBus<br/>channel: user:id"]
        REG["Connection Registry<br/>userId → serverId"]
        PRES["Presence (TTL 30s)<br/>presence:id"]
        GRP["Group Store (SETs)<br/>group:id → members"]
    end

    CASS["Cassandra — MessageStore<br/>partition by chatId · durable source of truth"]
    PUSH["Push Gateway<br/>APNs (iOS) / FCM (Android)"]

    CA <-->|WebSocket| LB
    CB <-->|WebSocket| LB
    LB --> S1 & S2 & S3

    S1 & S2 & S3 -->|"PUBLISH / SUBSCRIBE user:id"| BUS
    S1 & S2 & S3 -->|"Register / GetServer"| REG
    S1 & S2 & S3 -->|"Heartbeat (SETEX)"| PRES
    S1 & S2 & S3 -->|"GetMembers"| GRP
    S1 & S2 & S3 -->|"Save / GetUndelivered"| CASS
    S1 & S2 & S3 -.->|"offline → wake device"| PUSH
    PUSH -.->|"notification → reopen app"| CC

    classDef edge fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a;
    classDef svc fill:#ede9fe,stroke:#8b5cf6,color:#4c1d95;
    classDef cache fill:#fef3c7,stroke:#f59e0b,color:#78350f;
    classDef dur fill:#dcfce7,stroke:#22c55e,color:#14532d;
    classDef push fill:#fce7f3,stroke:#ec4899,color:#831843;

    class CA,CB,CC,LB edge;
    class S1,S2,S3 svc;
    class BUS,REG,PRES,GRP cache;
    class CASS dur;
    class PUSH push;
```

---

## ① Connect (open the app) — WebSocket handshake

```mermaid
sequenceDiagram
    participant C as Client
    participant S as Chat Server (Server1)
    participant REG as Connection Registry
    participant PRES as Presence (Redis)
    participant BUS as Pub/Sub (Redis)
    participant CASS as Cassandra

    C->>S: WebSocket handshake
    S->>REG: Register(user → Server1)
    S->>PRES: Heartbeat(user)  (SETEX, 30s TTL)
    S->>BUS: Subscribe("user:id")
    S->>CASS: GetUndelivered(user)
    CASS-->>S: backlog messages
    loop each backlog message
        S->>C: deliver (mark Delivered)
    end
    Note over C,S: socket stays open; client pings Heartbeat every ~10s
```

## ② Send to an ONLINE user — `A → B` (different servers)

```mermaid
sequenceDiagram
    participant A as Client A
    participant S1 as Server1
    participant CASS as Cassandra
    participant REG as Connection Registry
    participant BUS as Pub/Sub
    participant S2 as Server2
    participant B as Client B

    A->>S1: Send(chatId, content)
    S1->>CASS: INSERT (persist FIRST — durability)
    CASS-->>S1: OK
    S1->>REG: where is B?
    REG-->>S1: "Server2"
    S1->>BUS: PUBLISH "user:B"
    BUS->>S2: deliver (Server2 is subscribed)
    S2->>B: push down B's WebSocket  ✓✓ Delivered
    S1-->>A: SendResult { OnlineDelivered: 1, PushSent: 0 }
```

## ③ Send to an OFFLINE user — `A → C`

```mermaid
sequenceDiagram
    participant A as Client A
    participant S1 as Server1
    participant CASS as Cassandra
    participant BUS as Pub/Sub
    participant PUSH as Push Gateway (APNs/FCM)
    participant C as Client C

    A->>S1: Send(chatId, content)
    S1->>CASS: INSERT (message is now safe)
    S1->>BUS: PUBLISH "user:C"
    BUS-->>S1: 0 subscribers → false
    S1->>PUSH: Send(C, preview)  ✓ Sent
    PUSH-->>C: notification (wake device)
    S1-->>A: SendResult { OnlineDelivered: 0, PushSent: 1 }
    Note over C,CASS: later — C reopens app → flow ① drains backlog from Cassandra
```

---

## Why each component exists

| Component | Role | Maps to in code |
|-----------|------|-----------------|
| **Load Balancer (sticky)** | Pin each WebSocket to one server for its lifetime | *(prod-only)* |
| **Chat Servers** | Stateful; hold live WebSocket connections | `ChatServer` (× N) |
| **Redis Pub/Sub** | Cross-server message routing by channel | `MessageBusRedis` |
| **Connection Registry** | Global `userId → serverId` map | `ConnectionRegistryRedis` |
| **Presence (TTL)** | Online status via heartbeat expiry | `PresenceServiceRedis` |
| **Group Store** | Group membership SETs | `GroupStoreRedis` |
| **Cassandra** | Durable message history, partitioned by chatId | `MessageStoreCassandra` |
| **Push Gateway** | Wake offline devices (APNs/FCM) | `PushNotificationServiceAPNsFCM` |

## Key HLD design decisions

- **Persist before deliver** — every message hits Cassandra *first*, so a crash mid-delivery never
  loses it. Delivery is best-effort layered on top of durable storage.
- **Stateful servers, anonymous routing** — servers hold sockets but never know each other's
  addresses. Pub/sub channels (`user:{id}`) decouple them; a new server just subscribes its users
  and starts receiving. No service-discovery mesh.
- **Sticky load balancing** — a WebSocket is a long-lived connection pinned to one server; the LB
  must route the same socket's frames to the same server (unlike stateless HTTP).
- **Presence via TTL, not events** — heartbeats with a 30s Redis TTL self-correct for crashes /
  network drops that fire no disconnect event. Worst-case staleness is bounded.
- **Push is a wake-up, not delivery** — APNs/FCM is fire-and-forget; real delivery always happens
  via the backlog drain on reconnect. Push just tells the device to reconnect.
- **At-least-once + dedup** — backlog drain guarantees delivery; client-generated message IDs
  (Snowflake) let the server discard duplicates from retries.

## Capacity sketch (back-of-envelope)

| Metric | Estimate |
|--------|----------|
| Concurrent connections | ~50 M live WebSockets |
| Per server | ~50–100 K sockets → ~500–1,000 chat servers |
| Messages | ~10 B/day → ~115 K msg/sec sustained |
| Fan-out | group msg × members → one Redis PUBLISH per recipient |
| Storage | ~300 B/msg × 10 B/day ≈ 3 TB/day → Cassandra TTL / archival |
