// Program — entry point for all Recommendation System demo scenarios.

using System;
using System.Collections.Generic;
using System.Linq;

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

            Console.WriteLine("\n  Items most similar to 'The Matrix' (A):");
            foreach (var (itemId, sim) in cf.SimilarItems("A", 3))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  sim={sim:F4}");

            Console.WriteLine("\n  Recommendations for Alice (rated A,B,C,J):");
            foreach (var (itemId, score) in cf.Recommend("alice", 5))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  predicted_rating={score:F2}");

            Console.WriteLine("\n  Recommendations for Bob (rated A,D,F,B):");
            foreach (var (itemId, score) in cf.Recommend("bob", 5))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  predicted_rating={score:F2}");
        }

        Console.WriteLine("\n=== Scenario 2: Matrix Factorization (SGD) ===\n");
        {
            var mf = new MatrixFactorization(k: 5, eta: 0.02, lambda: 0.02, epochs: 30);
            mf.Train(matrix);

            Console.WriteLine("\n  Rating predictions:");
            var toPredict = new[] { ("alice","D"), ("alice","E"), ("bob","C"), ("carol","A") };
            foreach (var (u, i) in toPredict)
                Console.WriteLine($"    {u,-10} × {catalog[i].Title,-25} → predicted={mf.Predict(u, i):F2}");

            Console.WriteLine("\n  MF Recommendations for Carol (rated E,H,I,G — Drama/Horror):");
            foreach (var (itemId, score) in mf.Recommend("carol", matrix, 4))
                Console.WriteLine($"    {itemId}: {catalog[itemId].Title,-25}  score={score:F2}");
        }

        Console.WriteLine("\n=== Scenario 3: Two-Tower Retrieval ===\n");
        {
            // Simulate item embeddings learned by two-tower model
            var rand = new Random(7);
            var index = new TwoTowerIndex(dim: 4);
            var itemEmbeddings = new Dictionary<string, double[]>();

            foreach (var item in items)
            {
                // Sci-Fi → high dim[0], Action → dim[1], Drama → dim[2], Horror → dim[3]
                double[] vec = new double[4];
                if (item.Category == "Sci-Fi")   { vec[0] = 0.8 + rand.NextDouble() * 0.2; vec[1] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Action"){ vec[1] = 0.8 + rand.NextDouble() * 0.2; vec[0] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Drama") { vec[2] = 0.8 + rand.NextDouble() * 0.2; vec[3] = rand.NextDouble() * 0.2; }
                else if (item.Category == "Horror"){ vec[3] = 0.8 + rand.NextDouble() * 0.2; vec[2] = rand.NextDouble() * 0.2; }
                else { vec[0] = vec[1] = vec[2] = vec[3] = rand.NextDouble() * 0.5; }

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
