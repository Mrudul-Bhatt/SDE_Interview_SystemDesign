// Q8. Implement a Reverse Proxy
//
// Simulate the core mechanics of a reverse proxy: three load-balancing
// algorithms (round-robin, least-connections, IP hash), active + passive
// health checks, SSL termination, X-Forwarded-For header chain, connection
// pooling, and graceful drain before backend removal.
//
// Architecture
// ─────────────
//   Client Request
//       │
//   ReverseProxy
//       ├── SslTermination  (HTTPS → HTTP, inject X-Forwarded-Proto)
//       ├── HeaderEnricher  (X-Forwarded-For chain, X-Real-IP, strip internal)
//       ├── LoadBalancer    (pick a healthy backend by algorithm)
//       │       ├── RoundRobinBalancer
//       │       ├── LeastConnectionsBalancer
//       │       └── IpHashBalancer
//       ├── HealthMonitor   (active polling + passive failure counting)
//       └── BackendPool     (list of BackendServer with state)
//
// Complexity: Forward O(1) per request; HealthCheck O(backends)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Infrastructure
{
    // =========================================================================
    // Backend server
    // =========================================================================

    public enum BackendState { Healthy, Draining, Down }

    public class BackendServer
    {
        public string Id;           // e.g. "backend-A"
        public string Address;      // e.g. "http://10.0.0.1:8080"
        public BackendState State = BackendState.Healthy;
        public int ActiveConns = 0;   // live connections (for least-conn)
        public int PassiveFails = 0;   // consecutive 5xx on real traffic
        public int TotalHandled = 0;   // counter for demo output

        // Simulate whether this backend will succeed or fail for testing.
        public bool SimulateFailure = false;

        public override string ToString() => $"{Id}({State}, conns={ActiveConns})";
    }

    // =========================================================================
    // Request / Response
    // =========================================================================

    public class ProxyRequest
    {
        public string Method = "GET";
        public string Url;     // full URL including scheme
        public string Path;
        public string ClientIp;
        public Dictionary<string, string> Headers = [];
        public string Body;

        public string Scheme => Url?.StartsWith("https", StringComparison.OrdinalIgnoreCase) == true
                                ? "https" : "http";
    }

    public class ProxyResponse
    {
        public int StatusCode;
        public Dictionary<string, string> Headers = [];
        public string Body;
        public string HandledBy; // which backend served it

        public override string ToString() =>
            $"HTTP {StatusCode} from {HandledBy ?? "?"} | {Body}";
    }

    // =========================================================================
    // Load-balancing algorithms
    // =========================================================================

    public interface ILoadBalancer
    {
        string Name { get; }
        BackendServer Pick(IReadOnlyList<BackendServer> pool, string clientIp = null);
    }

    // Round-Robin: distribute evenly regardless of load.
    // Simplest and most common for uniform-cost requests.
    public class RoundRobinBalancer : ILoadBalancer
    {
        public string Name => "Round-Robin";
        private int _index = -1;

        public BackendServer Pick(IReadOnlyList<BackendServer> pool, string clientIp = null)
        {
            if (pool.Count == 0) return null;
            // Interlocked for thread-safe increment — multiple requests in parallel
            int idx = Interlocked.Increment(ref _index);
            return pool[idx % pool.Count];
        }
    }

    // Least Connections: send to backend with fewest active connections.
    // Best when requests have variable processing times — avoids overloading
    // a slow backend that has not yet finished previous requests.
    public class LeastConnectionsBalancer : ILoadBalancer
    {
        public string Name => "Least-Connections";

        public BackendServer Pick(IReadOnlyList<BackendServer> pool, string clientIp = null)
        {
            if (pool.Count == 0) return null;
            return pool.MinBy(b => b.ActiveConns);
        }
    }

    // IP Hash: hash(clientIp) % pool.Count → same client always hits same backend.
    // Required for stateful backends where session data is not shared.
    public class IpHashBalancer : ILoadBalancer
    {
        public string Name => "IP-Hash";

        public BackendServer Pick(IReadOnlyList<BackendServer> pool, string clientIp = null)
        {
            if (pool.Count == 0) return null;
            int hash = Math.Abs((clientIp ?? "0.0.0.0").GetHashCode());
            return pool[hash % pool.Count];
        }
    }

    // =========================================================================
    // Health Monitor
    // =========================================================================
    // Active: polls each backend periodically with a lightweight probe request.
    // Passive: tracks consecutive failures on real traffic (circuit-breaker style).
    //
    // Separation of concerns: the health monitor only changes BackendState.
    // The load balancer only picks from Healthy backends.
    public class HealthMonitor
    {
        private readonly List<BackendServer> _backends;
        private readonly int _passiveFailThreshold; // consecutive real-traffic fails → DOWN
        private readonly int _activeCheckIntervalMs;

        private Timer _timer;

        public HealthMonitor(
            List<BackendServer> backends,
            int passiveFailThreshold = 3,
            int activeCheckIntervalMs = 500)
        {
            _backends = backends;
            _passiveFailThreshold = passiveFailThreshold;
            _activeCheckIntervalMs = activeCheckIntervalMs;
        }

        // Start background polling thread.
        public void StartActive() =>
            _timer = new Timer(_ => RunActiveChecks(), null, 0, _activeCheckIntervalMs);

        public void Stop() => _timer?.Dispose();

        // Simulate an active health probe to each backend.
        // In production: HTTP GET /health; timeout 2s; 2xx = healthy.
        private void RunActiveChecks()
        {
            foreach (BackendServer b in _backends)
            {
                if (b.State == BackendState.Draining) continue;

                bool healthy = !b.SimulateFailure; // simulation hook

                if (healthy && b.State == BackendState.Down)
                {
                    b.State = BackendState.Healthy;
                    b.PassiveFails = 0;
                    Console.WriteLine($"    [HealthMonitor] {b.Id} RECOVERED → Healthy");
                }
                else if (!healthy && b.State == BackendState.Healthy)
                {
                    b.State = BackendState.Down;
                    Console.WriteLine($"    [HealthMonitor] {b.Id} FAILED active check → Down");
                }
            }
        }

        // Called by the proxy after each real request.
        // Passive detection: accumulate failures on live traffic.
        public void RecordResult(BackendServer backend, bool success)
        {
            if (success)
            {
                backend.PassiveFails = 0; // reset on success — only sustained failure trips it
                return;
            }

            backend.PassiveFails++;
            if (backend.PassiveFails >= _passiveFailThreshold && backend.State == BackendState.Healthy)
            {
                backend.State = BackendState.Down;
                Console.WriteLine($"    [HealthMonitor] {backend.Id} → Down " +
                                  $"(passive: {backend.PassiveFails} consecutive failures)");
            }
        }

        // Graceful drain: stop new requests, wait for in-flight to finish.
        public void Drain(BackendServer backend, int drainTimeoutMs = 100)
        {
            backend.State = BackendState.Draining;
            Console.WriteLine($"    [HealthMonitor] {backend.Id} → Draining " +
                              $"(waiting up to {drainTimeoutMs}ms for in-flight requests)");

            // In production: poll until ActiveConns == 0 or timeout expires.
            int waited = 0;
            while (backend.ActiveConns > 0 && waited < drainTimeoutMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }

            backend.State = BackendState.Down;
            Console.WriteLine($"    [HealthMonitor] {backend.Id} → Down (drain complete)");
        }
    }

    // =========================================================================
    // Reverse Proxy
    // =========================================================================
    public class ReverseProxy
    {
        private readonly List<BackendServer> _backends;
        private readonly ILoadBalancer _balancer;
        private readonly HealthMonitor _health;

        public ReverseProxy(List<BackendServer> backends, ILoadBalancer balancer)
        {
            _backends = backends;
            _balancer = balancer;
            _health = new HealthMonitor(backends, passiveFailThreshold: 3);
        }

        public HealthMonitor HealthMonitor => _health;

        // Forward a request through the full proxy pipeline.
        public ProxyResponse Forward(ProxyRequest request)
        {
            // ── Step 1: SSL termination ───────────────────────────────────────
            // Record the original scheme before rewriting to HTTP internally.
            // X-Forwarded-Proto tells the backend what the client actually used.
            string originalScheme = request.Scheme;
            request.Headers["X-Forwarded-Proto"] = originalScheme;

            // Strip internal identity headers from client — prevent spoofing.
            // A client that sends X-Real-IP: 127.0.0.1 must not be trusted.
            request.Headers.Remove("X-Real-IP");
            request.Headers.Remove("X-Forwarded-For"); // we'll rebuild the chain below

            // ── Step 2: Header enrichment ─────────────────────────────────────
            // Build X-Forwarded-For chain: append proxy's address to any existing value.
            // The backend reads the leftmost IP as the real client.
            request.Headers["X-Forwarded-For"] = request.ClientIp ?? "unknown";
            request.Headers["X-Real-IP"] = request.ClientIp ?? "unknown";

            // ── Step 3: Pick a healthy backend ────────────────────────────────
            List<BackendServer> healthy = _backends
                .Where(b => b.State == BackendState.Healthy)
                .ToList();

            BackendServer backend = _balancer.Pick(healthy, request.ClientIp);

            if (backend == null)
            {
                Console.WriteLine("    [Proxy] No healthy backends available → 503");
                return new ProxyResponse
                {
                    StatusCode = 503,
                    Body = "{\"error\":\"no healthy backends\"}",
                    HandledBy = "proxy"
                };
            }

            Console.WriteLine($"    [Proxy:{_balancer.Name}] {request.Method} {request.Path} " +
                              $"→ {backend.Id} (conns={backend.ActiveConns})");

            // ── Step 4: Forward to backend ────────────────────────────────────
            Interlocked.Increment(ref backend.ActiveConns);
            try
            {
                ProxyResponse response = CallBackend(backend, request);
                backend.TotalHandled++;

                bool success = response.StatusCode < 500;
                _health.RecordResult(backend, success);

                // Attach diagnostics headers to the response.
                response.HandledBy = backend.Id;
                response.Headers["X-Served-By"] = backend.Id;
                response.Headers["X-Forwarded-Proto"] = originalScheme;

                return response;
            }
            finally
            {
                // Always decrement — even on exception — so the counter stays accurate.
                Interlocked.Decrement(ref backend.ActiveConns);
            }
        }

        // Simulates an HTTP call to the chosen backend.
        private static ProxyResponse CallBackend(BackendServer backend, ProxyRequest req)
        {
            if (backend.SimulateFailure)
            {
                return new ProxyResponse
                {
                    StatusCode = 500,
                    Body = $"{{\"error\":\"{backend.Id} internal error\"}}"
                };
            }

            // Successful backend response — includes the headers the proxy forwarded.
            string xForwardedFor = req.Headers.GetValueOrDefault("X-Forwarded-For", "—");
            string xForwardedProto = req.Headers.GetValueOrDefault("X-Forwarded-Proto", "—");
            return new ProxyResponse
            {
                StatusCode = 200,
                Body = $"{{\"backend\":\"{backend.Id}\",\"path\":\"{req.Path}\"," +
                             $"\"X-Forwarded-For\":\"{xForwardedFor}\"," +
                             $"\"X-Forwarded-Proto\":\"{xForwardedProto}\"}}"
            };
        }
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public class Program
    {
        private static List<BackendServer> FreshPool() =>
        [
            new BackendServer { Id = "backend-A", Address = "http://10.0.0.1:8080" },
            new BackendServer { Id = "backend-B", Address = "http://10.0.0.2:8080" },
            new BackendServer { Id = "backend-C", Address = "http://10.0.0.3:8080" }
        ];

        public static void Main()
        {
            // =================================================================
            // Scenario 1 — Round-Robin: 6 requests spread evenly across 3 backends
            // Each backend gets 2 requests in order A→B→C→A→B→C.
            // Shows the default distribution algorithm for uniform-cost requests.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Round-Robin — 6 requests across 3 backends       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var rrPool = FreshPool();
            var rrProxy = new ReverseProxy(rrPool, new RoundRobinBalancer());

            for (int i = 1; i <= 6; i++)
            {
                ProxyResponse r = rrProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Url = "https://api.example.com/products",
                    Path = "/products",
                    ClientIp = $"203.0.113.{i}"
                });
                Console.WriteLine($"  Request {i}: {r}");
            }

            Console.WriteLine("\n  Requests per backend:");
            foreach (var b in rrPool)
                Console.WriteLine($"    {b.Id}: {b.TotalHandled} requests");

            // =================================================================
            // Scenario 2 — Least-Connections: long-running requests simulated
            // backend-A is given 3 artificial active connections (slow request).
            // Least-connections routes new requests away from it to B and C.
            // Shows: least-conn avoids piling on an already-busy backend.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Least-Connections — avoid overloaded backend      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var lcPool = FreshPool();
            var lcProxy = new ReverseProxy(lcPool, new LeastConnectionsBalancer());

            // Simulate 3 long-running requests already in-flight on backend-A.
            lcPool[0].ActiveConns = 3;
            Console.WriteLine("  backend-A has 3 active connections (slow in-flight requests).");
            Console.WriteLine("  backend-B and backend-C have 0.\n");

            for (int i = 1; i <= 4; i++)
            {
                ProxyResponse r = lcProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/checkout",
                    Url = "https://api.example.com/checkout",
                    ClientIp = $"198.51.100.{i}"
                });
                Console.WriteLine($"  Request {i}: {r}");
            }

            // =================================================================
            // Scenario 3 — IP Hash: same client always hits same backend
            // Client 1.2.3.4 always → one backend; client 5.6.7.8 → another.
            // Sending 6 requests from two IPs shows consistent affinity.
            // Shows: sticky sessions without shared session storage.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: IP-Hash — same client always hits same backend    ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var ihPool = FreshPool();
            var ihProxy = new ReverseProxy(ihPool, new IpHashBalancer());

            string[] clients = { "1.2.3.4", "5.6.7.8", "1.2.3.4", "5.6.7.8", "1.2.3.4", "9.10.11.12" };
            foreach (string ip in clients)
            {
                ProxyResponse r = ihProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/dashboard",
                    Url = "https://api.example.com/dashboard",
                    ClientIp = ip
                });
                Console.WriteLine($"  Client {ip,-15} → {r.HandledBy}");
            }

            // =================================================================
            // Scenario 4 — Passive health check: backend-B fails 3 times → Down
            // SimulateFailure on backend-B causes it to return 500 on real traffic.
            // After passiveFailThreshold=3 consecutive failures, proxy marks it Down.
            // Subsequent requests route only to A and C.
            // Shows: passive detection removes a silently failing backend.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Passive health check — backend-B fails → Down     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var phPool = FreshPool();
            var phProxy = new ReverseProxy(phPool, new RoundRobinBalancer());

            phPool[1].SimulateFailure = true; // backend-B will always return 500

            Console.WriteLine("  Sending 8 requests (backend-B is broken):");
            for (int i = 1; i <= 8; i++)
            {
                ProxyResponse r = phProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/api/data",
                    Url = "http://api.example.com/api/data",
                    ClientIp = "10.10.10.1"
                });
                Console.WriteLine($"  Request {i}: HTTP {r.StatusCode} from {r.HandledBy}");
            }

            Console.WriteLine("\n  Backend states after passive check:");
            foreach (var b in phPool)
                Console.WriteLine($"    {b.Id}: {b.State} (passive fails: {b.PassiveFails})");

            // =================================================================
            // Scenario 5 — Active health check + recovery
            // backend-C starts as SimulateFailure=true (active check → Down).
            // After a pause, failure is cleared → active check marks it Healthy.
            // Shows: active polling detects failures without real traffic.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Active health check — backend-C down then recovers║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var ahPool = FreshPool();
            var ahProxy = new ReverseProxy(ahPool, new RoundRobinBalancer());

            ahPool[2].SimulateFailure = true; // backend-C broken at start
            ahProxy.HealthMonitor.StartActive();

            Thread.Sleep(600); // let active checker run and mark backend-C Down

            Console.WriteLine("  Backend states after first active check:");
            foreach (var b in ahPool)
                Console.WriteLine($"    {b.Id}: {b.State}");

            Console.WriteLine("\n  Sending 4 requests (backend-C excluded from pool):");
            for (int i = 1; i <= 4; i++)
            {
                ProxyResponse r = ahProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/status",
                    Url = "http://api.example.com/status",
                    ClientIp = "172.16.0.1"
                });
                Console.WriteLine($"  Request {i}: {r}");
            }

            // Simulate backend-C recovering
            Console.WriteLine("\n  backend-C recovered — clearing SimulateFailure...");
            ahPool[2].SimulateFailure = false;
            Thread.Sleep(600); // wait for active checker to rediscover

            Console.WriteLine("  Backend states after recovery:");
            foreach (var b in ahPool)
                Console.WriteLine($"    {b.Id}: {b.State}");

            ahProxy.HealthMonitor.Stop();

            // =================================================================
            // Scenario 6 — Graceful drain: remove backend-A for zero-downtime deploy
            // Drain marks backend-A as Draining (stops new requests immediately).
            // Active connections drain (here: none, so instant).
            // Then backend-A is Down — traffic continues on B and C seamlessly.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 6: Graceful drain — zero-downtime backend removal    ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var gdPool = FreshPool();
            var gdProxy = new ReverseProxy(gdPool, new RoundRobinBalancer());

            Console.WriteLine("  2 requests before drain (all 3 backends healthy):");
            for (int i = 1; i <= 2; i++)
            {
                ProxyResponse r = gdProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/home",
                    Url = "http://api.example.com/home",
                    ClientIp = "10.0.1.1"
                });
                Console.WriteLine($"  Request {i}: {r}");
            }

            Console.WriteLine("\n  Draining backend-A for zero-downtime deploy...");
            gdProxy.HealthMonitor.Drain(gdPool[0], drainTimeoutMs: 100);

            Console.WriteLine("\n  4 requests after drain (backend-A excluded):");
            for (int i = 3; i <= 6; i++)
            {
                ProxyResponse r = gdProxy.Forward(new ProxyRequest
                {
                    Method = "GET",
                    Path = "/home",
                    Url = "http://api.example.com/home",
                    ClientIp = "10.0.1.1"
                });
                Console.WriteLine($"  Request {i}: {r}  ← no backend-A");
            }
        }
    }

} // namespace Infrastructure
