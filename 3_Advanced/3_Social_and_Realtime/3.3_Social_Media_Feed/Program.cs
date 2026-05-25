// Program — entry point for all Social Media Feed demo scenarios.

namespace AdvancedDesigns
{
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

        static (FollowGraph, PostStore, FeedCache, FanOutService, FeedService) BuildSystem()
        {
            var graph     = new FollowGraph();
            var postStore = new PostStore();
            var feedCache = new FeedCache();
            var fanOut    = new FanOutService(graph, feedCache, postStore);
            var feedSvc   = new FeedService(graph, feedCache, postStore);
            return (graph, postStore, feedCache, fanOut, feedSvc);
        }

        // ── Scenario 1: Basic Fan-out on Write ────────────────────────────────

        static void Scenario1_BasicFanOutAndFeedRead()
        {
            Console.WriteLine("─── Scenario 1: Basic Fan-out on Write ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            graph.Follow("bob",   "alice");
            graph.Follow("carol", "alice");
            graph.Follow("dave",  "bob");

            Console.WriteLine("Follow graph: bob→alice, carol→alice, dave→bob");
            Console.WriteLine($"Alice has {graph.GetFollowerCount("alice")} followers (regular user, threshold=10)\n");

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

            Console.WriteLine($"\nBob's feed ({cache.GetFeedSize("bob")} entries):");
            foreach (var p in feedSvc.GetFeed("bob", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine($"\nCarol's feed ({cache.GetFeedSize("carol")} entries):");
            foreach (var p in feedSvc.GetFeed("carol", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine($"\nDave's feed ({cache.GetFeedSize("dave")} entries):");
            foreach (var p in feedSvc.GetFeed("dave", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine();
        }

        // ── Scenario 2: Celebrity Hybrid Fan-out ─────────────────────────────

        static void Scenario2_CelebrityHybridFanOut()
        {
            Console.WriteLine("─── Scenario 2: Celebrity Hybrid Fan-out (threshold = 10 followers) ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            string[] fans = ["u1","u2","u3","u4","u5","u6","u7","u8","u9","u10","u11","u12"];
            foreach (var fan in fans) graph.Follow(fan, "techguru");

            Console.WriteLine($"techguru has {graph.GetFollowerCount("techguru")} followers");
            Console.WriteLine($"Is celebrity? {graph.IsCelebrity("techguru")} (threshold=10)");

            graph.Follow("u1", "alice");
            graph.Follow("u2", "alice");

            var alicePost  = posts.CreatePost("alice", "Regular user post", likes: 5);
            var aliceResult = fanOut.OnPost(alicePost);
            Console.WriteLine($"\nalice posts → fan-out to {aliceResult.FanOutCount} followers (IS written to feed cache)");

            var celebPost   = posts.CreatePost("techguru", "Big announcement! New product launch.", likes: 500, comments: 200);
            var celebResult = fanOut.OnPost(celebPost);
            Console.WriteLine($"techguru posts → fan-out to {celebResult.FanOutCount} followers (celebrity: SKIPPED)");
            Console.WriteLine($"  (Post stored in DB only; pulled at read time)");

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

            var baseTime = DateTime.UtcNow.AddMinutes(-150);
            for (int i = 1; i <= 15; i++)
            {
                var p = posts.CreatePost("writer", $"Post #{i:D2}", baseTime.AddMinutes(i * 10));
                fanOut.OnPost(p);
            }

            Console.WriteLine("15 posts in feed. Reading in pages of 5:\n");

            var page1 = feedSvc.GetFeed("reader", count: 5);
            Console.WriteLine($"Page 1 ({page1.Posts.Count} posts):");
            foreach (var p in page1.Posts) Console.WriteLine($"  {p.Content} (score={p.CreatedAt.Ticks})");
            Console.WriteLine($"  next_cursor = {page1.NextCursor}\n");

            var page2 = feedSvc.GetFeed("reader", count: 5, cursor: page1.NextCursor);
            Console.WriteLine($"Page 2 ({page2.Posts.Count} posts):");
            foreach (var p in page2.Posts) Console.WriteLine($"  {p.Content}");
            Console.WriteLine($"  next_cursor = {page2.NextCursor}\n");

            var page3 = feedSvc.GetFeed("reader", count: 5, cursor: page2.NextCursor);
            Console.WriteLine($"Page 3 ({page3.Posts.Count} posts):");
            foreach (var p in page3.Posts) Console.WriteLine($"  {p.Content}");

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
            var p1 = posts.CreatePost("alice", "Viral post from 3h ago",    now.AddHours(-3),   likes: 800,  comments: 150, shares: 200);
            var p2 = posts.CreatePost("bob",   "Decent post from 2h ago",   now.AddHours(-2),   likes: 40,   comments: 5);
            var p3 = posts.CreatePost("carol", "Fresh post just now",        now.AddMinutes(-5), likes: 2);
            var p4 = posts.CreatePost("alice", "Mildly interesting 1h ago", now.AddHours(-1),   likes: 120,  comments: 30);
            var p5 = posts.CreatePost("bob",   "Old but viral post 6h ago", now.AddHours(-6),   likes: 2000, comments: 500);
            foreach (var p in new[] { p1, p2, p3, p4, p5 }) fanOut.OnPost(p);

            var affinity = new Dictionary<string, double> { { "alice", 0.8 }, { "bob", 0.2 }, { "carol", 0.1 } };

            Console.WriteLine("All posts (raw):");
            foreach (var p in new[] { p1, p2, p3, p4, p5 })
                Console.WriteLine($"  {p.Content,-35} | eng={p.EngagementRaw,5} | score={FeedRanker.Score(p):F1}");

            var chronoFeed = feedSvc.GetFeed("viewer", count: 5, algorithmic: false);
            Console.WriteLine("\nChronological feed (newest first):");
            for (int i = 0; i < chronoFeed.Posts.Count; i++)
                Console.WriteLine($"  #{i+1} {chronoFeed.Posts[i].Content}");

            var algoFeed = feedSvc.GetFeed("viewer", count: 5, algorithmic: true, authorAffinity: affinity);
            Console.WriteLine("\nAlgorithmic feed (engagement × decay × affinity):");
            for (int i = 0; i < algoFeed.Posts.Count; i++)
                Console.WriteLine($"  #{i+1} {algoFeed.Posts[i].Content}");

            Console.WriteLine("  (Old viral post rises; fresh but unengaged post falls; alice's posts boosted by affinity)");
            Console.WriteLine();
        }

        // ── Scenario 5: Follow / Unfollow Feed Updates ────────────────────────

        static void Scenario5_FollowUnfollowFeedUpdate()
        {
            Console.WriteLine("─── Scenario 5: Follow / Unfollow Feed Updates ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            graph.Follow("bob", "alice");
            graph.Follow("bob", "carol");

            var a1 = posts.CreatePost("alice", "Alice post 1", DateTime.UtcNow.AddMinutes(-30));
            var a2 = posts.CreatePost("alice", "Alice post 2", DateTime.UtcNow.AddMinutes(-20));
            var c1 = posts.CreatePost("carol", "Carol post 1", DateTime.UtcNow.AddMinutes(-15));
            foreach (var p in new[] { a1, a2, c1 }) fanOut.OnPost(p);

            Console.WriteLine($"Bob follows alice + carol. Feed size: {cache.GetFeedSize("bob")} posts");
            Console.WriteLine("Bob's feed before unfollow:");
            foreach (var p in feedSvc.GetFeed("bob", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine("\nBob unfollows carol (async cleanup removes carol's posts from feed)");
            graph.Unfollow("bob", "carol");
            fanOut.CleanupOnUnfollow("bob", "carol");

            Console.WriteLine($"Feed size after cleanup: {cache.GetFeedSize("bob")} posts");
            Console.WriteLine("Bob's feed after unfollow:");
            foreach (var p in feedSvc.GetFeed("bob", count: 10).Posts) Console.WriteLine($"  {p}");

            posts.CreatePost("dave", "Dave post 1 (old)",    DateTime.UtcNow.AddHours(-2));
            posts.CreatePost("dave", "Dave post 2 (recent)", DateTime.UtcNow.AddMinutes(-10));

            Console.WriteLine("\nBob follows dave → backfill dave's recent posts into bob's feed");
            graph.Follow("bob", "dave");
            fanOut.BackfillOnFollow("bob", "dave", count: 5);

            Console.WriteLine($"Feed size after backfill: {cache.GetFeedSize("bob")} posts");
            Console.WriteLine("Bob's feed after following dave (dave's old posts now appear):");
            foreach (var p in feedSvc.GetFeed("bob", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine();
        }
    }
}
