# Architecture Fundamentals

---

## 19. Monolithic vs. Microservices Architecture

### What is a Monolithic Architecture?

A monolith is a single deployable unit where **all functionality lives in one codebase and process**. The UI, business logic, and data access layer are all bundled together.

```
┌─────────────────────────────────────┐
│           Monolithic App            │
│                                     │
│  ┌──────────┐  ┌──────────────────┐ │
│  │   User   │  │     Order        │ │
│  │  Module  │  │     Module       │ │
│  └──────────┘  └──────────────────┘ │
│  ┌──────────┐  ┌──────────────────┐ │
│  │ Payment  │  │   Notification   │ │
│  │  Module  │  │     Module       │ │
│  └──────────┘  └──────────────────┘ │
│                                     │
│         Single Database             │
└─────────────────────────────────────┘
         One deployable unit
```

All modules share the same process, memory, and database. They call each other as in-process function calls.

---

### What is Microservices Architecture?

Microservices splits the application into **small, independently deployable services**, each responsible for a specific business capability. Services communicate over the network (HTTP, gRPC, message queues).

```
[User Service] ←──→ [Order Service] ←──→ [Payment Service]
      ↓                    ↓                     ↓
  [Users DB]          [Orders DB]           [Payments DB]

      ↕ (via API or message queue)

[Notification Service] ←──→ [Inventory Service]
         ↓                          ↓
  [Notifications DB]          [Inventory DB]
```

Each service:
- Has its own database
- Can be deployed independently
- Can be written in a different language
- Can be scaled independently

---

### Monolith — Pros and Cons

**Pros:**

**Simple to develop initially**
No network calls between components, no distributed systems complexity. Debug with a single stack trace.

**Easy to test**
One process to spin up for integration tests. No mocking of external services.

**Simple deployment**
Deploy one artifact. No service discovery, no orchestration.

**No network overhead**
In-process function calls are nanoseconds. Network calls are milliseconds.

**Easier transactions**
ACID transactions across the entire system — one database, no distributed transaction complexity.

---

**Cons:**

**Scaling is all-or-nothing**
If your payment processing is the bottleneck, you must scale the entire app — including user management, notifications, etc.

```
Traffic spike on payments:
Monolith: scale entire app × 5 (wasteful)
Microservices: scale only Payment Service × 5
```

**Deployment risk**
Every deploy touches the entire codebase. A bug in the notification module can take down payments.

**Technology lock-in**
Entire team must use the same language, framework, and runtime.

**Codebase grows unwieldy**
As the team and product grow, the codebase becomes a "big ball of mud" — tight coupling, long build times, hard to onboard.

**Team scaling issues**
Multiple teams working on one codebase leads to merge conflicts, coordination overhead, and deployment bottlenecks.

---

### Microservices — Pros and Cons

**Pros:**

**Independent scaling**
Scale only what needs scaling. Pay for what you use.

**Independent deployment**
Teams deploy their services without coordinating with other teams.

**Technology flexibility**
Payment Service in Go (performance), ML Service in Python, UI in Node.js. Right tool for each job.

**Fault isolation**
A crash in the Notification Service doesn't take down Payments or Orders.

```
[Notification Service crashes]
→ Orders still process ✓
→ Payments still work ✓
→ Users just don't get emails temporarily
```

**Team autonomy**
Each team owns their service end-to-end — code, deployment, on-call. Conway's Law: system architecture mirrors team structure.

---

**Cons:**

**Distributed systems complexity**
Network calls fail. Services go down. You need retries, timeouts, circuit breakers, service discovery, distributed tracing.

```
Monolith function call: userService.getUser(id) → 0.001ms, never fails
Microservice network call: GET /users/{id} → could timeout, return 503, be slow
```

**Distributed transactions are hard**
A bank transfer spanning two services requires coordination (saga pattern, 2-phase commit) instead of a simple `BEGIN/COMMIT`.

**Operational overhead**
10 services = 10 deployment pipelines, 10 monitoring dashboards, 10 log streams.

