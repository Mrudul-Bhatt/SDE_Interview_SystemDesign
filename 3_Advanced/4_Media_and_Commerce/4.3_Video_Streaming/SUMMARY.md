# Video Streaming — Beginner Summary

## What is this project?

A **Video Streaming** platform (like YouTube, Netflix, or Twitch) takes a video someone uploads and makes it watchable by millions of people — on a fast laptop, a slow phone, a smart TV — each getting the best quality their connection can handle, starting in under a second, resuming exactly where they left off.

Think of it like a restaurant kitchen. A supplier drops off raw ingredients at the back door (**upload**). The kitchen doesn't serve those raw ingredients — it cooks them into several ready-to-eat dishes at different portion sizes (**transcoding into quality levels**). Those dishes are stocked in fridges close to every dining room around the world (**CDN**). When a customer orders, a waiter brings the portion that fits their appetite right now and keeps adjusting as they eat (**adaptive bitrate**). Meanwhile the manager tallies how many people actually ate a real meal, not just glanced at the menu (**view counting**).

The hard part isn't playing one video. It's running this whole pipeline for **500 hours of video uploaded every minute** and **billions of views a day**.

---

## The Big Challenges

1. **Uploads are huge and networks are flaky.** A 4K video is gigabytes. If the connection drops at 90%, the user must *not* have to restart from zero.
2. **One video, many devices and speeds.** A phone on 3G and a TV on fibre can't get the same file. You need multiple quality versions and a way to switch between them mid-playback.
3. **Latency — playback must start instantly.** Nobody waits 5 seconds for a video. The bytes have to be physically close to the viewer.
4. **Scale of view counting.** Incrementing a database row for every one of a billion daily views would melt any database. And bots try to fake views.
5. **Never serve a half-baked video.** A viewer must never see a video marked "ready" whose pieces aren't all in place yet.

Every file in this project solves a piece of one of these problems.

---

## The Pipeline — The One Big Idea

Everything in this project is a stage in a one-way assembly line:

```
   UPLOAD            TRANSCODE              DISTRIBUTE           PLAY
┌──────────┐     ┌───────────────┐      ┌────────────┐     ┌──────────┐
│ chunked  │     │ raw → many     │      │ origin →    │     │ ABR picks │
│ resumable│ ──▶ │ quality levels │ ──▶  │ CDN edge   │ ──▶ │ quality  │
│ upload   │     │ (HLS segments) │      │ caches     │     │ per seg  │
└──────────┘     └───────────────┘      └────────────┘     └──────────┘
  RawVideoStore    TranscodeWorker         CdnEdgeCache       AbrPlayer
                   → HlsStore                                 + ViewCounter
                   → VideoMetaStore                           + WatchHistory
```

The key insight: **the video is cooked once (at upload time) and served millions of times.** All the expensive work — transcoding into 4–5 quality levels, slicing into segments — happens once, up front. Playback is then just "download the right pre-made pieces," which is cheap and infinitely cacheable.

---

## Two Technologies You Need to Know

**HLS (HTTP Live Streaming):** Instead of one giant video file, the video is chopped into **~6-second segments**, and a **manifest** (a `.m3u8` text file) lists them. The player downloads the manifest first to learn what's available, then fetches segments one at a time over plain HTTP. Because it's just HTTP file downloads, every CDN on earth already knows how to cache it.

**ABR (Adaptive Bitrate):** The same video is transcoded into multiple **renditions** (360p, 480p, 720p, 1080p, 4K). Before each segment, the player measures the current network speed and picks the highest quality that won't stall. This is why YouTube quality bounces between sharp and blurry as your wifi fluctuates — that's ABR working as designed.

---

## The Files — What Each One Does

### The Models (the data shapes)

**`Models/Enums.cs`** — Two core enums. `VideoStatus` is the lifecycle: `Uploading → Transcoding → Ready → Deleted`. Players only ever show `Ready` videos. `Rendition` is the set of quality levels (360p through 4K).

**`Models/VideoMetadata.cs`** — The "row" describing a video (lives in Cassandra in production): title, uploader, status, duration, view count, and crucially the `ManifestUrl` — the CDN path to the master manifest that players fetch first. The `ViewCount` is *approximate* (batch-updated), which is the standard trade-off at YouTube scale.

