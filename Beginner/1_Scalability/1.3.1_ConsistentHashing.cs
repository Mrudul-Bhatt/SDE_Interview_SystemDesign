// Q1. Implement a Basic Consistent Hash Ring
// Given a list of servers, implement a consistent hash ring where you can:
//   - Add a server
//   - Remove a server
//   - Find which server a given key maps to
//
// Key C# Tools:
//   List<uint>.BinarySearch() — O(log n) position lookup
//   MD5                       — stable hash across runs (GetHashCode() is NOT stable)
//   Bitwise complement ~idx   — converts BinarySearch "not found" to insertion point

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// ---------------------------------------------------------------------------
// Q1 — Basic Consistent Hash Ring
// ---------------------------------------------------------------------------
public class ConsistentHashRing
{
    // Dictionary maps a hash position (uint) to the server name that owns that position.
    // uint gives us a 0–4,294,967,295 address space — the "ring" is just the number line
    // from 0 to uint.MaxValue that wraps around.
    private readonly Dictionary<uint, string> _ring = new();

    // Separate sorted list of just the positions (keys of _ring).
    // We keep this sorted so BinarySearch() can find the correct server in O(log n).
    // Dictionary alone has no ordering, so we need this parallel structure.
    private readonly List<uint> _sortedKeys = new();

    private uint Hash(string key)
    {
        // MD5 produces a stable, deterministic hash — the same input always gives the
        // same output across different runs, machines, and .NET versions.
        // We cannot use GetHashCode() here because .NET deliberately randomises it on
        // each process startup (security feature), so "server_A" would land at a
        // different ring position every time the app restarts — breaking consistency.
        using var md5 = MD5.Create();

        // Convert the string to raw bytes first because MD5 operates on bytes, not chars.
        // UTF8 is the standard encoding — consistent across all platforms.
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(key));

        // MD5 produces 16 bytes (128 bits). We only need a single uint (4 bytes / 32 bits)
        // to represent a position on the ring. We take the first 4 bytes — this is enough
        // to give ~4 billion distinct positions, far more than we'll ever have servers.
        return BitConverter.ToUInt32(bytes, 0);
    }

    public void AddServer(string server)
    {
        // Hash the server name to get its fixed position on the ring.
        // The same server name always hashes to the same position, so the ring
        // is stable — adding server_A twice lands on the same spot.
        uint position = Hash(server);

        // Store the mapping: position → server name.
        // This lets GetServer() look up which server owns a given ring position.
        _ring[position] = server;

        // Find where this position would sit in the sorted list.
        // BinarySearch returns the index if found (>= 0), or a negative bitwise
        // complement of the insertion index if not found (< 0).
        int idx = _sortedKeys.BinarySearch(position);

        // Only insert if the position isn't already in the list (idx < 0 means not found).
        // ~idx flips all bits of the negative value, giving us the correct insertion index
        // to keep the list sorted.
        //
        // Why ~idx works:
        //   _sortedKeys = [100, 400, 700], insert 250
        //   BinarySearch(250) → not found, insertion point is 1 → returns ~1 = -2
        //   ~(-2) = 1 ← insert at index 1 → [100, 250, 400, 700] ✓
        if (idx < 0)
            _sortedKeys.Insert(~idx, position);
    }

    public void RemoveServer(string server)
    {
        // Recompute the same position using the same hash — deterministic, so we always
        // get back the exact position this server was inserted at.
        uint position = Hash(server);

        // Remove from the dictionary so this position no longer maps to this server.
        _ring.Remove(position);

        // Find the position in the sorted list.
        // BinarySearch is O(log n) — much faster than a linear scan for large rings.
        int idx = _sortedKeys.BinarySearch(position);

        // idx >= 0 means we found it. Remove it to keep _sortedKeys in sync with _ring.
        if (idx >= 0)
            _sortedKeys.RemoveAt(idx);
    }

    public string? GetServer(string key)
    {
        // No servers on the ring — nothing to route to.
        if (_ring.Count == 0) return null;

        // Hash the key to find its position on the ring.
        // This is the "client request" landing at a point on the circle.
        uint position = Hash(key);

        // Binary search for this position in the sorted server positions.
        // O(log n) — this is why we maintain _sortedKeys as a sorted list.
        int idx = _sortedKeys.BinarySearch(position);

        // If idx < 0, the exact position wasn't found (almost always the case).
        // ~idx gives the index of the first server position that is GREATER than
        // our key's position — i.e., the next server clockwise on the ring.
        if (idx < 0)
            idx = ~idx;

        // If idx is past the end of the list, we've gone beyond the last server.
        // Wrap around to index 0 — the ring connects the end back to the beginning,
        // so the first server (smallest position) is the next clockwise server.
        if (idx >= _sortedKeys.Count)
            idx = 0;

        // Look up which server owns this ring position and return its name.
        return _ring[_sortedKeys[idx]];
    }

    public void PrintRing()
    {
        Console.WriteLine("\nRing positions (sorted):");
        foreach (uint pos in _sortedKeys)
            Console.WriteLine($"  {pos,12} → {_ring[pos]}");
    }
}

