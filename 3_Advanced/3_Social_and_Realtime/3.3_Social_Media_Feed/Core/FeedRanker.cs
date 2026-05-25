// FeedRanker — scores posts using a HackerNews-style time-decay formula.
//
// Formula: score = (engagement × affinityBoost) / (ageHours + 1)^gravity
// + decay × 50  ← floor so brand-new posts with 0 engagement still appear.
//
// Why gravity=1.8: higher gravity means fresher posts dominate more aggressively.
// HN uses 1.8; Twitter-style feeds lean higher (~2.0); long-lived content
// platforms (Reddit) lean lower (~1.2). Tuned via A/B experiments.
//
// Why affinity boost: users interact more with certain authors. Amplifying those
// posts keeps the feed relevant without requiring a full ML ranking model.

namespace AdvancedDesigns
{
    public static class FeedRanker
    {
        private const double Gravity = 1.8;

        public static double Score(Post post, double affinityBoost = 1.0)
        {
            double ageHours = (DateTime.UtcNow - post.CreatedAt).TotalHours;
            double decay    = 1.0 / Math.Pow(ageHours + 1, Gravity);
            return post.EngagementRaw * decay * affinityBoost + decay * 50;
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
}
