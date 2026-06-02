# Video Streaming — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on YouTube, Netflix, and Twitch).

---

## `Models/Enums.cs` — Status + Rendition enums

**Problem in production:** A fixed `Rendition` enum (360p…4K) and a simple `VideoStatus` can't express the real matrix: multiple codecs (H.264, H.265/HEVC, AV1, VP9), audio-only tracks, HDR variants, and per-region availability.

**Production replacement: A codec/rendition matrix + a richer lifecycle**

```
Rendition becomes (resolution × codec × profile):
  360p-h264, 720p-h264, 1080p-h265, 4K-av1, audio-only-128k, ...
  → newer codecs (AV1) cut bandwidth ~30–50% but cost more to encode;
    platforms ship multiple codecs and let the player pick what it supports.

VideoStatus gains: Quarantined (content-ID / moderation hold),
  GeoBlocked, ProcessingFailed, with an audit trail of transitions.
```

---

## `Models/VideoMetadata.cs` — Plain row with an approximate view count

**Problem in production:** One flat row mixes slow-changing metadata (title, uploader) with fast-changing counters (views) and search/discovery fields — different access patterns that don't belong together.

**Production replacement: Split metadata, counters, and a search index**

```
Metadata DB (Cassandra/Spanner):  title, uploader, duration, manifest URL, status
View counter (separate):          sharded counters, batch-rolled-up (already approximate — correct call)
Search/discovery (Elasticsearch): title, tags, transcript, indexed at "became Ready"
Recommendation features (store):  watch-time, CTR, embeddings for the rec model
```

The demo's "ViewCount is approximate" note is exactly right and stays — exact per-view DB increments don't scale.

---

## `Models/HlsSegment.cs` — Segment with a content-addressed URL

**Problem in production:** The content-addressed URL design is excellent and carries forward almost unchanged. The gap is that segments are raw `byte[]` in memory rather than encrypted, packaged files on disk/CDN.

**Production replacement: Packaged, encrypted segments in object storage**

```
- Stored as real .ts (or fMP4/CMAF) files in S3/GCS, fronted by a CDN.
- Encrypted for DRM: Widevine / FairPlay / PlayReady — the player fetches a
  decryption key from a license server before playback.
- CMAF lets one set of segments serve both HLS and DASH (one encode, two protocols).
- Content-addressed URLs keep the demo's key property: immutable URL → safe to
  cache forever; a re-transcode produces NEW URLs, never stale bytes.
```

---

## `Models/UploadSession.cs` — Chunk-tracking session

**Problem in production:** The resumable-upload model (idempotent chunk Set, count-based completion) is exactly right. It just needs durability and integrity checks.

**Production replacement: Durable session state + per-chunk checksums**

```
- Session state in Redis/DB (not in-process) so any upload server can resume it.
- Per-chunk checksum (MD5/CRC) verified on receive — the demo's null check
  stands in for this; production rejects corrupted chunks and re-requests them.
- Direct-to-storage uploads: clients often PUT chunks straight to S3 multipart
  (presigned URLs), so bytes never pass through your app tier at all.
```

---

## `Models/WatchProgress.cs` — Per-user playhead

**Problem in production:** Correct model; the only gap is write volume — saving every segment would hammer the database.

**Production replacement: Debounced writes + a behavioral signal**

```
- Debounce playhead writes to ~every 10s (the demo notes this) or on
  pause/seek/close, not every frame.
- Cassandra partitioned by user_id, clustered by last_updated DESC →
  "continue watching" is a fast single-partition scan.
- Doubles as a recommendation signal (what/how-long you watched).
```

---

## `Core/BitrateTable.cs` — Static rendition→bitrate map

**Problem in production:** A single fixed bitrate per resolution ignores that content complexity varies — a static talking-head needs far fewer bits than fast-motion sports at the same resolution.

**Production replacement: Per-title / per-scene adaptive encoding**

```
- Per-title encoding (Netflix): analyze each video and pick a custom
  bitrate ladder — simple content gets lower bitrates at the same quality.
- Per-scene / per-shot encoding: vary bitrate within a video by complexity.
- The table stays as the player↔encoder contract, but the values become
  per-video, computed at transcode time, and embedded in the manifest.
```

---

## `Service/UploadService.cs` — In-process chunk assembly + a queue

**Problem in production:** Synchronous assembly in one process; the transcode "queue" is an in-memory `Queue`. Real uploads are huge, parallel, and must survive server failure.

**Production replacement: Direct-to-S3 multipart + Kafka job queue**

```
1. Init → create session, return presigned multipart URLs (or an upload-server target)
2. Client uploads chunks DIRECTLY to S3 (parallel, resumable, integrity-checked)
3. Complete → S3 assembles the multipart object
4. Publish a "transcode.requested" event to Kafka  ← durable, the demo's queue
5. Transcode workers consume independently and auto-scale on queue depth
```

