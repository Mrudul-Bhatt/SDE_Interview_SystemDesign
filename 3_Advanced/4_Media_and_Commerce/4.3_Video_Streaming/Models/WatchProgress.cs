// WatchProgress — one user's bookmark in one video.
//
// THE BIG IDEA:
// Think of WatchProgress like a physical bookmark, but one that updates itself
// automatically every few seconds. It remembers exactly which page you were on
// (PositionSeconds), whether you finished the book (Completed), and when you
// last put it down (LastUpdated). If the app crashes, you lose at most one
// segment's worth of position (~6 seconds) — not your entire session.
//
// AbrPlayer writes this record after every segment tick via WatchHistory.Update().
// AbrPlayer reads it at startup: if a record exists, it resumes from PositionSeconds;
// if null, it starts from zero.
//
// In production this is a Cassandra row:
//   Partition key = user_id  → one partition per user, all their videos in one place
//   Clustering key = last_updated DESC → "continue watching" list is one fast scan
//
// WHY CHECKPOINT EVERY SEGMENT (not every frame):
// A 30fps video would produce 30 writes/second per active viewer. At Netflix scale
// (millions of concurrent viewers) that saturates any database. Writing once per
// segment (~6 seconds) reduces write load by 180× while losing at most 6 seconds
// of position on a crash — a UX trade-off users never notice. Production debounces
// further to ~10 seconds for the same reason.
//
// WHY Completed IS A SEPARATE BOOL (not inferred from PositionSeconds):
// "Did the user finish this video?" cannot be reliably inferred from
// PositionSeconds >= DurationSeconds because:
//   1. DurationSeconds lives on VideoMetadata, not here — reading it would require
//      a second DB lookup every time the UI checks completion.
//   2. Integer segment rounding means PositionSeconds may land at 174s on a 180s
//      video and never reach 180 exactly.
// The explicit bool lets the UI render "Watch Again" vs "Continue Watching" with
// a single field read, and gives the recommendation engine a clean "user finished"
// signal that is stronger than "user was 97% through."

using System;

public class WatchProgress
{
    // The viewer. Partition key in the production Cassandra schema — all of a
    // user's watch progress lives on the same node, making "continue watching"
    // a single partition scan rather than a scatter-gather across the cluster.
    public string UserId { get; set; }

    // The video being watched. Together with UserId forms the composite lookup
    // key "userId:videoId" that WatchHistory uses to ensure one record per
    // (user, video) pair — re-watching overwrites rather than appending.
    public string VideoId { get; set; }

    // How far into the video the user has watched, in whole seconds.
    // Written by AbrPlayer.Play() after each segment: PositionSeconds += 6.
    // Read by AbrPlayer's constructor to decide where to start playback.
    //
    // ── RUNTIME SNAPSHOT (alice watches "b7f3a1c9e2d8", 180-second video) ──
    //   AbrPlayer ticks — WatchHistory.Update called after each segment:
    //     seg 0 → PositionSeconds =  6   (0:06)
    //     seg 1 → PositionSeconds = 12   (0:12)
    //     seg 2 → PositionSeconds = 18   (0:18)
    //     ...
    //     seg 14 → PositionSeconds = 90  (1:30)   ← alice closes the app here
    //
    //   Next session: AbrPlayer constructor calls WatchHistory.Get("alice","b7f3a1c9e2d8")
    //     → PositionSeconds = 90 → player resumes at 1:30, not from the beginning
    public int PositionSeconds { get; set; }

    // True once the user has watched to the end of the video.
    // Set by AbrPlayer when PositionSeconds reaches the final segment.
    // Drives two UI behaviours:
    //   false → show "Continue Watching" with a progress bar at PositionSeconds
    //   true  → show "Watch Again" starting from zero
    // Also a strong recommendation signal: a completed view outweighs a partial
    // one when the engine decides which videos to surface next.
    //
    // ── RUNTIME SNAPSHOT ──
    //   Mid-watch:  Completed = false, PositionSeconds = 90   → "Continue at 1:30"
    //   After end:  Completed = true,  PositionSeconds = 180  → "Watch Again"
    public bool Completed { get; set; }

    // Timestamp of the most recent checkpoint write, set to DateTime.UtcNow by
    // WatchHistory.Update() on every call. WatchHistory.GetHistory() orders by
    // this field DESC to produce a "recently watched" list — the video the user
    // was watching most recently appears first.
    //
    // ── RUNTIME SNAPSHOT (alice's full watch history, newest first) ──
    //   GetHistory("alice") →
    //     [0] { VideoId="b7f3a1c9e2d8", PositionSeconds= 90, Completed=false, LastUpdated=14:32:06 }
    //     [1] { VideoId="a9c2f7e1b3d5", PositionSeconds=240, Completed=false, LastUpdated=13:15:44 }
    //     [2] { VideoId="d4e8b2f6c1a7", PositionSeconds=180, Completed=true,  LastUpdated=09:41:03 }
    //
    //   Row [0] is the "Continue Watching" candidate shown at the top of alice's home screen.
    //   Row [2] shows a completed video — UI renders "Watch Again" with no progress bar.
    public DateTime LastUpdated { get; set; }
}
