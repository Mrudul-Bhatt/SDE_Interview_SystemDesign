// IdGenerator
// Simulates a Redis INCR: atomic counter, each call returns a globally unique ID.
// Starting at 100,000 to avoid obviously guessable low codes like "0000001".
// Output length is always exactly 7 chars — that's hardcoded in Base62 (Length = 7),
// not controlled by this starting value.

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
