# Networking & Communication

---

## 11. What is the difference between REST, GraphQL, and gRPC?

### The Problem They All Solve

All three are ways for clients and servers to communicate — they answer the question: **"How does a client ask a server for data or actions?"**

---

### REST (Representational State Transfer)

REST is an **architectural style** built on top of HTTP. Data is organized around **resources** (nouns), and HTTP methods (verbs) define actions on those resources.

```
GET    /users/1          → fetch user 1
POST   /users            → create a new user
PUT    /users/1          → update user 1
DELETE /users/1          → delete user 1
GET    /users/1/orders   → fetch orders for user 1
```

Each resource has a URL. The server decides what data is returned for each endpoint.

**Response example:**
```json
GET /users/1
{
  "id": 1,
  "name": "Alice",
  "email": "alice@email.com",
  "role": "admin",
  "created_at": "2024-01-01"
}
```

#### The Two Core Problems with REST

**Over-fetching:** You get more data than you need.
```
You only need the user's name for a UI label.
But GET /users/1 returns 20 fields — you discard 19.
```

**Under-fetching (N+1 problem):** One request isn't enough, so you make many.
```
GET /users          → returns 10 users (no order info)
GET /users/1/orders → fetch orders for user 1
GET /users/2/orders → fetch orders for user 2
... 10 requests total for data that should be one round trip
```

**When REST works well:**
- Public APIs consumed by many different clients
- Simple CRUD operations
- When HTTP caching is important
- When your team is familiar and tooling matters

---

### GraphQL

GraphQL is a **query language** for APIs where the **client specifies exactly what data it needs** in a single request. Developed by Facebook in 2012, open-sourced in 2015.

Instead of many endpoints, there is typically **one endpoint** (`/graphql`) and clients send queries to it.

```graphql
# Client asks for exactly what it needs
query {
  user(id: 1) {
    name
    orders {
      id
      total
      items {
        name
        price
      }
    }
  }
}
```

**Response — only what was asked for:**
```json
{
  "data": {
    "user": {
      "name": "Alice",
      "orders": [
        {
          "id": 1,
          "total": 50.00,
          "items": [{ "name": "Book", "price": 20.00 }]
        }
      ]
    }
  }
}
```

No over-fetching. No N+1 problem. One round trip.

#### GraphQL Core Concepts

**Query** — read data
```graphql
query { user(id: 1) { name email } }
```

**Mutation** — write data
```graphql
mutation {
  updateUser(id: 1, name: "Alice Smith") {
    id
    name
  }
}
```

**Subscription** — real-time updates over WebSocket
```graphql
subscription {
  orderStatusChanged(orderId: 42) {
    status
    updatedAt
  }
}
```

**Schema** — the contract between client and server
```graphql
type User {
  id: ID!
  name: String!
  email: String!
  orders: [Order]
}

type Order {
  id: ID!
  total: Float!
  items: [Item]
}
```

#### GraphQL Trade-offs

| Pro | Con |
|-----|-----|
| No over/under-fetching | Complex to implement server-side |
| Single round trip for nested data | HTTP caching is harder (all POST to /graphql) |
| Strongly typed schema = great tooling | N+1 problem moves to resolver layer (need DataLoader) |
| Client drives the query | Overkill for simple CRUD APIs |
| Great for mobile (bandwidth matters) | Learning curve |

**When GraphQL works well:**
- Mobile apps where bandwidth is precious
- Complex, highly nested data relationships
- Multiple clients (web, mobile, third-party) needing different data shapes
- Rapid product iteration (no backend changes needed for new data requirements)

---

### gRPC (Google Remote Procedure Call)

gRPC is a **high-performance RPC framework** built on HTTP/2. Instead of resources (REST) or queries (GraphQL), you call **remote functions** directly as if they were local.

Data is serialized using **Protocol Buffers (protobuf)** — a binary format, much smaller and faster than JSON.

**Step 1 — Define your service in a `.proto` file:**
```protobuf
syntax = "proto3";

service UserService {
  rpc GetUser (GetUserRequest) returns (User);
  rpc CreateUser (CreateUserRequest) returns (User);
  rpc ListUsers (ListUsersRequest) returns (stream User);
}

message GetUserRequest {
  int32 id = 1;
}

message User {
  int32 id = 1;
  string name = 2;
  string email = 3;
}
```

