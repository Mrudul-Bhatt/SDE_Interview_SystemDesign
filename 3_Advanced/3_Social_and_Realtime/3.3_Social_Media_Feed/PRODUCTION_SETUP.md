# Social Media Feed — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on Twitter/X, Instagram, and Facebook timelines).

---

## `Models/Post.cs` — Plain C# object with engagement counts

**Problem in production:** Three issues:
1. C# objects can't be stored or sent over the network as-is
2. `LikeCount` / `CommentCount` / `ShareCount` as mutable fields on the post invites lost-update races — two servers incrementing concurrently overwrite each other
3. `EngagementRaw` is computed inline with hardcoded weights — you can't A/B test ranking weights without redeploying

**Production replacement: Protobuf record + sharded counters + a feature store**

The post itself becomes an immutable Protobuf record in a content store (Cassandra/Manhattan):

```protobuf
message Post {
  string post_id    = 1;
  string author_id  = 2;
  string content    = 3;   // or a reference to a media/blob service
  int64  created_at = 4;
  repeated string media_ids = 5;
}
```

**Engagement counts live separately, not on the post**, because they change far more often than the post and at much higher volume:

```
Likes/comments/shares → sharded distributed counters (Redis INCR, or a
  write-optimized store like Twitter's "Tweetypie" counters / DynamoDB atomic adds)
  → eventually rolled up into a read cache
```

**Ranking weights move to a feature store / config service** (e.g. LaunchDarkly-style flags or a feature platform like Feast). Engagement weight tuning then becomes a config change with live A/B experiments, not a code deploy.

---

## `Models/FeedEntry.cs` — `{ PostId, Score }` pair

**Problem in production:** The shape is right (store IDs + score, not full posts), but an in-process object can't back a feed for hundreds of millions of users.

**Production replacement: Redis Sorted Set members**

This maps almost 1:1 to a Redis ZSET, which is exactly how Instagram and Twitter store materialized feeds:

```
ZADD feed:{userId} {score} {postId}
ZREVRANGEBYSCORE feed:{userId} {cursor} -inf LIMIT 0 {count}
```

