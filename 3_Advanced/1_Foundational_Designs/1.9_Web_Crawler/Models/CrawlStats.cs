// CrawlStats — counters updated by the Crawler during a run.
// Kept in a separate class so Program.cs can print a clean summary without
// reaching into Crawler internals.

namespace AdvancedDesigns
{
    public class CrawlStats
    {
        public int PagesCrawled        { get; set; }  // pages fetched (200 or 404)
        public int UrlsDiscovered      { get; set; }  // links extracted from HTML
        public int DuplicatesBlocked   { get; set; }  // Bloom filter hits
        public int RobotsBlocked       { get; set; }  // robots.txt disallow matches
        public int DepthBlocked        { get; set; }  // links beyond maxDepth
        public int NormalizationFailed { get; set; }  // URLs that couldn't be parsed
    }
}
