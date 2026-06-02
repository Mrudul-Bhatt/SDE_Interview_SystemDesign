# Real-Time Chat — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on WhatsApp, Slack, Messenger, and Discord).

---

## `Models/ChatMessage.cs` — Plain C# object with a string body

**Problem in production:** Three issues:
1. C# objects can't be persisted or sent over the wire as-is
2. A single auto-increment `SequenceNumber` doesn't work across a distributed cluster — there's no global counter
3. No encryption — the message body is plaintext, unacceptable for private messaging

**Production replacement: Protobuf record + per-chat monotonic IDs + E2E encryption**

The wire/storage format becomes a compact Protobuf message:

```protobuf
message ChatMessage {
  string message_id  = 1;   // globally unique (Snowflake or KSUID)
  string chat_id     = 2;
  string sender_id   = 3;
  bytes  ciphertext  = 4;   // encrypted body, not plaintext
  int64  seq         = 5;   // per-chat monotonic sequence
  int64  sent_at     = 6;
  DeliveryStatus status = 7;
}
```

- **Message ID:** a Snowflake ID (timestamp + machine + counter) or KSUID — globally unique without a central counter, and roughly time-sortable.
- **Per-chat sequence:** assigned by the single server/partition that owns the chat (see `ChatServer`), guaranteeing gap-free ordering within a conversation.
- **End-to-end encryption:** WhatsApp/Signal use the **Signal Protocol** (Double Ratchet) so even the server can't read message bodies. The server routes ciphertext it cannot decrypt.

---

## `Models/SendResult.cs` — `{ OnlineDelivered, PushSent }` receipt

**Problem in production:** Fine as a synchronous summary, but real delivery is asynchronous and per-recipient — there's no single moment when you know the final counts, especially in large groups.

**Production replacement: Per-recipient delivery tracking + acks**

Delivery state is tracked **per recipient**, not as an aggregate count:

```
message_receipts (message_id, recipient_id, status, updated_at)
  status: SENT → DELIVERED (device ack) → READ (user opened)
```

The sender's UI updates as individual `DELIVERED`/`READ` acks stream back over the connection. In a 500-person group, you don't return "delivered to N" synchronously — you show "delivered" once the first device acks, and aggregate read counts lazily.

---

## `Core/ConnectionRegistry.cs` — `userId → serverId` dictionary

**Problem in production:** This shared map must be consistent across thousands of chat servers, survive crashes, and handle users with **multiple devices** (phone + laptop + tablet) connected at once.

**Production replacement: Redis with TTL, keyed per device**

```
HSET  presence:{userId}  {deviceId}  {serverId}    ← one user, many devices
EXPIRE presence:{userId} {ttl}                      ← auto-cleanup if a server crashes
```

- **Per-device, not per-user:** a message fans out to *all* of a user's connected devices.
- **TTL-based cleanup:** if a chat server dies, it can't deregister its users — the TTL expires the stale entries automatically (the same pattern as presence).
- **Refreshed on heartbeat:** each live connection refreshes its TTL, so only genuinely dead connections expire.

---

## `Core/MessageBus.cs` — In-process pub/sub

**Problem in production:** This is the heart of cross-server delivery and the most-load-bearing component. An in-process `Dictionary` of handlers obviously can't route between machines.

**Production replacement: Redis Pub/Sub → or a sharded routing tier at scale**

The direct mapping is **Redis Pub/Sub**: each chat server subscribes to `user:{userId}` channels for its connected users; senders `PUBLISH` to those channels.

```
Server2 (holds Bob):  SUBSCRIBE user:bob
Server1 (sends):      PUBLISH  user:bob  {message}
   → Redis delivers to Server2 → Server2 pushes down Bob's WebSocket
```

**At WhatsApp/Slack scale, Redis Pub/Sub alone isn't enough** (it broadcasts to all subscribers of a channel and doesn't persist). Production evolves to:
- A **routing service** that looks up the recipient's server in the registry and forwards via direct RPC (gRPC), or
- A **Kafka-backed** per-user partition for durability + ordering, with servers consuming their users' partitions, or
- A purpose-built fan-out tier (Slack's "message server" + "gateway" split; Discord's Elixir/Erlang sessions).

The boolean "was anyone subscribed?" return becomes a registry lookup: recipient online → route; offline → persist + push.

---

## `Core/PresenceService.cs` — Heartbeat dictionary with 30s TTL

**Problem in production:** The heartbeat + TTL idea is exactly right, but an in-memory dictionary can't be shared, and presence fan-out (telling all your contacts you came online) is its own scaling problem.

**Production replacement: Redis TTL keys + presence fan-out via pub/sub**

