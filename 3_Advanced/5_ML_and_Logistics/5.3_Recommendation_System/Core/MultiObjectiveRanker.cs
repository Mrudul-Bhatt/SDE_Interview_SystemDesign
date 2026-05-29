// MultiObjectiveRanker — weighted linear blend of feature signals.
//
// Production systems use XGBoost or a small neural net here; the linear blend
// is the conceptual model — pick weights that reflect business priorities:
//   - Heaviest weight on Relevance (retrieval already filtered for matches)
//   - Popularity as a "wisdom of crowds" prior
//   - Freshness so new content can ever break through
//   - UserAffinity for personalization
//
// Tuning these weights is itself an A/B-tested decision. The numbers here are
// illustrative; YouTube and Netflix learn them per surface.

using System.Collections.Generic;
using System.Linq;

public static class MultiObjectiveRanker
{
    private const double WRelevance  = 0.50;
    private const double WPopularity = 0.20;
    private const double WFreshness  = 0.15;
    private const double WAffinity   = 0.15;

    public static double Score(RankingFeatures f) =>
        WRelevance  * f.Relevance  +
        WPopularity * f.Popularity +
        WFreshness  * f.Freshness  +
        WAffinity   * f.UserAffinity;

    public static List<RankingFeatures> Rank(IEnumerable<RankingFeatures> candidates) =>
        candidates.OrderByDescending(Score).ToList();
}
