// Q1. Implement Round Robin Load Balancer
// Given a list of servers, distribute requests evenly across them in order.
// Handle servers being added and removed dynamically.
// Complexity: GetServer O(1), AddServer O(1), RemoveServer O(n)

// Q2. Implement Weighted Round Robin
// Servers have different capacities. A server with weight 3 should handle
// 3x more requests than a server with weight 1.
// Complexity: GetServer O(1), RebuildSequence O(total weight)

// Q3. Implement Least Connections Load Balancer
// Route each request to the server with the fewest active connections.
// When a request completes, the connection count decreases.

// Q4. Load Balancer with Health Checks
// Extend a load balancer to skip unhealthy servers. Mark a server DOWN after
// N consecutive failures. Recover it after M consecutive successes.

using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// Q1 — Round Robin Load Balancer
// ---------------------------------------------------------------------------
public class RoundRobinLoadBalancer
{
    // List<string> — not List<int> — because servers are identified by name/address.
    // A list (not a set) because order matters: round robin cycles through positions,
    // and we need index-based access for O(1) GetServer().
    private readonly List<string> _servers = new();

    // Tracks which server gets the NEXT request.
    // We advance it modulo Count so it wraps around forever without resetting.
    private int _currentIndex = 0;

    // A lock object for thread safety — multiple request threads will call GetServer()
    // concurrently. Without this, two threads could read/write _currentIndex at the
    // same time and both route to the same server (or skip one).
    // We use a dedicated object (not `this`) so external callers can't accidentally
    // deadlock us by locking on the same instance.
    private readonly object _lock = new();

    public void AddServer(string server)
    {
        // Append to the end — the current index is unaffected because all new
        // positions are AFTER the current pointer. No lock needed here since
        // add is typically done at startup before serving traffic.
        _servers.Add(server);
    }

    public void RemoveServer(string server)
    {
        int idx = _servers.IndexOf(server);
        if (idx == -1) return; // Server not found — nothing to do.

        _servers.RemoveAt(idx);

        // If we removed a server AT or BEFORE the current pointer, the pointer
        // now points one position too far ahead (everything shifted left by 1).
        // Decrement to stay on the same logical "next" server.
        // Math.Max(0, ...) prevents going negative when removing the first server.
        if (idx <= _currentIndex)
            _currentIndex = Math.Max(0, _currentIndex - 1);
    }

    public string? GetServer()
    {
        // Return null instead of throwing — callers should handle the no-server case
        // gracefully (e.g. return 503 to the client).
        if (_servers.Count == 0) return null;

        // lock ensures only one thread reads+advances _currentIndex at a time.
        // Without this, two concurrent requests could both read index 2, both route
        // to server[2], and both advance to 3 — meaning server[2] gets double traffic
        // and one position is effectively skipped.
        lock (_lock)
        {
            string server = _servers[_currentIndex];

            // Modulo wrap-around: when we reach the last server, the next call
            // goes back to index 0 instead of throwing IndexOutOfRangeException.
            _currentIndex = (_currentIndex + 1) % _servers.Count;

            return server;
        }
    }
}

// ---------------------------------------------------------------------------
// Q2 — Weighted Round Robin Load Balancer
// ---------------------------------------------------------------------------
public class WeightedRoundRobinLoadBalancer
{
    // Store the original (server, weight) pairs so we can rebuild the sequence
    // whenever the server list changes. We need the weights to reconstruct,
    // so we can't just keep the expanded sequence.
    private readonly List<(string Server, int Weight)> _servers = new();

    // Pre-expanded sequence: ["S1", "S2", "S2", "S3"] for weights 1,2,1.
    // We expand upfront so GetServer() is O(1) — just index + advance.
    // The alternative (computing weight-based routing on every call) would be
    // O(servers) per request.
    private List<string> _sequence = new();

    private int _currentIndex = 0;

    public void AddServer(string server, int weight)
    {
        _servers.Add((server, weight));

        // Rebuild every time a server is added so the sequence always reflects
        // the current set. This is O(total weight) but only happens on config
        // changes, not on every request.
        RebuildSequence();
    }

