// BitrateTable — the canonical mapping from rendition to target bitrate.
//
// These are the numbers the ABR player compares against the measured download
// throughput to pick the next quality. Centralised here so the transcoder and
// the player stay in sync — a divergence between them would either starve the
// buffer (player picks rendition higher than actual bitrate) or under-utilise
// bandwidth (player picks lower than necessary).

using System.Collections.Generic;

public static class BitrateTable
{
    public static readonly Dictionary<Rendition, int> Kbps = new Dictionary<Rendition, int>
    {
        { Rendition.R360p,  400  },
        { Rendition.R480p,  800  },
        { Rendition.R720p,  2500 },
        { Rendition.R1080p, 5000 },
        { Rendition.R4K,    16000}
    };
}
