// WatchHistory — the "resume where you left off" store: one playhead bookmark per (user, video).
//
// THE BIG IDEA:
// This is the store behind "Continue Watching." Every few seconds while you watch, the player
// quietly writes down how far you've gotten. Close the app on your phone at 1:30, open it on
// your TV later, and playback resumes at 1:30 — because that position was checkpointed here.
// Think of it as a shelf of self-updating bookmarks: one bookmark per book you've opened,
// each remembering your exact page and stamped with when you last touched it.
//
// Each record is a WatchProgress (the bookmark). WatchHistory is the shelf that holds them,
// keyed so each (user, video) pair has exactly ONE bookmark — re-watching overwrites the old
// position rather than piling up duplicates.
//
// WHO READS AND WRITES IT:
//   WRITTEN BY: AbrPlayer.Play()        — calls Update() after every 6-second segment tick
//   READ BY:    AbrPlayer's constructor — calls Get() once at startup to find the resume point
//               (production also reads GetHistory() to build the "Continue Watching" row)
//
// WHY THE KEY IS "userId:videoId" (a composite):
// The natural identity of a bookmark is the PAIR (who, what) — alice's position in video X is
// independent of bob's position in video X, and independent of alice's position in video Y.
// Concatenating them into one dictionary key "alice:X" gives:
//   - One record per pair (Update overwrites, never appends) → no duplicate bookmarks.
//   - O(1) lookup for the hot path: "where was alice in video X?" → Get("alice", "X").
// The trade-off: GetHistory("alice") can't use the key directly (it would need a prefix scan),
// so it falls back to a linear scan filtering on UserId. See WHY GetHistory SCANS below.
//
// WHY CASSANDRA PARTITIONED BY user_id (in production):
// The two access patterns both center on a single user:
//   "resume video X"        → Get(user, video)   — one row
//   "what's my history?"    → GetHistory(user)    — all of this user's rows
// Partitioning by user_id puts ALL of one user's bookmarks on the same node. Then:
//   - GetHistory is a single-partition scan (fast — no scatter-gather across the cluster).
//   - Clustering by last_updated DESC means the rows come back already sorted newest-first,
//     so "Continue Watching" needs no sort step — it's just "read the top N of the partition."
// This is the same partition-key-by-access-pattern reasoning used in PostStoreCassandra
// (social feed) and the message queue's partitioning — group the data the way it's read.
//
// WHY WatchHistory IS SEPARATE FROM ViewCounter (they look similar but aren't):
// Both get poked on every segment tick, but they answer different questions:
//   WatchHistory → "where is THIS user in THIS video?"  (per-user state, must be exact-ish,
//                   read back to that same user → strong-ish consistency needed)
//   ViewCounter  → "how many total views does this video have?" (aggregate, approximate,
//                   batched and flushed — see ViewCounter for the hotspot-avoidance reasoning)
// Mixing them would force per-user precision onto an aggregate counter (or vice versa).
// Keeping them apart lets each pick the right consistency/throughput trade-off.
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — WatchHistory state across Scenario 1 (Program.cs):
//
//   ┌─ Bob opens the video for the first time ──────────────────────────────────────
//   │  AbrPlayer constructor → WatchHistory.Get("bob", "x9y8z7") → null
//   │    (no bookmark yet)  → PositionSeconds = 0  → player starts from the beginning
//   │  _db = { }
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Bob plays 4 segments (Play(4, throughput=8000)) ──────────────────────────────
//   │  After each segment tick, AbrPlayer calls Update("bob", "x9y8z7", position):
//   │
//   │    tick 0 → Update(..., 6)   _db["bob:x9y8z7"] = { Pos= 6, LastUpdated=t0 }
//   │    tick 1 → Update(..., 12)  _db["bob:x9y8z7"] = { Pos=12, LastUpdated=t1 }  ← overwrites
//   │    tick 2 → Update(..., 18)  _db["bob:x9y8z7"] = { Pos=18, LastUpdated=t2 }  ← overwrites
//   │    tick 3 → Update(..., 24)  _db["bob:x9y8z7"] = { Pos=24, LastUpdated=t3 }  ← overwrites
//   │
//   │  Note: ONE key, repeatedly overwritten. After 4 ticks _db has exactly 1 entry,
//   │  not 4 — each Update replaces the previous bookmark for this (user, video) pair.
//   │  _db = { "bob:x9y8z7" → { UserId="bob", VideoId="x9y8z7",
//   │                            PositionSeconds=24, Completed=false, LastUpdated=t3 } }
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Bob closes the app and reopens it (bobPlayer2 in Program.cs) ─────────────────
//   │  New AbrPlayer constructor → WatchHistory.Get("bob", "x9y8z7")
//   │    → { PositionSeconds=24, ... }  → PositionSeconds = 24
//   │    → "[Player] bob opening x9y8z7, resuming from 24s"   ← the resume payoff
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Later: many users, many videos → GetHistory("bob") ──────────────────────────
//   │  _db = {
//   │    "bob:x9y8z7"   → { Pos=24,  Completed=false, LastUpdated=14:32 },
//   │    "bob:a9c2f7e1" → { Pos=240, Completed=false, LastUpdated=13:15 },
//   │    "bob:d4e8b2f6" → { Pos=180, Completed=true,  LastUpdated=09:41 },
//   │    "eve:x9y8z7"   → { Pos=18,  Completed=false, LastUpdated=15:02 },  ← different user
//   │  }
//   │
//   │  GetHistory("bob") scans _db, keeps UserId=="bob", sorts by LastUpdated DESC:
//   │    [0] { VideoId="x9y8z7",   Pos=24,  LastUpdated=14:32 }  ← "Continue Watching" top
//   │    [1] { VideoId="a9c2f7e1", Pos=240, LastUpdated=13:15 }
//   │    [2] { VideoId="d4e8b2f6", Pos=180, Completed=true }     ← UI shows "Watch Again"
//   │  (eve's row is excluded — different UserId)
//   └─────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;

