// LogCompactor — retains only the latest message per key, removes tombstones.
//
// Why log compaction: for changelog-style topics (e.g. user profile updates),
// only the current state matters. Compaction shrinks disk usage from O(all writes)
// to O(distinct keys) while keeping the log replayable from the beginning.
//
// Why tombstones (null value) are dropped at the end: a null value signals
// "delete this key". After compaction, the key no longer needs a placeholder —
// any new consumer starting from the beginning won't see it at all, which is
// the correct semantics for a deleted entity.

namespace AdvancedDesigns
{
    public static class LogCompactor
    {
        public static List<Message> Compact(IEnumerable<Message> log)
        {
            // Walk in order: later writes overwrite earlier ones for the same key.
            var latest = new Dictionary<string, Message>();
            foreach (var msg in log)
                if (msg.Key != null) latest[msg.Key] = msg;

            // Strip tombstones from the final output — deleted keys vanish entirely.
            return latest.Values
                .Where(m => m.Value != null)
                .OrderBy(m => m.Offset)
                .ToList();
        }
    }
}
