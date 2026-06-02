# Collaborative Document Editing — Beginner Summary

## What is this project?

A **Collaborative Document Editor** (like Google Docs, Notion, or Microsoft Office Online) lets many people type into the *same document at the same time* and somehow all end up seeing the exact same text — even though their keystrokes arrive in different orders, over a laggy network.

Imagine two people editing the same sentence on a whiteboard, but they're in different cities and can only see each other's changes a half-second late. Alice inserts a word at the start; at the same moment Bob deletes a word in the middle. If they naively apply each other's edits, their whiteboards drift apart — Alice's says one thing, Bob's says another. The whole challenge is making sure that **no matter what order edits happen, everyone converges to identical text.**

The algorithm that makes this work is called **Operational Transformation (OT)** — the same technique Google Docs uses. This project is a working OT implementation.

---

## The Core Problem — Why Naive Editing Breaks

Say the document is `"Hello"` and two people edit simultaneously:

```
Doc = "Hello"  (both start here, at version 0)

Alice inserts 'X' at position 5  → she sees "HelloX"
Bob   inserts 'Z' at position 2  → he sees "HeZllo"
```

Now their edits cross the network. Alice receives Bob's "insert Z at 2" and Bob receives Alice's "insert X at 5". If each just blindly applies the other's operation:

```
Alice applies "insert Z at 2" to "HelloX" → "HeZlloX"   ✓ (happens to be right)
Bob   applies "insert X at 5" to "HeZllo" → "HeZllXo"   ✗ (X landed in the wrong place!)
```

Bob's document is now **wrong** — because his "insert Z at 2" already shifted every character after position 2 one slot to the right. Alice's "insert X at position 5" was calculated against the *original* `"Hello"`, but Bob's document has changed underneath it. Position 5 doesn't mean the same thing anymore.

**This positional drift is the entire problem.** OT solves it by *transforming* each incoming operation to account for the edits that happened concurrently.

---

## The Core Idea — Operational Transformation

The key function is `Transform(op1, op2)`: it takes `op1` and adjusts it *as if `op2` had already been applied*.

```
Bob's pending op:  "insert X at 5"   (calculated against original "Hello")
Concurrent op:     "insert Z at 2"   (Bob just applied this)

Transform("insert X at 5", "insert Z at 2"):
   → Z was inserted at position 2, which is BEFORE position 5
   → so everything after position 2 shifted right by one
   → adjust X's target: position 5 + 1 = position 6
   → returns "insert X at 6"

Bob applies "insert X at 6" to "HeZllo" → "HeZlloX"   ✓ CORRECT!
```

Now Alice and Bob both have `"HeZlloX"`. **They converged.** That positional adjustment — "an insert before me means I shift right by one" — is the essence of OT.

---

## The Files — What Each One Does

### `Models/TextOp.cs` — The Atomic Edit

Every keystroke becomes one `TextOp`:

| Field | Meaning |
|---|---|
| `Kind` | `Insert`, `Delete`, or `NoOp` |
| `Position` | Where in the text the edit happens |
| `Character` | The character to insert (for inserts) |
| `ClientId` | Who made the edit (`"alice"`) — used for tie-breaking |
| `ClientVersion` | The document version the client was at *when* they made the edit |

**Why `ClientVersion` matters:** It tells the server *"this edit was based on document version N."* The server then knows exactly which concurrent operations (everything that happened after version N) it must transform this op against. Without it, the server couldn't tell a fresh edit from a stale one.

**What's a `NoOp`?** It's the "do nothing" result. If Alice and Bob *both* delete the same character, the second delete must become a NoOp — otherwise it would delete an *innocent neighbouring character* by mistake. NoOp is how OT says "this operation was cancelled out by a concurrent one."

---

### `Core/OTEngine.cs` — The Heart of the System

The `Transform(op1, op2)` function, handling all **four combinations** of edit types:

```
Insert vs Insert:  if op2 inserted before me → I shift right (+1)
                   if same position → tie-break by ClientId (deterministic!)

Insert vs Delete:  if op2 deleted before me → a slot freed up → I shift left (-1)

Delete vs Insert:  if op2 inserted at/before me → I shift right (+1)

Delete vs Delete:  if op2 deleted before me → I shift left (-1)
                   if op2 deleted the SAME position → I become a NoOp
                   (the character's already gone; don't delete its neighbour)
```

**Why tie-breaking by `ClientId` is critical:** When Alice and Bob both insert at *exactly* the same position, someone has to go first — and **every participant must agree on the order**, or they'd diverge. The engine compares client IDs alphabetically (`string.Compare`) so the decision is identical on every machine, every time. It doesn't matter *who* wins; it only matters that everyone makes the *same* choice. This determinism is what guarantees convergence.

---

### `Storage/ServerDocument.cs` — The Source of Truth

Holds the authoritative document, but the real treasure is the **op log** — an append-only record of every operation ever applied, each tagged with its version number.

**The op log IS the source of truth; the text is just a cache derived from replaying the log.** This is the same idea as an LSM tree's write-ahead log or Kafka's partition log: the sequence of changes is primary, the current state is derivable.

```
GetOpsSince(version=42)  →  "give me every op after version 42"
```