public class WatchHistory
{
    // "userId:videoId" → that user's bookmark in that video.
    // The composite string key guarantees one record per (user, video) pair: re-watching
    // overwrites the existing entry rather than creating a second one.
    // In production: a Cassandra table partitioned by user_id, clustered by last_updated DESC.
    private readonly Dictionary<string, WatchProgress> _db = new Dictionary<string, WatchProgress>();

    // Checkpoint the user's current position. Called by AbrPlayer after EVERY segment tick
    // (~every 6 seconds of playback). Builds a fresh WatchProgress and overwrites the prior
    // bookmark for this (user, video) pair.
    //
    // WHY IT REBUILDS THE WHOLE RECORD (rather than mutating fields):
    // Simplicity and a clean LastUpdated stamp — every checkpoint is a full snapshot with a
    // new timestamp. Maps cleanly to Cassandra, where every write is an upsert of the row.
    //
    // WHY completed DEFAULTS TO false:
    // Most checkpoints happen mid-video, where the user has NOT finished. AbrPlayer only
    // passes completed=true on the final segment. Defaulting to false means the common
    // mid-watch case needs no extra argument. (See WatchProgress for why Completed is an
    // explicit bool rather than inferred from PositionSeconds >= DurationSeconds.)
    public void Update(string userId, string videoId, int positionSeconds, bool completed = false)
    {
        var key = $"{userId}:{videoId}";
        _db[key] = new WatchProgress
        {
            UserId          = userId,
            VideoId         = videoId,
            PositionSeconds = positionSeconds,
            Completed       = completed,
            LastUpdated     = DateTime.UtcNow
        };
    }

    // The hot path: fetch one user's bookmark in one video. Called once by AbrPlayer's
    // constructor to decide the resume point. Returns null when the user has never watched
    // this video — AbrPlayer treats null as "start from 0" (progress?.PositionSeconds ?? 0).
    //
    // Returns null (not throws) precisely because "no bookmark yet" is the normal first-watch
    // case, not an error. O(1) dictionary lookup = O(1) Cassandra single-row read.
    public WatchProgress Get(string userId, string videoId) =>
        _db.TryGetValue($"{userId}:{videoId}", out var p) ? p : null;

    // Returns all of a user's bookmarks, newest-watched first — the data behind the
    // "Continue Watching" / "Recently Watched" row on the home screen.
    //
    // WHY THIS SCANS THE WHOLE _db (and why production doesn't):
    // The composite "userId:videoId" key is great for the exact Get() lookup but can't be
    // range-scanned by user here, so this filters every record by UserId — O(total records).
    // Fine for the demo. In production, partitioning by user_id makes this a single-partition
    // read, and clustering by last_updated DESC means the rows arrive pre-sorted, so the
    // OrderByDescending below disappears entirely — it's just "read the top N of the partition."
    public List<WatchProgress> GetHistory(string userId) =>
        _db.Values.Where(p => p.UserId == userId)
                  .OrderByDescending(p => p.LastUpdated)
                  .ToList();
}
