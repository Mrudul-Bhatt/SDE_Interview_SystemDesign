// Q7. Implement an API Gateway
//
// Simulate the middleware pipeline of an API gateway: correlation ID injection,
// API-key authentication, per-client rate limiting, GET response caching, and
// path-based routing to simulated backend services.
//
// Pipeline (left to right on every request):
//   CorrelationMiddleware → AuthMiddleware → RateLimitMiddleware
//       → CacheMiddleware → RouterMiddleware → [Backend Handler]
//
// Each middleware decides whether to short-circuit (return immediately)
// or call next() to pass the request further down the chain.
//
// Why middleware chain?
// ─────────────────────
// Each concern (auth, rate limit, cache, routing) is isolated, testable,
// and reorderable. Adding a new concern = add a new middleware class.
// The gateway itself has no business logic — it only wires the pipeline.
//
// Complexity: per-request O(middlewares) = O(1) for a fixed pipeline depth

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Infrastructure
{
    // =========================================================================
    // Request / Response / Context
    // =========================================================================

    public class GatewayRequest
    {
        public string                     Method;  // GET, POST, PUT, DELETE
        public string                     Path;    // /api/orders/99
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string                     Body;

        public string GetHeader(string key) =>
            Headers.TryGetValue(key, out string v) ? v : null;
    }

    public class GatewayResponse
    {
        public int                        StatusCode;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string                     Body;

        public void AddHeader(string key, string value) => Headers[key] = value;

        public static GatewayResponse Ok(string body) =>
            new GatewayResponse { StatusCode = 200, Body = body };

        public static GatewayResponse Created(string body) =>
            new GatewayResponse { StatusCode = 201, Body = body };

        public static GatewayResponse Unauthorized(string msg) =>
            new GatewayResponse { StatusCode = 401, Body = $"{{\"error\":\"{msg}\"}}" };

        public static GatewayResponse TooManyRequests(int retryAfterSecs) =>
            new GatewayResponse
            {
                StatusCode = 429,
                Body       = "{\"error\":\"rate limit exceeded\"}",
                Headers    = new Dictionary<string, string>
                {
                    ["Retry-After"]          = retryAfterSecs.ToString(),
                    ["X-RateLimit-Remaining"] = "0"
                }
            };

        public static GatewayResponse NotFound(string path) =>
            new GatewayResponse { StatusCode = 404, Body = $"{{\"error\":\"no route for {path}\"}}" };

        public override string ToString()
        {
            string headers = Headers.Count == 0 ? ""
                : "\n    Headers: " + string.Join(", ", Headers.Select(h => $"{h.Key}={h.Value}"));
            return $"HTTP {StatusCode}{headers}\n    Body: {Body}";
        }
    }

    // GatewayContext travels through the entire middleware chain.
    // Each middleware reads and writes to it instead of passing parameters.
    public class GatewayContext
    {
        public GatewayRequest  Request;
        public GatewayResponse Response;       // set by any middleware to short-circuit

        public string CorrelationId;           // injected by CorrelationMiddleware
        public string ClientId;                // set by AuthMiddleware (from API key lookup)
        public string UserId;                  // forwarded to backend as X-User-Id
        public string UserRole;                // forwarded to backend as X-User-Role
        public bool   ServedFromCache;         // set by CacheMiddleware

        public bool IsHandled => Response != null; // any middleware can terminate the chain
    }

    // Delegate representing the rest of the pipeline.
    public delegate void MiddlewareNext(GatewayContext ctx);

    public interface IMiddleware
    {
        string Name { get; }
        void   Invoke(GatewayContext ctx, MiddlewareNext next);
    }

    // =========================================================================
    // Middleware 1: Correlation ID
    // =========================================================================
    // First in the chain — assigns a unique ID to every request for distributed
    // tracing. Injected into both the forwarded request and the response.
    // If the client already provides X-Request-Id, honour it (useful for retries).
    public class CorrelationMiddleware : IMiddleware
    {
        public string Name => "Correlation";

        public void Invoke(GatewayContext ctx, MiddlewareNext next)
        {
            ctx.CorrelationId = ctx.Request.GetHeader("X-Request-Id")
                                ?? Guid.NewGuid().ToString()[..8];

            ctx.Request.Headers["X-Request-Id"] = ctx.CorrelationId;

            next(ctx); // always continue — correlation never rejects requests

            // Attach to response so the client can correlate logs.
            ctx.Response?.AddHeader("X-Request-Id", ctx.CorrelationId);
        }
    }

    // =========================================================================
    // Middleware 2: Authentication
    // =========================================================================
    // Validates the API key. On success: injects X-User-Id / X-User-Role into
    // the request so the backend sees trusted identity headers (not raw API keys).
    // On failure: short-circuits with 401 before any backend is called.
    public class AuthMiddleware : IMiddleware
    {
        public string Name => "Auth";

        // Simulates the API key → (userId, role) lookup in Redis or a DB.
        // In production this is a fast O(1) Redis GET, not a table scan.
        private static readonly Dictionary<string, (string UserId, string Role)> _keys
            = new Dictionary<string, (string, string)>
            {
                ["key-admin-001"] = ("user-1", "admin"),
                ["key-user-002"]  = ("user-2", "customer"),
                ["key-user-003"]  = ("user-3", "customer")
            };

        public void Invoke(GatewayContext ctx, MiddlewareNext next)
        {
            string apiKey = ctx.Request.GetHeader("X-Api-Key");

            if (string.IsNullOrEmpty(apiKey) || !_keys.TryGetValue(apiKey, out var identity))
            {
                // Short-circuit: reject before touching any backend.
                ctx.Response = GatewayResponse.Unauthorized("invalid or missing API key");
                Console.WriteLine($"    [Auth] REJECTED — invalid key '{apiKey}'");
                return;
            }

            ctx.ClientId  = apiKey;
            ctx.UserId    = identity.UserId;
            ctx.UserRole  = identity.Role;

            // Replace raw API key with decoded identity — backends never see the key.
            // Only the gateway can set these headers (internal network trust boundary).
            ctx.Request.Headers.Remove("X-Api-Key");
            ctx.Request.Headers["X-User-Id"]   = ctx.UserId;
            ctx.Request.Headers["X-User-Role"]  = ctx.UserRole;

            Console.WriteLine($"    [Auth] OK — clientId={ctx.ClientId}, userId={ctx.UserId}, role={ctx.UserRole}");
            next(ctx);
        }
    }

    // =========================================================================
    // Middleware 3: Rate Limiter (fixed window per client)
    // =========================================================================
    // Counts requests per ClientId within a rolling window using in-memory
    // counters (simulates Redis INCR + EXPIRE). Returns HTTP 429 when exceeded.
    public class RateLimitMiddleware : IMiddleware
    {
        public string Name => "RateLimit";

        private readonly int      _limit;      // max requests per window
        private readonly TimeSpan _window;     // window duration
        private readonly object   _lock = new object();

        private readonly Dictionary<string, (int Count, DateTime WindowStart)> _counters
            = new Dictionary<string, (int, DateTime)>();

        public RateLimitMiddleware(int limit, int windowSeconds)
        {
            _limit  = limit;
            _window = TimeSpan.FromSeconds(windowSeconds);
        }

        public void Invoke(GatewayContext ctx, MiddlewareNext next)
        {
            string key = ctx.ClientId; // rate limit by authenticated client

            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;

                if (_counters.TryGetValue(key, out var entry))
                {
                    // Reset counter if the window has elapsed.
                    if (now - entry.WindowStart >= _window)
                        entry = (0, now);
                }
                else
                {
                    entry = (0, now);
                }

                if (entry.Count >= _limit)
                {
                    int retryAfter = (int)(_window - (now - entry.WindowStart)).TotalSeconds + 1;
                    Console.WriteLine($"    [RateLimit] EXCEEDED for {key} " +
                                      $"({entry.Count}/{_limit}) — 429, retry in {retryAfter}s");
                    ctx.Response = GatewayResponse.TooManyRequests(retryAfter);
                    _counters[key] = entry;
                    return;
                }

                entry = (entry.Count + 1, entry.WindowStart);
                _counters[key] = entry;
                Console.WriteLine($"    [RateLimit] OK for {key} ({entry.Count}/{_limit})");
            }

            next(ctx);
        }
    }

    // =========================================================================
    // Middleware 4: Response Cache
    // =========================================================================
    // Caches GET responses keyed on method+path. On cache hit, returns the
    // stored response without touching the backend. On miss, forwards the request
    // and stores the response for future callers.
    // Only caches 200 responses — errors are never cached.
    public class CacheMiddleware : IMiddleware
    {
        public string Name => "Cache";

        private readonly int    _ttlSeconds;
        private readonly object _lock = new object();

        private readonly Dictionary<string, (string Body, DateTime ExpiresAt)> _cache
            = new Dictionary<string, (string, DateTime)>();

        public CacheMiddleware(int ttlSeconds) => _ttlSeconds = ttlSeconds;

        private static string CacheKey(GatewayContext ctx) =>
            $"{ctx.Request.Method}:{ctx.Request.Path}";

        public void Invoke(GatewayContext ctx, MiddlewareNext next)
        {
            // Only cache safe methods — POST/PUT/DELETE must always reach the backend.
            if (ctx.Request.Method != "GET")
            {
                next(ctx);
                return;
            }

            string key = CacheKey(ctx);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
                {
                    Console.WriteLine($"    [Cache] HIT for {key}");
                    ctx.Response           = GatewayResponse.Ok(entry.Body);
                    ctx.ServedFromCache    = true;
                    ctx.Response.AddHeader("X-Cache", "HIT");
                    ctx.Response.AddHeader("X-Cache-Key", key);
                    return; // short-circuit — backend not called
                }
            }

            Console.WriteLine($"    [Cache] MISS for {key} — forwarding to backend");
            next(ctx); // call backend

            // Store successful response in cache.
            if (ctx.Response?.StatusCode == 200)
            {
                lock (_lock)
                {
                    _cache[key] = (ctx.Response.Body, DateTime.UtcNow.AddSeconds(_ttlSeconds));
                }
                ctx.Response.AddHeader("X-Cache", "MISS");
                Console.WriteLine($"    [Cache] Stored response for {key} (TTL={_ttlSeconds}s)");
            }
        }
    }

    // =========================================================================
    // Middleware 5: Router
    // =========================================================================
    // Maps (method, path-prefix) → backend handler. The handler simulates what
    // the backend service would return. In production this would be an HTTP call
    // to the upstream service's address resolved from service discovery.
    public class RouterMiddleware : IMiddleware
    {
        public string Name => "Router";

        // Route table: (method, path-prefix) → handler
        private readonly List<(string Method, string Prefix, Action<GatewayContext> Handler)> _routes
            = new List<(string, string, Action<GatewayContext>)>();

        public RouterMiddleware Register(string method, string prefix, Action<GatewayContext> handler)
        {
            _routes.Add((method.ToUpper(), prefix, handler));
            return this;
        }

        public void Invoke(GatewayContext ctx, MiddlewareNext next)
        {
            string method = ctx.Request.Method.ToUpper();
            string path   = ctx.Request.Path;

            var route = _routes.FirstOrDefault(r =>
                r.Method == method && path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));

            if (route == default)
            {
                Console.WriteLine($"    [Router] No route for {method} {path} → 404");
                ctx.Response = GatewayResponse.NotFound(path);
                return;
            }

            Console.WriteLine($"    [Router] {method} {path} → {route.Prefix} handler");
            route.Handler(ctx);
        }
    }

    // =========================================================================
    // Gateway Pipeline
    // =========================================================================
    // Chains middlewares in registration order. Each middleware receives a
    // `next` delegate pointing to the rest of the chain. If middleware sets
    // ctx.Response without calling next, the chain short-circuits.
    public class GatewayPipeline
    {
        private readonly List<IMiddleware> _middlewares = new List<IMiddleware>();

        public GatewayPipeline Use(IMiddleware mw)
        {
            _middlewares.Add(mw);
            return this;
        }

        public GatewayResponse Process(GatewayRequest request)
        {
            var ctx = new GatewayContext { Request = request };

            // Build the chain right-to-left so each middleware wraps the next.
            MiddlewareNext pipeline = _ => { }; // terminal no-op at end of chain
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                IMiddleware    mw       = _middlewares[i];
                MiddlewareNext nextStep = pipeline;
                pipeline = c => mw.Invoke(c, nextStep);
            }

            pipeline(ctx);
            return ctx.Response ?? GatewayResponse.NotFound(request.Path);
        }
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public class Program
    {
        // Simulated backend handlers — each represents a microservice.
        private static void OrdersReadHandler(GatewayContext ctx)
        {
            string userId = ctx.Request.GetHeader("X-User-Id");
            string path   = ctx.Request.Path;
            ctx.Response  = GatewayResponse.Ok(
                $"{{\"service\":\"order-service\",\"path\":\"{path}\"," +
                $"\"userId\":\"{userId}\",\"orders\":[\"order-101\",\"order-102\"]}}");
        }

        private static void OrdersWriteHandler(GatewayContext ctx)
        {
            ctx.Response = GatewayResponse.Created(
                $"{{\"service\":\"order-service\",\"orderId\":\"order-{DateTime.UtcNow.Ticks % 9999:D4}\"," +
                $"\"status\":\"created\"}}");
        }

        private static void UsersHandler(GatewayContext ctx)
        {
            string userId = ctx.Request.GetHeader("X-User-Id");
            ctx.Response  = GatewayResponse.Ok(
                $"{{\"service\":\"user-service\",\"userId\":\"{userId}\"," +
                $"\"name\":\"Demo User\",\"role\":\"{ctx.UserRole}\"}}");
        }

        private static void PaymentsHandler(GatewayContext ctx)
        {
            ctx.Response = GatewayResponse.Ok(
                $"{{\"service\":\"payment-service\",\"balance\":\"$250.00\"}}");
        }

        private static GatewayPipeline BuildGateway(int rateLimit = 3, int rateLimitWindowSecs = 10, int cacheTtl = 30)
        {
            var router = new RouterMiddleware()
                .Register("GET",  "/api/orders",   OrdersReadHandler)
                .Register("POST", "/api/orders",   OrdersWriteHandler)
                .Register("GET",  "/api/users",    UsersHandler)
                .Register("GET",  "/api/payments", PaymentsHandler);

            return new GatewayPipeline()
                .Use(new CorrelationMiddleware())
                .Use(new AuthMiddleware())
                .Use(new RateLimitMiddleware(rateLimit, rateLimitWindowSecs))
                .Use(new CacheMiddleware(cacheTtl))
                .Use(router);
        }

        private static void PrintResponse(GatewayResponse r, string note = "")
        {
            string extra = string.IsNullOrEmpty(note) ? "" : $"  ← {note}";
            Console.WriteLine($"\n  Response:{extra}");
            Console.WriteLine($"    {r}");
        }

        public static void Main()
        {
            // =================================================================
            // Scenario 1 — Happy path: auth → rate limit → cache miss → backend
            // Shows the full pipeline for an authenticated GET request.
            // Correlation ID injected, API key stripped, X-User-Id forwarded.
            // Backend returns data; response carries X-Request-Id + X-Cache: MISS.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Happy path — full pipeline, cache miss           ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var gateway = BuildGateway(rateLimit: 3, rateLimitWindowSecs: 10, cacheTtl: 30);

            GatewayResponse r1 = gateway.Process(new GatewayRequest
            {
                Method  = "GET",
                Path    = "/api/orders",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-user-002" }
            });
            PrintResponse(r1, "200 with X-Cache: MISS");
            Console.WriteLine($"\n  X-Request-Id in response: {r1.Headers.GetValueOrDefault("X-Request-Id", "—")}");
            Console.WriteLine($"  X-Cache in response:      {r1.Headers.GetValueOrDefault("X-Cache", "—")}");

            // =================================================================
            // Scenario 2 — Auth rejection: invalid API key → 401, no backend hit
            // The Auth middleware short-circuits after the correlation ID is set.
            // Rate limiter and router never run. No backend called.
            // Shows: gateway enforces auth at the edge — backend services are shielded.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Auth rejection — invalid key → 401               ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            GatewayResponse r2 = gateway.Process(new GatewayRequest
            {
                Method  = "GET",
                Path    = "/api/orders",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-invalid-999" }
            });
            PrintResponse(r2, "401 — no backend hit");

            // =================================================================
            // Scenario 3 — Rate limiting: 5 requests, limit = 3
            // First 3 succeed. 4th and 5th get HTTP 429 with Retry-After header.
            // Counter is per-client (key-user-003) — other clients unaffected.
            // Shows: distributed rate limiting at the edge.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Rate limiting — 3/5 succeed, 2 get 429           ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var rateLimitedGateway = BuildGateway(rateLimit: 3, rateLimitWindowSecs: 60, cacheTtl: 5);

            for (int i = 1; i <= 5; i++)
            {
                GatewayResponse r = rateLimitedGateway.Process(new GatewayRequest
                {
                    Method  = "GET",
                    Path    = "/api/users",
                    Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-user-003" }
                });
                string note = r.StatusCode == 429
                    ? $"429 Too Many Requests (Retry-After={r.Headers.GetValueOrDefault("Retry-After", "?")}s)"
                    : $"{r.StatusCode} OK";
                Console.WriteLine($"\n  Request {i}: {note}");
            }

            // =================================================================
            // Scenario 4 — Cache hit: second identical GET skips the backend
            // First request: cache miss → backend called → response stored.
            // Second request: cache hit → response served instantly, no backend.
            // Shows: GET responses cached by method+path key; POST always passes through.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Cache hit — second GET served from cache          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var cachingGateway = BuildGateway(rateLimit: 20, rateLimitWindowSecs: 60, cacheTtl: 30);

            Console.WriteLine("\n  First GET /api/payments (cache miss):");
            GatewayResponse c1 = cachingGateway.Process(new GatewayRequest
            {
                Method  = "GET",
                Path    = "/api/payments",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-admin-001" }
            });
            Console.WriteLine($"  X-Cache: {c1.Headers.GetValueOrDefault("X-Cache", "—")}");

            Console.WriteLine("\n  Second GET /api/payments (cache hit — no backend call):");
            GatewayResponse c2 = cachingGateway.Process(new GatewayRequest
            {
                Method  = "GET",
                Path    = "/api/payments",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-admin-001" }
            });
            Console.WriteLine($"  X-Cache: {c2.Headers.GetValueOrDefault("X-Cache", "—")}  ← HIT");
            Console.WriteLine($"  Bodies match: {c1.Body == c2.Body}  ← same response, no backend hit");

            Console.WriteLine("\n  POST /api/orders (cache bypassed — writes always reach backend):");
            GatewayResponse c3 = cachingGateway.Process(new GatewayRequest
            {
                Method  = "POST",
                Path    = "/api/orders",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-user-002" },
                Body    = "{\"item\":\"keyboard\",\"qty\":1}"
            });
            Console.WriteLine($"  Status: {c3.StatusCode}  X-Cache present: {c3.Headers.ContainsKey("X-Cache")}  ← POST never cached");

            // =================================================================
            // Scenario 5 — Path-based routing: three paths → three backends
            // Verifies that the router correctly dispatches each path prefix to
            // its registered backend handler. Also shows 404 for unknown paths.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Path routing — three paths, three backends        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var routingGateway = BuildGateway(rateLimit: 20, rateLimitWindowSecs: 60);

            string[] paths = { "/api/orders", "/api/users", "/api/payments", "/api/unknown" };
            foreach (string path in paths)
            {
                GatewayResponse r = routingGateway.Process(new GatewayRequest
                {
                    Method  = "GET",
                    Path    = path,
                    Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-admin-001" }
                });
                Console.WriteLine($"\n  GET {path} → HTTP {r.StatusCode}");
                Console.WriteLine($"    {r.Body}");
            }
        }
    }

} // namespace Infrastructure