```
SET presence:{userId} online EX 30      ← refreshed by each heartbeat
  → key present  = online
  → key expired  = offline (no cleanup code; Redis handles it)
last_seen:{userId} → written on heartbeat and disconnect
```

**The hard part is presence fan-out:** when you come online, everyone who has your chat open wants to see it. Production approaches:
- **Subscribe-on-view:** only compute/stream presence for contacts whose chat is currently open (Slack does this) — you don't broadcast to all 2,000 contacts.
- **Debounced/coarse status:** "online / last seen recently / offline" buckets instead of exact timestamps, to cut update volume.
- WhatsApp deliberately makes presence cheap by showing "last seen" lazily rather than a constant live indicator for everyone.

---

## `Core/PushNotificationService.cs` — In-memory log of notifications

**Problem in production:** Real push goes through platform gateways with device-token management, batching, and strict reliability needs.

**Production replacement: APNs / FCM with a token registry and a dedicated worker**

```
Offline recipient → enqueue push job
Push worker:
  → look up the user's device tokens (APNs for iOS, FCM for Android, Web Push)
  → render a notification (respect mute settings, locale, badge counts)
  → send to the gateway; handle token invalidation (uninstalls) on failure
```

- **Notification ≠ message:** the real message is already durably stored; push just wakes the app (exactly as the demo notes). For E2E-encrypted apps, the push payload contains no message content — the app fetches and decrypts on wake.
- **Badge counts & mute:** production tracks per-chat mute and unread counts so it doesn't notify for muted threads.
- **Reliability:** push gateways are best-effort, so the app *always* re-syncs missed messages on open (the backlog drain) — push is an optimization, not the source of truth.

---

## `Storage/GroupStore.cs` — Member set per group

**Problem in production:** Fine for small groups, but production groups (Slack channels, WhatsApp/Discord servers with tens of thousands of members) need roles, pagination, and a different fan-out strategy.

**Production replacement: A membership service with roles + the group-size fan-out split**

```
group_members (group_id, user_id, role, joined_at)   ← paginated, indexed both ways
```

- **Roles & permissions:** owner/admin/member, who-can-post, who-can-add — enforced server-side.
- **Small groups → fan-out on write:** push the message to every member's inbox (like the demo).
- **Large groups/channels → fan-out on read:** for a 50k-member channel you don't write 50k copies per message; members pull the channel's shared message log when they open it (the same celebrity-vs-regular split as the Social Media Feed). Slack channels and Discord work this way.

---

## `Storage/MessageStore.cs` — In-memory dictionary, `Contains`-based queries

**Problem in production:** Won't hold billions of messages, loses everything on restart, and `GetUndelivered` scanning every chat with a `string.Contains` is O(everything).

**Production replacement: Cassandra partitioned by chat + a per-user inbox index**

The demo's noted access pattern maps directly to Cassandra modeling:

```sql
CREATE TABLE messages_by_chat (
  chat_id text, seq bigint, message_id text, sender_id text, body blob, sent_at timestamp,
  PRIMARY KEY (chat_id, seq)
) WITH CLUSTERING ORDER BY (seq DESC);   -- "messages in chat X, newest first"
```

