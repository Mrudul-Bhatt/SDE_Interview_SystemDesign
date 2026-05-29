# Technical Glossary — Foundational Designs

Terms found across the URL Shortener, Search Autocomplete, and Web Crawler projects.

---

## Data Structures & Algorithms

| Term | Where | One-line meaning |
|---|---|---|
| **Trie / Prefix Tree** | Search Autocomplete | A tree where each node is one character; shared prefixes share nodes |
| **Top-K** | Search Autocomplete | Keep only the K highest-ranked items, discard the rest |
| **LRU Cache** | All three projects | Memory cache that evicts the item used least recently when full |
| **Priority Queue** | Web Crawler (UrlFrontier) | Queue that always gives you the highest-priority item next, not FIFO |
| **Bloom Filter** | Web Crawler | Probabilistic bit array — can say "definitely not seen" but never "definitely seen" |
| **Polynomial Rolling Hash** | Bloom Filter, ContentHash | A fast way to convert a string into a number by multiplying char codes |
| **Base62 Encoding** | URL Shortener | Converting a number to a string using 62 characters (0–9, a–z, A–Z) instead of 10 |
| **Big-O Notation** | All three projects | Describes how fast an algorithm grows: O(1) = constant, O(N) = linear |

---

## System Design Concepts

| Term | Where | One-line meaning |
|---|---|---|
| **Cache Pre-warming** | URL Shortener, Autocomplete | Populating the cache at write time so the first read is already a hit |
| **Cache Invalidation** | Search Autocomplete (trend surge) | Removing or updating stale cache entries when underlying data changes |
| **Soft Delete** | URL Shortener | Marking a record inactive instead of deleting it, to preserve history |
| **TTL (Time To Live)** | URL Shortener | An expiry duration after which a resource is considered dead |
| **Zipf's Law** | Search Autocomplete | A few items account for most of the traffic — top 200 prefixes = ~80% of queries |
| **URL Normalization / Canonicalization** | Web Crawler | Converting all spellings of the same URL to one standard form |
| **Spider Trap** | Web Crawler | A website that generates infinite URLs to exhaust a crawler |
| **Crawl Politeness / Crawl-delay** | Web Crawler | Intentional delay between requests to the same domain to avoid overloading servers |
| **False Positive / False Negative** | Web Crawler (Bloom Filter) | False positive = wrongly flagged as seen; false negative = wrongly flagged as new |
| **Deduplication** | Web Crawler, Autocomplete | Detecting and skipping content or URLs you've already processed |
| **HTTP 302 / 404 / 410** | URL Shortener | 302 = redirect, 404 = not found, 410 = gone (intentionally removed) |
| **Near-duplicate Detection** | Web Crawler (ContentHash) | Finding pages that are almost identical (same article, different sidebar) |

---

## Concurrency

| Term | Where | One-line meaning |
|---|---|---|
| **Atomic Operation** | URL Shortener (`Interlocked.Increment`) | An operation the CPU completes in one uninterruptible step — no partial state |
| **Race Condition** | URL Shortener | Two threads reading/writing the same data simultaneously, producing wrong results |
| **Lock / Mutex** | All three projects | Forces only one thread to enter a block at a time |
| **Spin Loop** | Web Crawler (spinCount) | A loop that keeps retrying instead of blocking, until a condition is met |

---

## Production Infrastructure (mentioned in comments)

| Term | Where | One-line meaning |
|---|---|---|
| **Redis** | All three projects | An in-memory key-value store used for caching and atomic counters |
| **Kafka** | Web Crawler | A message queue for passing events between services asynchronously |
| **ClickHouse** | Web Crawler | A columnar database designed for fast analytics queries |
| **SimHash / MinHash** | Web Crawler | Hash algorithms where similar content produces similar hash values |
| **S3 / GCS** | Web Crawler | Cloud object storage (Amazon S3, Google Cloud Storage) for large files |