**Step 2 — Generated client code (any language):**
```python
# Feels like calling a local function
user = user_service.GetUser(GetUserRequest(id=1))
print(user.name)  # "Alice"
```

#### gRPC Streaming

gRPC supports 4 communication patterns:

```
Unary:            Client sends one request, server sends one response
Server Streaming: Client sends one request, server streams many responses
Client Streaming: Client streams many requests, server sends one response
Bidirectional:    Both sides stream simultaneously
```

```protobuf
rpc GetUser(GetUserRequest) returns (User);                   // Unary
rpc ListUsers(ListUsersRequest) returns (stream User);        // Server streaming
rpc UploadData(stream Chunk) returns (UploadResponse);        // Client streaming
rpc Chat(stream Message) returns (stream Message);            // Bidirectional
```

#### Why gRPC is Fast

- **HTTP/2** — multiplexed streams, header compression, binary framing
- **Protobuf** — binary serialization, 3-10x smaller than JSON, faster to parse
- **Code generation** — type-safe clients in any language from one `.proto` file

#### gRPC Trade-offs

| Pro | Con |
|-----|-----|
| Very high performance | Not human-readable (binary) |
| Strongly typed contract | Browser support limited (needs gRPC-Web proxy) |
| Multi-language code generation | Harder to debug than REST |
| Streaming built-in | Requires protobuf tooling |
| Great for internal services | Overkill for simple public APIs |

**When gRPC works well:**
- Internal microservice-to-microservice communication
- High-throughput, low-latency systems
- Polyglot environments (services in different languages)
- Streaming data (real-time feeds, large file transfers)

---

### Side-by-Side Comparison

| Dimension | REST | GraphQL | gRPC |
|-----------|------|---------|------|
| Protocol | HTTP/1.1 | HTTP/1.1 or 2 | HTTP/2 |
| Data format | JSON/XML | JSON | Protobuf (binary) |
| Schema | Implicit (OpenAPI optional) | Strongly typed | Strongly typed (.proto) |
| Fetching | Fixed endpoints | Client-defined queries | Remote function calls |
| Over-fetching | Common | Never | Never |
| Caching | Easy (HTTP cache) | Hard | Hard |
| Browser support | Native | Native | Needs proxy |
| Streaming | Limited (SSE/WebSockets) | Subscriptions | Native (4 modes) |
| Performance | Medium | Medium | High |
| Best for | Public APIs, CRUD | Mobile, complex data | Internal services |

---

### How They're Used Together in Practice

Large systems often use all three:

```
[Mobile App] ——GraphQL——→ [API Gateway]
[Browser]    ——REST——————→ [API Gateway]
                               ↓
                    [Internal Microservices]
                    communicating via gRPC
```

- **Public-facing:** REST or GraphQL (browser/mobile friendly)
- **Internal:** gRPC (performance, type safety, streaming)

---

## 12. TCP vs. UDP — When would you choose one over the other?

### What They Are

Both TCP and UDP are **transport layer protocols** — they define how data is sent between two machines over a network. They sit on top of IP and below application protocols like HTTP.

---

### TCP (Transmission Control Protocol)

TCP establishes a **connection** before transferring data and guarantees:

- **Delivery** — lost packets are retransmitted
- **Order** — packets arrive in the order they were sent
- **Integrity** — checksums detect corruption

#### The TCP Handshake

Before any data flows, TCP does a 3-way handshake:

```
Client → SYN        → Server   "I want to connect"
Client ← SYN-ACK   ← Server   "OK, I'm ready"
Client → ACK        → Server   "Great, let's go"
           ↓
      Connection established
      Data transfer begins
```

This handshake adds **1 round trip of latency** before data can flow.

#### TCP Flow Control and Congestion Control

**Flow control:** Receiver tells sender how much buffer space it has — sender won't overwhelm receiver.

**Congestion control:** TCP detects network congestion (packet loss) and slows transmission to avoid making it worse.

#### What TCP Guarantees Cost You

- **Latency:** Handshake overhead + retransmission delays
- **Head-of-line blocking:** If packet #3 is lost, packets #4, #5, #6 wait even if they arrived
- **Connection state:** Server must maintain state for every open connection

---

### UDP (User Datagram Protocol)

UDP is **connectionless** — just fire packets and forget. No handshake, no delivery guarantee, no ordering.

