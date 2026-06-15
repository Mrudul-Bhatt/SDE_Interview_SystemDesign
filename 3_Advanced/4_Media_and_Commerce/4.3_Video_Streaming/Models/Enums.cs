// Enums — the two domain types shared across the entire streaming pipeline.
//
// THE BIG IDEA:
// VideoStatus and Rendition are the shared vocabulary that connects every component.
// UploadService, TranscodeWorker, HlsStore, CdnEdgeCache, and AbrPlayer all read
// or write these values. Centralising them here means adding a new quality tier or
// lifecycle state is a single-file change — the compiler then flags every switch
// and dictionary that needs updating.

// The one-way lifecycle of a video from first byte to content-on-CDN.
//
// STATE MACHINE (transitions are one-way — a video never moves backward):
//   Uploading → Transcoding : UploadService.CompleteUpload() hands off to TranscodeWorker
//   Transcoding → Ready     : TranscodeWorker writes ALL rendition segments + master
//                             manifest to HLS store, THEN atomically flips to Ready.
//                             Atomic ordering matters: if Ready were set before the
//                             manifest is written, a player fetching that URL would get
//                             a 404 on a video that appears playable.
//   Any → Deleted           : user request or DMCA takedown. Soft-delete — the status
//                             is tombstoned but bytes stay in object storage until a
//                             background GC job purges them (30-day retention for legal).
//
// WHY ONLY Ready VIDEOS ARE SERVED:
// VideoMetaStore.Get() returns null for any non-Ready video so the API layer can
// return HTTP 404 / "still processing" before the player even attempts to fetch
// the manifest. A player that hit the manifest URL during Transcoding would get
// a 404 or an incomplete segment list, causing immediate playback failure.
//
// ── RUNTIME SNAPSHOT (lifecycle of video "vid-abc123") ──
//   t=  0s   Status = Uploading    ← user hit Upload, first chunk arriving at UploadService
//   t= 30s   Status = Transcoding  ← last chunk stored; TranscodeWorker.Transcode() fires
//   t=120s   Status = Ready        ← all 5 renditions + master M3U8 written; CDN-cacheable
//   t= ???   Status = Deleted      ← takedown request; bytes GC'd after 30-day window
//
//   VideoMetaStore.Get("vid-abc123"):
//       during Uploading/Transcoding → returns null → API returns 404
//       once Ready                   → returns VideoMetadata → player can stream
//       once Deleted                 → returns null → API returns 404
public enum VideoStatus
{
    Uploading,    // raw bytes arriving at UploadService; no playable content exists yet
    Transcoding,  // ffmpeg workers encoding all renditions in parallel; manifest not yet written
    Ready,        // all segments + master M3U8 written to HLS store and CDN; safe to stream
    Deleted       // soft-delete tombstone; object-storage bytes not yet purged
}

// The discrete quality tiers produced by the transcoder and selected by the ABR player.
// Each value maps to a target bitrate in BitrateTable.Kbps. AbrPlayer measures the
// client's download throughput and picks the highest tier whose bitrate fits within it.
//
// WHY THE "R" PREFIX:
// C# enum values cannot start with a digit — "360p" is a compile error. The "R"
// prefix (Resolution) keeps them valid identifiers while remaining immediately
// readable: R720p = "the 720p rendition."
//
// WHY THESE FIVE TIERS (and why the gaps are ~2×):
// The ladder is spaced so each step roughly doubles the bitrate of the one below:
// 400 → 800 → 2500 → 5000 → 16000 Kbps. That spacing lets the player make clean
// up/down switches — no ambiguous mid-tier. Real platforms (YouTube, Netflix) add
// 240p, 144p, and audio-only for extremely poor connections; omitted here for brevity.
//
// ── RUNTIME SNAPSHOT (AbrPlayer.PickRendition with varying bandwidth) ──
//   Rendition.R360p  →   400 Kbps  (weak mobile data / rural signal)
//   Rendition.R480p  →   800 Kbps  (standard mobile or congested WiFi)
//   Rendition.R720p  →  2500 Kbps  (good home WiFi — HD quality)
//   Rendition.R1080p →  5000 Kbps  (fast broadband — full HD)
//   Rendition.R4K    → 16000 Kbps  (gigabit / smart TV / premium stream)
//
//   PickRendition(bandwidth=3000 Kbps):
//       R4K    needs 16000 → over budget, skip
//       R1080p needs  5000 → over budget, skip
//       R720p  needs  2500 → 2500 ≤ 3000 ✓ → best fit → return R720p
//
//   PickRendition(bandwidth= 600 Kbps) → R360p   (only tier under 600)
//   PickRendition(bandwidth=20000 Kbps) → R4K    (all tiers fit; return highest)
public enum Rendition
{
    R360p,   //   400 Kbps — mobile data / weak signal baseline
    R480p,   //   800 Kbps — standard mobile or congested WiFi
    R720p,   //  2500 Kbps — good home WiFi, HD quality
    R1080p,  //  5000 Kbps — fast broadband, full HD
    R4K      // 16000 Kbps — gigabit / smart TV / premium stream
}