**`Models/HlsSegment.cs`** — One ~6-second slice of one quality level. The clever bit is the `Url`: it's derived from `(videoId, quality, segmentIndex)`, so a given segment **always has the same URL and never changes**. This content-addressing is why CDNs can cache segments forever without ever serving stale data — a re-transcode produces *different* URLs.

**`Models/UploadSession.cs`** — Tracks one chunked upload in progress. `ReceivedChunks` is a **Set**, which makes re-sending a chunk after a network hiccup harmless (idempotent) — adding the same chunk index twice does nothing. `IsComplete` checks the *count* of chunks, so they can arrive out of order.

**`Models/WatchProgress.cs`** — One user's playhead position in one video. This is what powers "resume where you left off." Written every segment by the player (debounced to ~every 10s in production to avoid hammering the database).

### The Core Logic

**`Core/BitrateTable.cs`** — The single source of truth mapping each rendition to its target bitrate (360p = 400 kbps ... 4K = 16,000 kbps). It's centralized so the **transcoder** and the **player** agree on the numbers. If they disagreed, the player might pick a quality the network can't sustain (stall) or play lower than necessary (wasted bandwidth).

### The Services (the pipeline stages)

**`Service/UploadService.cs`** — Chunked, resumable upload:
```
Init     → assigns a videoId BEFORE any bytes arrive
            (so the client has a stable ID to resume against if the connection drops)
ReceiveChunk → idempotent: re-sends are safe (HashSet add)
GetResumePoint → "which chunk is missing?" → client restarts from exactly there
Complete → only when ALL chunks present → store raw bytes → queue a transcode job
```
That transcode queue stands in for **Kafka** — a durable message so transcoder workers can be scaled independently of the upload tier.

**`Service/TranscodeWorker.cs`** — The kitchen. Converts one raw upload into HLS segments across multiple renditions. **The order of operations is the whole point:**
```
1. Write ALL segments first.
2. Write the master manifest second.
3. Flip status to Ready LAST.   ← the atomic "go live" moment
```
This guarantees a player never sees `Ready=true` for a video whose manifest points to segments that don't exist yet. (In production this runs on a GPU farm using FFmpeg, transcoding renditions in parallel.)

**`Service/AbrPlayer.cs`** — The client-side brain. On open, it reads `WatchHistory` to resume from the saved position. Then, per segment:
```
ChooseQuality():
  if buffer < 5s  → EMERGENCY DROP to 360p   (continuity beats quality)
  else            → pick highest rendition whose bitrate fits within
                    80% of measured throughput  (20% headroom absorbs dips)
```
Each "tick" simulates downloading a segment (which drains the buffer), adding 6s of playback, advancing the playhead, firing a view heartbeat, and checkpointing watch position. **The 80% rule and the buffer floor are the essence of ABR** — be greedy for quality, but never so greedy that playback stalls.

**`Service/ViewCounter.cs`** — Buffered, anti-fraud view counting:
```
Why buffered: incrementing a DB row per playback × a billion/day = dead database.
   → buffer heartbeats (Kafka in production), flush aggregated counts every ~60s.

Anti-fraud floor: a "view" requires ≥30 seconds of actual playback.
   → stops a bot that opens-and-closes videos to inflate the counter.
```

### The Storage Layer

**`Storage/RawVideoStore.cs`** — The S3 bucket for original uploads. Rule: **never delete originals** — they're the only loss-free source if you ever need to re-transcode (new codec, bug fix). Production moves them to cheap cold storage (Glacier) after 30 days.

**`Storage/HlsStore.cs`** — The **CDN origin**: stores all segments (by URL) and manifests. This is the authoritative copy the CDN pulls from on a cache miss.

**`Storage/VideoMetaStore.cs`** — The metadata database. Provides `Get`, `Search` (toy linear scan; real systems use Elasticsearch), and `Trending` (sorts by view count; real systems blend view *velocity* + recency so a fresh viral video can outrank an old cumulative hit).

