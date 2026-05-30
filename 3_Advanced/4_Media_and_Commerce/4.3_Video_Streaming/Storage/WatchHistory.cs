// WatchHistory — per-user playhead positions.
//
// Production: Cassandra partitioned by user_id with clustering by last_updated
// DESC, so "what was I watching?" is a single fast partition scan. Keyed here
// as "user:video" for simplicity; the real schema separates the columns so
// recommendations can also use this table as a behavioral signal.

using System;
using System.Collections.Generic;
using System.Linq;

public class WatchHistory
{
    private readonly Dictionary<string, WatchProgress> _db = new Dictionary<string, WatchProgress>();

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

    public WatchProgress Get(string userId, string videoId) =>
        _db.TryGetValue($"{userId}:{videoId}", out var p) ? p : null;

    public List<WatchProgress> GetHistory(string userId) =>
        _db.Values.Where(p => p.UserId == userId)
                  .OrderByDescending(p => p.LastUpdated)
                  .ToList();
}
