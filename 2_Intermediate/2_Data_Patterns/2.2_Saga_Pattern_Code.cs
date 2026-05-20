// Q4. Implement an Orchestration-Based Saga
//
// Simulate a multi-step order placement across three independent services
// (Inventory, Payment, Shipping). Show happy path, failure at each step
// with compensating transactions in reverse order, transient retry, and
// idempotent step execution.
//
// Saga execution model
// ─────────────────────
// Orchestrator drives every step:
//   T1 (reserve inventory) → T2 (charge payment) → T3 (schedule shipping)
//
// Failure at step Ti → compensate in reverse: C(i-1) … C1
//   Failure at T2 (payment): C1 = release inventory
//   Failure at T3 (shipping): C2 = refund payment, C1 = release inventory
//
// Key properties demonstrated
// ────────────────────────────
// Compensating tx  → a new forward tx that semantically undoes a committed step
// Idempotent steps → replay with same saga+step key returns stored result, no re-execution
// Retry            → transient failures retried before triggering compensation
// Durable state    → saga state persisted to store before each step; survives orchestrator crash
//
// Complexity: Execute O(steps), Compensate O(steps)

using System;
using System.Collections.Generic;
using System.Threading;

namespace DataPatterns
{
    // -------------------------------------------------------------------------
    // SagaStatus
    // -------------------------------------------------------------------------
    public enum SagaStatus
    {
        Started,
        InventoryReserved,
        PaymentCharged,
        ShippingScheduled,
        Confirmed,    // terminal — success
        Compensating, // rolling back completed steps
        Cancelled     // terminal — fully compensated
    }

    // -------------------------------------------------------------------------
    // OrderSagaState
    // -------------------------------------------------------------------------
    // Persisted to a durable store (simulated as an in-memory dictionary here).
    // In production this lives in a DB row so the orchestrator can resume after crash.
    public class OrderSagaState
    {
        public string     SagaId;
        public string     OrderId;
        public string     UserId;
        public decimal    Amount;
        public string     ItemId;
        public SagaStatus Status;

        // IDs returned by each step — needed for compensation.
        // Stored durably so compensation works even after orchestrator restarts.
        public string ReservationId; // from InventoryService.Reserve
        public string ChargeId;      // from PaymentService.Charge
        public string ShipmentId;    // from ShippingService.Schedule

        public List<string> Log = new List<string>();
    }

    // -------------------------------------------------------------------------
    // InventoryService
    // -------------------------------------------------------------------------
    public class InventoryService
    {
        // Transient failures: fail this many times before succeeding (0 = always ok).
        public int TransientFailuresRemaining = 0;

        // Idempotency store: sagaId+step → reservationId
        private readonly Dictionary<string, string> _idempotencyStore
            = new Dictionary<string, string>();
        private          int _counter = 0;

        public string Reserve(string itemId, string sagaKey)
        {
            // Idempotent: same saga+step returns existing result without re-reserving.
            if (_idempotencyStore.TryGetValue(sagaKey, out string existing))
            {
                Console.WriteLine($"    [Inventory] Reserve replay → returning cached {existing}");
                return existing;
            }

            if (TransientFailuresRemaining > 0)
            {
                TransientFailuresRemaining--;
                throw new Exception($"Inventory: connection timeout (transient, {TransientFailuresRemaining} retries left)");
            }

            int n = Interlocked.Increment(ref _counter);
            string id = $"res_{n:D4}";
            _idempotencyStore[sagaKey] = id;
            return id;
        }

        // Compensation: release a previously created reservation.
        // Always succeeds in this simulation — real systems must handle failure here too
        // (dead-letter queue / manual ops if compensation itself fails).
        public void Release(string reservationId)
        {
            Console.WriteLine($"    [Inventory] Released reservation {reservationId}");
        }
    }

    // -------------------------------------------------------------------------
    // PaymentService
    // -------------------------------------------------------------------------
    public class PaymentService
    {
        public bool ShouldFail = false;

        private readonly Dictionary<string, string> _idempotencyStore
            = new Dictionary<string, string>();
        private int _counter = 0;

        public string Charge(decimal amount, string userId, string sagaKey)
        {
            if (_idempotencyStore.TryGetValue(sagaKey, out string existing))
            {
                Console.WriteLine($"    [Payment]   Charge replay → returning cached {existing}");
                return existing;
            }

            if (ShouldFail)
                throw new Exception("Payment: card declined (insufficient funds)");

            int n = Interlocked.Increment(ref _counter);
            string id = $"ch_{n:D4}";
            _idempotencyStore[sagaKey] = id;
            return id;
        }

        // Compensation: refund a committed charge.
        public void Refund(string chargeId)
        {
            Console.WriteLine($"    [Payment]   Refunded charge {chargeId}");
        }
    }

    // -------------------------------------------------------------------------
    // ShippingService
    // -------------------------------------------------------------------------
    public class ShippingService
    {
        public bool ShouldFail = false;

        private readonly Dictionary<string, string> _idempotencyStore
            = new Dictionary<string, string>();
        private int _counter = 0;

