// Question 1: Implement Round Robin Load Balancer (most commonly asked)
// "Design a thread-safe round robin load balancer supporting add/remove servers."

// Question 2: Implement Least Connections (medium difficulty)
// "Implement a load balancer that always routes to the server with fewest active connections."
// Two implementations: PriorityQueue O(log n) and linear scan O(n).

// Question 3: Design a Rate-Limited Load Balancer (hard)
// "Extend your load balancer to reject requests if any server exceeds 1000 req/min."

using System;                       // Math.Abs, DateTime, Exception, InvalidOperationException
using System.Collections.Generic;   // List<T>, Dictionary<T,T>, Queue<T>, PriorityQueue<T,T>
using System.Threading;             // ReaderWriterLockSlim, Interlocked

// ---------------------------------------------------------------------------
// Question 1 — Round Robin Load Balancer
// ---------------------------------------------------------------------------
public class LoadBalancer
{
    // List<string> holds the server addresses/names (e.g. "192.168.1.1:8080").
    // WHY List: we need index-based access (O(1)) for the modulo round-robin formula.
    private List<string> _servers = new();

    // Monotonically increasing counter — each GetServer() call increments it.
    // WHY a counter instead of an index: Interlocked.Increment can safely bump it
    // from multiple threads without a lock, giving us lock-free sequencing.
    private int _counter = 0;

    // ReaderWriterLockSlim allows MANY threads to read _servers simultaneously,
    // but gives exclusive access to the single thread that is adding/removing a server.
    // WHY not a plain lock: plain lock blocks ALL readers even when no write is happening.
    // Under high request load (thousands of req/sec) that contention is expensive.
    // ReaderWriterLockSlim lets all GET threads run in parallel — only ADD/REMOVE block.
    private readonly ReaderWriterLockSlim _lock = new();

    // Adds a server to the rotation.
    public void AddServer(string server)
    {
        // Acquire an exclusive write lock — no reads can happen while we mutate the list.
        // WHY exclusive: List<string> is not thread-safe; a concurrent read during Add
        // could see a partially-resized internal array and throw or return garbage.
        _lock.EnterWriteLock();
        try
        {
            // Append the new server at the end of the list.
            // WHY not insert at a specific position: round-robin doesn't care about order,
            // and appending is O(1) amortized vs O(n) for arbitrary insertion.
            _servers.Add(server);
        }
        finally
        {
            // ALWAYS release in a finally block — if Add threw an exception we must
            // still unlock, otherwise every future caller deadlocks.
            _lock.ExitWriteLock();
        }
    }

