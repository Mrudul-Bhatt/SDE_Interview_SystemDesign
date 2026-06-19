// HlsStore — the CDN origin server: holds every transcoded segment and master manifest.
//
// THE BIG IDEA:
// Think of HlsStore as a book distribution warehouse. TranscodeWorker prints all the
// editions of a book (360p/720p/1080p) and ships the pages to this warehouse. The master
// manifest is the catalog — it lists all available editions and where to find them.
// CdnEdgeCache is a network of regional libraries that borrow popular pages from the
// warehouse and keep local copies. When a reader (AbrPlayer) wants page 7 of the hardcover
// edition, their nearest regional library (CDN edge) serves it from its local copy —
// the warehouse (HlsStore) is only consulted on the very FIRST request per segment.
//
// HlsStore holds TWO distinct types of data:
//
//   _segments  — the actual video content: one HlsSegment per (videoId, quality, index).
//                Keyed by content-addressed URL. Immutable once written. CDN-cacheable forever.
//
//   _manifests — the routing index: one M3U8 string per videoId that lists all available
//                renditions and their URLs. AbrPlayer fetches this first to discover options.
//
// The FULL read path when a viewer hits play:
//   AbrPlayer.Play()
//     → VideoMetaStore.Get(videoId)              ← is video Ready? get manifest URL
//     → HlsStore.FetchManifest(videoId)          ← which renditions exist?
//     → AbrPlayer.ChooseQuality(throughput)       ← which rendition can my network sustain?
//     → CdnEdgeCache.FetchSegment(url)            ← try the edge cache first
//         cache miss → HlsStore.FetchSegment(url) ← fall back to origin (here)
//         cache hit  → return segment without touching HlsStore
//
// WHY TWO SEPARATE DICTIONARIES (not one combined store):
// Segments and manifests have completely different caching lifetimes:
//
//   _segments:  content-addressed → immutable → CDN TTL: forever (or until eviction).
//               Once seg000.ts is written for a given (videoId, quality), it never changes.
//               A CDN edge can cache it with Cache-Control: max-age=31536000 (1 year).
//
//   _manifests: video-scoped → occasionally updated → CDN TTL: short (e.g. 5 seconds).
//               If a new rendition is added or the manifest is patched, the old cached copy
//               must expire quickly so players pick up the change. A short TTL on manifests
//               avoids a global CDN invalidation on every manifest update.
//
// Using one dictionary for both would lose this distinction — you'd have to apply the
// same (conservative, short) TTL to everything, killing CDN efficiency for segments.
//
// WHY SEGMENTS USE CONTENT-ADDRESSED URLS:
// The URL for a segment is fully determined by three fields: videoId, quality, segmentIndex.
//   "hls/{videoId}/{quality}/seg{index:D3}.ts"
// The same (videoId, quality, index) triple always produces the same URL — and the same
// bytes. This means:
//
//   1. NO CACHE INVALIDATION — a CDN edge can cache a segment forever. If the video is
//      re-transcoded (new codec, bug fix), it gets a new videoId → new URLs. The old cached
//      segments just age out naturally. Without content-addressing, re-transcoding would
//      require a global CDN purge: thousands of edge nodes, minutes of propagation delay,
//      a brief window where some viewers get old segments and some get new ones.
//
//   2. DEDUPLICATION — if two different videos somehow produce identical segment bytes,
//      they would have different videoIds → different URLs → different cache entries.
//      Content-addressing here is per-video, not content-hash-based (that's a separate
//      optimisation some CDNs do at the byte level).
//
//   3. PREDICTABLE KEYS — AbrPlayer computes the URL client-side from just three values.
//      No database lookup needed to find "where is segment 7 of the 720p rendition?" —
//      it's always hls/{videoId}/R720p/seg007.ts.
//
// WHY HasVideo CHECKS _manifests (not _segments):
// A manifest only exists after TranscodeWorker has written ALL segments AND then called
// StoreManifest() (step 2 of the 3-step gate). So HasVideo = "is this video fully
// transcoded and CDN-ready?" — not "does any segment exist for this video?"
// This mirrors the VideoMetaStore Status=Ready gate: both are checked before serving.
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — HlsStore state across the demo pipeline (Program.cs):
//
//   ┌─ Before TranscodeWorker.Process() (just after upload) ─────────────────────────
//   │  _segments  = { }       ← empty; no segments exist yet
//   │  _manifests = { }       ← empty; HasVideo("x9y8z7") = false
//   │
//   │  If AbrPlayer tried to play now:
//   │    VideoMetaStore.Get("x9y8z7") → null (Status not Ready)
//   │    → play aborted before HlsStore is even consulted
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After TranscodeWorker.Process("x9y8z7", 300s, [R360p, R720p, R1080p]) ────────
//   │  numSegments = ceil(300/6) = 50 per rendition  →  150 total segment writes
//   │
//   │  _segments = {
//   │    "hls/x9y8z7/R360p/seg000.ts" → { Quality=R360p, Index=0,  Bitrate=400  }
//   │    "hls/x9y8z7/R360p/seg001.ts" → { Quality=R360p, Index=1,  Bitrate=400  }
//   │    ...
//   │    "hls/x9y8z7/R360p/seg049.ts" → { Quality=R360p, Index=49, Bitrate=400  }
//   │    "hls/x9y8z7/R720p/seg000.ts" → { Quality=R720p, Index=0,  Bitrate=2500 }
//   │    ...
//   │    "hls/x9y8z7/R720p/seg049.ts" → { Quality=R720p, Index=49, Bitrate=2500 }
//   │    "hls/x9y8z7/R1080p/seg000.ts"→ { Quality=R1080p,Index=0,  Bitrate=5000 }
//   │    ...
//   │    "hls/x9y8z7/R1080p/seg049.ts"→ { Quality=R1080p,Index=49, Bitrate=5000 }
//   │  }  ← 150 entries total
//   │
//   │  _manifests = {
//   │    "x9y8z7" → "#EXTM3U\n#EXT-X-VERSION:3\n
//   │                 #EXT-X-STREAM-INF:BANDWIDTH=5000000\nhls/x9y8z7/R1080p/index.m3u8\n
//   │                 #EXT-X-STREAM-INF:BANDWIDTH=2500000\nhls/x9y8z7/R720p/index.m3u8\n
//   │                 #EXT-X-STREAM-INF:BANDWIDTH=400000\nhls/x9y8z7/R360p/index.m3u8"
//   │  }
//   │
//   │  HasVideo("x9y8z7") = true   ← manifest exists → video is CDN-ready
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Bob streams (8000 Kbps) — AbrPlayer picks R1080p ────────────────────────────
//   │
//   │  Position=0s  → SegmentIndex=0/6=0
//   │    CdnEdgeCache.FetchSegment("hls/x9y8z7/R1080p/seg000.ts")
//   │      → cache MISS → HlsStore.FetchSegment(url) → returns { Bitrate=5000 }
//   │      → CDN caches it → served to Bob                    CDN misses: 1
//   │
//   │  Position=6s  → SegmentIndex=1
//   │    CdnEdgeCache.FetchSegment("hls/x9y8z7/R1080p/seg001.ts")
//   │      → cache MISS (first request for this segment) → HlsStore consulted
//   │                                                                CDN misses: 2
//   │  ...
//   │  (Bob watches 4 segments at R1080p → 4 cache misses, each populates CDN cache)
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Scenario 4: second viewer (u2) plays same video immediately after u1 ─────────
//   │
//   │  CdnEdgeCache already holds: "hls/x9y8z7/R1080p/seg000.ts" (cached by u1's play)
//   │
//   │  u2 position=0s → CdnEdgeCache.FetchSegment("hls/x9y8z7/R1080p/seg000.ts")
//   │    → cache HIT → returned directly, HlsStore.FetchSegment NOT called
//   │
//   │  u2 position=6s → CdnEdgeCache.FetchSegment("hls/x9y8z7/R1080p/seg001.ts")
//   │    → cache HIT → same result
//   │
//   │  HlsStore is consulted 0 times for u2's entire playback ← this is the CDN payoff
//   │  (In production: HlsStore = S3 in us-east-1; CDN edge in Tokyo serves u2 from
//   │   its local cache, never crossing the Pacific to reach the S3 origin)
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Quality switch: Eve's network degrades mid-stream ────────────────────────────
//   │
//   │  Eve at position=12s (SegmentIndex=2), quality drops R720p → R360p:
//   │    Old URL: "hls/x9y8z7/R720p/seg002.ts"   ← was being fetched
//   │    New URL: "hls/x9y8z7/R360p/seg002.ts"   ← same index, different quality
//   │
//   │  HlsStore holds BOTH — the quality switch is just fetching a different URL for
//   │  the same time window. No state change in HlsStore needed; the segment was written
//   │  during transcoding and is already waiting at its content-addressed URL.
//   └─────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;

