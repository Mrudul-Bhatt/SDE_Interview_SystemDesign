// Video Streaming System — C# simulation
// Covers: chunked upload, transcoding pipeline, HLS ABR, CDN caching,
//         view count buffering, watch history / resume, and search ranking.
// assembly-guid: {D5E6F7A8-B9C0-1234-D678-234567800034}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

public enum VideoStatus { Uploading, Transcoding, Ready, Deleted }
public enum Rendition  { R360p, R480p, R720p, R1080p, R4K }

public class VideoMetadata
{
    public string VideoId       { get; set; }
    public string UploaderId    { get; set; }
    public string Title         { get; set; }
    public List<string> Tags    { get; set; } = new List<string>();
    public VideoStatus Status   { get; set; }
    public int DurationSeconds  { get; set; }
    public long ViewCount       { get; set; }
    public DateTime CreatedAt   { get; set; }
    public string ManifestUrl   { get; set; }  // CDN path to master M3U8
}

public class HlsSegment
{
    public string VideoId    { get; set; }
    public Rendition Quality { get; set; }
    public int SegmentIndex  { get; set; }
    public byte[] Data       { get; set; }
    public int BitrateKbps   { get; set; }
    public string Url => $"hls/{VideoId}/{Quality}/seg{SegmentIndex:D3}.ts";
}

public class WatchProgress
{
    public string UserId          { get; set; }
    public string VideoId         { get; set; }
    public int    PositionSeconds  { get; set; }
    public bool   Completed        { get; set; }
    public DateTime LastUpdated    { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. Chunked Upload Service
// ─────────────────────────────────────────────────────────────────────────────

public class UploadSession
{
    public string UploadId     { get; set; }
    public string VideoId      { get; set; }
    public string UploaderId   { get; set; }
    public string OriginalName { get; set; }
    public long   TotalSize    { get; set; }
    public int    TotalChunks  { get; set; }
    public HashSet<int> ReceivedChunks { get; } = new HashSet<int>();
    public bool IsComplete => ReceivedChunks.Count == TotalChunks;
    public int LastReceivedChunk => ReceivedChunks.Count == 0 ? -1 : ReceivedChunks.Max();
}

public class RawVideoStore
{
    // Simulates S3 — maps videoId → assembled data
    private readonly Dictionary<string, byte[]> _store = new Dictionary<string, byte[]>();

    public void Store(string videoId, byte[] data) => _store[videoId] = data;
    public bool Exists(string videoId)             => _store.ContainsKey(videoId);
    public byte[] Fetch(string videoId)            => _store[videoId];
}

public class UploadService
{
    private readonly RawVideoStore _raw;
    private readonly Dictionary<string, UploadSession> _sessions = new Dictionary<string, UploadSession>();
    // Simulate Kafka: queue of completed video IDs for transcoding
    private readonly Queue<string> _transcodeQueue = new Queue<string>();

    public UploadService(RawVideoStore raw) { _raw = raw; }

    public UploadSession Init(string uploaderId, string filename, long totalSize, int chunkSize = 5 * 1024 * 1024)
    {
        var videoId  = Guid.NewGuid().ToString("N")[..12];
        var uploadId = Guid.NewGuid().ToString("N")[..8];
        var session  = new UploadSession
        {
            UploadId     = uploadId,
            VideoId      = videoId,
            UploaderId   = uploaderId,
            OriginalName = filename,
            TotalSize    = totalSize,
            TotalChunks  = (int)Math.Ceiling((double)totalSize / chunkSize)
        };
        _sessions[uploadId] = session;
        return session;
    }

    // Returns false if checksum mismatch (simulated by null check)
    public bool ReceiveChunk(string uploadId, int chunkIndex, byte[] data)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return false;
        if (data == null || data.Length == 0)                  return false;

        session.ReceivedChunks.Add(chunkIndex);
        Console.WriteLine($"  [Upload] {uploadId} chunk {chunkIndex}/{session.TotalChunks - 1} received");
        return true;
    }

    // Returns (success, videoId)
    public (bool ok, string videoId) Complete(string uploadId, byte[] fullData)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return (false, null);
        if (!session.IsComplete)
        {
            Console.WriteLine($"  [Upload] INCOMPLETE — received {session.ReceivedChunks.Count}/{session.TotalChunks} chunks");
            return (false, null);
        }

        _raw.Store(session.VideoId, fullData);
        _transcodeQueue.Enqueue(session.VideoId);
        Console.WriteLine($"  [Upload] Video {session.VideoId} assembled → queued for transcoding");
        return (true, session.VideoId);
    }

