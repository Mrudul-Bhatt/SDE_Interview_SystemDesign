// Q1. Implement a Circuit Breaker
//
// Wrap every call to a downstream service. Track failures. When failures
// exceed the threshold, open the circuit — return an error immediately
// instead of waiting for a timeout. After a reset timer, probe the service.
// If the probe succeeds, close the circuit and resume normal traffic.
//
// Three States
// ─────────────
// CLOSED    → normal operation; failures counted; trips at threshold
// OPEN      → fail fast (< 1ms); downstream not called; timer running
// HALF-OPEN → one probe allowed through; success → CLOSED, fail → OPEN
//
// Why this matters
// ─────────────────
// Without CB: Payment Service slow → Order Service threads all waiting
//             → Order Service thread pool exhausted → cascading failure
// With CB:    After N failures, every call fails in < 1ms, threads free,
//             cascade stops, Payment Service gets breathing room to recover
//
// Complexity: Execute O(1) — state check and counter increment only

using System;
using System.Threading;

namespace ResiliencePatterns
{
    // -------------------------------------------------------------------------
    // CircuitBreakerState
    // -------------------------------------------------------------------------
    public enum CircuitBreakerState
    {
        Closed,   // normal — requests flow through
        Open,     // tripped — requests fail immediately
        HalfOpen  // probing — one request allowed through to test recovery
    }

    // -------------------------------------------------------------------------
    // CircuitOpenException
    // -------------------------------------------------------------------------
    // Distinct exception type so callers can distinguish "circuit is open"
    // from "service threw an error". The catch block in Execute explicitly
    // filters this out so circuit exceptions never count as service failures.
    public class CircuitOpenException : Exception
    {
        public CircuitOpenException(string service)
            : base($"Circuit OPEN for '{service}' — call rejected, failing fast") { }
    }

    // -------------------------------------------------------------------------
    // CircuitBreaker
    // -------------------------------------------------------------------------
    public class CircuitBreaker
    {
        // ── State ────────────────────────────────────────────────────────────
        private CircuitBreakerState _state = CircuitBreakerState.Closed;
        private int _failureCount = 0; // consecutive failures in CLOSED
        private int _probeSuccessCount = 0; // successes needed in HALF-OPEN
        private DateTime _openedAt = DateTime.MinValue;

        // ── Config ───────────────────────────────────────────────────────────
        private readonly string _serviceName;
        private readonly int _failureThreshold;     // CLOSED → OPEN after this many failures
        private readonly TimeSpan _resetTimeout;         // how long to stay OPEN before probing
        private readonly int _probeSuccessThreshold; // HALF-OPEN → CLOSED after this many successes

        // One lock for all state transitions. State checks AND mutations must
        // be atomic — without the lock, two threads could both read CLOSED,
        // both increment _failureCount to threshold, and both trip the circuit
        // simultaneously, causing double-logging and inconsistent state.
        private readonly object _lock = new object();

        public CircuitBreakerState State => _state;

        public CircuitBreaker(
            string serviceName,
            int failureThreshold = 3,
            int resetTimeoutMs = 500,
            int probeSuccessThreshold = 1)
        {
            _serviceName = serviceName;
            _failureThreshold = failureThreshold;
            _resetTimeout = TimeSpan.FromMilliseconds(resetTimeoutMs);
            _probeSuccessThreshold = probeSuccessThreshold;
        }