        public string Schedule(string orderId, string sagaKey)
        {
            if (_idempotencyStore.TryGetValue(sagaKey, out string existing))
            {
                Console.WriteLine($"    [Shipping]  Schedule replay → returning cached {existing}");
                return existing;
            }

            if (ShouldFail)
                throw new Exception("Shipping: no carriers available in region");

            int n = Interlocked.Increment(ref _counter);
            string id = $"ship_{n:D4}";
            _idempotencyStore[sagaKey] = id;
            return id;
        }

        public void Cancel(string shipmentId)
        {
            Console.WriteLine($"    [Shipping]  Cancelled shipment {shipmentId}");
        }
    }

    // -------------------------------------------------------------------------
    // SagaOrchestrator
    // -------------------------------------------------------------------------
    // Drives the saga step-by-step. Persists state before each step so that
    // on crash and restart the saga can be resumed from where it left off.
    //
    // Retry policy: each step retried up to maxRetries times for transient errors
    // before the saga begins compensation.
    public class SagaOrchestrator
    {
        private readonly InventoryService _inventory;
        private readonly PaymentService   _payment;
        private readonly ShippingService  _shipping;

        // Durable saga store — in production: a DB table with saga_id as PK.
        private readonly Dictionary<string, OrderSagaState> _sagaStore
            = new Dictionary<string, OrderSagaState>();

        private readonly int _maxRetries;

        public SagaOrchestrator(
            InventoryService inventory,
            PaymentService   payment,
            ShippingService  shipping,
            int              maxRetries = 2)
        {
            _inventory  = inventory;
            _payment    = payment;
            _shipping   = shipping;
            _maxRetries = maxRetries;
        }

        public OrderSagaState Execute(
            string orderId, string userId, string itemId, decimal amount)
        {
            string sagaId = Guid.NewGuid().ToString()[..8];

            // Persist initial state before doing anything — orchestrator can resume here on crash.
            var state = new OrderSagaState
            {
                SagaId  = sagaId,
                OrderId = orderId,
                UserId  = userId,
                Amount  = amount,
                ItemId  = itemId,
                Status  = SagaStatus.Started
            };
            _sagaStore[sagaId] = state;
            state.Log.Add($"  [{sagaId}] Saga STARTED for order {orderId}");

            // ── Step 1: Reserve inventory ─────────────────────────────────────
            // sagaKey scopes idempotency to this exact saga + step so retries
            // and replays return the same result without creating duplicate reservations.
            string step1Key = $"{sagaId}:reserve-inventory";
            if (!TryExecuteStep(state, "Step 1 — Reserve inventory",
                () => state.ReservationId = _inventory.Reserve(itemId, step1Key)))
            {
                // First step failed — nothing was committed, no compensation needed.
                state.Status = SagaStatus.Cancelled;
                state.Log.Add("  No compensation needed (Step 1 was the first commit).");
                return state;
            }
            state.Status = SagaStatus.InventoryReserved;
            state.Log.Add($"  Step 1 ✓  ReservationId = {state.ReservationId}");

            // ── Step 2: Charge payment ────────────────────────────────────────
            string step2Key = $"{sagaId}:charge-payment";
            if (!TryExecuteStep(state, "Step 2 — Charge payment",
                () => state.ChargeId = _payment.Charge(amount, userId, step2Key)))
            {
                Compensate(state, compensateFromStep: 1);
                return state;
            }
            state.Status = SagaStatus.PaymentCharged;
            state.Log.Add($"  Step 2 ✓  ChargeId = {state.ChargeId}");

            // ── Step 3: Schedule shipping ─────────────────────────────────────
            string step3Key = $"{sagaId}:schedule-shipping";
            if (!TryExecuteStep(state, "Step 3 — Schedule shipping",
                () => state.ShipmentId = _shipping.Schedule(orderId, step3Key)))
            {
                Compensate(state, compensateFromStep: 2);
                return state;
            }
            state.Status = SagaStatus.ShippingScheduled;
            state.Log.Add($"  Step 3 ✓  ShipmentId = {state.ShipmentId}");

            // ── All steps succeeded ───────────────────────────────────────────
            state.Status = SagaStatus.Confirmed;
            state.Log.Add($"  ✓ Saga CONFIRMED — order {orderId} is complete");
            return state;
        }

        // Executes a step with retry. Returns true on success, false after all retries exhausted.
        private bool TryExecuteStep(OrderSagaState state, string stepName, Action step)
        {
            for (int attempt = 1; attempt <= _maxRetries + 1; attempt++)
            {
                try
                {
                    step();
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt <= _maxRetries)
                    {
                        state.Log.Add($"  {stepName}: transient failure (attempt {attempt}) — {ex.Message}. Retrying...");
                        Thread.Sleep(20); // back-off (simplified; production uses exponential back-off)
                    }
                    else
                    {
                        state.Log.Add($"  {stepName}: FAILED after {attempt} attempts — {ex.Message}");
                        return false;
                    }
                }
            }
            return false;
        }

