// CdnEdgeCache — one CDN edge node sitting between viewers and origin.
//
// Caches both manifests and segments; first request misses → fetch from origin
// → cache → serve; subsequent requests for the same URL hit. Real CDN nodes
// also do LRU eviction, regional fan-out, and pre-warming for trending content
// — omitted here so the hit/miss math stays easy to follow in the demo.
//
// HitRate is the key metric: production targets 95%+ for popular content.

using System.Collections.Generic;

public class CdnEdgeCache
{
    private readonly HlsStore _origin;
    private readonly Dictionary<string, HlsSegment> _cache = new Dictionary<string, HlsSegment>();
    private readonly Dictionary<string, string>     _manifestCache = new Dictionary<string, string>();
    public int Hits   { get; private set; }
    public int Misses { get; private set; }

    public CdnEdgeCache(HlsStore origin) { _origin = origin; }

    public string GetManifest(string videoId)
    {
        if (_manifestCache.TryGetValue(videoId, out var m)) { Hits++; return m; }
        Misses++;
        m = _origin.FetchManifest(videoId);
        if (m != null) _manifestCache[videoId] = m;
        return m;
    }

    public HlsSegment GetSegment(string url)
    {
        if (_cache.TryGetValue(url, out var seg)) { Hits++; return seg; }
        Misses++;
        seg = _origin.FetchSegment(url);
        if (seg != null) _cache[url] = seg;
        return seg;
    }

    public double HitRate => (Hits + Misses) == 0 ? 0 : (double)Hits / (Hits + Misses);
}
