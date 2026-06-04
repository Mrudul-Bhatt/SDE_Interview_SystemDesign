// FeedPage — one screenful of feed posts, plus a bookmark for where to continue.
//
// THE BIG IDEA:
// Think of FeedPage like a chapter in a book that ends with "continued on page 47".
// The reader finishes the chapter, then flips to page 47 to pick up exactly where
// they left off — no re-reading the end of the previous chapter, no skipping ahead.
// NextCursor is the "continued on page 47" — the exact position to start the next fetch.
//
// WHY CURSOR PAGINATION (not "give me page 2"):
//
//   Offset-based (bad for live feeds):
//     Page 1: SKIP 0  LIMIT 10  → posts [A, B, C, D, E, F, G, H, I, J]
//     (3 new posts arrive: X, Y, Z)
//     Page 2: SKIP 10 LIMIT 10  → posts [H, I, J, K, ...]   ← H, I, J REPEATED
//
//   Cursor-based (this approach):
//     Page 1: Score > -∞ LIMIT 10 → posts end at post J with Score=1000
//     (3 new posts arrive: X, Y, Z — they have higher Scores, don't affect J)
//     Page 2: Score < 1000 LIMIT 10 → posts [K, L, M, ...]  ← clean, no repeats
//
// The cursor is the Score of the LAST post the user saw. "Give me posts with
// Score strictly less than the cursor" always picks up exactly after that post,
// no matter what has been inserted since page 1 was fetched.
//
// WHY NEXT CURSOR IS NULLABLE:
// null means "you've reached the end of the feed" — there are no more pages to fetch.
// The client uses this to know when to stop showing a "load more" spinner. Without
// a null sentinel, the client would have to make an extra round-trip to discover
// the feed is exhausted (an empty page), wasting bandwidth and latency.
//
// IN REDIS TERMS:
// Each page fetch is essentially:
//   ZREVRANGEBYSCORE feed:{userId} {cursor - 1} -inf LIMIT 0 {pageSize}
// The result set's lowest Score becomes the NextCursor for the next call.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class FeedPage
    {
        // The hydrated Post objects for this page — full content, ready to render.
        // These are fetched from PostStore in a batch lookup after the FeedCache
        // returns the ordered list of PostIds for this cursor window.
        public List<Post> Posts { get; }

        // The Score of the oldest post on this page. Pass this value as the cursor
        // to the next GetFeed call to retrieve the following page without gaps or
        // duplicates. Null when this is the last page — no more posts remain.
        public long? NextCursor { get; }

        public FeedPage(List<Post> posts, long? nextCursor)
        {
            Posts = posts;
            NextCursor = nextCursor;
        }
    }
}
