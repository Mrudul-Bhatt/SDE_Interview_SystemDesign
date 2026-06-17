// UploadSession — a resumable upload's flight recorder: tracks every chunk received so far.
//
// THE BIG IDEA:
// Uploading a 2 GB video over a mobile network in one HTTP request is a recipe for failure
// — one dropped packet and you restart from zero. Instead, the client cuts the file into
// 5 MB chunks and uploads each one as a separate, retryable request. UploadSession is the
// server-side ledger that records WHICH chunks have landed safely, so if the connection
// drops at chunk 47 of 400, the client resumes at 47 — not 0.
//
// Think of it like a jigsaw puzzle arrival sheet. The sheet records which pieces (chunks)
// have been delivered to the warehouse. Once all pieces are checked off, the puzzle can be
// assembled (Complete). If the courier delivers piece #12 twice (retry), you just tick the
// same box again — no harm done.
//
// WHY VideoId IS MINTED BEFORE ANY DATA ARRIVES:
// The VideoId is assigned the instant the session is created — BEFORE a single byte of
// video has been received. This is intentional and important:
//   - The client gets its future URL (youtube.com/watch?v=<VideoId>) immediately, even
//     while the upload is still in progress.
//   - If the upload takes hours (large files), the bookmark stays valid throughout.
//   - If the upload crashes and resumes, the video ends up at the SAME URL — not a new one.
//   - TranscodeWorker and CDN can start provisioning slots for this VideoId in parallel.
// This is the "pre-mint the ID" pattern: the ID is the promise of what will exist.
//
// WHY ReceivedChunks IS A HASHSET (not a List, array, or running count):
// Three properties fall out of HashSet for free:
//
//   1. IDEMPOTENCY — a client retrying chunk #12 after a timeout calls ReceiveChunk(12)
//      again. HashSet.Add(12) on a set that already contains 12 is a silent no-op.
//      A List would grow to {0,1,2,12,12,...}; a counter would over-count to 5/3 "complete".
//
//   2. OUT-OF-ORDER DELIVERY — chunks may arrive in any sequence ({2,0,1} = {0,1,2}).
//      HashSet treats all orderings identically; a range-scan or sorted-array approach
//      would need extra sorting or gap-detection logic.
//
//   3. O(1) OPERATIONS — IsComplete is just .Count == TotalChunks (no scan).
//      GetResumePoint calls .Contains(i) per index (no sort needed).
//      Both run in constant time regardless of file size.
//
// WHY COMPLETENESS IS Count == TotalChunks (not a range scan):
// We don't iterate 0..TotalChunks-1 and check each. We just compare the set size to the
// expected count. By the pigeonhole principle: if ReceivedChunks holds TotalChunks
// entries and every entry is a valid chunk index in [0, TotalChunks-1], then every index
// must be present exactly once. This collapses a potential O(N) scan into O(1).
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — UploadSession state at each stage (from Scenario 1 in Program.cs):
//
//   Alice uploads "vacation.mp4" (15 MB, 5 MB chunks → 3 chunks total)
//
//   ┌─ Stage 1: right after UploadService.Init("alice", "vacation.mp4", 15 MB) ───────
//   │  UploadId        = "a1b2c3d4"       ← 8-char token client stores to resume
//   │  VideoId         = "x9y8z7w6v5u4"  ← 12-char URL key, minted NOW before any bytes
//   │  UploaderId      = "alice"
//   │  OriginalName    = "vacation.mp4"
//   │  TotalSize       = 15,728,640       ← 15 * 1024 * 1024 bytes
//   │  TotalChunks     = 3               ← Math.Ceiling(15 MB / 5 MB)
//   │  ReceivedChunks  = { }             ← empty, nothing arrived yet
//   │  IsComplete      = false           ← 0 / 3
//   │  LastReceivedChunk = -1            ← sentinel: no chunks yet
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Stage 2: after ReceiveChunk(uploadId, 0, ...) and ReceiveChunk(uploadId, 1, ...) ─
//   │  ReceivedChunks  = { 0, 1 }        ← two of three chunks safely stored
//   │  IsComplete      = false           ← 2 / 3
//   │  LastReceivedChunk = 1
//   │
//   │  [connection drops here]
//   │  Client calls GetResumePoint(uploadId) → server scans {0,1} and returns 2
//   │  Client reconnects and re-sends chunk 1 as a safety measure (retry)
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Stage 3: after ReceiveChunk(1) retry + ReceiveChunk(2) ──────────────────────
//   │  ReceivedChunks  = { 0, 1, 2 }     ← retry of chunk 1 was a silent no-op (HashSet)
//   │  IsComplete      = true            ← 3 / 3 ✓ — ready to assemble
//   │  LastReceivedChunk = 2
//   └────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Stage 4: after Complete(uploadId, fullData) succeeds ─────────────────────────
//   │  (UploadSession is no longer the active player)
//   │  RawVideoStore["x9y8z7w6v5u4"] = <15 MB raw bytes>   ← assembled file stored
//   │  UploadService._transcodeQueue  = ["x9y8z7w6v5u4"]   ← videoId waiting for worker
//   │  TranscodeWorker picks it up → produces HLS segments at 360p / 720p / 1080p
//   └────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;

public class UploadSession
{
    // Short random token the client presents on EVERY chunk request and on resume.
    // Distinct from VideoId — this token is ephemeral (discarded after Complete
    // succeeds), while VideoId is permanent and public. Separating them means a
    // leaked upload token can't be used to mess with an already-live video.
    public string UploadId { get; set; }

    // The video's permanent, public identity — used in URLs, the metadata store,
    // and CDN cache keys. Pre-assigned at session creation so the client has a
    // stable destination before uploading a single byte.
    // (See "WHY VideoId IS MINTED BEFORE ANY DATA ARRIVES" above.)
    public string VideoId { get; set; }

    // Who initiated this upload. Carried through to VideoMetadata so the finished
    // video is attributed to the right creator without an extra lookup.
    public string UploaderId { get; set; }

    // Client's original filename — for display ("you uploaded vacation.mp4") and
    // deduplication checks. Never used as a storage key; VideoId is the key.
    // (Filenames are not unique even for the same user across time.)
    public string OriginalName { get; set; }

    // Total file size in bytes. Drives TotalChunks calculation and progress bars:
    // approx bytes received = (ReceivedChunks.Count / TotalChunks) * TotalSize.
    public long TotalSize { get; set; }

    // How many chunks the file was split into: Math.Ceiling(TotalSize / chunkSize).
    // The client must send exactly these many distinct chunk indices [0..TotalChunks-1]
    // before Complete will accept the upload.
    public int TotalChunks { get; set; }

    // The core state: which chunk indices have safely arrived.
    // HashSet gives idempotency (retries are no-ops), order-independence (any sequence
    // of arrivals works), and O(1) existence checks. See "WHY HASHSET" above.
    public HashSet<int> ReceivedChunks { get; } = [];

    // True only when every one of the TotalChunks distinct indices has arrived.
    // Count == TotalChunks collapses a potential O(N) range-scan into O(1) by
    // relying on the pigeonhole principle. (See "WHY Count == TotalChunks" above.)
    public bool IsComplete => ReceivedChunks.Count == TotalChunks;

    // Highest chunk index seen so far — useful for rough "uploading… chunk 47 of 400"
    // progress estimates. Returns -1 (sentinel) when nothing has arrived yet;
    // -1 can never be a real chunk index since valid indices start at 0.
    public int LastReceivedChunk => ReceivedChunks.Count == 0 ? -1 : ReceivedChunks.Max();
}
