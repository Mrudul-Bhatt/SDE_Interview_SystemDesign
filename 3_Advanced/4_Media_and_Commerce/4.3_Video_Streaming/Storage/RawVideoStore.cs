// RawVideoStore — the lossless master archive for every video ever uploaded.
//
// THE BIG IDEA:
// When a video upload completes, UploadService writes the assembled bytes here once.
// That's it — this store is then the source of truth for the original, unprocessed file.
// Think of it like a film studio's vault: the master tape is preserved forever even after
// DVDs, Blu-rays, and streaming versions are produced from it. The master is the ONLY
// source that can produce new formats without quality loss. Every HLS rendition viewers
// watch is ultimately derived from this one stored original.
//
// This store has exactly TWO roles in the pipeline:
//
//   WRITTEN BY: UploadService.Complete()   — exactly once per video, ever
//   READ BY:    TranscodeWorker.Process()  — once per transcode job (now or years later)
//
// Viewers NEVER touch this store. All viewer traffic goes through the HLS pipeline:
//   RawVideoStore → TranscodeWorker → HlsStore → CdnEdgeCache → AbrPlayer → viewer
//
// WHY KEEP THE ORIGINAL FOREVER (never delete after transcoding):
// Future re-transcoding is the key reason:
//   - New codec available: H.264 → H.265 saves 40% bandwidth at same quality. You need
//     the original to re-encode; re-encoding from H.264 HLS segments into H.265 introduces
//     generation loss (like photocopying a photocopy — each step degrades the image).
//   - New quality tier added: YouTube added 4K/8K years after the originals were uploaded.
//     All old videos could be re-transcoded to 4K because originals were preserved.
//   - Transcode bug discovered: a color-grading bug in the encoder produces washed-out
//     video for a batch of uploads. Fix the encoder, re-transcode from originals.
// The cost to keep originals in cold storage is negligible: a 100 MB upload in Glacier
// costs ~$0.0004/month — less than $0.05 over 10 years. The cost of losing it is
// permanent quality degradation for every future re-transcode of that video.
//
// WHY S3 STANDARD FOR ~30 DAYS, THEN GLACIER (the lifecycle policy):
// The original is accessed in two distinct phases:
//
//   Phase 1 — Active (~0–30 days):  S3 Standard ($0.023/GB/month, millisecond retrieval)
//     During this window, TranscodeWorker reads the file to produce renditions. If a
//     transcode job fails and retries, or a new rendition is added quickly, fast retrieval
//     matters. S3 Standard's latency fits into the upload → transcode → ready pipeline.
//
//   Phase 2 — Archive (~30 days+): S3 Glacier ($0.004/GB/month, 3-5 hour retrieval)
//     Once all renditions are Ready, the original is dormant — it's only ever retrieved
//     again if someone explicitly orders a re-transcode. Glacier is 83% cheaper and the
//     hours-long retrieval is acceptable for a planned re-encode job. The lifecycle policy
//     moves files automatically; no human needs to remember to do it.
//
// WHY RAW STORE IS SEPARATE FROM HlsStore:
// Two completely different access patterns:
//
//   RawVideoStore — one giant object per video, write-once, read-rarely, never by viewers.
//                   Maps to S3/GCS blob storage (cheap, durable, slow-ish).
//
//   HlsStore      — many small segments per video, write-once, read-constantly by all viewers.
//                   Maps to S3 + CDN edge nodes (fast, globally distributed, cached).
//
// Serving the raw 15 MB file directly to a viewer would mean: no seeking, no ABR quality
// switching, buffering the full file before playback starts, and 15 MB of CDN bandwidth
// per view instead of ~500 KB per 6-second segment. Separating the two stores makes both
// sides independently optimisable.
//
// ──────────────────────────────────────────────────────────────────────────────────────
// RUNTIME SNAPSHOT — RawVideoStore state across all scenarios in Program.cs:
//
//   ┌─ Scenario 1: Alice uploads "vacation.mp4" (15 MB, 3 chunks) ───────────────────
//   │
//   │  After UploadService.Init():        _store = { }          ← Init only creates the
//   │                                                              UploadSession; no bytes
//   │                                                              land here yet
//   │
//   │  After ReceiveChunk(0,1,2):         _store = { }          ← chunks tracked in
//   │                                                              UploadSession.ReceivedChunks;
//   │                                                              RawVideoStore still empty
//   │
//   │  After UploadService.Complete():
//   │    _store = { "abc123ef4512" → <15,728,640 bytes> }       ← ONE write, ever
//   │
//   │  After TranscodeWorker.Process():
//   │    _store = { "abc123ef4512" → <15,728,640 bytes> }       ← UNCHANGED — originals
//   │                                                              are never deleted after
//   │                                                              transcoding completes
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Scenario 2: Carol uploads "tutorial.mp4" (10 MB, resumes after drop) ─────────
//   │
//   │  After Carol's UploadService.Complete():
//   │    _store = {
//   │      "abc123ef4512" → <15,728,640 bytes>,     ← Alice's (from Scenario 1)
//   │      "d7e8f9012345" → <10,485,760 bytes>      ← Carol's (new entry)
//   │    }
//   │
//   │  Note: the interrupted upload (only chunk 0 delivered, then resumed from chunk 1)
//   │  makes NO difference to RawVideoStore. It only sees the final assembled file once
//   │  Complete() succeeds — the chunking is entirely UploadService's concern.
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Caller chain visualised ───────────────────────────────────────────────────────
//   │
//   │  UploadService.Complete(uploadId, fullData)
//   │    └─▶ _raw.Store(videoId, fullData)             ← one write, ever
//   │
//   │  TranscodeWorker.Process(videoId, ...)
//   │    └─▶ _raw.Exists(videoId)                      ← guard: is source available?
//   │         (in production: _raw.Fetch(videoId) returns a stream piped to FFmpeg)
//   │         (in this demo:  Fetch() is not called — segment data is stubbed)
//   │
//   │  AbrPlayer, CdnEdgeCache, ViewCounter            ← never touch RawVideoStore
//   └─────────────────────────────────────────────────────────────────────────────────
//
//   ┌─ Production S3 key structure ───────────────────────────────────────────────────
//   │  raw/{videoId}/original.mp4   ← the key in the S3 bucket
//   │
//   │  Lifecycle policy applied to this prefix:
//   │    0–30 days  → S3 Standard   ($0.023/GB/month, instant retrieval)
//   │    30+ days   → S3 Glacier    ($0.004/GB/month, 3–5 hour retrieval)
//   │    Never      → S3 Delete     (originals are retained indefinitely)
//   └─────────────────────────────────────────────────────────────────────────────────
// ──────────────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