**Latency**
Each inter-service call adds network latency. A user request may fan out to 5-10 services.

**Data consistency**
Each service has its own DB — no foreign keys across services, eventual consistency between service boundaries.

---

### Side-by-Side Comparison

| Dimension | Monolith | Microservices |
|-----------|----------|---------------|
| Deployment | Single unit | Independent per service |
| Scaling | All-or-nothing | Per-service |
| Complexity | Low initially | High (distributed systems) |
| Development speed | Fast early | Faster at scale (parallel teams) |
| Fault isolation | Poor | Strong |
| Technology choice | One stack | Per-service |
| Transactions | Simple (ACID) | Complex (sagas) |
| Debugging | Easy (one log, one trace) | Hard (distributed tracing needed) |
| Team size | Small teams | Large, multiple teams |
| Best for | Early stage, small teams | Scale, large org, many teams |

---

### The Spectrum — Modular Monolith

The choice isn't binary. Many teams start with a **modular monolith** — a monolith with clear internal module boundaries that can be extracted into services later.

```
┌──────────────────────────────────────┐
│        Modular Monolith              │
│  ┌────────────┐  ┌────────────────┐  │
│  │   User     │  │    Order       │  │
│  │  Module    │  │    Module      │  │
│  │(self-cont.)│  │ (self-cont.)   │  │
│  └────────────┘  └────────────────┘  │
│    Only communicate via interfaces   │
└──────────────────────────────────────┘
```

When a module needs to scale independently → extract it into a microservice.

---

### When to Use Each

```
Start with a monolith when:
✓ Early-stage startup — speed matters, problem not fully understood
✓ Small team (< 10 engineers)
✓ Domain not yet clear — hard to draw service boundaries correctly
✓ Low traffic — no scaling need yet

Move to microservices when:
✓ Multiple teams stepping on each other's deployments
✓ Specific components need independent scaling
✓ Different components have different reliability/performance needs
✓ You have operational maturity (CI/CD, monitoring, tracing)
✓ Team is large enough to own services end-to-end
```

> "Don't start with microservices. Monolith first, extract later when you feel the pain." — Martin Fowler

---

## 20. What is a Single Point of Failure, and how do you avoid it?

### What is a Single Point of Failure (SPOF)?

A **Single Point of Failure** is any component whose failure causes the entire system to stop working.

```
Users → [Load Balancer] → [Only Server] → [Only Database]
                                ↑
                          If this fails,
                          system is down
```

SPOFs exist at every layer: hardware, software, network, power, even people.

---

### Identifying SPOFs

Ask: **"If this component disappeared right now, does the system stop working?"**

Common SPOFs in web systems:

```
1. Single web/app server
2. Single database (no replicas)
3. Single load balancer
4. Single data center
5. Single DNS provider
6. Single cloud region
7. Single network switch/router
8. Single power supply
9. Single third-party dependency (payment gateway, auth provider)
```

---

### How to Eliminate SPOFs

The core strategy: **redundancy**. Have multiple instances of every critical component, with automatic failover.

#### 1. Redundant Servers (Horizontal Scaling)

```
Before (SPOF):          After (redundant):
[Single Server]    →    [Server 1]
                        [Server 2]  ← if S1 fails, S2 takes over
                        [Server 3]
```

Load balancer distributes traffic and health-checks — removes failed servers automatically.

#### 2. Database Replication

```
Before (SPOF):               After (redundant):
[Primary DB only]    →    [Primary DB]
                            ↓ replication
                          [Replica 1]
                          [Replica 2]  ← automatic failover
```

If Primary fails → Replica 1 is promoted to Primary.

#### 3. Redundant Load Balancers

```
Before (SPOF):               After (redundant):
[Single LB]    →    [Active LB] ←heartbeat→ [Passive LB]
                         ↑
                    Virtual IP floats to Passive if Active fails
```

#### 4. Multi-Availability Zone Deployment

A single data center is itself a SPOF — power outage, network failure, natural disaster.