        // Compensate in reverse order from compensateFromStep down to step 1.
        // Each compensation is a new forward transaction — not a DB rollback.
        private void Compensate(OrderSagaState state, int compensateFromStep)
        {
            state.Status = SagaStatus.Compensating;
            state.Log.Add($"  → Compensation starting (reversing from step {compensateFromStep})...");

            // C2: refund payment (only if payment was charged)
            if (compensateFromStep >= 2 && state.ChargeId != null)
            {
                _payment.Refund(state.ChargeId);
                state.Log.Add($"  C2 ✓  Refunded payment {state.ChargeId}");
            }

            // C1: release inventory reservation (only if inventory was reserved)
            if (compensateFromStep >= 1 && state.ReservationId != null)
            {
                _inventory.Release(state.ReservationId);
                state.Log.Add($"  C1 ✓  Released reservation {state.ReservationId}");
            }

            state.Status = SagaStatus.Cancelled;
            state.Log.Add("  Saga CANCELLED — fully compensated");
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        private static void PrintSaga(OrderSagaState s)
        {
            Console.WriteLine($"\n  Final status: {s.Status}");
            Console.WriteLine("  Execution log:");
            foreach (string line in s.Log)
                Console.WriteLine($"    {line}");
        }

        public static void Main()
        {
            var inventory = new InventoryService();
            var payment   = new PaymentService();
            var shipping  = new ShippingService();
            var saga      = new SagaOrchestrator(inventory, payment, shipping, maxRetries: 2);

            // =================================================================
            // Scenario 1 — Happy path: all steps succeed → CONFIRMED
            // Shows the full saga flow: T1 → T2 → T3 → CONFIRMED.
            // Each step returns an ID that is stored on the saga state for
            // potential compensation.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Happy path — all steps succeed → CONFIRMED       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            OrderSagaState s1 = saga.Execute("order-101", "user-1", "item-SKU-A", 79.99m);
            PrintSaga(s1);

            // =================================================================
            // Scenario 2 — Payment failure: inventory reserved, payment declines
            // T1 succeeds (reservation created). T2 fails (card declined).
            // Compensation: C1 = release reservation. No refund needed (never charged).
            // Demonstrates the critical property: compensation runs in reverse order,
            // only for steps that already committed.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Payment fails → C1 release inventory             ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            payment.ShouldFail = true;
            OrderSagaState s2 = saga.Execute("order-102", "user-2", "item-SKU-B", 49.99m);
            PrintSaga(s2);
            payment.ShouldFail = false;

            // =================================================================
            // Scenario 3 — Shipping failure: inventory + payment committed, shipping fails
            // T1 and T2 succeed. T3 fails (no carrier).
            // Compensation in reverse: C2 = refund payment, C1 = release reservation.
            // Both compensating transactions must succeed before saga is CANCELLED.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Shipping fails → C2 refund + C1 release          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            shipping.ShouldFail = true;
            OrderSagaState s3 = saga.Execute("order-103", "user-3", "item-SKU-C", 129.00m);
            PrintSaga(s3);
            shipping.ShouldFail = false;

            // =================================================================
            // Scenario 4 — Transient retry: inventory service blips twice, then recovers
            // The orchestrator retries up to maxRetries=2 times before giving up.
            // First two attempts throw (simulating connection timeout).
            // Third attempt (within retry budget) succeeds → saga continues normally.
            // Without retry, every transient network hiccup would trigger compensation.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Transient retry — inventory blips, recovers       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            inventory.TransientFailuresRemaining = 2; // fails twice, succeeds on 3rd attempt
            OrderSagaState s4 = saga.Execute("order-104", "user-4", "item-SKU-D", 19.99m);
            PrintSaga(s4);

            // =================================================================
            // Scenario 5 — Idempotent step replay: same sagaId+step key replayed
            // Simulates orchestrator crash-and-resume: step 1 was executed and its
            // result stored under the idempotency key. On resume, the orchestrator
            // calls Reserve again with the same key — inventory service returns the
            // stored reservationId without creating a duplicate reservation.
            // This is essential: without idempotency, crash-and-resume = double reservation.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Idempotent step replay — no duplicate reservation ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            // Manually pre-seed the inventory idempotency store to simulate a
            // saga that already completed Step 1 before a crash.
            string fixedSagaId = "saga-9999";
            string step1IdKey  = $"{fixedSagaId}:reserve-inventory";

            // Directly register the key as if Step 1 already ran.
            // (In production this happens automatically during the first Execute call.)
            var inventoryForReplay = new InventoryService();
            // Force the idempotency store via a first call that we treat as "already ran":
            inventoryForReplay.Reserve("item-SKU-E", step1IdKey); // first call — executes
            string firstResId = inventoryForReplay
                .Reserve("item-SKU-E", step1IdKey); // second call — replay

            Console.WriteLine($"\n  First Reserve  : {inventoryForReplay.Reserve("item-SKU-E", step1IdKey)} ← same ID (idempotent)");
            Console.WriteLine($"  Replay Reserve : {firstResId}  ← same ID (no new reservation created)");
            Console.WriteLine("  → Orchestrator can safely retry Step 1 after crash without double-reserving.");
        }
    }

} // namespace DataPatterns