The score is a timestamp (chronological) or a precomputed rank. Redis gives O(log N) inserts and O(log N + K) range reads natively — no application-side sorting. For durability and capacity beyond RAM, the same structure is tiered to a disk-backed store (e.g. Redis on Flash, or a custom store like Twitter's "Haplo").

---

## `Models/FeedPage.cs` — Posts + a `long?` cursor

**Problem in production:** Cursor-based pagination is the right call, but a raw timestamp cursor is leaky (exposes internal scoring) and breaks on ties (two posts with the same timestamp).

**Production replacement: Opaque, signed, composite cursors**

```
cursor = base64( {score}:{postId}:{direction} )  + an HMAC to prevent tampering
```

- **Composite** (score + postId) breaks ties deterministically — no skipped or repeated items when timestamps collide
- **Opaque** so clients can't construct arbitrary cursors to scrape or probe
- **Signed** so a tampered cursor is rejected, not silently mis-served

GraphQL Relay's connection spec (`edges`, `pageInfo`, `endCursor`) is the standard contract here.

---

## `Models/FanOutResult.cs` — Diagnostic receipt

**Problem in production:** Fine for a synchronous demo, but real fan-out is asynchronous — there's no single moment where you know the final count.

**Production replacement: An async job handle + metrics**

Fan-out becomes a background job (see `FanOutService` below). Instead of a return value, you emit:
- A **job ID** the caller can poll or subscribe to
- **Metrics**: `fanout_writes_total`, `fanout_latency_seconds`, `fanout_celebrity_skips_total` to Prometheus
- A **completion event** on a message bus for downstream systems (e.g. analytics)

---

## `Core/FollowGraph.cs` — Two in-memory dictionaries

**Problem in production:** This is one of the hardest parts to scale. The graph has billions of edges (Twitter: ~half a trillion). It can't live in one process's RAM, and "who follows X?" for a celebrity returns 100M+ rows.

**Production replacement: A dedicated graph/edge store, sharded by user**

```
Edge storage (Twitter's "FlockDB" / Facebook's "TAO" model):
  followers:{userId}  → sharded set of follower IDs
  following:{userId}  → sharded set of followed IDs

Sharded by userId so one user's edges live together; a follower-list read
is a single-shard scan, paginated (you never load 100M followers at once).
```

**Key production additions:**
- **Edge caching (TAO-style):** a read-through cache in front of the graph DB; the follow graph is read constantly and changes rarely, so cache hit rates are very high
- **Celebrity threshold tuning:** the demo's `10` becomes a tuned value (often ~1M+), and the classification is precomputed and cached, not recounted per request
- **Async follower-list pagination:** fan-out reads followers in batches of a few thousand, never the whole list in memory

---

## `Core/FeedRanker.cs` — Static time-decay formula

**Problem in production:** A fixed `engagement / age^gravity` formula is a good baseline but leaves quality on the table. Modern feeds use machine-learned ranking on hundreds of signals.

**Production replacement: A two-stage ML ranking pipeline**

```
Stage 1 — Candidate generation (cheap, high recall):
  pull a few thousand candidate posts (follows + celebrity pull + recommendations)

Stage 2 — Heavy ranking (expensive, high precision):
  a learned model (gradient-boosted trees → deep nets) scores each candidate on
  hundreds of features: author affinity, predicted like/comment/share/dwell-time,
  recency, content embeddings, diversity penalties, "show me less" feedback
  → sort by predicted engagement → apply business rules (ads, dedup, diversity)
```

- The time-decay formula survives as **one feature** (recency) among many
- Models are retrained continuously (hourly/daily) on fresh interaction logs
- Inference runs on a model-serving tier (TensorFlow Serving / Triton) with strict latency budgets (single-digit ms per candidate batch)

The demo's `affinityBoost` is the seed of this: production just replaces "one hand-tuned multiplier" with "a model that learned thousands of such weights."

---

## `Storage/PostStore.cs` — Two in-memory dictionaries

**Problem in production:** `_postsById` and `_postsByAuthor` won't hold billions of posts, and a process restart loses everything.

**Production replacement: Cassandra with two query-optimized tables**

The dual-index pattern in the demo maps directly to Cassandra modeling (model tables per query):

```sql
-- by id (feed hydration: random lookup)
CREATE TABLE posts_by_id (post_id text PRIMARY KEY, author_id text, content text, ...);

-- by author, newest-first (celebrity pull + backfill: range scan)
CREATE TABLE posts_by_author (
  author_id text, created_at timestamp, post_id text,
  PRIMARY KEY (author_id, created_at)
) WITH CLUSTERING ORDER BY (created_at DESC);
```

- Post content/media offloads to a **blob store** (S3 + CDN); the DB holds metadata + URLs
- A **read cache** (Redis/Memcached) fronts `posts_by_id` because hydration is the hottest path
- Hot posts (a viral tweet) get extra cache replication to avoid a single-key hotspot

---

## `Storage/FeedCache.cs` — In-memory list per user, sorted on every write

**Problem in production:** This is the materialized feed — the highest-volume, most latency-critical store in the whole system. A `Dictionary<string, List<FeedEntry>>` can't serve hundreds of millions of users, and re-sorting on every insert is wasteful.

**Production replacement: Sharded Redis Sorted Sets with capped length + TTL**

```
ZADD  feed:{userId} {score} {postId}      ← O(log N), no app-side sort
ZREMRANGEBYRANK feed:{userId} 0 -1001     ← cap to newest 1000 (your MaxFeedSize)
EXPIRE feed:{userId} {inactiveTTL}        ← evict dormant users' feeds
```

- **Sharded** across a Redis cluster by `userId` (consistent hashing)
- **Capped length** (the demo's 1000) trims the tail automatically
- **TTL on inactive users:** don't keep a materialized feed for someone who hasn't opened the app in 30 days — rebuild lazily on their return
- **Tiered storage:** active users in RAM, the long tail on flash (Redis-on-Flash) for cost

This is essentially Instagram's and Twitter's real feed-cache design.

---

## `Service/FanOutService.cs` — Synchronous push loop

**Problem in production:** The biggest gap. `OnPost` loops over followers synchronously inside the post request. For a user with 1M followers that's 1M cache writes blocking the API call — unacceptable. And there's no retry if a write fails midway.

**Production replacement: Async fan-out workers behind a message queue**

```
Post created
   ↓ publish "post.created" event to Kafka  (the API returns immediately)
Fan-out worker pool (auto-scaled) consumes the event:
   → load author's follower list, paginated in batches of ~1–5k
   → for each batch: pipeline ZADD into followers' Redis feeds
   → checkpoint progress so a crash resumes mid-fan-out (idempotent writes)
```

**Hybrid fan-out, formalized:**

```
Regular author (< threshold)  → fan-out on write (push to follower feeds)
Celebrity author (≥ threshold)→ skip push; pulled at read time (FeedService merges)
Borderline / bursty           → some platforms push to active followers only,
                                pull for the dormant majority
```

**Backfill and cleanup** (the demo's `BackfillOnFollow` / `CleanupOnUnfollow`) become their own async jobs — a follow/unfollow returns instantly while a worker reconciles the feed in the background.

---

## `Service/FeedService.cs` — Synchronous 4-step read assembly

**Problem in production:** The pipeline (precomputed + celebrity pull + merge + rank) is correct, but each step needs to be fast, fault-tolerant, and parallel — and the "pull celebrity posts on every read" step needs heavy caching.

**Production replacement: A parallel, cached, degradable read path**

```
GetFeed(userId):
  parallel:
    A. ZREVRANGE feed:{userId}        → precomputed feed (regular follows)
    B. for each celebrity followed:   → their recent posts (HEAVILY cached:
                                         one cache entry per celebrity, shared by
                                         all their followers — pulled once, fanned
                                         out at read for millions)
  merge + dedupe (by postId)
  hydrate post IDs → posts (from read cache, then Cassandra)
  rank (ML scorer, stage-2)
  apply business rules: ads injection, dedup, diversity, "seen" filtering
  return page + opaque cursor
```

**Production essentials the demo omits:**
- **Graceful degradation:** if the ranking service is slow, fall back to chronological; if celebrity pull times out, serve the precomputed feed alone. Never fail the whole feed because one input is slow.
- **"Already seen" filtering:** track recently-shown post IDs (a per-user Bloom filter / Redis set) so refreshing doesn't repeat content.
- **Edge/CDN caching** of the assembled first page for a few seconds for very active feeds.

---

## `Program.cs` — Sequential in-memory demo scenarios

**Problem in production:** It's a single-process demo. Production is many always-on services across a fleet.

**Production replacement: Deployed microservices + event backbone**

```
Post service        → writes posts, emits post.created
Fan-out workers     → consume events, materialize feeds (auto-scaled)
Feed read service   → assembles + ranks feeds (horizontally scaled, behind a CDN)
Graph service       → follow/unfollow, follower-list reads (TAO-style cache)
Ranking service     → ML model serving
Counter service     → engagement counts
```

The five demo scenarios map to real concerns: basic fan-out (the write pipeline), celebrity hybrid (the core scaling decision), cursor pagination (infinite scroll), algorithmic ranking (the ML pipeline), and follow/unfollow (async backfill/cleanup jobs).

---

## Cross-cutting concerns not modelled in this project

### 1. Observability

```
Feed metrics:
  feed_read_latency_seconds{quantile="0.99"}     p99 feed-open latency (the headline SLO)
  fanout_latency_seconds                          post → visible-in-follower-feeds time
  feed_cache_hit_ratio                            materialized-feed hit rate
  celebrity_pull_latency_seconds                  the on-read merge cost
  ranking_model_latency_seconds                   stage-2 inference time
  feed_empty_total                                feeds served with 0 items (a bug smell)
```

Distributed tracing follows one feed request across graph → cache → hydration → ranking so you can find the slow hop. "Time to first post visible" (fan-out lag) is a key product metric — a post should appear in followers' feeds within seconds.

### 2. The thundering-herd / hot-author problem

A celebrity posting causes millions of simultaneous read-time pulls of the same posts. Mitigations: cache the celebrity's recent posts **once** (shared by all followers), use request coalescing (single-flight) so concurrent misses trigger one backend fetch, and pre-warm the cache the moment a celebrity posts.

### 3. Ranking feedback & integrity

- **Negative feedback:** "show me less," mute, block, and report must immediately influence ranking and candidate generation.
- **Spam / abuse filtering:** integrity models remove or down-rank policy-violating content before it reaches a feed.
- **Diversity rules:** don't show five posts from the same author in a row; balance topics.

### 4. Privacy & access control

Every post has an audience (public, followers-only, close-friends, blocked-users-excluded). The feed must filter by visibility **at read time** — a follower relationship can change, and blocked users must never see content. This check can't be skipped for cache-hit speed.

### 5. Multi-region

Feeds are served from the region nearest the reader. Follower feeds are materialized per-region; the follow graph and posts replicate cross-region asynchronously. A user in the EU reads an EU feed cache populated by fan-out workers consuming a globally-replicated post stream.

---

## The Full Production Picture

```
POST (a user publishes):

Post service → write to Cassandra (posts_by_id, posts_by_author)
            → emit "post.created" to Kafka     (API returns immediately)
   ↓
Fan-out workers (auto-scaled) consume:
   author celebrity?  → YES: skip (pulled at read)
                        NO:  load followers in batches → pipeline ZADD into
                             each follower's Redis sorted-set feed (idempotent,
                             checkpointed for crash-resume)


READ (a user opens the app):

Feed read service (behind CDN/edge):
   parallel:
     A. ZREVRANGE feed:{user}           → precomputed (regular follows)
     B. celebrity follows → shared per-celebrity cache (single-flight on miss)
   merge + dedupe → hydrate IDs (read cache → Cassandra)
   → ML ranking (stage-1 candidates → stage-2 scorer)
   → business rules: ads, diversity, "already seen", visibility/privacy filter
   → return page + opaque signed cursor


GRAPH CHANGES:

Follow   → graph service writes edge → async BackfillOnFollow job
Unfollow → graph service removes edge → async CleanupOnUnfollow job
(both API calls return instantly; reconciliation happens in the background)


Background processes (always running):
  Fan-out workers       → materialize feeds from post events
  Backfill/cleanup jobs → reconcile feeds on follow/unfollow
  Counter rollups       → aggregate likes/comments/shares
  Model retraining      → continuous, on fresh interaction logs
  Cache TTL eviction    → drop dormant users' materialized feeds

Cluster-wide observability (always-on):
  Prometheus            → feed-read p99, fan-out lag, cache hit ratio
  Distributed tracing   → per-request across graph/cache/hydration/ranking
  Product metrics       → time-to-visible, feed engagement, empty-feed rate
```

The core logic (hybrid fan-out, pre-computed feed caches, storing IDs not content, cursor pagination, time-decay ranking, the dual-index follow graph) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with async fan-out, sharded caches, ML ranking, privacy filtering, multi-region serving, and full observability.
