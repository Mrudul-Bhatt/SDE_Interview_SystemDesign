// RawVideoStore — the lossless master archive of every original upload.
//
// THE BIG IDEA:
// Like a film studio's vault: the master tape is kept forever even after DVDs and streaming
// versions are made from it. UploadService writes the assembled bytes here once; everything
// viewers watch (the HLS segments) is derived from this original. Viewers never touch it.
//   WRITTEN BY: UploadService.Complete()   (once per video)
//   READ BY:    TranscodeWorker.Process()  (once per transcode — now or years later)
//
// WHY KEEP ORIGINALS FOREVER: re-transcoding needs them — new codec (H.264→H.265), a new
// quality tier (4K added years later), or an encoder bug fix. Re-encoding from the lossy HLS
// segments instead would compound quality loss (photocopy of a photocopy). Cold storage is
// near-free (~$0.0004/GB-month on Glacier), so production keeps them on an S3 lifecycle:
// S3 Standard for ~30 days (fast, while transcoding may retry) → Glacier after (83% cheaper).
//
// WHY SEPARATE FROM HlsStore: one giant blob per video, read rarely → cheap blob storage;
// vs many tiny segments read constantly by all viewers → CDN-fronted. Different access
// patterns, optimised independently.
//
// HOW IT BEHAVES AT RUNTIME (Alice uploads vacation.mp4, 15 MB, Scenario 1):
//
//   Operation                       | _store contents after
//   --------------------------------|--------------------------------------
//   (start)                         | {}
//   UploadService.Init()            | {}   (chunks live in UploadSession, not here)
//   ReceiveChunk(0), (1), (2)       | {}   (still no bytes here)
//   UploadService.Complete()        | { vacation -> <15 MB bytes> }   (the one write)
//   TranscodeWorker.Process()       | { vacation -> <15 MB bytes> }   (reads only, unchanged)
//
//   The entry is never deleted - the original is kept for future re-transcodes.

using System.Collections.Generic;

public class RawVideoStore
{
    // videoId → original bytes. In production: videoId → S3 object key "raw/{videoId}/original.mp4".
    private readonly Dictionary<string, byte[]> _store = [];

    // Idempotent write: re-running Complete() with the same videoId safely overwrites.
    // Production: S3.PutObject.
    public void Store(string videoId, byte[] data) => _store[videoId] = data;

    // TranscodeWorker's pre-flight guard — bails out rather than producing corrupt output if
    // the source is missing. Production: S3.HeadObject (existence check, no byte transfer).
    public bool Exists(string videoId) => _store.ContainsKey(videoId);

    // Returns the raw bytes. The demo stubs segment data so doesn't actually call this;
    // production streams these bytes into FFmpeg. Production: S3.GetObject.
    public byte[] Fetch(string videoId) => _store[videoId];
}
