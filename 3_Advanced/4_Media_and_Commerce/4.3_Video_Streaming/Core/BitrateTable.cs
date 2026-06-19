// BitrateTable — the single source of truth for target bitrate per quality tier.
//
// THE BIG IDEA:
// A shared menu both the kitchen (TranscodeWorker) and the waiter (AbrPlayer) read from.
// The transcoder uses it to know how big to encode each rendition; the player uses it to know
// which rendition a viewer's bandwidth can handle. One table keeps both sides in sync — if they
// disagreed, viewers would buffer (too big) or waste bandwidth (too small).
//
// WHY THESE VALUES (~2x per step): each tier roughly doubles the one below (400/800/2500/5000/
// 16000), so a bandwidth change big enough to switch tiers is unambiguous — the player won't
// oscillate between adjacent tiers on minor noise. Values track H.264/H.265 industry norms.
//
// WHY A STATIC DICTIONARY (not per-video config): bitrate targets are a one-time product
// decision affecting CDN cost and the quality floor. Centralising avoids a DB read on every
// quality decision; a per-video ladder would add lookups for little benefit.
//
// HOW IT BEHAVES AT RUNTIME:
//
//   Rendition | Kbps   | typical network
//   ----------|--------|-----------------------------
//   R360p     |   400  | weak 3G / rural
//   R480p     |   800  | LTE / congested WiFi
//   R720p     |  2500  | good home WiFi (HD)
//   R1080p    |  5000  | fast broadband (full HD)
//   R4K       | 16000  | gigabit / fibre
//
//   AbrPlayer reads Kbps[r] two ways:
//     pick quality   -> highest tier with Kbps[r] < throughput x 0.8
//     buffer math    -> downloadTime = Kbps[r] / throughput x 6   (per 6s segment)
//   e.g. R720p at 3200 Kbps: 2500/3200x6 = 4.7s to download a 6s segment -> buffer grows.

using System.Collections.Generic;

public static class BitrateTable
{
    public static readonly Dictionary<Rendition, int> Kbps = new()
    {
        { Rendition.R360p,    400 },   // SD    — 3G / weak signal floor
        { Rendition.R480p,    800 },   // SD+   — LTE minimum comfortable
        { Rendition.R720p,   2500 },   // HD    — standard home WiFi
        { Rendition.R1080p,  5000 },   // FHD   — strong WiFi / fast cable
        { Rendition.R4K,    16000 },   // UHD   — gigabit LAN / fibre ceiling
    };
}