public class HlsStore
{
    // Content-addressed segment store: URL → HlsSegment.
    // The URL is computed from (videoId, quality, segmentIndex) — see HlsSegment.Url.
    // Because URLs are content-addressed, this dictionary never needs to update an
    // existing entry (same URL always means same bytes). StoreSegment is write-once per key.
    private readonly Dictionary<string, HlsSegment> _segments = [];

    // videoId → M3U8 master manifest text. One entry per fully-transcoded video.
    // Only written after ALL _segments for that videoId are in place (TranscodeWorker step 2).
    // In production: also stored in S3 with a short Cache-Control TTL so CDN edges
    // re-validate it quickly when a new rendition is added, while segments use a long TTL.
    private readonly Dictionary<string, string> _manifests = [];

    // Writes one 6-second segment into the store. Called by TranscodeWorker for every
    // (rendition × segmentIndex) combination — 150 calls for a 300s video at 3 renditions.
    // Uses seg.Url as the key (content-addressed), so calling this twice with the same
    // segment is idempotent — the second write is a no-op in effect.
    public void StoreSegment(HlsSegment seg) => _segments[seg.Url] = seg;

    // Writes the master M3U8 manifest for a video. Called by TranscodeWorker AFTER all
    // segments are written (step 2 of the 3-step gate). Until this is called, HasVideo
    // returns false — the video is not yet considered CDN-ready, regardless of how many
    // segments are already stored.
    public void StoreManifest(string videoId, string m3u8) => _manifests[videoId] = m3u8;

