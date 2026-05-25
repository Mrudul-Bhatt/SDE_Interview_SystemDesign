// Post — the core content unit in the feed system.
//
// EngagementRaw weights different interaction types: shares > comments > likes
// because sharing signals stronger intent than a passive like.
// These weights are tuning knobs; real systems A/B test them continuously.

namespace AdvancedDesigns
{
    public class Post
    {
        public string   PostId    { get; }
        public string   AuthorId  { get; }
        public string   Content   { get; }
        public DateTime CreatedAt { get; }
        public int      LikeCount    { get; set; }
        public int      CommentCount { get; set; }
        public int      ShareCount   { get; set; }

        public double EngagementRaw => LikeCount * 2 + CommentCount * 3 + ShareCount * 5;

        public Post(string postId, string authorId, string content, DateTime? createdAt = null)
        {
            PostId    = postId;
            AuthorId  = authorId;
            Content   = content;
            CreatedAt = createdAt ?? DateTime.UtcNow;
        }

        public override string ToString() =>
            $"[{PostId}] @{AuthorId}: \"{Content}\" | " +
            $"likes={LikeCount} comments={CommentCount} age={Math.Round((DateTime.UtcNow - CreatedAt).TotalMinutes)}min";
    }
}
