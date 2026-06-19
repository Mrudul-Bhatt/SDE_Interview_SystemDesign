// WatchProgress — one user's bookmark in one video.
//
// THE BIG IDEA:
// A self-updating bookmark: it remembers the page you're on (PositionSeconds), whether you
// finished (Completed), and when you last touched it (LastUpdated). AbrPlayer writes it after
// every segment and reads it on open to resume. On a crash you lose at most ~6s of position.
//
// WHY CHECKPOINT PER SEGMENT (not per frame): 30fps would mean 30 writes/sec per viewer —
// fatal at scale. Once per ~6s segment cuts that 180x while losing at most 6s on a crash.
//
// WHY Completed IS A SEPARATE BOOL (not PositionSeconds >= duration): duration lives on
// VideoMetadata (a second lookup), and integer rounding can stop at 174s on a 180s video.
// An explicit flag gives the UI "Watch Again" vs "Continue" in one read.
//
// HOW IT BEHAVES AT RUNTIME (alice watches a 180s video):
//
//   Moment                   | PositionSeconds | Completed | UI shows
//   -------------------------|-----------------|-----------|------------------
//   paused mid-watch (1:30)  | 90              | false     | "Continue at 1:30"
//   reached the end          | 180             | true      | "Watch Again"

using System;

public class WatchProgress
{
    // The viewer. Production Cassandra partition key — all of a user's progress on one node.
    public string UserId { get; set; }

    // The video. With UserId forms the "userId:videoId" key (one record per pair).
    public string VideoId { get; set; }

    // Seconds watched. AbrPlayer does PositionSeconds += 6 per segment; reads it on open to resume.
    public int PositionSeconds { get; set; }

    // True once watched to the end. Drives "Watch Again" vs "Continue", and is a strong
    // recommendation signal (finished > 97%-through).
    public bool Completed { get; set; }

    // Last checkpoint time. WatchHistory.GetHistory orders by this DESC for "recently watched".
    public DateTime LastUpdated { get; set; }
}
