// FeedEntry — a slot in a user's cached feed (mirrors a Redis sorted-set member).
//
// Why store Score instead of the Post itself: the feed cache only holds post IDs
// and their sort scores (timestamps or ranking scores). The actual post content
// is fetched from PostStore in a separate step, keeping the cache compact and
// allowing post data to be updated without invalidating the feed cache.

namespace AdvancedDesigns
{
    public class FeedEntry
    {
        public string PostId { get; }
        public long   Score  { get; } // timestamp ticks used for ordering

        public FeedEntry(string postId, long score)
        {
            PostId = postId;
            Score  = score;
        }
    }
}