// ---------------------------------------------------------------------------
// Q2 — Consistent Hash Ring with Virtual Nodes
// Each physical server gets _virtualNodes positions on the ring so that
// load is distributed evenly even with only a handful of real servers.
// ---------------------------------------------------------------------------
public class ConsistentHashRingWithVNodes
{
    private readonly Dictionary<uint, string> _ring = new();
    private readonly List<uint> _sortedKeys = new();

    // How many virtual positions each real server occupies on the ring.
    // More virtual nodes = more even distribution, but more memory.
    // 100–200 is a common default in production systems (e.g. Cassandra uses 256).
    private readonly int _virtualNodes;

    public ConsistentHashRingWithVNodes(int virtualNodes = 100)
    {
        _virtualNodes = virtualNodes;
    }

    private uint Hash(string key)
    {
        // Same stable MD5 approach as Q1 — must be consistent across restarts.
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToUInt32(bytes, 0);
    }

    public void AddServer(string server)
    {
        for (int i = 0; i < _virtualNodes; i++)
        {
            // Each virtual node gets a unique key by appending "#i" to the server name.
            // "server_A#0", "server_A#1", ... "server_A#99" → 100 distinct ring positions.
            // Without this suffix every virtual node would hash to the same position.
            uint position = Hash($"{server}#{i}");

            // All virtual nodes of the same physical server map back to that server's name.
            // So GetServer() returns the physical server regardless of which vnode was hit.
            _ring[position] = server;

            int idx = _sortedKeys.BinarySearch(position);
            if (idx < 0)
                _sortedKeys.Insert(~idx, position);
        }
    }

    public void RemoveServer(string server)
    {
        // Must remove ALL virtual nodes for this server, not just one.
        // Leaving any behind would still route some keys to a removed server.
        for (int i = 0; i < _virtualNodes; i++)
        {
            uint position = Hash($"{server}#{i}");
            _ring.Remove(position);

            int idx = _sortedKeys.BinarySearch(position);
            if (idx >= 0)
                _sortedKeys.RemoveAt(idx);
        }
    }

    public string? GetServer(string key)
    {
        if (_ring.Count == 0) return null;

        uint position = Hash(key);
        int idx = _sortedKeys.BinarySearch(position);

        // Same clockwise-lookup logic as Q1.
        if (idx < 0) idx = ~idx;
        if (idx >= _sortedKeys.Count) idx = 0;

        return _ring[_sortedKeys[idx]];
    }