    public void RemoveServer(string server)
    {
        // RemoveAll handles the case where the server isn't present (removes 0 items).
        // We match by name, not index, because we don't expose indices externally.
        _servers.RemoveAll(s => s.Server == server);
        RebuildSequence();
    }

    private void RebuildSequence()
    {
        _sequence = new List<string>();

        // Expand each server into `weight` copies in the list.
        // weight:1 → 1 slot, weight:2 → 2 slots, weight:3 → 3 slots.
        // The ratio of slots = ratio of traffic each server receives.
        foreach (var (server, weight) in _servers)
            for (int i = 0; i < weight; i++)
                _sequence.Add(server);

        // Reset to 0 after a rebuild — the old index is no longer meaningful
        // because the sequence length (and contents) may have changed entirely.
        _currentIndex = 0;
    }

    public string? GetServer()
    {
        if (_sequence.Count == 0) return null;

        string server = _sequence[_currentIndex];

        // Same modulo wrap-around as Q1 — cycles back to the first slot after
        // reaching the end of the weighted sequence.
        _currentIndex = (_currentIndex + 1) % _sequence.Count;

        return server;
    }
}

// ---------------------------------------------------------------------------
// Q3 — Least Connections Load Balancer
// ---------------------------------------------------------------------------
public class LeastConnectionsLoadBalancer
{
    // Dictionary maps server name → current active connection count.
    // Dictionary gives O(1) lookup and update per server — better than a list
    // when we need to increment/decrement by name on every request/release.
    private readonly Dictionary<string, int> _connections = new();

    // Same lock-per-instance pattern as Q1 — GetServer() and ReleaseConnection()
    // both mutate _connections and are called concurrently from multiple threads.
    private readonly object _lock = new();

    public void AddServer(string server)
    {
        // Initialize at 0 — a brand new server has no active connections.
        // We lock here too because AddServer can be called during live traffic
        // (e.g., when auto-scaling adds a node).
        lock (_lock) { _connections[server] = 0; }
    }

    public void RemoveServer(string server)
    {
        lock (_lock) { _connections.Remove(server); }
    }

    public string? GetServer()
    {
        if (_connections.Count == 0) return null;

        lock (_lock)
        {
            // Linear scan to find the server with the minimum active connections.
            // O(n) per request — acceptable because n = number of servers (typically
            // small, e.g. <100), not the number of requests.
            string? best = null;
            int minConn = int.MaxValue;
            foreach (var (server, count) in _connections)
            {
                if (count < minConn)
                {
                    minConn = count;
                    best = server;
                }
            }

            // Increment BEFORE returning — the connection is "active" from the moment
            // we assign the request, not after the server starts processing it.
            // Failing to do this would cause thundering herd: all threads picking the
            // same server before any of them increments the counter.
            if (best != null) _connections[best]++;
            return best;
        }
    }

    public void ReleaseConnection(string server)
    {
        lock (_lock)
        {
            // Guard against going below 0 — could happen if ReleaseConnection is called
            // more times than GetServer (e.g. a bug in the caller).
            if (_connections.ContainsKey(server) && _connections[server] > 0)
                _connections[server]--;
        }
    }

    public void PrintConnections()
    {
        foreach (var (server, count) in _connections)
            Console.WriteLine($"  {server}: {count} active connections");
    }
}

// ---------------------------------------------------------------------------
// Q4 — Health-Aware Load Balancer
// ---------------------------------------------------------------------------
public class HealthAwareLoadBalancer
{
    // Inner class groups all per-server state together — cleaner than 4 parallel
    // dictionaries (one for health, one for failures, etc.).
    // `init` on Name means it can only be set at construction — it's immutable after.
    private class ServerState
    {
        public string Name { get; init; } = "";
        public bool IsHealthy { get; set; } = true;
        public int ConsecFailures { get; set; } = 0;
        public int ConsecSuccesses { get; set; } = 0;
    }