```
Before:                         After:
[All servers in AZ-1]    →    [AZ-1: Primary]
                               [AZ-2: Standby]   ← cross-AZ failover
                               [AZ-3: Standby]
```

#### 5. Multi-Region Deployment

Even a region can fail. Distribute across regions for highest availability.

```
[Region: us-east-1] ←── replication ──→ [Region: us-west-2]
        ↑                                          ↑
    Primary                                    Standby/Active
```

Active-active: both regions serve traffic
Active-passive: failover only when primary region fails

#### 6. External Dependency Redundancy

```
Payment Gateway:
[Stripe] → primary
[Braintree] → fallback if Stripe is down

DNS:
[Route 53] + [Cloudflare] → two DNS providers
```

#### 7. Eliminate Human SPOFs

The "bus factor" — what happens if the one person who knows X gets hit by a bus?

- Document everything
- Cross-train team members
- Automate runbooks
- Runbooks shouldn't require one specific person

---

### Availability and the Nines

| Availability | Downtime per year | Downtime per month |
|-------------|------------------|-------------------|
| 99% (two nines) | 3.65 days | 7.2 hours |
| 99.9% (three nines) | 8.7 hours | 43.8 minutes |
| 99.99% (four nines) | 52.6 minutes | 4.4 minutes |
| 99.999% (five nines) | 5.26 minutes | 26 seconds |

Each additional nine requires eliminating more SPOFs — costs grow non-linearly.

---

### Trade-offs of Redundancy

| Benefit | Cost |
|---------|------|
| Higher availability | More infrastructure cost (2x-3x servers) |
| Fault tolerance | More complexity (failover logic, health checks) |
| Geographic distribution | Data consistency challenges across regions |
| No SPOF | Operational overhead (more systems to monitor) |

Design for the **right level of availability** for your use case. A personal blog doesn't need five nines. A payment processor does.

---

## 21. Stateless vs. Stateful Services

### What is State?

**State** is any data that persists between requests and affects how future requests are handled.

```
Stateless: each request contains everything needed to process it
           server has no memory of previous requests

Stateful:  server remembers information about previous interactions
           request outcome depends on server's stored memory
```

---

### Stateless Services

A stateless service treats each request **independently**. No session data, no memory of past interactions.

Authentication in a stateless system uses **tokens** (JWT) — the token carries all the information the server needs:

```
Client sends: GET /orders
              Authorization: Bearer eyJhbGciOiJIUzI1NiJ9...

Token contains: { user_id: 1, role: "admin", expires: "..." }
Server decodes token → has all it needs → no DB lookup for session
```

**Any server can handle any request** — the client carries the state.

```
Request from Alice → Load Balancer → Server 1 ✓
Next request       → Load Balancer → Server 3 ✓ (works fine, no memory needed)
```

---

### Stateful Services

A stateful service remembers information about clients across requests.

```
Request 1: POST /login (username, password)
           Server: stores session { user_id: 1, cart: [] } in memory
           Returns: session_id=abc123

Request 2: GET /cart
           Client sends: Cookie: session_id=abc123
           Server: looks up session abc123 → finds user_id=1, cart=[]
```

**Problem:** If Request 2 goes to a different server, that server has no session data.

```
Request 1 → Server 1 (stores session in memory)
Request 2 → Server 2 (no session data → user is logged out!) ✗
```

---

### Why It Matters for Scaling

**Stateless services scale horizontally with zero friction:**

```
Traffic spike:
[Load Balancer]
  → [Server 1]  ← any request works
  → [Server 2]  ← any request works
  → [Server 3]  ← just added, works immediately
  → [Server 4]  ← just added, works immediately
```

**Stateful services have the sticky session problem:**

```
[Load Balancer]
  → [Server 1: Alice's session]
  → [Server 2: Bob's session]

Alice must always go to Server 1
If Server 1 goes down → Alice's session is lost
```

---

### Solutions for Stateful Services

**Option 1 — Sticky Sessions (Session Affinity)**
Load balancer always routes a client to the same server.

```
Problem: defeats load balancing
         if Server 1 dies, session is gone
```

