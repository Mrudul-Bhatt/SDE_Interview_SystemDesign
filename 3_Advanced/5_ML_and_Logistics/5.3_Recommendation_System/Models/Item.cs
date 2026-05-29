// Item — one row in the content catalog.
//
// Popularity and Freshness are pre-computed signals (in production: fed from a
// stream-processing job that aggregates view counts and upload time per item).
// Both are normalized to 0–1 so the ranker can combine them with relevance
// without manual rescaling.

using System.Collections.Generic;

public class Item
{
    public string ItemId    { get; set; }
    public string Title     { get; set; }
    public string Category  { get; set; }
    public double Popularity { get; set; }  // 0–1 normalized
    public double Freshness  { get; set; }  // 0–1 (1 = brand new)
    public List<string> Tags { get; set; } = new List<string>();
}
