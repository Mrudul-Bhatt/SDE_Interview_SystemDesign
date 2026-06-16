// VideoMetadata — the catalogue record for one video in the metadata DB.
//
// THE BIG IDEA:
// Think of VideoMetadata like a library catalogue card. It describes the item and
// tells you where to find it (ManifestUrl), but it is NOT the item itself — the
// actual video bytes live in RawVideoStore and the HLS segments live in HlsStore.
// Every component that needs to reason about a video (search, trending, the ABR
// player, the view counter) reads this card. Only two components ever write it:
// UploadService.Init() creates a stub, and TranscodeWorker.Process() fills it in
// and flips Status to Ready when the video is actually playable.
//
// In production this maps to a Cassandra row:
//   Partition key = VideoId (routes every query to exactly one node)
//   No clustering key — each video is one row, fetched by primary key
//   Secondary indexes / Elasticsearch used for Title and Tags search
//
// WHY VideoId IS ASSIGNED BEFORE BYTES ARRIVE:
// UploadService.Init() generates VideoId before the first chunk is received.
// The client stores this ID immediately so it can resume a dropped upload against
// the same stable ID — without it, a network failure mid-upload would start a new
// video with a new ID and orphan the partial upload.
//
// WHY ViewCount IS APPROXIMATE:
// At YouTube scale, a single popular video can receive thousands of concurrent
// views per second. Incrementing a Cassandra counter on every view would produce
// thousands of writes/second to one row — a hotspot that saturates the partition.
// Instead, ViewCounter batches increments in memory and flushes periodically
// (every 30 seconds in this demo). The count lags real-time by at most one flush
// window — acceptable, since users don't expect view counts to be exact.
//
// WHY ManifestUrl IS NULL UNTIL Ready:
// TranscodeWorker writes all HLS segments first, then the master manifest, then
// flips Status to Ready (see TranscodeWorker.Process()). ManifestUrl is only
// populated in that final step. If it were set earlier, a player fetching it
// during Transcoding would receive an empty or partial manifest, causing a
// playback failure on a video that looks Ready.

using System;
using System.Collections.Generic;

public class VideoMetadata
{
    // Globally unique, stable identifier assigned by UploadService.Init() before
    // the first byte arrives. Used as the Cassandra partition key — every segment
    // lookup, manifest fetch, and view count update keys off this value.
    // Format: 12-char hex from a trimmed Guid (e.g., "b7f3a1c9e2d8").
    public string VideoId { get; set; }

    // The user who initiated the upload. Used for access control ("can this user
    // edit/delete this video?") and creator analytics (total views across uploads).
    public string UploaderId { get; set; }

    // Human-readable title. Indexed in Elasticsearch for full-text search;
    // VideoMetaStore.Search() also scans this field (linear scan in the demo).
    public string Title { get; set; }

    // Searchable topic labels attached by the uploader. Initialised to an empty
    // list (not null) so callers can safely iterate without a null check.
    // In production: stored as a Cassandra SET<text>; Elasticsearch indexes each
    // tag as a keyword so "exact tag" search is O(1) rather than a substring scan.
    public List<string> Tags { get; set; } = [];

    // The current lifecycle state — see Enums.cs for the full state machine.
    // VideoMetaStore.Search() and .Trending() filter to Status == Ready so that
    // videos still uploading or transcoding never appear in user-facing results.
    //
    // ── RUNTIME SNAPSHOT (video "b7f3a1c9e2d8" at two points in time) ──
    //   t= 0s  Status = Uploading    ManifestUrl = null
    //   t=30s  Status = Transcoding  ManifestUrl = null
    //   t=90s  Status = Ready        ManifestUrl = "hls/b7f3a1c9e2d8/manifest.m3u8"
    //
    //   VideoMetaStore.Get("b7f3a1c9e2d8"):
    //       during Uploading / Transcoding → returns the row (Status != Ready),
    //       but the API layer checks Status and returns HTTP 404 "still processing."
    //       after Ready → returns the row → API returns 200 + ManifestUrl to player.
    public VideoStatus Status { get; set; }

    // Total video length in seconds. Set by TranscodeWorker from the upload metadata.
    // Used by the player to render the seek bar and by AbrPlayer to know how many
    // HLS segments to expect (numSegments = ceil(DurationSeconds / 6)).
    public int DurationSeconds { get; set; }

    // Approximate play count — may lag real-time by up to one ViewCounter flush
    // window (30 seconds in this demo). See WHY VIEWCOUNT IS APPROXIMATE above.
    // VideoMetaStore.Trending() sorts by this field descending; production systems
    // blend view velocity + recency so a viral video outranks an old high-total one.
    //
    // ── RUNTIME SNAPSHOT (after ViewCounter.Flush() runs) ──
    //   Before flush: ViewCount = 14000  (in-memory buffer has 823 pending increments)
    //   After  flush: ViewCount = 14823  (batch-flushed; next real-time count may already
    //                                     be higher but that's acceptable approximation)
    public long ViewCount { get; set; }

    // Timestamp of when the video went live — set by TranscodeWorker at the moment
    // Status is flipped to Ready, not when the upload started. This is intentional:
    // from a user's perspective the video "exists" when it is playable, not when
    // the uploader hit Upload. Sorting by CreatedAt DESC gives chronological feeds.
    public DateTime CreatedAt { get; set; }

    // CDN path to the master M3U8 playlist. The player fetches this URL first to
    // discover all available renditions and their bitrates, then requests individual
    // segment playlists and finally the segment files.
    // Null during Uploading and Transcoding — see WHY ManifestUrl IS NULL UNTIL Ready.
    //
    // ── RUNTIME SNAPSHOT ──
    //   ManifestUrl = "hls/b7f3a1c9e2d8/manifest.m3u8"
    //
    //   Master M3U8 contents (built by TranscodeWorker.BuildManifest):
    //       #EXTM3U
    //       #EXT-X-VERSION:3
    //       #EXT-X-STREAM-INF:BANDWIDTH=5000000          ← R1080p listed first (highest)
    //       hls/b7f3a1c9e2d8/R1080p/index.m3u8
    //       #EXT-X-STREAM-INF:BANDWIDTH=2500000
    //       hls/b7f3a1c9e2d8/R720p/index.m3u8
    //       #EXT-X-STREAM-INF:BANDWIDTH=800000
    //       hls/b7f3a1c9e2d8/R480p/index.m3u8
    //       #EXT-X-STREAM-INF:BANDWIDTH=400000
    //       hls/b7f3a1c9e2d8/R360p/index.m3u8
    //   Player reads BANDWIDTH values → picks the rendition that fits its connection.
    public string ManifestUrl { get; set; }
}