        // Execute: attempt the operation, apply circuit logic around it.
        // fallback: optional function to call when the circuit is OPEN or the
        //           operation fails — returns a degraded-but-safe response.
        public T Execute<T>(Func<T> operation, Func<T> fallback = null)
        {
            // ── Pre-call state check ──────────────────────────────────────────
            // Snapshot the state inside a lock, but DO NOT hold the lock during
            // execution. Holding the lock while calling operation() would:
            //   1. Serialize all concurrent calls (no parallelism)
            //   2. Deadlock if operation() itself calls Execute() (re-entrant)
            CircuitBreakerState stateSnapshot;
            lock (_lock)
            {
                // Auto-transition OPEN → HALF-OPEN when the reset timer expires.
                // We check this on every call rather than via a background timer
                // so there are no background threads — simpler, fewer moving parts.
                if (_state == CircuitBreakerState.Open &&
                    DateTime.UtcNow - _openedAt >= _resetTimeout)
                {
                    TransitionTo(CircuitBreakerState.HalfOpen);
                }
                stateSnapshot = _state;
            }

            // ── OPEN: fail fast ───────────────────────────────────────────────
            if (stateSnapshot == CircuitBreakerState.Open)
            {
                Console.WriteLine($"    [CB:{_serviceName}] OPEN  → fast fail (no downstream call)");
                if (fallback != null) return fallback();
                throw new CircuitOpenException(_serviceName);
            }

            // ── CLOSED or HALF-OPEN: attempt the call ─────────────────────────
            try
            {
                T result = operation();
                RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                // Never count a CircuitOpenException as a service failure.
                // That exception comes from THIS circuit breaker, not the service.
                // Counting it would trip an already-open circuit again — incorrect.
                if (ex is CircuitOpenException) throw;

                RecordFailure();
                if (fallback != null) return fallback();
                throw;
            }
        }

        private void RecordSuccess()
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    _probeSuccessCount++;
                    Console.WriteLine($"    [CB:{_serviceName}] HALF-OPEN probe succeeded " +
                                      $"({_probeSuccessCount}/{_probeSuccessThreshold})");

