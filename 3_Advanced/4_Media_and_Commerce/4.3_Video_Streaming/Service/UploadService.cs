// UploadService — orchestrates the full lifecycle of a chunked, resumable upload.
//
// THE BIG IDEA:
// Think of uploading a large video like mailing a thick manuscript chapter by chapter.
// The client cuts the file into fixed-size envelopes (chunks), mails them one by one,
// and gets a receipt for each one. If the postal service drops an envelope, the client
// only re-mails that one envelope — not the whole manuscript. The server keeps a
// checklist (UploadSession.ReceivedChunks) of which envelopes arrived. When the
// checklist is complete, the server assembles the manuscript and hands it to the
// print shop (TranscodeWorker) for formatting into multiple reading sizes (renditions).
//
// THE THREE GUARANTEES THIS SERVICE PROVIDES:
//
//   1. RESUMABILITY — if the connection drops mid-upload, the client calls GetResumePoint
//      and continues from the first missing chunk. No data needs to be re-sent from scratch.
//
//   2. IDEMPOTENCY — a client can safely retry any chunk any number of times. Receiving the
//      same chunk twice is a no-op (UploadSession's HashSet deduplicates). This makes the
//      whole flow tolerant of network flakiness without complex server-side dedup logic.
//
//   3. DECOUPLING FROM TRANSCODING — Complete() does NOT transcode the video. It stores
//      the raw bytes and enqueues the videoId. TranscodeWorker picks it up independently,
//      so the upload tier and transcode tier can be scaled and deployed separately.
//      In production _transcodeQueue is a Kafka topic; here it's an in-memory Queue.
//
// THE FOUR-STEP CLIENT FLOW:
//   [1] Init(uploaderId, filename, totalSize)
//         → server returns UploadSession with UploadId + VideoId
//   [2] ReceiveChunk(uploadId, chunkIndex=0, data)  → true
//       ReceiveChunk(uploadId, chunkIndex=1, data)  → true
//       ReceiveChunk(uploadId, chunkIndex=2, data)  → true
//         → ReceivedChunks grows; IsComplete flips true at last chunk
//   [3] Complete(uploadId, fullData)
//         → assembles raw file, enqueues videoId for TranscodeWorker
//   [4] HasTranscodeJob() → TranscodeWorker.Process(videoId)
//         → HLS segments written, VideoMetadata.Status = Ready
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — internal state across Scenario 1 (Program.cs):
//
//   ┌─ After Init("alice", "vacation.mp4", 15 MB) ──────────────────────────────────────
//   │  _sessions       = { "a1b2c3" → UploadSession { UploadId="a1b2c3",
//   │                                                  VideoId="x9y8z7w6v5u4",
//   │                                                  TotalChunks=3,
//   │                                                  ReceivedChunks={},
//   │                                                  IsComplete=false } }
//   │  _transcodeQueue = []
//   │  RawVideoStore   = {}
//   └───────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After ReceiveChunk(0), ReceiveChunk(1), ReceiveChunk(2) ─────────────────────────
//   │  ReceivedChunks = { 0, 1, 2 }   IsComplete = true
//   │  _transcodeQueue = []            ← still empty; Complete() not called yet
//   │  RawVideoStore   = {}            ← still empty
//   └───────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After Complete("a1b2c3", fullData) ──────────────────────────────────────────────
//   │  _sessions["a1b2c3"] still present (kept for re-check if client retries Complete)
//   │  _transcodeQueue = ["x9y8z7w6v5u4"]   ← videoId waiting for TranscodeWorker
//   │  RawVideoStore["x9y8z7w6v5u4"] = <15 MB assembled bytes>
//   └───────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ After HasTranscodeJob() + TranscodeWorker.Process() ─────────────────────────────
//   │  _transcodeQueue = []                ← dequeued; worker now owns the job
//   │  HlsStore["x9y8z7w6v5u4"]           = { 360p/480p/720p/1080p/4K segments }
//   │  VideoMetaStore["x9y8z7w6v5u4"]     = { Status=Ready, ManifestUrl="hls/x9y8z7w6v5u4/manifest.m3u8" }
//   │  (AbrPlayer can now stream the video via CdnEdgeCache → HlsStore)
//   └───────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

