// FeedPage — one page of feed results plus a cursor for the next page.
//
// Why cursor-based pagination (not offset): offset-based pagination drifts when
// new posts arrive between page requests — page 2 can repeat posts from page 1.
// A cursor encodes the exact position (last seen timestamp), so each page always
// starts after the last item the user actually saw, regardless of new inserts.

namespace AdvancedDesigns
{
    public class FeedPage
    {
        public List<Post> Posts      { get; }
        public long?      NextCursor { get; } // null when no more pages

        public FeedPage(List<Post> posts, long? nextCursor)
        {
            Posts      = posts;
            NextCursor = nextCursor;
        }
    }
}