    // Removes a server from the rotation (e.g. when a node goes down or is scaled in).
    public void RemoveServer(string server)
    {
        // Exclusive write lock — same reasoning as AddServer.
        _lock.EnterWriteLock();
        try
        {
            // List.Remove() scans linearly for the first matching element and removes it.
            // O(n) — acceptable because server list changes are rare config events, not
            // per-request operations.
            _servers.Remove(server);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // Returns the next server in round-robin order. Thread-safe for concurrent calls.
    public string GetServer()
    {
        // Shared read lock — multiple threads can hold this simultaneously.
        // WHY read lock here: we are only reading _servers, never writing it.
        // The counter is updated atomically below with Interlocked, so no write lock needed for it.
        _lock.EnterReadLock();
        try
        {
            // Guard against an empty list — caller should get a clear error, not an IndexOutOfRangeException.
            if (_servers.Count == 0)
                throw new InvalidOperationException("No servers available");

            // Interlocked.Increment atomically increments _counter and returns the NEW value.
            // WHY Interlocked: without it, two threads could both read 5, both write 6,
            // and both route to the same server — breaking the round-robin guarantee.
            // Math.Abs guards against integer overflow wrap-around producing a negative value,
            // which would make the modulo negative and crash the array access.
            int idx = Math.Abs(Interlocked.Increment(ref _counter));

            // Modulo maps the ever-growing counter into [0, serverCount-1].
            // e.g. counter=7, 3 servers → index 1 → second server.
            // This is why we need index-based access — sets/queues don't support this.
            return _servers[idx % _servers.Count];
        }
        finally
        {
            // Always release the read lock, even if an exception was thrown above.
            _lock.ExitReadLock();
        }
    }
}

// Question 2a — Least Connections using PriorityQueue  [O(log n) per Acquire]
// ---------------------------------------------------------------------------
// WHEN TO USE: many servers (>20) or very high request rate where scanning adds up.
// TRADE-OFF:   Release() cannot update the heap, so heap priorities drift over time.
public class LeastConnectionsBalancerPriorityQueue
{
    // Min-heap keeps the server with the fewest connections at the top.
    // PriorityQueue<TElement, TPriority>: TElement = server name, TPriority = connection count.
    // WHY min-heap: Dequeue() finds the minimum in O(log n) vs O(n) for a linear scan.
    // NOTE: PriorityQueue was introduced in .NET 6 -- requires net6.0+ in the .csproj.
    private readonly PriorityQueue<string, int> _heap = new();

    // Source-of-truth for connection counts.
    // WHY needed alongside the heap: PriorityQueue cannot expose or update a stored priority --
    // without this dictionary we have no way to read the real count on Release().
    private readonly Dictionary<string, int> _connections = new();

    // Single exclusive lock covering both structures.
    // WHY one lock for both: every operation reads AND writes both atomically --
    // a partial update (dictionary changed, heap not yet) would corrupt routing.
    private readonly object _lock = new();

    // Registers a new server with 0 active connections.
    public void AddServer(string server)
    {
        lock (_lock)
        {
            // Initialize count to 0 so Release() can decrement safely without going negative.
            _connections[server] = 0;

            // Enqueue with priority 0 -- lowest value = top of the min-heap.
            _heap.Enqueue(server, 0);
        }
    }

    // Routes a request to the server with the fewest active connections. O(log n).
    public string Acquire()
    {
        lock (_lock)
        {
            // Dequeue pops the minimum-priority (fewest-connections) server in O(log n).
            // WHY Dequeue instead of Peek: we must remove-then-reinsert to change priority,
            // since PriorityQueue has no UpdatePriority operation.
            var server = _heap.Dequeue();

            // Increment the authoritative dictionary count.
            _connections[server]++;

            // Reinsert with the new (higher) priority so the heap stays ordered correctly.
            // WHY reinsert: the old heap entry still holds the pre-increment priority;
            // leaving it would make this server look cheaper than it really is.
            _heap.Enqueue(server, _connections[server]);

            return server;
        }
    }

    // Decrements connection count when a request finishes.
    public void Release(string server)
    {
        lock (_lock)
        {
            // Update only the dictionary -- the stale heap entry (with the higher old priority)
            // remains in the heap. Over many releases, heap priorities drift above real counts.
            // The server is still routed correctly overall because the dictionary stays accurate,
            // but the heap may not always pick the true minimum on the next Acquire().
            // Fix: lazy-deletion pattern or an indexed heap with decrease-key support.
            _connections[server]--;
        }
    }

    // Prints real counts from the dictionary (heap priorities may have drifted).
    public void PrintConnections()
    {
        foreach (var (server, count) in _connections)
            Console.WriteLine($"  {server}: {count} active connections");
    }
}

// ---------------------------------------------------------------------------
// Question 2b -- Least Connections without PriorityQueue  [O(n) per Acquire]
// ---------------------------------------------------------------------------
// WHEN TO USE: few servers (<20), or .NET versions before 6 where PriorityQueue is unavailable.
// TRADE-OFF:   Acquire() scans all servers every call, but Release() is always perfectly accurate.
public class LeastConnectionsBalancerWithoutPriorityQueue
{
    // Dictionary maps server name to its current active connection count.
    // WHY Dictionary (not List): O(1) increment/decrement by name on every Acquire/Release.
    // A list would require an O(n) name scan before we could update the count.
    private readonly Dictionary<string, int> _connections = new();

    // Exclusive lock -- every Acquire reads the entire dictionary to find the minimum then
    // writes to the winner, so read and write must be atomic together.
    private readonly object _lock = new();

    // Registers a new server with 0 active connections.
    public void AddServer(string server)
    {
        lock (_lock)
        {
            // Start at 0 so this server is immediately eligible for the next request.
            _connections[server] = 0;
        }
    }

    // Routes a request to the server with the fewest connections via a linear scan. O(n).
    public string Acquire()
    {
        lock (_lock)
        {
            // Scan every server's count to find the minimum.
            // O(n) where n = number of servers -- acceptable when n is small (typically <100).
            string best  = null;
            int minConns = int.MaxValue;

            foreach (var (server, count) in _connections)
            {
                // Update best whenever we find a server with a strictly lower count.
                if (count < minConns)
                {
                    minConns = count;
                    best     = server;
                }
            }

            // Increment BEFORE returning -- marks the connection active immediately.
            // WHY before return: without this, two concurrent threads could both read count=0,
            // both pick the same server, then both increment -- defeating the whole algorithm.
            if (best != null) _connections[best]++;

            return best;
        }
    }

    // Decrements connection count when a request finishes. Always exact -- no heap drift.
    public void Release(string server)
    {
        lock (_lock)
        {
            // Guard against underflow: if Release() is called more times than Acquire() due to a
            // caller bug, clamp at 0 rather than going negative and corrupting future routing.
            if (_connections.ContainsKey(server) && _connections[server] > 0)
                _connections[server]--;
        }
    }

    // Prints current counts -- always accurate since there is no heap to drift.
    public void PrintConnections()
    {
        foreach (var (server, count) in _connections)
            Console.WriteLine($"  {server}: {count} active connections");
    }
}

// ---------------------------------------------------------------------------
// Question 3 — Rate-Limited Load Balancer
// ---------------------------------------------------------------------------
public class RateLimitedBalancer
{
    // Per-server sliding window: maps each server to the timestamps of its recent requests.
    // WHY Queue<DateTime>: a queue is FIFO — old timestamps at the front, new ones at the back.
    // This lets us efficiently evict timestamps older than 1 minute by Dequeue()-ing from the front
    // without scanning the whole collection.
    private readonly Dictionary<string, Queue<DateTime>> _requestTimes = new();

    // Hard limit: more than 1000 requests in any 60-second window = server at capacity.
    // const means this is inlined at compile time — no runtime allocation or lookup needed.
    private const int MaxRequestsPerMinute = 1000;

    // Tries each server in order and returns the first one that is under its rate limit.
    // WHY accept the server list as a parameter: the balancer is stateless about server membership —
    // callers control which servers exist; this class only tracks request counts.
    public string GetServer(List<string> servers)
    {
        // Capture the current UTC time ONCE and reuse it for all servers in this call.
        // WHY UTC: avoids daylight-saving-time ambiguity — clocks don't jump backward in UTC.
        var now = DateTime.UtcNow;

        // "cutoff" is the oldest timestamp that still counts within the sliding window.
        // Any timestamp before this point is outside the 1-minute window and must be discarded.
        var cutoff = now.AddMinutes(-1);

        foreach (var server in servers)
        {
            // Lazily initialize the request-time queue for servers we haven't seen before.
            // WHY lazy: we don't know the server list at construction time.
            if (!_requestTimes.ContainsKey(server))
                _requestTimes[server] = new Queue<DateTime>();

            var times = _requestTimes[server];

            // SLIDING WINDOW EVICTION: remove all timestamps that have aged out of the 1-minute window.
            // WHY a while loop (not foreach): we're modifying the collection as we scan it.
            // WHY Peek before Dequeue: Peek is O(1) and avoids the overhead of actually removing
            // an item only to decide we don't need to.
            while (times.Count > 0 && times.Peek() < cutoff)
                times.Dequeue(); // This timestamp is older than 1 minute — it no longer counts.

            // After eviction, times.Count is the number of requests in the last 60 seconds.
            // If it's below the cap, this server can accept one more request.
            if (times.Count < MaxRequestsPerMinute)
            {
                // Record this request's timestamp so future calls can count it in the window.
                times.Enqueue(now);

                // Return this server — first available server under its rate limit wins.
                return server;
            }
            // If we reach here, this server is at capacity — try the next one.
        }

        // All servers exhausted their rate limit — reject the request entirely.
        // WHY throw instead of returning null: the caller must handle this explicitly;
        // silently returning null would cause a NullReferenceException deep in the stack.
        throw new Exception("All servers at capacity");
    }
}

// ---------------------------------------------------------------------------
// Entry point — demos for all 3 questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Round Robin Load Balancer
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Round Robin Load Balancer       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var lb = new LoadBalancer();
        lb.AddServer("S1");
        lb.AddServer("S2");
        lb.AddServer("S3");

        Console.WriteLine("\n=== 6 requests across 3 servers ===");
        for (int i = 0; i < 6; i++)
            Console.WriteLine($"  Request {i + 1} → {lb.GetServer()}");
        // Expected: S1 S2 S3 S1 S2 S3

        Console.WriteLine("\n=== Remove S2, next 4 requests ===");
        lb.RemoveServer("S2");
        for (int i = 0; i < 4; i++)
            Console.WriteLine($"  Request {i + 1} → {lb.GetServer()}");
        // Expected: cycles between S1 and S3 only

        // ===================================================================
        // Q2a DEMO — Least Connections with PriorityQueue  O(log n)
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2a: Least Connections (Heap O(logn))║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var heapLcb = new LeastConnectionsBalancerPriorityQueue();
        heapLcb.AddServer("S1");
        heapLcb.AddServer("S2");
        heapLcb.AddServer("S3");

        Console.WriteLine("\n=== Acquire 3 connections (all start at 0) ===");
        string r1 = heapLcb.Acquire(); Console.WriteLine($"  Acquired → {r1}");
        string r2 = heapLcb.Acquire(); Console.WriteLine($"  Acquired → {r2}");
        string r3 = heapLcb.Acquire(); Console.WriteLine($"  Acquired → {r3}");

        Console.WriteLine($"\n=== Release {r1}, next acquire should go back to {r1} ===");
        heapLcb.Release(r1);
        string r4 = heapLcb.Acquire(); Console.WriteLine($"  Acquired → {r4}");

        Console.WriteLine("\n=== Connection counts (from dictionary) ===");
        heapLcb.PrintConnections();

        // ===================================================================
        // Q2b DEMO — Least Connections without PriorityQueue  O(n)
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q2b: Least Connections (Scan O(n))  ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var scanLcb = new LeastConnectionsBalancerWithoutPriorityQueue();
        scanLcb.AddServer("S1");
        scanLcb.AddServer("S2");
        scanLcb.AddServer("S3");

        Console.WriteLine("\n=== Acquire 3 connections (all start at 0) ===");
        string s1 = scanLcb.Acquire(); Console.WriteLine($"  Acquired → {s1}");
        string s2 = scanLcb.Acquire(); Console.WriteLine($"  Acquired → {s2}");
        string s3 = scanLcb.Acquire(); Console.WriteLine($"  Acquired → {s3}");

        Console.WriteLine($"\n=== Release {s1}, next acquire should go back to {s1} ===");
        scanLcb.Release(s1);
        string s4 = scanLcb.Acquire(); Console.WriteLine($"  Acquired → {s4}");

        Console.WriteLine("\n=== Connection counts (always exact) ===");
        scanLcb.PrintConnections();

        // ===================================================================
        // Q3 DEMO — Rate-Limited Load Balancer
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Rate-Limited Load Balancer      ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var rlb = new RateLimitedBalancer();
        var servers = new List<string> { "S1", "S2" };

        Console.WriteLine("\n=== 5 requests — all under limit ===");
        for (int i = 0; i < 5; i++)
            Console.WriteLine($"  Request {i + 1} → {rlb.GetServer(servers)}");

        Console.WriteLine("\n=== Saturate S1 with 1000 requests, then try one more ===");
        // Use a fresh instance and flood S1 to its limit
        var rlb2 = new RateLimitedBalancer();
        var single = new List<string> { "S1" };
        for (int i = 0; i < 1000; i++) rlb2.GetServer(single); // fills S1's window

        try
        {
            rlb2.GetServer(single); // should throw
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Caught: {ex.Message}");
        }
    }
}
