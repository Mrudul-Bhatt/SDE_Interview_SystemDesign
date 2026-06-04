// FanOutService — the WRITE path. Runs when someone posts, and decides who gets it.
//
// THE BIG IDEA:
// "Fan-out" = taking one post and spreading copies of its ID into many feeds. This
// service is where that happens. But it can't blindly copy to everyone — that's the
// celebrity problem — so it makes ONE key decision per post: push now, or skip?
//
// THE HYBRID STRATEGY (the heart of this whole project):
//   Regular author → "fan-out on write": push the post ID into every follower's feed
//                     cache NOW. Reads later are instant (the feed is pre-built).
//   Celebrity author → SKIP the push. The post just sits in the database, and is
//                      pulled in fresh when each follower reads their feed (that pull
//                      happens over in FeedService).
//
// WHY SKIP CELEBRITIES ON WRITE:
// A user with 10M followers would need 10M feed-cache writes for a SINGLE post —
// seconds of work, hammering the cache, for one tweet. Pushing is great for the many
// normal users (a few hundred writes) but disastrous for the few huge accounts. So we
// push for the many and pull for the few — best of both worlds.

namespace AdvancedDesigns
{
    public class FanOutService
    {
        private readonly FollowGraphRedis _graph;     // who follows whom (to find recipients)
        private readonly FeedCacheRedis _cache;     // the per-user feeds we write into
        private readonly PostStoreCassandra _postStore; // source of an author's recent posts (for backfill)

        public FanOutService(FollowGraphRedis graph, FeedCacheRedis cache, PostStoreCassandra postStore)
        {
            _graph = graph;
            _cache = cache;
            _postStore = postStore;
        }

        // Called once whenever a post is created. Returns a small receipt
        // (FanOutResult) saying which path was taken and how many feeds were written.
        public FanOutResult OnPost(Post post)
        {
            // THE decision: is the author big enough to skip the push?
            bool isCelebrity = _graph.IsCelebrity(post.AuthorId);
            int fanOutCount = 0;

            if (!isCelebrity)
            {
                // Regular author → push this post into every follower's feed now.
                // We use the post's creation time as the sort score, so it lands in
                // the right chronological spot in each feed.
                long score = post.CreatedAt.Ticks;
                foreach (string followerId in _graph.GetFollowers(post.AuthorId))
                {
                    _cache.AddToFeed(followerId, post.PostId, score);
                    fanOutCount++;  // count writes so the demo can show the fan-out size
                }
            }
            // (Celebrity → do nothing here. The post is already saved in PostStoreCassandra;
            //  FeedService will pull it in at read time. fanOutCount stays 0.)

            return new FanOutResult(post.PostId, post.AuthorId, isCelebrity, fanOutCount);
        }

        // Called when a user FOLLOWS someone new. Without this, the new follow's old
        // posts would be missing from your feed until they happen to post again — your
        // feed would look empty. So we retroactively inject the target's recent posts.
        // (Only their already-existing posts; future ones arrive via normal OnPost.)
        public void BackfillOnFollow(string followerId, string targetId, int count = 20)
        {
            foreach (var post in _postStore.GetRecentByAuthor(targetId, count))
                _cache.AddToFeed(followerId, post.PostId, post.CreatedAt.Ticks);
        }

        // Called when a user UNFOLLOWS someone. Removes that author's posts from the
        // follower's feed so they stop seeing content from someone they dropped.
        // In production this runs asynchronously (in the background) so the unfollow
        // button responds instantly instead of waiting for the cleanup to finish.
        public void CleanupOnUnfollow(string followerId, string targetId)
            => _cache.RemoveAuthorFromFeed(followerId, targetId, _postStore);
    }
}
