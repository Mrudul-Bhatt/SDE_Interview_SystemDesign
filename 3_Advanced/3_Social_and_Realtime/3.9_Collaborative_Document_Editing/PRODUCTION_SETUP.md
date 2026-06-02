# Collaborative Document Editing — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on Google Docs, Figma, and Notion).

---

## `Models/TextOp.cs` — Single-character Insert/Delete op

**Problem in production:** Three issues:
1. One op per **character** is enormously chatty — typing a paragraph emits hundreds of ops and hundreds of network frames
2. It models plain text only — no formatting (bold, headings), no rich objects (images, tables, comments)
3. A plain `Position` (an integer index) is fragile under heavy concurrency — every concurrent edit shifts it, which is the whole reason OT transforms exist

**Production replacement: Batched, rich operations (and often a CRDT position model)**

```
- Batch ops: coalesce a burst of keystrokes into one operation/frame
  (e.g. "insert 'hello world' at pos 5" instead of 11 single-char ops)
- Rich model: ops carry attributes (bold, link, heading) and target structured
  nodes, not just a flat character index. Google Docs / Quill use a "Delta"
  format: a sequence of retain/insert/delete with attributes.
- Stable identity: many modern systems replace integer positions with stable,
  unique element IDs so an op references "the character with id X" — that ID
  never shifts, sidestepping positional drift entirely. This is the core idea
  behind CRDTs (see the FeedRanker-style "successor" note below).
```

---

## `Core/OTEngine.cs` — The transform function

**Problem in production:** The transform logic is correct for plain-text insert/delete, but production needs far more: rich-text attribute transforms, undo that respects others' edits, and either a battle-tested OT library or a CRDT.

**Production replacement: A hardened OT library or a CRDT engine**

```
Operational Transformation (OT) path:
  - Google Docs runs server-authoritative OT (Jupiter model): the server holds
    the canonical op order and transforms client ops against it.
  - Production OT must transform ATTRIBUTES too (bold vs italic at the same
    range), handle composite ops, and support transform-based UNDO (your undo
    must skip edits others made after you).
  - Correctness is notoriously hard — teams use well-tested libraries
    (ShareDB / ot.js descendants) rather than hand-rolling.

CRDT path (the modern alternative):
  - Conflict-free Replicated Data Types (Yjs, Automerge; Figma's custom CRDT)
    make merge commutative by construction — no central transform server needed,
    works peer-to-peer and offline-first.
  - Trade-off: simpler convergence, but more memory per document (tombstones for
    deleted items) and trickier to get intent-preserving for rich text.
```

The demo's deterministic `ClientId` tie-break is the same principle both approaches rely on: when two edits genuinely tie, *every* replica must pick the same winner.

---

## `Storage/ServerDocument.cs` — In-memory text + op log

**Problem in production:** The op-log-as-source-of-truth design is exactly right, but an in-memory `List` loses everything on restart and replaying millions of ops to load a document is far too slow.

**Production replacement: Durable op log + periodic snapshots + a real datastore**

```
Op log:    append-only, durable (Spanner / Cassandra / a write-ahead log),
           indexed by (docId, version). This IS the source of truth.

Snapshots: every N ops (or T seconds), persist the materialized document so
           loading = "latest snapshot + replay only the ops since it."
           Without snapshots, a 5-year-old doc would replay millions of ops.

GetOpsSince(version) → still the catch-up primitive, now a ranged DB read,
           serving reconnecting clients and late joiners.
```

This is the same event-sourcing pattern as the Distributed KV Store's LSM log and Kafka's partition log: store the changes, snapshot periodically, derive current state.

---

## `Service/CollabServer.cs` — In-process receive/transform/apply/broadcast

**Problem in production:** The pipeline is correct, but it must run as a network service, hold thousands of live connections per document, enforce single-writer ownership across a cluster, and persist before broadcasting.

**Production replacement: WebSocket service sharded by docId, with durable apply**

```
WebSocket gateway:
  → terminates client connections, does TLS + auth (per-doc access control)

Document logic tier (SHARDED BY docId):
  ReceiveOp pipeline (order preserved from the demo):
    1. transform incoming op against all server ops since client's version
    2. PERSIST the transformed op to the durable log   ← durability before broadcast
    3. apply to the in-memory doc → assign new version
    4. broadcast to all connected clients over their WebSockets

  Single-writer per doc: one shard owns each document, so the op order is
  unambiguous and the transform chain is deterministic — the same single-writer
  constraint as Real-Time Chat (per chat) and the KV Store (per key).
```

- **Connection scale:** a popular doc may have hundreds of live cursors; the server pushes ops + presence (cursors, selections) to all of them.
- **Failover:** if the owning shard dies, another loads the latest snapshot + tail of the op log and resumes — no committed edit lost, because step 2 persisted first.

---

## `Service/EditingClient.cs` — Optimistic local apply + pending queue

**Problem in production:** The optimistic-UI + pending-queue model is exactly how real clients work, but production must survive disconnects, offline editing, and reconnection resync — and render other users' cursors.

**Production replacement: A resilient client with offline buffering and presence**

