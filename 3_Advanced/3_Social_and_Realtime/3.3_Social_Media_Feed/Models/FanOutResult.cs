// FanOutResult — diagnostic receipt from a fan-out operation.
// Lets callers (and tests) verify whether the celebrity path was taken
// and how many feed caches were actually updated.

namespace AdvancedDesigns
{
    public class FanOutResult
    {
        public string PostId       { get; }
        public string AuthorId     { get; }
        public bool   WasCelebrity { get; } // true → post was NOT written to follower feeds
        public int    FanOutCount  { get; } // 0 if celebrity, N otherwise

        public FanOutResult(string postId, string authorId, bool wasCelebrity, int fanOutCount)
        {
            PostId       = postId;
            AuthorId     = authorId;
            WasCelebrity = wasCelebrity;
            FanOutCount  = fanOutCount;
        }
    }
}