    private readonly List<ServerState> _servers = new();
    private int _currentIndex = 0;

    // Configurable thresholds so the same class works for different environments.
    // A slow database might need failThreshold:10 while a web server needs 3.
    private readonly int _failThreshold;
    private readonly int _recoverThreshold;

    public HealthAwareLoadBalancer(int failThreshold = 3, int recoverThreshold = 2)
    {
        _failThreshold = failThreshold;
        _recoverThreshold = recoverThreshold;
    }

    public void AddServer(string server) =>
        _servers.Add(new ServerState { Name = server });

    public string? GetServer()
    {
        // Limit scan to _servers.Count attempts — if every server is unhealthy,
        // we return null after one full loop rather than spinning forever.
        int attempts = 0;
        while (attempts < _servers.Count)
        {
            var state = _servers[_currentIndex];

            // Always advance the index regardless of health — this ensures
            // we don't get stuck retrying the same unhealthy server on every call.
            _currentIndex = (_currentIndex + 1) % _servers.Count;

            if (state.IsHealthy) return state.Name;
            attempts++;
        }
        return null; // All servers are down — caller should return 503.
    }

    public void ReportSuccess(string server)
    {
        var state = _servers.Find(s => s.Name == server);
        if (state == null) return;

        // Any success resets the failure streak — we only care about CONSECUTIVE
        // failures. One success means the server is responding again.
        state.ConsecFailures = 0;

        // Only track recovery progress if the server is currently marked DOWN.
        // A healthy server's success count doesn't need tracking.
        if (!state.IsHealthy)
        {
            state.ConsecSuccesses++;

            // Require M consecutive successes before bringing a server back.
            // This prevents flapping — a server that alternates success/failure
            // should stay DOWN, not bounce back online after a single lucky response.
            if (state.ConsecSuccesses >= _recoverThreshold)
            {
                state.IsHealthy = true;
                state.ConsecSuccesses = 0;
                Console.WriteLine($"[RECOVERED] {server} is back online");
            }
        }
    }

    public void ReportFailure(string server)
    {
        var state = _servers.Find(s => s.Name == server);
        if (state == null) return;

        // Any failure resets the recovery streak — mirror of ReportSuccess logic.
        state.ConsecSuccesses = 0;
        state.ConsecFailures++;

        // Only mark DOWN once — avoid spamming the log on every subsequent failure.
        if (state.ConsecFailures >= _failThreshold && state.IsHealthy)
        {
            state.IsHealthy = false;
            Console.WriteLine($"[DOWN] {server} marked as unhealthy");
        }
    }

    public void PrintStatus()
    {
        foreach (var s in _servers)
            Console.WriteLine($"  {s.Name}: {(s.IsHealthy ? "HEALTHY" : "DOWN")}  " +
                              $"(failures={s.ConsecFailures}, successes={s.ConsecSuccesses})");
    }
}

