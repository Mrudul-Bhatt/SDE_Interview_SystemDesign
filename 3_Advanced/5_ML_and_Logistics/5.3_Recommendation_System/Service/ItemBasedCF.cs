// ItemBasedCF — predict ratings by looking at similar items the user has rated.
//
// Two-phase usage:
//   1. BuildSimilarityIndex (offline, run periodically): O(items² × users)
//      — computes cosine similarity between every pair of items based on the
//      pattern of users who rated them. The result is a static index that
//      doesn't depend on the active user.
//   2. PredictRating / Recommend (per request): O(k × log items)
//      — for a candidate item, find the K items most similar to it that the
//      user has actually rated, then take the similarity-weighted average of
//      the user's ratings on those K items.
//
// Item-item CF is preferred over user-user CF because item similarities are
// MORE STABLE — they change slowly as the catalog evolves, while user
// similarities shift on every new interaction.

using System;
using System.Collections.Generic;
using System.Linq;

public class ItemBasedCF
{
    private readonly InteractionMatrix _matrix;
    private Dictionary<string, Dictionary<string, double>> _itemSim;

    public ItemBasedCF(InteractionMatrix matrix) { _matrix = matrix; }

    public void BuildSimilarityIndex()
    {
        var allItems = _matrix.GetAllItems().ToList();
        var allUsers = _matrix.GetAllUsers().ToList();

        // Build item → { user → rating } for similarity calculation
        var itemVectors = allItems.ToDictionary(
            item => item,
            item => allUsers.Where(u => _matrix.Get(u, item) > 0)
                            .ToDictionary(u => u, u => _matrix.Get(u, item))
        );

        _itemSim = new Dictionary<string, Dictionary<string, double>>();
        foreach (var i in allItems)
        {
            _itemSim[i] = new Dictionary<string, double>();
            foreach (var j in allItems)
            {
                if (i != j)
                    _itemSim[i][j] = Similarity.Cosine(itemVectors[i], itemVectors[j]);
            }
        }

        Console.WriteLine($"  [ItemCF] Similarity index built for {allItems.Count} items");
    }

    // Predict rating for user u on item i using K most similar rated items
    public double PredictRating(string userId, string candidateItem, int k = 5)
    {
        if (_itemSim == null) throw new InvalidOperationException("Call BuildSimilarityIndex first");

        var userRatings = _matrix.GetUserRatings(userId);
        if (!_itemSim.ContainsKey(candidateItem)) return 0;

        var sims = _itemSim[candidateItem];
        var topK = userRatings
            .Where(kv => sims.ContainsKey(kv.Key))
            .Select(kv => (item: kv.Key, rating: kv.Value, sim: sims[kv.Key]))
            .Where(t => t.sim > 0)
            .OrderByDescending(t => t.sim)
            .Take(k)
            .ToList();

        if (!topK.Any()) return 0;

        double numerator   = topK.Sum(t => t.sim * t.rating);
        double denominator = topK.Sum(t => Math.Abs(t.sim));
        return denominator == 0 ? 0 : numerator / denominator;
    }

    // Return top-N recommended items for user (excluding already-rated)
    public List<(string itemId, double score)> Recommend(string userId, int n = 10)
    {
        var rated      = _matrix.GetRatedItems(userId);
        var candidates = _matrix.GetAllItems().Where(i => !rated.Contains(i));

        return candidates
            .Select(i => (itemId: i, score: PredictRating(userId, i)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .Take(n)
            .ToList();
    }

    public List<(string itemId, double sim)> SimilarItems(string itemId, int k = 5)
    {
        if (!_itemSim.ContainsKey(itemId)) return new List<(string, double)>();
        return _itemSim[itemId]
            .OrderByDescending(kv => kv.Value)
            .Take(k)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
