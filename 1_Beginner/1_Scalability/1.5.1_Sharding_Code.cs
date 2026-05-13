// Q1. Implement a Hash-Based Shard Router
// Given N shards, route any key to the correct shard using modulo hashing. Support adding shards (with resharding cost) and looking up which shard owns a key.
// Complexity: GetShard O(1), CountKeysToRemap O(k) — k = number of keys

// Q2. Implement a Range-Based Shard Router
// Keys are strings (e.g., usernames). Route each key to a shard based on alphabetical ranges. Support dynamic range boundaries.
// Complexity: GetShard O(S) — S = number of shards (typically very small)

// Q3. Resharding Calculator — Modulo vs Consistent Hashing
// Show concretely how many keys move when adding a shard with modulo hashing vs consistent hashing.

// Q4. Hotspot Detector
// Given a stream of key accesses, detect which shard is receiving disproportionately more traffic. A shard is a hotspot if it receives more than threshold times its fair share.

using System;
using System.Collections.Generic;
using System.Linq;                        // Enumerable.Range, .Select
using System.Security.Cryptography;      // MD5 — used for stable, deterministic hashing in Q3
using System.Text;                        // Encoding.UTF8 — converts strings to bytes for MD5

// ---------------------------------------------------------------------------
// Q1 — Hash-Based Shard Router
// ---------------------------------------------------------------------------
public class HashShardRouter
{
    // Stores how many shards currently exist (e.g. 4 database nodes)
    private int _shardCount;

    // Constructor: called once at startup to set the initial number of shards
    public HashShardRouter(int shardCount)
    {
        _shardCount = shardCount;
    }

    // Given any string key (e.g. "user:alice"), deterministically returns which shard (0 to N-1) owns it
    public int GetShard(string key)
    {
        // GetHashCode() converts the string to an integer fingerprint — same string always gives same int
        // Math.Abs() guards against negative values (GetHashCode can return negative)
        int hash = Math.Abs(key.GetHashCode());

        // Modulo maps the hash into the range [0, _shardCount-1]
        // e.g. hash=17, shardCount=4 → shard 1
        // WHY: simplest way to spread keys evenly across N shards
        return hash % _shardCount;
    }

    // Simulates a resize: counts how many keys would move to a different shard
    // WHY: reveals the core weakness of modulo hashing — almost all keys remap when N changes
    public int CountKeysToRemap(List<string> keys, int newShardCount)
    {
        int moved = 0;

        foreach (var key in keys)
        {
            // Compute which shard this key currently lives on
            int oldShard = Math.Abs(key.GetHashCode()) % _shardCount;

            // Compute which shard this key WOULD live on after the resize
            int newShard = Math.Abs(key.GetHashCode()) % newShardCount;

            // If the shard assignment changed, this key must be migrated — expensive in production
            if (oldShard != newShard) moved++;
        }

        return moved;
    }

    // Updates the shard count in-place (router reconfigured after a resize)
    // WHY: all future GetShard calls must use the new N
    public void Resize(int newShardCount) => _shardCount = newShardCount;
}

// ---------------------------------------------------------------------------
// Q2 — Range-Based Shard Router
// ---------------------------------------------------------------------------
public class RangeShardRouter
{
    // Each entry maps a range boundary (inclusive upper end) to a shard ID.
    // e.g. ("f", 0) means all keys ≤ "f" go to shard 0.
    // WHY a list and not a dictionary: we need to iterate in sorted order to find the first range the key falls into.
    private readonly List<(string RangeEnd, int ShardId)> _ranges = new();

    // Adds a range: all keys ≤ rangeEnd go to shardId.
    // After inserting, re-sorts by rangeEnd so GetShard always scans in ascending order.
    // WHY sort on insert (not on every read): range changes happen rarely; reads happen constantly.
    public void AddRange(string rangeEnd, int shardId)
    {
        _ranges.Add((rangeEnd, shardId));

        // Sort lexicographically (Ordinal = byte-by-byte, no locale tricks) so ranges are in order
        _ranges.Sort((a, b) => string.Compare(a.RangeEnd, b.RangeEnd, StringComparison.Ordinal));
    }