- **History** is a single-partition range scan — exactly what Cassandra is best at.
- **Undelivered/offline backlog** uses a dedicated **per-user inbox index** (don't scan all chats): `inbox_by_user (user_id, chat_id, last_delivered_seq)`. On reconnect, for each chat the user is in, fetch messages with `seq > last_delivered_seq`.
- **Message IDs** use Snowflake/KSUID, not a process-local `Interlocked` counter.

---

## `Service/ChatServer.cs` — In-process orchestrator

**Problem in production:** The send pipeline order is right (**persist → deliver → push fallback**), but each step is a network operation, the server must hold millions of concurrent WebSocket connections, and "one server owns a chat's sequence" must be enforced across a cluster.

**Production replacement: A gateway tier + a session/logic tier, sharded by chat**

```
WebSocket Gateway tier:
  → terminates millions of long-lived connections (epoll/Netty/Elixir processes)
  → does TLS, auth, heartbeat, and forwards frames to the logic tier

Chat logic tier (sharded by chat_id):
  Send pipeline (order preserved from the demo):
    1. assign per-chat seq (the shard owns this → gap-free ordering)
    2. PERSIST to Cassandra first        ← durability before delivery
    3. for each recipient device: route via registry (online) → gateway → WebSocket
    4. offline devices → enqueue push
    5. stream delivery/read acks back to the sender
```

- **Connection scale:** one server can hold ~100k–1M idle WebSockets with the right runtime (WhatsApp famously used Erlang for this; Discord uses Elixir).
- **Sharding by chat_id** is what makes the per-chat sequence and ordering deterministic — the single-writer constraint, same as the Collaborative Editing project.
- **Backpressure:** if a recipient is slow, buffer/drop with flow control rather than blocking the sender.

---

## `Program.cs` — Sequential in-memory demo scenarios

**Problem in production:** A single process running scenarios end to end; production is many always-on services.

**Production replacement: Deployed services + infrastructure**

```
Gateway fleet      → WebSocket termination, auth, heartbeat
Chat logic fleet   → sharded by chat_id, send pipeline, ordering
Presence service   → Redis TTL keys + presence fan-out
Push workers       → APNs/FCM delivery
Storage            → Cassandra (messages), Redis (registry/presence)
Routing            → Redis Pub/Sub or Kafka per-user partitions
```

The six demo scenarios map to real concerns: online 1:1 (cross-server routing), offline delivery (persist + push + backlog drain), group fan-out (the size-based split), receipts (per-recipient ack tracking), presence (TTL + fan-out), and history (Cassandra pagination).

---

## Cross-cutting concerns not modelled in this project

### 1. End-to-end encryption

WhatsApp/Signal encrypt with the **Signal Protocol** (X3DH key agreement + Double Ratchet). The server stores and routes ciphertext it cannot read. This changes everything: search, server-side moderation, and multi-device sync all become harder and must be solved on the client. Slack and Discord (workplace/community) typically use encryption *in transit and at rest* but not E2E, so the server can search and moderate.

### 2. Multi-device sync

A user on phone + laptop + web must see identical, consistent message state. Each device is a separate connection (per-device registry entries), each tracks its own `last_delivered_seq`, and read state syncs across devices ("read on laptop" clears the badge on phone). E2E systems replicate encryption sessions per device.

### 3. Observability

```
message_send_latency_seconds{quantile="0.99"}   send → recipient-device latency
ws_connections_active                           live connections per gateway
delivery_success_ratio                          delivered / sent
push_delivery_latency_seconds                   offline notification latency
presence_fanout_rate                            presence updates/sec
undelivered_backlog_size                        per-user offline queue depth
```

The headline product metric is **end-to-end message latency** (send tap → appears on recipient's screen) — target well under a second for online users.

### 4. Ordering & exactly-once display

Per-chat sequence numbers give ordering; clients dedupe by `message_id` (delivery is at-least-once, so a message can arrive twice after a retry). The client renders in `seq` order and ignores any `message_id` it has already shown.

### 5. Abuse, spam & moderation

Rate limiting per sender, spam classifiers, report/block flows, and (for non-E2E platforms) content moderation. Block must take effect immediately — a blocked user's messages are dropped server-side before delivery.

### 6. Media handling

Images/videos/files don't travel through the message path. The sender uploads to a blob store (S3) + CDN and sends a *reference* (the demo's `MediaUrl`); recipients download from the CDN. For E2E, media is encrypted client-side before upload.

---

## The Full Production Picture

```
CONNECT (Bob opens the app on phone + laptop):

each device → WebSocket Gateway (TLS, auth, heartbeat)
   → registry: HSET presence:bob {deviceId} {gatewayId}, EXPIRE
   → presence: SET presence:bob online EX 30
   → backlog drain: for each chat, fetch messages where seq > last_delivered_seq


SEND (Alice messages Bob):

Gateway → Chat logic shard owning chat:alice:bob
   1. assign per-chat seq           (shard is the single writer → gap-free order)
   2. PERSIST to Cassandra          (durability before delivery)
   3. registry lookup for Bob's devices:
        online device  → route via its gateway → WebSocket push (ciphertext)
        offline device → enqueue push (APNs/FCM, content-free for E2E)
   4. stream DELIVERED/READ acks back to Alice as devices ack


PRESENCE:
   heartbeats refresh TTL keys; expiry = offline (no cleanup code)
   presence fan-out only to contacts currently viewing the chat


Background / always-on:
  Push workers          → APNs/FCM, token management, mute/badge logic
  Presence expiry       → Redis TTL marks dead connections offline
  Media pipeline        → blob upload + CDN, references in messages
  Moderation/abuse      → rate limits, spam models, block enforcement

Observability (always-on):
  Prometheus            → e2e message latency p99, active connections, delivery ratio
  Distributed tracing   → send → persist → route → gateway → device
  Product metrics       → time-to-deliver, backlog depth, push latency
```

The core logic (pub/sub cross-server routing, persist-then-deliver, online/offline fallback to push, heartbeat+TTL presence, the connection registry, Cassandra-backed history with backlog drain) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with a gateway/logic split, sharded chat ownership, per-device delivery, E2E encryption, and full observability.
