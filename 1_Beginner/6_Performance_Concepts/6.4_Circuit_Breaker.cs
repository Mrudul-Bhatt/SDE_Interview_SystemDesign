// Q1. Implement a Circuit Breaker
// Wrap a remote service call with a circuit breaker. Track failures and open the
// circuit when a threshold is exceeded. After a timeout, probe once (half-open).
// If the probe succeeds, close the circuit. If it fails, stay open.
//
// The Three States:
//   CLOSED    → requests flow through, failures are counted
//   OPEN      → requests fail immediately (no call made), service gets recovery time
//   HALF-OPEN → one probe request is allowed through to test if service recovered
//
// State transitions:
//   CLOSED    → failures >= threshold  → OPEN
//   OPEN      → timeout elapsed        → HALF-OPEN
//   HALF-OPEN → probe succeeds         → CLOSED
//   HALF-OPEN → probe fails            → OPEN (reset timer)
//
// Why circuit breaker beats unlimited retries:
//   Without it: Service B is down 30s → Service A retries every 500ms → 60 storms
//               → B's thread pool exhausted → cascading failure up the call chain
//   With it:    After 3 failures → OPEN → zero traffic sent to B → B recovers quietly
//               → HALF-OPEN probe → one test call → CLOSED → normal traffic resumes
//
// Complexity: Execute O(1) — all state transitions are O(1)

using System;

namespace PerformanceConcepts
{

    // ---------------------------------------------------------------------------
    // CircuitBreaker — wraps any Func<T> with open/close/half-open protection
    // ---------------------------------------------------------------------------
    public class CircuitBreaker
    {
        // Enum as a named state machine — far clearer than magic integers or booleans.
        // Three distinct states mean three distinct behaviors in Execute(); adding a
        // fourth state later (e.g. "Forced Open") only requires adding one enum value.
        public enum State { Closed, Open, HalfOpen }

        private State _state = State.Closed;

        // Counts consecutive failures in the CLOSED state.
        // Reset to 0 on any success so intermittent errors don't accumulate unfairly.
        private int _failureCount = 0;

        // Records the exact moment the circuit opened.
        // Used in the OPEN → HALF-OPEN transition: we compare UtcNow against this
        // to know when the recovery timeout has elapsed.
        private DateTime _openedAt;

        private readonly int _failureThreshold;
        private readonly TimeSpan _recoveryTimeout;

        // Expose current state for monitoring dashboards and unit tests
        // without allowing external code to mutate it (read-only property).
        public State CurrentState => _state;

        public CircuitBreaker(int failureThreshold = 3, int recoveryTimeoutSeconds = 10)
        {
            _failureThreshold = failureThreshold;

            // Store as TimeSpan internally so comparisons use DateTime arithmetic,
            // not raw integer seconds. Avoids off-by-one errors and unit confusion.
            _recoveryTimeout = TimeSpan.FromSeconds(recoveryTimeoutSeconds);
        }

        // Generic so one CircuitBreaker instance can wrap any return type —
        // string for HTTP calls, int for DB queries, bool for health checks, etc.
        // fallback is optional: callers that have a safe degraded response pass one;
        // callers that have no fallback let the exception propagate (fail loudly).
        public T Execute<T>(Func<T> action, Func<T>? fallback = null)
        {
            switch (_state)
            {
                case State.Open:
                    // Check if enough time has passed for the service to recover.
                    // We don't use a background timer because that would require a
                    // separate thread — checking inline on each call is simpler and
                    // equally correct (the first call after the timeout triggers the probe).
                    if (DateTime.UtcNow - _openedAt >= _recoveryTimeout)
                    {
                        TransitionTo(State.HalfOpen);
                        return TryExecute(action, fallback, isProbe: true);
                    }

                    // Timeout not yet elapsed — reject immediately without touching the
                    // downstream service. This is the core benefit: no network calls,
                    // no thread blocking, instant response to caller.
                    Console.WriteLine("  [CB] OPEN — failing fast (no call made)");
                    return UseFallback(fallback);

                case State.HalfOpen:
                    // Only one probe is allowed through. If this call fails, we go back
                    // to OPEN. If it succeeds, we go to CLOSED and open the floodgates.
                    // isProbe=true tells OnFailure to trip immediately (don't wait for
                    // failureThreshold — one probe failure is enough to stay OPEN).
                    return TryExecute(action, fallback, isProbe: true);

                default: // Closed — normal operation
                    return TryExecute(action, fallback, isProbe: false);
            }
        }

