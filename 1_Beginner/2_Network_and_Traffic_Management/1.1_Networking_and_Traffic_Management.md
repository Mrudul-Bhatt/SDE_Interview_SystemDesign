# Networking & Traffic Management

---

## 3. What are Load Balancers, and why are they used?

### The Problem

A single server handling all traffic has two fundamental problems:

```
All Users → [Single Server]
              ↑
         Overloaded / Single point of failure
```

If traffic spikes or the server crashes, your entire system goes down.

### What is a Load Balancer?

A load balancer sits in front of multiple servers and **distributes incoming requests** across them.

```
           [Load Balancer]
          /       |       \
    [S1]       [S2]       [S3]
```

It acts as the single entry point, but no single backend server bears all the load.

---

### What Load Balancers Do

**1. Traffic Distribution**
Spread requests across servers so no single one is overwhelmed.

**2. Health Checking**
Continuously ping backend servers. If one fails, stop sending traffic to it.

```
[LB] → pings S1, S2, S3 every 5 seconds
S2 fails → LB routes only to S1 and S3
S2 recovers → LB adds it back automatically
```

**3. SSL Termination**
Decrypt HTTPS traffic once at the load balancer instead of on every backend server — offloads CPU from your servers.

**4. Session Persistence (Sticky Sessions)**
Route the same user to the same backend server consistently (useful when session state is stored locally on the server).

---

### Load Balancing Algorithms

#### Round Robin
Requests go to each server in order: S1 → S2 → S3 → S1 → S2 → S3...

```
Request 1 → S1
Request 2 → S2
Request 3 → S3
Request 4 → S1  (cycle repeats)
```

**Best for:** Servers with equal capacity and similar request costs.

#### Weighted Round Robin
Servers get traffic proportional to their weight.

```
S1 (weight 3): gets 3 out of every 5 requests
S2 (weight 2): gets 2 out of every 5 requests
```

**Best for:** Heterogeneous servers with different capacities.

#### Least Connections
Route to the server with the fewest active connections.

```
S1: 100 active connections
S2: 20 active connections   ← next request goes here
S3: 80 active connections
```

**Best for:** Long-lived connections (WebSockets, file uploads) where request duration varies.

#### IP Hash
Hash the client's IP to always route them to the same server.

```
hash(client_ip) % N = server index
```

**Best for:** Sticky sessions without application-level session sharing.

#### Least Response Time
Route to the server with lowest latency + fewest connections combined.

**Best for:** Latency-sensitive applications.

---

### Layer 4 vs Layer 7 Load Balancers

This is a commonly asked follow-up in interviews.

#### Layer 4 (Transport Layer)
Operates on **IP and TCP/UDP** — routes based on source/destination IP and port. Does not inspect the content of packets.

```
Client → LB sees: TCP connection to port 443 → forwards to backend
```

- Very fast, minimal processing
- Cannot make routing decisions based on URL, headers, or cookies
- **Examples:** AWS NLB, HAProxy in TCP mode

#### Layer 7 (Application Layer)
Operates on **HTTP/HTTPS** — can inspect the actual request content.

```
/api/users  → Route to User Service
/api/orders → Route to Order Service
/images/*   → Route to Static File Server
```

- Smarter routing (URL-based, header-based, cookie-based)
- Can do SSL termination, compression, caching
- Slightly more overhead than L4
- **Examples:** AWS ALB, Nginx, HAProxy in HTTP mode

| Feature | Layer 4 | Layer 7 |
|---------|---------|---------|
| Speed | Faster | Slightly slower |
| Routing intelligence | IP/Port only | URL, headers, cookies |
| SSL termination | No | Yes |
| Content-based routing | No | Yes |
| Use case | Raw TCP throughput | HTTP microservices |

---

### Types of Load Balancers

**Hardware Load Balancers**
Physical appliances (F5, Citrix). Extremely fast, extremely expensive. Used in legacy enterprise setups.

**Software Load Balancers**
Run on commodity hardware — Nginx, HAProxy, Envoy. Flexible and cheap.

**Cloud Load Balancers**
Managed services — AWS ALB/NLB, GCP Load Balancer, Azure LB. Easiest to operate, auto-scaling built in.

---

### Load Balancer as a Single Point of Failure

The load balancer itself can become the bottleneck or failure point. The solution:

```
         DNS
          ↓
   [Active LB] ←→ [Passive LB]   (heartbeat between them)
      /    \
   [S1]   [S2]
```

Use **active-passive** (one takes over if the other fails) or **active-active** (both serve traffic via DNS round robin or anycast IP).

---

### Trade-offs

| Pro | Con |
|-----|-----|
| Eliminates single point of failure | LB itself can become SPOF if not redundant |
| Enables horizontal scaling | Adds network hop (minor latency) |
| Health checks enable self-healing | Sticky sessions complicate stateless design |
| SSL termination offloads backend | L7 LBs add complexity and cost |

---

## 6. What is a CDN, and when should you use it?