    // Returns the segment at the given URL, or null if it doesn't exist.
    // Returns null (not throws) because the caller — CdnEdgeCache — treats null as a
    // cache miss and handles it gracefully. Throwing would require a try/catch in every
    // player tick, which is noisy for a normal cache-miss code path.
    // In production: this is an S3.GetObject() call, which returns null (404) vs a stream.
    public HlsSegment FetchSegment(string url)
    {
        _segments.TryGetValue(url, out var seg);
        return seg; // null = segment not found (never transcoded, or URL computed wrong)
    }

    // Returns the M3U8 manifest string for a video, or null if not transcoded yet.
    // AbrPlayer calls this first to discover which renditions exist and what their URLs are.
    // Null means the video is still Uploading or Transcoding — caller should return "not ready."
    public string FetchManifest(string videoId)
    {
        _manifests.TryGetValue(videoId, out var m);
        return m;
    }

    // True only when the master manifest has been written for this videoId.
    // Because TranscodeWorker writes the manifest AFTER all segments (step 2 of 3),
    // HasVideo=true guarantees that all segments for all renditions already exist.
    // Used by CdnEdgeCache and the demo to verify a video is fully ready before streaming.
    public bool HasVideo(string videoId) => _manifests.ContainsKey(videoId);

    // Returns all segments for a given (videoId, rendition) pair, sorted by index.
    // Used in tests and the demo to verify a full rendition was written by TranscodeWorker.
    //
    // WHY THIS IS O(N) ACROSS ALL SEGMENTS:
    // This scans the entire _segments dictionary (all videos, all qualities). In production
    // this method doesn't exist — segments are always fetched one at a time by exact URL,
    // never listed. If listing were needed (e.g. for admin tooling), a secondary index
    // _segmentsByVideo[videoId][rendition] → List<HlsSegment> would make it O(1).
    // The linear scan here is acceptable only because the demo has a small number of videos.
    public List<HlsSegment> GetRenditionSegments(string videoId, Rendition r) =>
        _segments.Values
                 .Where(s => s.VideoId == videoId && s.Quality == r)
                 .OrderBy(s => s.SegmentIndex)
                 .ToList();
}
