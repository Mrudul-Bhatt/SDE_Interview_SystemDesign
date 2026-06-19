// CdnEdgeCache — one CDN edge node sitting between viewers and the HlsStore origin.
//
// THE BIG IDEA:
// A regional library that keeps local copies of popular pages. The FIRST request for a URL
// misses, fetches from origin, caches it, and serves it; every later request for that same URL
// is a hit served locally — origin is never touched again. This is why a video watched by
// millions only hits the origin a handful of times (once per segment per edge).
//
// Caches manifests and segments separately because they're fetched by different keys (videoId
// vs segment URL). Real edges add LRU eviction, regional fan-out, and pre-warming — omitted so
// the hit/miss math stays clear. HitRate is the headline metric; production targets 95%+.
//
// HOW IT BEHAVES AT RUNTIME (two viewers watch the same video, Scenario 4):
//
//   Operation                        | Hits / Misses | served from
//   ---------------------------------|---------------|-------------------
//   u1 GetSegment(seg000) [1st ever] | 0 / 1         | origin (now cached)
//   u1 GetSegment(seg001)            | 0 / 2         | origin (now cached)
//   u2 GetSegment(seg000) [same url] | 1 / 2         | edge cache (HIT)
//   u2 GetSegment(seg001)            | 2 / 2         | edge cache (HIT)
//
//   The second viewer is served entirely from the edge — origin sees zero extra load.

using System.Collections.Generic;

public class CdnEdgeCache
{
    private readonly HlsStore _origin;

    // Local copies, keyed exactly as the origin keys them (segment URL, videoId).
    private readonly Dictionary<string, HlsSegment> _cache = [];
    private readonly Dictionary<string, string> _manifestCache = [];

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public CdnEdgeCache(HlsStore origin) { _origin = origin; }

    // Manifest read-through: serve from cache, else fetch from origin and cache it.
    public string GetManifest(string videoId)
    {
        if (_manifestCache.TryGetValue(videoId, out var m)) { Hits++; return m; }
        Misses++;
        m = _origin.FetchManifest(videoId);
        if (m != null) _manifestCache[videoId] = m;   // only cache real hits, never null
        return m;
    }

    // Segment read-through — the hot path, called once per playback tick per viewer.
    // Content-addressed URLs make this safe to cache indefinitely (see HlsSegment).
    public HlsSegment GetSegment(string url)
    {
        if (_cache.TryGetValue(url, out var seg)) { Hits++; return seg; }
        Misses++;
        seg = _origin.FetchSegment(url);
        if (seg != null) _cache[url] = seg;
        return seg;
    }

    // Fraction of requests served locally. 0 when nothing has been requested yet.
    public double HitRate => (Hits + Misses) == 0 ? 0 : (double)Hits / (Hits + Misses);
}
