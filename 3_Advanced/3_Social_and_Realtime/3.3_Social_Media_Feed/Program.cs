// Program — entry point for all Social Media Feed demo scenarios.
//
// Each scenario builds a fresh, empty system (BuildSystem) and then acts out a story
// — people follow each other, post, read their feeds — so you can watch one feature
// of the design in isolation.
//
// Scenario 1 — Basic Fan-out:    a regular user posts → it lands in followers' feeds.
//                                Shows the push (fan-out on write) path end to end.
// Scenario 2 — Celebrity Hybrid: a big account posts → push is SKIPPED, post is pulled
//                                at read time instead. Shows the push/pull split.
// Scenario 3 — Cursor Pagination: read a feed page by page; prove a new post arriving
//                                doesn't shift or duplicate already-seen pages.
// Scenario 4 — Ranking:          compare chronological vs algorithmic ordering — an
//                                old-but-viral post outranks a fresh-but-boring one.
// Scenario 5 — Follow/Unfollow:  unfollow scrubs an author's posts; follow backfills
//                                a new author's recent posts into your feed.

using System;
using System.Collections.Generic;
using System.Linq;

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

        // Wires up one complete, empty system: the follow graph, post storage, the
        // feed cache, and the two services that read/write them. Returns all five so
        // a scenario can drive them. Each scenario calls this to start clean, so they
        // never interfere with each other.
        static (FollowGraphRedis, PostStoreCassandra, FeedCacheRedis, FanOutService, FeedService) BuildSystem()
        {
            var graph = new FollowGraphRedis();
            var postStore = new PostStoreCassandra();
            var feedCache = new FeedCacheRedis();
            // Both services share the SAME graph/cache/store instances — that shared
            // state is how a write (fan-out) becomes visible to a later read (feed).
            var fanOut = new FanOutService(graph, feedCache, postStore);
            var feedSvc = new FeedService(graph, feedCache, postStore);
            return (graph, postStore, feedCache, fanOut, feedSvc);
        }

        // ── Scenario 1: Basic Fan-out on Write ────────────────────────────────
        // The simplest end-to-end story. Three follow relationships, a few posts, then
        // we read each person's feed. Watch how a post by Alice automatically appears
        // in Bob's and Carol's feeds (they follow her) but NOT in Dave's (he doesn't).
        static void Scenario1_BasicFanOutAndFeedRead()
        {
            Console.WriteLine("─── Scenario 1: Basic Fan-out on Write ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            // Set up the follow graph: Follow(follower, target).
            // bob & carol follow alice; dave follows bob.
            graph.Follow("bob", "alice");
            graph.Follow("carol", "alice");
            graph.Follow("dave", "bob");

            Console.WriteLine("Follow graph: bob→alice, carol→alice, dave→bob");
            Console.WriteLine($"Alice has {graph.GetFollowerCount("alice")} followers (regular user, threshold=10)\n");

            // Create a post (stores it) then fan it out (pushes its ID to follower feeds).
            // Alice has 2 followers, so we expect FanOutCount = 2.
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
        // The key scaling idea. "techguru" crosses the celebrity threshold (10), so
        // their post is NOT pushed to follower feeds (FanOutCount = 0). A regular user
        // (alice) IS pushed. Then when u1 reads their feed, FeedService merges alice's
        // pushed post with techguru's pulled-at-read-time post — the hybrid in action.
        static void Scenario2_CelebrityHybridFanOut()
        {
            Console.WriteLine("─── Scenario 2: Celebrity Hybrid Fan-out (threshold = 10 followers) ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();

            // Give techguru 12 followers to push them over the celebrity threshold (10).
            string[] fans = ["u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u9", "u10", "u11", "u12"];
            foreach (var fan in fans) graph.Follow(fan, "techguru");

            Console.WriteLine($"techguru has {graph.GetFollowerCount("techguru")} followers");
            Console.WriteLine($"Is celebrity? {graph.IsCelebrity("techguru")} (threshold=10)");

            graph.Follow("u1", "alice");
            graph.Follow("u2", "alice");

            var alicePost = posts.CreatePost("alice", "Regular user post", likes: 5);
            var aliceResult = fanOut.OnPost(alicePost);
            Console.WriteLine($"\nalice posts → fan-out to {aliceResult.FanOutCount} followers (IS written to feed cache)");

            var celebPost = posts.CreatePost("techguru", "Big announcement! New product launch.", likes: 500, comments: 200);
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
        // Proves the anti-drift property of cursors. We read a 15-post feed in pages of
        // 5, then a NEW post arrives at the top — and re-reading an earlier page with the
        // same cursor returns the exact same posts (no duplicates, no skips). An offset
        // ("skip 5") would have shifted and shown a duplicate here.
        static void Scenario3_CursorPagination()
        {
            Console.WriteLine("─── Scenario 3: Cursor-Based Pagination ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();
            graph.Follow("reader", "writer");

            // Create 15 posts with steadily increasing timestamps (10 min apart) so the
            // feed order is predictable and we can eyeball the paging.
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

            // A brand-new post lands at the TOP of the feed...
            var newPost = posts.CreatePost("writer", "BREAKING: new post arrived!");
            fanOut.OnPost(newPost);
            Console.WriteLine($"\nNew post arrives. Re-reading page 2 with same cursor (no drift):");
            // ...but re-reading page 2 with the SAME cursor still returns the same posts.
            // The cursor points at a fixed timestamp, so new posts above it don't shift it.
            var page2Again = feedSvc.GetFeed("reader", count: 5, cursor: page1.NextCursor);
            Console.WriteLine($"  Page 2 still shows same {page2Again.Posts.Count} posts: {string.Join(", ", page2Again.Posts.Select(p => p.Content))}");

            Console.WriteLine();
        }

        // ── Scenario 4: Algorithmic Ranking vs Chronological ─────────────────
        // Shows WHY apps don't just sort by time. The same 5 posts are ordered two ways:
        // chronological (pure newest-first) and algorithmic (engagement × time-decay ×
        // affinity). Under ranking, an old-but-hugely-popular post can beat a fresh-but-
        // ignored one, and posts from authors the viewer likes get boosted.
        static void Scenario4_AlgorithmicRanking()
        {
            Console.WriteLine("─── Scenario 4: Algorithmic Ranking vs Chronological ───");

            var (graph, posts, cache, fanOut, feedSvc) = BuildSystem();
            graph.Follow("viewer", "alice");
            graph.Follow("viewer", "bob");
            graph.Follow("viewer", "carol");

            // Deliberately varied posts: different ages AND different engagement, so the
            // two ordering modes produce visibly different results.
            var now = DateTime.UtcNow;
            var p1 = posts.CreatePost("alice", "Viral post from 3h ago", now.AddHours(-3), likes: 800, comments: 150, shares: 200);
            var p2 = posts.CreatePost("bob", "Decent post from 2h ago", now.AddHours(-2), likes: 40, comments: 5);
            var p3 = posts.CreatePost("carol", "Fresh post just now", now.AddMinutes(-5), likes: 2);
            var p4 = posts.CreatePost("alice", "Mildly interesting 1h ago", now.AddHours(-1), likes: 120, comments: 30);
            var p5 = posts.CreatePost("bob", "Old but viral post 6h ago", now.AddHours(-6), likes: 2000, comments: 500);
            foreach (var p in new[] { p1, p2, p3, p4, p5 }) fanOut.OnPost(p);

            // Affinity = how much THIS viewer likes each author (a personalization boost).
            // viewer strongly prefers alice (0.8) over bob (0.2) and carol (0.1).
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
        // Shows how the feed reacts to graph changes. After Bob unfollows Carol, her
        // posts are scrubbed from his feed (cleanup). After Bob follows Dave, Dave's
        // EXISTING recent posts are injected into Bob's feed (backfill) — so the feed
        // isn't empty until Dave next posts. Watch the feed size change at each step.
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

            // Dave posts these BEFORE Bob follows him — so normal fan-out never reached
            // Bob. Backfill (below) is what pulls these existing posts into Bob's feed.
            posts.CreatePost("dave", "Dave post 1 (old)", DateTime.UtcNow.AddHours(-2));
            posts.CreatePost("dave", "Dave post 2 (recent)", DateTime.UtcNow.AddMinutes(-10));

            Console.WriteLine("\nBob follows dave → backfill dave's recent posts into bob's feed");
            graph.Follow("bob", "dave");
            // Backfill injects Dave's already-existing posts so Bob's feed isn't empty
            // until Dave's next post.
            fanOut.BackfillOnFollow("bob", "dave", count: 5);

            Console.WriteLine($"Feed size after backfill: {cache.GetFeedSize("bob")} posts");
            Console.WriteLine("Bob's feed after following dave (dave's old posts now appear):");
            foreach (var p in feedSvc.GetFeed("bob", count: 10).Posts) Console.WriteLine($"  {p}");

            Console.WriteLine();
        }
    }
}
