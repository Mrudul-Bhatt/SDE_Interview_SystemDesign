// TranscodeWorker — converts one raw uploaded file into a full HLS rendition ladder.
//
// THE BIG IDEA:
// When a video is uploaded, the raw file is one monolithic blob — a single quality, a
// single format, not streamable mid-video. To support adaptive streaming, TranscodeWorker
// re-encodes it into multiple bitrates (the "rendition ladder") and chops each rendition
// into 6-second segments — a format called HLS (HTTP Live Streaming).
//
// Think of it like a publisher taking a manuscript and printing it in three editions:
//   Pocket paperback  (360p,  400 Kbps) — for readers on slow 3G connections
//   Trade paperback   (720p, 2500 Kbps) — for readers on standard home WiFi
//   Hardcover        (1080p, 5000 Kbps) — for readers on fast cable / fibre
// Each edition is sliced into chapters of fixed length (6 seconds). A table of contents
// (master manifest) lists all editions. The reader (AbrPlayer) picks which edition to
// read based on how fast they can turn pages (their bandwidth), and can switch editions
// between chapters if their connection improves or degrades.
//
// ── THE ORDER OF OPERATIONS (critical — these three steps must not be reordered) ──
//
//   Step 1 — WRITE ALL SEGMENTS: every 6-second slice of every rendition goes into HlsStore.
//   Step 2 — WRITE MANIFEST:     the master M3U8 that lists all renditions is written.
//   Step 3 — FLIP STATUS=READY:  VideoMetadata.Status changes from Processing → Ready.
//
// WHY THIS ORDER IS NON-NEGOTIABLE:
// Status=Ready is the signal that players poll for before loading a video. If Ready is
// set before the segments exist, a player opens the manifest and finds segment URLs that
// 404 → unplayable crash. If the manifest is written before segments are stored, the same
// race: the manifest references URLs that aren't in HlsStore yet. By the time any player
// sees Ready=true, all 150 segments (or however many) are already guaranteed to exist.
// This is the "write-behind gate" pattern: write the data, then flip the visibility flag.
//
// WHY SEGMENT LENGTH IS 6 SECONDS:
// Segment length is the master trade-off in HLS:
//
//   Too short (1-2s) → hundreds of HTTP requests per minute → high CDN load and
//                       per-request overhead. A 2-hour film at 2s segments =
//                       3,600 requests per viewer just to finish the movie.
//
//   Too long (30s)   → ABR quality switching is delayed by up to 30 seconds — the
//                       player is committed to downloading the wrong-quality chunk
//                       before it can react to a changed network. Seeking also feels
//                       slow: jumping to 1:23:45 downloads a 30s block for a few frames.
//
//   6 seconds        → Apple's HLS spec default; used by Netflix, YouTube, and Twitch.
//                       A 2-hour film = 1,200 requests. ABR reacts within ~6 seconds of
//                       a bandwidth change. Seeking worst-case: 6s of data for 1 frame.
//
// WHY MULTIPLE RENDITIONS (not just one high-quality file):
// Network conditions vary enormously — a viewer on a plane's WiFi gets ~500 Kbps; one
// on gigabit fibre gets 100+ Mbps. A single 1080p file buffers endlessly for the slow
// viewer. A single 360p file wastes the fast viewer's connection. Multiple renditions let
// AbrPlayer pick the best quality the current network can sustain and switch as conditions
// change. This is Adaptive Bitrate (ABR) streaming — the core of every modern video
// platform (YouTube, Netflix, Twitch, Disney+).
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — Process("x9y8z7", 300s, "Vacation 2024", "alice", [R360p,R720p,R1080p]):
//
//   numSegments = Math.Ceiling(300 / 6) = 50 per rendition
//   Total writes = 3 renditions × 50 segments = 150 segment objects
//
//   ┌─ Before Process() ──────────────────────────────────────────────────────────────
//   │  RawVideoStore["x9y8z7"] = <15 MB raw bytes>   ← written by UploadService
//   │  HlsStore._segments      = { }                 ← empty
//   │  VideoMetaStore          = { }                 ← empty (video not discoverable)
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After R360p loop (50 segments written) ────────────────────────────────────────
//   │  HlsStore._segments = {
//   │    "hls/x9y8z7/R360p/seg000.ts" → { BitrateKbps=400, Data=[0x00,0x00,0xAB,0xCD] }
//   │    "hls/x9y8z7/R360p/seg001.ts" → { BitrateKbps=400, Data=[0x00,0x01,0xAB,0xCD] }
//   │    ...
//   │    "hls/x9y8z7/R360p/seg049.ts" → { BitrateKbps=400, Data=[0x00,0x31,0xAB,0xCD] }
//   │  }  (50 keys)
//   │  Manifest:  NOT written yet  →  video still not discoverable or playable
//   │  Status:    NOT Ready        →  players checking VideoMetaStore see nothing
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After R720p + R1080p loops (150 segments total) ──────────────────────────────
//   │  HlsStore._segments also contains:
//   │    "hls/x9y8z7/R720p/seg000.ts"  … seg049.ts  (BitrateKbps=2500)
//   │    "hls/x9y8z7/R1080p/seg000.ts" … seg049.ts  (BitrateKbps=5000)
//   │  Total keys: 150
//   │  Manifest:  still NOT written   →  still not live
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After BuildManifest + HlsStore.StoreManifest ─────────────────────────────────
//   │  HlsStore._manifest["x9y8z7"] =
//   │    #EXTM3U
//   │    #EXT-X-VERSION:3
//   │    #EXT-X-STREAM-INF:BANDWIDTH=5000000     ← R1080p first (highest → listed first)
//   │    hls/x9y8z7/R1080p/index.m3u8
//   │    #EXT-X-STREAM-INF:BANDWIDTH=2500000
//   │    hls/x9y8z7/R720p/index.m3u8
//   │    #EXT-X-STREAM-INF:BANDWIDTH=400000
//   │    hls/x9y8z7/R360p/index.m3u8
//   │  Status: still NOT Ready  →  still not live (manifest exists but no player finds it)
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After _meta.Upsert(Status=Ready) — the "go live" moment ──────────────────────
//   │  VideoMetaStore["x9y8z7"] = {
//   │    VideoId="x9y8z7",  Status=Ready,  Title="Vacation 2024",
//   │    DurationSeconds=300,  ManifestUrl="hls/x9y8z7/manifest.m3u8"
//   │  }
//   │  ← All 150 segments + manifest already exist. Any player that now sees
//   │    Status=Ready can safely fetch the manifest and start streaming immediately.
//   └─────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class TranscodeWorker
{
    private readonly RawVideoStore  _raw;
    private readonly HlsStore       _hls;
    private readonly VideoMetaStore _meta;

    // The fixed segment duration for all HLS output. Every segment (except the last)
    // covers exactly this many seconds. Changing this constant would break in-flight
    // players because AbrPlayer computes SegmentIndex = PositionSeconds / 6 — if
    // segments were 10s instead, the index arithmetic would land on wrong segments.
    private const int SegmentSeconds = 6;

    public TranscodeWorker(RawVideoStore raw, HlsStore hls, VideoMetaStore meta)
    {
        _raw  = raw;
        _hls  = hls;
        _meta = meta;
    }

    // Transcodes one video end-to-end: raw bytes → HLS segments → manifest → Ready.
    //
    //   videoId         — stable ID from UploadSession; becomes the CDN path prefix
    //   durationSeconds — source video length, drives how many segments to produce
    //   title/uploaderId — forwarded to VideoMetadata for search and attribution
    //   renditions      — which quality tiers to produce; null → default 4-tier ladder
    //
    // WHY renditions IS NULLABLE WITH A DEFAULT:
    // A 240p tutorial screen-recording cannot meaningfully be upscaled to 4K — the GPU
    // would waste time producing a blurry, oversized file. Callers can pass only the
    // renditions that make sense for the source resolution. The default (360p through
    // 1080p) covers the vast majority of uploaded content.
    public void Process(string videoId, int durationSeconds, string title, string uploaderId,
                        IEnumerable<Rendition> renditions = null)
    {
        Console.WriteLine($"  [Transcode] Starting {videoId} ({durationSeconds}s)");

        // Guard: raw bytes must be in RawVideoStore before transcoding can begin.
        // A missing raw file means the upload completed inconsistently — worth surfacing
        // loudly (in production: alert + dead-letter the transcode job) rather than
        // silently producing empty output.
        if (!_raw.Exists(videoId))
        {
            Console.WriteLine($"  [Transcode] ERROR: raw video {videoId} not found");
            return;
        }

        // Materialise the rendition list once. ToList() prevents double-enumeration:
        // we iterate targetRenditions in the segment loop below AND in BuildManifest.
        var targetRenditions = renditions?.ToList()
            ?? new List<Rendition> { Rendition.R360p, Rendition.R480p, Rendition.R720p, Rendition.R1080p };

        // Math.Ceiling so the last partial segment gets its own index rather than
        // being dropped. Examples:
        //   300s / 6s = 50.00 → 50 segments (clean boundary)
        //   301s / 6s = 50.17 → 51 segments (last = 1 second of content)
        int numSegments = (int)Math.Ceiling((double)durationSeconds / SegmentSeconds);

        // ── Step 1: Write all segments — BEFORE the manifest or status flip ──────────
        // In production: FFmpeg spawns one process per rendition and they run in parallel
        // on a GPU cluster. Here we simulate it sequentially with stub data.
        // The nested loop order (rendition outer, segment inner) means each rendition's
        // segment range is written atomically before moving to the next quality tier.
        foreach (var r in targetRenditions)
        {
            for (int i = 0; i < numSegments; i++)
            {
                var seg = new HlsSegment
                {
                    VideoId      = videoId,
                    Quality      = r,
                    SegmentIndex = i,
                    // Copy bitrate from BitrateTable so the segment is self-describing —
                    // AbrPlayer can compute download time from the segment alone without
                    // a separate table lookup per tick.
                    BitrateKbps  = BitrateTable.Kbps[r],
                    // Stub: real bytes would be MPEG-TS frames from FFmpeg. The 4-byte
                    // pattern [(byte)r, (byte)i, 0xAB, 0xCD] makes segments distinguishable
                    // in tests (each segment has a unique first two bytes).
                    Data = new byte[] { (byte)r, (byte)i, 0xAB, 0xCD }
                    // Url is computed: "hls/{videoId}/{r}/seg{i:D3}.ts"  (see HlsSegment.Url)
                };
                _hls.StoreSegment(seg);
            }
            Console.WriteLine($"  [Transcode] {videoId} {r}: {numSegments} segments written");
        }

        // ── Step 2: Write the master M3U8 manifest — AFTER all segments ──────────────
        // A player fetches this file first. It lists all rendition URLs with their
        // BANDWIDTH values; the player picks whichever rendition fits its throughput.
        // Writing this after segments means no player can ever load the manifest
        // and then 404 on a segment URL — all URLs in the manifest already exist.
        var manifest = BuildManifest(videoId, targetRenditions);
        _hls.StoreManifest(videoId, manifest);

        // ── Step 3: Flip Status=Ready — the "go live" gate, always last ──────────────
        // AbrPlayer.Play() will only stream a video once its VideoMetadata.Status==Ready.
        // This is the atomic visibility flip. By the time it executes, both steps 1 and 2
        // are complete — 150 segments in HlsStore + manifest written. No player can
        // observe a partial state where Ready=true but content is missing.
        _meta.Upsert(new VideoMetadata
        {
            VideoId         = videoId,
            UploaderId      = uploaderId,
            Title           = title,
            Status          = VideoStatus.Ready,
            DurationSeconds = durationSeconds,
            CreatedAt       = DateTime.UtcNow,
            ManifestUrl     = $"hls/{videoId}/manifest.m3u8"
        });

        Console.WriteLine($"  [Transcode] {videoId} READY — manifest written");
    }

    // Builds the HLS master manifest: a plain-text M3U8 listing every rendition URL
    // and its target bandwidth. Players fetch this first when opening a video.
    //
    // WHY HIGHEST BANDWIDTH IS LISTED FIRST:
    // The M3U8 spec doesn't mandate ordering, but players conventionally scan top-to-bottom
    // and pick the FIRST variant whose BANDWIDTH fits their connection. Listing highest
    // quality first means a fast-connection player picks 1080p immediately without scanning
    // the whole file. A 3G player falls through the list until it reaches 360p.
    // Also, some players display the first-listed rendition briefly before their ABR logic
    // kicks in — highest first = best initial frame, not worst.
    //
    // ── EXAMPLE OUTPUT (videoId="x9y8z7", renditions=[R360p, R720p, R1080p]) ────────
    //   #EXTM3U
    //   #EXT-X-VERSION:3
    //   #EXT-X-STREAM-INF:BANDWIDTH=5000000       ← R1080p (5000 Kbps × 1000 = bits/sec)
    //   hls/x9y8z7/R1080p/index.m3u8
    //   #EXT-X-STREAM-INF:BANDWIDTH=2500000
    //   hls/x9y8z7/R720p/index.m3u8
    //   #EXT-X-STREAM-INF:BANDWIDTH=400000
    //   hls/x9y8z7/R360p/index.m3u8
    //
    // A player with 3000 Kbps throughput applies the 80% headroom from AbrPlayer:
    //   Budget = 3000 × 0.8 = 2400 Kbps
    //   R1080p (5000 Kbps) > 2400 → skip
    //   R720p  (2500 Kbps) > 2400 → skip
    //   R360p  ( 400 Kbps) ≤ 2400 → selected
    // (At 3200 Kbps, budget=2560 → R720p passes: 2500 ≤ 2560 → selected)
    private string BuildManifest(string videoId, List<Rendition> renditions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        foreach (var r in renditions.OrderByDescending(r => BitrateTable.Kbps[r]))
        {
            // BANDWIDTH tag uses bits/sec, not Kbps — multiply by 1000.
            int bps = BitrateTable.Kbps[r] * 1000;
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bps}");
            sb.AppendLine($"hls/{videoId}/{r}/index.m3u8");
        }
        return sb.ToString();
    }
}
