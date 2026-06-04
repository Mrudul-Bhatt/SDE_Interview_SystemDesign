using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AdvancedDesigns
{
    // ─── Post ──────────────────────────────────────────────────────────────────

    public class Post
    {
        public string PostId { get; }
        public string AuthorId { get; }
        public string Content { get; }
        public DateTime CreatedAt { get; }
        public int LikeCount { get; set; }
        public int CommentCount { get; set; }
        public int ShareCount { get; set; }

        public double EngagementRaw => LikeCount * 2 + CommentCount * 3 + ShareCount * 5;

        public Post(string postId, string authorId, string content, DateTime? createdAt = null)
        {
            PostId = postId;
            AuthorId = authorId;
            Content = content;
            CreatedAt = createdAt ?? DateTime.UtcNow;
        }

        public override string ToString() =>
            $"[{PostId}] @{AuthorId}: \"{Content}\" | " +
            $"likes={LikeCount} comments={CommentCount} age={Math.Round((DateTime.UtcNow - CreatedAt).TotalMinutes)}min";
    }

    // ─── Follow Graph (simulates Redis SADD / SCARD / SMEMBERS) ──────────────

    public class FollowGraphRedis
    {
        private readonly Dictionary<string, HashSet<string>> _followers = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, HashSet<string>> _following = new Dictionary<string, HashSet<string>>();
        private const int CelebrityThreshold = 10; // 10 for demo (10M in production)

        public void Follow(string followerId, string targetId)
        {
            GetOrCreate(_followers, targetId).Add(followerId);
            GetOrCreate(_following, followerId).Add(targetId);
        }

        public void Unfollow(string followerId, string targetId)
        {
            GetOrCreate(_followers, targetId).Remove(followerId);
            GetOrCreate(_following, followerId).Remove(targetId);
        }

        public IEnumerable<string> GetFollowers(string userId) =>
            _followers.TryGetValue(userId, out var s) ? s : Enumerable.Empty<string>();

        public IEnumerable<string> GetFollowing(string userId) =>
            _following.TryGetValue(userId, out var s) ? s : Enumerable.Empty<string>();

        public int GetFollowerCount(string userId) =>
            _followers.TryGetValue(userId, out var s) ? s.Count : 0;

        public bool IsCelebrity(string userId) => GetFollowerCount(userId) >= CelebrityThreshold;

        public IEnumerable<string> GetCelebrityFollows(string userId) =>
            GetFollowing(userId).Where(IsCelebrity);

        public IEnumerable<string> GetRegularFollows(string userId) =>
            GetFollowing(userId).Where(f => !IsCelebrity(f));

        private static HashSet<string> GetOrCreate(Dictionary<string, HashSet<string>> dict, string key)
        {
            if (!dict.ContainsKey(key)) dict[key] = new HashSet<string>();
            return dict[key];
        }
    }

    // ─── Post Store (simulates Cassandra — indexed by author + time) ──────────

    public class PostStoreCassandra
    {
        private readonly Dictionary<string, Post> _postsById = new Dictionary<string, Post>();
        private readonly Dictionary<string, List<Post>> _postsByAuthor = new Dictionary<string, List<Post>>();
        private long _idCounter;

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
            if (!_postsByAuthor.TryGetValue(authorId, out var posts))
                return new List<Post>();
            return posts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();
        }
    }

    // ─── Feed Cache (simulates Redis sorted set per user) ─────────────────────

    public class FeedEntry
    {
        public string PostId { get; }
        public long Score { get; } // timestamp ticks

        public FeedEntry(string postId, long score)
        {
            PostId = postId;
            Score = score;
        }
    }

    public class FeedCacheRedis
    {
        private readonly Dictionary<string, List<FeedEntry>> _feeds = new Dictionary<string, List<FeedEntry>>();
        private const int MaxFeedSize = 1000;

        public void AddToFeed(string userId, string postId, long score)
        {
            if (!_feeds.ContainsKey(userId)) _feeds[userId] = new List<FeedEntry>();
            // Remove duplicate if exists
            _feeds[userId].RemoveAll(e => e.PostId == postId);
            _feeds[userId].Add(new FeedEntry(postId, score));
            // Keep sorted descending by score (newest first)
            _feeds[userId].Sort((a, b) => b.Score.CompareTo(a.Score));
            // Trim to max size
            if (_feeds[userId].Count > MaxFeedSize)
                _feeds[userId].RemoveRange(MaxFeedSize, _feeds[userId].Count - MaxFeedSize);
        }

        public List<string> GetFeed(string userId, int count, long? beforeScore = null)
        {
            if (!_feeds.TryGetValue(userId, out var entries)) return new List<string>();
            var query = entries.AsEnumerable();
            if (beforeScore.HasValue)
                query = query.Where(e => e.Score < beforeScore.Value);
            return query.Take(count).Select(e => e.PostId).ToList();
        }

        public void RemoveFromFeed(string userId, string postId)
        {
            if (_feeds.TryGetValue(userId, out var entries))
                entries.RemoveAll(e => e.PostId == postId);
        }

        public void RemoveAuthorFromFeed(string userId, string authorId, PostStoreCassandra postStore)
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

    // ─── Ranking ───────────────────────────────────────────────────────────────

    public static class FeedRanker
    {
        // HackerNews-style decay: engagement / (age_hours + 1)^gravity
        private const double Gravity = 1.8;

        public static double Score(Post post, double affinityBoost = 1.0)
        {
            double ageHours = (DateTime.UtcNow - post.CreatedAt).TotalHours;
            double decay = 1.0 / Math.Pow(ageHours + 1, Gravity);
            return post.EngagementRaw * decay * affinityBoost + decay * 50; // floor for new posts
        }

        public static List<Post> Rank(IEnumerable<Post> posts,
            Dictionary<string, double> authorAffinity = null)
        {
            return posts
                .Select(p =>
                {
                    double affinity = 1.0;
                    if (authorAffinity != null && authorAffinity.TryGetValue(p.AuthorId, out double a))
                        affinity = 1.0 + a * 0.5;
                    return (post: p, score: Score(p, affinity));
                })
                .OrderByDescending(x => x.score)
                .Select(x => x.post)
                .ToList();
        }
    }

    // ─── Fan-out Service ───────────────────────────────────────────────────────

    public class FanOutService
    {
        private readonly FollowGraphRedis _graph;
        private readonly FeedCacheRedis _cache;
        private readonly PostStoreCassandra _postStore;

        public FanOutService(FollowGraphRedis graph, FeedCacheRedis cache, PostStoreCassandra postStore)
        {
            _graph = graph;
            _cache = cache;
            _postStore = postStore;
        }

        public FanOutResult OnPost(Post post)
        {
            bool isCelebrity = _graph.IsCelebrity(post.AuthorId);
            int fanOutCount = 0;

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
            var recentPosts = _postStore.GetRecentByAuthor(targetId, count);
            foreach (var post in recentPosts)
                _cache.AddToFeed(followerId, post.PostId, post.CreatedAt.Ticks);
        }

        public void CleanupOnUnfollow(string followerId, string targetId)
        {
            _cache.RemoveAuthorFromFeed(followerId, targetId, _postStore);
        }
    }

    public class FanOutResult
    {
        public string PostId { get; }
        public string AuthorId { get; }
        public bool WasCelebrity { get; }
        public int FanOutCount { get; }

        public FanOutResult(string postId, string authorId, bool wasCelebrity, int fanOutCount)
        {
            PostId = postId;
            AuthorId = authorId;
            WasCelebrity = wasCelebrity;
            FanOutCount = fanOutCount;
        }
    }

    // ─── Feed Service (read path: pre-computed + celebrity merge + rank) ───────

    public class FeedService
    {
        private readonly FollowGraphRedis _graph;
        private readonly FeedCacheRedis _cache;
        private readonly PostStoreCassandra _postStore;

        public FeedService(FollowGraphRedis graph, FeedCacheRedis cache, PostStoreCassandra postStore)
        {
            _graph = graph;
            _cache = cache;
            _postStore = postStore;
        }

        public FeedPage GetFeed(string userId, int count = 20, long? cursor = null,
            bool algorithmic = false, Dictionary<string, double> authorAffinity = null)
        {
            // Step 1: pre-computed feed entries from non-celebrity follows
            var precomputedIds = _cache.GetFeed(userId, count * 2, cursor);
            var precomputedPosts = _postStore.GetByIds(precomputedIds);

            // Step 2: pull recent posts from celebrity follows
            var celebPosts = new List<Post>();
            foreach (string celebId in _graph.GetCelebrityFollows(userId))
            {
                var recent = _postStore.GetRecentByAuthor(celebId, 10);
                celebPosts.AddRange(recent);
                if (cursor.HasValue)
                    celebPosts = celebPosts.Where(p => p.CreatedAt.Ticks < cursor.Value).ToList();
            }

            // Step 3: merge and deduplicate
            var allPosts = precomputedPosts
                .Concat(celebPosts)
                .GroupBy(p => p.PostId)
                .Select(g => g.First())
                .ToList();

            // Step 4: rank (algorithmic) or sort by time (chronological)
            List<Post> ranked;
            if (algorithmic)
                ranked = FeedRanker.Rank(allPosts, authorAffinity).Take(count).ToList();
            else
                ranked = allPosts.OrderByDescending(p => p.CreatedAt).Take(count).ToList();

            long? nextCursor = ranked.Count > 0 ? ranked.Last().CreatedAt.Ticks : (long?)null;
            return new FeedPage(ranked, nextCursor);
        }
    }

    public class FeedPage
    {
        public List<Post> Posts { get; }
        public long? NextCursor { get; }

        public FeedPage(List<Post> posts, long? nextCursor)
        {
            Posts = posts;
            NextCursor = nextCursor;
        }
    }

    // ─── Main Program ──────────────────────────────────────────────────────────

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Social Media Feed Demo ===\n");

            Scenario1_BasicFanOutAndFeedRead();
            Scenario2_CelebrityHybridFanOut();
            Scenario3_CursorPagination();
            Scenario4_AlgorithmicRanking();
            Scenario5_FollowUnfollowFeedUpdate();
        }

        static (FollowGraphRedis, PostStoreCassandra, FeedCacheRedis, FanOutService, FeedService) BuildSystem()
        {
            var graph = new FollowGraphRedis();
            var postStore = new PostStoreCassandra();
            var feedCache = new FeedCacheRedis();
            var fanOut = new FanOutService(graph, feedCache, postStore);
            var feedSvc = new FeedService(graph, feedCache, postStore);
            return (graph, postStore, feedCache, fanOut, feedSvc);
        }

        // ── Scenario 1: Basic Fan-out on Write ────────────────────────────────

        static void Scenario1_BasicFanOutAndFeedRead()
        {
            Console.WriteLine("─── Scenario 1: Basic Fan-out on Write ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            // Bob and Carol follow Alice; Dave follows Bob
            graph.Follow("bob", "alice");
            graph.Follow("carol", "alice");
            graph.Follow("dave", "bob");

            Console.WriteLine("Follow graph: bob→alice, carol→alice, dave→bob");
            Console.WriteLine($"Alice has {graph.GetFollowerCount("alice")} followers (regular user, threshold=10)\n");

            // Alice posts — fan-out to bob + carol
            var p1 = posts.CreatePost("alice", "Just shipped a new feature!");
            var r1 = fanOut.OnPost(p1);
            Console.WriteLine($"Alice posts \"{p1.Content}\"");
            Console.WriteLine($"  Fan-out: celebrity={r1.WasCelebrity}, pushed to {r1.FanOutCount} feeds");

            var p2 = posts.CreatePost("alice", "Weekend hiking trip");
            fanOut.OnPost(p2);

            var p3 = posts.CreatePost("bob", "Watching the game tonight");
            var r3 = fanOut.OnPost(p3);
            Console.WriteLine($"\nBob posts \"{p3.Content}\"");
            Console.WriteLine($"  Fan-out: pushed to {r3.FanOutCount} feed (dave)");

            // Read feeds
            Console.WriteLine($"\nBob's feed ({cache.GetFeedSize("bob")} entries):");
            var bobFeed = feedSvc.GetFeed("bob", count: 10);
            foreach (var p in bobFeed.Posts) Console.WriteLine($"  {p}");

            Console.WriteLine($"\nCarol's feed ({cache.GetFeedSize("carol")} entries):");
            var carolFeed = feedSvc.GetFeed("carol", count: 10);
            foreach (var p in carolFeed.Posts) Console.WriteLine($"  {p}");

            Console.WriteLine($"\nDave's feed ({cache.GetFeedSize("dave")} entries):");
            var daveFeed = feedSvc.GetFeed("dave", count: 10);
            foreach (var p in daveFeed.Posts) Console.WriteLine($"  {p}");

            Console.WriteLine();
        }

        // ── Scenario 2: Celebrity Hybrid Fan-out ─────────────────────────────

        static void Scenario2_CelebrityHybridFanOut()
        {
            Console.WriteLine("─── Scenario 2: Celebrity Hybrid Fan-out (threshold = 10 followers) ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            // Create a celebrity (techguru) with 12 followers
            string[] fans = { "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u9", "u10", "u11", "u12" };
            foreach (var fan in fans) graph.Follow(fan, "techguru");

            Console.WriteLine($"techguru has {graph.GetFollowerCount("techguru")} followers");
            Console.WriteLine($"Is celebrity? {graph.IsCelebrity("techguru")} (threshold={10})");

            // u1 also follows alice (regular user, 2 followers)
            graph.Follow("u1", "alice");
            graph.Follow("u2", "alice");

            // Alice posts (regular user) — fan-out happens
            var alicePost = posts.CreatePost("alice", "Regular user post", likes: 5);
            var aliceResult = fanOut.OnPost(alicePost);
            Console.WriteLine($"\nalice posts → fan-out to {aliceResult.FanOutCount} followers (IS written to feed cache)");

            // techguru posts (celebrity) — NO fan-out
            var celebPost = posts.CreatePost("techguru", "Big announcement! New product launch.", likes: 500, comments: 200);
            var celebResult = fanOut.OnPost(celebPost);
            Console.WriteLine($"techguru posts → fan-out to {celebResult.FanOutCount} followers (celebrity: SKIPPED)");
            Console.WriteLine($"  (Post stored in DB only; pulled at read time)");

            // u1 opens feed — gets alice's post (pre-computed) + techguru's post (pulled on demand)
            Console.WriteLine($"\nu1's feed (follows alice + techguru):");
            Console.WriteLine($"  Feed cache size: {cache.GetFeedSize("u1")} entry (only alice's post pre-computed)");
            var u1Feed = feedSvc.GetFeed("u1", count: 10);
            Console.WriteLine($"  After merge (pre-computed + celebrity pull): {u1Feed.Posts.Count} posts");
            foreach (var p in u1Feed.Posts) Console.WriteLine($"    {p}");

            Console.WriteLine();
        }

        // ── Scenario 3: Cursor-Based Pagination ──────────────────────────────

        static void Scenario3_CursorPagination()
        {
            Console.WriteLine("─── Scenario 3: Cursor-Based Pagination ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            graph.Follow("reader", "writer");

            // Create 15 posts spread over time
            var baseTime = DateTime.UtcNow.AddMinutes(-150);
            for (int i = 1; i <= 15; i++)
            {
                var p = posts.CreatePost("writer", $"Post #{i:D2}", baseTime.AddMinutes(i * 10));
                fanOut.OnPost(p);
            }

            Console.WriteLine($"15 posts in feed. Reading in pages of 5:\n");

            // Page 1
            var page1 = feedSvc.GetFeed("reader", count: 5);
            Console.WriteLine($"Page 1 ({page1.Posts.Count} posts):");
            foreach (var p in page1.Posts) Console.WriteLine($"  {p.Content} (score={p.CreatedAt.Ticks})");
            Console.WriteLine($"  next_cursor = {page1.NextCursor}\n");

            // Page 2 — using cursor from page 1
            var page2 = feedSvc.GetFeed("reader", count: 5, cursor: page1.NextCursor);
            Console.WriteLine($"Page 2 ({page2.Posts.Count} posts):");
            foreach (var p in page2.Posts) Console.WriteLine($"  {p.Content}");
            Console.WriteLine($"  next_cursor = {page2.NextCursor}\n");

            // Page 3
            var page3 = feedSvc.GetFeed("reader", count: 5, cursor: page2.NextCursor);
            Console.WriteLine($"Page 3 ({page3.Posts.Count} posts):");
            foreach (var p in page3.Posts) Console.WriteLine($"  {p.Content}");

            // New post arrives — does NOT affect existing pages
            var newPost = posts.CreatePost("writer", "BREAKING: new post arrived!");
            fanOut.OnPost(newPost);
            Console.WriteLine($"\nNew post arrives. Re-reading page 2 with same cursor (no drift):");
            var page2Again = feedSvc.GetFeed("reader", count: 5, cursor: page1.NextCursor);
            Console.WriteLine($"  Page 2 still shows same {page2Again.Posts.Count} posts: {string.Join(", ", page2Again.Posts.Select(p => p.Content))}");

            Console.WriteLine();
        }

        // ── Scenario 4: Algorithmic Ranking vs Chronological ─────────────────

        static void Scenario4_AlgorithmicRanking()
        {
            Console.WriteLine("─── Scenario 4: Algorithmic Ranking vs Chronological ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            graph.Follow("viewer", "alice");
            graph.Follow("viewer", "bob");
            graph.Follow("viewer", "carol");

            var now = DateTime.UtcNow;

            // Mix of old viral post and new posts
            var p1 = posts.CreatePost("alice", "Viral post from 3h ago", now.AddHours(-3), likes: 800, comments: 150, shares: 200);
            var p2 = posts.CreatePost("bob", "Decent post from 2h ago", now.AddHours(-2), likes: 40, comments: 5);
            var p3 = posts.CreatePost("carol", "Fresh post just now", now.AddMinutes(-5), likes: 2);
            var p4 = posts.CreatePost("alice", "Mildly interesting 1h ago", now.AddHours(-1), likes: 120, comments: 30);
            var p5 = posts.CreatePost("bob", "Old but viral post 6h ago", now.AddHours(-6), likes: 2000, comments: 500);

            foreach (var p in new[] { p1, p2, p3, p4, p5 }) fanOut.OnPost(p);

            // Affinity: viewer interacts with alice more than others
            var affinity = new Dictionary<string, double> { { "alice", 0.8 }, { "bob", 0.2 }, { "carol", 0.1 } };

            Console.WriteLine("All posts (raw):");
            foreach (var p in new[] { p1, p2, p3, p4, p5 })
                Console.WriteLine($"  {p.Content,-35} | eng={p.EngagementRaw,5} | score={FeedRanker.Score(p):F1}");

            var chronoFeed = feedSvc.GetFeed("viewer", count: 5, algorithmic: false);
            Console.WriteLine("\nChronological feed (newest first):");
            for (int i = 0; i < chronoFeed.Posts.Count; i++)
                Console.WriteLine($"  #{i + 1} {chronoFeed.Posts[i].Content}");

            var algoFeed = feedSvc.GetFeed("viewer", count: 5, algorithmic: true, authorAffinity: affinity);
            Console.WriteLine("\nAlgorithmic feed (engagement × decay × affinity):");
            for (int i = 0; i < algoFeed.Posts.Count; i++)
                Console.WriteLine($"  #{i + 1} {algoFeed.Posts[i].Content}");

            Console.WriteLine("  (Old viral post rises; fresh but unengaged post falls; alice's posts boosted by affinity)");
            Console.WriteLine();
        }

        // ── Scenario 5: Follow / Unfollow Feed Updates ────────────────────────

        static void Scenario5_FollowUnfollowFeedUpdate()
        {
            Console.WriteLine("─── Scenario 5: Follow / Unfollow Feed Updates ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            // Initial follows
            graph.Follow("bob", "alice");
            graph.Follow("bob", "carol");

            // Pre-existing posts from alice and carol
            var a1 = posts.CreatePost("alice", "Alice post 1", DateTime.UtcNow.AddMinutes(-30));
            var a2 = posts.CreatePost("alice", "Alice post 2", DateTime.UtcNow.AddMinutes(-20));
            var c1 = posts.CreatePost("carol", "Carol post 1", DateTime.UtcNow.AddMinutes(-15));
            foreach (var p in new[] { a1, a2, c1 }) fanOut.OnPost(p);

            Console.WriteLine($"Bob follows alice + carol. Feed size: {cache.GetFeedSize("bob")} posts");
            var feedBefore = feedSvc.GetFeed("bob", count: 10);
            Console.WriteLine("Bob's feed before unfollow:");
            foreach (var p in feedBefore.Posts) Console.WriteLine($"  {p}");

            // Bob unfollows carol → remove carol's posts from feed (background cleanup)
            Console.WriteLine("\nBob unfollows carol (async cleanup removes carol's posts from feed)");
            graph.Unfollow("bob", "carol");
            fanOut.CleanupOnUnfollow("bob", "carol");

            Console.WriteLine($"Feed size after cleanup: {cache.GetFeedSize("bob")} posts");
            var feedAfterUnfollow = feedSvc.GetFeed("bob", count: 10);
            Console.WriteLine("Bob's feed after unfollow:");
            foreach (var p in feedAfterUnfollow.Posts) Console.WriteLine($"  {p}");

            // Bob follows dave — backfill dave's recent posts into bob's feed
            var d1 = posts.CreatePost("dave", "Dave post 1 (old)", DateTime.UtcNow.AddHours(-2));
            var d2 = posts.CreatePost("dave", "Dave post 2 (recent)", DateTime.UtcNow.AddMinutes(-10));
            // Dave's posts exist in post store, but not yet in bob's feed

            Console.WriteLine("\nBob follows dave → backfill dave's recent posts into bob's feed");
            graph.Follow("bob", "dave");
            fanOut.BackfillOnFollow("bob", "dave", count: 5);

            Console.WriteLine($"Feed size after backfill: {cache.GetFeedSize("bob")} posts");
            var feedAfterFollow = feedSvc.GetFeed("bob", count: 10);
            Console.WriteLine("Bob's feed after following dave (dave's old posts now appear):");
            foreach (var p in feedAfterFollow.Posts) Console.WriteLine($"  {p}");

            Console.WriteLine();
        }
    }
}
