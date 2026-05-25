// RedirectResult — the response returned by UrlShortenerService.Redirect().
// Models HTTP semantics: 302 redirect, 404 not found, 410 gone (expired/deactivated).

namespace AdvancedDesigns
{
    public class RedirectResult
    {
        // 302 = active link found, redirect to LongUrl
        // 404 = short code does not exist
        // 410 = short code exists but is expired or deactivated (Gone, not Not Found)
        //       410 is preferred over 404 for expired links so search engines
        //       know the resource existed and was intentionally removed.
        public int StatusCode { get; set; }

        // Populated on 302 only — the destination URL the client is redirected to.
        public string LongUrl { get; set; }

        // Human-readable explanation for non-302 results (logged or shown to user).
        public string Note { get; set; }

        // True when the record was served from LRU cache rather than the DB.
        // Used in demos and monitoring to measure cache hit rate.
        public bool FromCache { get; set; }
    }
}
