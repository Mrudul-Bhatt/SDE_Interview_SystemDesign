// InteractionMatrix — the (user, item) → rating sparse matrix.
//
// In production this is what comes out of the event pipeline (click, watch,
// purchase events streamed via Kafka and aggregated into per-user / per-item
// rating signals). Stored sparsely because 99.9%+ of cells are empty — most
// users have only interacted with a tiny fraction of the catalog.
//
// The "rating" can be an explicit 1-5 star rating or an implicit signal
// (normalized engagement count). The CF and MF algorithms don't care which.

using System.Collections.Generic;
using System.Linq;

public class InteractionMatrix
{
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
