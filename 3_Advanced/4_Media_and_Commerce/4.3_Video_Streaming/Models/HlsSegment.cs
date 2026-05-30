// HlsSegment — one ~6-second slice of one rendition.
//
// The Url is content-addressed by (videoId, quality, segmentIndex), which makes
// segments effectively immutable: re-transcodes produce different URLs, so CDN
// caches never serve stale bytes for a given URL. This is why we can set very
// long cache TTLs without risking inconsistency.

public class HlsSegment
{
    public string VideoId    { get; set; }
    public Rendition Quality { get; set; }
    public int SegmentIndex  { get; set; }
    public byte[] Data       { get; set; }
    public int BitrateKbps   { get; set; }
    public string Url => $"hls/{VideoId}/{Quality}/seg{SegmentIndex:D3}.ts";
}
