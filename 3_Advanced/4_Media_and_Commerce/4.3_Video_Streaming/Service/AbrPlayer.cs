// AbrPlayer — client-side adaptive bitrate streaming.
//
// On open: fetches the saved playhead from WatchHistory so the user resumes
// where they left off.
//
// Per segment: ChooseQuality picks the highest rendition whose bitrate fits
// within 80% of measured throughput (the 20% headroom absorbs short-term
// throughput dips without forcing a downshift). If buffer falls below 5s we
// EMERGENCY DROP to 360p — preserving playback continuity is more important
// than picture quality.
//
// Each "tick" simulates: download time drains buffer, +6s of playback gets
// added, position advances, heartbeat fires (for view counting), watch
// position is checkpointed.

using System;
using System.Linq;

public class AbrPlayer
{
    private readonly CdnEdgeCache  _cdn;
    private readonly WatchHistory  _history;
    private readonly ViewCounter   _counter;
    private Rendition _currentQuality = Rendition.R720p;
    private double    _bufferSeconds  = 5.0; // initial startup buffer
    private const double BufferTarget = 20.0;

    public string UserId  { get; }
    public string VideoId { get; }
    public int PositionSeconds { get; private set; }

    public AbrPlayer(string userId, string videoId, CdnEdgeCache cdn, WatchHistory history, ViewCounter counter)
    {
        UserId  = userId;
        VideoId = videoId;
        _cdn    = cdn;
        _history   = history;
        _counter   = counter;

        // Resume from last position
        var progress = _history.Get(userId, videoId);
        PositionSeconds = progress?.PositionSeconds ?? 0;
        Console.WriteLine($"  [Player] {userId} opening {videoId}, resuming from {PositionSeconds}s");
    }

    public void Play(int numSegments, int simulatedThroughputKbps)
    {
        var manifest = _cdn.GetManifest(VideoId);
        if (manifest == null)
        {
            Console.WriteLine($"  [Player] ERROR: manifest not found for {VideoId}");
            return;
        }

        for (int i = 0; i < numSegments; i++)
        {
            _currentQuality = ChooseQuality(simulatedThroughputKbps, _bufferSeconds);

            var url = new HlsSegment
            {
                VideoId      = VideoId,
                Quality      = _currentQuality,
                SegmentIndex = PositionSeconds / 6
            }.Url;

            var seg = _cdn.GetSegment(url);
            if (seg == null)
            {
                Console.WriteLine($"  [Player] segment {url} not available");
                break;
            }

            // Downloading a 6s segment takes (bitrate / throughput) * 6 seconds of wall-clock time.
            // During that download time, the player consumes from the buffer.
            double downloadTimeSec = (double)BitrateTable.Kbps[_currentQuality] / simulatedThroughputKbps * 6.0;
            _bufferSeconds -= downloadTimeSec;
            _bufferSeconds  = Math.Max(_bufferSeconds, 0);
            _bufferSeconds += 6;
            _bufferSeconds  = Math.Min(_bufferSeconds, BufferTarget);

            PositionSeconds += 6;

            _counter.RecordHeartbeat(UserId, VideoId, PositionSeconds);
            _history.Update(UserId, VideoId, PositionSeconds);

            Console.WriteLine($"  [Player] t={PositionSeconds}s  quality={_currentQuality}  buffer={_bufferSeconds:F1}s  throughput={simulatedThroughputKbps}kbps");
        }
    }

    private Rendition ChooseQuality(int throughputKbps, double bufferSeconds)
    {
        if (bufferSeconds < 5) return Rendition.R360p; // emergency drop

        // Pick highest rendition whose bitrate fits within 80% of throughput
        var ordered = Enum.GetValues(typeof(Rendition))
                          .Cast<Rendition>()
                          .OrderByDescending(r => BitrateTable.Kbps[r]);

        foreach (var r in ordered)
            if (BitrateTable.Kbps[r] < throughputKbps * 0.8)
                return r;

        return Rendition.R360p;
    }
}
