// Enums — the two domain types shared across the whole streaming pipeline.
//
// THE BIG IDEA:
// VideoStatus and Rendition are the shared vocabulary every component reads/writes. Keeping them
// in one file means adding a quality tier or lifecycle state is a single-file change the compiler
// then propagates everywhere.

// One-way lifecycle of a video. Only Ready is served; everything else => API returns
// 404 / "still processing". Ready is flipped last (after segments + manifest exist), so a
// player can never load a video whose content isn't fully in place.
//
//   Uploading -> Transcoding -> Ready -> Deleted   (never moves backward)
//     Deleted is a soft-delete tombstone; bytes are GC'd later (e.g. 30-day legal retention).
public enum VideoStatus
{
    Uploading,    // raw bytes arriving; no playable content yet
    Transcoding,  // encoding all renditions; manifest not written yet
    Ready,        // all segments + manifest live on the CDN; safe to stream
    Deleted       // soft-delete tombstone; object-storage bytes not yet purged
}

// Quality tiers, each mapped to a target bitrate in BitrateTable.Kbps. The ladder ~doubles each
// step (400/800/2500/5000/16000) so AbrPlayer makes clean up/down switches with no ambiguous tier.
// "R" prefix because C# identifiers can't start with a digit (360p is a compile error).
//
// AbrPlayer picks the highest tier with bitrate < throughput x 0.8 (20% headroom):
//   throughput 8000 -> budget 6400 -> R1080p (5000 fits)
//   throughput 3000 -> budget 2400 -> R480p  (R720p 2500 too big, R480p 800 fits)
//   throughput 500  -> budget 400  -> R360p  (nothing fits -> floor)
public enum Rendition
{
    R360p,   //   400 Kbps — mobile data / weak signal baseline
    R480p,   //   800 Kbps — standard mobile or congested WiFi
    R720p,   //  2500 Kbps — good home WiFi, HD
    R1080p,  //  5000 Kbps — fast broadband, full HD
    R4K      // 16000 Kbps — gigabit / smart TV / premium
}
