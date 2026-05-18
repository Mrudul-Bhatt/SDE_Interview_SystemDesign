// Q4. Implement a Service Registry with Health Checks
//
// Services register themselves on startup. Callers look up healthy instances
// by service name. A health monitor periodically probes each instance and
// marks failing ones as DOWN — eliminating the SPOF of manual service management.
//
// How Real Service Registries Work
// ──────────────────────────────────
// Tools: Consul, Eureka (Netflix), etcd, AWS Cloud Map, Kubernetes DNS
//
// Two discovery patterns:
//
// Client-side discovery (this implementation):
//   Client → Registry.Discover() → gets address → calls service directly
//   Used by: Netflix Eureka + Ribbon, Consul
//   Trade-off: client library is aware of the registry; simpler infra
//
// Server-side discovery:
//   Client → Load Balancer → LB queries registry → routes to instance
//   Used by: Kubernetes (kube-proxy + CoreDNS), AWS ALB with Cloud Map
//   Trade-off: client is oblivious; registry coupling stays in the LB tier
//
// Self-registration vs third-party registration:
//   Self:        service calls Register() on startup, sends heartbeats
//   Third-party: orchestrator (Kubernetes) registers/deregisters pods
//
// The SPOF problem for the registry itself:
//   → Registry must be highly available: run 3+ replicas with consensus (Raft)
//   → Consul and etcd use the Raft algorithm so every write is agreed on by
//     a majority of nodes — one replica dying does not lose data
//   → Kubernetes etcd is always deployed as a 3-node or 5-node cluster
//
// Complexity: Register O(1), Discover O(healthy), HealthCheck O(1)
//             Space O(services × instances)


// What Interviewers Test
// 1. Fixed Window — can you name the boundary burst bug?
//    → 2x limit possible at window boundaries
//    → Fix: Sliding Window Counter (covered in Networking section)
//    → Still used in practice because it's simple and good enough

// 2. Leaky Bucket vs Token Bucket — what is the key difference?
//    → Leaky: fixed output rate, excess rejected/queued
//    → Token: allows burst up to capacity, then throttles
//    → Leaky protects downstream; Token rewards clients for being bursty

// 3. JWT — why can't you invalidate a JWT before expiry?
//    → Tokens are stateless — server has no list of issued tokens
//    → Fix: short TTL (15min) + refresh tokens
//    → Or: token denylist in Redis (trade statelessness for revocability)

// 4. JWT — what is the difference between HS256 and RS256?
//    → HS256: symmetric — same secret signs and verifies (all servers need secret)
//    → RS256: asymmetric — private key signs, public key verifies
//    → RS256 is better for microservices: only auth server holds private key

// 5. Service Registry — how do you handle the registry itself as a SPOF?
//    → Registry must be highly available: run 3+ instances with consensus (Raft)
//    → Consul and etcd use Raft for distributed consensus
//    → Kubernetes etcd is a 3-node or 5-node cluster for exactly this reason

// 6. Service Registry — what if a service dies without deregistering?
//    → TTL-based deregistration: if no heartbeat within TTL → remove
//    → Health checks: registry actively probes services (this implementation)
//    → Both are used together in production


