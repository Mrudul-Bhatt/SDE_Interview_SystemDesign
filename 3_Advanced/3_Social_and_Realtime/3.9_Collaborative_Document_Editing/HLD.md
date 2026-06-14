# Collaborative Document Editing — High-Level Design (System Architecture)

This is the **system-level** view: the production infrastructure behind the OT design
(WebSocket gateway, single-writer-per-document sharding, op log in Cassandra, periodic snapshots).
For the class-level view see [LLD.md](LLD.md).

The single most important constraint shapes everything: **all edits for one document must funnel
through one server** (single-writer per doc), because OT only converges with one agreed total order.

> **How to view the diagrams below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If they don't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). They also render automatically on GitHub.

---

## System Architecture

```mermaid
flowchart TB
    BA["🌐 Browser A (doc-42)<br/>local OT engine + pending queue"]
    BB["🌐 Browser B (doc-42)<br/>local OT engine + pending queue"]
    BC["🌐 Browser C (doc-99)<br/>local OT engine + pending queue"]

    GW["Gateway / Load Balancer<br/>routes by documentId, NOT user"]

    subgraph SVC["Collab Servers — single-writer per document"]
        S42["Collab Server (owns doc-42)<br/>ServerDocument: text · version · opLog<br/>OTEngine.Transform()"]
        S99["Collab Server (owns doc-99)<br/>ServerDocument · OTEngine.Transform()"]
    end

    subgraph STORE["Durable + Ephemeral Stores"]
        CASS["Cassandra (op log)<br/>partition=docId · cluster=version<br/>append-only"]
        SNAP["Snapshot Store (Redis / S3)<br/>docId → text + version, every N ops"]
        PRES["Presence / Awareness (Redis)<br/>cursors · who's online per doc"]
    end

    BA <-->|WebSocket| GW
    BB <-->|WebSocket| GW
    BC <-->|WebSocket| GW
    GW -->|doc-42| S42
    GW -->|doc-99| S99

    S42 -->|"append op"| CASS
    S42 -->|"snapshot every N ops"| SNAP
    S42 -->|"cursors"| PRES
    S42 -.->|"broadcast transformed op to doc-42 clients"| BA
    S42 -.->|"broadcast transformed op to doc-42 clients"| BB

    classDef edge fill:#dbeafe,stroke:#3b82f6,color:#1e3a8a;
    classDef svc fill:#ede9fe,stroke:#8b5cf6,color:#4c1d95;
    classDef dur fill:#dcfce7,stroke:#22c55e,color:#14532d;
    classDef cache fill:#fef3c7,stroke:#f59e0b,color:#78350f;

    class BA,BB,BC,GW edge;
    class S42,S99 svc;
    class CASS dur;
    class SNAP,PRES cache;
```

---

## ① Join a document (open the doc) — WebSocket connect

```mermaid
sequenceDiagram
    participant B as Browser
    participant GW as Gateway
    participant S as Collab Server (doc-42)
    participant SNAP as Snapshot Store
    participant CASS as Cassandra (op log)

    B->>GW: connect (documentId = doc-42)
    GW->>S: route to doc-42's owning server
    S->>SNAP: load latest snapshot
    SNAP-->>S: text + version 1000
    S->>CASS: GetOpsSince(1000)
    CASS-->>S: ops 1001..1042
    S->>S: replay ops, rebuild state at version 1042
    S-->>B: initial state text + version 1042
    S->>S: register browser in doc-42 broadcast session
    Note over B,S: browser seeds its local buffer and starts editing
```

## ② Edit a character — optimistic local apply + server serialize

```mermaid
sequenceDiagram
    participant A as Browser A
    participant S as Collab Server (doc-42)
    participant CASS as Cassandra
    participant B as Browser B

    Note over A: types '!' — apply locally NOW, enqueue pending op
    A->>S: op { Insert, pos, ClientVersion }
    S->>S: GetOpsSince(op.ClientVersion) — concurrent ops
    S->>S: OTEngine.Transform(op, each concurrent op) — chained
    S->>S: ServerDocument.Apply(transformed) — version++
    S->>CASS: append transformed op (durable)
    S-->>A: broadcast transformed op (also serves as ACK)
    S-->>B: broadcast transformed op
    Note over B: ReceiveRemoteOp — double-transform over pending — converge
```

---

## Why each component exists

| Component | Role | Maps to in code |
|-----------|------|-----------------|
| **Client OT engine + pending queue** | Optimistic local apply; integrate remote ops | `EditingClient` + `OTEngine` |
| **Gateway (route by docId)** | Send every edit for a doc to its one owning server | *(prod-only)* |
| **Collab Server (single-writer)** | Serialize concurrent ops into one total order | `CollabServer` |
| **ServerDocument** | Authoritative text + version + op log | `ServerDocument` |
| **OTEngine** | The transform — *same code* on client and server | `OTEngine` |
| **Cassandra (op log)** | Durable append-only ledger, partitioned by docId | `_opLog` → durable |
| **Snapshot store (Redis/S3)** | Periodic text snapshot so joins don't replay from 0 | *(prod-only)* |
| **Presence / Awareness** | Live cursors, who's editing (ephemeral) | *(not in demo)* |

## Key HLD design decisions

- **Single-writer per document — the whole game.** Every edit for one doc must hit one server so
  there's a single agreed total order. The gateway routes **by documentId**, not by user (the
  opposite of the chat system, which routes by user). Two servers on one doc = divergent transform
  chains = corruption.
- **Op log + periodic snapshot** — replaying millions of ops from version 0 on every join is too
  slow. Snapshot the text every N ops; a join loads the snapshot and replays only the tail.
- **OTEngine is identical on client and server** — convergence depends on both computing the *same*
  position adjustments. This is why it's a pure, stateless, dependency-free function.
- **Optimistic local apply** — a 100–300 ms round-trip per keystroke would feel like typing
  telegrams. The client applies instantly and reconciles via double-transform when remote ops arrive.
- **Reconnect = `GetOpsSince(clientVersion)`** — a client that dropped at v=5 and rejoins at v=42
  just fetches ops 6–42 and transforms its pending work; it can even reconnect to a different server
  instance for that doc.
- **OT vs CRDT** — this design uses OT (central server transforms). CRDTs (Yjs / Automerge) trade a
  larger per-character data model for true peer-to-peer merging; OT keeps ops tiny but requires the
  single-writer server.

## Capacity sketch (back-of-envelope)

| Metric | Estimate |
|--------|----------|
| Concurrent docs | ~10 M open documents |
| Editors per doc | typically 1–10 (long tail to ~100s) |
| Op rate | ~5–20 ops/sec per active editor (keystrokes) |
| Op size | ~50 bytes/op → op log grows fast → snapshot + TTL / compaction |
| Snapshot cadence | every ~100–1000 ops or ~30 s per doc |
```