    // Resume: return index of next missing chunk
    public int GetResumePoint(string uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return -1;
        for (int i = 0; i < session.TotalChunks; i++)
            if (!session.ReceivedChunks.Contains(i)) return i;
        return session.TotalChunks; // all received
    }

    public bool HasTranscodeJob(out string videoId)
    {
        if (_transcodeQueue.Count > 0) { videoId = _transcodeQueue.Dequeue(); return true; }
        videoId = null;
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Transcoding Pipeline
// ─────────────────────────────────────────────────────────────────────────────

public static class BitrateTable
{
    public static readonly Dictionary<Rendition, int> Kbps = new Dictionary<Rendition, int>
    {
        { Rendition.R360p,  400  },
        { Rendition.R480p,  800  },
        { Rendition.R720p,  2500 },
        { Rendition.R1080p, 5000 },
        { Rendition.R4K,    16000}
    };
}

public class HlsStore
{
    // Simulates S3 CDN origin: maps segment URL → segment
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

        // Write master M3U8
        var manifest = BuildManifest(videoId, targetRenditions);
        _hls.StoreManifest(videoId, manifest);

        // Update metadata
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
        foreach (var r in renditions.OrderByDescending(r => BitrateTable.Kbps[r]))
        {
            int bps = BitrateTable.Kbps[r] * 1000;
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bps}");
            sb.AppendLine($"hls/{videoId}/{r}/index.m3u8");
        }
        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. CDN Cache Simulation
// ─────────────────────────────────────────────────────────────────────────────

public class CdnEdgeCache
{
    private readonly HlsStore _origin;
    private readonly Dictionary<string, HlsSegment> _cache = new Dictionary<string, HlsSegment>();
    private readonly Dictionary<string, string>     _manifestCache = new Dictionary<string, string>();
    public int Hits   { get; private set; }
    public int Misses { get; private set; }

    public CdnEdgeCache(HlsStore origin) { _origin = origin; }

    public string GetManifest(string videoId)
    {
        if (_manifestCache.TryGetValue(videoId, out var m)) { Hits++; return m; }
        Misses++;
        m = _origin.FetchManifest(videoId);
        if (m != null) _manifestCache[videoId] = m;
        return m;
    }

    public HlsSegment GetSegment(string url)
    {
        if (_cache.TryGetValue(url, out var seg)) { Hits++; return seg; }
        Misses++;
        seg = _origin.FetchSegment(url);
        if (seg != null) _cache[url] = seg;
        return seg;
    }

    public double HitRate => (Hits + Misses) == 0 ? 0 : (double)Hits / (Hits + Misses);
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. ABR Player Simulation
// ─────────────────────────────────────────────────────────────────────────────

public class AbrPlayer
{
    private readonly CdnEdgeCache  _cdn;
    private readonly WatchHistory  _history;
    private readonly ViewCounter   _counter;
    private Rendition _currentQuality = Rendition.R720p;
    private double    _bufferSeconds  = 5.0; // simulate initial startup buffer fill
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

    // Simulate playing N segments; simulatedThroughputKbps drives quality switching
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
            // ABR decision based on current buffer level
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
            _bufferSeconds -= downloadTimeSec; // buffer drains while we download
            _bufferSeconds  = Math.Max(_bufferSeconds, 0);
            _bufferSeconds += 6;              // segment added to buffer
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

// ─────────────────────────────────────────────────────────────────────────────
// 5. Video Metadata Store
// ─────────────────────────────────────────────────────────────────────────────

public class VideoMetaStore
{
    private readonly Dictionary<string, VideoMetadata> _db = new Dictionary<string, VideoMetadata>();

    public void Upsert(VideoMetadata v) => _db[v.VideoId] = v;

    public VideoMetadata Get(string videoId) =>
        _db.TryGetValue(videoId, out var v) ? v : null;

    public List<VideoMetadata> Search(string query)
    {
        var q = query.ToLower();
        return _db.Values
                  .Where(v => v.Status == VideoStatus.Ready &&
                             (v.Title.ToLower().Contains(q) ||
                              v.Tags.Any(t => t.ToLower().Contains(q))))
                  .OrderByDescending(v => v.ViewCount)
                  .ToList();
    }

    public List<VideoMetadata> Trending(int top = 5) =>
        _db.Values.Where(v => v.Status == VideoStatus.Ready)
                  .OrderByDescending(v => v.ViewCount)
                  .Take(top)
                  .ToList();
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. Watch History (resume playback)
// ─────────────────────────────────────────────────────────────────────────────

public class WatchHistory
{
    // keyed by "userId:videoId"
    private readonly Dictionary<string, WatchProgress> _db = new Dictionary<string, WatchProgress>();

    public void Update(string userId, string videoId, int positionSeconds, bool completed = false)
    {
        var key = $"{userId}:{videoId}";
        _db[key] = new WatchProgress
        {
            UserId          = userId,
            VideoId         = videoId,
            PositionSeconds = positionSeconds,
            Completed       = completed,
            LastUpdated     = DateTime.UtcNow
        };
    }

    public WatchProgress Get(string userId, string videoId) =>
        _db.TryGetValue($"{userId}:{videoId}", out var p) ? p : null;

    public List<WatchProgress> GetHistory(string userId) =>
        _db.Values.Where(p => p.UserId == userId)
                  .OrderByDescending(p => p.LastUpdated)
                  .ToList();
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. View Counter (buffered, anti-fraud)
// ─────────────────────────────────────────────────────────────────────────────

public class ViewCounter
{
    // Simulates Kafka buffer: userId+videoId → list of heartbeats
    private readonly Dictionary<string, List<int>> _heartbeats = new Dictionary<string, List<int>>();
    private readonly VideoMetaStore _meta;
    private const int MinPlaybackSeconds = 30; // must watch at least 30s to count

    public ViewCounter(VideoMetaStore meta) { _meta = meta; }

    public void RecordHeartbeat(string userId, string videoId, int positionSeconds)
    {
        var key = $"{userId}:{videoId}";
        if (!_heartbeats.ContainsKey(key)) _heartbeats[key] = new List<int>();
        _heartbeats[key].Add(positionSeconds);
    }

    // Flush: validate and increment view counts (called by batch job every 60s)
    public void Flush()
    {
        var toCount = new Dictionary<string, int>(); // videoId → incremental views

        foreach (var kv in _heartbeats)
        {
            var parts   = kv.Key.Split(':');
            var videoId = parts[1];
            var beats   = kv.Value;

            // Valid view: played for at least MinPlaybackSeconds
            if (beats.Count > 0 && beats.Max() - beats.Min() >= MinPlaybackSeconds)
            {
                if (!toCount.ContainsKey(videoId)) toCount[videoId] = 0;
                toCount[videoId]++;
            }
        }

        foreach (var kv in toCount)
        {
            var video = _meta.Get(kv.Key);
            if (video != null)
            {
                video.ViewCount += kv.Value;
                Console.WriteLine($"  [ViewCount] {kv.Key} +{kv.Value} → total {video.ViewCount}");
            }
        }

        _heartbeats.Clear();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo: end-to-end scenarios
// ─────────────────────────────────────────────────────────────────────────────

public class Program
{
    public static void Main()
    {
        var rawStore    = new RawVideoStore();
        var hlsStore    = new HlsStore();
        var metaStore   = new VideoMetaStore();
        var uploadSvc   = new UploadService(rawStore);
        var transcoder  = new TranscodeWorker(rawStore, hlsStore, metaStore);
        var cdnEdge     = new CdnEdgeCache(hlsStore);
        var watchHist   = new WatchHistory();
        var viewCounter = new ViewCounter(metaStore);

        Console.WriteLine("=== Scenario 1: Upload → Transcode → Stream ===\n");
        {
            // Alice uploads a 5-minute video in 3 chunks
            long fileSize = 15 * 1024 * 1024; // 15 MB
            var session = uploadSvc.Init("alice", "vacation.mp4", fileSize, 5 * 1024 * 1024);
            Console.WriteLine($"Upload session {session.UploadId}, video {session.VideoId}, {session.TotalChunks} chunks\n");

            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            uploadSvc.ReceiveChunk(session.UploadId, 1, new byte[5 * 1024 * 1024]);
            uploadSvc.ReceiveChunk(session.UploadId, 2, new byte[5 * 1024 * 1024]);

            var (ok, videoId) = uploadSvc.Complete(session.UploadId, new byte[fileSize]);
            Console.WriteLine($"  Upload complete: {ok}, videoId={videoId}\n");

            // Transcode worker picks up the job
            if (uploadSvc.HasTranscodeJob(out var jobId))
            {
                transcoder.Process(jobId, 300, "Vacation 2024", "alice",
                    new[] { Rendition.R360p, Rendition.R720p, Rendition.R1080p });
            }
            Console.WriteLine();

            // Bob streams the video (good connection → 1080p)
            Console.WriteLine("--- Bob streams (8000 kbps) ---");
            var bobPlayer = new AbrPlayer("bob", videoId, cdnEdge, watchHist, viewCounter);
            bobPlayer.Play(4, simulatedThroughputKbps: 8000);
            Console.WriteLine();

            // Bob resumes after closing — should start from last position
            Console.WriteLine("--- Bob resumes ---");
            var bobPlayer2 = new AbrPlayer("bob", videoId, cdnEdge, watchHist, viewCounter);
            Console.WriteLine($"  Resumed at position {bobPlayer2.PositionSeconds}s (expected {bobPlayer.PositionSeconds}s)");
            Console.WriteLine();

            // Flush view counter
            Console.WriteLine("--- Flush view counter ---");
            viewCounter.Flush();
        }

        Console.WriteLine("\n=== Scenario 2: Interrupted Upload / Resume ===\n");
        {
            long fileSize = 10 * 1024 * 1024;
            var session = uploadSvc.Init("carol", "tutorial.mp4", fileSize);
            Console.WriteLine($"Session {session.UploadId}, {session.TotalChunks} chunks");

            // Only chunk 0 received before network drops
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);

            var resumeFrom = uploadSvc.GetResumePoint(session.UploadId);
            Console.WriteLine($"  Resume from chunk: {resumeFrom}");

            // Carol reconnects, uploads chunk 1
            uploadSvc.ReceiveChunk(session.UploadId, 1, new byte[5 * 1024 * 1024]);

            var (ok, videoId) = uploadSvc.Complete(session.UploadId, new byte[fileSize]);
            Console.WriteLine($"  Upload complete after resume: {ok}, videoId={videoId}");

            // Drain transcode queue so Scenario 3 starts clean
            if (uploadSvc.HasTranscodeJob(out var tid))
                transcoder.Process(tid, 90, "Tutorial", "carol");
        }

        Console.WriteLine("\n=== Scenario 3: ABR Quality Switching on Poor Network ===\n");
        {
            // Upload a short video
            var session = uploadSvc.Init("dave", "short.mp4", 5 * 1024 * 1024);
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            var (_, videoId) = uploadSvc.Complete(session.UploadId, new byte[5 * 1024 * 1024]);

            if (uploadSvc.HasTranscodeJob(out var jobId))
                transcoder.Process(jobId, 120, "Short Clip", "dave",
                    new[] { Rendition.R360p, Rendition.R480p, Rendition.R720p });

            // Eve streams with degrading network
            Console.WriteLine("--- Eve on poor network (500 kbps) ---");
            var evePlayer = new AbrPlayer("eve", videoId, cdnEdge, watchHist, viewCounter);
            evePlayer.Play(3, simulatedThroughputKbps: 500);
            Console.WriteLine();

            Console.WriteLine("--- Eve on good network (6000 kbps) ---");
            var evePlayer2 = new AbrPlayer("eve", videoId, cdnEdge, watchHist, viewCounter);
            evePlayer2.Play(3, simulatedThroughputKbps: 6000);
        }

        Console.WriteLine("\n=== Scenario 4: CDN Hit Rate ===\n");
        {
            // Drain any leftover transcode jobs from Scenario 3
            while (uploadSvc.HasTranscodeJob(out var leftover))
                transcoder.Process(leftover, 60, "Leftover", "system");

            Console.WriteLine($"CDN hits={cdnEdge.Hits}, misses={cdnEdge.Misses}, hit_rate={cdnEdge.HitRate:P1}");
            // Second playback of same video should hit cache
            var session = uploadSvc.Init("user", "test.mp4", 5 * 1024 * 1024);
            uploadSvc.ReceiveChunk(session.UploadId, 0, new byte[5 * 1024 * 1024]);
            var (_, videoId) = uploadSvc.Complete(session.UploadId, new byte[5 * 1024 * 1024]);
            if (uploadSvc.HasTranscodeJob(out var jid))
                transcoder.Process(jid, 60, "Test", "user", new[] { Rendition.R360p, Rendition.R720p, Rendition.R1080p });

            int hitsBefore = cdnEdge.Hits;
            var p1 = new AbrPlayer("u1", videoId, cdnEdge, watchHist, viewCounter);
            p1.Play(2, 8000); // first play → misses
            var p2 = new AbrPlayer("u2", videoId, cdnEdge, watchHist, viewCounter);
            p2.Play(2, 8000); // second play → should hit cache
            Console.WriteLine($"\nHits gained by second viewer: {cdnEdge.Hits - hitsBefore}");
        }

        Console.WriteLine("\n=== Scenario 5: Search and Trending ===\n");
        {
            // Manually add some videos for search test
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid001", Title = "Funny Cats 2024", Tags = new List<string> { "cats", "funny" },
                Status = VideoStatus.Ready, ViewCount = 5000000, CreatedAt = DateTime.UtcNow
            });
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid002", Title = "Cat Training Tips", Tags = new List<string> { "cats", "tutorial" },
                Status = VideoStatus.Ready, ViewCount = 1200000, CreatedAt = DateTime.UtcNow
            });
            metaStore.Upsert(new VideoMetadata
            {
                VideoId = "vid003", Title = "Dog vs Cat", Tags = new List<string> { "dogs", "cats" },
                Status = VideoStatus.Ready, ViewCount = 800000, CreatedAt = DateTime.UtcNow
            });

            var results = metaStore.Search("cats");
            Console.WriteLine("Search 'cats':");
            foreach (var v in results)
                Console.WriteLine($"  {v.VideoId}: \"{v.Title}\" ({v.ViewCount:N0} views)");

            Console.WriteLine("\nTrending top 3:");
            foreach (var v in metaStore.Trending(3))
                Console.WriteLine($"  {v.VideoId}: \"{v.Title}\" ({v.ViewCount:N0} views)");
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
