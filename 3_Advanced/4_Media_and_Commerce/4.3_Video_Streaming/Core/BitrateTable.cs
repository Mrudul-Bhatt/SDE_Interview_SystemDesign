// BitrateTable — the single source of truth for target encode bitrate per rendition.
//
// THE BIG IDEA:
// Think of BitrateTable like a menu that lists the "size" of each meal option.
// Both the kitchen (TranscodeWorker) and the waiter (AbrPlayer) read from the
// same menu — the kitchen uses it to know how large to make each portion, and
// the waiter uses it to know which portion a customer's appetite (bandwidth)
// can handle. If they used different menus, the customer would either go hungry
// (buffer starvation) or waste food (under-utilised bandwidth).
//
// Every component that touches bitrate reads this table:
//   TranscodeWorker.Process()   → passes BitrateTable.Kbps[r] to the encoder
//   HlsSegment.BitrateKbps      → set from BitrateTable at transcode time, cached on the segment
//   AbrPlayer.ChooseQuality()   → compares BitrateTable.Kbps[r] < throughput * 0.8
//   AbrPlayer.Play()            → computes downloadTimeSec = (Kbps[q] / throughput) * 6
//
// WHY THESE SPECIFIC VALUES (400 / 800 / 2500 / 5000 / 16000 Kbps):
// Each tier is ~2× the tier below it. That spacing is deliberate — it means a
// bandwidth change that justifies a quality switch is always large enough to be
// meaningful (not just noise), and the player won't oscillate between adjacent
// tiers on minor throughput fluctuations. The exact values match the H.264/H.265
// industry benchmarks widely used by Netflix, YouTube, and Apple HLS guidelines:
//   360p  →    400 Kbps  (SD, mobile on 3G, ~50 KB/s)
//   480p  →    800 Kbps  (SD+, mobile on LTE, ~100 KB/s)
//   720p  →  2,500 Kbps  (HD, home wifi, ~312 KB/s)
//   1080p →  5,000 Kbps  (Full HD, strong wifi, ~625 KB/s)
//   4K    → 16,000 Kbps  (UHD, gigabit LAN or fast fibre, ~2 MB/s)
//
// WHY A STATIC DICTIONARY (not per-video or per-segment config):
// Bitrate targets are a product decision made once — they determine the CDN
// storage cost per video (more renditions × higher bitrate = more bytes stored)
// and the viewer experience floor. Centralising them here means a single change
// updates both the encoder and the player consistently. A per-video config would
// allow custom targets but adds a DB read on every quality decision — not worth
// it unless the product explicitly needs variable rendition ladders.
//
// WHY AbrPlayer USES 80% OF THROUGHPUT (not 100%):
// The 0.8 headroom in AbrPlayer.ChooseQuality() guards against the fact that
// measured throughput is a trailing average, not a guarantee. Using 100% would
// cause buffer starvation whenever throughput dips even slightly below the bitrate
// of the chosen rendition. 80% means the player only picks R720p (2500 Kbps) when
// throughput is at least 3125 Kbps — giving a 625 Kbps safety margin.
//
// ── RUNTIME SNAPSHOT (AbrPlayer quality decisions at various throughputs) ──
//
//   throughput = 500 Kbps  (weak 3G)
//     R4K    (16000) < 500 * 0.8 = 400?  No  → skip
//     R1080p  (5000) < 400?                No  → skip
//     R720p   (2500) < 400?                No  → skip
//     R480p    (800) < 400?                No  → skip
//     R360p    (400) < 400?                No  → all skip → fallback: R360p
//     Result: R360p (floor)
//
//   throughput = 1200 Kbps  (LTE)
//     R4K,R1080p,R720p — all above 960 → skip
//     R480p  (800) < 960?  Yes → PickedQuality = R480p
//     Result: R480p
//
//   throughput = 3200 Kbps  (strong wifi)
//     R4K,R1080p — above 2560 → skip
//     R720p  (2500) < 2560?  Yes → PickedQuality = R720p
//     Result: R720p
//
//   throughput = 6500 Kbps  (fast cable)
//     R4K  (16000) < 5200?  No  → skip
//     R1080p (5000) < 5200?  Yes → PickedQuality = R1080p
//     Result: R1080p
//
//   throughput = 20000 Kbps  (gigabit fibre)
//     R4K  (16000) < 16000?  Yes → PickedQuality = R4K
//     Result: R4K
//
//   Buffer emergency (bufferSeconds < 5):
//     ChooseQuality() returns R360p immediately, ignoring throughput.
//     WHY: at buffer = 0 the player freezes. Dropping to the smallest rendition
//     (400 Kbps) minimises download time regardless of picture quality — the user
//     sees a small pixellated picture rather than a spinning buffer icon.
//
// ── DOWNLOAD TIME vs SEGMENT LENGTH MATH ──
//   For a 6-second segment:
//     downloadTimeSec = BitrateKbps / throughputKbps * 6
//   If downloadTimeSec < 6  → buffer grows   (player stays ahead of playhead)
//   If downloadTimeSec > 6  → buffer drains  (player can't keep up)
//   If downloadTimeSec >> 6 → eventual stall
//
//   Example — R720p (2500 Kbps) at 3200 Kbps throughput:
//     downloadTimeSec = 2500 / 3200 * 6 = 4.69s  → buffer grows by 1.31s per segment
//
//   Example — R1080p (5000 Kbps) at 3200 Kbps throughput:
//     downloadTimeSec = 5000 / 3200 * 6 = 9.38s  → buffer drains by 3.38s per segment
//     → AbrPlayer correctly skips R1080p at this throughput (5000 > 3200 * 0.8 = 2560)

using System.Collections.Generic;

public static class BitrateTable
{
    public static readonly Dictionary<Rendition, int> Kbps = new Dictionary<Rendition, int>
    {
        { Rendition.R360p,    400 },   // SD    — 3G/weak signal floor
        { Rendition.R480p,    800 },   // SD+   — LTE minimum comfortable
        { Rendition.R720p,   2500 },   // HD    — standard home wifi
        { Rendition.R1080p,  5000 },   // FHD   — strong wifi / fast cable
        { Rendition.R4K,    16000 },   // UHD   — gigabit LAN / fibre ceiling
    };
}
