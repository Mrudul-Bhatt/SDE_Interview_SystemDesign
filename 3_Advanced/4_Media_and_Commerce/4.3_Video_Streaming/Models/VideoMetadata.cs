// VideoMetadata — the catalogue record (card) for one video. Holds no bytes, just describes
// the video and points to its content via ManifestUrl.
//
// THE BIG IDEA:
// A library catalogue card. Search, trending, the player, and the view counter all read it.
// Only UploadService.Init (stub) and TranscodeWorker.Process (full record + Status=Ready) write
// it. Production: a Cassandra row keyed by VideoId.
//
// WHY VideoId IS ASSIGNED BEFORE BYTES ARRIVE: so the client has a stable id to resume a
// dropped upload against (no orphaned partial uploads).
//
// WHY ViewCount IS APPROXIMATE: a viral video gets thousands of views/sec; incrementing one
// row per view is a write hotspot. ViewCounter batches and flushes, so the count lags slightly.
//
// WHY ManifestUrl IS NULL UNTIL Ready: it's set in the same final step as Status=Ready, so a
// player can never fetch a half-written manifest on a video that looks playable.
//
// HOW IT BEHAVES AT RUNTIME (one row over a video's lifecycle):
//
//   Stage         | Status       | ManifestUrl                  | ViewCount
//   --------------|--------------|------------------------------|----------------
//   uploading     | Uploading    | null                         | 0
//   transcoding   | Transcoding  | null                         | 0
//   live          | Ready        | hls/vacation/manifest.m3u8   | grows on flush
//
//   Search/Trending only ever return rows at Status=Ready.

using System;
using System.Collections.Generic;

public class VideoMetadata
{
    // Stable public id, assigned at Init. Cassandra partition key; the join key for every store.
    public string VideoId { get; set; }

    // Uploader — for access control and creator analytics.
    public string UploaderId { get; set; }

    // Title. Searched by VideoMetaStore.Search (Elasticsearch in production).
    public string Title { get; set; }

    // Topic labels. Empty list (not null) so callers iterate without a null check.
    public List<string> Tags { get; set; } = [];

    // Lifecycle state (see Enums.cs). Search/Trending filter to Ready so in-progress videos hide.
    public VideoStatus Status { get; set; }

    // Length in seconds. Set by TranscodeWorker; drives the seek bar and segment count.
    public int DurationSeconds { get; set; }

    // Approximate play count (lags by one ViewCounter flush). Trending sorts by this.
    public long ViewCount { get; set; }

    // When the video went live (set at Status=Ready, not upload start). Sort DESC for recency.
    public DateTime CreatedAt { get; set; }

    // CDN path to the master M3U8 the player fetches first. Null until Ready.
    public string ManifestUrl { get; set; }
}