This decouples the upload tier from the transcode tier so each scales on its own.

---

## `Service/TranscodeWorker.cs` — Sequential simulated transcode

**Problem in production:** The single biggest compute cost in the system, and the demo simulates it sequentially in-process. The crucial **segments → manifest → Ready** ordering is correct and must be preserved.

**Production replacement: Distributed FFmpeg on a GPU/CPU farm, parallel + chunked**

```
- Split the source into chunks; transcode chunks in PARALLEL across a worker
  fleet (GPU for popular codecs, CPU for the long tail). A 1-hour 4K video
  finishes in minutes, not hours.
- Each rendition × codec is an independent job; fan out, then assemble.
- Package into CMAF/HLS/DASH, encrypt for DRM, write segments to S3.
- Write the manifest only AFTER all segments land; flip status to Ready LAST
  — the demo's ordering is the production "go-live" atomicity guarantee.
- Idempotent + retryable: a failed chunk re-transcodes without redoing the rest.
- Pipeline also runs: thumbnail extraction, content-ID/copyright scan,
  moderation, captions/transcription, preview-sprite generation.
```

---

## `Service/AbrPlayer.cs` — Throughput-based quality selection

**Problem in production:** The 80%-of-throughput + buffer-floor heuristic is a solid classic ABR baseline. Modern players use smarter, hybrid algorithms and run on-device, not server-side.

**Production replacement: Buffer-based / hybrid ABR in a real client player**

```
- Real algorithms: BOLA (buffer-based), or hybrid throughput+buffer models
  (what dash.js / hls.js / ExoPlayer / AVPlayer ship) — fewer stalls, fewer
  needless quality switches than pure throughput-based selection.
- Runs in the client (browser MSE, mobile SDK, smart-TV app), not the server.
- Extras the demo omits: low-latency HLS/DASH (chunked CMAF) for near-live,
  fast startup (begin low, ramp up), bandwidth estimation smoothing, and
  capping quality to the screen resolution to save bytes.
- Still fetches the manifest first, still picks per-segment — that core loop
  carries forward.
```

---

## `Service/ViewCounter.cs` — Buffered counter with an anti-fraud floor

**Problem in production:** The buffer-and-flush design with a minimum-playback floor is exactly the right pattern. Production hardens the fraud detection and the pipeline.

**Production replacement: Stream-processed view validation (Kafka + Flink)**

```
- Heartbeats → Kafka → a stream processor (Flink/Spark) aggregates and validates.
- Anti-fraud beyond the 30s floor: IP/device diversity, bot fingerprinting,
  velocity limits, and a 48-hour delayed reconciliation for monetized views
  (the demo names these as the production additions).
- Views land in a scalable counter store; exact counts settle later, approximate
  counts show immediately — the standard YouTube-scale trade-off.
```

---

## `Storage/RawVideoStore.cs` — In-memory original storage

