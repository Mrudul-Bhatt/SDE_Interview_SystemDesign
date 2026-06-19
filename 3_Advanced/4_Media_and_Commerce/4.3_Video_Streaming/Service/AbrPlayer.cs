// AbrPlayer — the client-side adaptive bitrate (ABR) player.
//
// THE BIG IDEA:
// A video is stored at several qualities. The player's job each segment is to pick the highest
// quality the current network can sustain — high enough to look good, low enough not to stall.
// As bandwidth changes mid-stream, it switches quality between segments. On open it also
// resumes from the viewer's saved playhead (via WatchHistory) so they pick up where they left off.
//
// WHY 80% OF THROUGHPUT (not 100%): measured throughput is a trailing average, not a promise.
// Picking a rendition that needs 100% of it would stall the moment bandwidth dips. The 0.8
// factor leaves 20% headroom — e.g. R720p (2500 Kbps) is only chosen at >=3125 Kbps.
//
// WHY EMERGENCY DROP AT buffer < 5s: a near-empty buffer means a freeze is imminent. Dropping
// straight to 360p (smallest, fastest to download) refills the buffer fastest — a small blurry
// picture beats a spinning wheel.
//
// HOW IT BEHAVES AT RUNTIME (quality choice = highest tier with bitrate < throughput × 0.8):
//
//   Situation                    | Budget (×0.8) | Quality chosen
//   -----------------------------|---------------|-----------------------------
//   throughput 8000 (Bob, good)  | 6400          | R1080p (5000 fits)
//   throughput 500  (Eve, poor)  | 400           | R360p  (nothing fits -> floor)
//   buffer < 5s (any throughput) | —             | R360p  (emergency, ignores network)
//
//   Buffer math per 6s segment: download takes (bitrate / throughput) × 6 seconds; the player
//   drains that much, then adds 6s of new content. R1080p at 8000 Kbps: 5000/8000×6 = 3.75s
//   to download, so the buffer GROWS by 6 - 3.75 = 2.25s each tick (stays ahead of the playhead).

using System;
using System.Linq;

public class AbrPlayer
{
    private readonly CdnEdgeCache  _cdn;
    private readonly WatchHistory  _history;
    private readonly ViewCounter   _counter;
    private Rendition _currentQuality = Rendition.R720p;
    private double    _bufferSeconds  = 5.0;  // startup buffer
    private const double BufferTarget = 20.0; // cap; player stops pre-buffering past this

    public string UserId  { get; }
    public string VideoId { get; }
    public int PositionSeconds { get; private set; }

    public AbrPlayer(string userId, string videoId, CdnEdgeCache cdn, WatchHistory history, ViewCounter counter)
    {
        UserId  = userId;
        VideoId = videoId;
        _cdn    = cdn;
        _history = history;
        _counter = counter;

        // Resume from the saved playhead, or start at 0 if this video was never watched.
        var progress = _history.Get(userId, videoId);
        PositionSeconds = progress?.PositionSeconds ?? 0;
        Console.WriteLine($"  [Player] {userId} opening {videoId}, resuming from {PositionSeconds}s");
    }

    // Simulates playing numSegments segments at a fixed (simulated) network throughput.
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
            // Decide quality for THIS segment based on network + buffer health.
            _currentQuality = ChooseQuality(simulatedThroughputKbps, _bufferSeconds);

            // The segment URL is computed client-side from (videoId, quality, index) — no lookup.
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

            // Buffer dynamics: downloading drains the buffer, then 6s of playback is added.
            double downloadTimeSec = (double)BitrateTable.Kbps[_currentQuality] / simulatedThroughputKbps * 6.0;
            _bufferSeconds -= downloadTimeSec;
            _bufferSeconds  = Math.Max(_bufferSeconds, 0);
            _bufferSeconds += 6;
            _bufferSeconds  = Math.Min(_bufferSeconds, BufferTarget);

            PositionSeconds += 6;

            // Tell ViewCounter we're watching, and checkpoint the playhead for resume.
            _counter.RecordHeartbeat(UserId, VideoId, PositionSeconds);
            _history.Update(UserId, VideoId, PositionSeconds);

            Console.WriteLine($"  [Player] t={PositionSeconds}s  quality={_currentQuality}  buffer={_bufferSeconds:F1}s  throughput={simulatedThroughputKbps}kbps");
        }
    }

    // Picks the highest rendition whose bitrate fits within 80% of throughput.
    // Buffer emergency overrides everything: near-empty buffer -> smallest rendition.
    private Rendition ChooseQuality(int throughputKbps, double bufferSeconds)
    {
        if (bufferSeconds < 5) return Rendition.R360p; // emergency drop

        foreach (var r in Enum.GetValues(typeof(Rendition))
                              .Cast<Rendition>()
                              .OrderByDescending(r => BitrateTable.Kbps[r]))
            if (BitrateTable.Kbps[r] < throughputKbps * 0.8)
                return r;

        return Rendition.R360p; // nothing fits -> lowest quality floor
    }
}