public class UploadService
{
    private readonly RawVideoStore _raw;

    // In-flight sessions keyed by UploadId (not VideoId — the client only knows
    // UploadId during the upload phase). O(1) lookup on every chunk receive.
    // Sessions are never explicitly expired in this demo; production adds a TTL
    // (e.g., 24 hours) to reclaim memory for abandoned uploads.
    private readonly Dictionary<string, UploadSession> _sessions = new Dictionary<string, UploadSession>();

    // Stands in for a Kafka topic in production. Complete() publishes here; a pool
    // of TranscodeWorkers subscribes. Decoupling the two lets each tier auto-scale
    // independently: a spike in uploads doesn't require a spike in transcode capacity
    // — the queue absorbs the burst and workers drain it at their own pace.
    private readonly Queue<string> _transcodeQueue = new Queue<string>();

    public UploadService(RawVideoStore raw) { _raw = raw; }

    // ── STEP 1: Init ─────────────────────────────────────────────────────────────────
    //
    // Creates the upload session and returns it to the client, who must store the
    // UploadId for all subsequent chunk requests. The VideoId is also returned so
    // the client has a stable public URL immediately — even before any bytes arrive.
    //
    // WHY PRE-MINT BOTH IDs BEFORE ANY DATA ARRIVES:
    // VideoId is the permanent public key; UploadId is the ephemeral session token.
    // Keeping them separate means the upload token can expire or rotate (e.g., after
    // 24 hours of inactivity) without changing the video's public URL. The client
    // can also immediately create a "processing" page at /video/{VideoId} and show
    // a spinner rather than waiting for transcoding to finish.
    //
    // WHY 5 MB DEFAULT CHUNK SIZE:
    // This is the sweet spot between two failure modes:
    //   too small (< 1 MB) → hundreds of round-trips for a 100 MB file; HTTP overhead
    //                         per chunk adds up; progress tracking is noisy
    //   too large (> 20 MB) → a single retry after packet loss re-sends a lot of data
    //                         that the server already received; worse on mobile
    // In production this is a config knob, often 8–16 MB on fast pipes.
    //
    // WHY Math.Ceiling FOR TotalChunks:
    // The last chunk may be smaller than chunkSize. Ceiling ensures it gets its own
    // index. Example: 11 MB file, 5 MB chunks → 11/5 = 2.2 → ceil → 3 chunks
    // (chunks 0 and 1 are 5 MB each, chunk 2 is 1 MB). Floor would give 2 and the
    // last megabyte would never be tracked.
    //
    // ── RUNTIME SNAPSHOT ──
    //   Init("alice", "vacation.mp4", 15_728_640 bytes, chunkSize=5_242_880)
    //     videoId  = "x9y8z7w6v5u4"   (first 12 chars of a trimmed Guid)
    //     uploadId = "a1b2c3d4"        (first 8 chars of a different Guid)
    //     TotalChunks = ceil(15728640 / 5242880) = ceil(3.0) = 3
    //     ReceivedChunks = {}   IsComplete = false
    //     → session stored in _sessions["a1b2c3d4"]
    //     → client receives UploadSession and stores uploadId + videoId locally
    public UploadSession Init(string uploaderId, string filename, long totalSize, int chunkSize = 5 * 1024 * 1024)
    {
        var videoId  = Guid.NewGuid().ToString("N")[..12];
        var uploadId = Guid.NewGuid().ToString("N")[..8];

        var session = new UploadSession
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

    // ── STEP 2: ReceiveChunk ──────────────────────────────────────────────────────────
    //
    // Called once per chunk, in any order, any number of times safely (idempotent).
    //
    // WHY HASHSET FOR ReceivedChunks (not a list or counter):
    // A HashSet<int> gives O(1) Add and O(1) Contains. Add on a duplicate is a no-op
    // that returns false — no extra conditional needed. A counter would lose the
    // ability to know WHICH chunks arrived (needed by GetResumePoint), and a list
    // would require a linear Contains check on every chunk receive.
    //
    // Returns false on two failure modes:
    //   - Unknown uploadId  (session expired or never existed) → client must re-Init
    //   - Null/empty data   (simulates a failed checksum / corrupted chunk)
    //     In production this is an MD5/SHA-256 sent alongside the chunk; if the hash
    //     doesn't match the server's computed hash of the received bytes, return false
    //     so the client retries that chunk.
    //
    // ── RUNTIME SNAPSHOT — happy path ──
    //   ReceiveChunk("a1b2c3d4", 0, <5 MB>) → ReceivedChunks={0},  IsComplete=false
    //   ReceiveChunk("a1b2c3d4", 1, <5 MB>) → ReceivedChunks={0,1},IsComplete=false
    //   ReceiveChunk("a1b2c3d4", 2, <5 MB>) → ReceivedChunks={0,1,2},IsComplete=true
    //
    // ── RUNTIME SNAPSHOT — idempotency (client retries chunk 1) ──
    //   ReceiveChunk("a1b2c3d4", 1, <5 MB>) → HashSet.Add(1) is a no-op
    //                                          ReceivedChunks={0,1,2}  IsComplete=true
    //                                          return true  ← client treats as ACK, moves on
    //
    // ── RUNTIME SNAPSHOT — failure (unknown session) ──
    //   ReceiveChunk("zzzzzzzz", 0, <5 MB>) → _sessions.TryGetValue → false
    //                                          return false  ← client must re-Init
    public bool ReceiveChunk(string uploadId, int chunkIndex, byte[] data)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return false;
        if (data == null || data.Length == 0)                  return false;

        session.ReceivedChunks.Add(chunkIndex); // idempotent: re-adding same index is a no-op
        Console.WriteLine($"  [Upload] {uploadId} chunk {chunkIndex}/{session.TotalChunks - 1} received");
        return true;
    }

