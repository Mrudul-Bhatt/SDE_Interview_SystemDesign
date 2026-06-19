// VideoMetaStore — the catalogue: one searchable record per video, keyed by VideoId.
//
// THE BIG IDEA:
// If RawVideoStore is the film vault and HlsStore is the distribution warehouse, then
// VideoMetaStore is the library card catalogue. It holds no video bytes at all — just the
// "cards" (VideoMetadata) that describe each video: its title, tags, view count, status,
// and a pointer (ManifestUrl) to where the actual playable content lives. Every user-facing
// feature that needs to FIND or DESCRIBE a video reads from here:
//
//   "Is this video ready to play?"     → Get(videoId).Status
//   "Show me videos about cats"        → Search("cats")
//   "What's hot right now?"            → Trending()
//   "Where's the manifest for play?"   → Get(videoId).ManifestUrl
//
// Only TWO components ever WRITE here, and both go through Upsert():
//   UploadService.Init()       → creates a stub row (Status=Uploading) so the video exists
//                                 in the catalogue the instant the upload begins.
//   TranscodeWorker.Process()  → overwrites that row with the full record and flips
//                                 Status=Ready — the moment the video becomes playable.
//
// WHY EVERY READ FILTERS Status == Ready:
// A video sitting in the catalogue is NOT necessarily watchable. During Uploading and
// Transcoding its segments don't exist yet (or only partially), and ManifestUrl is null.
// If Search() or Trending() returned such a video, a user could click it and hit a 404 or
// a broken player. Filtering to Status==Ready is the guarantee that "if you can find it,
// you can play it." This mirrors the same Ready-gate enforced in TranscodeWorker (segments
// → manifest → Ready, in that order) and HlsStore.HasVideo.
//
// WHY THIS MAPS TO CASSANDRA (in production):
// The dominant access pattern is Get(videoId) — fetch one video by its ID. That's a
// primary-key lookup, which Cassandra does in O(1) by routing straight to the node owning
// that partition. VideoId is the partition key; there's no clustering key because each
// video is exactly one row. Cassandra is chosen over a relational DB because:
//   - Reads massively outnumber writes (a video is written ~twice, read millions of times)
//   - The data is naturally partitioned by VideoId — no cross-video joins needed
//   - It scales horizontally: add nodes to hold more videos, no re-sharding pain
//
// WHY Search() AND Trending() ARE "TOY" HERE (and what production really uses):
// Both methods below do a LINEAR SCAN of the entire _db — fine for a demo with a handful
// of videos, catastrophic at scale (scanning billions of rows per search). In production:
//
//   Search  → Elasticsearch. When a video flips to Ready, its Title and Tags are pushed
//             into an inverted index. A search for "cats" then hits the index in
//             milliseconds instead of scanning every video. The index is updated
//             asynchronously at "video became Ready" time, not on every read.
//
//   Trending → A precomputed leaderboard. A background job continuously ranks videos by
//             view VELOCITY (views per hour) blended with RECENCY, and writes the top-N
//             to a small Redis sorted set. Trending() then just reads that tiny set —
//             never scanning the full catalogue. (See WHY TRENDING SORTS BY VELOCITY below.)
//
// WHY TRENDING SORTS BY VELOCITY + RECENCY (not raw cumulative ViewCount):
// The demo's Trending() sorts by total ViewCount descending — so a 5-year-old video with
// 50M lifetime views always outranks a 24-hour-old video exploding with 2M views today.
// That's wrong for "trending," which means "hot RIGHT NOW." Real systems compute something
// like (views in last 24h) / (age + constant)^gravity — the same time-decay idea used in
// FeedRanker for the social feed. A fresh viral video has enormous recent velocity and
// rises; an old hit has high total but low current velocity and sinks. The demo skips this
// for simplicity, but it's the single biggest gap between this code and a real system.
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — VideoMetaStore state across the demo (Program.cs):
//
//   ┌─ During Scenario 1: Alice uploads + transcodes "vacation.mp4" ─────────────────
//   │
//   │  After UploadService.Init() (NOTE: in this demo Init does not write metadata —
//   │  the row first appears when TranscodeWorker runs. In production Init writes a
//   │  Status=Uploading stub here so the video is trackable from the first byte):
//   │    _db = { }                          (or { "x9y8z7" → {Status=Uploading} } in prod)
//   │
//   │  After TranscodeWorker.Process("x9y8z7", 300s, "Vacation 2024", "alice"):
//   │    _db = {
//   │      "x9y8z7" → { Title="Vacation 2024", Status=Ready, ViewCount=0,
//   │                   DurationSeconds=300, UploaderId="alice",
//   │                   ManifestUrl="hls/x9y8z7/manifest.m3u8", Tags=[] }
//   │    }
//   │    Get("x9y8z7").Status == Ready  → AbrPlayer is now allowed to stream it
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After Scenario 1 view counting + Scenario 5 manual seeding ───────────────────
//   │  (Scenario 5 directly Upserts three search-test videos)
//   │
//   │  _db = {
//   │    "x9y8z7" → { Title="Vacation 2024",     Status=Ready, ViewCount=1   },  ← Bob watched
//   │    "vid001" → { Title="Funny Cats 2024",   Status=Ready, ViewCount=5_000_000,
//   │                                              Tags=["cats","funny"]        },
//   │    "vid002" → { Title="Cat Training Tips",  Status=Ready, ViewCount=1_200_000,
//   │                                              Tags=["cats","tutorial"]     },
//   │    "vid003" → { Title="Dog vs Cat",         Status=Ready, ViewCount=  800_000,
//   │                                              Tags=["dogs","cats"]         },
//   │    ... (carol's, dave's, etc. from earlier scenarios)
//   │  }
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Scenario 5: Search("cats") ───────────────────────────────────────────────────
//   │
//   │  q = "cats"
//   │  Scan _db, keep Ready videos whose Title OR any Tag contains "cats":
//   │
//   │    "vid001" Funny Cats 2024   → Title contains "cats"     ✓  (5,000,000 views)
//   │    "vid002" Cat Training Tips → Tag "cats" matches         ✓  (1,200,000 views)
//   │    "vid003" Dog vs Cat        → Tag "cats" matches         ✓  (  800,000 views)
//   │    "x9y8z7" Vacation 2024     → no "cats" in title/tags    ✗
//   │
//   │  OrderByDescending(ViewCount) →
//   │    [ vid001 (5.0M), vid002 (1.2M), vid003 (0.8M) ]
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Scenario 5: Trending(3) ──────────────────────────────────────────────────────
//   │
//   │  Scan _db, keep all Ready videos, sort by ViewCount desc, take top 3:
//   │    [ vid001 (5.0M), vid002 (1.2M), vid003 (0.8M) ]
//   │
//   │  NOTE the flaw this snapshot reveals: "Vacation 2024" (1 view, uploaded seconds ago)
//   │  never appears, while "Funny Cats 2024" (5M lifetime views) always tops the list —
//   │  even if those 5M views accrued over 3 years and nobody's watching it today.
//   │  A velocity-based ranking would surface a freshly-exploding video instead.
//   └─────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;

