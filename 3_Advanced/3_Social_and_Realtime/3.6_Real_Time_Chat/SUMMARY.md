# Real-Time Chat — Beginner Summary

## What is this project?

A **Real-Time Chat** system (like WhatsApp, Slack, Messenger, or Telegram) does one thing that sounds easy and turns out to be hard: **deliver a message from one person to another, instantly, no matter where either of them is.**

Think of it like a postal system that has to work three different ways at once:
- If the recipient is **standing right there**, hand them the letter directly (instant delivery).
- If they're **out**, slip a "you have mail" note under their door so they know to come back (push notification), and keep the letter safe in their mailbox.
- When they **come home**, give them everything that piled up while they were away (offline backlog).

The twist that makes it genuinely hard: there isn't one post office. There are **thousands of chat servers**, and Alice might be connected to Server1 while Bob is connected to Server2. The message has to find its way across servers without every server having to shout at every other server.

---

## The Big Challenges

1. **Cross-server delivery.** Alice is on Server1, Bob is on Server2. How does Server1 get a message to Bob without knowing Server2's address — or which of 10,000 servers Bob happens to be on right now?
2. **Online vs offline.** A connected user should get the message in milliseconds over a live connection. An offline user can't — so the message must be stored durably and a push notification fired to wake their phone.
3. **Never lose a message.** Networks fail mid-delivery. If the system tries to deliver *before* saving, a crash loses the message forever. Order of operations matters.
4. **Delivery receipts.** The ✓ → ✓✓ → ✓✓(read) progression every chat app shows requires tracking each message's state as it moves from sent to delivered to read.
5. **Presence ("online", "last seen 5 min ago").** How do you know someone is online when their server might have crashed without telling anyone?

Every file in this project solves one of these problems.

---

## The Core Idea — Pub/Sub Routing Across Servers

The single most important concept here is how a message crosses servers. The answer is **publish/subscribe via a shared message bus** (Redis Pub/Sub in production):

```
When Bob connects to Server2:
    Server2 SUBSCRIBES to the channel "user:bob"

When Alice (on Server1) sends Bob a message:
    Server1 PUBLISHES to the channel "user:bob"
        ↓
    The bus delivers it to whoever is subscribed → Server2
        ↓
    Server2 pushes it down Bob's live WebSocket connection ✓

If NOBODY is subscribed to "user:bob" (Bob is offline):
    Publish returns false → Server1 falls back to a push notification
```

Server1 never needs to know where Bob is. It just shouts into the `"user:bob"` channel, and whichever server is holding Bob's connection hears it. **That `false` return value — "nobody was listening" — is also how the system detects that a user is offline.**

---

## The Files — What Each One Does

### `Models/ChatMessage.cs` — The Unit of Conversation

Every message is a `ChatMessage`:

| Field | Example | Meaning |
|---|---|---|
| `MessageId` | `"msg:0042"` | Unique ID |
| `ChatId` | `"chat:alice:bob"` | Which conversation it belongs to |
| `SenderId` | `"alice"` | Who sent it |
| `Content` | `"Dinner at 7?"` | The text |
| `Type` / `MediaUrl` | `Image / "s3://..."` | Text, or media with a link |
| `SentAt` | `10:00:00` | Wall-clock send time |
| `Status` | `Sent → Delivered → Read` | Delivery state (drives the ✓ icons) |
| `SequenceNumber` | `7` | Monotonic per-chat ordering counter |

**Why a `SequenceNumber` *and* a timestamp?** Clocks on different servers drift. If two messages are sent in the same millisecond (rapid-fire texting), their timestamps tie — and you can't tell which came first. A sequence number that only ever increases breaks the tie and guarantees a stable order within a chat.

**Why `Status` only moves forward (`Sent → Delivered → Read`):** A message that's been read was obviously delivered. The state never goes backward, so a later "Read" receipt implicitly confirms "Delivered" too.

---

### `Models/SendResult.cs` — The Delivery Receipt

After a send, the server returns `{ MessageId, OnlineDelivered, PushSent }`. This separates the two delivery paths: `OnlineDelivered` counts recipients reached instantly via a live connection; `PushSent` counts offline recipients who got a push notification instead. In a group chat, one send might produce `OnlineDelivered=2, PushSent=1` — two members online, one asleep.

