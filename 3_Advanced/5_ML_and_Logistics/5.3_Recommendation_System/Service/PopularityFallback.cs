// PopularityFallback — the cold-start safety net.
//
// New users have no interaction history, so CF and MF have nothing to work
// with. The first session falls back to globally popular content (optionally
// filtered by onboarding survey results like "I like Sci-Fi"). Even 3-5 clicks
// during the first session are enough to seed real-time personalization.
//
// Trending blends popularity and freshness — popular today beats popular five
// years ago, but a brand-new item with zero engagement shouldn't outrank a
// proven hit. The 60/40 split here is a starting point; tune per surface.

using System.Collections.Generic;
using System.Linq;

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