public class VideoMetaStore
{
    // videoId → metadata record. The catalogue itself.
    // In production: a Cassandra table keyed by VideoId (partition key). This in-memory
    // dictionary is the demo stand-in; Get() here is the O(1) primary-key lookup that
    // Cassandra would route to a single node.
    private readonly Dictionary<string, VideoMetadata> _db = [];

    // Insert-or-update. Called twice in a video's life: once by UploadService.Init() to
    // create the Status=Uploading stub, once by TranscodeWorker.Process() to overwrite it
    // with the complete record and flip Status=Ready.
    //
    // WHY UPSERT (not separate Insert/Update):
    // The caller doesn't need to know or care whether the row already exists. TranscodeWorker
    // just writes the final record; whether that's creating or overwriting is irrelevant.
    // This also makes the write idempotent — if Process() is retried after a crash, re-writing
    // the same record is harmless. Maps directly to Cassandra, where every write IS an upsert.
    public void Upsert(VideoMetadata v) => _db[v.VideoId] = v;

    // The hot path: fetch one video's metadata by ID. Returns null if no such video exists.
    // Called before every playback (to check Status and get ManifestUrl) and by ViewCounter
    // (to read-modify-write ViewCount). O(1) dictionary lookup = O(1) Cassandra partition read.
    //
    // Returns null (not throws) so the API layer can cleanly map "unknown video" → HTTP 404.
    // Note: this returns the row even when Status != Ready; it's the CALLER's job to check
    // Status and decide whether to serve the video or return "still processing."
    public VideoMetadata Get(string videoId) => _db.TryGetValue(videoId, out var v) ? v : null;

    // Full-text-ish search over Title and Tags, restricted to playable (Ready) videos,
    // ranked by popularity (ViewCount desc).
    //
    // HOW IT WORKS (toy version):
    //   1. Lowercase the query once so matching is case-insensitive.
    //   2. Scan EVERY video in _db (this is the part that doesn't scale).
    //   3. Keep a video only if Status==Ready AND (title contains q OR any tag contains q).
    //   4. Sort survivors by ViewCount descending so the most popular match is first.
    //
    // WHY Status==Ready IS CHECKED FIRST in the predicate:
    // It's the cheapest filter (an enum compare) and eliminates Uploading/Transcoding videos
    // before the more expensive string Contains calls run. It's also the correctness gate:
    // a still-processing "cats" video must never appear in results a user can click.
    //
    // PRODUCTION: this entire method is replaced by an Elasticsearch query against an
    // inverted index built when each video flipped to Ready. The substring scan below
    // (O(videos × fields)) becomes an index lookup (O(matching results)).
    public List<VideoMetadata> Search(string query)
    {
        var q = query.ToLower();
        return _db.Values
                  .Where(v => v.Status == VideoStatus.Ready &&
                             (v.Title.ToLower().Contains(q) || v.Tags.Any(t => t.ToLower().Contains(q)))
                        )
                  .OrderByDescending(v => v.ViewCount)
                  .ToList();
    }

    // Returns the top-N most-viewed Ready videos.
    //
    // THE KNOWN LIMITATION (see WHY TRENDING SORTS BY VELOCITY + RECENCY above):
    // This sorts by raw cumulative ViewCount, which conflates "all-time popular" with
    // "trending now." A real trending system ranks by recent view velocity blended with
    // recency, so a video exploding today can outrank an old video with more total views.
    // The fix is the same time-decay math used in the social feed's FeedRanker.
    //
    // PRODUCTION: reads a precomputed leaderboard (e.g. a Redis sorted set) maintained by
    // a background ranking job — never scans the full catalogue like this demo does.
    public List<VideoMetadata> Trending(int top = 5) =>
        _db.Values.Where(v => v.Status == VideoStatus.Ready)
                  .OrderByDescending(v => v.ViewCount)
                  .Take(top)
                  .ToList();
}
