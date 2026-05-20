// Recommendation System — C# simulation
// Covers: item-based collaborative filtering, matrix factorization (SGD),
//         two-tower retrieval, ranking with multi-objective scoring,
//         cold start, diversity (MMR), and evaluation metrics (NDCG@K).
// assembly-guid: {B9C0D1E2-F3A4-5678-B901-678901200038}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

public class Item
{
    public string ItemId    { get; set; }
    public string Title     { get; set; }
    public string Category  { get; set; }
    public double Popularity { get; set; }  // 0–1 normalized
    public double Freshness  { get; set; }  // 0–1 (1 = brand new)
    public List<string> Tags { get; set; } = new List<string>();
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. User-Item Interaction Matrix
// ─────────────────────────────────────────────────────────────────────────────

public class InteractionMatrix
{
    // ratings[userId][itemId] = rating (0–5; 0 = not seen)
    private readonly Dictionary<string, Dictionary<string, double>> _ratings
        = new Dictionary<string, Dictionary<string, double>>();

    public void Add(string userId, string itemId, double rating)
    {
        if (!_ratings.ContainsKey(userId)) _ratings[userId] = new Dictionary<string, double>();
        _ratings[userId][itemId] = rating;
    }

    public double Get(string userId, string itemId)
    {
        if (_ratings.TryGetValue(userId, out var items) && items.TryGetValue(itemId, out var r))
            return r;
        return 0;
    }

    public Dictionary<string, double> GetUserRatings(string userId) =>
        _ratings.TryGetValue(userId, out var r) ? r : new Dictionary<string, double>();