---

### `Core/ConnectionRegistry.cs` — Who's Connected Where

A simple map: `userId → serverId` (mirrors a Redis hash shared by all servers).

```
_userToServer:  "alice" → "Server1"
                "bob"   → "Server2"
```

**Why a central registry?** When Server1 needs to reach Bob, it could broadcast to all 10,000 servers and let the right one respond — but that's 10,000 wasted messages per send. Instead it looks Bob up: "Bob is on Server2," then routes precisely. The entry is written on connect and removed on disconnect (or auto-expired by TTL if the server crashes).

---

### `Core/MessageBus.cs` — The Cross-Server Router (Pub/Sub)

The in-process stand-in for **Redis Pub/Sub**. Servers `Subscribe` to channels; senders `Publish` to them.

```
Subscribe("user:bob", handler)   ← Server2 does this when Bob connects
Publish("user:bob", message)     ← Server1 does this to reach Bob
   → returns true  if someone was subscribed (delivered)
   → returns false if nobody was (Bob is offline → trigger push)
```

The genius is in that **boolean return**: the same call that delivers the message also tells you whether delivery succeeded. No separate "is Bob online?" lookup needed in the hot path — publish, and if it returns false, fall back to push.

---

### `Core/PresenceService.cs` — Online Status & "Last Seen"

Tracks who's online using a **heartbeat with a TTL (time-to-live)**, not just connect/disconnect events.

**Why heartbeats instead of just connect/disconnect?** If a server *crashes*, it never gets to send a "disconnect" event — so the user would appear online forever. Instead, each connected client sends a heartbeat every few seconds. If no heartbeat arrives for 30 seconds, the user is considered offline:

```
IsOnline(user) = (a heartbeat arrived within the last 30 seconds)

Server crashes → heartbeats stop → 30s later the user auto-flips to offline.
In production, Redis key expiry does this automatically — no cleanup code.
```

`LastSeen` is updated on every heartbeat *and* on disconnect, so "last seen 5 minutes ago" stays accurate even when a session ends without a clean goodbye.

---

### `Core/PushNotificationService.cs` — Waking Up Offline Users

The fallback delivery path (simulates Apple APNs / Google FCM). When a recipient has no live connection, the message is *already safely stored* — but the user won't see it until they open the app. A push notification ("New message from Alice: ...") wakes the app so delivery still feels real-time.

Key point: **push is a notification, not the message itself.** The real message lives durably in the `MessageStore`; push just nudges the user to come get it.

---

### `Storage/GroupStore.cs` — Group Membership

A set of members per group chat (mirrors a Redis SET): `"group:family" → { alice, bob, carol, dave }`.

**A clever `null` trick:** `GetMembers` returns `null` (not an empty set) for an unknown chat ID. This lets the `ChatServer` distinguish *"this is a 1:1 chat"* (no group entry → decode recipients from the chat ID) from *"this is a group chat that happens to have 0 members."* The ID format `"chat:alice:bob"` identifies 1:1 chats, keeping group logic and 1:1 logic cleanly separate.

---

### `Storage/MessageStore.cs` — Durable History

The permanent record of every message (simulates Cassandra).

**Why Cassandra fits perfectly:** Chat history is *always* queried the same way — "messages in chat X, newest first, before time T." Cassandra with `partition key = chatId` and `clustering key = sentAt DESC` makes that a single-partition range scan: O(1) to find the chat, O(count) to read the page. It's the textbook access pattern.

It also provides:
- **`GetHistory`** with cursor pagination (fetch newest-first for efficiency, return oldest-first for display).
- **`GetUndelivered`** — finds messages with status `Sent` (never delivered) addressed to a user, used to drain their backlog when they reconnect.
- **`Interlocked.Increment` for message IDs** — multiple users' sends can hit `Save` concurrently; the atomic increment guarantees no two messages get the same ID without locking the whole method.

---

### `Service/ChatServer.cs` — The Orchestrator

This ties everything together. The **send pipeline order is critical for correctness**:

