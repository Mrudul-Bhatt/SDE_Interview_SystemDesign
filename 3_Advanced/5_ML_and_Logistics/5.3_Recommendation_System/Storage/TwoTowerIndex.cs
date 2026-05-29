// TwoTowerIndex — pre-computed item embeddings + nearest-neighbour search.
//
// In production this is FAISS or ScaNN — an Approximate Nearest Neighbour
// index that finds the top-K nearest item vectors to a query vector in under
// 10ms across 100M items. The trick: HNSW or IVF index structures that trade
// 99% accuracy for 1000× speedup over exact search.
//
// Here we do a linear scan, which is fine for the demo but would be hopeless
// at production scale. The architectural point — pre-compute item vectors
// offline, only the user vector is computed at request time — still holds.

using System.Collections.Generic;
using System.Linq;

public class TwoTowerIndex
{
    private readonly Dictionary<string, double[]> _itemVectors = new Dictionary<string, double[]>();
    private readonly int _dim;

    public TwoTowerIndex(int dim = 8) { _dim = dim; }

    public void AddItem(string itemId, double[] vector) => _itemVectors[itemId] = vector;

    // ANN search: return top-K items nearest to query vector
    public List<(string itemId, double score)> Search(double[] userVector, int k = 20)
    {
        return _itemVectors
            .Select(kv => (itemId: kv.Key, score: Similarity.Cosine(userVector, kv.Value)))
            .OrderByDescending(t => t.score)
            .Take(k)
            .ToList();
    }
}
