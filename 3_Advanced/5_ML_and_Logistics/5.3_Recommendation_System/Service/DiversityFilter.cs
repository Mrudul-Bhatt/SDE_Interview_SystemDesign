// DiversityFilter — break out of filter bubbles via MMR + category quotas.
//
// SelectDiverse implements Maximal Marginal Relevance (MMR):
//   mmr = λ × relevance - (1-λ) × max_similarity_to_already_picked
//
// Greedy: at each step, pick the candidate that maximizes mmr. Items that are
// highly similar to ones already picked get penalized, so the final list
// covers more "ground" in the embedding space. λ=0.5 balances relevance and
// diversity; λ=1.0 reduces to pure relevance, λ=0.0 to maximum diversity.
//
// EnforceCategoryLimit is a hard constraint applied after MMR — useful when
// product wants "at most 2 cooking videos in the top 10" regardless of how
// strong the embedding-level diversity is.

using System.Collections.Generic;
using System.Linq;

public class DiversityFilter
{
    private const double Lambda = 0.5;

    // Greedy MMR selection from ranked candidates using embedding similarity
    public List<string> SelectDiverse(
        List<(string itemId, double score)> ranked,
        Dictionary<string, double[]> embeddings,
        int k)
    {
        var selected = new List<string>();
        var pool     = ranked.ToList();

        while (selected.Count < k && pool.Count > 0)
        {
            double bestMmr = double.MinValue;
            string bestItem = null;

            foreach (var (itemId, score) in pool)
            {
                // Maximum similarity to any already-selected item
                double maxSim = selected.Count == 0 ? 0
                    : selected.Where(s => embeddings.ContainsKey(s) && embeddings.ContainsKey(itemId))
                              .Select(s => Similarity.Cosine(embeddings[s], embeddings[itemId]))
                              .DefaultIfEmpty(0)
                              .Max();

                double mmr = Lambda * score - (1 - Lambda) * maxSim;
                if (mmr > bestMmr) { bestMmr = mmr; bestItem = itemId; }
            }

            if (bestItem == null) break;
            selected.Add(bestItem);
            pool.RemoveAll(t => t.itemId == bestItem);
        }

        return selected;
    }

    // Category diversity: at most maxPerCategory items of same category
    public List<string> EnforceCategoryLimit(
        List<string> itemIds, Dictionary<string, string> categories, int maxPerCategory = 2)
    {
        var counts  = new Dictionary<string, int>();
        var result  = new List<string>();
        foreach (var id in itemIds)
        {
            var cat = categories.GetValueOrDefault(id, "unknown");
            if (!counts.ContainsKey(cat)) counts[cat] = 0;
            if (counts[cat] < maxPerCategory)
            {
                result.Add(id);
                counts[cat]++;
            }
        }
        return result;
    }
}
