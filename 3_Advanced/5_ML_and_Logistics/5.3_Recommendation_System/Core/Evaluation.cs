// Evaluation — offline ranking quality metrics.
//
// NDCG@K is the one to track most closely: unlike Precision@K, it REWARDS
// putting relevant items at the top of the list. A list with the relevant
// items at positions 1,2,3 scores higher than one with them at 8,9,10 — even
// though Precision@K is identical.
//
// Standard caveat: offline NDCG ≠ online engagement. Always A/B test before
// shipping a model that "wins" on NDCG. Online metrics (CTR, watch time,
// retention) are what actually drive product decisions.

using System;
using System.Collections.Generic;
using System.Linq;

public static class Evaluation
{
    // NDCG@K: normalized discounted cumulative gain
    public static double NdcgAtK(List<string> recommended, HashSet<string> relevant, int k)
    {
        double dcg = 0;
        int n = Math.Min(k, recommended.Count);
        for (int i = 0; i < n; i++)
            if (relevant.Contains(recommended[i]))
                dcg += 1.0 / Math.Log2(i + 2);  // i+2 because log base 2 of position (1-indexed)

        // Ideal DCG: all relevant items at top positions
        double idcg = 0;
        for (int i = 0; i < Math.Min(k, relevant.Count); i++)
            idcg += 1.0 / Math.Log2(i + 2);

        return idcg == 0 ? 0 : dcg / idcg;
    }

    public static double PrecisionAtK(List<string> recommended, HashSet<string> relevant, int k)
    {
        int hits = recommended.Take(k).Count(r => relevant.Contains(r));
        return (double)hits / Math.Min(k, recommended.Count);
    }

    public static double RecallAtK(List<string> recommended, HashSet<string> relevant, int k)
    {
        if (!relevant.Any()) return 0;
        int hits = recommended.Take(k).Count(r => relevant.Contains(r));
        return (double)hits / relevant.Count;
    }
}