### The Problem: Latency is Physical

The speed of light is a hard limit. A server in New York responding to a user in Tokyo adds **~150ms of round-trip latency** just from distance — before any processing happens.

```
Tokyo User → [Across Pacific Ocean] → New York Server
             ~150ms one-way
```

For static assets (images, CSS, JS, videos), this is wasteful — the content doesn't change per user, so why serve it from one place?

### What is a CDN?

A **Content Delivery Network** is a globally distributed network of servers (called **Points of Presence** or **PoPs**) that cache and serve content from locations **geographically close to users**.

```
                    [Origin Server - New York]
                   /           |             \
        [PoP - London]   [PoP - Tokyo]   [PoP - São Paulo]
               ↑                ↑                 ↑
         EU Users          Asia Users        LATAM Users
```

The Tokyo user now fetches content from the Tokyo PoP — **~5ms instead of ~150ms**.

---

### How a CDN Works

#### Cache Miss (First Request)
```
User → CDN PoP (cache miss) → Origin Server → CDN PoP caches it → User
```

#### Cache Hit (Subsequent Requests)
```
User → CDN PoP (cache hit) → User
         ↑
   Origin never involved
```

The CDN stores a copy at the edge. All future users near that PoP get served instantly from cache.

---

### What CDNs Serve

**Static Assets (Primary use case)**
- Images, videos, audio
- CSS, JavaScript bundles
- Fonts
- HTML files (for static sites)

**Dynamic Content (Advanced)**
Modern CDNs can also accelerate dynamic content via:
- **Edge computing** — run logic at the PoP (Cloudflare Workers, Lambda@Edge)
- **Route optimization** — use the CDN's optimized backbone network instead of public internet even for dynamic requests

**Streaming**
- Video on demand (Netflix, YouTube)
- Live streaming — CDN distributes the stream to millions of viewers

---

### CDN Caching — Key Concepts

#### TTL (Time to Live)
Each cached object has a TTL. After it expires, the CDN re-fetches from origin.

```
Cache-Control: max-age=86400   → cache for 24 hours
Cache-Control: no-cache        → always revalidate with origin
```

#### Cache Invalidation
Need to update a file before its TTL expires? You can:
- **Purge by URL** — tell the CDN to evict a specific file
- **Versioned filenames** — `app.v2.js` instead of `app.js` (most reliable)
- **Cache tags** — tag groups of assets and purge by tag

#### Cache Key
By default, the URL is the cache key. CDNs can also vary cache by:
- Headers (e.g., `Accept-Encoding`, `Accept-Language`)
- Cookies (serve different content to logged-in users)
- Query parameters

---

### CDN Benefits Beyond Latency

**1. Reduced Origin Load**
If 95% of requests are cache hits, your origin server handles only 5% of traffic — massive cost and infrastructure savings.

**2. DDoS Protection**
CDN absorbs attack traffic at the edge before it reaches your origin. Cloudflare, Akamai, and AWS CloudFront all offer DDoS mitigation.

**3. Availability**
If your origin goes down, cached content can still be served (stale-while-revalidate).

**4. Bandwidth Cost Savings**
CDN providers negotiate bulk bandwidth rates — cheaper than serving everything from your own servers.

---

### When to Use a CDN

**Use a CDN when:**
- You have a global or geographically distributed user base
- You serve large static assets (images, videos, JS bundles)
- You need to reduce load on your origin servers
- You need DDoS protection
- You're building a media-heavy product (video, images, audio)

**You may not need a CDN when:**
- All your users are in one region and near your servers
- Your content is highly personalized (no caching benefit)
- You're in early-stage with low traffic

---

### Popular CDN Providers

| Provider | Known For |
|----------|-----------|
| Cloudflare | DDoS protection, edge compute, free tier |
| AWS CloudFront | Deep AWS integration |
| Akamai | Enterprise, largest network |
| Fastly | Real-time purging, developer-friendly |
| Google Cloud CDN | GCP integration |

---

### CDN vs. Load Balancer — Common Confusion

These are often confused but serve different purposes:

| | CDN | Load Balancer |
|-|-----|---------------|
| Purpose | Serve cached content close to users | Distribute traffic across backend servers |
| Location | Globally distributed edge nodes | In front of your servers (one region) |
| Content | Mostly static/cacheable | All traffic (static + dynamic) |
| Reduces | Latency + origin load | Server overload + single point of failure |

In a real system, **you use both** — CDN at the edge for static assets, load balancer in your data center for dynamic requests.

```
User
  ↓
[CDN PoP]  ← static assets served here, never reach origin
  ↓ (cache miss or dynamic request)
[Load Balancer]
  ↓
[Backend Servers]
```

---

### Trade-offs

| Pro | Con |
|-----|-----|
| Drastically reduces latency globally | Cache invalidation is complex |
| Reduces origin server load | Stale content can be served if TTL is long |
| Built-in DDoS protection | Cost at high bandwidth volumes |
| Improves availability | Dynamic content harder to cache |
