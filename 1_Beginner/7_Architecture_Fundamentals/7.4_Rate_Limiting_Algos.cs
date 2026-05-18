// Q1. Implement a Fixed Window Rate Limiter (and demonstrate the boundary burst bug)
//
// Count requests per fixed time window per client.
// Reject if the client exceeds the limit within that window.
// Then show the double-limit boundary burst problem that makes this algorithm
// weaker than a sliding window.
//
// The Boundary Burst Problem
// ─────────────────────────
// Limit: 5 requests per minute
//
//   t=00:58  Requests 1–5  → ALLOWED  (window 1 fills up)
//   t=01:00  NEW WINDOW — counter resets to 0
//   t=01:00  Requests 6–10 → ALLOWED  (window 2 starts fresh)
//
//   Result: 10 requests in 2 seconds — 2× the intended limit.
//   Fixed window allows bursting at window boundaries.
//
// When Fixed Window is still acceptable
// ──────────────────────────────────────
//   → Simplicity matters more than precision
//   → Limits are generous (boundary burst is a small % of total)
//   → Protecting against sustained abuse, not short bursts
//   → Examples: basic API gateways, login-attempt throttling
//
// When to use Sliding Window instead
// ────────────────────────────────────
//   → Accuracy matters (financial APIs, per-user billing)
//   → Boundary burst would cause real damage
//   → Memory cost of storing per-request timestamps is acceptable
//
// Complexity: Allow O(1) time, O(clients) space

using System;
using System.Collections.Generic;

namespace ArchitectureFundamentals
{
    // -------------------------------------------------------------------------
    // FixedWindowRateLimiter
    // -------------------------------------------------------------------------
    public class FixedWindowRateLimiter
    {
        // WindowEntry holds the state for one client's current window.
        // Using a nested class keeps this detail private to the limiter —
        // callers never need to know how the window is stored internally.
        private class WindowEntry
        {
            public int Count { get; set; }

            // init instead of set: WindowStart is written once when the window
            // opens and must never change mid-window. init enforces this at
            // compile time — no accidental reset of the window start time.
            public DateTime WindowStart { get; init; }
        }

        private readonly int _limit;
        private readonly TimeSpan _window;

        // One entry per client ID. In production this would be Redis so all
        // API server replicas share state. Using Dictionary here to show the
        // algorithm without Redis as a dependency.
        private readonly Dictionary<string, WindowEntry> _windows = new Dictionary<string, WindowEntry>();

        // A single lock protecting _windows. This is safe because window
        // operations are microsecond-fast — contention is negligible.
        // In production you'd use a distributed lock (Redis SET NX) or
        // Redis INCR which is atomic by default.
        private readonly object _lock = new object();

        public FixedWindowRateLimiter(int requestLimit, TimeSpan window)
        {
            _limit = requestLimit;
            _window = window;
        }

        public bool Allow(string clientId)
        {
            // lock ensures read-check-write is atomic.
            // Without it: two threads could both read Count=4, both increment
            // to 5, and both return true — exceeding the limit by 1.
            lock (_lock)
            {
                // DateTime.UtcNow instead of DateTime.Now: UtcNow is not
                // affected by DST transitions or timezone settings on the server.
                // A server DST "fall back" could make Now go backwards 1 hour,
                // causing windows to never expire. UtcNow is monotonically safe.
                DateTime now = DateTime.UtcNow;

                // Open a fresh window if this client has no record yet,
                // OR if the current window has expired (elapsed >= window duration).
                if (!_windows.TryGetValue(clientId, out var entry) ||
                    now - entry.WindowStart >= _window)
                {
                    // Count starts at 1 because this request IS the first one
                    // in the new window — it's allowed, so we count it immediately.
                    _windows[clientId] = new WindowEntry { Count = 1, WindowStart = now };
                    return true;
                }

                // Still inside the current window — check against the limit.
                if (entry.Count < _limit)
                {
                    entry.Count++;
                    return true;
                }

                // Client has exhausted their allowance for this window.
                return false;
            }
        }