public class RawVideoStore
{
    // videoId → raw bytes of the original upload.
    // In production: videoId → S3 object key; the bytes live in the bucket, not in memory.
    // This in-memory dictionary is the demo stand-in so TranscodeWorker can call Exists()
    // and the pipeline runs end-to-end without a real object storage service.
    private readonly Dictionary<string, byte[]> _store = [];

    // Called exactly once per video by UploadService.Complete() after all chunks are
    // assembled. Overwrites silently if called again with the same videoId — making it
    // safe to retry a Complete() call without corrupting state (idempotent write).
    //
    // Production equivalent: S3.PutObject(bucket, key=$"raw/{videoId}/original.mp4", body=stream)
    public void Store(string videoId, byte[] data) => _store[videoId] = data;

    // Called by TranscodeWorker.Process() as a pre-flight guard. If the raw file isn't
    // here, the transcode job cannot proceed — it logs an error and exits rather than
    // producing corrupted output. Returns in O(1) via dictionary ContainsKey.
    //
    // Production equivalent: S3.HeadObject(key) — cheap metadata-only request that
    // checks existence without transferring any bytes (important for large files).
    public bool Exists(string videoId) => _store.ContainsKey(videoId);

    // Returns the raw bytes for a given videoId. In this demo, TranscodeWorker.Process()
    // uses Exists() as its guard but stubs the actual video data rather than calling Fetch()
    // (because there is no real FFmpeg here). Fetch() is provided for completeness and for
    // any caller that genuinely needs the bytes.
    //
    // Production equivalent: S3.GetObject(key) — returns a stream that FFmpeg reads
    // directly without loading the entire file into memory (crucial for multi-GB videos).
    public byte[] Fetch(string videoId) => _store[videoId];
}