```
- Optimistic apply: unchanged — local edits show instantly, queued for the server.
- Disconnect tolerance: keep editing offline; the pending queue grows. On
  reconnect, send GetOpsSince(myVersion), transform the backlog both ways
  (the demo's "double transform"), then flush pending ops. This is the same
  reconnect-and-catch-up logic, hardened.
- Acknowledgement & flow control: cap in-flight ops; don't fire a new op until
  the server acks, or pipeline with bounded depth (mirrors the demo's ACK path).
- Presence: broadcast cursor position + selection so collaborators see each
  other's carets in real time (a separate, ephemeral, non-persisted channel).
```

---

## `Program.cs` — Sequential convergence demo scenarios

**Problem in production:** A single process proving convergence; production is many always-on services.

**Production replacement: Deployed services + infrastructure**

```
WebSocket gateway   → connection termination, auth
Doc logic fleet     → sharded by docId, transform + apply + broadcast
Op log store        → durable append-only log (Spanner/Cassandra)
Snapshot store      → periodic materialized documents (blob store)
Presence channel    → cursors/selections (Redis pub/sub, ephemeral)
```

The five demo scenarios map to real concerns: sequential edits (the baseline), the three concurrent cases (insert/insert, insert/delete, same-char-delete → NoOp), and three-client convergence (the N-client guarantee every collaborative editor must hold).

---

## Cross-cutting concerns not modelled in this project

### 1. Presence & awareness

Beyond text, collaborators must see each other's **cursors, selections, and names** live. This rides a separate ephemeral channel (Redis pub/sub) — it's high-frequency but disposable (never persisted, no durability needed). Figma's "multiplayer cursors" are this.

### 2. Persistence, history & undo

```
Version history:  the durable op log enables "view/restore any past version"
                  and named revisions (Google Docs' version history).
Undo/redo:        must be intent-preserving and per-user — your Ctrl-Z undoes
                  YOUR last edit, transformed past everyone else's intervening
                  edits, not the document's globally-last change.
Snapshots:        bound replay cost and back the history UI.
```

### 3. Access control & sharing

Per-document permissions (owner / editor / commenter / viewer), link-sharing, and org policies — enforced at the gateway on every op. A viewer's ops are rejected; a commenter can only add comment-type ops.

### 4. Rich content beyond text

Real documents have images, tables, embeds, and comments anchored to ranges. Each needs its own op types and transform/merge rules. Comment anchors must survive concurrent edits to the text they're attached to (anchor to stable IDs, not offsets).

### 5. Observability

```
op_apply_latency_seconds{quantile="0.99"}   edit → persisted → broadcast latency
active_collaborators_per_doc                 live connections per document
convergence_errors_total                     clients that diverged (must be ~0)
oplog_append_latency_seconds                 durability write latency
reconnect_resync_duration_seconds            catch-up time after a disconnect
```

The headline correctness metric is **zero divergence** — automated checksums of client vs server document state catch any transform bug immediately.

### 6. Scale limits & large documents

Very large docs (a 500-page document, a huge Figma file) are chunked/paginated so a client loads only the visible region. Op routing and snapshots operate per-chunk to keep memory and transform cost bounded.

---

## The Full Production Picture

```
OPEN (a user opens a doc):

WebSocket gateway (TLS, auth, per-doc permission check)
   → doc logic shard owning docId:
        load latest snapshot + replay ops since it → current document
        send document + current version to client
   → subscribe client to the doc's op + presence channels


EDIT (a user types):

EditingClient.LocalInsert()  → apply instantly (optimistic), queue pending, send
   ↓ over WebSocket
Doc logic shard.ReceiveOp():
   1. transform incoming op vs all server ops since client's version
   2. PERSIST transformed op to durable log    ← before broadcast
   3. apply → new version
   4. broadcast to all connected clients
   ↓
each client.ReceiveRemoteOp():
   own op? → ACK, advance version
   else    → double-transform vs pending → apply → converge ✓


PRESENCE (always, ephemeral):
   cursor/selection updates → Redis pub/sub → all collaborators (not persisted)


DISCONNECT / RECONNECT:
   keep editing offline (pending queue grows)
   on reconnect: GetOpsSince(myVersion) → transform backlog both ways → flush pending


Background / always-on:
  Snapshotter        → periodic materialized doc to bound replay cost
  Op log compaction  → archive old ops behind snapshots
  History service    → version browsing/restore from the op log

Observability (always-on):
  Prometheus         → op-apply p99, active collaborators, oplog append latency
  Convergence checks → client vs server checksums (divergence must be ~0)
  Distributed tracing→ edit → transform → persist → broadcast
```

The core logic (operational transformation, the op log as source of truth, optimistic local apply with a pending queue, the double-transform on remote ops, deterministic tie-breaking, single-writer-per-document) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with durable storage, snapshots, WebSocket fan-out, presence, access control, rich content, and full observability. The biggest architectural fork is OT-vs-CRDT: this project teaches OT because it makes *why* concurrent editing is hard explicit, while many modern tools (Figma, Notion-like editors) adopt CRDTs for offline-first, peer-to-peer convergence.