```
Client → [Packet 1] → Server
Client → [Packet 2] → Server  (may arrive before Packet 1)
Client → [Packet 3] → Server  (may be lost entirely — nobody knows)
```

UDP is essentially IP with a port number and a checksum added. That's it.

**What UDP gives you:**
- No connection setup overhead
- No retransmission delays
- No head-of-line blocking
- Supports broadcast and multicast (TCP is point-to-point only)
- Lower latency when data loss is acceptable

---

### Core Comparison

| Dimension | TCP | UDP |
|-----------|-----|-----|
| Connection | Connection-oriented (handshake) | Connectionless |
| Delivery guarantee | Yes (retransmits lost packets) | No |
| Ordering | Yes (in-order delivery) | No |
| Speed | Slower (overhead) | Faster (no overhead) |
| Error correction | Yes | Checksum only (no correction) |
| Congestion control | Yes | No |
| Broadcast/Multicast | No | Yes |
| Use case | Reliability critical | Latency critical |

---

### When to Choose TCP

**Choose TCP when data correctness and completeness matter more than speed.**

- **Web browsing (HTTP/HTTPS)** — a missing packet in HTML or JS corrupts the response
- **File transfers (FTP, SFTP)** — a file with missing bytes is unusable
- **Email (SMTP, IMAP)** — missing parts of an email are unacceptable
- **Database connections** — a missing byte in a SQL query could corrupt data
- **Financial transactions** — every byte of a payment instruction must arrive correctly

---

### When to Choose UDP

**Choose UDP when speed matters more than perfect delivery.**

**Video/Voice calls (VoIP, Zoom, WebRTC)**
A lost audio packet causes a brief glitch — far better than waiting for retransmission which would cause audio to freeze.

```
TCP video call: packet lost → wait for retransmit → video freezes → jerky playback
UDP video call: packet lost → slight glitch → continues smoothly
```

**Online gaming**
Player position updates happen 60 times per second. A lost update is immediately superseded by the next one — retransmitting old position data is useless.

**DNS lookups**
A single small query/response. If lost, client just retries. No need for TCP overhead.

**Live video streaming**
Viewers accept occasional artifacts — freezing to wait for retransmission is worse.

**IoT sensor data**
Sending temperature readings every second — one lost reading is fine.

---

### The Middle Ground — Application-Level Reliability over UDP

Some applications need **selective reliability** — UDP's speed but some ordering/delivery guarantees for certain data.

**QUIC (HTTP/3)** — runs over UDP but implements its own reliability, congestion control, and encryption. Fixes TCP's head-of-line blocking problem.

```
HTTP/1.1 → TCP
HTTP/2   → TCP (still has HOL blocking at transport level)
HTTP/3   → UDP + QUIC (no HOL blocking, faster connection setup)
```

**Game networking protocols** — many games implement their own "reliable UDP" where critical events (player joined, item picked up) are retransmitted, but position updates are fire-and-forget.

---

### Decision Framework

```
Does every byte need to arrive correctly?
  Yes → TCP (HTTP, databases, file transfer, email)
  No  → consider UDP

Is latency more important than occasional loss?
  Yes → UDP (video calls, gaming, live streaming, DNS)
  No  → TCP

Do you need broadcast/multicast?
  Yes → UDP (TCP is point-to-point only)
```

---

## 13. What is a Reverse Proxy? How is it different from a Load Balancer?

### What is a Forward Proxy?

A **forward proxy** sits between clients and the internet, acting on behalf of clients:

```
[Client] → [Forward Proxy] → [Internet / Server]
```

The server sees the proxy's IP, not the client's. Used for corporate firewalls, VPNs, bypassing geo-restrictions.

---

### What is a Reverse Proxy?

A **reverse proxy** sits in front of servers, acting on behalf of servers:

```
[Client] → [Reverse Proxy] → [Server 1]
                           → [Server 2]
                           → [Server 3]
```

The client thinks it's talking to one server. It has no idea how many servers are behind it.

---

### What a Reverse Proxy Does

**1. SSL Termination**
Handles HTTPS decryption once, forwards plain HTTP to backend servers.

```
Client ——HTTPS——→ [Reverse Proxy] ——HTTP——→ [Backend Servers]
```

**2. Request Routing**
Route different URLs to different backend services:

