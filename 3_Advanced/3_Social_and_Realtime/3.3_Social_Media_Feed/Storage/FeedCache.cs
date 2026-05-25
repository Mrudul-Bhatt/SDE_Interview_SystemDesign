// FeedCache — per-user pre-computed feed (mirrors a Redis sorted set per user).
//
// Why capped at 1000 entries: storing an unbounded feed per user would exhaust
// Redis memory. Users rarely scroll past 1000 posts; beyond that, re-query the
// DB. The cap is applied after every insert (trim oldest entries = lowest scores).
//
// Why sort descending on every insert: we always read from the top (newest),
// so keeping the list pre-sorted avoids sorting on every read. The tradeoff is
// O(n log n) on write, which is acceptable because writes (posts) are far less
// frequent than reads (feed opens).

namespace AdvancedDesigns
{
    public class FeedCache
    {
        private readonly Dictionary<string, List<FeedEntry>> _feeds = new();
        private const int MaxFeedSize = 1000;

        public void AddToFeed(string userId, string postId, long score)
        {
            if (!_feeds.ContainsKey(userId)) _feeds[userId] = new List<FeedEntry>();
            _feeds[userId].RemoveAll(e => e.PostId == postId); // deduplicate
            _feeds[userId].Add(new FeedEntry(postId, score));
            _feeds[userId].Sort((a, b) => b.Score.CompareTo(a.Score)); // newest first
            if (_feeds[userId].Count > MaxFeedSize)
                _feeds[userId].RemoveRange(MaxFeedSize, _feeds[userId].Count - MaxFeedSize);
        }

        // beforeScore implements cursor pagination: only return entries older than the cursor.
        public List<string> GetFeed(string userId, int count, long? beforeScore = null)
        {
            if (!_feeds.TryGetValue(userId, out var entries)) return new List<string>();
            var query = entries.AsEnumerable();
            if (beforeScore.HasValue) query = query.Where(e => e.Score < beforeScore.Value);
            return query.Take(count).Select(e => e.PostId).ToList();
        }

        public void RemoveFromFeed(string userId, string postId)
        {
            if (_feeds.TryGetValue(userId, out var entries))
                entries.RemoveAll(e => e.PostId == postId);
        }

        // Used on unfollow: clean up all posts by the unfollowed author from the follower's feed.
        public void RemoveAuthorFromFeed(string userId, string authorId, PostStore postStore)
        {
            if (!_feeds.TryGetValue(userId, out var entries)) return;
            entries.RemoveAll(e =>
            {
                var post = postStore.Get(e.PostId);
                return post != null && post.AuthorId == authorId;
            });
        }

        public int GetFeedSize(string userId) =>
            _feeds.TryGetValue(userId, out var e) ? e.Count : 0;
    }
}
