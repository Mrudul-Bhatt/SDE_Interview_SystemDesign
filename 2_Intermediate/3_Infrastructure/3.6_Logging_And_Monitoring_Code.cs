// Q9. Implement Logging & Monitoring
//
// Simulate the three observability pillars in a single in-process demo:
//   Logs    — structured JSON log entries with levels, correlation IDs, PII redaction
//   Metrics — Counter, Gauge, Histogram (p50/p95/p99), RED method per service
//   Traces  — TraceId/SpanId propagation across simulated service calls, span tree
//
// Plus: alert rule engine that fires when metric thresholds are breached.
//
// Architecture
// ─────────────
//   StructuredLogger   → writes JSON log entries; filters by min level
//   MetricsRegistry    → stores Counter/Gauge/Histogram; computes percentiles
//   Tracer             → creates Trace/Span tree; propagates context via TraceContext
//   AlertEngine        → evaluates rules against current metric values; fires alerts
//   ObservabilityStack → wires all three together (correlation, shared traceId in logs)
//
// Complexity: log O(1), record metric O(1), percentile O(n log n) on read

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Infrastructure
{
    // =========================================================================
    // PILLAR 1: Structured Logging
    // =========================================================================

    public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Fatal = 5 }

    public class LogEntry
    {
        public DateTime Timestamp;
        public LogLevel Level;
        public string   Service;
        public string   TraceId;
        public string   SpanId;
        public string   Message;
        public Dictionary<string, object> Fields = new Dictionary<string, object>();

        // Serialize as compact JSON for log aggregators (Elasticsearch, Loki).
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"ts\":\"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ}\"");
            sb.Append($",\"level\":\"{Level.ToString().ToUpper()}\"");
            sb.Append($",\"service\":\"{Service}\"");
            if (!string.IsNullOrEmpty(TraceId)) sb.Append($",\"traceId\":\"{TraceId}\"");
            if (!string.IsNullOrEmpty(SpanId))  sb.Append($",\"spanId\":\"{SpanId}\"");
            sb.Append($",\"msg\":\"{Message}\"");
            foreach (var f in Fields)
                sb.Append($",\"{f.Key}\":{Serialize(f.Value)}");
            sb.Append('}');
            return sb.ToString();
        }

        private static string Serialize(object v) =>
            v is string s ? $"\"{s}\"" :
            v is bool   b ? (b ? "true" : "false") :
            v?.ToString() ?? "null";
    }

    // PII redactor: strips sensitive fields before writing to log sink.
    // Prevents card numbers, passwords, tokens from reaching log storage.
    public static class Redactor
    {
        private static readonly HashSet<string> _sensitiveKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "password", "cardNumber", "cvv", "ssn", "apiKey", "token", "secret"
        };

        public static Dictionary<string, object> Redact(Dictionary<string, object> fields)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in fields)
                clean[kv.Key] = _sensitiveKeys.Contains(kv.Key) ? "***REDACTED***" : kv.Value;
            return clean;
        }
    }

    // Structured logger: writes to an in-memory sink (simulates stdout → Fluentd).
    // Min-level filter: DEBUG/TRACE suppressed in production.
    public class StructuredLogger
    {
        private readonly string   _service;
        private readonly LogLevel _minLevel;
        private readonly List<LogEntry> _sink = new List<LogEntry>(); // in-memory log store

        // Trace context injected per-request so all logs within a request share traceId.
        [ThreadStatic] public static string CurrentTraceId;
        [ThreadStatic] public static string CurrentSpanId;

        public IReadOnlyList<LogEntry> Entries => _sink;

        public StructuredLogger(string service, LogLevel minLevel = LogLevel.Info)
        {
            _service  = service;
            _minLevel = minLevel;
        }

        public void Log(LogLevel level, string message, Dictionary<string, object> fields = null)
        {
            if (level < _minLevel) return; // filter below minimum level

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level     = level,
                Service   = _service,
                TraceId   = CurrentTraceId,
                SpanId    = CurrentSpanId,
                Message   = message,
                Fields    = Redactor.Redact(fields ?? new Dictionary<string, object>())
            };
            _sink.Add(entry);
        }

        public void Info (string msg, Dictionary<string, object> f = null) => Log(LogLevel.Info,  msg, f);
        public void Warn (string msg, Dictionary<string, object> f = null) => Log(LogLevel.Warn,  msg, f);
        public void Error(string msg, Dictionary<string, object> f = null) => Log(LogLevel.Error, msg, f);
        public void Debug(string msg, Dictionary<string, object> f = null) => Log(LogLevel.Debug, msg, f);

        // Query entries by level (simulates Elasticsearch filter query).
        public IEnumerable<LogEntry> Query(LogLevel? level = null, string traceId = null)
        {
            IEnumerable<LogEntry> q = _sink;
            if (level   != null) q = q.Where(e => e.Level   == level);
            if (traceId != null) q = q.Where(e => e.TraceId == traceId);
            return q;
        }
    }

    // =========================================================================
    // PILLAR 2: Metrics
    // =========================================================================

    // Counter: monotonically increasing. Reset only on service restart.
    // Use for: total_requests, total_errors, total_bytes_sent.
    public class Counter
    {
        public string Name { get; }
        private long  _value;
        public Counter(string name) => Name = name;
        public void   Increment(long by = 1) => Interlocked.Add(ref _value, by);
        public long   Value => Interlocked.Read(ref _value);
    }

    // Gauge: current value; can go up or down.
    // Use for: active_connections, queue_depth, memory_bytes.
    public class Gauge
    {
        public string Name { get; }
        private double _value;
        private readonly object _lock = new object();
        public Gauge(string name) => Name = name;
        public void   Set(double v) { lock (_lock) _value = v; }
        public void   Inc(double by = 1) { lock (_lock) _value += by; }
        public void   Dec(double by = 1) { lock (_lock) _value -= by; }
        public double Value { get { lock (_lock) return _value; } }
    }

    // Histogram: distribution of observed values across fixed buckets.
    // Computes p50/p95/p99/p999 on demand — critical for latency SLOs.
    // The mean hides tail latency: mean=50ms but p99=2s = bad user experience.
    public class Histogram
    {
        public string Name { get; }
        private readonly List<double>    _observations = new List<double>();
        private readonly object          _lock         = new object();
        private readonly double[]        _buckets;     // upper bounds in ms

        public Histogram(string name, double[] buckets = null)
        {
            Name     = name;
            _buckets = buckets ?? new[] { 5.0, 10, 25, 50, 100, 250, 500, 1000, 2000 };
        }

        public void Observe(double value) { lock (_lock) _observations.Add(value); }

        public double Percentile(double p) // p = 0.95 for p95
        {
            lock (_lock)
            {
                if (_observations.Count == 0) return 0;
                var sorted = _observations.OrderBy(v => v).ToList();
                int idx    = (int)Math.Ceiling(p * sorted.Count) - 1;
                return sorted[Math.Max(0, idx)];
            }
        }

        public long   Count { get { lock (_lock) return _observations.Count; } }
        public double Sum   { get { lock (_lock) return _observations.Sum(); } }

        // Bucket counts (for histogram bar charts in Grafana).
        public Dictionary<string, long> BucketCounts()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, long>();
                foreach (double upper in _buckets)
                {
                    result[$"le={upper}ms"] = _observations.Count(v => v <= upper);
                }
                result["le=+Inf"] = _observations.Count;
                return result;
            }
        }
    }

    // Central registry: single place to create and retrieve all metrics.
    // In production: Prometheus client library; metrics exposed at GET /metrics.
    public class MetricsRegistry
    {
        private readonly Dictionary<string, Counter>   _counters   = new Dictionary<string, Counter>();
        private readonly Dictionary<string, Gauge>     _gauges     = new Dictionary<string, Gauge>();
        private readonly Dictionary<string, Histogram> _histograms = new Dictionary<string, Histogram>();

        public Counter   Counter  (string name) => _counters  .GetOrAdd(name, n => new Counter(n));
        public Gauge     Gauge    (string name) => _gauges    .GetOrAdd(name, n => new Gauge(n));
        public Histogram Histogram(string name) => _histograms.GetOrAdd(name, n => new Histogram(n));

        public void PrintSummary(string service)
        {
            Console.WriteLine($"\n  [{service}] Metrics summary:");
            foreach (var c in _counters.Values)
                Console.WriteLine($"    Counter   {c.Name,-40} = {c.Value}");
            foreach (var g in _gauges.Values)
                Console.WriteLine($"    Gauge     {g.Name,-40} = {g.Value:F0}");
            foreach (var h in _histograms.Values)
                Console.WriteLine($"    Histogram {h.Name,-40} count={h.Count} " +
                                  $"p50={h.Percentile(0.50):F1}ms " +
                                  $"p95={h.Percentile(0.95):F1}ms " +
                                  $"p99={h.Percentile(0.99):F1}ms");
        }
    }

    internal static class DictionaryExtensions
    {
        public static TValue GetOrAdd<TKey, TValue>(
            this Dictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> factory)
        {
            if (!dict.TryGetValue(key, out TValue v))
                dict[key] = v = factory(key);
            return v;
        }
    }

    // =========================================================================
    // PILLAR 3: Distributed Tracing
    // =========================================================================

    public class Span
    {
        public string   TraceId;
        public string   SpanId;
        public string   ParentSpanId;
        public string   ServiceName;
        public string   OperationName;
        public DateTime StartTime;
        public DateTime EndTime;
        public bool     IsFinished;
        public Dictionary<string, string> Tags = new Dictionary<string, string>();

        public double DurationMs => (EndTime - StartTime).TotalMilliseconds;

        public void Finish()
        {
            EndTime    = DateTime.UtcNow;
            IsFinished = true;
        }

        public void SetTag(string key, string value) => Tags[key] = value;
    }

    // Tracer: creates spans and holds the in-memory trace store.
    // Propagates context via TraceContext (simulates W3C traceparent header).
    public class Tracer
    {
        private readonly string       _service;
        private readonly List<Span>   _spans = new List<Span>();

        public Tracer(string service) => _service = service;

        public Span StartSpan(string operation, string traceId = null, string parentSpanId = null)
        {
            string tid = traceId ?? Guid.NewGuid().ToString("N")[..16];
            var span = new Span
            {
                TraceId       = tid,
                SpanId        = Guid.NewGuid().ToString("N")[..8],
                ParentSpanId  = parentSpanId,
                ServiceName   = _service,
                OperationName = operation,
                StartTime     = DateTime.UtcNow
            };
            lock (_spans) _spans.Add(span);
            return span;
        }

        public IReadOnlyList<Span> GetTrace(string traceId)
        {
            lock (_spans) return _spans.Where(s => s.TraceId == traceId).ToList();
        }

        // Print the trace as a tree (Jaeger-style waterfall view).
        public void PrintTrace(string traceId)
        {
            IReadOnlyList<Span> spans = GetTrace(traceId);
            if (spans.Count == 0) { Console.WriteLine("  (no spans for this trace)"); return; }

            Console.WriteLine($"\n  Trace {traceId} — {spans.Count} span(s):");
            Console.WriteLine($"  {"Operation",-32} {"Service",-20} {"Duration",10}  SpanId");
            Console.WriteLine($"  {new string('─', 75)}");

            // Sort by start time for waterfall display.
            foreach (Span s in spans.OrderBy(sp => sp.StartTime))
            {
                string indent = string.IsNullOrEmpty(s.ParentSpanId) ? "" : "  └─ ";
                Console.WriteLine($"  {(indent + s.OperationName),-32} {s.ServiceName,-20} " +
                                  $"{s.DurationMs,8:F1}ms  {s.SpanId}");
            }
        }
    }

    // =========================================================================
    // Alert Engine
    // =========================================================================

    public class AlertRule
    {
        public string          Name;
        public string          Description;
        public Func<bool>      Condition;    // returns true when alert should fire
        public string          Severity;     // "critical" | "warning"
        public string          Runbook;      // link to remediation steps
    }

    public class AlertFiring
    {
        public string   Rule;
        public string   Severity;
        public DateTime FiredAt;
        public string   Description;
    }

    public class AlertEngine
    {
        private readonly List<AlertRule>   _rules    = new List<AlertRule>();
        private readonly List<AlertFiring> _fired    = new List<AlertFiring>();

        public AlertEngine Register(AlertRule rule) { _rules.Add(rule); return this; }

        // Evaluate all rules. Returns newly fired alerts.
        public List<AlertFiring> Evaluate()
        {
            var newAlerts = new List<AlertFiring>();
            foreach (AlertRule rule in _rules)
            {
                try
                {
                    if (rule.Condition())
                    {
                        var alert = new AlertFiring
                        {
                            Rule        = rule.Name,
                            Severity    = rule.Severity,
                            FiredAt     = DateTime.UtcNow,
                            Description = rule.Description
                        };
                        _fired.Add(alert);
                        newAlerts.Add(alert);
                    }
                }
                catch { /* rule evaluation must never crash the engine */ }
            }
            return newAlerts;
        }
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public class Program
    {
        // Shared tracer so spans from all "services" end up in one store.
        private static readonly Tracer SharedTracer = new Tracer("multi-service");

        public static void Main()
        {
            // =================================================================
            // Scenario 1 — Structured logging: levels, fields, PII redaction
            // INFO and above written; DEBUG filtered (min level = INFO).
            // Sensitive fields (password, cardNumber) automatically redacted.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Structured logging — levels, fields, PII redact  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var logger = new StructuredLogger("order-service", minLevel: LogLevel.Info);

            logger.Debug("Computing cart total", new Dictionary<string, object>
                { ["cartId"] = "cart-55" }); // filtered — below INFO

            logger.Info("Order placed", new Dictionary<string, object>
            {
                ["orderId"] = "order-301",
                ["userId"]  = 42,
                ["total"]   = 149.99
            });

            logger.Warn("Payment retry", new Dictionary<string, object>
                { ["attempt"] = 2, ["maxAttempts"] = 3 });

            logger.Error("Payment failed", new Dictionary<string, object>
            {
                ["userId"]     = 42,
                ["cardNumber"] = "4111111111111111", // ← PII — will be redacted
                ["password"]   = "secret123",         // ← PII — will be redacted
                ["error"]      = "card_declined"
            });

            Console.WriteLine("  All log entries (JSON — as they arrive in Elasticsearch):");
            foreach (LogEntry e in logger.Entries)
                Console.WriteLine($"  {e.ToJson()}");

            Console.WriteLine($"\n  Total entries: {logger.Entries.Count}  (DEBUG was filtered)");

            Console.WriteLine("\n  Query: level=ERROR only:");
            foreach (LogEntry e in logger.Query(level: LogLevel.Error))
                Console.WriteLine($"    {e.ToJson()}");

            // =================================================================
            // Scenario 2 — Distributed tracing: traceId propagated across services
            // Simulates API Gateway → Order Service → DB query + Cache lookup.
            // All logs within the request share the same traceId — correlatable.
            // Trace waterfall shows where time was spent.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Distributed tracing — traceId across services    ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var gatewayLogger = new StructuredLogger("api-gateway");
            var orderLogger   = new StructuredLogger("order-service");
            var dbLogger      = new StructuredLogger("db-proxy");

            // Step 1: API Gateway creates the root span and injects traceId.
            Span rootSpan = SharedTracer.StartSpan("HTTP GET /api/orders/301");
            string traceId = rootSpan.TraceId;

            // Set thread-static context so all loggers in this "request" share traceId.
            StructuredLogger.CurrentTraceId = traceId;
            StructuredLogger.CurrentSpanId  = rootSpan.SpanId;

            gatewayLogger.Info("Request received", new Dictionary<string, object>
                { ["method"] = "GET", ["path"] = "/api/orders/301", ["clientIp"] = "1.2.3.4" });

            Thread.Sleep(5);

            // Step 2: Order Service creates a child span.
            Span orderSpan = SharedTracer.StartSpan("GetOrder", traceId, rootSpan.SpanId);
            StructuredLogger.CurrentSpanId = orderSpan.SpanId;
            orderLogger.Info("Fetching order", new Dictionary<string, object> { ["orderId"] = "order-301" });

            Thread.Sleep(10);

            // Step 3: DB query — another child span (the bottleneck).
            Span dbSpan = SharedTracer.StartSpan("SELECT orders WHERE id=301", traceId, orderSpan.SpanId);
            StructuredLogger.CurrentTraceId = traceId;
            StructuredLogger.CurrentSpanId  = dbSpan.SpanId;
            dbLogger.Info("Executing query", new Dictionary<string, object>
                { ["table"] = "orders", ["index"] = "idx_orders_id" });

            Thread.Sleep(120); // ← slow DB query (missing index simulation)
            dbSpan.SetTag("db.rows_examined", "450000");
            dbSpan.Finish();

            // Step 4: Cache lookup (fast).
            Span cacheSpan = SharedTracer.StartSpan("Cache GET order:301", traceId, orderSpan.SpanId);
            Thread.Sleep(3);
            cacheSpan.Finish();

            orderSpan.Finish();

            Thread.Sleep(5);
            rootSpan.Finish();

            SharedTracer.PrintTrace(traceId);

            Console.WriteLine($"\n  All logs for traceId={traceId} (from any service):");
            var allLoggers = new[] { gatewayLogger, orderLogger, dbLogger };
            foreach (var lg in allLoggers)
                foreach (LogEntry e in lg.Query(traceId: traceId))
                    Console.WriteLine($"    {e.ToJson()}");

            // =================================================================
            // Scenario 3 — Metrics (RED method): counters, gauges, histograms
            // Simulate 20 requests to the checkout service with realistic latencies.
            // 2 requests fail (error rate = 10%). Compute p50/p95/p99 latency.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Metrics — RED method (Rate/Errors/Duration)       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var metrics = new MetricsRegistry();

            Counter   reqTotal    = metrics.Counter  ("http_requests_total");
            Counter   errTotal    = metrics.Counter  ("http_errors_total");
            Gauge     activeConns = metrics.Gauge    ("http_active_connections");
            Histogram latency     = metrics.Histogram("http_request_duration_ms");

            // Simulated latency distribution: mostly fast, two slow outliers.
            double[] simulatedLatencies = {
                12, 18, 15, 22, 14, 11, 19, 16, 13, 17,
                20, 25, 18, 14, 16, 23, 1800, 2100, 12, 15
            };
            bool[] failures = {
                false, false, false, false, false, false, false, false, false, false,
                false, false, false, false, false, false, true,  true,  false, false
            };

            Console.WriteLine("  Processing 20 simulated checkout requests...\n");
            for (int i = 0; i < simulatedLatencies.Length; i++)
            {
                activeConns.Inc();
                reqTotal.Increment();
                latency.Observe(simulatedLatencies[i]);

                if (failures[i])
                {
                    errTotal.Increment();
                    Console.WriteLine($"  Request {i + 1,2}: {simulatedLatencies[i],6:F0}ms  ERROR");
                }
                else
                {
                    Console.WriteLine($"  Request {i + 1,2}: {simulatedLatencies[i],6:F0}ms  OK");
                }
                activeConns.Dec();
            }

            metrics.PrintSummary("checkout-service");

            double errorRate = errTotal.Value * 100.0 / reqTotal.Value;
            Console.WriteLine($"\n  Error rate: {errorRate:F1}%");
            Console.WriteLine($"  Observation: p99={latency.Percentile(0.99):F0}ms reveals 2 outliers");
            Console.WriteLine($"  Mean would show: {simulatedLatencies.Average():F1}ms  ← hides tail!");

            // =================================================================
            // Scenario 4 — Alert engine: rules fire when thresholds breached
            // Rules: high error rate (>5%), p99 latency SLO breach (>500ms),
            // and high active connections (gauge). Evaluate and report fired alerts.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Alert engine — thresholds and SLO breach          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

            var alerts = new AlertEngine();

            alerts
                .Register(new AlertRule
                {
                    Name        = "HighErrorRate",
                    Severity    = "critical",
                    Description = $"Error rate {errorRate:F1}% > 5% threshold",
                    Runbook     = "https://runbooks/high-error-rate",
                    Condition   = () => errTotal.Value * 100.0 / reqTotal.Value > 5.0
                })
                .Register(new AlertRule
                {
                    Name        = "P99LatencySLOBreach",
                    Severity    = "critical",
                    Description = $"p99={latency.Percentile(0.99):F0}ms > 500ms SLO",
                    Runbook     = "https://runbooks/latency-slo",
                    Condition   = () => latency.Percentile(0.99) > 500
                })
                .Register(new AlertRule
                {
                    Name        = "HighActiveConnections",
                    Severity    = "warning",
                    Description = $"Active connections = {activeConns.Value} > 1000",
                    Runbook     = "https://runbooks/connection-pool",
                    Condition   = () => activeConns.Value > 1000 // not breached
                });

            List<AlertFiring> fired = alerts.Evaluate();

            Console.WriteLine($"  Evaluated {3} alert rules — {fired.Count} fired:\n");
            if (fired.Count == 0)
            {
                Console.WriteLine("  (no alerts fired)");
            }
            else
            {
                foreach (AlertFiring a in fired)
                    Console.WriteLine($"  🔥 [{a.Severity.ToUpper()}] {a.Rule}\n" +
                                      $"     {a.Description}\n" +
                                      $"     Fired: {a.FiredAt:HH:mm:ss}");
            }
        }
    }

} // namespace Infrastructure
