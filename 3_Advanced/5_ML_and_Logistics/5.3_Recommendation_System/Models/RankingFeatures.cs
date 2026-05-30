// RankingFeatures — the feature bundle passed to the ranking model.
//
// In production this is a wider struct (often 100+ features) including device
// type, time-of-day, position bias correction, etc. The four kept here are the
// most explanatory ones for a system design discussion:
//   Relevance    — score from the retrieval stage (CF / two-tower / etc.)
//   Popularity   — how broadly the item engages users
//   Freshness    — recency boost so new content can surface
//   UserAffinity — historical engagement of THIS user with similar items

public class RankingFeatures
{
    public string ItemId      { get; set; }
    public double Relevance   { get; set; }  // from retrieval score
    public double Popularity  { get; set; }  // 0–1
    public double Freshness   { get; set; }  // 0–1
    public double UserAffinity { get; set; } // historical engagement
}