    // Returns how many of the given keys landed on each server.
    // Used to verify that virtual nodes give near-equal distribution.
    public Dictionary<string, int> GetDistribution(List<string> keys)
    {
        var distribution = new Dictionary<string, int>();
        foreach (var key in keys)
        {
            string? server = GetServer(key);
            if (server != null)
                distribution[server] = distribution.GetValueOrDefault(server) + 1;
        }
        return distribution;
    }

    // ---------------------------------------------------------------------------
    // Q4 — Find K Replica Servers for a Key
    // For replication (like Cassandra's replication factor), find the next K unique
    // physical servers clockwise from the key's position. Virtual nodes of the same
    // server do NOT count as separate replicas.
    // In real distributed databases (Cassandra, DynamoDB), you don't store data on just one server — you store copies on K servers for fault tolerance. If one goes down, the others still have the data.
    // The question: Given a key, find the next K unique physical servers clockwise on the ring.
    // The tricky part: with virtual nodes, one physical server has 100+ positions on the ring. Walking clockwise you might hit node_1#45, then node_1#67, then node_2#3. That's still only 2 unique servers, not 3.
    // ---------------------------------------------------------------------------
    public List<string> GetReplicaServers(string key, int k)
    {
        // _ring, _sortedKeys, Hash() are all accessible here because this method
        // lives inside ConsistentHashRingWithVNodes — no need for a separate class.
        if (_ring.Count == 0) return new List<string>();

        uint position = Hash(key);
        int startIdx = _sortedKeys.BinarySearch(position);
        if (startIdx < 0) startIdx = ~startIdx;
        if (startIdx >= _sortedKeys.Count) startIdx = 0;

        var replicas = new List<string>();
        var seenServers = new HashSet<string>();
        int i = 0;

        while (replicas.Count < k && i < _sortedKeys.Count)
        {
            int idx = (startIdx + i) % _sortedKeys.Count; // Walk clockwise, wrap at end
            string server = _ring[_sortedKeys[idx]]; // Which physical server is this vnode?

            // Skip virtual nodes of already-selected physical servers —
            // the same machine should not count as two different replicas.
            if (!seenServers.Contains(server)) // Is this a NEW physical server?
            {
                replicas.Add(server);
                seenServers.Add(server);
            }
            i++;
        }

        return replicas;
    }
}

// ---------------------------------------------------------------------------
// Q3 — Count Keys Redistributed After Adding a Server
// Given a set of keys already distributed across N servers, how many keys need
// to move when you add a new server? The answer should approach 1/N of total keys.
// ---------------------------------------------------------------------------
public static class KeyRedistribution
{
    // Static helper class (not named after the method) so C# doesn't complain
    // about a method having the same name as its enclosing class.
    public static int CountMoved(List<string> existingServers, string newServer, List<string> keys)
    {
        // Snapshot distribution BEFORE adding the new server.
        var ringBefore = new ConsistentHashRing();
        foreach (var s in existingServers)
            ringBefore.AddServer(s);

        // Snapshot distribution AFTER adding the new server.
        var ringAfter = new ConsistentHashRing();
        foreach (var s in existingServers)
            ringAfter.AddServer(s);
        ringAfter.AddServer(newServer);

        // Count keys that land on a different server after the addition.
        int moved = 0;
        foreach (var key in keys)
        {
            if (ringBefore.GetServer(key) != ringAfter.GetServer(key))
                moved++;
        }

        return moved;
    }
}

// ---------------------------------------------------------------------------
// Q5 — Weighted Consistent Hashing
// Servers have different capacities. A server with weight 3 should receive
// approximately 3x more keys than a server with weight 1.
// ---------------------------------------------------------------------------
public class WeightedConsistentHashRing
{
    private readonly Dictionary<uint, string> _ring = new();
    private readonly List<uint> _sortedKeys = new();
    private readonly int _baseVirtualNodes;

    public WeightedConsistentHashRing(int baseVirtualNodes = 100)
    {
        _baseVirtualNodes = baseVirtualNodes;
    }

