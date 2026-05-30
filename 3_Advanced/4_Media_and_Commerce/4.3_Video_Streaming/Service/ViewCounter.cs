// ViewCounter — buffered view counting with anti-fraud floor.
//
// Why buffered? Counting a billion daily views by incrementing a DB row per
// playback would melt any database. Instead we buffer heartbeats (Kafka in
// production) and flush aggregated counts every ~60 seconds.
//
// Anti-fraud rule: a "view" requires at least MinPlaybackSeconds of actual
// playback. This stops a bot that just opens-and-closes a video to game the
// counter. Real platforms add IP diversity, user-agent fingerprinting, and
// 48-hour delayed reconciliation for monetised views.

using System.Collections.Generic;
using System.Linq;

public class ViewCounter
{
    private readonly Dictionary<string, List<int>> _heartbeats = new Dictionary<string, List<int>>();
    private readonly VideoMetaStore _meta;
    private const int MinPlaybackSeconds = 30;

    public ViewCounter(VideoMetaStore meta) { _meta = meta; }

    public void RecordHeartbeat(string userId, string videoId, int positionSeconds)
    {
        var key = $"{userId}:{videoId}";
        if (!_heartbeats.ContainsKey(key)) _heartbeats[key] = new List<int>();
        _heartbeats[key].Add(positionSeconds);
    }

    // Flush: validate and increment view counts (called by batch job every 60s)
    public void Flush()
    {
        var toCount = new Dictionary<string, int>(); // videoId → incremental views

        foreach (var kv in _heartbeats)
        {
            var parts   = kv.Key.Split(':');
            var videoId = parts[1];
            var beats   = kv.Value;

            // Valid view: played for at least MinPlaybackSeconds
            if (beats.Count > 0 && beats.Max() - beats.Min() >= MinPlaybackSeconds)
            {
                if (!toCount.ContainsKey(videoId)) toCount[videoId] = 0;
                toCount[videoId]++;
            }
        }

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
