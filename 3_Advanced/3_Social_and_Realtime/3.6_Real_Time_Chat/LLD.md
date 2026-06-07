# Real-Time Chat — Low-Level Design (UML Class Diagram)

This is the **class-level** view of the Real-Time Chat system. The defining structural
feature: multiple `ChatServer` instances (Server1, Server2, Server3 in the demo) all share
the **same six backing services** — that shared substrate is exactly what makes cross-server
message routing work.

> **How to view the diagram below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If it doesn't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). It also renders automatically on GitHub.

---

## Class Diagram

```mermaid
classDiagram
    direction TB

    class ChatServer {
        «facade / orchestrator»
        +string ServerId
        -MessageStoreCassandra _store
        -ConnectionRegistryRedis _registry
        -PresenceServiceRedis _presence
        -PushNotificationServiceAPNsFCM _push
        -MessageBusRedis _bus
        -GroupStoreRedis _groups
        -Dictionary~string,Action~ _deliveryCallbacks
        -List~string~ _log
        +ConnectUser(userId, onReceive) void
        +DisconnectUser(userId) void
        +Send(sender, chatId, content, type, mediaUrl) SendResult
        +MarkRead(userId, chatId) void
        +GetHistory(chatId, count, before) List~ChatMessage~
        -Deliver(userId, msg) void
        -GetRecipients(chatId, senderId) IEnumerable
    }

    class MessageStoreCassandra {
        «source of truth»
        -Dictionary~string,List~ _chats
        -long _idCounter
        +Save(chatId, senderId, content, ...) ChatMessage
        +GetHistory(chatId, count, before) List~ChatMessage~
        +GetById(messageId) ChatMessage
        +GetUndelivered(userId) List~ChatMessage~
    }

    class ConnectionRegistryRedis {
        «routing table»
        -Dictionary~string,string~ _userToServer
        +Register(userId, serverId) void
        +Deregister(userId) void
        +GetServer(userId) string
        +IsOnline(userId) bool
    }

    class PresenceServiceRedis {
        «online / last-seen»
        -Dictionary~string,DateTime~ _heartbeats
        -Dictionary~string,DateTime~ _lastSeen
        -TimeSpan _timeout
        +Heartbeat(userId) void
        +Disconnect(userId) void
        +IsOnline(userId) bool
        +GetLastSeen(userId) DateTime?
    }

    class PushNotificationServiceAPNsFCM {
        «offline fallback»
        -List~tuple~ _log
        +Send(userId, notification) void
        +GetLog() IReadOnlyList
    }

    class MessageBusRedis {
        «pub/sub»
        -Dictionary~string,List~ _subscribers
        +Subscribe(channel, handler) void
        +Publish(channel, message) bool
    }

    class GroupStoreRedis {
        «membership»
        -Dictionary~string,HashSet~ _groups
        +CreateGroup(groupId, members) void
        +AddMember(groupId, userId) void
        +GetMembers(chatId) HashSet~string~
    }

    class ChatMessage {
        «entity (mutable Status)»
        +string MessageId
        +string ChatId
        +string SenderId
        +string Content
        +string MediaUrl
        +MessageType Type
        +DateTime SentAt
        +DeliveryStatus Status
        +int SequenceNumber
    }

    class SendResult {
        «receipt DTO»
        +string MessageId
        +int OnlineDelivered
        +int PushSent
    }

    class DeliveryStatus {
        «enum (one-way)»
        Sent
        Delivered
        Read
    }

    class MessageType {
        «enum»
        Text
        Image
        Video
        File
    }

    %% ── Aggregation: every ChatServer shares the SAME six injected services ──
    ChatServer o-- MessageStoreCassandra
    ChatServer o-- ConnectionRegistryRedis
    ChatServer o-- PresenceServiceRedis
    ChatServer o-- PushNotificationServiceAPNsFCM
    ChatServer o-- MessageBusRedis
    ChatServer o-- GroupStoreRedis

    %% ── Dependency: creates / routes ──
    ChatServer ..> SendResult : creates
    MessageBusRedis ..> ChatMessage : routes (Action)

    %% ── Composition: source of truth owns the messages ──
    MessageStoreCassandra "1" *-- "0..*" ChatMessage : owns

    %% ── Entity has-a enum ──
    ChatMessage --> MessageType
    ChatMessage --> DeliveryStatus
```

---

## Reading the relationships

| Notation | Relationship | In this design |
|----------|--------------|----------------|
| `o--` | **Aggregation** (holds, shared) | Every `ChatServer` (Server1/2/3) is constructor-injected the **same** six service instances (created once in `Program.ResetSystem`). This sharing **is** the multi-server story — servers coordinate only through these, never directly. |
| `*--` | **Composition** (owns contents) | `MessageStoreCassandra` creates and owns its `ChatMessage` objects (via `Save`). |
| `..>` | **Dependency** (uses, no field) | `ChatServer.Send` *creates* a `SendResult`; `MessageBusRedis` *routes* `ChatMessage`s through `Action<ChatMessage>` handlers without storing them. |
| `-->` | **Association** (has-a) | `ChatMessage` holds a `MessageType` and a `DeliveryStatus`. |

> **Note — local vs shared state inside `ChatServer`:** `_deliveryCallbacks` and `_log`
> are **per-instance** (Server1's ≠ Server2's) — they're composed, owned, and die with that
> one server. The six services above are **shared**. That split is the crux of the design.

## The structural story (the "why" behind the shape)

- **One facade, six collaborators, N instances.** `ChatServer` stores no domain data itself —
  it's pure orchestration. You run **many** `ChatServer` instances behind a load balancer, and
  they coordinate *only* through the six shared services, never by calling each other.
- **Local state vs global state — the key distinction.** `_deliveryCallbacks` (how to reach a
  socket) is **local** to each server; `ConnectionRegistryRedis._userToServer` (which server owns
  a user) is **global**. Cross-server routing = look up the global registry → publish on the
  global bus → the *target* server's local callback fires.
- **`MessageStoreCassandra` is the source of truth.** It's the only component that *creates and
  owns* `ChatMessage` objects. Everything else passes references: `MessageBusRedis` routes them,
  `ChatServer` mutates `Status`, but the durable copy lives here.
- **`SendResult` is a pure return DTO; `ChatMessage` is the mutable entity** (its `Status` advances
  Sent → Delivered → Read). The same entity-vs-DTO split seen in the other projects.
- **The two enums encode invariants** — `DeliveryStatus` is a one-way machine (never moves
  backward); `MessageType` discriminates whether `Content` or `MediaUrl` carries the payload.

## Call flow at a glance

```
CONNECT  ConnectUser(userId, onReceive):
   ConnectionRegistryRedis.Register(userId, ServerId)   ← global routing entry
   PresenceServiceRedis.Heartbeat(userId)               ← mark online
   _deliveryCallbacks[userId] = onReceive               ← local socket handle
   MessageBusRedis.Subscribe("user:userId", Deliver)    ← start receiving
   MessageStoreCassandra.GetUndelivered(userId)         ← drain offline backlog

SEND     Send(sender, chatId, content):
   1. MessageStoreCassandra.Save(...)                   ← persist FIRST (durability)
   2. for each GetRecipients(chatId, sender):           ← group→members | 1:1→split chatId
        MessageBusRedis.Publish("user:recipient", msg)
          ├─ true  → OnlineDelivered++   (target server's Deliver fires its callback)
          └─ false → PushNotificationServiceAPNsFCM.Send(...) ; PushSent++
   → return SendResult(msgId, OnlineDelivered, PushSent)
```