**`Storage/WatchHistory.cs`** — Per-user playhead positions, powering resume. In production: Cassandra partitioned by `user_id`, also used as a behavioral signal for recommendations.

### The Infrastructure

**`Infrastructure/CdnEdgeCache.cs`** — One CDN edge node between viewers and origin:
```
GetSegment(url):
  in cache?  → HIT  → serve instantly (bytes are physically near the viewer)
  miss?      → fetch from origin (HlsStore) → cache it → serve
```
`HitRate` is the headline metric — production targets **95%+** for popular content. The content-addressed segment URLs are what make this caching so effective: the same segment is requested by millions of viewers under the identical URL.

### `Program.cs` — The Demo

Runs 5 scenarios covering the full pipeline:

| Scenario | What it demonstrates |
|---|---|
| 1 | Full pipeline — upload → transcode → stream at 1080p → resume from saved position → flush views |
| 2 | Interrupted upload — only chunk 0 arrives; `GetResumePoint` says "restart at chunk 1"; completes after reconnect |
| 3 | ABR switching — Eve on 500 kbps gets 360p; same video on 6000 kbps gets high quality |
| 4 | CDN hit rate — first viewer misses (fills cache); second viewer of the same video hits |
| 5 | Search & trending — find videos by tag/title, rank by views |

---

## The Big Picture — How It All Fits Together

```
UPLOAD (Alice uploads vacation.mp4, 15 MB in 3 chunks):

UploadService.Init("alice", "vacation.mp4", 15MB)
   → videoId assigned up front, 3-chunk session created
UploadService.ReceiveChunk(0), ReceiveChunk(1), ReceiveChunk(2)
   → each idempotent; a re-send is harmless
UploadService.Complete()
   → all chunks present → RawVideoStore.Store() → queue transcode job


TRANSCODE (worker picks up the job):

TranscodeWorker.Process(videoId, renditions=[360p,720p,1080p])
   → for each rendition: slice into 6s segments → HlsStore.StoreSegment()
   → HlsStore.StoreManifest()        (only after all segments written)
   → VideoMetaStore status = Ready   (the "go live" flip)


STREAM (Bob watches on a fast connection):

AbrPlayer("bob", videoId)
   → WatchHistory.Get() → resume from last position
   → CdnEdgeCache.GetManifest()  (miss → origin → cache)
   → per segment:
        ChooseQuality(throughput=8000) → 1080p fits within 80% → pick it
        CdnEdgeCache.GetSegment(url)   (miss first time, hit after)
        buffer math, advance playhead
        ViewCounter.RecordHeartbeat()
        WatchHistory.Update()          (so resume works next time)


COUNT (every 60s):

ViewCounter.Flush()
   → for each viewer: played ≥30s? → count a real view
   → VideoMetaStore.ViewCount += valid views


RESUME (Bob reopens later):

AbrPlayer("bob", videoId)
   → WatchHistory.Get() → starts exactly where Bob stopped ✓
```

---

## Why This Design Is Used Everywhere

- **Cook-once, serve-many** is the foundation of all video platforms — the expensive transcode happens once at upload; playback is just cacheable file downloads. This is why a video that costs cents to transcode can serve millions of views cheaply.
- **HLS segments + manifest** is the universal streaming format (YouTube, Netflix, Twitch, Disney+) precisely because "a folder of small files listed in a text file" is something every CDN and every device already understands — no special video protocol needed.
- **Content-addressed segment URLs** are why CDN hit rates hit 95%+ — the same URL for the same bytes means a segment cached for one viewer serves the next million for free.
- **Adaptive bitrate** is why streaming "just works" across every device and connection — the player, not the server, continuously picks the right quality, degrading gracefully instead of stalling.
- **Resumable chunked upload** is the standard for any large file transfer (Google Drive, Dropbox, S3 multipart) — assign a stable ID, track chunks idempotently, resume from the gap.
- **Buffered, validated view counting** is how every platform counts billions of events without a database meltdown and without rewarding bots — aggregate in a stream, flush periodically, require real engagement.
- **Persist-then-publish ordering** (segments → manifest → Ready) is the same durability-first discipline seen across the other projects — never expose a thing until all of it is safely in place.
