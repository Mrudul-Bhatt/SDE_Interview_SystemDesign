// MatrixFactorization — SGD training of R ≈ μ + b_u + b_i + U·V^T.
//
// Each user and item gets a K-dimensional latent vector. The dot product of
// those vectors, plus user/item bias terms and the global mean, predicts the
// rating. The model is trained by stochastic gradient descent over observed
// ratings — for each (user, item, rating) tuple, we compute the error and
// nudge the vectors in the direction that reduces it.
//
// Why biases? Because some users always rate high, some items are always
// loved, and the latent factors shouldn't have to encode those constants.
// Separating them lets U·V^T capture only the "interaction" — the user-item
// affinity beyond the baselines.
//
// Lambda is L2 regularization to prevent overfitting; eta is the learning
// rate. 50 epochs is overkill for this small demo but typical for production.

using System;
using System.Collections.Generic;
using System.Linq;

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
            var shuffled = obs.OrderBy(_ => _rng.Next()).ToList();
            double totalLoss = 0;

            foreach (var (u, i, r) in shuffled)
            {
                double pred  = Predict(u, i);
                double error = r - pred;
                totalLoss   += error * error;

                // Update biases (regularization shrinks toward zero)
                _userBias[u] += _eta * (error - _lambda * _userBias[u]);
                _itemBias[i] += _eta * (error - _lambda * _itemBias[i]);

                // Update latent vectors — the standard SGD update for MF
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
