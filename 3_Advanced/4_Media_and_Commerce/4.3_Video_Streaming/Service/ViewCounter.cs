// ViewCounter — buffered, fraud-resistant view counting.
//
// THE BIG IDEA:
// Incrementing a video's view count in the database on every playback would melt the DB at
// scale (a viral video gets thousands of plays per second on one row — a write hotspot).
// Instead, playback "heartbeats" are buffered in memory and a batch job flushes aggregated
// counts every ~60s. The count lags real time by at most one flush window — fine, since
// nobody expects view counts to be exact to the second. (Production buffers in Kafka.)
//
// ANTI-FRAUD FLOOR: a play only counts as a view if it had at least MinPlaybackSeconds (30s)
// of actual playback. This blocks a bot that just opens-and-closes a video to inflate counts.
// Real platforms add IP diversity, fingerprinting, and delayed reconciliation on top.
//
// HOW IT BEHAVES AT RUNTIME (Bob watches "vacation" for 24s, Scenario 1):
//
//   Operation                            | State after
//   -------------------------------------|--------------------------------------
//   RecordHeartbeat("bob","vacation",6)  | _heartbeats={ bob:vacation -> [6] }
//   ...heartbeats at 12, 18, 24          | _heartbeats={ bob:vacation -> [6,12,18,24] }
//   Flush()                              | span = 24-6 = 18s < 30s floor -> NOT a view
//                                        | buffer cleared; ViewCount stays unchanged
//
//   Bob's 24s session doesn't count — that's the anti-fraud floor in action. A session whose
//   heartbeat span reaches >=30s would add +1 to that video's ViewCount on flush.

using System.Collections.Generic;
using System.Linq;

public class ViewCounter
{
    // "userId:videoId" -> all playhead positions seen this flush window. One list per viewing
    // session so we can measure how long each viewer actually watched.
    private readonly Dictionary<string, List<int>> _heartbeats = [];
    private readonly VideoMetaStore _meta;

    // Minimum watch span for a play to count. The anti-fraud floor.
    private const int MinPlaybackSeconds = 30;

    public ViewCounter(VideoMetaStore meta) { _meta = meta; }

    // Called by AbrPlayer on every segment tick with the current playhead position.
    // Cheap append — the validation work is deferred to Flush.
    public void RecordHeartbeat(string userId, string videoId, int positionSeconds)
    {
        var key = $"{userId}:{videoId}";
        if (!_heartbeats.ContainsKey(key)) _heartbeats[key] = [];
        _heartbeats[key].Add(positionSeconds);
    }

    // Batch job (every ~60s): turn buffered heartbeats into view increments, then clear.
    // A session counts only if its watched span (max - min position) clears the fraud floor.
    public void Flush()
    {
        var toCount = new Dictionary<string, int>(); // videoId -> new views this flush

        foreach (var kv in _heartbeats)
        {
            var videoId = kv.Key.Split(':')[1];
            var beats   = kv.Value;

            if (beats.Count > 0 && beats.Max() - beats.Min() >= MinPlaybackSeconds)
            {
                if (!toCount.ContainsKey(videoId)) toCount[videoId] = 0;
                toCount[videoId]++;
            }
        }

        // Apply the aggregated increments to the catalogue in one pass per video.
        foreach (var kv in toCount)
        {
            var video = _meta.Get(kv.Key);
            if (video != null)
            {
                video.ViewCount += kv.Value;
                System.Console.WriteLine($"  [ViewCount] {kv.Key} +{kv.Value} → total {video.ViewCount}");
            }
        }

        _heartbeats.Clear();
    }
}
