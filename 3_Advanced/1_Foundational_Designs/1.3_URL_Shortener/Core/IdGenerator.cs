// IdGenerator
// Simulates a Redis INCR: atomic counter, each call returns a globally unique ID.
// Starting at 100,000 so Base62.Encode always produces exactly 7-char codes (≥ 62^4).

using System.Threading;

namespace AdvancedDesigns
{
    public class IdGenerator
    {
        private long _counter = 100_000;

        // Interlocked.Increment is a single CPU instruction (LOCK XADD) —
        // thread-safe without a lock, and far cheaper than lock() for a hot path
        // that every shorten request hits.
        public long NextId() => Interlocked.Increment(ref _counter);
    }
}
