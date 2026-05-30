// TranscodeWorker — converts one raw upload into multiple renditions of HLS segments.
//
// Order of operations matters:
//   1. Write all segments to the HLS store FIRST.
//   2. Write the master manifest SECOND.
//   3. Flip metadata status to Ready LAST.
//
// This sequence ensures players never see Ready=true for a video whose manifest
// references segments that don't exist yet. The metadata flip is the atomic
// "this video is now playable" signal.
//
// In production this runs on a GPU farm using FFmpeg; renditions transcode in
// parallel. Segment length is fixed at 6 seconds — short enough for fast ABR
// switching, long enough to amortize HTTP overhead.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class TranscodeWorker
{
    private readonly RawVideoStore  _raw;
    private readonly HlsStore       _hls;
    private readonly VideoMetaStore _meta;
    private const int SegmentSeconds = 6;

    public TranscodeWorker(RawVideoStore raw, HlsStore hls, VideoMetaStore meta)
    {
        _raw  = raw;
        _hls  = hls;
        _meta = meta;
    }

    public void Process(string videoId, int durationSeconds, string title, string uploaderId,
                        IEnumerable<Rendition> renditions = null)
    {
        Console.WriteLine($"  [Transcode] Starting {videoId} ({durationSeconds}s)");

        if (!_raw.Exists(videoId))
        {
            Console.WriteLine($"  [Transcode] ERROR: raw video {videoId} not found");
            return;
        }

        var targetRenditions = renditions?.ToList()
            ?? new List<Rendition> { Rendition.R360p, Rendition.R480p, Rendition.R720p, Rendition.R1080p };

        int numSegments = (int)Math.Ceiling((double)durationSeconds / SegmentSeconds);

        // Simulate transcoding each rendition
        foreach (var r in targetRenditions)
        {
            for (int i = 0; i < numSegments; i++)
            {
                var seg = new HlsSegment
                {
                    VideoId      = videoId,
                    Quality      = r,
                    SegmentIndex = i,
                    BitrateKbps  = BitrateTable.Kbps[r],
                    // Simulate segment data: [rendition, segIndex, ...padding]
                    Data = new byte[] { (byte)r, (byte)i, 0xAB, 0xCD }
                };
                _hls.StoreSegment(seg);
            }
            Console.WriteLine($"  [Transcode] {videoId} {r}: {numSegments} segments written");
        }

        // Write master M3U8 only AFTER all segments are in place
        var manifest = BuildManifest(videoId, targetRenditions);
        _hls.StoreManifest(videoId, manifest);

        // Flip status to Ready last — this is the "go live" moment
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

    private string BuildManifest(string videoId, List<Rendition> renditions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        // List highest bitrate first — convention so players know all options up front
        foreach (var r in renditions.OrderByDescending(r => BitrateTable.Kbps[r]))
        {
            int bps = BitrateTable.Kbps[r] * 1000;
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bps}");
            sb.AppendLine($"hls/{videoId}/{r}/index.m3u8");
        }
        return sb.ToString();
    }
}
