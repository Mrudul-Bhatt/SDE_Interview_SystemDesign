// FollowGraphRedis — the directed social graph: who follows whom.
//
// THE BIG IDEA:
// Think of the follow graph like a city's one-way street map. "Alice follows Bob"
// means there's a one-way street from Alice → Bob. Alice sees Bob's posts; Bob
// does NOT automatically see Alice's posts. This is unlike a friendship graph
// (Facebook) where the edge is bidirectional — Twitter/Instagram style is directed.
//
// Internally the graph is stored in TWO dictionaries:
//
//   _followers["bob"]  = { alice, carol, dave, ... }   ← "who can see bob's posts?"
//   _following["alice"] = { bob, eve, frank, ... }     ← "whose posts does alice see?"
//
// WHY TWO DICTIONARIES (not one)?
// Both lookup directions are on the critical path with different callers:
//
//   Fan-out on WRITE (publishing):
//     "Alice just posted — which followers' caches do I update?"
//     → needs _followers["alice"]   O(1) lookup, no scan
//
//   Feed read path:
//     "Show Bob his feed — which authors does he follow?"
//     → needs _following["bob"]     O(1) lookup, no scan
//
// If you stored only one direction you'd have to scan ALL keys on every call to
// answer the other question — O(N) across the entire user base.
// In production this is two separate Redis key spaces:
//   followers:{userId}   → SADD / SCARD / SMEMBERS
//   following:{userId}   → SADD / SREM / SMEMBERS
//
// THE CELEBRITY THRESHOLD — why it exists:
// When Bob (10M followers) posts, naive fan-out would write his PostId to 10 million
// feed caches. At ~1 µs per Redis write that's 10 seconds of blocking writes for
// ONE post. Instead, above the threshold we skip all cache writes and let each
// follower pull Bob's latest posts at read time — "hybrid fan-out" (see FanOutService).
// The threshold is the crossover point where read-time pull becomes cheaper than
// write-time push. Demo uses 10 for clarity; production is typically 100k–1M
// (depends on write-to-read ratio and Redis capacity).

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class FollowGraphRedis
    {
        // "who follows X?" — keyed by the person being followed.
        // Used exclusively by the fan-out path: given an author, get their audience.
        private readonly Dictionary<string, HashSet<string>> _followers = [];

        // "who does X follow?" — keyed by the follower.
        // Used exclusively by the feed read path: given a reader, get the authors
        // whose posts should appear in their feed.
        private readonly Dictionary<string, HashSet<string>> _following = [];

        // Users with follower count >= this are treated as celebrities and bypass
        // write-time fan-out. Their posts are pulled at read time instead.
        // Set to 10 for demo; real value is a config knob tuned per cluster load.
        private const int CelebrityThreshold = 10;

        // Records that followerId now follows targetId. Both dictionaries must be
        // updated atomically — an inconsistency (one updated, other not) would
        // mean fan-out delivers to a follower the feed doesn't know about, or
        // vice versa. In production this is a Lua script or Redis transaction.
        public void Follow(string followerId, string targetId)
        {
            GetOrCreate(_followers, targetId).Add(followerId);
            GetOrCreate(_following, followerId).Add(targetId);
        }

        // Mirror of Follow — both dicts must be updated together for the same reason.
        // Unfollow does NOT retroactively remove already-cached FeedEntries from the
        // follower's feed cache. Those expire or fall off naturally as new posts push
        // them out. This is the standard tradeoff: stale entries for a short window
        // are acceptable; synchronous cache invalidation across millions of entries is not.
        public void Unfollow(string followerId, string targetId)
        {
            GetOrCreate(_followers, targetId).Remove(followerId);
            GetOrCreate(_following, followerId).Remove(targetId);
        }

        // "Who needs to receive this author's post?" — the fan-out input list.
        // Returns an empty set (not null) if the author has zero followers, so
        // callers can iterate safely without a null check.
        public IEnumerable<string> GetFollowers(string userId) =>
            _followers.TryGetValue(userId, out var s) ? s : Enumerable.Empty<string>();

        // "Which authors' posts should appear in this reader's feed?" — the read-path input.
        // Returned set is split into celebrity vs regular buckets by the two helpers below.
        public IEnumerable<string> GetFollowing(string userId) =>
            _following.TryGetValue(userId, out var s) ? s : Enumerable.Empty<string>();

        // O(1): the HashSet tracks its own count, so this is just a .Count property read.
        // Called by IsCelebrity on EVERY fan-out decision, so it must be fast.
        public int GetFollowerCount(string userId) =>
            _followers.TryGetValue(userId, out var s) ? s.Count : 0;

        // The gate for the hybrid fan-out decision. FanOutService calls this once per
        // author before deciding whether to push to caches or skip. If true, the post
        // is stored in PostStoreCassandra only and pulled at read time by GetCelebrityFollows.
        public bool IsCelebrity(string userId) => GetFollowerCount(userId) >= CelebrityThreshold;

        // Returns the subset of people Bob follows who are celebrities. Used by
        // FeedService at read time to fetch each celebrity's recent posts from
        // PostStoreCassandra and merge them into Bob's feed alongside his pre-computed cache.
        // In production this set is also cached (Redis SET) because it changes rarely.
        public IEnumerable<string> GetCelebrityFollows(string userId) =>
            GetFollowing(userId).Where(IsCelebrity);

        // Returns the subset of people Bob follows who are NOT celebrities — the authors
        // whose posts were already pushed into Bob's feed cache at write time. FeedService
        // does NOT need to fetch these from PostStoreCassandra; their PostIds are already in FeedCache.
        public IEnumerable<string> GetRegularFollows(string userId) =>
            GetFollowing(userId).Where(f => !IsCelebrity(f));

        // Lazy initialisation: creates an empty HashSet on first access rather than
        // pre-populating all possible keys. This keeps memory proportional to actual
        // edges in the graph, not the full user × user space.
        private static HashSet<string> GetOrCreate(Dictionary<string, HashSet<string>> dict, string key)
        {
            if (!dict.ContainsKey(key)) dict[key] = [];
            return dict[key];
        }
    }
}
