// FeedCacheRedis — each user's ready-to-show feed, pre-built and waiting.
//
// THE BIG IDEA:
// This is the whole reason feeds open instantly. Instead of computing "what should
// Bob see?" when Bob opens the app (slow), we build Bob's feed AHEAD of time — every
// time someone he follows posts, we drop that post's ID into Bob's feed here. So when
// Bob finally opens the app, his feed is already sitting here, sorted and ready: we
// just read off the top. This is the "fan-out on write" payoff.
//
// Note it stores FeedEntry objects = (postId, score) ONLY — never full posts. The
// real content is fetched from PostStoreCassandra at read time. That's what keeps this cache
// small enough to live entirely in fast memory.
//
// In production this is literally a Redis sorted set per user. This class mimics one
// with a Dictionary of lists; the design choices below mirror how Redis behaves.
//
// WHY CAPPED AT 1000 ENTRIES:
// An active user could accumulate millions of feed items over the years. Multiply by
// hundreds of millions of users and you blow through memory. Nobody scrolls past ~100
// posts anyway, so we keep only the newest 1000 (trimming the oldest after each add).
// If someone somehow scrolls past 1000, we fall back to querying the slower database.
//
// WHY SORT NEWEST-FIRST ON EVERY WRITE (not on read):
// We always read from the top (newest), so keeping the list pre-sorted means reads do
// zero sorting. We pay the sort cost on writes instead — a deliberate trade, because
// feed opens (reads) vastly outnumber posts (writes), so we move the cost to the rare
// operation. (Real Redis keeps a sorted set ordered automatically, far more cheaply.)

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class FeedCacheRedis
    {
        // user_id → that user's feed (a list of lightweight postId+score entries).
        private readonly Dictionary<string, List<FeedEntry>> _feeds = [];

        // The per-user cap. Keep only the newest this-many entries; trim the rest.
        private const int MaxFeedSize = 1000;

        // Drop one post into a user's feed. Called once per follower when someone
        // posts (the fan-out write path) — so a post with 500 followers triggers
        // 500 calls to this, one per follower's feed.
        public void AddToFeed(string userId, string postId, long score)
        {
            // First post for this user? Create their (empty) feed list.
            if (!_feeds.ContainsKey(userId)) _feeds[userId] = [];

            // Remove any existing copy of this post first, so re-adding the same post
            // (e.g. backfill overlapping with a live fan-out) can't create a duplicate.
            _feeds[userId].RemoveAll(e => e.PostId == postId);

            _feeds[userId].Add(new FeedEntry(postId, score));

            // Keep the list ordered newest-first (highest score first) so reads are
            // trivial. b.CompareTo(a) — note b before a — gives descending order.
            _feeds[userId].Sort((a, b) => b.Score.CompareTo(a.Score));

            // Enforce the cap: if we're over 1000, chop off the oldest (the tail).
            if (_feeds[userId].Count > MaxFeedSize)
                _feeds[userId].RemoveRange(MaxFeedSize, _feeds[userId].Count - MaxFeedSize);
        }

        // Read a page of the feed. Returns just post IDs (the caller hydrates them
        // into full posts via PostStoreCassandra).
        //
        // beforeScore is the cursor for pagination: pass null for the first page
        // (newest), then pass the score of the last post you saw to get the next page.
        // Filtering by "score < cursor" instead of "skip N rows" is what makes paging
        // immune to new posts arriving at the top — no duplicates, no skips.
        public List<string> GetFeed(string userId, int count, long? beforeScore = null)
        {
            if (!_feeds.TryGetValue(userId, out var entries)) return [];
            var query = entries.AsEnumerable();
            if (beforeScore.HasValue) query = query.Where(e => e.Score < beforeScore.Value);
            return query.Take(count).Select(e => e.PostId).ToList();
        }

        // Remove a single post from a user's feed (e.g. the post was deleted).
        public void RemoveFromFeed(string userId, string postId)
        {
            if (_feeds.TryGetValue(userId, out var entries))
                entries.RemoveAll(e => e.PostId == postId);
        }

        // Used on UNFOLLOW: scrub every post by the now-unfollowed author out of the
        // follower's feed. We have to ask PostStoreCassandra who wrote each cached post, since
        // the feed only stores IDs, not authors. (This is the "strict" unfollow path;
        // a lazy approach would instead just let those posts age out over time.)
        public void RemoveAuthorFromFeed(string userId, string authorId, PostStoreCassandra postStore)
        {
            if (!_feeds.TryGetValue(userId, out var entries)) return;
            entries.RemoveAll(e =>
            {
                var post = postStore.Get(e.PostId);
                return post != null && post.AuthorId == authorId;
            });
        }

        // How many entries are in a user's feed right now (0 if they have none).
        // Used by the demo to show fan-out and cleanup actually changing feed sizes.
        public int GetFeedSize(string userId) => _feeds.TryGetValue(userId, out var e) ? e.Count : 0;
    }
}
