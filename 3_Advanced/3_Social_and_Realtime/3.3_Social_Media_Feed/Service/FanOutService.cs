// FanOutService — write-path: pushes a new post into follower feed caches.
//
// Hybrid fan-out strategy:
//   Regular user → fan-out on write: post is pushed to every follower's feed cache.
//   Celebrity     → skip fan-out:    post is stored in DB only; pulled at read time.
//
// Why skip celebrities on write: a user with 10M followers would require 10M cache
// writes per post, taking seconds and overwhelming Redis. Instead, celebrity posts
// are fetched fresh on every feed read (FeedService merges them in).
//
// Backfill on follow: when a user follows someone new, their recent posts are
// retroactively inserted into the follower's feed so the feed isn't empty.
//
// Cleanup on unfollow: posts from the unfollowed author are removed from the
// follower's feed cache. Done asynchronously in production to avoid blocking the
// unfollow API call.

namespace AdvancedDesigns
{
    public class FanOutService
    {
        private readonly FollowGraph _graph;
        private readonly FeedCache   _cache;
        private readonly PostStore   _postStore;

        public FanOutService(FollowGraph graph, FeedCache cache, PostStore postStore)
        {
            _graph     = graph;
            _cache     = cache;
            _postStore = postStore;
        }

        public FanOutResult OnPost(Post post)
        {
            bool isCelebrity = _graph.IsCelebrity(post.AuthorId);
            int  fanOutCount = 0;

            if (!isCelebrity)
            {
                long score = post.CreatedAt.Ticks;
                foreach (string followerId in _graph.GetFollowers(post.AuthorId))
                {
                    _cache.AddToFeed(followerId, post.PostId, score);
                    fanOutCount++;
                }
            }

            return new FanOutResult(post.PostId, post.AuthorId, isCelebrity, fanOutCount);
        }

        public void BackfillOnFollow(string followerId, string targetId, int count = 20)
        {
            foreach (var post in _postStore.GetRecentByAuthor(targetId, count))
                _cache.AddToFeed(followerId, post.PostId, post.CreatedAt.Ticks);
        }

        public void CleanupOnUnfollow(string followerId, string targetId)
            => _cache.RemoveAuthorFromFeed(followerId, targetId, _postStore);
    }
}
