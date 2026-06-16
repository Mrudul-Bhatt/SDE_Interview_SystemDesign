// HlsSegment — one ~6-second independently-decodable slice of one rendition.
//
// THE BIG IDEA:
// Think of an HlsSegment like a page in a flipbook. Each page covers one fixed
// time window (~6 seconds), is self-contained (begins with a keyframe so it can
// be decoded without the previous page), and is stored at a predictable address
// (its Url). The player requests pages sequentially but can jump to any page
// directly — seeking is just computing the right SegmentIndex and fetching that
// one URL. The CDN holds the pages; the player is just a reader that asks for
// the next page it needs at the quality its network can sustain.
//
// WHY CONTENT-ADDRESSED URL (videoId + quality + index baked into the path):
// The Url is fully determined by three fields — it never changes for a given
// (VideoId, Quality, SegmentIndex) triple. This makes segments effectively
// immutable in the CDN cache. A CDN edge node can cache the segment forever with
// a multi-day TTL and never serve stale bytes, because the URL for a different
// transcode will be different (new VideoId). Without this property, every CDN
// cache invalidation on a re-transcode would be a global purge — expensive and
// error-prone.
//
// WHY .ts (MPEG-2 Transport Stream) FORMAT:
// HLS mandates .ts containers. Each .ts file begins with a keyframe, making it
// independently decodable — the player can start mid-video by fetching any segment
// without decoding every prior one. This is the property that makes seeking and
// ABR quality switches instantaneous: both are just "fetch a different segment URL."
//
// WHY SegmentIndex (not a timestamp):
// A numeric index is exact and cheap to compute from playhead position:
//   SegmentIndex = PositionSeconds / 6  (integer division)
// A timestamp would require floating-point arithmetic and rounding, and segment
// boundaries don't always align to integer seconds. The index is also what
// HlsStore uses as part of the dictionary key (via Url).
//
// WHY BitrateKbps IS STORED ON THE SEGMENT (not looked up from BitrateTable):
// AbrPlayer computes download time as (BitrateKbps / throughputKbps) * 6 per
// segment. Storing the bitrate avoids a dictionary lookup on every tick. In
// production this value also appears in the per-rendition M3U8 playlist file
// (#EXTINF duration and BANDWIDTH tags), so the player always has it locally.

public class HlsSegment
{
    // The video this segment belongs to. Combined with Quality and SegmentIndex
    // to form the content-addressed Url that HlsStore and the CDN key off.
    public string VideoId { get; set; }

    // Which quality tier this segment encodes — one of R360p / R480p / R720p /
    // R1080p / R4K. AbrPlayer picks the quality before constructing the Url,
    // so fetching a different rendition of the same window is just swapping Quality
    // and recomputing Url — no other field changes.
    public Rendition Quality { get; set; }

    // Zero-based position of this segment within the rendition.
    // AbrPlayer derives it from the playhead: SegmentIndex = PositionSeconds / 6.
    // The :D3 format in Url zero-pads to 3 digits (seg000 … seg999) so segment
    // paths sort lexicographically in chronological order — "seg002" < "seg010"
    // (without padding "seg2" > "seg10" in string sort, breaking ordered listings).
    public int SegmentIndex { get; set; }

    // The raw video bytes for this segment.
    // In production: not stored in this object at all — the CDN fetches bytes
    // directly from S3/object storage via the Url; HlsSegment is just metadata.
    // In this demo: 4-byte stub [ (byte)Quality, (byte)SegmentIndex, 0xAB, 0xCD ]
    // written by TranscodeWorker so segment objects exist without real ffmpeg output.
    public byte[] Data { get; set; }

    // Target encode bitrate for this segment's quality tier, copied from
    // BitrateTable.Kbps[Quality] at transcode time. AbrPlayer uses it to estimate
    // how long this segment will take to download and whether the buffer will grow
    // or shrink during playback of this tick.
    //
    // ── RUNTIME SNAPSHOT (30-second video, 4 renditions, 5 segments each = 20 total) ──
    //   TranscodeWorker produces this grid of segments:
    //
    //   VideoId = "b7f3a1c9e2d8"
    //   ┌──────────────┬──────────────┬──────────────┐
    //   │  Quality     │ BitrateKbps  │ SegmentIndex │
    //   ├──────────────┼──────────────┼──────────────┤
    //   │ R360p        │   400        │  0 … 4       │   5 segments
    //   │ R480p        │   800        │  0 … 4       │   5 segments
    //   │ R720p        │  2500        │  0 … 4       │   5 segments
    //   │ R1080p       │  5000        │  0 … 4       │   5 segments
    //   └──────────────┴──────────────┴──────────────┘
    //
    //   Example segment object:
    //     { VideoId="b7f3a1c9e2d8", Quality=R720p, SegmentIndex=2,
    //       BitrateKbps=2500, Data=[0x02,0x02,0xAB,0xCD] }
    //     Url → "hls/b7f3a1c9e2d8/R720p/seg002.ts"
    //
    //   HlsStore keys: _segments["hls/b7f3a1c9e2d8/R720p/seg002.ts"] = <above>
    //
    //   AbrPlayer at PositionSeconds=12, Quality=R720p:
    //     SegmentIndex = 12 / 6 = 2
    //     Url = "hls/b7f3a1c9e2d8/R720p/seg002.ts"  ← CDN hit
    //     downloadTimeSec = (2500 / 3000) * 6 = 5.0s  (throughput=3000 Kbps)
    //
    //   AbrPlayer drops to R480p at PositionSeconds=18 (buffer low):
    //     SegmentIndex = 18 / 6 = 3
    //     Url = "hls/b7f3a1c9e2d8/R480p/seg003.ts"  ← quality switch, same index
    //     downloadTimeSec = (800 / 3000) * 6 = 1.6s  ← cheaper, buffer recovers
    public int BitrateKbps { get; set; }

    // Content-addressed URL computed from the three identity fields.
    // Used as the dictionary key in HlsStore and as the CDN request path.
    // Never stored separately — always derived on demand so it can't drift
    // out of sync with the fields that define it.
    public string Url => $"hls/{VideoId}/{Quality}/seg{SegmentIndex:D3}.ts";
}
