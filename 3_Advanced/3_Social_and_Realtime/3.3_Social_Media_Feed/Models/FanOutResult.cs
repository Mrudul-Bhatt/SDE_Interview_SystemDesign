// FanOutResult — the receipt handed back after a post is published.
//
// THE BIG IDEA:
// When a user posts, two very different things can happen depending on their
// follower count. FanOutResult tells the caller which path was taken and how
// much work was done. Think of it as a delivery report:
//
//   Regular user (few followers) — fan-out on WRITE:
//     Post published → immediately copied into every follower's feed cache.
//     FanOutCount = N (number of caches updated right now).
//     WasCelebrity = false.
//     Next feed read for any follower is instant — data already in cache.
//
//   Celebrity (millions of followers) — skip fan-out, pull on READ:
//     Post published → stored in PostStore only. Zero cache writes.
//     FanOutCount = 0.
//     WasCelebrity = true.
//     Next feed read merges celebrity posts in fresh from PostStore.
//
// WHY THE TWO PATHS EXIST:
// Pushing Beyoncé's post to 200 million feed caches synchronously would take
// minutes and flood Redis with writes — the system would fall over. The celebrity
// threshold (typically ~10 000 followers) is the point where fan-out on write
// becomes more expensive than fan-out on read. Below the threshold, write-time
// fan-out is cheap and makes reads fast. Above it, skip the fan-out and pay
// a small extra cost on every read instead.
//
// HOW CALLERS USE THIS:
// The result is primarily for observability — tests assert WasCelebrity matches
// expectations, monitoring dashboards track average FanOutCount to catch
// follower-graph anomalies, and the demo prints it to show which path ran.
// The post is already persisted before this result is returned; the result is
// informational, not a gate on anything.

namespace AdvancedDesigns
{
    public class FanOutResult
    {
        // Which post triggered this fan-out. Used to correlate the result
        // with the post in logs and tests.
        public string PostId { get; }

        // Who wrote the post. The celebrity check is done on this ID against
        // the FollowGraph — if their follower count exceeds the threshold,
        // WasCelebrity is true and no feed caches are updated.
        public string AuthorId { get; }

        // True when the celebrity path was taken — the post was NOT pushed to
        // any follower feed caches. Followers will pick up this post at read
        // time when FeedService merges celebrity posts into their feed.
        // False when the regular fan-out ran: every follower's cache was updated.
        public bool WasCelebrity { get; }

        // How many follower feed caches were written to. For regular users this
        // equals their follower count; for celebrities it is always 0. A sudden
        // drop to 0 for a previously non-celebrity author signals they crossed
        // the celebrity threshold — worth alerting on in production.
        public int FanOutCount { get; }

        public FanOutResult(string postId, string authorId, bool wasCelebrity, int fanOutCount)
        {
            PostId = postId;
            AuthorId = authorId;
            WasCelebrity = wasCelebrity;
            FanOutCount = fanOutCount;
        }
    }
}