    // ── STEP 3: Complete ──────────────────────────────────────────────────────────────
    //
    // Called after the client believes all chunks are in. Assembles the raw file,
    // stores it, and enqueues the videoId for TranscodeWorker.
    //
    // WHY CHECK IsComplete INSTEAD OF TRUSTING THE CLIENT:
    // The client may have had a retry in-flight when it decided to call Complete. If
    // chunk N's retry never reached the server, IsComplete is false and we reject —
    // forcing the client to call GetResumePoint, discover the missing chunk, and
    // re-send it. Without this check, we'd assemble a file with a silent gap, and
    // the transcoder would produce corrupted segments or crash on a malformed input.
    //
    // WHY Complete TAKES fullData (not assembles from chunks):
    // This demo simplifies chunk assembly — in production the server reassembles
    // from the chunks it received (which are stored server-side, e.g., in S3 as
    // multipart upload parts). The client sends fullData here only to give the
    // RawVideoStore something real to hold; the ReceivedChunks check is the real guard.
    //
    // Returns (ok=true, videoId) on success so the caller has the videoId without
    // needing to remember what session they started with.
    //
    // ── RUNTIME SNAPSHOT — success ──
    //   Complete("a1b2c3d4", <15 MB>)
    //     session.IsComplete = true (ReceivedChunks={0,1,2}, TotalChunks=3)
    //     RawVideoStore.Store("x9y8z7w6v5u4", <15 MB>)
    //     _transcodeQueue.Enqueue("x9y8z7w6v5u4")
    //     → returns (true, "x9y8z7w6v5u4")
    //
    // ── RUNTIME SNAPSHOT — failure (chunk 1 never arrived) ──
    //   Complete("a1b2c3d4", <15 MB>)
    //     session.ReceivedChunks={0,2}  TotalChunks=3  IsComplete=false
    //     → prints "INCOMPLETE — received 2/3 chunks"
    //     → returns (false, null)
    //     Client calls GetResumePoint("a1b2c3d4") → 1 → re-sends chunk 1 → Complete again
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

