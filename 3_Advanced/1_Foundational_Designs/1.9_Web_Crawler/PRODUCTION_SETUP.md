# Web Crawler — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system.

---

## `Core/BloomFilter.cs` — In-memory bit array

**Problem in production:** Three issues:
1. Stored in one server's RAM — resets to empty on every restart, forcing re-crawl of billions of URLs
2. Single filter shared across all crawl workers — becomes a write bottleneck under concurrent inserts
3. No support for deletion — a URL that was removed from a site can never be re-crawled

**Production replacement: RedisBloom (Redis Bloom Filter module)**
- Persistent — survives restarts, pre-populated from the previous crawl session
- Distributed — all crawler workers share one filter via the network
- Scales to billions of URLs with configurable false positive rate
- `BF.ADD url` and `BF.EXISTS url` are atomic Redis commands

For even higher scale: **Cuckoo Filter** (alternative data structure)
- Supports deletion — if a URL is removed from the web, it can be re-queued
- Same O(1) insert/lookup as a Bloom filter
- Slightly higher memory usage but the deletion support is worth it at Google scale

For the largest crawlers (Common Crawl, Google): a **tiered approach**
```
Tier 1: In-memory Bloom filter  → last 24 hours of URLs (fast, small)
Tier 2: Redis Bloom filter      → last 30 days (medium, distributed)
Tier 3: HBase / Bigtable table  → all-time seen URLs (slow, authoritative)
```
Check tier 1 first. Only fall through to tier 3 on a miss — keeps the hot path fast.

---

## `Core/RobotsCache.cs` — Pre-loaded robots.txt rules

**Problem in production:** Rules are hardcoded in the demo — in reality, every domain has its own `/robots.txt` that must be fetched over the network before the first page is crawled. The file can also change at any time.

**Production replacement: Robots.txt fetcher + TTL cache**

On first visit to any new domain:
```
HTTP GET https://example.com/robots.txt
    ↓
Parse rules (Disallow, Allow, Crawl-delay, Sitemap directives)
    ↓
Store parsed rules in Redis with TTL = 24 hours
    ↓
On subsequent visits: read from Redis, no re-fetch needed
```

Edge cases that must be handled in production but are skipped here:
- **404 on robots.txt** → treat as "crawl everything allowed"
- **503 on robots.txt** → treat as "crawl nothing" (server overloaded, be polite)
- **Malformed file** → skip unparseable lines, apply what can be parsed
- **Very large file** → cap at ~500KB to prevent memory exhaustion
- **Sitemap directive** → extract `Sitemap:` URLs and seed them into the frontier at High priority

Use Google's open-source **robotstxt** library rather than a hand-rolled parser — it handles all RFC edge cases correctly.

---

## `Core/UrlNormalizer.cs` — Static URL normalization

**Problem in production:** Several real-world URL forms are not handled:
- **Internationalized domain names** — `münchen.de` must be converted to punycode `xn--mnchen-3ya.de` before comparison
- **URL encoding inconsistencies** — `%20` vs `+` vs space in query strings
- **www vs non-www** — `www.example.com` and `example.com` often serve identical content
- **HTTP vs HTTPS** — same page, different scheme — without normalization, crawled twice
- **Hardcoded tracking param list** — new tracking params (`ttclid`, `msclkid`, `mc_eid`) appear regularly

**Production enhancement:**
- Replace hardcoded `_trackingParams` with a **remotely configurable list** (loaded from a config service, updated without redeploy)
- Add **www canonicalization** — check `Link: <https://example.com>; rel="canonical"` header in HTTP response and normalize to canonical form
- Use **Uri.UnescapeDataString** consistently to normalize percent-encoding before comparison
- Run **punycode conversion** on all non-ASCII hostnames before hashing into the Bloom filter

---

## `Infrastructure/SimulatedWeb.cs` — Fake in-memory web

**Problem in production:** This class exists only for testing. It has no network, no HTML parsing, and no real HTTP semantics. It is entirely replaced in production.

**Production replacement: Async HTTP client with full fetch pipeline**

```csharp
// Production fetch pipeline (simplified)
var response = await _httpClient.GetAsync(url, timeout: 30s);
// → Follow redirects (301/302/307) up to 5 hops
// → Respect 429 Too Many Requests (back off and retry)
// → Check Content-Type: only parse text/html, skip images/PDFs
// → Decompress gzip/brotli response body
// → Extract <a href> links using a real HTML parser
// → Check ETag / Last-Modified for conditional re-crawl
```