**Problem in production:** Originals are gigabytes each and must never be lost (they're the only re-transcode source).

**Production replacement: S3 with lifecycle tiering**

```
S3 Standard (≈30 days, hot for re-transcode) → S3 Glacier / Deep Archive (cold).
Never deleted — a new codec or an encoding bug means re-pulling the original.
Cross-region replication for durability.
```

---

## `Storage/HlsStore.cs` — In-memory origin

**Problem in production:** This is the CDN **origin** — it must be durable object storage, not RAM.

**Production replacement: S3/GCS as origin behind the CDN**

Segments and manifests live in object storage; the CDN pulls on a miss (the demo's `CdnEdgeCache` → `HlsStore` relationship). Origin is rarely hit because content-addressed segments cache extremely well.

---

## `Storage/VideoMetaStore.cs` — Dictionary with linear-scan search

**Problem in production:** A `Dictionary` can't hold billions of videos, and `Search`/`Trending` as linear scans are O(everything).

**Production replacement: Cassandra + Elasticsearch + a trending pipeline**

```
Metadata:  Cassandra/Spanner, keyed by video_id.
Search:    Elasticsearch indexed on title/tags/transcript at "became Ready".
Trending:  a streaming job blending view VELOCITY + recency + region (not just
           cumulative count) — so a 24h-old viral video can outrank an old hit.
```

---

## `Storage/WatchHistory.cs` — Per-user playhead store

**Problem in production:** Correct shape; needs to scale and feed recommendations.

**Production replacement: Cassandra partitioned by user, also a rec signal**

Partitioned by `user_id`, clustered by `last_updated DESC` → "continue watching" and watch-history queries are single-partition scans; the same data feeds the recommendation model.

---

## `Infrastructure/CdnEdgeCache.cs` — One edge node, hit/miss tracking

**Problem in production:** A single in-process cache; real CDNs are global, multi-tier, with eviction and pre-warming.

**Production replacement: A multi-tier global CDN (CloudFront/Akamai/own POPs)**

```
- Hundreds of edge POPs near viewers; a mid-tier shield layer in front of origin
  so a cold object is fetched from origin ONCE, not once per edge.
- LRU eviction + pre-warming/push for predictably popular content
  (a new episode, a creator's scheduled drop).
- 95%+ hit rate for popular content (the demo's metric) thanks to immutable,
  content-addressed segment URLs.
- ISP-embedded caches (Netflix Open Connect, YouTube Google Global Cache) put
  segments inside the ISP's network — the last-mile latency win.
```

---

## `Program.cs` — Sequential demo scenarios

**Problem in production:** A single process running scenarios; production is many always-on services.

**Production replacement: Deployed microservices + pipelines**

```
Upload service      → presigned multipart, session state
Transcode fleet     → FFmpeg on GPU/CPU, auto-scaled on Kafka depth
Packaging/DRM       → CMAF + encryption + license server
Storage             → S3 (raw + HLS origin), Cassandra (metadata/history)
CDN                 → global multi-tier edge
Player SDKs         → web/mobile/TV with buffer-based ABR
View pipeline       → Kafka + Flink validation
Search/trending     → Elasticsearch + streaming jobs
```

The five demo scenarios map to real concerns: the full pipeline, resumable upload, ABR switching, CDN hit rate, and search/trending.

---

## Cross-cutting concerns not modelled in this project

### 1. DRM & content protection

Premium content is encrypted; the player fetches a license/key from a DRM license server (Widevine/FairPlay/PlayReady) gated by entitlement checks. Plus signed/expiring CDN URLs and token auth so segment URLs can't be freely shared.

### 2. Live streaming & low latency

Live (Twitch/YouTube Live) reuses the segment+manifest model but with tiny chunks (LL-HLS/LL-DASH, chunked CMAF) to push glass-to-glass latency from ~30s toward ~2–5s, plus a real-time ingest path (RTMP/SRT → transcode → package).

### 3. Observability & QoE

```
qoe_startup_time_seconds        time-to-first-frame (the headline player metric)
qoe_rebuffer_ratio              % of playback spent stalled (the #1 quality killer)
qoe_avg_bitrate                 delivered quality
cdn_hit_ratio / origin_offload  caching health
transcode_queue_depth / latency pipeline backlog
```

Players beacon QoE telemetry; dashboards alert on rebuffer-ratio and startup-time regressions by region/ISP/device.

### 4. Recommendations & discovery

The home feed, "up next," and search ranking run ML models on watch history, embeddings, and engagement — a large subsystem this project only hints at via `WatchHistory` and `Trending`.

### 5. Cost & efficiency

Egress bandwidth is the dominant cost — hence aggressive caching, ISP-embedded caches, and newer codecs (AV1) to cut bytes. Transcode compute is the second cost — hence per-title encoding to avoid over-spending bits.

### 6. Multi-region & availability

Metadata replicates across regions; viewers hit the nearest CDN and the nearest read replica. Uploads land regionally and replicate; transcode can run wherever capacity is cheapest.

---

## The Full Production Picture

```
UPLOAD:
  Upload service → presigned S3 multipart → client uploads chunks directly
  Complete → S3 assembles object → publish "transcode.requested" to Kafka


TRANSCODE (auto-scaled fleet consumes the event):
  split source → transcode chunks in PARALLEL (GPU/CPU) across renditions×codecs
  → package CMAF + DRM-encrypt → write segments to S3
  → write manifest AFTER all segments land
  → flip status to Ready LAST (atomic go-live)
  → side pipelines: thumbnails, content-ID, moderation, captions


STREAM:
  Player SDK → fetch manifest (via CDN) → per segment:
     buffer-based ABR picks rendition (capped to screen, ramp from low)
     GET segment from nearest CDN edge (miss → shield → origin S3, then cached)
     DRM license fetched once before playback
     beacon QoE (startup, rebuffer, bitrate) + view heartbeat
     debounced playhead write to WatchHistory (resume support)


COUNT:  heartbeats → Kafka → Flink validates (30s floor + anti-fraud) → counters

Background / always-on:
  Transcode workers      → drain the job queue
  CDN pre-warm/eviction  → keep popular content hot
  View validation        → stream-process heartbeats, delayed reconciliation
  Trending/search index  → streaming jobs + Elasticsearch
  Lifecycle tiering       → originals Standard → Glacier

Observability (always-on):
  QoE telemetry          → startup time, rebuffer ratio, avg bitrate by region
  Pipeline metrics       → transcode queue depth/latency, CDN hit ratio
  Distributed tracing    → upload → transcode → CDN → player
```

The core logic (cook-once-serve-many, HLS segments + manifest, content-addressed cacheable URLs, adaptive bitrate, resumable chunked upload, buffered+validated view counting, persist-segments-then-manifest-then-Ready ordering) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with distributed transcoding, DRM, a global multi-tier CDN, real client player SDKs, stream-processed analytics, and full QoE observability.