**Option 2 — Externalize State**
Move session state out of the server into a shared store (Redis).

```
Before:                          After:
[Server 1: stores session]  →   [Server 1] ──→ [Redis: sessions]
                                [Server 2] ──→ [Redis: sessions]
                                [Server 3] ──→ [Redis: sessions]
```

Now any server can handle any request — sessions survive server failures.

**Option 3 — Stateless Tokens (JWT)**
Encode session state in a signed token. Server validates the signature, extracts data — no storage needed.

```
JWT: eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyX2lkIjoxfQ.signature
     [header]              [payload: user_id=1] [HMAC signature]

Server: verify signature → decode payload → done
        no database, no Redis, no memory
```

---

### Stateless vs. Stateful Comparison

| Dimension | Stateless | Stateful |
|-----------|-----------|---------|
| Horizontal scaling | Trivial | Complex (sticky sessions or external store) |
| Fault tolerance | Excellent (any server takes over) | Poor (session loss on failure) |
| Server memory usage | Low | High (stores sessions) |
| Request independence | Complete | Dependent on server state |
| Complexity | Low | Higher |
| Examples | REST APIs with JWT, CDN servers | WebSocket connections, game servers |

---

### Designing for Statelessness

Push state to dedicated stores, keep app servers stateless:

```
Stateless layer (scale freely):
[App Servers] — no state stored here

State lives in dedicated stores:
[Redis]     — sessions, caches, ephemeral state
[Database]  — persistent business data
[S3]        — files, media
[JWT]       — auth state in the token itself
```

---

## 22. What is Rate Limiting, and how would you implement it?

### What is Rate Limiting?

Rate limiting **controls how many requests** a client can make within a given time window. It protects your system from:

- **Abuse** — a bad actor hammering your API
- **DDoS attacks** — overwhelming servers with traffic
- **Runaway clients** — a buggy client in an infinite retry loop
- **Resource fairness** — one client monopolizing shared resources
- **Cost control** — preventing runaway API costs

```
Without rate limiting:
[Attacker: 100,000 req/sec] → [Server: overwhelmed, crashes]

With rate limiting:
[Attacker: 100,000 req/sec] → [Rate Limiter: blocks after 100 req/min]
                                → [Server: protected]
```

---

### Rate Limiting Algorithms

#### 1. Fixed Window Counter

Divide time into fixed windows. Count requests per window. Reject if limit exceeded.

```
Window: 12:00:00 - 12:01:00 → limit: 100 requests

12:00:10 → request #1   → allow
12:00:45 → request #100 → allow
12:00:59 → request #101 → REJECT
12:01:00 → new window   → counter resets to 0
```

```python
def is_allowed(user_id, limit=100, window=60):
    key = f"rate:{user_id}:{int(time.time() / window)}"
    count = redis.incr(key)
    if count == 1:
        redis.expire(key, window)
    return count <= limit
```

**Problem — boundary burst:**
```
12:00:30 - 12:01:00: 100 requests (fills window)
12:01:00 - 12:01:30: 100 requests (new window resets)
→ 200 requests in 60 seconds — 2x the intended limit
```

---

#### 2. Sliding Window Log

Keep a log of timestamps for each request. Remove timestamps older than the window. If count is under limit, allow.

```
Limit: 5 requests per minute
Current time: 12:01:45

Log: [12:00:50, 12:01:10, 12:01:25, 12:01:40]
Remove entries before 12:00:45 → [12:01:10, 12:01:25, 12:01:40]
Count: 3 → under limit → ALLOW
```

**Pro:** Accurate — no boundary burst problem
**Con:** High memory usage (stores every timestamp per user)

---

#### 3. Sliding Window Counter

Hybrid — weights two fixed windows by time overlap.

```
Limit: 100 per minute
Current time: 12:01:45 (45 seconds into current window)

Previous window (12:00:00-12:01:00): 80 requests
Current window  (12:01:00-12:02:00): 30 requests

Weight of previous window = (60-45)/60 = 0.25
Estimated count = 80 × 0.25 + 30 = 50 → under 100 → ALLOW
```

