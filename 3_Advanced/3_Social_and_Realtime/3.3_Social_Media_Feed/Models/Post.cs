// Post — the core content unit in the feed system. One Post = one tweet/photo/status.
//
// THE BIG IDEA:
// A Post is the "thing" that travels through the whole system. When Alice publishes,
// a Post is created once and stored once — then its ID is copied into the feeds of
// everyone who should see it (see FanOutService). The post's content lives here;
// the feeds only ever hold the lightweight PostId, not a full copy of this object.
//
// WHY ENGAGEMENT IS WEIGHTED (shares > comments > likes):
// Not all interactions mean the same thing. A like is a one-tap reflex; a comment
// takes real effort; a share means "I'm willing to put this in front of MY followers"
// — the strongest possible vote of confidence. So the ranking formula weights them
// 5 / 3 / 2 to reflect that escalating level of intent. These exact numbers are
// tuning knobs: real platforms run constant A/B tests to find the weights that keep
// people engaged.

using System;

namespace AdvancedDesigns
{
    public class Post
    {
        // Globally unique ID for this post. In production this is a Snowflake ID
        // (timestamp + machine + counter) so it's unique AND time-sortable, and any
        // server can generate one without asking a central counter. This is the value
        // copied into follower feeds — small and cheap to pass around.
        public string PostId { get; }

        // Who wrote it. Used to (a) find this author's followers when fanning out,
        // and (b) group all of one author's posts together in storage so "give me
        // Alice's latest posts" (the celebrity read path) is one fast lookup.
        public string AuthorId { get; }

        // The actual payload the reader sees (text here; a real post also carries
        // image/video URLs). This is the "heavy" data that's deliberately kept OUT
        // of the feed caches — feeds store only the PostId and fetch this on demand.
        public string Content { get; }

        // When the post was created. Drives two things: the default chronological
        // ordering (newest first), and the time-decay in the ranking score — an
        // older post scores lower even if it has the same engagement.
        public DateTime CreatedAt { get; }

        // Engagement counters. `set` (not read-only) because they change constantly
        // as people interact. In production these are NOT stored on the post row like
        // this — they're high-volume distributed counters updated separately, because
        // a viral post can get thousands of likes/second and you can't rewrite one
        // row that fast. Here they're simple fields to keep the demo readable.
        public int LikeCount { get; set; }
        public int CommentCount { get; set; }
        public int ShareCount { get; set; }

        // The raw "how interesting is this post?" number, before time-decay is applied.
        // Computed on the fly from the counters using the weights described above.
        // FeedRanker later divides this by the post's age to get the final feed score.
        public double EngagementRaw => LikeCount * 2 + CommentCount * 3 + ShareCount * 5;

        public Post(string postId, string authorId, string content, DateTime? createdAt = null)
        {
            PostId = postId;
            AuthorId = authorId;
            Content = content;
            // Default to "right now" if no time is given. Tests pass an explicit time
            // so they can simulate old vs fresh posts and check the ranking behaves.
            CreatedAt = createdAt ?? DateTime.UtcNow;
        }

        // Short human-readable form for demo output — shows the post plus its age in
        // minutes so you can eyeball why the ranker ordered things the way it did.
        public override string ToString() =>
            $"[{PostId}] @{AuthorId}: \"{Content}\" | " +
            $"likes={LikeCount} comments={CommentCount} age={Math.Round((DateTime.UtcNow - CreatedAt).TotalMinutes)}min";
    }
}