This is what lets a client catch up after a network hiccup: *"I'm at version 42, give me everything since then,"* apply them (with transforms), and you're current again. In production this log lives in Cassandra with periodic snapshots, so you don't replay millions of ops on every document open.

---

### `Service/CollabServer.cs` — Receive, Transform, Apply, Broadcast

The server is the **single referee** that decides the official order of edits. Its receive pipeline:

```
ReceiveOp(op, clientId):
  1. serverOpsSince = GetOpsSince(op.ClientVersion)
        ← every edit that happened after the client's base version
  2. for each serverOp:
        op = Transform(op, serverOp)
        if op became NoOp: stop early (it was cancelled by a concurrent edit)
  3. doc.Apply(op)  → assigns a new official version number
  4. broadcast the transformed op to ALL clients
```

The server transforms the incoming op against everything that happened since the client last synced — so by the time it's applied, it's correctly positioned against the document's *current* state.

**Why a single server per document?** Having one referee means there's exactly one authoritative order of operations. In production, the system is **sharded by document ID** so one server owns each document — that single-writer constraint is what makes the transform chain deterministic and the math tractable.

---

### `Service/EditingClient.cs` — Optimistic Local Editing

This is the per-user editor, and it implements the trick that makes typing feel **instant**: *optimistic UI*.

**The problem:** if every keystroke had to round-trip to the server before appearing on screen, typing would feel laggy (100ms+ per character). Unbearable.

**The solution:** apply edits *locally and immediately*, then send them to the server in the background. Track which ops haven't been confirmed yet in a **pending queue**.

```
LocalInsert('X'):
  → apply to local text instantly (zero latency, feels great)
  → add to _pendingOps queue (not yet confirmed by server)
  → send to server
```

When a **remote op** arrives from the server, two cases:

```
1. It's MY OWN op echoed back (an ACK):
     → dequeue from pending, advance my version. Done.

2. It's SOMEONE ELSE'S op:
     → "double transform":
        a. transform the incoming op against each of my pending ops
           (so it fits MY current local text, which has un-synced edits)
        b. transform each of my pending ops against the incoming op
           (so my still-unconfirmed edits stay aligned with the server)
     → apply the adjusted incoming op to local text
```

That **double transform** is the subtle part. The incoming op needs adjusting because the client's local text contains pending edits the server hasn't seen yet. And the pending edits themselves need adjusting because the server's state just moved. Keeping both sides consistent is what lets the client stay in sync as edits fly in both directions.

---

### `Program.cs` — The Demo

Runs 5 scenarios, each proving convergence under harder conditions:

| Scenario | What it proves |
|---|---|
| 1 | Sequential edits — no conflict, the easy baseline |
| 2 | Concurrent insert/insert — Alice's 'X' and Bob's 'Z' both land correctly |
| 3 | Concurrent insert/delete — an insert and a delete at once still converge |
| 4 | Both delete the same char — the second delete becomes a NoOp (deleted exactly once) |
| 5 | Three clients at once — insert + insert + delete, all concurrent, all converge |

Every scenario ends by asserting `All converged: true` — the whole point of OT.

---

## The Big Picture — How It All Fits Together

```
TYPING (Alice presses 'X'):

EditingClient("alice").LocalInsert(5, 'X')
   → apply to local text INSTANTLY → "HelloX"   (optimistic, zero latency)
   → enqueue in _pendingOps
   → send TextOp{Insert, pos=5, clientVersion=0} to server


SERVER PROCESSES IT:

CollabServer.ReceiveOp(op, "alice")
   → GetOpsSince(0) = [Bob's concurrent op, ...]   (anything since v0)
   → transform Alice's op against each one
   → ServerDocument.Apply(transformed)  → version becomes 1
   → broadcast transformed op to ALL clients


CLIENTS RECEIVE THE BROADCAST:

alice.ReceiveRemoteOp(op, v=1)
   → "that's my own op!" → dequeue pending, set version=1  (ACK)

bob.ReceiveRemoteOp(op, v=1)
   → "someone else's op" → double-transform against bob's pending ops
   → apply to bob's local text
   → bob now matches alice ✓


CONVERGENCE GUARANTEE:

Because the server fixes ONE official order, and every client transforms
ops the SAME deterministic way (with ClientId tie-breaking), every client
ends at byte-for-byte identical text — regardless of network timing.
```

---

## Why This Design Is Used Everywhere

- **Operational Transformation** is the algorithm behind Google Docs, Etherpad, and older versions of collaborative editors — the canonical solution to real-time multi-user editing.
- **Optimistic local apply** is why typing in Google Docs feels instant even on a slow connection — your keystrokes never wait for the server.
- **The op log as source of truth** (text is just a cache) is the same event-sourcing pattern as Kafka and LSM databases — store the changes, derive the state.
- **Deterministic tie-breaking** is the unsung hero of every distributed convergence system — when there's a genuine tie, the rule isn't "pick the right one," it's "make sure everyone picks the *same* one."
- **Single-writer-per-document sharding** is how the system scales to millions of documents while keeping each one's edit order simple and correct — partition the problem so each shard is single-threaded logic.
- **The modern successor, CRDTs** (Conflict-free Replicated Data Types, used by Figma and newer tools) solves the same convergence problem with different math — but OT remains the clearest way to *understand* why concurrent editing is hard and how transformation fixes it.
