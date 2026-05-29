# Search Autocomplete — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system.

---

## `Core/TrieNode.cs` — One node in the in-memory tree

**Problem in production:** The entire trie lives inside one server's RAM. At Google scale, the trie for all 5+ billion indexed terms would require hundreds of GB — far too large for a single machine.

**Production replacement: Trie sharded across servers by prefix range**

The trie is split by the first 1–2 characters of the prefix:

```
Shard 1 → prefixes a–f   (one server or cluster)
Shard 2 → prefixes g–m
Shard 3 → prefixes n–s
Shard 4 → prefixes t–z + numbers
```

A request for "ap" is routed to Shard 1 only. Each shard holds its slice of the trie in RAM and handles only its portion of traffic.

Each node's `TopK` list is also persisted to **Redis Sorted Sets** so the trie can be rebuilt after a restart without losing rankings:
```
ZADD trie:node:ap 8200000 "apple watch"
ZADD trie:node:ap 10000000 "apple"
```

---

## `Core/Trie.cs` — The prefix tree with Insert + Search

**Problem in production:** Three issues:
1. Built once at startup from a static list — can't reflect trending searches in real-time
2. `Insert` walks all ancestor nodes updating TopK — at millions of terms this is slow
3. Single server, no replication — if it crashes, autocomplete goes down

**Production replacement: Two separate systems for writes vs reads**

**Write path — hourly batch rebuild:**
```
Search logs (last 30 days)
    ↓
Spark / Hadoop aggregation job
    ↓
(term, frequency) pairs sorted by frequency
    ↓
New trie built on a shadow instance
    ↓
Blue-green swap — new trie goes live atomically
    ↓
Old trie discarded
```
The trie is never mutated live. It's rebuilt from scratch hourly and swapped in atomically — no locking, no partial updates.

**Alternative — Redis Sorted Sets (no custom trie code at all):**
```
ZADD autocomplete:ap  10000000 "apple"
ZADD autocomplete:ap   8200000 "apple watch"
ZADD autocomplete:app  5800000 "app store"

ZREVRANGE autocomplete:ap 0 4   → ["apple", "apple watch", "app store", ...]
```
Redis natively maintains a sorted set with O(log N) insert and O(K) top-K retrieval. The custom `UpdateTopK` logic in `Trie.cs` becomes a single Redis command. This is the approach Bing and many mid-scale systems use.

---

## `Core/PrefixCache.cs` — In-memory LRU cache

**Problem in production:** Same as the URL Shortener's LruCache — lives in one server's process. 20 app servers = 20 independent caches that don't share state.

**Production replacement: Redis as the unified cache layer**

If Redis Sorted Sets replace the trie entirely, the cache disappears as a separate concept — Redis is simultaneously the data store and the cache.

If keeping a custom trie, add **two levels of caching:**

| Level | Technology | What it caches | Why |
|---|---|---|---|
| L1 | In-process dictionary | Last 50 prefixes per server | Sub-millisecond, no network hop |
| L2 | Redis cluster | Top 10,000 prefixes globally | Shared across all servers, survives restarts |
| L3 | CDN edge (Cloudflare) | Single-character prefixes ("a", "b", …) | These never change — cache at the network edge for ~2ms globally |

Single-character prefixes like "a" account for enormous traffic and their results barely change. Caching them at the CDN edge means they never reach your servers at all.

---

## `Models/RankedCompletion.cs` — A suggestion with a frequency score

**Problem in production:** Ranking purely by search frequency is naive. It ignores who is searching, where they are, and what's trending right now.

**Production enhancement: Multi-signal ranking score**

```
Final score = (base frequency)
            × (personalization weight)     ← user's own search history
            × (geographic relevance)       ← "cricket" ranks higher in India
            × (freshness multiplier)       ← trending terms boosted temporarily
            × (safe search filter)         ← blocked terms score = 0
            × (language match)             ← user's browser language
```

The model gains fields:
```csharp
public string Term          { get; set; }
public long   BaseFrequency { get; set; }   // 30-day aggregate
public double PersonalScore { get; set; }   // from user history service
public bool   IsTrending    { get; set; }   // from real-time stream
public string Language      { get; set; }
```

Ranking becomes a scoring function, not just a sort by one number.

---

## `Service/AutocompleteService.cs` — Orchestrator of trie + cache

**Problem in production:** One class handles everything synchronously. At 100,000 queries per second (Google's actual scale), a synchronous single-threaded orchestrator becomes a bottleneck. Also: `RecordTrendSurge` updates the trie synchronously on the request thread — a slow trie update blocks the user's query.

**Production enhancement: Split into read service + async write pipeline**

**Read service** (called on every keystroke):
```
Client types "ap"
    ↓  (with 150ms debounce on the client — don't query every keystroke)
Load balancer routes to correct shard (a–f)
    ↓
L1 cache hit? → return immediately
L2 Redis hit? → return immediately
    ↓ (miss)
Trie lookup → store in L2 → return
```

**Write pipeline** (separate, async):
```
User completes a search → log event to Kafka
    ↓ (Kafka consumer, separate process)
Real-time frequency counter updated in Redis
    ↓ (hourly Spark job)
Trie rebuilt from 30-day aggregated logs
    ↓
Blue-green swap — new trie goes live
    ↓
Cache flushed
```

The key separation: **reads never wait for writes**. Trend surges update rankings in the next hourly rebuild, not synchronously during a user's query.

Also add:
- **gRPC endpoint** — faster than REST for high-frequency keystroke calls (binary protocol, persistent connection)
- **Client-side prefix cache** — browser caches results for prefixes already seen in this session. Typing "appl" reuses the "app" result until "appl" results load
- **A/B testing hooks** — experiment with different ranking algorithms on 1% of traffic without a full deploy

---

## `Program.cs` — Console demo entry point

**Production replacement: ASP.NET Core API controller**

```csharp
[HttpGet("/autocomplete")]
public IActionResult GetSuggestions([FromQuery] string q, [FromQuery] int limit = 5)
{
    if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<string>());
    var results = _autocompleteService.GetCompletions(q, limit);
    return Ok(results);
}
```

Response is cached at the HTTP layer too:
```
Cache-Control: public, max-age=60, stale-while-revalidate=300
```
Meaning: serve this response for 60 seconds, and serve a stale copy for up to 5 minutes while refreshing in the background.

---

## The Full Production Picture

```
User types "ap" (after 150ms debounce)
      ↓
CDN edge (Cloudflare)
  ├─ Single-char prefix ("a") → cached at edge, return instantly
  └─ Longer prefix → App servers (gRPC, horizontal scale)
          ↓
      L1 in-process cache → HIT? return
          ↓ MISS
      L2 Redis Sorted Set → ZREVRANGE autocomplete:ap 0 4
          ↓ MISS
      Trie shard (in-memory, read-only, rebuilt hourly)
          ↓
      Return top-5 suggestions

── Async write pipeline (separate) ─────────────────
User submits search → Kafka → Spark aggregation
    → hourly trie rebuild → blue-green swap → cache flush
```

The core ideas (TopK at every node, LRU cache, prefix normalization, blocklist) all carry forward — only the infrastructure changes from single-process to distributed.
