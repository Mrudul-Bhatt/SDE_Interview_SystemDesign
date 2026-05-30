// HlsStore — the CDN origin: master manifest + per-rendition segments.
//
// Segments are addressed by their content-derived URL, so the same segment
// always has the same URL — perfect for caching. The manifest is the only
// piece that ever changes for a given video (when we add or remove a rendition),
// and even then we typically just write a new file.

using System.Collections.Generic;
using System.Linq;

public class HlsStore
{
    private readonly Dictionary<string, HlsSegment> _segments = new Dictionary<string, HlsSegment>();
    private readonly Dictionary<string, string>     _manifests = new Dictionary<string, string>();

    public void StoreSegment(HlsSegment seg) => _segments[seg.Url] = seg;
    public void StoreManifest(string videoId, string m3u8) => _manifests[videoId] = m3u8;

    public HlsSegment FetchSegment(string url)
    {
        _segments.TryGetValue(url, out var seg);
        return seg;
    }
    public string FetchManifest(string videoId)
    {
        _manifests.TryGetValue(videoId, out var m);
        return m;
    }

    public bool HasVideo(string videoId) => _manifests.ContainsKey(videoId);

    public List<HlsSegment> GetRenditionSegments(string videoId, Rendition r) =>
        _segments.Values.Where(s => s.VideoId == videoId && s.Quality == r)
                        .OrderBy(s => s.SegmentIndex)
                        .ToList();
}
