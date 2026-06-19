// VideoMetaStore — the catalogue: one searchable record per video, keyed by VideoId.
//
// THE BIG IDEA:
// The library card catalogue. It holds no video bytes — just the "cards" (VideoMetadata)
// describing each video (title, tags, view count, status, manifest URL). Everything that
// FINDS or DESCRIBES a video reads here: "is it ready?" (Get), "search cats" (Search),
// "what's hot?" (Trending). Only UploadService.Init (stub row) and TranscodeWorker.Process
// (full row + Status=Ready) ever write, both via Upsert.
//
// WHY READS FILTER Status == Ready: a row can exist while the video is still transcoding
// (segments missing, ManifestUrl null). Filtering to Ready guarantees "if you can find it,
// you can play it." Same gate as HlsStore.HasVideo.
//
// WHY CASSANDRA (production): the dominant query is Get(videoId) — a primary-key lookup
// (VideoId = partition key, one row per video, O(1)). Reads vastly outnumber writes and
// there are no cross-video joins, so it scales horizontally by just adding nodes.
//
// WHY Search/Trending ARE TOY: both linear-scan _db here — fine for a few videos, fatal at
// billions. Production: Search → Elasticsearch (inverted index built when a video goes Ready);
// Trending → a precomputed Redis leaderboard ranked by view VELOCITY + recency (the same
// time-decay idea as the social feed's FeedRanker), so a video surging today beats an old
// high-lifetime-total one. The demo's raw-ViewCount sort can't do that.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 5, after three cat videos are seeded):
//
//   Operation                          | Result
//   -----------------------------------|--------------------------------------
//   TranscodeWorker.Process("vacation")| _db = { vacation -> Status=Ready, 0 views }
//   (Scenario 5 seeds 3 cat videos)    | _db also has vid001/vid002/vid003 (Ready)
//   Search("cats")                     | -> [vid001 5.0M, vid002 1.2M, vid003 0.8M]
//                                      |    ("vacation" excluded - no "cats" match)
//   Trending(3)                        | -> same 3, ordered by view count
//
//   The flaw Trending reveals: a fresh 1-view upload never ranks, and a 5M-lifetime
//   video tops the list even if nobody watches it today. Velocity ranking would fix this.

using System.Collections.Generic;
using System.Linq;

public class VideoMetaStore
{
    // videoId → metadata. Production: a Cassandra table keyed by VideoId; Get is the O(1) read.
    private readonly Dictionary<string, VideoMetadata> _db = [];

    // Insert-or-update. Called twice per video (Init stub, then TranscodeWorker final record).
    // Caller-agnostic and idempotent on retry — maps directly to Cassandra's write model.
    public void Upsert(VideoMetadata v) => _db[v.VideoId] = v;

    // Hot path: fetch one video by ID. Returns null (→ HTTP 404) if absent. Returns the row
    // even when Status != Ready — the caller checks Status and decides whether to serve it.
    public VideoMetadata Get(string videoId) => _db.TryGetValue(videoId, out var v) ? v : null;

    // Substring search over Title + Tags, Ready-only, ranked by popularity.
    // Status==Ready is checked first: cheapest filter, and the correctness gate (a
    // still-processing match must never be clickable). Production replaces this with Elasticsearch.
    public List<VideoMetadata> Search(string query)
    {
        var q = query.ToLower();
        return _db.Values
                  .Where(v => v.Status == VideoStatus.Ready &&
                             (v.Title.ToLower().Contains(q) ||
                              v.Tags.Any(t => t.ToLower().Contains(q))))
                  .OrderByDescending(v => v.ViewCount)
                  .ToList();
    }

    // Top-N by cumulative ViewCount. See header: real systems rank by recent velocity, not
    // lifetime total, and read a precomputed leaderboard instead of scanning the catalogue.
    public List<VideoMetadata> Trending(int top = 5) =>
        _db.Values.Where(v => v.Status == VideoStatus.Ready)
                  .OrderByDescending(v => v.ViewCount)
                  .Take(top)
                  .ToList();
}
