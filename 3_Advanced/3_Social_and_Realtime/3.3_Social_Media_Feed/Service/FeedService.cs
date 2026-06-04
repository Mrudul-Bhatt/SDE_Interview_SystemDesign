// FeedService — the READ path. Runs when a user opens the app and asks for their feed.
//
// THE BIG IDEA:
// This is the other half of the hybrid model. FanOutService (the write path) already
// pushed regular authors' posts into the user's feed cache. But celebrity posts were
// deliberately NOT pushed — so here, at read time, we go fetch them fresh and stitch
// the two sources together into one ranked page. In short:
//
//   final feed = (pre-built cache of regular follows) + (live pull of celebrity follows)
//
// This is why neither approach alone works: the cache gives instant reads for the many
// normal accounts, and the on-demand pull handles the few celebrities without ever
// having to fan a celebrity post out to millions of feeds.
//
// THE 4-STEP PIPELINE:
//   1. Read the pre-built feed from cache (regular follows).
//   2. Pull recent posts from each celebrity the user follows (live).
//   3. Merge the two lists and remove any duplicates.
//   4. Rank (or just sort by time) and return one page.

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class FeedService
    {
        private readonly FollowGraphRedis _graph;     // to find which follows are celebrities
        private readonly FeedCacheRedis _cache;     // the pre-built feed (regular follows)
        private readonly PostStoreCassandra _postStore; // to hydrate IDs and pull celebrity posts

        public FeedService(FollowGraphRedis graph, FeedCacheRedis cache, PostStoreCassandra postStore)
        {
            _graph = graph;
            _cache = cache;
            _postStore = postStore;
        }

        // Assemble one page of a user's feed.
        //   cursor      → pagination position (null = first/newest page)
        //   algorithmic → true = rank by engagement/decay; false = pure chronological
        //   authorAffinity → optional per-author boost (how much THIS user likes THAT author)
        public FeedPage GetFeed(string userId, int count = 20, long? cursor = null,
            bool algorithmic = false, Dictionary<string, double> authorAffinity = null)
        {
            // Step 1: read the pre-built feed from cache (the regular-follow posts that
            // were pushed in at write time). We ask for count*2 — see why below — and
            // then hydrate the bare IDs into full Post objects via PostStoreCassandra.
            var precomputedIds = _cache.GetFeed(userId, count * 2, cursor);
            var precomputedPosts = _postStore.GetByIds(precomputedIds);

            // Step 2: the celebrity PULL. For each celebrity this user follows, fetch
            // their recent posts live (these were never cached). If we're paginating,
            // keep only posts older than the cursor so they line up with the cache page.
            var celebPosts = new List<Post>();
            foreach (string celebId in _graph.GetCelebrityFollows(userId))
            {
                var recent = _postStore.GetRecentByAuthor(celebId, 10);
                celebPosts.AddRange(recent);
                if (cursor.HasValue)
                    celebPosts = celebPosts.Where(p => p.CreatedAt.Ticks < cursor.Value).ToList();
            }

            // Step 3: merge the two sources and drop duplicates. A post can appear in
            // both lists (e.g. an author who is borderline celebrity, or a recent
            // follow whose backfill overlapped the live pull). GroupBy(PostId) + First
            // keeps exactly one copy of each.
            var allPosts = precomputedPosts
                .Concat(celebPosts)
                .GroupBy(p => p.PostId)
                .Select(g => g.First())
                .ToList();

            // Step 4: order the merged set and cut it down to one page.
            //   algorithmic → FeedRanker scores by engagement × time-decay × affinity
            //   chronological → just newest-first
            List<Post> ranked = algorithmic
                ? FeedRanker.Rank(allPosts, authorAffinity).Take(count).ToList()
                : allPosts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();

            // The cursor for the NEXT page is the timestamp of the last post on THIS
            // page. The caller passes it back in to continue scrolling. Null when the
            // page is empty (nothing left to show).
            long? nextCursor = ranked.Count > 0 ? ranked.Last().CreatedAt.Ticks : (long?)null;
            return new FeedPage(ranked, nextCursor);
        }

        // WHY count*2 IN STEP 1: after we merge in celebrity posts and remove
        // duplicates (step 3) and then trim to `count` (step 4), we could end up short
        // if we'd only fetched exactly `count` to begin with. Grabbing twice as many
        // up front gives headroom so we rarely need a second round-trip to the cache.
    }
}