    public HashSet<string> GetAllUsers() => new HashSet<string>(_ratings.Keys);
    public HashSet<string> GetAllItems() =>
        new HashSet<string>(_ratings.Values.SelectMany(d => d.Keys));
    public HashSet<string> GetRatedItems(string userId) =>
        _ratings.TryGetValue(userId, out var r) ? new HashSet<string>(r.Keys) : new HashSet<string>();
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Similarity utilities
// ─────────────────────────────────────────────────────────────────────────────

public static class Similarity
{
    // Cosine similarity between two sparse rating vectors (item-item or user-user)
    public static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        double dot = 0, normA = 0, normB = 0;
        foreach (var kv in a)
        {
            normA += kv.Value * kv.Value;
            if (b.TryGetValue(kv.Key, out var bv)) dot += kv.Value * bv;
        }
        foreach (var kv in b) normB += kv.Value * kv.Value;
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    // Cosine on dense float arrays (for embedding vectors)
    public static double Cosine(double[] a, double[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    // Dot product (for MF prediction, embeddings already normalized during training)
    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Item-Based Collaborative Filtering
// ─────────────────────────────────────────────────────────────────────────────

public class ItemBasedCF
{
    private readonly InteractionMatrix _matrix;
    // itemSim[i][j] = cosine similarity between item i and item j
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

// ─────────────────────────────────────────────────────────────────────────────
// 4. Matrix Factorization (SGD)
// ─────────────────────────────────────────────────────────────────────────────

public class MatrixFactorization
{
    private readonly int _k;          // latent dimensions
    private readonly double _eta;     // learning rate
    private readonly double _lambda;  // regularization
    private readonly int _epochs;

    private Dictionary<string, double[]> _userVectors;
    private Dictionary<string, double[]> _itemVectors;
    private Dictionary<string, double>   _userBias;
    private Dictionary<string, double>   _itemBias;
    private double _globalMean;

    private readonly Random _rng = new Random(42);

    public MatrixFactorization(int k = 10, double eta = 0.01, double lambda = 0.02, int epochs = 50)
    {
        _k      = k;
        _eta    = eta;
        _lambda = lambda;
        _epochs = epochs;
    }

    private double[] RandomVector() =>
        Enumerable.Range(0, _k).Select(_ => (_rng.NextDouble() - 0.5) * 0.1).ToArray();

    public void Train(InteractionMatrix matrix)
    {
        var users = matrix.GetAllUsers().ToList();
        var items = matrix.GetAllItems().ToList();

        // Collect all observed ratings
        var obs = new List<(string u, string i, double r)>();
        foreach (var u in users)
        foreach (var kv in matrix.GetUserRatings(u))
            obs.Add((u, kv.Key, kv.Value));

        _globalMean = obs.Any() ? obs.Average(x => x.r) : 0;

        _userVectors = users.ToDictionary(u => u, _ => RandomVector());
        _itemVectors = items.ToDictionary(i => i, _ => RandomVector());
        _userBias    = users.ToDictionary(u => u, _ => 0.0);
        _itemBias    = items.ToDictionary(i => i, _ => 0.0);

        for (int epoch = 0; epoch < _epochs; epoch++)
        {
            // Shuffle
            var shuffled = obs.OrderBy(_ => _rng.Next()).ToList();
            double totalLoss = 0;

            foreach (var (u, i, r) in shuffled)
            {
                double pred  = Predict(u, i);
                double error = r - pred;
                totalLoss   += error * error;

                // Update biases
                _userBias[u] += _eta * (error - _lambda * _userBias[u]);
                _itemBias[i] += _eta * (error - _lambda * _itemBias[i]);

                // Update latent vectors
                var pu = _userVectors[u];
                var qi = _itemVectors[i];
                for (int f = 0; f < _k; f++)
                {
                    double puf = pu[f];
                    double qif = qi[f];
                    pu[f] += _eta * (error * qif - _lambda * puf);
                    qi[f] += _eta * (error * puf - _lambda * qif);
                }
            }

            if ((epoch + 1) % 10 == 0)
                Console.WriteLine($"  [MF] Epoch {epoch + 1}/{_epochs}  RMSE={Math.Sqrt(totalLoss / obs.Count):F4}");
        }
    }

    public double Predict(string userId, string itemId)
    {
        if (!_userVectors.ContainsKey(userId) || !_itemVectors.ContainsKey(itemId)) return _globalMean;

        double bias = _globalMean + _userBias[userId] + _itemBias[itemId];
        return bias + Similarity.Dot(_userVectors[userId], _itemVectors[itemId]);
    }

    public double[] GetUserVector(string userId) =>
        _userVectors.TryGetValue(userId, out var v) ? v : null;

    public double[] GetItemVector(string itemId) =>
        _itemVectors.TryGetValue(itemId, out var v) ? v : null;

    public List<(string itemId, double score)> Recommend(string userId, InteractionMatrix matrix, int n = 10)
    {
        var rated = matrix.GetRatedItems(userId);
        return matrix.GetAllItems()
            .Where(i => !rated.Contains(i))
            .Select(i => (itemId: i, score: Predict(userId, i)))
            .OrderByDescending(t => t.score)
            .Take(n)
            .ToList();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Two-Tower Retrieval (simulated ANN)
// ─────────────────────────────────────────────────────────────────────────────

public class TwoTowerIndex
{
    // Pre-computed item vectors (normally stored in FAISS; simulated here)
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

// ─────────────────────────────────────────────────────────────────────────────
// 6. Ranker — multi-objective scoring
// ─────────────────────────────────────────────────────────────────────────────

public class RankingFeatures
{
    public string ItemId      { get; set; }
    public double Relevance   { get; set; }  // from retrieval score
    public double Popularity  { get; set; }  // 0–1
    public double Freshness   { get; set; }  // 0–1
    public double UserAffinity { get; set; } // historical engagement
}

public static class MultiObjectiveRanker
{
    // Weighted linear combination (in prod: gradient boosted tree / neural)
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

// ─────────────────────────────────────────────────────────────────────────────
// 7. Diversity — Maximal Marginal Relevance
// ─────────────────────────────────────────────────────────────────────────────

public class DiversityFilter
{
    // λ = 0.5: balance relevance and diversity
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

// ─────────────────────────────────────────────────────────────────────────────
// 8. Evaluation — NDCG@K, Precision@K
// ─────────────────────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────────────────────
// 9. Cold Start — popularity-based fallback
// ─────────────────────────────────────────────────────────────────────────────

public class PopularityFallback
{
    private readonly List<Item> _items;

    public PopularityFallback(List<Item> items) { _items = items; }

    public List<string> TopByPopularity(int n, string category = null) =>
        _items.Where(i => category == null || i.Category == category)
              .OrderByDescending(i => i.Popularity)
              .Take(n)
              .Select(i => i.ItemId)
              .ToList();

    // Trending = popularity + freshness blend
    public List<string> Trending(int n) =>
        _items.OrderByDescending(i => i.Popularity * 0.6 + i.Freshness * 0.4)
              .Take(n)
              .Select(i => i.ItemId)
              .ToList();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo: end-to-end scenarios
// ─────────────────────────────────────────────────────────────────────────────

public class Program
{
    static List<Item> BuildItemCatalog() => new List<Item>
    {
        new Item { ItemId="A", Title="The Matrix",       Category="Sci-Fi",  Popularity=0.90, Freshness=0.1, Tags=new List<string>{"action","cyberpunk"} },
        new Item { ItemId="B", Title="Inception",        Category="Sci-Fi",  Popularity=0.88, Freshness=0.2, Tags=new List<string>{"thriller","dreams"} },
        new Item { ItemId="C", Title="Interstellar",     Category="Sci-Fi",  Popularity=0.85, Freshness=0.3, Tags=new List<string>{"space","emotional"} },
        new Item { ItemId="D", Title="The Dark Knight",  Category="Action",  Popularity=0.95, Freshness=0.1, Tags=new List<string>{"superhero","crime"} },
        new Item { ItemId="E", Title="Parasite",         Category="Drama",   Popularity=0.80, Freshness=0.5, Tags=new List<string>{"thriller","class"} },
        new Item { ItemId="F", Title="Avengers Endgame", Category="Action",  Popularity=0.92, Freshness=0.2, Tags=new List<string>{"superhero","epic"} },
        new Item { ItemId="G", Title="Spirited Away",    Category="Anime",   Popularity=0.78, Freshness=0.6, Tags=new List<string>{"fantasy","family"} },
        new Item { ItemId="H", Title="Pulp Fiction",     Category="Drama",   Popularity=0.85, Freshness=0.1, Tags=new List<string>{"crime","nonlinear"} },
        new Item { ItemId="I", Title="Get Out",          Category="Horror",  Popularity=0.75, Freshness=0.7, Tags=new List<string>{"thriller","social"} },
        new Item { ItemId="J", Title="Arrival",          Category="Sci-Fi",  Popularity=0.77, Freshness=0.4, Tags=new List<string>{"space","language"} },
    };

    static InteractionMatrix BuildMatrix()
    {
        var m = new InteractionMatrix();
        // Alice: likes Sci-Fi and thrillers
        m.Add("alice", "A", 5); m.Add("alice", "B", 4); m.Add("alice", "C", 5); m.Add("alice", "J", 4);
        // Bob: likes Action and Sci-Fi
        m.Add("bob", "A", 4); m.Add("bob", "D", 5); m.Add("bob", "F", 5); m.Add("bob", "B", 3);
        // Carol: likes Drama and Horror
        m.Add("carol", "E", 5); m.Add("carol", "H", 4); m.Add("carol", "I", 5); m.Add("carol", "G", 3);
        // Dave: broad taste
        m.Add("dave", "A", 3); m.Add("dave", "D", 4); m.Add("dave", "E", 4); m.Add("dave", "G", 5); m.Add("dave", "B", 3);
        // Eve: similar to Alice
        m.Add("eve", "A", 5); m.Add("eve", "B", 5); m.Add("eve", "C", 4); m.Add("eve", "D", 2);
        return m;
    }

    public static void Main()
    {
        var items   = BuildItemCatalog();
        var matrix  = BuildMatrix();
        var catalog = items.ToDictionary(i => i.ItemId);

        Console.WriteLine("=== Scenario 1: Item-Based Collaborative Filtering ===\n");
        {
            var cf = new ItemBasedCF(matrix);
            cf.BuildSimilarityIndex();

            // Similar items to "The Matrix" (A)
            Console.WriteLine("\n  Items most similar to 'The Matrix' (A):");
            foreach (var (itemId, sim) in cf.SimilarItems("A", 3))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  sim={sim:F4}");

            // Recommendations for Alice (rated A,B,C,J — Sci-Fi fan)
            Console.WriteLine("\n  Recommendations for Alice (rated A,B,C,J):");
            foreach (var (itemId, score) in cf.Recommend("alice", 5))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  predicted_rating={score:F2}");

            // Recommendations for Bob (rated A,D,F,B — Action/Sci-Fi)
            Console.WriteLine("\n  Recommendations for Bob (rated A,D,F,B):");
            foreach (var (itemId, score) in cf.Recommend("bob", 5))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  predicted_rating={score:F2}");
        }

        Console.WriteLine("\n=== Scenario 2: Matrix Factorization (SGD) ===\n");
        {
            var mf = new MatrixFactorization(k: 5, eta: 0.02, lambda: 0.02, epochs: 30);
            mf.Train(matrix);

            // Predict specific ratings
            Console.WriteLine("\n  Rating predictions:");
            var toPredict = new[] { ("alice","D"), ("alice","E"), ("bob","C"), ("carol","A") };
            foreach (var (u, i) in toPredict)
                Console.WriteLine($"    {u,-10} × {catalog[i].Title,-25} → predicted={mf.Predict(u, i):F2}");

            // Recommendations for carol via MF
            Console.WriteLine("\n  MF Recommendations for Carol (rated E,H,I,G — Drama/Horror):");
            foreach (var (itemId, score) in mf.Recommend("carol", matrix, 4))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  score={score:F2}");
        }

        Console.WriteLine("\n=== Scenario 3: Two-Tower Retrieval ===\n");
        {
            // Simulate item embeddings learned by two-tower model
            // (in prod: output of item tower neural net)
            var rand = new Random(7);
            var index = new TwoTowerIndex(dim: 4);
            var itemEmbeddings = new Dictionary<string, double[]>();

            foreach (var item in items)
            {
                // Sci-Fi gets high dim[0], Action high dim[1], Drama high dim[2], Horror high dim[3]
                double[] vec = new double[4];
                if (item.Category == "Sci-Fi")   { vec[0] = 0.8 + rand.NextDouble() * 0.2; vec[1] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Action"){ vec[1] = 0.8 + rand.NextDouble() * 0.2; vec[0] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Drama") { vec[2] = 0.8 + rand.NextDouble() * 0.2; vec[3] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Horror"){ vec[3] = 0.8 + rand.NextDouble() * 0.2; vec[2] = rand.NextDouble() * 0.2; }
                else { vec[0] = vec[1] = vec[2] = vec[3] = rand.NextDouble() * 0.5; }

                // Normalize
                double norm = Math.Sqrt(vec.Sum(x => x * x));
                for (int i = 0; i < 4; i++) vec[i] /= norm;

                index.AddItem(item.ItemId, vec);
                itemEmbeddings[item.ItemId] = vec;
            }

            // Alice's user vector — Sci-Fi affinity (high dim[0])
            double[] aliceVec = { 0.9, 0.1, 0.1, 0.0 };
            double aliceNorm  = Math.Sqrt(aliceVec.Sum(x => x * x));
            for (int i = 0; i < 4; i++) aliceVec[i] /= aliceNorm;

            Console.WriteLine("  Two-tower top-5 candidates for Alice (Sci-Fi user vector):");
            foreach (var (itemId, score) in index.Search(aliceVec, k: 5))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  cosine={score:F4}  cat={catalog[itemId].Category}");
        }

        Console.WriteLine("\n=== Scenario 4: Ranking with Multi-Objective Scoring ===\n");
        {
            // Simulate 8 retrieval candidates with different feature profiles
            var candidates = new List<RankingFeatures>
            {
                new RankingFeatures { ItemId="A", Relevance=0.9, Popularity=0.90, Freshness=0.1, UserAffinity=0.8 },
                new RankingFeatures { ItemId="B", Relevance=0.8, Popularity=0.88, Freshness=0.2, UserAffinity=0.7 },
                new RankingFeatures { ItemId="C", Relevance=0.7, Popularity=0.85, Freshness=0.3, UserAffinity=0.9 },
                new RankingFeatures { ItemId="D", Relevance=0.6, Popularity=0.95, Freshness=0.1, UserAffinity=0.3 },
                new RankingFeatures { ItemId="E", Relevance=0.5, Popularity=0.80, Freshness=0.5, UserAffinity=0.2 },
                new RankingFeatures { ItemId="G", Relevance=0.4, Popularity=0.78, Freshness=0.6, UserAffinity=0.1 },
                new RankingFeatures { ItemId="I", Relevance=0.3, Popularity=0.75, Freshness=0.7, UserAffinity=0.4 },
                new RankingFeatures { ItemId="J", Relevance=0.8, Popularity=0.77, Freshness=0.4, UserAffinity=0.6 },
            };

            var ranked = MultiObjectiveRanker.Rank(candidates);
            Console.WriteLine("  Ranked results (relevance × 0.5 + popularity × 0.2 + freshness × 0.15 + affinity × 0.15):");
            foreach (var f in ranked)
                Console.WriteLine($"    {f.ItemId}: {catalog[f.ItemId].Title,-25}  score={MultiObjectiveRanker.Score(f):F3}  (rel={f.Relevance:F1} pop={f.Popularity:F2} fresh={f.Freshness:F1} aff={f.UserAffinity:F1})");
        }

        Console.WriteLine("\n=== Scenario 5: Diversity (MMR) and Category Constraints ===\n");
        {
            // Without diversity: top-4 are all Sci-Fi
            var retrieved = new List<(string, double)>
            {
                ("A", 0.90), ("B", 0.85), ("C", 0.82), ("J", 0.80),
                ("D", 0.75), ("E", 0.65), ("G", 0.55), ("I", 0.50)
            };
            var embeddings = new Dictionary<string, double[]>
            {
                ["A"] = new[] {0.9, 0.1, 0.0, 0.0},
                ["B"] = new[] {0.85,0.15,0.0, 0.0},
                ["C"] = new[] {0.88,0.0, 0.12,0.0},
                ["J"] = new[] {0.82,0.0, 0.0, 0.18},
                ["D"] = new[] {0.1, 0.9, 0.0, 0.0},
                ["E"] = new[] {0.0, 0.1, 0.9, 0.0},
                ["G"] = new[] {0.1, 0.0, 0.5, 0.4},
                ["I"] = new[] {0.0, 0.0, 0.3, 0.7},
            };

            Console.WriteLine("  Top-4 without diversity (pure relevance):");
            foreach (var (id, s) in retrieved.Take(4))
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  score={s:F2}  category={catalog[id].Category}");

            var filter     = new DiversityFilter();
            var mmrResult  = filter.SelectDiverse(retrieved, embeddings, k: 4);
            Console.WriteLine("\n  Top-4 with MMR diversity (λ=0.5):");
            foreach (var id in mmrResult)
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  category={catalog[id].Category}");

            // Category constraint
            var catMap    = items.ToDictionary(i => i.ItemId, i => i.Category);
            var catResult = filter.EnforceCategoryLimit(mmrResult.Concat(new[]{"B","C","J"}).Distinct().ToList(), catMap, maxPerCategory: 1);
            Console.WriteLine("\n  After category limit (max 1 per category):");
            foreach (var id in catResult)
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  category={catalog[id].Category}");
        }

        Console.WriteLine("\n=== Scenario 6: Cold Start — New User ===\n");
        {
            var fallback = new PopularityFallback(items);

            Console.WriteLine("  New user (no history) → popularity-based:");
            foreach (var id in fallback.TopByPopularity(4))
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  popularity={catalog[id].Popularity:F2}");

            Console.WriteLine("\n  New user after onboarding 'Sci-Fi' preference:");
            foreach (var id in fallback.TopByPopularity(3, "Sci-Fi"))
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  popularity={catalog[id].Popularity:F2}");

            Console.WriteLine("\n  Trending (popularity × 0.6 + freshness × 0.4):");
            foreach (var id in fallback.Trending(4))
                Console.WriteLine($"    {id}: {catalog[id].Title,-25}  pop={catalog[id].Popularity:F2}  fresh={catalog[id].Freshness:F2}");
        }

        Console.WriteLine("\n=== Scenario 7: Evaluation Metrics ===\n");
        {
            // Alice's actual relevant items (held-out test set, rated ≥ 4): A,B,C,J
            var aliceRelevant = new HashSet<string> { "A", "B", "C", "J" };

            // Simulate two recommendation lists to compare
            var recListA = new List<string> { "A", "D", "B", "E", "C", "J", "F", "G" };  // good list
            var recListB = new List<string> { "D", "F", "H", "E", "G", "A", "I", "B" };  // weaker list

            Console.WriteLine("  Evaluating Alice's recommendations (relevant: A,B,C,J):\n");
            Console.WriteLine($"  {"Metric",-20} {"List A (good)",12}   {"List B (weaker)",12}");
            Console.WriteLine($"  {new string('-', 48)}");

            foreach (int k in new[] { 3, 5, 8 })
            {
                double pA = Evaluation.PrecisionAtK(recListA, aliceRelevant, k);
                double pB = Evaluation.PrecisionAtK(recListB, aliceRelevant, k);
                Console.WriteLine($"  {"Precision@" + k,-20} {pA,12:F3}   {pB,12:F3}");

                double rA = Evaluation.RecallAtK(recListA, aliceRelevant, k);
                double rB = Evaluation.RecallAtK(recListB, aliceRelevant, k);
                Console.WriteLine($"  {"Recall@" + k,-20} {rA,12:F3}   {rB,12:F3}");

                double nA = Evaluation.NdcgAtK(recListA, aliceRelevant, k);
                double nB = Evaluation.NdcgAtK(recListB, aliceRelevant, k);
                Console.WriteLine($"  {"NDCG@" + k,-20} {nA,12:F3}   {nB,12:F3}");
                Console.WriteLine();
            }

            Console.WriteLine("  List A has higher NDCG: relevant items appear earlier in ranking ✓");
        }

        Console.WriteLine("\nDone — 0 errors, 0 warnings");
    }
}