    // Routes a key to the correct shard by scanning ranges in ascending order.
    // Returns the first shard whose rangeEnd >= key (i.e. the key falls within that range).
    public int GetShard(string key)
    {
        // Normalize to lowercase so "Alice" and "alice" route to the same shard
        string lowerKey = key.ToLowerInvariant();

        foreach (var (rangeEnd, shardId) in _ranges)
        {
            // Ordinal comparison: if key comes before or at the range boundary, it belongs here
            if (string.Compare(lowerKey, rangeEnd, StringComparison.Ordinal) <= 0)
                return shardId;
        }

        // Key is beyond all defined ranges — fall back to the last shard
        // WHY [^1]: C# index-from-end syntax, equivalent to _ranges[_ranges.Count - 1]
        return _ranges[^1].ShardId;
    }

    // Prints the range → shard mapping for debugging / interview demos
    public void PrintRanges()
    {
        string prev = "start";
        foreach (var (rangeEnd, shardId) in _ranges)
        {
            Console.WriteLine($"  Shard {shardId}: {prev} → {rangeEnd}");
            prev = rangeEnd;
        }
    }
}

// ---------------------------------------------------------------------------
// Q3 — Resharding Calculator: Modulo vs Consistent Hashing
// ---------------------------------------------------------------------------
public static class ReshardingCalculator
{
    // Modulo hashing: shard = hash(key) % N
    // Counts keys that move when shard count changes from oldShards to newShards
    public static int ModuloKeysRemapped(List<string> keys, int oldShards, int newShards)
    {
        int moved = 0;
        foreach (var key in keys)
        {
            int hash = Math.Abs(key.GetHashCode());

            // If the modulo result changes, the key must move to a different node
            if (hash % oldShards != hash % newShards)
                moved++;
        }
        return moved;
    }

    // Consistent hashing: place shards at fixed positions on a hash ring.
    // When a new shard is added, only keys between the new shard and its predecessor move.
    // Ideally ~1/N of keys move (vs ~(N-1)/N for modulo).
    public static int ConsistentHashKeysRemapped(List<string> keys, int oldShards, int newShards)
    {
        // Build the hash ring for both the old and new shard counts
        var ringBefore = BuildRing(oldShards);
        var ringAfter = BuildRing(newShards);

        int moved = 0;
        foreach (var key in keys)
        {
            // If the key's responsible shard differs between old and new ring, it must move
            if (GetServer(key, ringBefore) != GetServer(key, ringAfter))
                moved++;
        }
        return moved;
    }

    // Builds a consistent hash ring: maps each shard's position (uint) to its shard ID.
    // Returns the ring dictionary + a sorted list of positions for binary search.
    private static (Dictionary<uint, int> ring, List<uint> positions) BuildRing(int shardCount)
    {
        var ring = new Dictionary<uint, int>();
        var positions = new List<uint>();

        for (int i = 0; i < shardCount; i++)
        {
            // Use a stable hash (MD5) so shard positions are the same across runs.
            // WHY not GetHashCode: GetHashCode is randomized per process in .NET 5+ for security.
            uint pos = StableHash($"shard:{i}");
            ring[pos] = i;

            // Binary search insert keeps the list sorted without a full Sort() each time
            int idx = positions.BinarySearch(pos);
            if (idx < 0) positions.Insert(~idx, pos); // ~idx is the insertion point when not found
        }

        return (ring, positions);
    }

    // Finds which shard owns a key by walking clockwise on the ring to the nearest shard position.
    private static int GetServer(string key, (Dictionary<uint, int> ring, List<uint> positions) r)
    {
        uint pos = StableHash(key);

        // Binary search: find the first shard position >= key's position
        int idx = r.positions.BinarySearch(pos);

        // BinarySearch returns a negative value when the exact position isn't found;
        // ~idx gives the index of the next larger element (the clockwise successor shard)
        if (idx < 0) idx = ~idx;

        // Wrap around: if we're past the last shard, go back to shard 0 (ring wraps)
        if (idx >= r.positions.Count) idx = 0;

        return r.ring[r.positions[idx]];
    }

