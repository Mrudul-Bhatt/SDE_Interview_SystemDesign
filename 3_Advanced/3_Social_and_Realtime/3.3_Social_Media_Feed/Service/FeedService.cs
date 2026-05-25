// FeedService — read-path: assembles a feed page from cache + celebrity pull + ranking.
//
// Read pipeline (4 steps):
//   1. Pre-computed: fetch post IDs from the user's feed cache (regular follows, fan-out on write).
//   2. Celebrity pull: fetch recent posts from each celebrity the user follows (on-demand).
//   3. Merge + deduplicate: union the two sets; a post can appear in both if a celebrity
//      was reclassified or the user recently followed them with backfill running in parallel.
//   4. Rank or sort: algorithmic uses FeedRanker (engagement × decay × affinity);
//      chronological just sorts by timestamp.
//
// Why fetch count*2 from cache in step 1: after merging with celebrity posts and
// deduplicating, we may have fewer than `count` items. Over-fetching gives headroom
// without a second cache round-trip in the common case.

namespace AdvancedDesigns
{
    public class FeedService
    {
        private readonly FollowGraph _graph;
        private readonly FeedCache   _cache;
        private readonly PostStore   _postStore;

        public FeedService(FollowGraph graph, FeedCache cache, PostStore postStore)
        {
            _graph     = graph;
            _cache     = cache;
            _postStore = postStore;
        }

        public FeedPage GetFeed(string userId, int count = 20, long? cursor = null,
            bool algorithmic = false, Dictionary<string, double> authorAffinity = null)
        {
            // Step 1: pre-computed feed from cache (non-celebrity follows)
            var precomputedIds   = _cache.GetFeed(userId, count * 2, cursor);
            var precomputedPosts = _postStore.GetByIds(precomputedIds);

            // Step 2: on-demand pull from celebrity follows
            var celebPosts = new List<Post>();
            foreach (string celebId in _graph.GetCelebrityFollows(userId))
            {
                var recent = _postStore.GetRecentByAuthor(celebId, 10);
                celebPosts.AddRange(recent);
                if (cursor.HasValue)
                    celebPosts = celebPosts.Where(p => p.CreatedAt.Ticks < cursor.Value).ToList();
            }

            // Step 3: merge and deduplicate by postId
            var allPosts = precomputedPosts
                .Concat(celebPosts)
                .GroupBy(p => p.PostId)
                .Select(g => g.First())
                .ToList();

            // Step 4: rank or sort
            List<Post> ranked = algorithmic
                ? FeedRanker.Rank(allPosts, authorAffinity).Take(count).ToList()
                : allPosts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();

            long? nextCursor = ranked.Count > 0 ? ranked.Last().CreatedAt.Ticks : (long?)null;
            return new FeedPage(ranked, nextCursor);
        }
    }
}
