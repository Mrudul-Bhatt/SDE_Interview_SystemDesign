// FollowGraph — bidirectional follow relationships (mirrors Redis SET operations).
//
// Why two dictionaries (_followers + _following): both directions are queried
// frequently. Fan-out needs "who follows X?" (followers); feed read needs
// "who does X follow?" (following). A single direction would require a full scan
// of the other on every operation.
//
// Celebrity threshold: users above this follower count skip write-time fan-out.
// Their posts are pulled on demand at read time instead (hybrid fan-out model).
// Set to 10 here for demo clarity; production systems use ~1-10 million.

namespace AdvancedDesigns
{
    public class FollowGraph
    {
        private readonly Dictionary<string, HashSet<string>> _followers = new();
        private readonly Dictionary<string, HashSet<string>> _following = new();
        private const int CelebrityThreshold = 10;

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

        // Split following list into two buckets for the hybrid fan-out read path.
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
}
