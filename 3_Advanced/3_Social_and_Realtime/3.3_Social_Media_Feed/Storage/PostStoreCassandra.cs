// PostStoreCassandra — the durable home of every post's actual content.
//
// THE BIG IDEA:
// Remember the golden rule of this system: feeds store only post IDs, never the
// full post. So something has to turn those IDs back into real posts (text, author,
// counts) when it's time to show them. That "something" is the PostStoreCassandra. It's the
// single source of truth for post content; everything else just holds references to it.
//
// WHY TWO INDEXES (the same posts, organized two different ways):
// The system looks up posts in two completely different ways, so we keep two maps:
//
//   1. _postsById     "give me THIS exact post"   → used to turn feed IDs into posts
//                                                    (called "hydration" — see GetByIds)
//   2. _postsByAuthor "give me this author's
//                      recent posts"               → used by the celebrity read path
//                                                    and by backfill-on-follow
//
// Keeping both is the classic space-for-speed trade: a little extra storage so BOTH
// lookups are instant, instead of scanning everything to answer one of them.
//
// In production this is Cassandra. _postsById maps to a table keyed by post_id;
// _postsByAuthor maps to a table partitioned by author_id and clustered by
// created_at DESC, so "this author's latest posts" is one fast, pre-sorted scan.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class PostStoreCassandra
    {
        // Index #1: post_id → the post. The random-access lookup used to "hydrate"
        // feed entries (a feed gives us IDs; this turns each ID into a real Post).
        private readonly Dictionary<string, Post> _postsById = [];

        // Index #2: author_id → that author's posts, in insert order. The range
        // lookup used to pull "a user's recent posts" on demand (celebrity reads,
        // backfill). Same Post objects as above — just grouped by who wrote them.
        private readonly Dictionary<string, List<Post>> _postsByAuthor = [];

        // Simple counter to mint demo post IDs ("post:1", "post:2", ...).
        // Production uses Snowflake IDs instead (see Post.cs) so any server can
        // generate unique, time-sortable IDs without a shared counter like this one.
        private long _idCounter;

        // Creates a post and writes it into BOTH indexes at once, so it's
        // immediately findable by ID and by author. (The likes/comments/shares
        // params let tests seed a post with pre-set engagement to exercise ranking.)
        public Post CreatePost(string authorId, string content, DateTime? createdAt = null,
            int likes = 0, int comments = 0, int shares = 0)
        {
            string postId = $"post:{++_idCounter}";
            var post = new Post(postId, authorId, content, createdAt)
            {
                LikeCount = likes,
                CommentCount = comments,
                ShareCount = shares
            };

            // Write to index #1 (by ID).
            _postsById[postId] = post;

            // Write to index #2 (by author) — create the author's list on first post.
            if (!_postsByAuthor.ContainsKey(authorId))
                _postsByAuthor[authorId] = [];
            _postsByAuthor[authorId].Add(post);

            return post;
        }

        // Look up one post by ID. Returns null if it doesn't exist (e.g. a stale ID
        // left in a feed after the post was deleted) — callers must handle null.
        public Post Get(string postId) => _postsById.TryGetValue(postId, out var p) ? p : null;

        // Hydration: turn a list of feed IDs into real posts. Quietly drops any ID
        // that no longer resolves (Where p != null), so a deleted post just vanishes
        // from the feed instead of crashing the read.
        public List<Post> GetByIds(IEnumerable<string> postIds) => postIds.Select(Get).Where(p => p != null).ToList();

        // The celebrity / backfill read path: "give me this author's newest N posts."
        // Returns empty if the author has none. Sorting here mimics Cassandra's
        // clustering-by-time, which would return them pre-sorted with no work.
        public List<Post> GetRecentByAuthor(string authorId, int count = 20)
        {
            if (!_postsByAuthor.TryGetValue(authorId, out var posts)) return [];
            return posts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();
        }
    }
}
