// VideoMetaStore — the metadata DB (Cassandra in production).
//
// Search here is a toy linear scan; production uses Elasticsearch indexed at
// "video became Ready" time. Trending sorts by view count, but real systems
// blend view velocity + recency so a 24-hour-old viral video can outrank a
// 5-year-old hit with more cumulative views.

using System.Collections.Generic;
using System.Linq;

public class VideoMetaStore
{
    private readonly Dictionary<string, VideoMetadata> _db = new Dictionary<string, VideoMetadata>();

    public void Upsert(VideoMetadata v) => _db[v.VideoId] = v;

    public VideoMetadata Get(string videoId) =>
        _db.TryGetValue(videoId, out var v) ? v : null;

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

    public List<VideoMetadata> Trending(int top = 5) =>
        _db.Values.Where(v => v.Status == VideoStatus.Ready)
                  .OrderByDescending(v => v.ViewCount)
                  .Take(top)
                  .ToList();
}