using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchitectureFundamentals
{
    // -------------------------------------------------------------------------
    // ServiceRegistry
    // -------------------------------------------------------------------------
    public class ServiceRegistry
    {
        // ServiceInstance is a record for one running process of a named service.
        // init-only setters on the identity fields (ServiceName, Host, Port) prevent
        // them from changing after construction — an instance's address is fixed for
        // its lifetime; only health state changes.
        private class ServiceInstance
        {
            public string ServiceName { get; init; } = "";
            public string Host { get; init; } = "";
            public int Port { get; init; }
            public bool IsHealthy { get; set; } = true;
            public DateTime LastSeen { get; set; } = DateTime.UtcNow;
            public int FailCount { get; set; } = 0;

            // Convenience so callers never format "host:port" themselves.
            public string Address => $"{Host}:{Port}";
        }

        // One list of instances per service name. In production (Consul, etcd) this
        // is a strongly consistent distributed key-value store replicated across nodes.
        // Dictionary here keeps the demo dependency-free while showing the algorithm.
        private readonly Dictionary<string, List<ServiceInstance>> _registry
            = new Dictionary<string, List<ServiceInstance>>();

        // A single lock covering all registry mutations. Every public method acquires
        // this lock so concurrent Register/Discover/HealthCheck calls can't corrupt
        // the list (e.g. Discover iterating while HealthCheck removes an element).
        private readonly object _lock = new object();

        private readonly int _failThreshold; // consecutive failures before marking DOWN
        private readonly TimeSpan _ttl;           // deregister if no heartbeat within this window

        // Round-robin counter persists across Discover() calls so each call picks
        // the next instance. A local variable would restart from index 0 every call
        // and always return the same first instance — no load distribution at all.
        private int _roundRobinIndex = 0;

        public ServiceRegistry(int failThreshold = 3, int ttlSeconds = 30)
        {
            _failThreshold = failThreshold;
            _ttl = TimeSpan.FromSeconds(ttlSeconds);
        }

        // Services call this on startup AND on each heartbeat tick.
        // Idempotent: re-registering an existing (host, port) just refreshes LastSeen
        // rather than adding a duplicate entry.
        public void Register(string serviceName, string host, int port)
        {
            lock (_lock)
            {
                if (!_registry.ContainsKey(serviceName))
                    _registry[serviceName] = new List<ServiceInstance>();

                // Heartbeat path: already registered — just refresh LastSeen.
                // Without this, a service that restarts would add a second entry
                // for the same address, doubling its weight in round-robin.
                var existing = _registry[serviceName]
                    .FirstOrDefault(s => s.Host == host && s.Port == port);

                if (existing != null)
                {
                    existing.LastSeen = DateTime.UtcNow;
                    existing.IsHealthy = true;  // a heartbeat proves liveness
                    existing.FailCount = 0;     // reset: transient failure is forgiven
                }
                else
                {
                    _registry[serviceName].Add(new ServiceInstance
                    {
                        ServiceName = serviceName,
                        Host = host,
                        Port = port
                    });
                    Console.WriteLine($"[REGISTRY] Registered:   {serviceName} @ {host}:{port}");
                }
            }
        }

        // Graceful shutdown: service calls Deregister before exiting.
        // Removes the instance immediately so no traffic is sent to it.
        // Ungraceful exits (crash, OOM kill) are handled by health check probes
        // detecting the TCP connection failure and eventually marking it DOWN.
        public void Deregister(string serviceName, string host, int port)
        {
            lock (_lock)
            {
                if (_registry.TryGetValue(serviceName, out var instances))
                {
                    int removed = instances.RemoveAll(s => s.Host == host && s.Port == port);
                    if (removed > 0)
                        Console.WriteLine($"[REGISTRY] Deregistered: {serviceName} @ {host}:{port}");
                }
            }
        }

        // Returns the address of a healthy instance using round-robin selection.
        // Returns null if no healthy instance exists — caller should return HTTP 503.
        public string Discover(string serviceName)
        {
            lock (_lock)
            {
                if (!_registry.TryGetValue(serviceName, out var instances)) return null;

                // Filter before indexing: a downed instance must never receive traffic
                // even momentarily. The client would get a connection error and the
                // error would propagate as a failed request rather than being avoided.
                var healthy = instances.Where(s => s.IsHealthy).ToList();
                if (healthy.Count == 0) return null;

                // Modulo distributes requests across all healthy instances in order.
                // If healthy.Count changes (an instance goes down), the index wraps
                // correctly — no ArrayIndexOutOfRange, no skipping instances.
                var instance = healthy[_roundRobinIndex % healthy.Count];
                _roundRobinIndex++;
                return instance.Address;
            }
        }

        // Called by a background health-monitor thread on a fixed interval.
        // In production: Consul sends an HTTP GET to the service's /health endpoint;
        // a non-2xx response or TCP timeout counts as a failure. We accept a boolean
        // here to keep the demo synchronous and dependency-free.
        public void HealthCheck(string serviceName, string host, int port, bool isReachable)
        {
            lock (_lock)
            {
                if (!_registry.TryGetValue(serviceName, out var instances)) return;
                var instance = instances.FirstOrDefault(s => s.Host == host && s.Port == port);
                if (instance == null) return;

                if (isReachable)
                {
                    instance.LastSeen = DateTime.UtcNow;
                    instance.FailCount = 0;

                    // Only log the state transition, not every successful probe —
                    // a service is healthy most of the time; logging every probe
                    // would flood the registry log.
                    if (!instance.IsHealthy)
                    {
                        instance.IsHealthy = true;
                        Console.WriteLine($"[HEALTH]   RECOVERED:    {serviceName} @ {instance.Address}");
                    }
                }
                else
                {
                    instance.FailCount++;
                    Console.WriteLine($"[HEALTH]   FAIL #{instance.FailCount}:        {serviceName} @ {instance.Address}");

                    // Require _failThreshold consecutive failures before marking DOWN.
                    // A single failed probe might be a transient network blip, not a
                    // crashed service. A false positive would incorrectly remove a
                    // healthy instance and reduce capacity unnecessarily.
                    if (instance.FailCount >= _failThreshold)
                    {
                        instance.IsHealthy = false;
                        Console.WriteLine($"[HEALTH]   MARKED DOWN:  {serviceName} @ {instance.Address}");
                    }
                }
            }
        }

        public void PrintRegistry()
        {
            lock (_lock)
            {
                Console.WriteLine("\n[REGISTRY] Current state:");
                foreach (var (name, instances) in _registry)
                {
                    foreach (var inst in instances)
                        Console.WriteLine($"  {name,-20} {inst.Address,-20} " +
                                          $"{(inst.IsHealthy ? "HEALTHY" : "DOWN   ")}  " +
                                          $"fails={inst.FailCount}");
                }
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
            // failThreshold=3: a node must fail 3 consecutive probes before being
            // removed from rotation. One transient TCP hiccup won't yank a node.
            var registry = new ServiceRegistry(failThreshold: 3, ttlSeconds: 30);

            // =================================================================
            // Scenario 1 — Services register on startup; discovery round-robins
            // Four instances register. Four Discover() calls cycle through
            // payment-service instances in order: 1 → 2 → 3 → 1 (wraps).
            // =================================================================
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Register services → round-robin discovery    ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            registry.Register("payment-service", "10.0.0.1", 8080);
            registry.Register("payment-service", "10.0.0.2", 8080);
            registry.Register("payment-service", "10.0.0.3", 8080);
            registry.Register("order-service", "10.0.1.1", 9090);

            Console.WriteLine("\n  Discover payment-service (round-robin across 3 instances):");
            for (int i = 0; i < 4; i++)
                Console.WriteLine($"    Call {i + 1}: → {registry.Discover("payment-service")}");

            // =================================================================
            // Scenario 2 — A node fails 3 consecutive health checks → marked DOWN
            // failThreshold=3 protects against false positives from transient
            // network blips. Each failure increments FailCount; at threshold
            // IsHealthy flips to false and Discover stops routing to it.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Node 10.0.0.2 fails health checks            ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            registry.HealthCheck("payment-service", "10.0.0.2", 8080, isReachable: false);
            registry.HealthCheck("payment-service", "10.0.0.2", 8080, isReachable: false);
            registry.HealthCheck("payment-service", "10.0.0.2", 8080, isReachable: false); // → DOWN

            registry.PrintRegistry();

            // =================================================================
            // Scenario 3 — Discovery skips the downed node automatically
            // Only .1 and .3 are healthy so round-robin alternates between them.
            // Client code never needs to know a node was removed — it just keeps
            // calling Discover() and always gets a healthy address back.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Discovery skips downed node                  ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine("\n  Discover payment-service (only .1 and .3 are healthy):");
            for (int i = 0; i < 4; i++)
                Console.WriteLine($"    Call {i + 1}: → {registry.Discover("payment-service")}");

            // =================================================================
            // Scenario 4 — Node recovers; health check restores it to rotation
            // A single successful probe clears FailCount and flips IsHealthy back.
            // The next Discover() call can return 10.0.0.2 again.
            // =================================================================
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Node 10.0.0.2 recovers and rejoins rotation  ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");

            Console.WriteLine();
            registry.HealthCheck("payment-service", "10.0.0.2", 8080, isReachable: true);

            Console.WriteLine("\n  Discover payment-service (all 3 healthy again):");
            for (int i = 0; i < 4; i++)
                Console.WriteLine($"    Call {i + 1}: → {registry.Discover("payment-service")}");
        }
    }

} // namespace ArchitectureFundamentals
