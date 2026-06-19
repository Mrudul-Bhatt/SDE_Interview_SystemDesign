// WatchHistory — the "resume where you left off" store: one playhead bookmark per (user, video).
//
// THE BIG IDEA:
// The store behind "Continue Watching." Every ~6 seconds the player checkpoints how far you've
// watched; close on your phone at 1:30, reopen on your TV, and it resumes at 1:30.
//   WRITTEN BY: AbrPlayer.Play()        (Update after every segment tick)
//   READ BY:    AbrPlayer's constructor (Get once at startup to find the resume point)
//
// WHY KEY "userId:videoId": a bookmark's identity is the (who, what) pair — alice's spot in a
// video is independent of bob's. The composite key gives one record per pair (Update overwrites,
// never appends) and an O(1) resume lookup. Trade-off: GetHistory can't range-scan by user here.
//
// WHY CASSANDRA PARTITIONED BY user_id (production): both queries center on one user. Partition
// by user_id → all a user's bookmarks on one node → GetHistory is a single-partition scan;
// cluster by last_updated DESC → "Continue Watching" comes back pre-sorted (no sort step).
//
// WHY SEPARATE FROM ViewCounter: both fire every tick but answer different questions —
// WatchHistory is exact per-user state read back to that user; ViewCounter is an approximate,
// batched aggregate. Different consistency/throughput trade-offs, so different stores.
//
// HOW IT BEHAVES AT RUNTIME (Bob watches "vacation", Scenario 1):
//
//   Operation                       | Store contents after
//   --------------------------------|--------------------------------------
//   (start)                         | {}
//   Get("bob","vacation")           | {}          -> null, so play starts at 0s
//   Update("bob","vacation",6)      | { bob:vacation -> 6 }
//   Update("bob","vacation",12)     | { bob:vacation -> 12 }   (same key, replaced)
//   Update("bob","vacation",24)     | { bob:vacation -> 24 }   (same key, replaced)
//   Get("bob","vacation") [reopen]  | -> 24, so playback resumes at 24s
//
//   Note: 4 ticks of Update leave ONE entry (not 4) - each call overwrites the
//   previous bookmark for the same (user, video) pair.

using System;
using System.Collections.Generic;
using System.Linq;

public class WatchHistory
{
    // "userId:videoId" → bookmark. Composite key = one record per pair (re-watch overwrites).
    // Production: Cassandra table partitioned by user_id, clustered by last_updated DESC.
    private readonly Dictionary<string, WatchProgress> _db = [];

    // Checkpoint the playhead. Called by AbrPlayer after every segment (~6s). Rebuilds the
    // whole record (clean LastUpdated stamp, maps to a Cassandra upsert). completed defaults
    // false since most checkpoints are mid-video; AbrPlayer passes true only on the last segment.
    public void Update(string userId, string videoId, int positionSeconds, bool completed = false)
    {
        var key = $"{userId}:{videoId}";
        _db[key] = new WatchProgress
        {
            UserId = userId,
            VideoId = videoId,
            PositionSeconds = positionSeconds,
            Completed = completed,
            LastUpdated = DateTime.UtcNow
        };
    }

    // Resume lookup. Null = never watched → AbrPlayer starts at 0 (progress?.PositionSeconds ?? 0).
    public WatchProgress Get(string userId, string videoId) => _db.TryGetValue($"{userId}:{videoId}", out var p) ? p : null;

    // "Continue Watching" list, newest first. Scans all records here (O(N)); production makes
    // this a single-partition read (partitioned by user) that arrives pre-sorted by clustering.
    public List<WatchProgress> GetHistory(string userId) =>
        _db.Values.Where(p => p.UserId == userId)
                  .OrderByDescending(p => p.LastUpdated)
                  .ToList();
}
