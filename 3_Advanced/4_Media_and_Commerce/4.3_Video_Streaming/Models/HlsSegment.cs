// HlsSegment — one ~6-second, independently-decodable slice of one rendition.
//
// THE BIG IDEA:
// Like a page in a flipbook: a fixed time window, self-contained (starts with a keyframe, so it
// decodes without the previous page), stored at a predictable address (Url). Seeking and ABR
// quality switches are both just "fetch a different segment URL."
//
// WHY THE URL IS CONTENT-ADDRESSED (videoId + quality + index): the same triple always yields
// the same URL and bytes, so the CDN can cache it ~forever and never serve stale data — a
// re-transcode gets a new VideoId, hence new URLs. No global cache purge needed.
//
// WHY BitrateKbps IS STORED HERE (not looked up): AbrPlayer computes download time =
// (BitrateKbps / throughput) * 6 every tick; keeping it on the segment avoids a table lookup.
//
// HOW IT BEHAVES AT RUNTIME (one segment object; Url is derived from the first three fields):
//
//   field        | value
//   -------------|--------------------------------------
//   VideoId      | vacation
//   Quality      | R720p
//   SegmentIndex | 2            (= playhead 12s / 6)
//   BitrateKbps  | 2500
//   Url          | hls/vacation/R720p/seg002.ts
//
//   Quality switch to R480p at the same window -> Url becomes hls/vacation/R480p/seg002.ts
//   (only Quality changed; same index, so it's the same 6s of video at a lower bitrate).

public class HlsSegment
{
    // The video this slice belongs to. Part of the content-addressed Url.
    public string VideoId { get; set; }

    // Quality tier (R360p..R4K). AbrPlayer swaps this to switch quality; Url recomputes.
    public Rendition Quality { get; set; }

    // Zero-based position within the rendition. AbrPlayer derives it: PositionSeconds / 6.
    // Url zero-pads to 3 digits (seg002) so paths sort chronologically as strings.
    public int SegmentIndex { get; set; }

    // The raw bytes. Production: not held here — the CDN streams them from object storage via Url.
    // Demo: 4-byte stub written by TranscodeWorker so segment objects exist without real ffmpeg.
    public byte[] Data { get; set; }

    // Encode bitrate, copied from BitrateTable at transcode time so the segment is self-describing.
    public int BitrateKbps { get; set; }

    // Content-addressed URL, always derived from the three identity fields so it can't drift.
    // Used as the HlsStore dictionary key and the CDN request path.
    public string Url => $"hls/{VideoId}/{Quality}/seg{SegmentIndex:D3}.ts";
}
