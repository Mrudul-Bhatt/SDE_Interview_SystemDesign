// UploadService — orchestrates the full lifecycle of a chunked, resumable upload.
//
// THE BIG IDEA:
// This service is the server-side half of the "resumable upload" contract.
// The client's job: cut the file into chunks, send them one by one, retry any that fail.
// This service's job: create a session to track which chunks arrived, assemble the file
// when all chunks are in, and hand the videoId off to the transcode pipeline.
//
// The three key guarantees this service provides:
//
//   1. RESUMABILITY — if the connection drops mid-upload, the client calls GetResumePoint
//      and continues from the first missing chunk. No data needs to be re-sent from scratch.
//
//   2. IDEMPOTENCY — a client can safely retry any chunk any number of times. Receiving the
//      same chunk twice is a no-op (UploadSession's HashSet deduplicates). This makes the
//      whole flow tolerant of network flakiness without server-side dedup logic.
//
//   3. DECOUPLING FROM TRANSCODING — Complete() does NOT transcode the video. It stores
//      the raw bytes and enqueues the videoId. TranscodeWorker picks it up independently,
//      so the upload tier and transcode tier can be scaled and deployed separately.
//      In production _transcodeQueue is a Kafka topic; here it's an in-memory Queue.
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — UploadService internal state across Scenario 1 (Program.cs):
//
//   ┌─ After Init("alice", "vacation.mp4", 15 MB) ───────────────────────────────────
//   │  _sessions       = { "a1b2c3d4" → UploadSession { VideoId="x9y8z7w6v5u4",
//   │                                                    TotalChunks=3,
//   │                                                    ReceivedChunks={} } }
//   │  _transcodeQueue = []
//   │  _raw            = {}   ← RawVideoStore still empty
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After ReceiveChunk(0), ReceiveChunk(1), ReceiveChunk(2) ──────────────────────
//   │  _sessions["a1b2c3d4"].ReceivedChunks = { 0, 1, 2 }
//   │  _sessions["a1b2c3d4"].IsComplete     = true
//   │  _transcodeQueue = []          ← still empty; Complete() not called yet
//   │  _raw            = {}          ← still empty
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After Complete("a1b2c3d4", fullData) ─────────────────────────────────────────
//   │  _sessions["a1b2c3d4"]  still present (kept for potential re-check)
//   │  _transcodeQueue = ["x9y8z7w6v5u4"]   ← videoId waiting for TranscodeWorker
//   │  _raw["x9y8z7w6v5u4"]  = <15 MB>      ← assembled bytes in RawVideoStore
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After HasTranscodeJob() + TranscodeWorker.Process() ──────────────────────────
//   │  _transcodeQueue = []           ← dequeued; worker now owns the job
//   │  HlsStore["x9y8z7w6v5u4"]      = { 360p segments, 720p segments, 1080p segments }
//   │  VideoMetaStore["x9y8z7w6v5u4"] = { Status=Ready, Title="Vacation 2024", ... }
//   │  (AbrPlayer can now stream the video via CdnEdgeCache → HlsStore)
//   └────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

public class UploadService
{
    private readonly RawVideoStore _raw;

    // In-flight sessions keyed by UploadId (not VideoId — the client only knows
    // the UploadId during the upload phase). O(1) lookup on every chunk receive.
    private readonly Dictionary<string, UploadSession> _sessions = new Dictionary<string, UploadSession>();

    // Stands in for a Kafka topic in production. Complete() publishes here; a pool
    // of TranscodeWorkers subscribes. Decoupling the two lets each tier auto-scale
    // independently: spike in uploads doesn't need a spike in transcode capacity
    // — the queue absorbs the burst and workers drain it at their own pace.
    private readonly Queue<string> _transcodeQueue = new Queue<string>();

    public UploadService(RawVideoStore raw) { _raw = raw; }

