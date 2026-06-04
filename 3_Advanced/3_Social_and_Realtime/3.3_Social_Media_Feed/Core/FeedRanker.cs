// FeedRanker — turns raw post data into a ranked ordering using a time-decay formula.
//
// THE BIG IDEA:
// Think of FeedRanker like a newspaper editor who stacks the morning edition.
// Two stories compete for the front page: yesterday's viral story (1 000 likes)
// and today's breaking story (5 likes). The editor picks breaking news — recency
// beats raw popularity when the gap isn't enormous. FeedRanker encodes that
// editorial judgment as a math formula so it runs in microseconds for millions
// of posts.
//
// THE FORMULA:
//
//   score = (EngagementRaw × affinityBoost) × decay  +  decay × 50
//              ↑ "how interesting?"                       ↑ freshness floor
//
//   where:
//     EngagementRaw  = likes×2 + comments×3 + shares×5  (from Post)
//     decay          = 1 / (ageHours + 1)^gravity
//     gravity        = 1.8   (how fast old posts fall off)
//     affinityBoost  = 1.0 + authorAffinity × 0.5  (personalisation multiplier)
//
// Worked example — two posts competing for position 1:
//
//   Post A: 0 hours old,  0 engagement  → decay=1.0  → score = 0×1.0 + 1.0×50 = 50.0
//   Post B: 2 hours old, 20 engagement  → decay=0.17 → score = 20×0.17 + 0.17×50 = 3.4 + 8.5 = 11.9
//   Post C: 0 hours old, 20 engagement  → decay=1.0  → score = 20 + 50 = 70.0
//
//   Ranking: C > A > B  ← a brand-new post with no engagement (A=50) still beats
//   an older post with moderate engagement (B=11.9). That's the freshness floor at work.
//
// WHY GRAVITY = 1.8:
// Gravity controls how steeply scores drop with age:
//
//   gravity=1.2  → posts stay competitive for days    (Reddit — long-lived discussions)
//   gravity=1.8  → posts fall off within hours        (HackerNews — this value)
//   gravity=2.0  → posts become irrelevant in minutes (breaking-news Twitter feeds)
//
// HN uses 1.8 publicly. Real platforms tune this via A/B experiments: raise gravity
// → more churn, users see more new content; lower gravity → classics resurface,
// feels like "same posts again". There is no universally correct value.
//
// WHY THE + decay × 50 FLOOR:
// Without the floor, a brand-new post with zero likes/comments would score 0.0 and
// never appear above any post with even a single like, no matter how old. The floor
// injects a decaying baseline that gives new posts a fighting chance until they
// accumulate real engagement. The constant 50 is arbitrary — it represents "one
// unit of initial freshness" on the same scale as EngagementRaw.
//
// WHY AFFINITY BOOST (not a full ML model):
// A proper recommendation model (TikTok, YouTube) runs a neural network trained on
// each user's history. That requires GPUs, hours of training, and complex infra.
// Affinity boost is the lightweight approximation: if you've liked Alice's posts 80%
// of the time, bump her posts by 1 + 0.8×0.5 = 1.4×. No model training needed.
// The 0.5 scaling caps the boost at 1.5× max (when affinity=1.0) so one author
// can never completely crowd out everyone else.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public static class FeedRanker
    {
        // How fast scores decay with age. Higher = newer posts dominate more aggressively.
        // This is the single most impactful tuning knob for feed feel.
        private const double Gravity = 1.8;

        // Computes the ranking score for a single post.
        // affinityBoost = 1.0 means no personalisation (neutral); values above 1.0
        // amplify the score for authors the reader frequently engages with.
        // Called once per post per feed refresh — must be O(1) and allocation-free.
        public static double Score(Post post, double affinityBoost = 1.0)
        {
            double ageHours = (DateTime.UtcNow - post.CreatedAt).TotalHours;
            // decay approaches 0 as ageHours grows; the +1 prevents division by zero
            // at age=0 and ensures a fresh post with zero engagement scores decay×50 > 0.
            double decay = 1.0 / Math.Pow(ageHours + 1, Gravity);
            return post.EngagementRaw * decay * affinityBoost + decay * 50;
        }

        // Ranks a collection of posts highest-score-first.
        // authorAffinity maps AuthorId → [0,1] affinity score derived from the
        // reader's interaction history. Null means no personalisation (chronological
        // tie-breaking is still applied via the freshness floor).
        //
        // In production this runs on a pre-filtered candidate set (~hundreds of posts),
        // not the full corpus. The feed pipeline fetches candidates from FeedCache and
        // PostStore, then calls Rank to impose the final ordering before returning to
        // the client. Running it on millions of posts would be too slow.
        public static List<Post> Rank(IEnumerable<Post> posts,
            Dictionary<string, double> authorAffinity = null)
        {
            return posts
                .Select(p =>
                {
                    double affinity = 1.0;
                    // 1 + a×0.5 maps affinity [0,1] → boost [1.0, 1.5].
                    // Cap at 1.5× prevents a single heavily-liked author from
                    // monopolising the entire feed.
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
