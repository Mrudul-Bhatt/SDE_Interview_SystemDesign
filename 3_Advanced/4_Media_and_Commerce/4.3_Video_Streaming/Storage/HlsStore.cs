// HlsStore — the CDN origin server: holds every transcoded segment + each video's manifest.
//
// THE BIG IDEA:
// Think of HlsStore as a distribution warehouse. TranscodeWorker ships in all editions of a
// video (the 6-second segments at each quality); the manifest is the catalog listing them.
// CdnEdgeCache is a network of regional libraries that copy popular segments locally — so
// HlsStore (the warehouse) is only hit on the FIRST request for each segment; every later
// viewer is served from the nearby CDN edge.
//
// Two dictionaries, because segments and manifests cache differently:
//   _segments  — content-addressed by URL, immutable once written → CDN can cache ~forever.
//   _manifests — one M3U8 per video, may change if a rendition is added → short CDN TTL.
//
// WHY CONTENT-ADDRESSED SEGMENT URLS ("hls/{videoId}/{quality}/seg{index}.ts"):
// The same (videoId, quality, index) always maps to the same URL and the same bytes, so a
// CDN edge never has to invalidate: a re-transcode produces a new videoId → new URLs, and
// the old segments just age out. AbrPlayer also computes URLs client-side — no lookup needed.
//
// WHY HasVideo CHECKS _manifests: TranscodeWorker writes the manifest LAST (after all
// segments). So a manifest existing guarantees the whole video is transcoded and CDN-ready.
//
// HOW IT BEHAVES AT RUNTIME (transcode "vacation", 300s, 3 quality tiers):
//
//   Operation                            | _segments / _manifests after
//   -------------------------------------|--------------------------------------
//   (start)                              | segments {}      manifests {}
//   Process() writes segments            | segments {150}   manifests {}     (50 x 3 tiers)
//   Process() then writes the manifest   | segments {150}   manifests {vacation}
//   HasVideo("vacation")                 | -> true (manifest exists => all segments exist)
//
//   Then, when viewers stream (the CDN sits in front of HlsStore):
//     Bob requests seg000           -> CDN miss -> reads HlsStore (origin), CDN caches it
//     2nd viewer requests seg000    -> CDN hit  -> HlsStore NOT touched (the CDN payoff)
//     Quality switch (720p -> 360p) -> fetch a different URL, same index; both already in
//                                      _segments, so HlsStore needs no change.

using System.Collections.Generic;
using System.Linq;

public class HlsStore
{
    // URL → segment. Content-addressed, so writes are effectively write-once per key.
    private readonly Dictionary<string, HlsSegment> _segments = [];

    // videoId → M3U8 manifest. Written only after all segments exist (TranscodeWorker step 2).
    private readonly Dictionary<string, string> _manifests = [];

    // Called by TranscodeWorker for every (rendition × index) — 150 calls for a 300s/3-tier video.
    public void StoreSegment(HlsSegment seg) => _segments[seg.Url] = seg;

    // Called AFTER all segments are stored; flips HasVideo to true (the CDN-ready gate).
    public void StoreManifest(string videoId, string m3u8) => _manifests[videoId] = m3u8;

    // Returns null on miss (not throws) — CdnEdgeCache treats null as a normal cache miss.
    public HlsSegment FetchSegment(string url)
    {
        _segments.TryGetValue(url, out var seg);
        return seg;
    }

    // Null = video not transcoded yet (still Uploading/Transcoding).
    public string FetchManifest(string videoId)
    {
        _manifests.TryGetValue(videoId, out var m);
        return m;
    }

    // True only once the manifest is written → guarantees all segments exist too.
    public bool HasVideo(string videoId) => _manifests.ContainsKey(videoId);

    // Returns one rendition's segments sorted by index. Demo/test helper only — this scans
    // ALL segments (O(N)); production fetches segments one-by-one by exact URL, never lists.
    public List<HlsSegment> GetRenditionSegments(string videoId, Rendition r) =>
        _segments.Values
                 .Where(s => s.VideoId == videoId && s.Quality == r)
                 .OrderBy(s => s.SegmentIndex)
                 .ToList();
}
