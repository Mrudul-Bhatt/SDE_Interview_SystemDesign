// TranscodeWorker — turns one raw upload into a full HLS rendition ladder.
//
// THE BIG IDEA:
// Like a publisher printing one manuscript in several editions. The raw file is re-encoded
// into multiple bitrates (360p / 720p / 1080p) and each edition is sliced into 6-second
// segments. A master manifest lists the editions. The player (AbrPlayer) then picks whichever
// edition its network can sustain and switches between them as conditions change — that's
// adaptive bitrate (ABR) streaming.
//
// WHY THE 3-STEP ORDER MATTERS (segments → manifest → Status=Ready, never reordered):
// Status=Ready is the signal players wait for. Writing segments first, then the manifest,
// then flipping Ready last guarantees that by the time any player sees Ready, every segment
// the manifest references already exists. Reorder it and a player can hit a 404 mid-play.
//
// WHY 6-SECOND SEGMENTS: short enough that ABR can react to a bandwidth change within ~6s and
// seeking is cheap; long enough to avoid flooding the CDN with requests (a 2h film = 1,200
// requests, not 3,600). Apple's HLS default, used by Netflix/YouTube/Twitch.
//
// HOW IT BEHAVES AT RUNTIME (transcode "vacation", 300s, 3 tiers; 50 segs/tier = 150):
//
//   Step                                | State after
//   ------------------------------------|--------------------------------------
//   guard: RawVideoStore.Exists?        | bails out if the source bytes are missing
//   step 1: write 150 segments          | HlsStore.segments={150}  manifest absent
//   step 2: write manifest              | HlsStore.manifests={vacation}
//   step 3: Upsert Status=Ready         | VideoMetaStore={ vacation -> Ready }  (now playable)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class TranscodeWorker
{
    private readonly RawVideoStore _raw;
    private readonly HlsStore _hls;
    private readonly VideoMetaStore _meta;

    // Fixed segment duration. AbrPlayer relies on it (SegmentIndex = PositionSeconds / 6),
    // so changing it would break index math for in-flight players.
    private const int SegmentSeconds = 6;

    public TranscodeWorker(RawVideoStore raw, HlsStore hls, VideoMetaStore meta)
    {
        _raw = raw;
        _hls = hls;
        _meta = meta;
    }

    // renditions is nullable: a low-res source can't be meaningfully upscaled to 4K, so callers
    // pass only the tiers that make sense; null falls back to the standard 360p–1080p ladder.
    public void Process(string videoId, int durationSeconds, string title, string uploaderId,
                        IEnumerable<Rendition> renditions = null)
    {
        Console.WriteLine($"  [Transcode] Starting {videoId} ({durationSeconds}s)");

        // Guard: no source bytes => abort loudly rather than emit empty output.
        if (!_raw.Exists(videoId))
        {
            Console.WriteLine($"  [Transcode] ERROR: raw video {videoId} not found");
            return;
        }

        // ToList() so we can enumerate the renditions twice (segment loop + BuildManifest).
        var targetRenditions = renditions?.ToList()
            ?? [Rendition.R360p, Rendition.R480p, Rendition.R720p, Rendition.R1080p];

        // Ceiling so a trailing partial segment still gets its own index.
        int numSegments = (int)Math.Ceiling((double)durationSeconds / SegmentSeconds);

        // Step 1: write every (rendition × segment). Production runs these in parallel on a
        // GPU farm via FFmpeg; here the bytes are stubbed.
        foreach (var r in targetRenditions)
        {
            for (int i = 0; i < numSegments; i++)
            {
                var seg = new HlsSegment
                {
                    VideoId = videoId,
                    Quality = r,
                    SegmentIndex = i,
                    BitrateKbps = BitrateTable.Kbps[r], // self-describing: AbrPlayer reads it directly
                    Data = [(byte)r, (byte)i, 0xAB, 0xCD] // stub bytes
                };
                _hls.StoreSegment(seg);
            }
            Console.WriteLine($"  [Transcode] {videoId} {r}: {numSegments} segments written");
        }

        // Step 2: manifest only after all segments exist.
        var manifest = BuildManifest(videoId, targetRenditions);
        _hls.StoreManifest(videoId, manifest);

        // Step 3: flip Ready last — the atomic "go live" gate.
        _meta.Upsert(new VideoMetadata
        {
            VideoId = videoId,
            UploaderId = uploaderId,
            Title = title,
            Status = VideoStatus.Ready,
            DurationSeconds = durationSeconds,
            CreatedAt = DateTime.UtcNow,
            ManifestUrl = $"hls/{videoId}/manifest.m3u8"
        });

        Console.WriteLine($"  [Transcode] {videoId} READY — manifest written");
    }

    // Builds the master M3U8: one BANDWIDTH line per rendition. Highest bitrate is listed first
    // by convention — players scan top-down and pick the first variant that fits their network.
    //
    //   #EXTM3U
    //   #EXT-X-VERSION:3
    //   #EXT-X-STREAM-INF:BANDWIDTH=5000000      (R1080p; Kbps × 1000 = bits/sec)
    //   hls/vacation/R1080p/index.m3u8
    //   ... then R720p, then R360p ...
    private string BuildManifest(string videoId, List<Rendition> renditions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        foreach (var r in renditions.OrderByDescending(r => BitrateTable.Kbps[r]))
        {
            int bps = BitrateTable.Kbps[r] * 1000;
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bps}");
            sb.AppendLine($"hls/{videoId}/{r}/index.m3u8");
        }
        return sb.ToString();
    }
}