                    if (_probeSuccessCount >= _probeSuccessThreshold)
                        TransitionTo(CircuitBreakerState.Closed);
                }
                else
                {
                    // In CLOSED state, a success resets the consecutive failure counter.
                    // We forgive transient failures — only sustained failures trip the circuit.
                    // Without this reset, 2 failures + 100 successes + 1 failure = trip,
                    // which is far too sensitive for a healthy service.
                    _failureCount = 0;
                }
            }
        }

        private void RecordFailure()
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    // A single probe failure proves the service is still down.
                    // Go back to OPEN immediately — don't accumulate failure count.
                    Console.WriteLine($"    [CB:{_serviceName}] HALF-OPEN probe FAILED → reopening");
                    TransitionTo(CircuitBreakerState.Open);
                }
                else if (_state == CircuitBreakerState.Closed)
                {
                    _failureCount++;
                    Console.WriteLine($"    [CB:{_serviceName}] CLOSED  failure " +
                                      $"{_failureCount}/{_failureThreshold}");

                    if (_failureCount >= _failureThreshold)
                        TransitionTo(CircuitBreakerState.Open);
                }
                // If somehow we get here in OPEN state (race condition), ignore —
                // the circuit is already open, nothing to do.
            }
        }

        // TransitionTo must always be called while holding _lock.
        // It is private so external code can never drive state changes directly.
        private void TransitionTo(CircuitBreakerState newState)
        {
            CircuitBreakerState prev = _state;
            _state = newState;

            if (newState == CircuitBreakerState.Open)
            {
                _openedAt = DateTime.UtcNow; // start the reset timer
                _failureCount = 0;
                Console.WriteLine($"    [CB:{_serviceName}] *** TRIPPED: {prev} → OPEN " +
                                  $"(reset in {_resetTimeout.TotalMilliseconds}ms) ***");
            }
            else if (newState == CircuitBreakerState.HalfOpen)
            {
                _probeSuccessCount = 0;
                Console.WriteLine($"    [CB:{_serviceName}] → HALF-OPEN (sending probe...)");
            }
            else // Closed
            {
                _failureCount = 0;
                _probeSuccessCount = 0;
                Console.WriteLine($"    [CB:{_serviceName}] ✓ CLOSED (service recovered, resuming)");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        // Simulates a payment service. healthy=true → succeeds, false → throws.
        private static bool _paymentServiceHealthy = true;
        private static int _callsMadeToService = 0;

        private static string CallPaymentService(int orderId)
        {
            _callsMadeToService++;
            if (!_paymentServiceHealthy)
                throw new Exception("Payment Service: connection refused (DB overloaded)");
            return $"Payment accepted for order #{orderId}";
        }

        public static void Main()
        {
            // failureThreshold=3: trip after 3 consecutive failures
            // resetTimeoutMs=500: stay OPEN for 500ms before probing
            // probeSuccessThreshold=1: one successful probe closes the circuit
            var cb = new CircuitBreaker(
                serviceName: "payment-service",
                failureThreshold: 3,
                resetTimeoutMs: 500,
                probeSuccessThreshold: 1);

            // =================================================================
            // Scenario 1 — Normal operation: 5 successful calls, state stays CLOSED
            // Each success resets the failure counter, so transient errors are
            // forgiven. The circuit only trips on SUSTAINED failure.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Normal operation — 5 calls, circuit stays CLOSED ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            _paymentServiceHealthy = true;
            for (int i = 1; i <= 5; i++)
            {
                string result = cb.Execute(() => CallPaymentService(i));
                Console.WriteLine($"  Order {i}: {result}  [state: {cb.State}]");
            }

            // =================================================================
            // Scenario 2 — Service degrades: 3 failures trip the circuit to OPEN
            // Failure 1 → count=1, Failure 2 → count=2, Failure 3 → TRIP.
            // Real world: DB on the downstream service becomes overloaded.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Service fails 3 times — circuit trips to OPEN   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            _paymentServiceHealthy = false;
            int callsBefore = _callsMadeToService;

            for (int i = 6; i <= 8; i++)
            {
                try
                {
                    cb.Execute<string>(() => CallPaymentService(i));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Order {i}: FAILED — {ex.Message}  [state: {cb.State}]");
                }
            }

            // =================================================================
            // Scenario 3 — Circuit OPEN: 5 more calls all fast-fail
            // The downstream service is NOT called at all — zero new entries in
            // _callsMadeToService. Thread pool stays free; cascade is prevented.
            // Fallback returns a safe default ("payment pending") instead of error.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: OPEN — fast fail, downstream not called          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            int callsAtOpen = _callsMadeToService;

            for (int i = 9; i <= 13; i++)
            {
                string result = cb.Execute(
                    () => CallPaymentService(i),
                    fallback: () => $"[FALLBACK] Order #{i} queued — payment pending"
                );
                Console.WriteLine($"  Order {i}: {result}  [state: {cb.State}]");
            }

            Console.WriteLine($"\n  Calls made to Payment Service during OPEN: " +
                              $"{_callsMadeToService - callsAtOpen}  ← should be 0");

            // =================================================================
            // Scenario 4 — Recovery: reset timer expires, HALF-OPEN probe succeeds
            // After 500ms the circuit transitions to HALF-OPEN automatically on
            // the next Execute() call. Service is now healthy — probe succeeds
            // → circuit closes → normal traffic resumes.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Service recovers — probe succeeds → CLOSED       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            Console.WriteLine("\n  Payment service restarted. Waiting 500ms for reset timer...");
            Thread.Sleep(510); // slightly over resetTimeoutMs to ensure transition
            _paymentServiceHealthy = true;

            // First call triggers OPEN → HALF-OPEN transition, then the probe runs
            string probe = cb.Execute(() => CallPaymentService(14));
            Console.WriteLine($"  Order 14 (probe): {probe}  [state: {cb.State}]");

            Console.WriteLine("\n  Circuit closed — normal traffic resumes:");
            for (int i = 15; i <= 17; i++)
            {
                string result = cb.Execute(() => CallPaymentService(i));
                Console.WriteLine($"  Order {i}: {result}  [state: {cb.State}]");
            }

            // =================================================================
            // Scenario 5 — Bad probe: reset timer expires but service still down
            // HALF-OPEN probe fails → circuit snaps back to OPEN immediately.
            // The reset timer restarts — service gets more time to recover.
            // This prevents premature reopening from crashing a still-sick service.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Probe fails — circuit snaps back to OPEN         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            // Force the circuit open by failing 3 times
            _paymentServiceHealthy = false;
            Console.WriteLine("\n  Service goes down again — tripping circuit:");
            for (int i = 18; i <= 20; i++)
            {
                try { cb.Execute<string>(() => CallPaymentService(i)); }
                catch { /* expected */ }
            }

            Console.WriteLine("\n  Waiting for reset timer, then probing (service still down):");
            Thread.Sleep(510);
            try
            {
                cb.Execute<string>(() => CallPaymentService(21));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Probe result: {ex.Message}  [state: {cb.State}]");
            }

            Console.WriteLine($"\n  Circuit state after failed probe: {cb.State}  ← back to OPEN");
        }
    }

} // namespace ResiliencePatterns