**Pro:** Accurate without storing every timestamp, memory efficient
**Con:** Approximate (but close enough for most use cases)

---

#### 4. Token Bucket

A bucket holds tokens. Each request consumes a token. Tokens are added at a fixed rate. If empty, request is rejected.

```
Bucket capacity: 10 tokens
Refill rate: 2 tokens/second

t=0:  bucket=[10 tokens]
t=0:  5 requests → bucket=[5 tokens]
t=1:  bucket=[7 tokens] (refilled 2)
t=1:  7 requests → bucket=[0 tokens]
t=1:  1 more request → REJECT (bucket empty)
```

**Key property:** Allows **bursting** up to bucket capacity.

---

#### 5. Leaky Bucket

Requests enter a queue (bucket). A worker processes them at a **fixed rate** (the leak). If the bucket is full, reject.

```
Bucket size: 10 requests
Leak rate: 2 requests/second

[Queue: req1, req2, req3...] → processes 2/sec → [Server]
If queue full → REJECT new requests
```

**Pro:** Smooth output rate — protects downstream from bursts
**Con:** Adds latency, can feel unresponsive during bursts

---

### Algorithm Comparison

| Algorithm | Memory | Burst handling | Accuracy | Best for |
|-----------|--------|----------------|----------|----------|
| Fixed Window | Low | Poor (boundary burst) | Low | Simple use cases |
| Sliding Window Log | High | Accurate | High | Low traffic, accuracy critical |
| Sliding Window Counter | Low | Good | Medium | General purpose (recommended) |
| Token Bucket | Low | Allows bursts | High | APIs needing burst tolerance |
| Leaky Bucket | Medium | Smooths bursts | High | Protecting downstream services |

---

### Where to Implement Rate Limiting

```
[Client]
    ↓
[CDN / Edge]           ← Layer 1: block obvious abuse before hitting infra
    ↓
[API Gateway]          ← Layer 2: per-client, per-endpoint limits (most common)
    ↓
[Load Balancer]        ← Layer 3: global rate limits
    ↓
[App Server]           ← Layer 4: business logic rate limits
    ↓
[Database / Services]  ← Layer 5: connection pool limits (last resort)
```

---

### Rate Limiting in Distributed Systems

**Problem:** 3 app servers each with local limit of 100 req/min — user can hit 300 req/min total.

```
User → Server 1: 100 req → allowed (under local limit)
User → Server 2: 100 req → allowed (under local limit)
User → Server 3: 100 req → allowed (under local limit)
Total: 300 requests — 3x the intended limit
```

**Solution: Centralized rate limit store (Redis)**

```python
def is_allowed(user_id, limit=100, window=60):
    key = f"rate:{user_id}:{int(time.time() / window)}"
    count = redis.incr(key)    # all servers share one Redis
    if count == 1:
        redis.expire(key, window)
    return count <= limit
```

**What if Redis goes down?**
- **Fail open:** Allow all requests (availability over protection)
- **Fail closed:** Reject all requests (protection over availability)
- **Local fallback:** Each server applies local limits until Redis recovers

Most systems **fail open** to avoid impacting legitimate users.

---

### Rate Limiting Response

```
HTTP 429 Too Many Requests

Headers:
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 0
X-RateLimit-Reset: 1713444000   (Unix timestamp when limit resets)
Retry-After: 30                  (seconds until client can retry)
```

---

### Types of Rate Limits in Practice

**Per-user / per-API key**
```
Free tier:    100 requests/minute
Pro tier:     1,000 requests/minute
Enterprise:   10,000 requests/minute
```

**Per-IP** — for unauthenticated endpoints
```
Login endpoint: 5 attempts/minute per IP (brute force protection)
```

**Per-endpoint** — different limits for different operations
```
GET /products:  1,000 req/min (cheap read)
POST /orders:   10 req/min    (expensive write)
POST /search:   50 req/min    (expensive query)
```

**Global** — protect the entire system
```
Total system: 1,000,000 req/min regardless of individual limits
```
