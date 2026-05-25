// PostStore — durable post storage indexed by ID and by author.
// Simulates Cassandra with two access patterns:
//   1. Get by postId        → _postsById     (random lookup for feed hydration)
//   2. Get recent by author → _postsByAuthor (range scan for celebrity pull + backfill)
//
// In production these would be separate Cassandra tables (or a single table
// with a partition key on authorId and a clustering key on createdAt DESC).

namespace AdvancedDesigns
{
    public class PostStore
    {
        private readonly Dictionary<string, Post>       _postsById     = new();
        private readonly Dictionary<string, List<Post>> _postsByAuthor = new();
        private long _idCounter;

        public Post CreatePost(string authorId, string content, DateTime? createdAt = null,
            int likes = 0, int comments = 0, int shares = 0)
        {
            string postId = $"post:{++_idCounter}";
            var post = new Post(postId, authorId, content, createdAt)
            {
                LikeCount    = likes,
                CommentCount = comments,
                ShareCount   = shares
            };
            _postsById[postId] = post;
            if (!_postsByAuthor.ContainsKey(authorId))
                _postsByAuthor[authorId] = new List<Post>();
            _postsByAuthor[authorId].Add(post);
            return post;
        }

        public Post Get(string postId) =>
            _postsById.TryGetValue(postId, out var p) ? p : null;

        public List<Post> GetByIds(IEnumerable<string> postIds) =>
            postIds.Select(Get).Where(p => p != null).ToList();

        public List<Post> GetRecentByAuthor(string authorId, int count = 20)
        {
            if (!_postsByAuthor.TryGetValue(authorId, out var posts)) return new List<Post>();
            return posts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();
        }
    }
}
