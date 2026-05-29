// UploadService — chunked, resumable upload flow.
//
// Init creates the session and assigns a videoId BEFORE any bytes arrive — that
// way the client can resume against a stable ID even if the connection drops.
// ReceiveChunk is idempotent (HashSet add), so retries don't corrupt anything.
// Complete only succeeds when every chunk index has been seen; on success the
// raw bytes are stored and a transcode job is queued.
//
// The transcode queue here stands in for Kafka; in production this would
// publish a durable message so transcoder workers can be auto-scaled
// independently of the upload tier.

using System;
using System.Collections.Generic;

public class UploadService
{
    private readonly RawVideoStore _raw;
    private readonly Dictionary<string, UploadSession> _sessions = new Dictionary<string, UploadSession>();
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

    // Resume: return index of next missing chunk so client knows where to restart
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