    // Produces a stable 32-bit hash using MD5 (first 4 bytes of the digest).
    // WHY MD5 here: we don't need cryptographic strength — just determinism and good distribution.
    // MD5 gives the same output on every run, unlike GetHashCode which is randomized in .NET 5+.
    private static uint StableHash(string key)
    {
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(key)); // hash the UTF-8 bytes of the key
        return BitConverter.ToUInt32(bytes, 0);                        // take the first 4 bytes as a uint
    }
}

// ---------------------------------------------------------------------------
// Q4 — Hotspot Detector
// ---------------------------------------------------------------------------
public class HotspotDetector
{
    // Number of shards to distribute traffic across — used to compute fair share per shard
    private readonly int _shardCount;

    // A shard is flagged as a hotspot if its hit count exceeds fairShare * _threshold.
    // Default 2.0 means "flag any shard receiving more than 2x its expected traffic."
    private readonly double _threshold;

    // Tracks how many requests each shard has received.
    // WHY Dictionary<int, int>: O(1) lookup and increment by shard ID on every request.
    private readonly Dictionary<int, int> _shardHits = new();

    // Running total of all recorded requests — needed to compute fairShare dynamically
    private int _totalRequests = 0;

    // Constructor: initializes all shard counters to 0 so we never get a KeyNotFoundException
    public HotspotDetector(int shardCount, double threshold = 2.0)
    {
        _shardCount = shardCount;
        _threshold = threshold;

        // Pre-populate all shard IDs so the dictionary is always complete
        for (int i = 0; i < shardCount; i++)
            _shardHits[i] = 0;
    }

    // Records a single request for a given key.
    // Uses modulo hashing to map the key to a shard — same logic as HashShardRouter.
    public void RecordRequest(string key)
    {
        // Route this key to its owning shard
        int shard = Math.Abs(key.GetHashCode()) % _shardCount;

        // Increment that shard's counter and the global total
        _shardHits[shard]++;
        _totalRequests++;
    }

    // Returns the list of shard IDs currently above the hotspot threshold.
    // WHY return a list: callers may want to trigger re-sharding or add replicas for each hot shard.
    public List<int> GetHotspots()
    {
        if (_totalRequests == 0) return new List<int>();

        // Expected hits per shard if traffic were perfectly even
        double fairShare = (double)_totalRequests / _shardCount;
        var hotspots = new List<int>();

        foreach (var (shard, hits) in _shardHits)
        {
            // Flag this shard if its traffic is more than `threshold` times the fair share
            if (hits > fairShare * _threshold)
                hotspots.Add(shard);
        }

        return hotspots;
    }

    // Prints a per-shard traffic report showing the ratio to fair share and hotspot flags
    public void PrintReport()
    {
        double fairShare = _totalRequests > 0 ? (double)_totalRequests / _shardCount : 0;
        Console.WriteLine($"  Total requests: {_totalRequests}  |  Fair share per shard: {fairShare:F0}");
        Console.WriteLine($"  Hotspot threshold: {_threshold}x fair share = {fairShare * _threshold:F0} hits");
        Console.WriteLine();

        foreach (var (shard, hits) in _shardHits)
        {
            // ratio > 1.0 means this shard is busier than average
            double ratio = fairShare > 0 ? hits / fairShare : 0;
            string flag = hits > fairShare * _threshold ? " ← HOTSPOT" : "";
            Console.WriteLine($"  Shard {shard}: {hits,5} hits ({ratio:F2}x fair share){flag}");
        }
    }
}