```
/api/*      → API servers
/static/*   → file servers or CDN
/admin/*    → admin service (internal only)
```

**3. Caching**
Cache responses and serve them without hitting the backend.

**4. Compression**
Compress responses (gzip) before sending to clients — offloads from backend.

**5. Rate Limiting**
Reject or throttle clients sending too many requests.

**6. Authentication**
Validate tokens/sessions at the proxy before requests reach backends.

**7. Logging and Monitoring**
Centralized access logs for all traffic without touching backend code.

**8. IP Whitelisting/Blacklisting**
Block malicious IPs before they reach your servers.

---

### Reverse Proxy vs. Load Balancer

**A load balancer is a type of reverse proxy**, but not all reverse proxies are load balancers.

| | Reverse Proxy | Load Balancer |
|-|--------------|---------------|
| Primary purpose | General request handling, security, routing | Distribute traffic across servers |
| Traffic distribution | May or may not | Core function |
| SSL termination | Yes | Sometimes |
| Caching | Yes | Rarely |
| Compression | Yes | No |
| Authentication | Yes | No |
| Health checks | Sometimes | Always |
| Example tools | Nginx, HAProxy, Envoy, Caddy | AWS ALB, Nginx, HAProxy |

**Key insight:** Nginx and HAProxy are both reverse proxies **and** load balancers. The distinction is more about primary purpose than capability.

```
Load Balancer concern: "Which of my N backend servers should handle this?"
Reverse Proxy concern: "How do I handle this request before/after it hits the backend?"
```

---

### Nginx as a Reverse Proxy — Example

```nginx
server {
    listen 443 ssl;
    server_name example.com;

    # SSL termination
    ssl_certificate /etc/ssl/cert.pem;
    ssl_certificate_key /etc/ssl/key.pem;

    # Route /api to backend API servers
    location /api/ {
        proxy_pass http://api_servers;
    }

    # Serve static files directly (cache them)
    location /static/ {
        root /var/www;
        expires 30d;
    }

    # Rate limiting
    limit_req zone=api_limit burst=20;
}

# Load balancing across API servers
upstream api_servers {
    server 10.0.0.1:8080;
    server 10.0.0.2:8080;
    server 10.0.0.3:8080;
}
```

---

### Where a Reverse Proxy Sits in a System

```
Internet
    ↓
[Reverse Proxy / API Gateway]    ← SSL, auth, rate limiting, routing
    ↓
[Load Balancer]                  ← distribute across instances
    ↓
[App Server 1] [App Server 2]
    ↓
[Database]
```

In simpler systems, one tool (Nginx) plays both roles simultaneously.

---

## 14. Long Polling vs. WebSockets vs. Server-Sent Events

### The Problem: Real-Time Communication

HTTP is request-response — the client asks, the server answers. But many applications need the server to **push updates to the client** without the client asking:

- Chat messages
- Live notifications
- Stock price updates
- Real-time collaboration (Google Docs)
- Live sports scores

---

### Short Polling (The Naive Approach)

Client repeatedly asks the server "anything new?" on a timer.

```
Client: "Anything new?"  → Server: "No"    (t=0)
Client: "Anything new?"  → Server: "No"    (t=1s)
Client: "Anything new?"  → Server: "No"    (t=2s)
Client: "Anything new?"  → Server: "Yes!"  (t=3s)
```

**Problems:** Wastes bandwidth and server resources. Updates are only as fresh as your polling interval.

---

### Long Polling

Client sends a request. Server **holds it open** until it has something to send (or a timeout). When the server responds, the client immediately sends a new request.

```
Client → Request ────────────────────→ Server holds it
                                       (waiting for event)
Client ←─────────── Response ────────← Server: "New message!"
Client → Request ────────────────────→ Server holds it again
```

```javascript
function longPoll() {
  fetch('/api/messages/wait')
    .then(res => {
      updateUI(res);
      longPoll();      // immediately reconnect
    })
    .catch(() => {
      setTimeout(longPoll, 1000);  // retry on error
    });
}
longPoll();
```

**Pro:** Works with standard HTTP, no special infrastructure
**Con:** High server connection count, complex server implementation, HTTP overhead on every cycle

**When to use:** When WebSockets aren't available, legacy browser support needed, or infrastructure doesn't support persistent connections.

---

### Server-Sent Events (SSE)