        private T TryExecute<T>(Func<T> action, Func<T>? fallback, bool isProbe)
        {
            try
            {
                T result = action();
                OnSuccess(isProbe);
                return result;
            }
            catch (Exception ex)
            {
                OnFailure(isProbe, ex.Message);
                return UseFallback(fallback);
            }
        }

        private void OnSuccess(bool isProbe)
        {
            // Any success resets the failure streak — a service that recovers after 2
            // failures should start fresh, not carry those 2 failures into the next window.
            _failureCount = 0;

            // If we were in HALF-OPEN, the probe succeeded → service is healthy → CLOSED.
            // If we were already CLOSED, this is a no-op.
            if (_state != State.Closed)
                TransitionTo(State.Closed);
        }

        private void OnFailure(bool isProbe, string reason)
        {
            _failureCount++;
            Console.WriteLine($"  [CB] Failure #{_failureCount}: {reason}");

            // Two ways to trip to OPEN:
            //   1. A HALF-OPEN probe failed — one failure is enough, service still sick.
            //   2. Cumulative failures in CLOSED state hit the threshold.
            // Both reset _openedAt so the recovery timeout starts from NOW.
            if (_state == State.HalfOpen || _failureCount >= _failureThreshold)
            {
                _openedAt = DateTime.UtcNow;
                TransitionTo(State.Open);
            }
        }

        private void TransitionTo(State newState)
        {
            Console.WriteLine($"  [CB] {_state} → {newState}");
            _state = newState;
        }

        private static T UseFallback<T>(Func<T>? fallback)
        {
            // If no fallback is provided, throw — the caller opted into hard failure
            // (useful for non-critical paths where a degraded response is unacceptable).
            if (fallback != null) return fallback();
            throw new InvalidOperationException("Circuit open — service unavailable, no fallback provided");
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
            Console.WriteLine("║  Circuit Breaker Demo                ║");
            Console.WriteLine("╚══════════════════════════════════════╝");

            var cb = new CircuitBreaker(failureThreshold: 3, recoveryTimeoutSeconds: 2);

            // Simulates a payment service that fails on the first 5 calls, then recovers.
            // callCount is captured by the closures below — no class needed.
            int callCount = 0;
            string CallPaymentService()
            {
                callCount++;
                if (callCount <= 5) throw new Exception("Connection timeout");
                return "Payment processed";
            }

            // Fallback is a safe degraded response — queue the payment for async retry
            // rather than dropping it entirely or returning an error to the user.
            string Fallback() => "Payment queued for retry";

            Console.WriteLine("\n=== Calls 1-3: failures accumulate (CLOSED) ===");
            for (int i = 0; i < 3; i++)
                Console.WriteLine($"  Result: {cb.Execute(CallPaymentService, Fallback)}");
            // 3rd failure hits threshold → CLOSED → OPEN

            Console.WriteLine($"\n=== Calls 4-5: circuit OPEN, failing fast ===");
            for (int i = 0; i < 2; i++)
                Console.WriteLine($"  Result: {cb.Execute(CallPaymentService, Fallback)}");
            // No network calls made — instant fallback response

            Console.WriteLine($"\n=== Waiting {2.1:F1}s for recovery timeout... ===");
            Thread.Sleep(2100);

            Console.WriteLine($"\n=== Call 6: HALF-OPEN probe (service still failing) ===");
            Console.WriteLine($"  Result: {cb.Execute(CallPaymentService, Fallback)}");
            // callCount=6, still ≤ 5 → fails → HALF-OPEN → OPEN again

            Console.WriteLine($"\n=== Call 7: HALF-OPEN probe (service recovered) ===");
            Console.WriteLine($"  Result: {cb.Execute(CallPaymentService, Fallback)}");
            // callCount=7, > 5 → succeeds → HALF-OPEN → CLOSED

            Console.WriteLine($"\n=== Call 8: back to CLOSED — normal traffic resumes ===");
            Console.WriteLine($"  Result: {cb.Execute(CallPaymentService, Fallback)}");

            Console.WriteLine($"\nFinal state: {cb.CurrentState}");
        }
    }

} // namespace PerformanceConcepts