```
Send(sender, chatId, content):
  1. PERSIST to MessageStore FIRST        ← durability before delivery
       (if delivery fails now, the message is still safe and retryable)
  2. For each recipient (excluding sender):
       Publish to "user:{recipient}" via the bus
         → online?  count as delivered
         → offline? (publish returned false) send a push notification
  3. Return SendResult { delivered, pushed }
```

**Why persist before delivering?** If you delivered first and crashed before saving, the message would be gone from history — the recipient saw it once and could never scroll back to it. Saving first means the message is durable no matter what happens next. This is the same "write-ahead" principle used in databases and message queues.

**`ConnectUser`** does three things: registers the user's location, subscribes this server to their channel, and **drains the offline backlog** — delivering everything that arrived while they were away (at-least-once delivery).

**`GetRecipients`** decides who receives a message:
```
Group chat  → look up members in GroupStore, exclude sender
1:1 chat    → decode "chat:alice:bob" → [alice, bob], exclude sender
```

---

### `Program.cs` — The Demo

Runs 6 scenarios:

| Scenario | What it demonstrates |
|---|---|
| 1 | Online 1:1 chat — Alice (Server1) ↔ Bob (Server2) deliver across servers via pub/sub |
| 2 | Offline delivery — Bob is offline; messages persist + push fires; backlog drains on reconnect |
| 3 | Group chat fan-out — one send reaches 3 online members + 1 push for the offline one |
| 4 | Delivery receipts — a message moves ✓ Sent → ✓✓ Delivered → ✓✓(read) |
| 5 | Presence & last seen — heartbeat keeps you online; disconnect flips status + records last seen |
| 6 | Message history — 12 messages read in stable cursor-paginated pages of 4 |

---

## The Big Picture — How It All Fits Together

```
CONNECT (Bob opens the app on Server2):

ChatServer("Server2").ConnectUser("bob")
   → ConnectionRegistry.Register("bob", "Server2")    (where is bob?)
   → PresenceService.Heartbeat("bob")                 (bob is online)
   → MessageBus.Subscribe("user:bob", deliver)        (listen for bob's messages)
   → MessageStore.GetUndelivered("bob") → drain backlog  (catch up on missed msgs)


SEND (Alice on Server1 messages Bob):

ChatServer("Server1").Send("alice", "chat:alice:bob", "Dinner at 7?")
   ↓
1. MessageStore.Save(...)                    ← PERSIST FIRST (durable)
   ↓
2. GetRecipients("chat:alice:bob") = [bob]   (exclude sender alice)
   ↓
   MessageBus.Publish("user:bob", msg)
      ├─ TRUE  → Server2 heard it → pushes down Bob's WebSocket
      │          msg.Status = Delivered  → SendResult.OnlineDelivered++
      │
      └─ FALSE → nobody subscribed (Bob offline)
                 PushNotificationService.Send("bob", "New message from alice...")
                 → SendResult.PushSent++


READ RECEIPT (Bob opens the chat):

ChatServer.MarkRead("bob", "chat:alice:bob")
   → every message from the other person → Status = Read
   → Alice's UI now shows ✓✓(read)


PRESENCE CHECK (Alice taps Bob's name):

PresenceService.IsOnline("bob")        → heartbeat within 30s?
PresenceService.GetLastSeen("bob")     → "last seen 10:42 UTC"
```

---

## Why This Design Is Used Everywhere

- **Pub/sub for cross-server routing** is how WhatsApp, Slack, and Discord deliver across fleets of thousands of servers without any server needing a map of all the others — the message bus is the switchboard.
- **Persist-then-deliver ordering** is why you never lose a chat message even when delivery fails mid-flight — the same durability-first discipline as a write-ahead log.
- **The online/offline fallback** (live WebSocket → push notification) is the universal pattern: instant when you can, "wake the app" when you can't, message always safe in storage either way.
- **Heartbeat + TTL presence** is how every chat app shows accurate "online"/"last seen" without leaking ghost-online users when servers crash — let the key expire instead of relying on a clean disconnect.
- **The connection registry** (userId → serverId in Redis) is the standard way to make a horizontally-scaled stateful service routable — the same idea behind session affinity in any large real-time system.
- **Cassandra-backed history with cursor pagination** is why you can scroll back through years of messages instantly — the access pattern and the storage engine are matched on purpose.