    // ── RESUME HELPER: GetResumePoint ────────────────────────────────────────────────
    //
    // Tells the client exactly which chunk index to restart from after a disconnect.
    // Scans 0..TotalChunks-1 and returns the FIRST index NOT in ReceivedChunks.
    //
    // WHY SCAN FROM ZERO (not from a stored cursor):
    // Chunks can arrive out of order. A cursor pointing to "last received + 1" would
    // miss gaps earlier in the sequence. The linear scan is O(TotalChunks) but
    // GetResumePoint is called only on reconnect (rare) — not on every chunk —
    // so the cost is acceptable.
    //
    // Return values:
    //   0..TotalChunks-1  → the first missing chunk index; client re-sends from here
    //   TotalChunks       → all chunks received; client should call Complete instead
    //   -1                → unknown uploadId; session expired; client must re-Init
    //
    // ── RUNTIME SNAPSHOT — mid-upload disconnect ──
    //   Client sent chunks 0 and 2 before disconnect; chunk 1 was dropped by the network.
    //   ReceivedChunks = { 0, 2 }   TotalChunks = 3
    //
    //   GetResumePoint("a1b2c3d4"):
    //     i=0 → Contains(0)? Yes → continue
    //     i=1 → Contains(1)? No  → return 1
    //
    //   Client resumes by sending chunk 1 only:
    //     ReceiveChunk("a1b2c3d4", 1, <5 MB>) → ReceivedChunks={0,1,2}  IsComplete=true
    //     Complete("a1b2c3d4", fullData)       → success
    //
    // ── RUNTIME SNAPSHOT — all chunks present ──
    //   ReceivedChunks = { 0, 1, 2 }   TotalChunks = 3
    //   GetResumePoint("a1b2c3d4"):
    //     i=0,1,2 → all present → loop ends → return 3 (= TotalChunks)
    //   Client sees return == TotalChunks → calls Complete immediately
    public int GetResumePoint(string uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session)) return -1;
        for (int i = 0; i < session.TotalChunks; i++)
            if (!session.ReceivedChunks.Contains(i)) return i;
        return session.TotalChunks; // all received — client should call Complete
    }

    // ── QUEUE BRIDGE: HasTranscodeJob ────────────────────────────────────────────────
    //
    // Dequeues the next videoId waiting for transcoding. In the demo, Program.cs
    // calls this in a polling loop to simulate a TranscodeWorker consumer.
    //
    // WHY OUT PARAMETER (not a nullable return):
    // The caller needs two pieces of information atomically: "is there a job?" and
    // "what is the videoId if so?" An out parameter avoids a second queue peek call
    // and is the C# convention for Try-pattern methods (TryGetValue, TryParse, etc.).
    //
    // In production this is a Kafka consumer commit:
    //   var record = consumer.Poll(timeout);
    //   if (record != null) Process(record.Value);  // videoId is record.Value
    // The Queue<string> here is structurally identical but without durability or
    // consumer groups — if the process crashes between Dequeue and Process, the job
    // is lost. Kafka's at-least-once delivery would replay it.
    //
    // ── RUNTIME SNAPSHOT ──
    //   Before TranscodeWorker polls:
    //     _transcodeQueue = ["x9y8z7w6v5u4", "p3q4r5s6t7u8"]  (two videos queued)
    //
    //   HasTranscodeJob(out videoId) → videoId="x9y8z7w6v5u4", returns true
    //     _transcodeQueue = ["p3q4r5s6t7u8"]   ← first job dequeued
    //   TranscodeWorker.Process("x9y8z7w6v5u4")
    //
    //   HasTranscodeJob(out videoId) → videoId="p3q4r5s6t7u8", returns true
    //     _transcodeQueue = []
    //   TranscodeWorker.Process("p3q4r5s6t7u8")
    //
    //   HasTranscodeJob(out videoId) → videoId=null, returns false  ← queue empty
    public bool HasTranscodeJob(out string videoId)
    {
        if (_transcodeQueue.Count > 0) { videoId = _transcodeQueue.Dequeue(); return true; }
        videoId = null;
        return false;
    }
}
