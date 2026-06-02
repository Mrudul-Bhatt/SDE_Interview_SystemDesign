# Technical Glossary — Social and Realtime

Terms found across the **Social Media Feed**, **Real-Time Chat**, and **Collaborative Document Editing** projects.

These center on two themes that define real-time social systems: **getting the right data to the right user fast** (fan-out, caching, pub/sub) and **keeping many users consistent despite concurrency and unreliable networks** (operational transformation, delivery guarantees, presence).

---

## Feed Generation & Fan-out — *Social Media Feed*

| Term | Where | One-line meaning |
|---|---|---|
| **Fan-out on Write (push)** | FanOutService | Copy a new post into every follower's feed cache at post time → instant reads |
| **Fan-out on Read (pull)** | FeedService | Fetch an author's posts fresh at read time instead of pre-pushing them |
| **Hybrid Fan-out** | FanOutService + FeedService | Push for regular users, pull for celebrities — the core feed-system trade-off |
| **The Celebrity Problem** | FollowGraph.IsCelebrity | A 100M-follower post can't be pushed to 100M feeds → must be pulled instead |
| **Follow Graph** | FollowGraph | Who-follows-whom, stored as two indexes (followers + following) for O(1) both ways |
| **Feed Cache** | FeedCache | Per-user pre-computed list of post IDs (mirrors a Redis sorted set) |
| **Backfill** | FanOutService.BackfillOnFollow | Retroactively load a newly-followed author's recent posts into your feed |
| **Hydration** | FeedService | Turning cached post IDs into full post objects from the post store |

---

## Ranking & Pagination — *Social Media Feed*

| Term | Where | One-line meaning |
|---|---|---|
| **Time-Decay Ranking** | FeedRanker | Score = engagement ÷ age^gravity — popular-but-old sinks, fresh surfaces |
| **Gravity** | FeedRanker | The exponent controlling how fast old posts lose rank (HN≈1.8, Twitter≈2.0) |
| **Affinity Boost** | FeedRanker | Amplify posts from authors you interact with — lightweight personalization |
| **Engagement Score** | Post.EngagementRaw | Weighted interaction sum (shares > comments > likes) |
| **Cursor-Based Pagination** | FeedPage, FeedCache | Page by "items older than timestamp X" — stable when new items arrive |
| **Offset Pagination (anti-pattern)** | FeedPage (comment) | Page by "skip N" — drifts and repeats items when the list shifts |

---

## Real-Time Messaging — *Real-Time Chat*

| Term | Where | One-line meaning |
|---|---|---|
| **Publish/Subscribe (Pub/Sub)** | MessageBus | Senders publish to a channel; whoever subscribed receives it — no direct addressing |
| **WebSocket** | ChatServer (concept) | A persistent two-way connection so the server can push messages to the client |
| **Connection Registry** | ConnectionRegistry | userId → serverId map so any server can find where a user is connected |
| **Cross-Server Routing** | ChatServer + MessageBus | Delivering to a user on another server by publishing to their channel |
| **Presence** | PresenceService | Tracking who is currently online vs offline |
| **Heartbeat + TTL** | PresenceService | Periodic "I'm alive" pings; absence for 30s auto-marks the user offline |
| **Last Seen** | PresenceService.GetLastSeen | Timestamp of a user's most recent activity, shown when they're offline |
| **Push Notification** | PushNotificationService | Wake an offline user's app via APNs/FCM (the message itself lives in storage) |
| **Offline Backlog Drain** | ChatServer.ConnectUser | Delivering messages that arrived while a user was disconnected, on reconnect |
| **Group Fan-out** | ChatServer.GetRecipients | One send delivered to every member of a group chat (minus the sender) |
| **Persist-then-Deliver** | ChatServer.Send | Save to storage before attempting delivery so a crash never loses a message |

---

## Delivery Guarantees & Ordering — *Real-Time Chat*

| Term | Where | One-line meaning |
|---|---|---|
| **At-Least-Once Delivery** | ChatServer + MessageStore | Retry until acknowledged; a message may arrive twice but never zero times |
| **Idempotent Receive** | MessageStore (EventId / dedup) | Re-processing the same message is harmless — the receiver dedupes by ID |
| **Delivery Receipt** | ChatMessage.Status | The Sent → Delivered → Read progression (✓ → ✓✓ → ✓✓ read) |
| **Sequence Number** | ChatMessage.SequenceNumber | Monotonic per-chat counter that breaks timestamp ties for stable ordering |

---

## Collaborative Editing & Convergence — *Collaborative Document Editing*

| Term | Where | One-line meaning |
|---|---|---|
| **Operational Transformation (OT)** | OTEngine | Adjust concurrent edits against each other so all clients converge |
| **Transform** | OTEngine.Transform | Rewrite op1 as if op2 already happened (e.g. shift a position +1) |
| **Convergence** | OTEngine, EditingClient | The guarantee that all clients end with byte-identical text |
| **Positional Drift** | (the core problem) | A position computed on old text being wrong after a concurrent edit |
| **Optimistic UI / Local Apply** | EditingClient.LocalInsert | Apply edits instantly on screen, sync to server in the background |
| **Pending Ops Queue** | EditingClient._pendingOps | Local edits not yet acknowledged by the server |
| **Double Transform** | EditingClient.ReceiveRemoteOp | Transform incoming op vs pending AND pending vs incoming, to stay aligned |
| **Op Log (source of truth)** | ServerDocument | Append-only record of every operation; the text is just a derived cache |
| **NoOp** | TextOp.Noop | An operation nullified by a concurrent one (e.g. both deleted the same char) |
| **Deterministic Tie-Breaking** | OTEngine (ClientId compare) | When two edits tie, all participants pick the same winner by a fixed rule |
| **Single-Writer per Document** | CollabServer (comment) | One server owns each doc so the operation order is unambiguous |
| **CRDT** | (mentioned as successor) | Conflict-free Replicated Data Type — OT's modern alternative (Figma, Yjs) |

---

## Concurrency & Consistency (shared themes)

| Term | Where | One-line meaning |
|---|---|---|
| **Event Sourcing** | OT op log, chat message store | Store the sequence of changes as truth; derive current state from it |
| **Eventual Consistency** | Feed cache, OT convergence | Replicas/views temporarily differ but provably converge to the same state |
| **Atomic Operation** | MessageStore (`Interlocked.Increment`) | A single uninterruptible step — used here for collision-free message IDs |
| **Optimistic Concurrency** | EditingClient, OT | Assume no conflict, apply locally, reconcile if a concurrent change appears |

---

## Production Infrastructure (mentioned in comments)

| Term | Where | One-line meaning |
|---|---|---|
| **Redis** | Feed cache, follow graph, presence, registry | In-memory store for sorted sets, hashes, sets, and TTL keys |
| **Redis Sorted Set** | FeedCache | Members (post IDs) ranked by a score (timestamp) — ideal for feeds |
| **Redis Pub/Sub** | MessageBus | The real cross-server message router this project simulates |
| **Cassandra** | PostStore, MessageStore | Wide-column DB for history queried as "items in X before time T, newest first" |
| **APNs / FCM** | PushNotificationService | Apple / Google push gateways that wake offline mobile apps |
| **WebSocket** | ChatServer, EditingClient | The persistent connection real-time clients hold open to the server |