    private uint Hash(string key)
    {
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToUInt32(bytes, 0);
    }

    // Weight multiplies the number of virtual nodes → proportionally more ring arc → more keys.
    // weight:1 → 100 vnodes, weight:2 → 200 vnodes, weight:3 → 300 vnodes, etc.
    public void AddServer(string server, int weight = 1)
    {
        int vnodes = _baseVirtualNodes * weight;
        for (int i = 0; i < vnodes; i++)
        {
            uint position = Hash($"{server}#{i}");
            _ring[position] = server;

            int idx = _sortedKeys.BinarySearch(position);
            if (idx < 0)
                _sortedKeys.Insert(~idx, position);
        }
    }

    public string? GetServer(string key)
    {
        if (_ring.Count == 0) return null;

        uint position = Hash(key);
        int idx = _sortedKeys.BinarySearch(position);

        if (idx < 0) idx = ~idx;
        if (idx >= _sortedKeys.Count) idx = 0;

        return _ring[_sortedKeys[idx]];
    }
}

// ---------------------------------------------------------------------------
// Entry point — runs demos for all 5 questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Basic Consistent Hash Ring
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Basic Consistent Hash Ring      ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var ring = new ConsistentHashRing();

        Console.WriteLine("\n=== Adding servers ===");
        ring.AddServer("server_A");
        ring.AddServer("server_B");
        ring.AddServer("server_C");
        ring.PrintRing();

        Console.WriteLine("\n=== Key lookups ===");
        string[] keys = { "user:1", "user:2", "user:3", "order:99", "product:42" };
        foreach (var key in keys)
            Console.WriteLine($"  {key,-15} → {ring.GetServer(key)}");

        Console.WriteLine("\n=== Consistency check (same key always returns same server) ===");
        string result1 = ring.GetServer("user:1")!;
        string result2 = ring.GetServer("user:1")!;
        Console.WriteLine($"  user:1 call 1: {result1}");
        Console.WriteLine($"  user:1 call 2: {result2}");
        Console.WriteLine($"  Consistent:    {result1 == result2}");

        Console.WriteLine("\n=== Removing server_B (only ~1/3 of keys remap) ===");
        Console.WriteLine("Before:");
        foreach (var key in keys)
            Console.WriteLine($"  {key,-15} → {ring.GetServer(key)}");

        ring.RemoveServer("server_B");

        Console.WriteLine("After:");
        foreach (var key in keys)
            Console.WriteLine($"  {key,-15} → {ring.GetServer(key)}");

        Console.WriteLine("\n=== Edge case: empty ring ===");
        var emptyRing = new ConsistentHashRing();
        Console.WriteLine($"  GetServer on empty ring: {emptyRing.GetServer("user:1") ?? "null"}");

        Console.WriteLine("\n=== Edge case: single server gets all keys ===");
        var singleRing = new ConsistentHashRing();
        singleRing.AddServer("only_server");
        Console.WriteLine($"  user:1   → {singleRing.GetServer("user:1")}");
        Console.WriteLine($"  user:999 → {singleRing.GetServer("user:999")}");

        // ===================================================================
        // Q2 DEMO — Virtual Nodes
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Virtual Nodes                   ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        Console.WriteLine("\n=== Distribution WITHOUT virtual nodes (3 servers, 1 position each) ===");
        var basicRing = new ConsistentHashRing();
        basicRing.AddServer("server_A");
        basicRing.AddServer("server_B");
        basicRing.AddServer("server_C");
        basicRing.PrintRing();

        var basicKeys = Enumerable.Range(0, 10000).Select(i => $"key:{i}").ToList();
        var basicCounts = new Dictionary<string, int>();
        foreach (var k in basicKeys)
        {
            string s = basicRing.GetServer(k)!;
            basicCounts[s] = basicCounts.GetValueOrDefault(s) + 1;
        }
        foreach (var (server, count) in basicCounts.OrderBy(x => x.Key))
            Console.WriteLine($"  {server}: {count,5} keys ({count / 100.0:F1}%)");

        Console.WriteLine("\n=== Distribution WITH virtual nodes (100 vnodes per server) ===");
        var vnodeRing = new ConsistentHashRingWithVNodes(virtualNodes: 100);
        vnodeRing.AddServer("server_A");
        vnodeRing.AddServer("server_B");
        vnodeRing.AddServer("server_C");

        var allKeys = Enumerable.Range(0, 10000).Select(i => $"key:{i}").ToList();
        var dist = vnodeRing.GetDistribution(allKeys);
        foreach (var (server, count) in dist.OrderBy(x => x.Key))
            Console.WriteLine($"  {server}: {count,5} keys ({count / 100.0:F1}%)");

        Console.WriteLine("\n=== Removing server_B (only its 100 vnodes vacate the ring) ===");
        Console.WriteLine("Before removal:");
        string[] sampleKeys = { "user:1", "user:2", "user:3", "order:99", "product:42" };
        foreach (var k in sampleKeys)
            Console.WriteLine($"  {k,-15} → {vnodeRing.GetServer(k)}");

        vnodeRing.RemoveServer("server_B");

        Console.WriteLine("After removal:");
        foreach (var k in sampleKeys)
            Console.WriteLine($"  {k,-15} → {vnodeRing.GetServer(k)}");

        // ===================================================================
        // Q3 DEMO — Count Keys Redistributed
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Keys Redistributed              ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var servers = new List<string> { "A", "B", "C" };
        var testKeys = Enumerable.Range(0, 10000).Select(i => $"key:{i}").ToList();

        int moved = KeyRedistribution.CountMoved(servers, "D", testKeys);
        Console.WriteLine($"\n  Keys moved: {moved} / {testKeys.Count}");
        Console.WriteLine($"  Percentage: {moved * 100.0 / testKeys.Count:F1}%");
        Console.WriteLine("  Expected:   ~25% (1/N = 1/4 when adding 4th server)");

        // ===================================================================
        // Q4 DEMO — Find K Replica Servers
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q4: K Replica Servers               ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var replicaRing = new ConsistentHashRingWithVNodes(100);
        replicaRing.AddServer("node_1");
        replicaRing.AddServer("node_2");
        replicaRing.AddServer("node_3");

        var replicas = replicaRing.GetReplicaServers("user:alice", k: 2);
        Console.WriteLine($"\n  Replicas for 'user:alice' (k=2): {string.Join(", ", replicas)}");

        replicas = replicaRing.GetReplicaServers("order:99", k: 3);
        Console.WriteLine($"  Replicas for 'order:99'   (k=3): {string.Join(", ", replicas)}");

        // ===================================================================
        // Q5 DEMO — Weighted Consistent Hashing
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q5: Weighted Consistent Hashing     ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var weightedRing = new WeightedConsistentHashRing(baseVirtualNodes: 100);
        weightedRing.AddServer("small_server", weight: 1);  // 100 vnodes → ~25%
        weightedRing.AddServer("medium_server", weight: 2);  // 200 vnodes → ~50%
        weightedRing.AddServer("large_server", weight: 1);  // 100 vnodes → ~25%

        var weightedKeys = Enumerable.Range(0, 10000).Select(i => $"key:{i}").ToList();
        var weightedDist = new Dictionary<string, int>();
        foreach (var key in weightedKeys)
        {
            string? server = weightedRing.GetServer(key);
            if (server != null)
                weightedDist[server] = weightedDist.GetValueOrDefault(server) + 1;
        }

        Console.WriteLine();
        foreach (var (server, count) in weightedDist.OrderBy(x => x.Key))
            Console.WriteLine($"  {server,-15}: {count,5} keys ({count / 100.0:F1}%)");
        Console.WriteLine("  Expected: small≈25%, medium≈50%, large≈25%");
    }
}
