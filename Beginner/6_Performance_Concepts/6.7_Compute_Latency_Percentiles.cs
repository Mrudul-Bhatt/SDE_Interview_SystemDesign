// Q4. Compute Latency Percentiles (p50, p95, p99)
// Given a list of request latency samples, compute p50, p95, and p99. This is how
// engineers measure "tail latency" — the worst-case experience that SLA guarantees
// are built around.
//
// Why p99 matters more than average:
//   Scenario: your API fans out to 10 backend services
//   Each backend has p99 = 500ms (1% of calls take >500ms)
//   Probability that AT LEAST ONE backend is slow on a given request:
//     = 1 - (0.99)^10 = 1 - 0.904 = 9.6%
//   At 1000 req/s → 96 users/second experience a slow response
//
// Rule of thumb:
//   Your system's p99 ≈ your worst backend's p99.9
//   → Always optimize tail latency, not average latency
//   → SLA: "p99 < 200ms" is far more meaningful than "avg < 50ms"
//
// What interviewers test:
//   1. Why use p99 over average for SLAs?
//      → Average is dragged down by fast requests and hides spikes
//      → p99 = worst 1% — the experience your slowest users actually get
//   2. Fan-out math: 10 services × p99=100ms → ~10% of all requests are slow
//   3. Nearest-rank method vs linear interpolation — know the difference
//
// Complexity: O(n log n) for sort, then O(1) per percentile query

using System.Collections.Generic;
using System.Linq;

namespace PerformanceConcepts
{

// ---------------------------------------------------------------------------
// LatencyAnalyzer — computes and prints latency percentiles
// ---------------------------------------------------------------------------
public static class LatencyAnalyzer
{
    public static double Percentile(List<double> samples, double percentile)
    {
        if (samples.Count == 0)
            throw new ArgumentException("No samples provided.");
        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Must be 0–100.");

        // Copy before sorting so we don't mutate the caller's list.
        // If the caller passes a pre-sorted list, this is wasted work — but
        // correctness beats micro-optimization for a utility method.
        var sorted = new List<double>(samples);
        sorted.Sort();

        // Nearest-rank method: rank = (p/100) * N, then round up.
        // Example: p50 of 10 samples → rank = 5.0 → index = 4 (0-based)
        // We use ceiling rather than floor so p100 maps to the last element,
        // not one past it.
        double rank = (percentile / 100.0) * sorted.Count;
        int index = (int)Math.Ceiling(rank) - 1;

        // Clamp to valid range: p0 on 1 sample gives index=-1 → clamp to 0.
        index = Math.Max(0, Math.Min(index, sorted.Count - 1));

        return sorted[index];
    }

    public static void PrintReport(List<double> samples, string label = "Latency Report")
    {
        // Sort once here so Percentile() calls below don't each re-sort.
        // We pass samples (unsorted) to Percentile() which copies and sorts
        // internally — redundant sorts are acceptable in a reporting method
        // that runs infrequently (not per-request).
        var sorted = new List<double>(samples);
        sorted.Sort();

        double avg  = samples.Average();
        double min  = sorted[0];
        double max  = sorted[^1];  // ^1 = last element (C# index-from-end syntax)
        double p50  = Percentile(samples, 50);
        double p95  = Percentile(samples, 95);
        double p99  = Percentile(samples, 99);
        double p999 = Percentile(samples, 99.9);

        Console.WriteLine($"\n=== {label} ({samples.Count:N0} samples) ===");
        Console.WriteLine($"  Min:   {min,8:F1}ms");
        Console.WriteLine($"  Avg:   {avg,8:F1}ms   ← misleading when distribution is skewed");
        Console.WriteLine($"  p50:   {p50,8:F1}ms   ← median: half of requests are faster");
        Console.WriteLine($"  p95:   {p95,8:F1}ms   ← 95% of requests complete within this");
        Console.WriteLine($"  p99:   {p99,8:F1}ms   ← tail latency: SLAs are usually set here");
        Console.WriteLine($"  p99.9: {p999,8:F1}ms   ← 1 in 1000 requests is this slow");
        Console.WriteLine($"  Max:   {max,8:F1}ms");
    }