// ---------------------------------------------------------------------------
// Entry point — demos for all 4 questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Hash-Based Shard Router
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Hash-Based Shard Router         ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var router = new HashShardRouter(shardCount: 4);

        Console.WriteLine("\n=== Route keys to shards ===");
        // Same key always maps to the same shard — determinism means reads/writes always find the right node
        Console.WriteLine($"  user:alice → shard {router.GetShard("user:alice")}");
        Console.WriteLine($"  user:bob   → shard {router.GetShard("user:bob")}");
        Console.WriteLine($"  user:alice → shard {router.GetShard("user:alice")}  ← same as first call (determinism)");

        Console.WriteLine("\n=== Resharding cost: growing 4 → 5 shards ===");
        var keys = Enumerable.Range(0, 10000).Select(i => $"key:{i}").ToList();
        int moved = router.CountKeysToRemap(keys, newShardCount: 5);
        Console.WriteLine($"  Keys remapped: {moved} / {keys.Count}  ({moved * 100.0 / keys.Count:F1}%)");

        // ===================================================================
        // Q2 DEMO — Range-Based Shard Router
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Range-Based Shard Router        ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var rangeRouter = new RangeShardRouter();
        rangeRouter.AddRange("f", shardId: 0);   // A–F → Shard 0
        rangeRouter.AddRange("m", shardId: 1);   // G–M → Shard 1
        rangeRouter.AddRange("s", shardId: 2);   // N–S → Shard 2
        rangeRouter.AddRange("￿", shardId: 3);   // T–Z (and beyond) → Shard 3

        Console.WriteLine("\n=== Route keys by alphabetical range ===");
        Console.WriteLine($"  alice  → shard {rangeRouter.GetShard("alice")}   (a ≤ f → shard 0)");
        Console.WriteLine($"  henry  → shard {rangeRouter.GetShard("henry")}   (h ≤ m → shard 1)");
        Console.WriteLine($"  oscar  → shard {rangeRouter.GetShard("oscar")}   (o ≤ s → shard 2)");
        Console.WriteLine($"  victor → shard {rangeRouter.GetShard("victor")}   (v > s → shard 3)");

        Console.WriteLine("\n=== Range boundaries ===");
        rangeRouter.PrintRanges();

        // ===================================================================
        // Q3 DEMO — Resharding Calculator
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Modulo vs Consistent Hashing    ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        Console.WriteLine("\n=== Growing from 4 → 5 shards (10,000 keys) ===");
        int moduloMoved = ReshardingCalculator.ModuloKeysRemapped(keys, 4, 5);
        int consistentMoved = ReshardingCalculator.ConsistentHashKeysRemapped(keys, 4, 5);

        Console.WriteLine($"  Modulo hashing:     {moduloMoved,5} / {keys.Count} keys moved ({moduloMoved * 100.0 / keys.Count:F1}%)");
        Console.WriteLine($"  Consistent hashing: {consistentMoved,5} / {keys.Count} keys moved ({consistentMoved * 100.0 / keys.Count:F1}%)");
        Console.WriteLine("\n  → Consistent hashing moves ~1/N of keys; modulo moves ~(N-1)/N.");
        Console.WriteLine("    At scale, that difference is millions of records vs billions.");

        // ===================================================================
        // Q4 DEMO — Hotspot Detector
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q4: Hotspot Detector                ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var detector = new HotspotDetector(shardCount: 4, threshold: 2.0);

        // Simulate normal distributed traffic across 1000 random users
        var rand = new Random(42);
        for (int i = 0; i < 1000; i++)
            detector.RecordRequest($"user:{rand.Next(1000)}");

        // Simulate a celebrity / viral user causing a traffic spike on one key
        for (int i = 0; i < 500; i++)
            detector.RecordRequest("user:celebrity_99");

        Console.WriteLine();
        detector.PrintReport();

        var hotspots = detector.GetHotspots();
        Console.WriteLine($"\n  Hotspot shards: [{string.Join(", ", hotspots)}]");
        Console.WriteLine("  → These shards need replicas or key splitting to handle the load.");
    }
}
