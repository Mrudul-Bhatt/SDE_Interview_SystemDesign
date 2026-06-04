// FeedEntry — a single slot in a user's pre-computed feed cache.
//
// THE BIG IDEA:
// Think of a FeedEntry like a library card-catalog card. The card doesn't contain
// the book itself — it contains the shelf reference (PostId) and a sort key (Score)
// that tells the librarian where to file it among the other cards.
//
// When Bob opens his feed, the system reads his cache of FeedEntries (just IDs and
// scores — tiny), then fetches the full Post objects from PostStoreCassandra in one batch
// lookup. Two separate concerns:
//   Feed cache  → "which posts, in what order?" (FeedEntry list, kept in Redis)
//   Post store  → "what is the content of post X?" (Post objects, kept in a DB)
//
// WHY STORE ONLY THE POST ID (not the full Post)?
// Indirection is the key design choice here. Alice has 10 million followers.
// When she posts, 10 million FeedEntry records get written — one per follower's cache.
// If each entry stored the full post content (1 KB), that's 10 GB of duplicated data
// for a single post. If each entry stores just the PostId (a few bytes), it's ~40 MB.
//
// Indirection also means correctness for free: if Alice edits her post after fan-out,
// every follower's feed automatically reflects the edit on next read, because all
// those FeedEntries still point to the same single Post object in PostStoreCassandra.
// No cache invalidation needed.
//
// WHY SCORE (not just a timestamp field)?
// Score is the abstraction that lets the same cache structure support two feed modes:
//
//   Chronological mode:  Score = post.CreatedAt.Ticks (larger = newer)
//     → sort by Score descending = "newest posts first"
//
//   Algorithmic mode:    Score = engagement-weighted ranking score
//     → sort by Score descending = "most interesting posts first"
//     (FeedRanker computes this from likes, comments, shares, and time-decay)
//
// FeedCacheRedis stores and sorts by Score without caring which mode produced it.
// This means switching from chronological to algorithmic (or back) is just a
// matter of which Score value gets written at fan-out time — the cache itself
// doesn't change.
//
// HOW CURSOR PAGINATION USES SCORE:
// Instead of "give me page 2", the caller says "give me entries with Score < lastSeen".
// This avoids the classic page-drift problem: if new posts arrive between page 1
// and page 2, a page-number cursor would skip some posts or show duplicates.
// A Score cursor is stable — it pins to a position in the sorted order, not a page number.
// In Redis this is ZREVRANGEBYSCORE key (lastScore-1) -inf LIMIT 0 20.

namespace AdvancedDesigns
{
    public class FeedEntry
    {
        // The lightweight pointer to the actual post. Only this ID is stored in the
        // feed cache; the full Post (content, likes, comments) is fetched separately
        // from PostStoreCassandra when the feed is rendered. Keeps the cache small and
        // allows post edits to propagate without touching any FeedEntry.
        public string PostId { get; }

        // The sort key for this entry. Higher Score = appears earlier in the feed.
        // In chronological mode this is DateTime.Ticks (monotone, newer = bigger).
        // In algorithmic mode this is a computed ranking score from FeedRanker.
        // Also serves as the cursor for pagination: "give me entries with Score < X"
        // lets a client scroll through the feed page by page without drift.
        public long Score { get; }

        public FeedEntry(string postId, long score)
        {
            PostId = postId;
            Score = score;
        }
    }
}