    // Generates a realistic bimodal latency distribution:
    //   70% fast (20–50ms)    — cache hits, simple reads
    //   25% medium (50–200ms) — DB reads, small computations
    //    4% slow (200–800ms)  — cold cache, complex queries
    //    1% spike (800–5000ms)— GC pause, network blip, downstream timeout
    //
    // Seeding Random(42) makes the output reproducible across runs — useful
    // for unit tests and demos where you need deterministic numbers.
    public static List<double> SimulateLatencies(int count, Random? rng = null)
    {
        rng ??= new Random(42);
        var samples = new List<double>(count);

        for (int i = 0; i < count; i++)
        {
            double u = rng.NextDouble(); // uniform 0.0–1.0

            // Switch expression maps each probability bucket to a latency range.
            // The bucket boundaries (0.70, 0.95, 0.99) match real-world P-distributions
            // from services like the AWS SDKs and Nginx access logs.
            double latency = u switch
            {
                < 0.70 => 20  + rng.NextDouble() * 30,    // fast:   20– 50ms
                < 0.95 => 50  + rng.NextDouble() * 150,   // medium: 50–200ms
                < 0.99 => 200 + rng.NextDouble() * 600,   // slow:  200–800ms
                _      => 800 + rng.NextDouble() * 4200   // spike: 800–5000ms
            };
            samples.Add(latency);
        }
        return samples;
    }
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Latency Percentile Calculator       ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        // ===================================================================
        // Demo 1 — Realistic API latency distribution (10,000 samples)
        // Shows the gap between average (~96ms) and p99 (~670ms).
        // ===================================================================
        var samples = LatencyAnalyzer.SimulateLatencies(10_000);
        LatencyAnalyzer.PrintReport(samples, "API Gateway — 10,000 requests");

        // ===================================================================
        // Demo 2 — Why average is a lie
        // 9 fast requests + 1 huge spike: average looks bad but p50 is fine.
        // In a real system, avg=501ms would wake up oncall; p50=10ms tells you
        // 90% of users are happy and the problem is isolated to tail requests.
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Why Average is Misleading           ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        var skewed = new List<double> { 10, 10, 10, 10, 10, 10, 10, 10, 10, 5000 };
        LatencyAnalyzer.PrintReport(skewed, "10 requests with 1 spike");

        // ===================================================================
        // Demo 3 — Fan-out tail latency math
        // Demonstrates why a single backend's p99 is not your system's p99.
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════╗");
        Console.WriteLine("║  Fan-out Tail Latency Math           ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        int[] fanOutSizes = [1, 3, 5, 10, 20, 50];
        double backendP99 = 0.99; // 99% chance each backend is fast

        Console.WriteLine($"\n  Each backend: p99 = 100ms (1% chance of being slow)");
        Console.WriteLine($"  {"Fan-out",-10} {"% requests with ≥1 slow backend",0}");
        Console.WriteLine($"  {new string('-', 48)}");

        foreach (int n in fanOutSizes)
        {
            // Probability ALL n backends are fast = 0.99^n.
            // Probability at least ONE is slow = 1 - 0.99^n.
            double probAllFast = Math.Pow(backendP99, n);
            double probAnySlow = 1.0 - probAllFast;
            Console.WriteLine($"  {n,-10} {probAnySlow * 100,6:F1}%  ← {(probAnySlow > 0.1 ? "⚠ > 10% of users see a slow response" : "")}");
        }

        Console.WriteLine("\n  Rule: fan-out makes your p99 look like your backend's p99.9");
        Console.WriteLine("        → optimize tail latency aggressively before scaling out");
    }
}

} // namespace PerformanceConcepts
