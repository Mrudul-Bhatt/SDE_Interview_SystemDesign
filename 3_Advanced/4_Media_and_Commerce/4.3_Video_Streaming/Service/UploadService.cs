// UploadService — the server side of chunked, resumable uploads.
//
// THE BIG IDEA:
// Uploading a big file like mailing a manuscript chapter by chapter. The client splits the
// file into fixed-size chunks and sends them one at a time; this service tracks which chunks
// arrived (in UploadSession). If the connection drops, the client asks where to resume and
// continues from the first missing chunk — no need to re-send what already landed.
//
// Three guarantees:
//   Resumable  — GetResumePoint tells the client the first missing chunk after a drop.
//   Idempotent — re-sending a chunk is a no-op (UploadSession's HashSet dedupes).
//   Decoupled  — Complete() doesn't transcode; it stores the bytes and enqueues the videoId.
//                TranscodeWorker drains the queue separately (a Kafka topic in production),
//                so the upload and transcode tiers scale independently.
//
// HOW IT BEHAVES AT RUNTIME (Carol uploads tutorial.mp4, 10 MB, 2 chunks, Scenario 2):
//
//   Operation                          | State after
//   -----------------------------------|--------------------------------------
//   Init("carol","tutorial.mp4",10MB)  | _sessions={ uploadId -> session(2 chunks) }
//   ReceiveChunk(0)                    | session.ReceivedChunks={0}  IsComplete=false
//   [connection drops] GetResumePoint  | -> 1   (first missing chunk)
//   ReceiveChunk(1)                    | ReceivedChunks={0,1}  IsComplete=true
//   Complete(uploadId, fullData)       | RawVideoStore written; _transcodeQueue=[videoId]
//   HasTranscodeJob(out id)            | -> true, id=videoId; _transcodeQueue=[]

using System;
using System.Collections.Generic;

public class UploadService
{
    private readonly RawVideoStore _raw;

    // Active sessions keyed by UploadId (the client only knows UploadId during upload).
    private readonly Dictionary<string, UploadSession> _sessions = [];

    // Stand-in for a Kafka topic: Complete() publishes videoIds, TranscodeWorker consumes.
    private readonly Queue<string> _transcodeQueue = [];

    public UploadService(RawVideoStore raw) { _raw = raw; }

    // Creates the session and pre-mints both IDs before any bytes arrive: VideoId is the
    // permanent public URL key; UploadId is the throwaway session token. 5 MB chunks balance
    // round-trips (smaller = more requests) against retry cost (bigger = re-send more on a drop).
    public UploadSession Init(string uploaderId, string filename, long totalSize, int chunkSize = 5 * 1024 * 1024)
    {
        var videoId = Guid.NewGuid().ToString("N")[..12];
        var uploadId = Guid.NewGuid().ToString("N")[..8];
        var session = new UploadSession
        {
            UploadId = uploadId,
            VideoId = videoId,
            UploaderId = uploaderId,
            OriginalName = filename,
            TotalSize = totalSize,
            // Ceiling so the last partial chunk gets its own index (11 MB / 5 MB = 3 chunks).
            TotalChunks = (int)Math.Ceiling((double)totalSize / chunkSize)
        };
        _sessions[uploadId] = session;
        return session;
    }

    // Records one chunk. Returns false on unknown session or empty data (the latter stands in
    // for a failed checksum). Adding an already-received index is a harmless no-op, so a client
    // retry after a timeout still returns true and moves on.
    public bool ReceiveChunk(string uploadId, int chunkIndex, byte[] data)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return false;
        if (data == null || data.Length == 0) return false;

        session.ReceivedChunks.Add(chunkIndex);
        Console.WriteLine($"  [Upload] {uploadId} chunk {chunkIndex}/{session.TotalChunks - 1} received");
        return true;
    }

    // Finalises the upload. Re-checks IsComplete server-side (don't trust the client) so a
    // missing chunk is caught here instead of assembling a file with a silent gap. On success,
    // stores the bytes and enqueues the videoId for transcoding.
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

    // Resume helper: the first chunk index not yet received, or TotalChunks if all are in.
    // O(chunks), but only called on reconnect — not on the hot per-chunk path.
    public int GetResumePoint(string uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return -1;
        for (int i = 0; i < session.TotalChunks; i++)
            if (!session.ReceivedChunks.Contains(i)) return i;
        return session.TotalChunks;
    }

    // Pops the next videoId waiting to be transcoded. Stands in for a Kafka consumer poll.
    public bool HasTranscodeJob(out string videoId)
    {
        if (_transcodeQueue.Count > 0) { videoId = _transcodeQueue.Dequeue(); return true; }
        videoId = null;
        return false;
    }
}