        // Returns how many requests the client has used and how long until
        // the window resets. Used to populate standard HTTP rate-limit headers.
        public (int Count, TimeSpan Remaining) GetStatus(string clientId)
        {
            lock (_lock)
            {
                if (!_windows.TryGetValue(clientId, out var entry))
                    return (0, _window); // no record → full window remaining

                DateTime now = DateTime.UtcNow;

                // Window may have expired since the last Allow() call.
                // Treat an expired window as empty — don't return stale counts.
                if (now - entry.WindowStart >= _window)
                    return (0, _window);

                return (entry.Count, _window - (now - entry.WindowStart));
            }
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            var limiter = new FixedWindowRateLimiter(
                requestLimit: 5,
                window: TimeSpan.FromSeconds(60)
            );

            // =================================================================
            // Scenario 1 — Normal usage: 5 requests allowed, 6th denied
            // Shows the basic allow/deny behaviour within a single window.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Normal usage — 6 requests, limit 5 ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            for (int i = 1; i <= 6; i++)
            {
                bool allowed = limiter.Allow("user:alice");
                Console.WriteLine($"  Request {i}: {(allowed ? "ALLOWED" : "DENIED ")}");
            }

            // =================================================================
            // Scenario 2 — Boundary burst (conceptual demo)
            // Two consecutive windows, each allowing 5 requests. When requests
            // arrive right at the window boundary, a client can fire 10 in ~2s.
            // We print this conceptually because sleeping 60s in a demo is impractical.
            // In a real Sliding Window implementation this burst is impossible.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Boundary burst (conceptual)         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            Console.WriteLine(@"
  Limit: 5 requests / minute

  t=00:58  Request 1  → ALLOWED  (window 1, count=1)
  t=00:59  Request 2  → ALLOWED  (window 1, count=2)
  t=00:59  Request 3  → ALLOWED  (window 1, count=3)
  t=00:59  Request 4  → ALLOWED  (window 1, count=4)
  t=00:59  Request 5  → ALLOWED  (window 1, count=5)  ← window 1 full

  t=01:00  NEW WINDOW — counter resets to 0

  t=01:00  Request 6  → ALLOWED  (window 2, count=1)
  t=01:00  Request 7  → ALLOWED  (window 2, count=2)
  t=01:00  Request 8  → ALLOWED  (window 2, count=3)
  t=01:00  Request 9  → ALLOWED  (window 2, count=4)
  t=01:00  Request 10 → ALLOWED  (window 2, count=5)

  !! 10 requests passed in ~2 seconds — 2x the intended limit of 5/min.
  This is the boundary burst bug.
  Sliding Window fixes this by tracking a rolling 60-second lookback
  instead of hard-resetting at a clock boundary.");

            // =================================================================
            // Scenario 3 — HTTP response headers for a rate-limited client
            // Standard headers tell the client how many requests remain and
            // when to retry. These are the headers a 429 response should carry.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: HTTP 429 response headers           ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");

            // alice exhausted her window in Scenario 1 — GetStatus reflects that.
            var (count, remaining) = limiter.GetStatus("user:alice");
            Console.WriteLine("\n  HTTP/1.1 429 Too Many Requests");
            Console.WriteLine($"  X-RateLimit-Limit:     5");

            // Remaining is max(0, limit - used). Clamp to 0 in case the window
            // just expired between Allow() and GetStatus() returning.
            Console.WriteLine($"  X-RateLimit-Remaining: {Math.Max(0, 5 - count)}");

            // Retry-After tells the client how many seconds until the window resets.
            // Returning an integer (ceiling) is safer than a fraction — the client
            // should wait at least this long, not slightly less.
            Console.WriteLine($"  Retry-After:           {(int)remaining.TotalSeconds}s");

            Console.WriteLine("\n  Why these headers matter:");
            Console.WriteLine("    Without them the client must guess when to retry.");
            Console.WriteLine("    With them the client backs off exactly the right amount");
            Console.WriteLine("    — no thundering-herd of retries the instant the window resets.");
        }
    }

} // namespace ArchitectureFundamentals
