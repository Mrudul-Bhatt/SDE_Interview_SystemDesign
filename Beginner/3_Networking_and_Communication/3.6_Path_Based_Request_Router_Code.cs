// Q4. Implement a Path-Based Request Router
// A reverse proxy routes incoming HTTP requests to different backend services based on
// URL path prefix.  Supports longest-prefix matching, per-route auth middleware, and
// a logging middleware that fires for every request regardless of outcome.

using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// RequestRouter — simulates an API Gateway / nginx location-block routing table
// ---------------------------------------------------------------------------
public class RequestRouter
{
    // Immutable request value: record gives structural equality and concise init syntax.
    // Headers dictionary holds key-value pairs (Authorization, Content-Type, etc.).
    public record HttpRequest(string Method, string Path, Dictionary<string, string> Headers);

    // Immutable response value: StatusCode follows HTTP conventions (200 / 401 / 404).
    public record HttpResponse(int StatusCode, string Body);

    // One registered routing rule: which URL prefix maps to which backend, plus auth flag.
    private class Route
    {
        public string Prefix { get; init; } = ""; // e.g. "/api/users"
        public string Backend { get; init; } = ""; // e.g. "user-service:8080"
        public bool RequireAuth { get; init; }       // true -> verify Authorization header
    }

    // Sorted list of routes — always kept in descending prefix-length order so that
    // the first match in Handle() is automatically the most specific one.
    private readonly List<Route> _routes = new();

    // Accepted bearer tokens for auth middleware; HashSet gives O(1) lookup.
    // In production this would call a JWT validator or OAuth introspection endpoint.
    private readonly HashSet<string> _validTokens = new();

    // Registers a backend for a URL prefix and re-sorts the routing table.
    // Caller order doesn't matter; the sort ensures longest-prefix always wins.
    public void AddRoute(string prefix, string backend, bool requireAuth = false)
    {
        _routes.Add(new Route { Prefix = prefix, Backend = backend, RequireAuth = requireAuth });

        // Sort descending by prefix length: "/api/users" (10) beats "/api/" (5) beats "/" (1).
        // A trie would give O(k) matching instead of O(n), but is overkill for small tables.
        _routes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
    }

    // Whitelists a token string (e.g. "Bearer abc123").
    public void AddValidToken(string token) => _validTokens.Add(token);

    // Processes one request through: logging -> route match -> auth -> proxy forward.
    public HttpResponse Handle(HttpRequest request)
    {
        // ---- Logging middleware (always runs, even if we 404 or 401 below) ----
        Console.WriteLine($"[LOG] {request.Method} {request.Path}");

        // ---- Route matching: walk sorted list, first hit = longest prefix ----
        Route? matched = null;
        foreach (var route in _routes)
        {
            // StartsWith is O(prefix.Length) and sufficient for path-prefix matching.
            // A production router would also normalise trailing slashes and URL-decode.
            if (request.Path.StartsWith(route.Prefix))
            {
                matched = route;
                break; // stop at first (= most specific) match
            }
        }

        // No route covers this path -> 404, same as nginx "location not found".
        if (matched == null)
            return new HttpResponse(404, "No route found");

        // ---- Auth middleware (only for routes marked RequireAuth=true) ----
        if (matched.RequireAuth)
        {
            // TryGetValue avoids KeyNotFoundException on a missing Authorization header.
            request.Headers.TryGetValue("Authorization", out string? token);

            if (token == null || !_validTokens.Contains(token))
            {
                Console.WriteLine("[AUTH] Rejected — missing or invalid token");
                return new HttpResponse(401, "Unauthorized"); // 401 = unauthenticated (not 403)
            }
        }

        // ---- Proxy: forward to backend (simulated) ----
        // A real reverse proxy opens a TCP connection to matched.Backend, rewrites the
        // Host header, streams the request body, and pipes the response back to the client.
        Console.WriteLine($"[PROXY] Forwarding to backend: {matched.Backend}");
        return new HttpResponse(200, $"Response from {matched.Backend}");
    }
}

// ---------------------------------------------------------------------------
// Entry point — demo
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q4: Path-Based Request Router       ║");
        Console.WriteLine("╚══════════════════════════════════════╝\n");

        // Build routing table (mirrors a real API Gateway or nginx config).
        // AddRoute order doesn't matter — the list is re-sorted after each insertion.
        var router = new RequestRouter();
        router.AddRoute("/api/users", backend: "user-service:8080", requireAuth: true);
        router.AddRoute("/api/orders", backend: "order-service:8081", requireAuth: true);
        router.AddRoute("/api/", backend: "api-service:8082", requireAuth: false);
        router.AddRoute("/static/", backend: "cdn-origin:9000", requireAuth: false);
        router.AddRoute("/", backend: "web-server:3000", requireAuth: false);
        router.AddValidToken("Bearer valid-token-123");

        // Test 1: protected route with a valid token -> 200
        Console.WriteLine("--- Authenticated request to protected route ---");
        var req1 = new RequestRouter.HttpRequest("GET", "/api/users/1",
            new Dictionary<string, string> { ["Authorization"] = "Bearer valid-token-123" });
        var res1 = router.Handle(req1);
        Console.WriteLine($"  => {res1.StatusCode}: {res1.Body}\n");

        // Test 2: same route without a token -> 401
        Console.WriteLine("--- Protected route, missing auth token ---");
        var req2 = new RequestRouter.HttpRequest("GET", "/api/users/1",
            new Dictionary<string, string>());
        var res2 = router.Handle(req2);
        Console.WriteLine($"  => {res2.StatusCode}: {res2.Body}\n");

        // Test 3: public static asset — auth not required -> 200
        Console.WriteLine("--- Public static asset ---");
        var req3 = new RequestRouter.HttpRequest("GET", "/static/logo.png",
            new Dictionary<string, string>());
        var res3 = router.Handle(req3);
        Console.WriteLine($"  => {res3.StatusCode}: {res3.Body}\n");

        // Test 4: path not covered by any route -> 404
        Console.WriteLine("--- Unknown path ---");
        var req4 = new RequestRouter.HttpRequest("GET", "/unknown/path",
            new Dictionary<string, string>());
        var res4 = router.Handle(req4);
        Console.WriteLine($"  => {res4.StatusCode}: {res4.Body}");

        Console.WriteLine("\n--- Route priority after sorting (longest prefix first) ---");
        Console.WriteLine("  /api/orders (11) > /api/users (10) > /static/ (8) > /api/ (5) > / (1)");
        Console.WriteLine("  GET /api/users/profile -> matches /api/users, not /api/ or /");
    }
}
