// LogCompactor — retains only the latest message per key, removes tombstones.
//
// THE BIG IDEA:
// Imagine a whiteboard where teammates post sticky notes. Every time someone
// updates "Alice's order status" they slap a new note on top. After a year,
// there are hundreds of notes, but only the topmost one matters — the rest
// are just history. LogCompactor erases all but the topmost note for each
// label (key), shrinking the whiteboard from wall-sized to desk-sized.
//
// The compacted log is still replayable from offset 0 — a new consumer can
// reconstruct the full current state of every key without reading years of
// intermediate updates.
//
// WHY LOG COMPACTION (vs infinite retention or TTL-based expiry):
//   Infinite retention   → disk grows forever, O(all writes ever)
//   TTL-based expiry     → data vanishes even if it was never superseded;
//                          a rarely-updated key disappears even though it's still valid
//   Log compaction       → disk grows with distinct keys, O(distinct keys);
//                          every key survives until explicitly deleted (tombstone)
//
// This makes compaction ideal for "current state" topics: user profiles,
// inventory counts, feature flags — anywhere you care about what IS true
// now, not every change that ever happened.
//
// WHY TOMBSTONES (null value) SURVIVE UNTIL THE FINAL STEP:
// During the pass over the log, a tombstone (null value) for key K must
// overwrite any earlier non-null message for K in the `latest` map.
// If we dropped tombstones on first sight, an older "Alice = {name: Alice}"
// message would survive and Alice would never be deleted. Only after the
// full pass do we strip remaining tombstones — at that point they've done
// their job of erasing earlier values, and a new consumer starting from
// offset 0 correctly sees Alice as absent.
//
// WHY NULL KEY MESSAGES ARE SKIPPED:
// A null key means the producer chose round-robin routing (no ordering
// guarantee). Without a stable key, there is no "latest version of X"
// concept — compaction is meaningless for these messages, so they are
// silently dropped. Use keyed messages for any data you want compacted.

using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public static class LogCompactor
    {
        public static List<Message> Compact(IEnumerable<Message> log)
        {
            // Single pass in offset order: for each key, later messages overwrite
            // earlier ones in the map. By the end, `latest` holds exactly one
            // message per key — the most recent write, tombstone or otherwise.
            var latest = new Dictionary<string, Message>();
            foreach (var msg in log)
                if (msg.Key != null) latest[msg.Key] = msg; // null key = uncompactable, skip

            // Final step: strip tombstones (Value == null). They've already cancelled
            // any earlier non-null entry for their key in the map above, so removing
            // them now makes deleted keys invisible to consumers replaying from offset 0.
            // OrderBy(Offset) preserves the original arrival order within the compacted
            // output so consumers still see keys in a consistent sequence.
            return latest.Values
                .Where(m => m.Value != null)
                .OrderBy(m => m.Offset)
                .ToList();
        }
    }
}
