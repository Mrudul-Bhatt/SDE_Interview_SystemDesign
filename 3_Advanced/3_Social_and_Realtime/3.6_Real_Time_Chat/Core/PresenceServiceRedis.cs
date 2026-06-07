// PresenceServiceRedis — tracks online/offline status and "last seen" timestamps.
//
// THE BIG IDEA:
// Think of this like a hotel's "do not disturb" sign system. When a guest is
// active, staff see the light on. When the guest leaves without checking out
// (server crash), the light eventually turns off on its own after a fixed
// timeout — staff don't need to manually flip it.
//
// Two questions drive the whole design:
//
//   "Is Alice online right now?"  → IsOnline()  — answered by heartbeat freshness
//   "When was Alice last active?" → GetLastSeen() — answered by the _lastSeen timestamp
//
// Clients send a heartbeat ping every ~10 seconds while the app is open.
// IsOnline returns true only if the most recent heartbeat arrived within _timeout (30s).
// If heartbeats stop arriving — app killed, network lost, server crashed — the user
// automatically falls offline once the window elapses. No cleanup code needed.
//
// In production, _heartbeats maps to a Redis key "presence:{userId}" with a 30-second
// TTL. Every heartbeat is a Redis SETEX that resets the TTL. When the key expires
// (no heartbeat for 30s), Redis deletes it — IsOnline(userId) becomes false with
// zero application-level code. _lastSeen is a separate persistent key that Redis
// does NOT expire, so "last seen" survives even after the presence key is gone.
//
// WHY HEARTBEAT + TTL (not just connect/disconnect events):
// A clean disconnect (user closes the app) fires a disconnect event. But a crashed
// server, a killed process, or a dropped network connection produces no event at all
// — the server simply goes silent. Without the TTL, those users would show as "online"
// forever. The heartbeat window bounds the worst-case staleness: a crashed user can
// appear online for at most _timeout seconds before the system corrects itself.
//
// WHY LASTSEEN IS UPDATED ON BOTH HEARTBEAT AND DISCONNECT:
// Updating on Disconnect gives an exact timestamp for clean logouts. Updating on
// Heartbeat covers the crash/drop case: if the last heartbeat was at 3:42 PM and
// the user's server died at 3:43 PM, GetLastSeen returns 3:42 PM — accurate to
// within one heartbeat interval. Without the Heartbeat update, a long-lived session
// that ended via crash would show the connect time as "last seen," which could be
// hours or days ago.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class PresenceServiceRedis
    {
        // Tracks the timestamp of each user's most recent heartbeat ping.
        // Presence = entry exists AND its age < _timeout. Entry is removed on
        // clean Disconnect so IsOnline immediately returns false without waiting
        // for the window to elapse. In production: Redis key "presence:{userId}"
        // with a rolling TTL reset by each heartbeat via SETEX.
        //
        // ── RUNTIME SNAPSHOT (Scenario 5, "now" = 15:42:10, _timeout = 30s) ──
        //   _heartbeats = {
        //       "alice" → 15:42:08,   ← 2s ago  → IsOnline = true
        //       "carol" → 15:42:05    ← 5s ago  → IsOnline = true
        //   }                         ← "bob" REMOVED by Disconnect() → IsOnline = false
        //
        //   If carol stops pinging: at 15:42:36 her entry is 31s stale (> 30s),
        //   so IsOnline("carol") flips to false even though the key still exists.
        //   (In prod, Redis would have already deleted the key via TTL expiry.)
        private readonly Dictionary<string, DateTime> _heartbeats = [];

        // Tracks when each user was last known to be active (updated on every
        // heartbeat AND on explicit Disconnect). Survives after the heartbeat
        // entry is gone — "last seen 5 min ago" must remain queryable even when
        // the user is offline. In production: a separate Redis key without TTL.
        //
        // ── RUNTIME SNAPSHOT (same moment as above) ──
        //   _lastSeen = {
        //       "alice" → 15:42:08,
        //       "bob"   → 15:40:12,   ← KEPT even though bob disconnected (stamped at logout)
        //       "carol" → 15:42:05
        //   }
        //   GetLastSeen("bob") → 15:40:12   →  UI shows "last seen at 3:40 PM"
        //   GetLastSeen("erin") → null      →  user we've never seen (distinct from "long ago")
        private readonly Dictionary<string, DateTime> _lastSeen = [];

        // How long after the last heartbeat a user is still considered online.
        // Must be comfortably longer than the client's heartbeat interval (~10s)
        // to tolerate one dropped ping without falsely showing the user as offline.
        // 30s = enough headroom for two missed pings before declaring offline.
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        // Called by the client every ~10 seconds while the app is open.
        // Advances both the presence window (keeping IsOnline true) and LastSeen
        // (so crash recovery shows the accurate last-active time, not connect time).
        public void Heartbeat(string userId)
        {
            _heartbeats[userId] = DateTime.UtcNow;
            _lastSeen[userId] = DateTime.UtcNow;
        }

        // Called when the WebSocket closes cleanly (user explicitly logs out or
        // closes the app). Removes the heartbeat entry immediately so IsOnline
        // returns false right away — no need to wait for the 30s window to elapse.
        // LastSeen is stamped here too so the exact logout time is preserved.
        public void Disconnect(string userId)
        {
            _lastSeen[userId] = DateTime.UtcNow;
            _heartbeats.Remove(userId);
        }

        // Returns true if a heartbeat arrived within the last _timeout seconds.
        // The time check handles the crash/drop case: the heartbeat entry is still
        // present but stale, so the window comparison declares the user offline
        // once _timeout elapses — equivalent to Redis TTL expiry in production.
        public bool IsOnline(string userId) =>
            _heartbeats.TryGetValue(userId, out var last) && DateTime.UtcNow - last < _timeout;

        // Returns the last time the user was known active, or null if never seen.
        // Null (not a default DateTime) signals "we have no record of this user"
        // vs "user was last seen at epoch 0" — callers can distinguish the two cases.
        public DateTime? GetLastSeen(string userId) =>
            _lastSeen.TryGetValue(userId, out var ts) ? ts : null;
    }
}
