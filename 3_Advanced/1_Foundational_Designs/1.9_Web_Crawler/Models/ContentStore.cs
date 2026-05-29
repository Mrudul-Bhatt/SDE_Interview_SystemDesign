// CrawledPage + ContentStore — models a crawled page and the store that holds them.
//
// In production: ContentStore writes to distributed object storage (S3/GCS)
// keyed by URL hash. ContentHash enables near-duplicate detection — two pages
// with the same hash are identical and only one needs to be indexed.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class CrawledPage
    {
        public string Url { get; set; }
        public string Html { get; set; }
        public int StatusCode { get; set; }
        public DateTime CrawledAt { get; set; }
        public int Depth { get; set; }

        // Simple polynomial hash of the HTML body.
        // Real system uses SimHash or MinHash for near-duplicate detection across
        // pages that differ only in ads, timestamps, or nav elements.
        public string ContentHash { get; set; }
    }

    public class ContentStore
    {
        private readonly List<CrawledPage> _pages = [];

        // lock on _pages itself: Store() and Count/Pages are called from the
        // crawler's main loop — simpler than a separate lock object here.
        public void Store(CrawledPage page)
        {
            lock (_pages) _pages.Add(page);
        }

        public int Count
        {
            get { lock (_pages) return _pages.Count; }
        }

        public IReadOnlyList<CrawledPage> Pages
        {
            // Return a snapshot so callers can iterate without holding the lock.
            get { lock (_pages) return _pages.ToList(); }
        }
    }
}