The server opens a **persistent one-way stream** to the client. Client connects once; server pushes events as they happen. Built into the browser via `EventSource` API.

```
Client → GET /events (one request)
Server ← ─────────────────────────── keeps connection open
         ← "data: message 1\n\n"
         ← "data: message 2\n\n"
         ← "data: message 3\n\n"    (server pushes as events happen)
```

```javascript
// Client
const source = new EventSource('/api/events');
source.onmessage = (event) => {
  updateUI(JSON.parse(event.data));
};
source.addEventListener('notification', (event) => {
  showNotification(event.data);
});
```

```python
# Server (Python/Flask)
def event_stream():
    while True:
        event = wait_for_event()
        yield f"data: {json.dumps(event)}\n\n"

@app.route('/api/events')
def events():
    return Response(event_stream(), content_type='text/event-stream')
```

**Built-in features:**
- **Auto-reconnect** — browser reconnects automatically if connection drops
- **Last-Event-ID** — browser sends last received event ID on reconnect, server can resume
- **Named events** — different event types in one stream

**Pro:** Simple, native browser support, auto-reconnect built-in, works over HTTP/1.1
**Con:** **One-way only** (server → client), not suitable when client also needs to push data

**When to use:** Notifications, live feeds, dashboards, activity streams.

---

### WebSockets

A **full-duplex, persistent, bidirectional** connection. After an initial HTTP upgrade handshake, both sides can send messages at any time over a single TCP connection.

```
Client → HTTP Upgrade Request → Server
Client ← 101 Switching Protocols ← Server
          ↓
    [Persistent WebSocket connection]
Client ↔ Server   (both can send messages anytime)
```

```javascript
// Client
const ws = new WebSocket('wss://example.com/chat');

ws.onopen = () => ws.send(JSON.stringify({ type: 'join', room: 'general' }));
ws.onmessage = (event) => updateChat(JSON.parse(event.data));

// Send a message
ws.send(JSON.stringify({ type: 'message', text: 'Hello!' }));
```

**Key properties:**
- **Bidirectional** — client and server both send whenever they want
- **Low overhead** — each message has only 2-14 bytes of framing overhead vs full HTTP headers
- **Real-time** — sub-millisecond latency for message delivery
- **Stateful** — server must maintain connection state per client

**Pro:** True real-time bidirectional communication, lowest latency, efficient for high-frequency messages
**Con:** Stateful connections complicate horizontal scaling, proxies/firewalls sometimes block WebSocket upgrades

**When to use:** Chat, multiplayer games, live collaboration, trading platforms.

---

### Side-by-Side Comparison

| | Long Polling | SSE | WebSockets |
|-|-------------|-----|------------|
| Direction | Client → Server (repeated) | Server → Client only | Bidirectional |
| Protocol | HTTP | HTTP | WS (upgrade from HTTP) |
| Connection | New connection each cycle | One persistent connection | One persistent connection |
| Browser support | Universal | All modern browsers | All modern browsers |
| Auto-reconnect | Manual | Built-in | Manual |
| Overhead | High (HTTP headers each time) | Low | Very low (2-14 byte framing) |
| Latency | Medium | Low | Lowest |
| Scaling complexity | Low | Medium | High |
| Best for | Legacy support, infrequent updates | Notifications, live feeds | Chat, gaming, collaboration |

---

### Choosing the Right Approach

```
Does the client need to send data to the server in real-time?
  Yes → WebSockets (bidirectional)
  No  → SSE (simpler, one-way is enough)

Is this infrequent updates (every few minutes)?
  Yes → Long polling (simple, no persistent connection needed)

Do you need sub-100ms latency for high-frequency messages?
  Yes → WebSockets

Are you behind infrastructure that blocks WebSocket upgrades?
  Yes → SSE or Long Polling (plain HTTP)
```

---

### Real-World Usage

| Product | Technology | Reason |
|---------|-----------|--------|
| Slack | WebSockets | Bidirectional chat |
| Twitter/X feed | SSE | One-way live feed |
| GitHub notifications | Long polling / SSE | Infrequent, one-way |
| Google Docs | WebSockets | Real-time collaborative editing |
| Uber driver location | WebSockets | Bidirectional position updates |
| Stock tickers | SSE | Server pushes price updates |
| Online multiplayer games | WebSockets / UDP | Low latency bidirectional |