    // Step 1 of the upload flow. Creates the session and returns it to the client,
    // who must store the UploadId for all subsequent chunk requests. The VideoId is
    // also returned so the client has a stable URL immediately.
    //
    // chunkSize defaults to 5 MB — the sweet spot between:
    //   too small → too many round-trips, too much overhead per chunk
    //   too large → a retry after failure re-sends a lot of already-received data
    // In production this is a config knob, often set higher (8–16 MB) on fast pipes.
    public UploadSession Init(string uploaderId, string filename, long totalSize, int chunkSize = 5 * 1024 * 1024)
    {
        // Pre-mint both IDs before any data arrives. VideoId is the permanent public key;
        // UploadId is the ephemeral session token. Keeping them separate means the upload
        // token can expire/rotate without changing the video's public URL.
        var videoId  = Guid.NewGuid().ToString("N")[..12];
        var uploadId = Guid.NewGuid().ToString("N")[..8];

        var session = new UploadSession
        {
            UploadId     = uploadId,
            VideoId      = videoId,
            UploaderId   = uploaderId,
            OriginalName = filename,
            TotalSize    = totalSize,
            // Math.Ceiling so the last chunk (which may be smaller than chunkSize)
            // still gets its own index. e.g. 15 MB / 5 MB = 3.0 → 3 chunks.
            //                                11 MB / 5 MB = 2.2 → 3 chunks (last = 1 MB).
            TotalChunks  = (int)Math.Ceiling((double)totalSize / chunkSize)
        };
        _sessions[uploadId] = session;
        return session;
    }

    // Step 2 (called once per chunk, in any order, any number of times safely).
    //
    // Returns false on two failure modes:
    //   - Unknown uploadId  (session expired or never existed) → client must re-Init
    //   - Null/empty data   (simulates a failed checksum / corrupted chunk)
    //     In production this checks an MD5/SHA-256 the client sends alongside the chunk.
    //
    // On success, adds chunkIndex to the session's HashSet. If the index was already
    // present (retry), the add is a no-op — the return value is still true so the
    // client treats it as a successful ACK and moves on.
    public bool ReceiveChunk(string uploadId, int chunkIndex, byte[] data)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return false;
        if (data == null || data.Length == 0)                  return false;

        session.ReceivedChunks.Add(chunkIndex); // idempotent: re-adding same index is a no-op
        Console.WriteLine($"  [Upload] {uploadId} chunk {chunkIndex}/{session.TotalChunks - 1} received");
        return true;
    }

    // Step 3 — called after the client believes all chunks are in.
    //
    // WHY we check IsComplete here (not just trust the client):
    // The client may have had a retry in-flight when it decided to call Complete. If chunk
    // N never actually arrived on the server, IsComplete is false and we reject — forcing
    // the client to call GetResumePoint, discover the missing chunk, and re-send it.
    // Without this check, we'd assemble a file with a silent gap in the middle.
    //
    // Returns (ok=true, videoId) on success so the caller has the videoId without
    // needing to remember what session they started with.
    public (bool ok, string videoId) Complete(string uploadId, byte[] fullData)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return (false, null);
        if (!session.IsComplete)
        {
            // Tell the operator exactly how many chunks are missing — useful for debugging.
            Console.WriteLine($"  [Upload] INCOMPLETE — received {session.ReceivedChunks.Count}/{session.TotalChunks} chunks");
            return (false, null);
        }

        // Store the assembled file. In production this is a write to S3/GCS, not memory.
        _raw.Store(session.VideoId, fullData);

        // Publish to the transcode queue. TranscodeWorker will pick this up asynchronously
        // and produce HLS segments at each configured rendition (360p / 720p / 1080p).
        _transcodeQueue.Enqueue(session.VideoId);
        Console.WriteLine($"  [Upload] Video {session.VideoId} assembled → queued for transcoding");
        return (true, session.VideoId);
    }

    // Resume helper — tells the client exactly which chunk to restart from.
    //
    // Scans 0..TotalChunks-1 linearly and returns the first index NOT in ReceivedChunks.
    // This is O(TotalChunks) but called only on reconnect (rare), not on every chunk.
    // Returns TotalChunks when every chunk is in (session is complete — client should
    // call Complete rather than upload more chunks).
    public int GetResumePoint(string uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return -1;
        for (int i = 0; i < session.TotalChunks; i++)
            if (!session.ReceivedChunks.Contains(i)) return i;
        return session.TotalChunks; // all received — client should call Complete
    }

    // Dequeues the next videoId waiting for transcoding. Called by the demo loop in
    // Program.cs to simulate a TranscodeWorker polling its job queue. In production
    // this is a Kafka consumer commit, not a Queue.Dequeue.
    public bool HasTranscodeJob(out string videoId)
    {
        if (_transcodeQueue.Count > 0) { videoId = _transcodeQueue.Dequeue(); return true; }
        videoId = null;
        return false;
    }
}