Key production components:

| Component | Technology | Purpose |
|---|---|---|
| HTTP client | `HttpClientFactory` (ASP.NET Core) | Connection pooling, DNS caching |
| Retry logic | Polly library | Exponential backoff on 5xx errors |
| HTML parser | AngleSharp or HtmlAgilityPack | Correctly extract links from real HTML |
| JS rendering | Playwright (headless Chromium) | Crawl React/Vue SPAs that need JavaScript |
| DNS cache | Custom TTL-based cache | Avoid repeated DNS lookups for the same domain |

**Exponential backoff** on failures (the real version of the spin loop in `Crawler.cs`):
```
1st retry → wait 1s
2nd retry → wait 2s
3rd retry → wait 4s
4th retry → wait 8s  (then give up, return URL to frontier with low priority)
```

---

## `Infrastructure/UrlFrontier.cs` — In-memory priority list

**Problem in production:** Four issues:
1. `List<UrlTask>` with LINQ sort on every dequeue is O(N log N) — unacceptably slow at millions of queued URLs
2. Entire queue lost on restart — crawl must start over from seeds
3. Single process — can't distribute work across multiple crawler machines
4. No persistence — can't pause and resume a long-running crawl

**Production replacement: Apache Kafka + per-domain back queues**

Mirrors the **Mercator architecture** (Google's original crawler design):

```
Front queues (Kafka topics, one per priority level):
    kafka-topic: crawl-high    → seed URLs, freshness-critical pages
    kafka-topic: crawl-medium  → standard discovered links
    kafka-topic: crawl-low     → archives, paginated results

Back queues (one Redis sorted set per domain):
    redis: frontier:example.com  → ZADD with timestamp score
    redis: frontier:news.com
    redis: frontier:shop.com
```

**Dequeue logic:**
- Kafka consumers read from high-priority topic first
- Each URL is routed to its domain's back queue
- A scheduler picks the domain whose crawl-delay has elapsed and dequeues one URL
- Domain crawl-delay enforced via Redis TTL on a lock key: `SET lock:example.com 1 PX 500`

**Benefits over in-memory list:**
- Persistent across restarts — resume any crawl from where it stopped
- Distributed — 50 crawler machines share the same frontier via Kafka
- Backpressure — if crawlers fall behind, Kafka buffers millions of URLs durably

---

## `Models/ContentStore.cs` — In-memory list of crawled pages

**Problem in production:** Storing HTML in a `List<CrawledPage>` in RAM is lost on restart and limited to a few GB on one machine. A production crawl stores petabytes of HTML.

**Production replacement: Three-layer storage**

| Layer | Technology | Stores | Why |
|---|---|---|---|
| Raw HTML | Amazon S3 / Google GCS | Full HTML body, keyed by URL hash | Cheap, durable, unlimited scale |
| Metadata | Apache Cassandra | URL, status code, crawl time, content hash | Fast lookup by URL, distributed |
| Search index | Elasticsearch / Apache Solr | Extracted text, title, links | Full-text search, ranking signals |

**Before storing**, run **SimHash** on the HTML body:
```
SimHash(page A) XOR SimHash(page B) → Hamming distance
If distance < 3 → near-duplicate → skip indexing, just store URL reference
```
This prevents storing 10,000 near-identical product pages that differ only in sidebar ads.

**Content pipeline:**
```
Raw HTML
    ↓
Text extraction (remove HTML tags)
    ↓
Language detection
    ↓
SimHash near-duplicate check
    ↓
Store to S3 (raw) + Cassandra (metadata) + Elasticsearch (text)
```

---

## `Models/CrawlStats.cs` — In-memory counters

**Problem in production:** Counters are lost on restart, exist on only one server, and can't be aggregated across a fleet of 50 crawler machines.

**Production replacement: Prometheus + Grafana**

Each crawler worker exposes metrics via a `/metrics` HTTP endpoint:
```
crawl_pages_total{status="200"} 142831
crawl_pages_total{status="404"} 3201
crawl_duplicates_blocked_total   89432
crawl_robots_blocked_total       12043
crawl_frontier_queue_size        458201
```

**Prometheus** scrapes all workers every 15 seconds and aggregates across the fleet.
**Grafana** displays live dashboards:
- Pages crawled per second (throughput)
- Frontier queue depth (are workers keeping up?)
- Bloom filter fill ratio (approaching 50%? Time to resize)
- Error rate by domain (is a site returning 5xx?)
- Cache hit rate

**Alerting:** PagerDuty alert fires if crawl throughput drops below a threshold — catches worker crashes, network issues, or domain-wide blocks automatically.

---

## `Models/UrlTask.cs` — Simple task model

**Problem in production:** Missing fields needed for fault tolerance and crawl quality.

**Production enhancement: Richer task model**

```csharp
public class UrlTask
{
    public string        Url             { get; set; }
    public string        Domain          { get; set; }
    public int           Depth           { get; set; }
    public CrawlPriority Priority        { get; set; }

    // Added for production:
    public int           RetryCount      { get; set; }  // give up after 3 failures
    public DateTime?     LastAttemptAt   { get; set; }  // for exponential backoff timing
    public string        DiscoveredFrom  { get; set; }  // which page linked here (for link graph)
    public string        ExpectedType    { get; set; }  // "text/html" — skip if mismatch
    public string        ETag            { get; set; }  // for conditional re-crawl (If-None-Match)
    public DateTime?     LastModified    { get; set; }  // for conditional re-crawl (If-Modified-Since)
}
```

`ETag` and `LastModified` enable **conditional re-crawl**: on revisit, send `If-None-Match: <etag>` — if the server returns `304 Not Modified`, skip re-processing entirely. Huge bandwidth saving for stable pages.

`DiscoveredFrom` builds a **link graph** — the data structure behind PageRank. Knowing which pages link to which lets you score page importance, not just crawl frequency.

---

## `Service/Crawler.cs` — Single-threaded main loop

**Problem in production:** Five issues:
1. Single-threaded — processes one URL at a time; a 1-second fetch blocks all other work
2. Single process — one machine doing all the work
3. No fault tolerance — an exception crashes the entire crawl
4. Link extraction is mocked — real HTML has nested links, relative paths, `<base>` tags, JavaScript-generated links
5. No re-crawl scheduling — pages are crawled once and never revisited

**Production replacement: Distributed async worker fleet**

```
Crawler fleet (50+ machines, each running N async workers):

Worker 1 ──┐
Worker 2 ──┤── reads from Kafka frontier
Worker 3 ──┤── fetches URL (async HTTP)
...        ──┘── writes to S3 + Cassandra + Elasticsearch
                └── publishes discovered links back to Kafka
```

Each worker is **async** — while waiting for an HTTP response (typically 200–500ms), it picks up other URLs. One machine can handle 100+ concurrent fetches this way.

**Re-crawl scheduling:** Pages aren't crawled just once. A scheduler re-queues pages based on change frequency:
```
News pages      → re-crawl every 1 hour   (content changes constantly)
Product pages   → re-crawl every 24 hours
Static articles → re-crawl every 7 days
Archive pages   → re-crawl every 30 days
```
Change frequency is learned from history: if a page's content hash changes on every visit, crawl it more often.

**Fault tolerance:**
- Worker crashes → Kafka re-delivers the unacknowledged URL to another worker
- Domain goes down → exponential backoff, URL re-queued with lower priority
- Bloom filter node fails → Redis replica takes over instantly

---

## The Full Production Picture

```
Seeds / Re-crawl scheduler
          ↓
    Kafka (front queues — High / Medium / Low priority)
          ↓
    Domain back queues (Redis sorted sets, per-domain crawl-delay)
          ↓
    Crawler workers (50+ machines, 100 async fetches each)
          ↓
    HTTP fetch pipeline
      ├─ Conditional request (ETag / Last-Modified)
      ├─ Exponential backoff on 5xx
      └─ Headless browser for JavaScript-rendered pages
          ↓
    Processing pipeline
      ├─ UrlNormalizer → canonical form
      ├─ RedisBloom → dedup check
      ├─ SimHash → near-duplicate check
      └─ Link extraction (AngleSharp HTML parser)
          ↓
    Storage
      ├─ S3 / GCS         → raw HTML
      ├─ Cassandra         → crawl metadata
      └─ Elasticsearch     → full-text index
          ↓
    Discovered links → back to Kafka
          ↓
    Prometheus + Grafana  → monitoring & alerting
```

The core logic (Bloom filter dedup, robots.txt respect, URL normalization, depth cap, priority ordering) all carries forward — only the infrastructure changes from single-process in-memory to a distributed, fault-tolerant, persistent pipeline.