// ---------------------------------------------------------------------------
// Entry point — runs demos for all 4 questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Round Robin
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Round Robin Load Balancer       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var lb = new RoundRobinLoadBalancer();
        lb.AddServer("S1");
        lb.AddServer("S2");
        lb.AddServer("S3");

        Console.WriteLine("\n=== 6 requests across 3 servers ===");
        for (int i = 0; i < 6; i++)
            Console.WriteLine($"  Request {i + 1} → {lb.GetServer()}");
        // Expected: S1, S2, S3, S1, S2, S3

        Console.WriteLine("\n=== Remove S2, next 4 requests ===");
        lb.RemoveServer("S2");
        for (int i = 0; i < 4; i++)
            Console.WriteLine($"  Request {i + 1} → {lb.GetServer()}");
        // Expected: cycles between S1 and S3 only

        Console.WriteLine("\n=== Edge case: empty balancer ===");
        var emptyLb = new RoundRobinLoadBalancer();
        Console.WriteLine($"  GetServer on empty: {emptyLb.GetServer() ?? "null"}");

        // ===================================================================
        // Q2 DEMO — Weighted Round Robin
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Weighted Round Robin            ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var wlb = new WeightedRoundRobinLoadBalancer();
        wlb.AddServer("S1", weight: 1);  // 1 slot  → ~25% of traffic
        wlb.AddServer("S2", weight: 2);  // 2 slots → ~50% of traffic
        wlb.AddServer("S3", weight: 1);  // 1 slot  → ~25% of traffic

        Console.WriteLine("\n=== One full weighted cycle (8 requests) ===");
        for (int i = 0; i < 8; i++)
            Console.Write(wlb.GetServer() + " ");
        Console.WriteLine();
        // Expected: S1 S2 S2 S3 S1 S2 S2 S3

        Console.WriteLine("\n=== Distribution over 10,000 requests ===");
        var wlb2 = new WeightedRoundRobinLoadBalancer();
        wlb2.AddServer("S1", weight: 1);
        wlb2.AddServer("S2", weight: 2);
        wlb2.AddServer("S3", weight: 1);

        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 10000; i++)
        {
            string s = wlb2.GetServer()!;
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        foreach (var (server, count) in counts)
            Console.WriteLine($"  {server}: {count} requests ({count / 100.0:F1}%)");
        // Expected: S1≈25%, S2≈50%, S3≈25%

        // ===================================================================
        // Q3 DEMO — Least Connections
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Least Connections               ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var lcLb = new LeastConnectionsLoadBalancer();
        lcLb.AddServer("S1");
        lcLb.AddServer("S2");
        lcLb.AddServer("S3");

        Console.WriteLine("\n=== Assign 4 requests (all start at 0) ===");
        string? r1 = lcLb.GetServer(); Console.WriteLine($"  Request 1 → {r1}  (S1 now 1)");
        string? r2 = lcLb.GetServer(); Console.WriteLine($"  Request 2 → {r2}  (S2 now 1)");
        string? r3 = lcLb.GetServer(); Console.WriteLine($"  Request 3 → {r3}  (S3 now 1)");
        string? r4 = lcLb.GetServer(); Console.WriteLine($"  Request 4 → {r4}  (all tied, picks first)");

        Console.WriteLine("\n=== Release 2 connections from S1 ===");
        lcLb.ReleaseConnection(r1!);
        lcLb.ReleaseConnection(r1!);

        Console.WriteLine($"  Request 5 → {lcLb.GetServer()}  (S1 wins with 0 connections)");

        Console.WriteLine("\n=== Connection counts after all above ===");
        lcLb.PrintConnections();

        // ===================================================================
        // Q4 DEMO — Health-Aware Load Balancer
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q4: Health-Aware Load Balancer      ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var hLb = new HealthAwareLoadBalancer(failThreshold: 3, recoverThreshold: 2);
        hLb.AddServer("S1");
        hLb.AddServer("S2");
        hLb.AddServer("S3");

        Console.WriteLine("\n=== Report 3 consecutive failures on S2 ===");
        hLb.ReportFailure("S2");
        hLb.ReportFailure("S2");
        hLb.ReportFailure("S2"); // triggers [DOWN]

        Console.WriteLine("\n=== Next 4 requests (S2 is skipped) ===");
        for (int i = 0; i < 4; i++)
            Console.WriteLine($"  Request {i + 1} → {hLb.GetServer()}");
        // Expected: S1, S3, S1, S3

        Console.WriteLine("\n=== Recover S2 with 2 consecutive successes ===");
        hLb.ReportSuccess("S2");
        hLb.ReportSuccess("S2"); // triggers [RECOVERED]

        Console.WriteLine("\n=== Server status after recovery ===");
        hLb.PrintStatus();

        Console.WriteLine("\n=== Next 6 requests (S2 back in rotation) ===");
        for (int i = 0; i < 6; i++)
            Console.WriteLine($"  Request {i + 1} → {hLb.GetServer()}");
        // Expected: S1, S2, S3 cycling
    }
}
