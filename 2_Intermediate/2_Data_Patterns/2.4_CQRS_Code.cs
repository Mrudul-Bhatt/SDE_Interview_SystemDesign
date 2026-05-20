// Q5. Implement CQRS (Command Query Responsibility Segregation)
//
// Separate the write path (commands, normalized domain model) from the
// read path (queries, denormalized read models). Show projection-driven
// sync, multiple read models from one write event, eventual consistency,
// and read model rebuild from the write store.
//
// Architecture
// ─────────────
// Command → CommandHandler → WriteStore (normalized Order aggregate)
//                                 │
//                           emit OrderEvent
//                                 │
//                          ProjectionEngine
//                         ┌───────┴────────┐
//                         ▼                ▼
//               OrderSummaryView    UserDashboardView
//                    (per-order          (per-user
//                    flat view)         aggregation)
//                         │                │
//               Query ────┘────────────────┘ Query
//               (QueryHandler reads from read store — no joins, instant)
//
// Why this matters
// ─────────────────
// Write model optimised for correctness: normalized, validates business rules.
// Read models optimised for each query: denormalized, pre-aggregated, zero joins.
// The same data can power many different read shapes from one write.
//
// Complexity: Command O(1), Query O(1), Projection O(events)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DataPatterns
{
    // =========================================================================
    // Domain — Write Side
    // =========================================================================

    public enum OrderStatus { Pending, Confirmed, Shipped, Cancelled }

    public class OrderItem
    {
        public string  ProductName;
        public int     Quantity;
        public decimal UnitPrice;
    }

    // The write-side aggregate: normalized, rich domain model.
    // Only the CommandHandler touches this — never exposed to the query path.
    public class Order
    {
        public string      Id;
        public string      UserId;
        public List<OrderItem> Items = new List<OrderItem>();
        public OrderStatus Status;
        public DateTime    CreatedAt;

        public decimal Total => Items.Sum(i => i.Quantity * i.UnitPrice);
    }

    // =========================================================================
    // Commands — intent to change state
    // =========================================================================

    // Commands return nothing (or just an ID). They never return data —
    // that is the Query side's job.
    public class PlaceOrderCommand
    {
        public string          OrderId;
        public string          UserId;
        public List<OrderItem> Items;
    }

    public class UpdateOrderStatusCommand
    {
        public string      OrderId;
        public OrderStatus NewStatus;
    }

    // =========================================================================
    // Events — emitted after a write commits; consumed by projections
    // =========================================================================

    // Events carry the minimal data projections need — they are the contract
    // between the write side and the read side.
    public class OrderPlacedEvent
    {
        public string      OrderId;
        public string      UserId;
        public decimal     Total;
        public int         ItemCount;
        public DateTime    PlacedAt;
        public OrderStatus Status;
    }

    public class OrderStatusUpdatedEvent
    {
        public string      OrderId;
        public string      UserId;
        public OrderStatus OldStatus;
        public OrderStatus NewStatus;
        public DateTime    UpdatedAt;
    }

    // =========================================================================
    // Read models — shaped for each query, not for the domain
    // =========================================================================

    // Per-order flat view — no joins required at query time.
    // Fields are denormalized: ProductNames is pre-concatenated.
    public class OrderSummaryView
    {
        public string      OrderId;
        public string      UserId;
        public decimal     Total;
        public int         ItemCount;
        public string      ProductNames; // "Widget, Gadget, Doohickey"
        public OrderStatus Status;
        public DateTime    PlacedAt;

        public override string ToString() =>
            $"Order {OrderId} | {UserId} | ${Total:F2} | {ItemCount} items ({ProductNames}) | {Status} | {PlacedAt:HH:mm:ss}";
    }

    // Per-user aggregation — powers a user dashboard with totals across all orders.
    // Updated by the same projection that updates OrderSummaryView.
    public class UserDashboardView
    {
        public string  UserId;
        public int     TotalOrders;
        public decimal LifetimeSpend;
        public string  LastOrderId;
        public DateTime LastOrderAt;

        public override string ToString() =>
            $"User {UserId} | {TotalOrders} orders | ${LifetimeSpend:F2} lifetime | Last: {LastOrderId} @ {LastOrderAt:HH:mm:ss}";
    }

    // =========================================================================
    // Stores
    // =========================================================================

    // Write store: normalized, source of truth.
    // In production: a relational DB (Postgres/MySQL) with full ACID.
    public class WriteStore
    {
        private readonly Dictionary<string, Order> _orders = new Dictionary<string, Order>();

        public void Save(Order order)         => _orders[order.Id] = order;
        public Order Get(string orderId)      => _orders.TryGetValue(orderId, out Order o) ? o : null;
        public IReadOnlyCollection<Order> All => _orders.Values.ToList();
    }

    // Read store: two denormalized read models, each shaped for its query.
    // In production: could be Redis, Elasticsearch, Cassandra, or a read replica.
    public class ReadStore
    {
        private readonly Dictionary<string, OrderSummaryView>  _summaries  = new Dictionary<string, OrderSummaryView>();
        private readonly Dictionary<string, UserDashboardView> _dashboards = new Dictionary<string, UserDashboardView>();

        public void UpsertSummary(OrderSummaryView  v) => _summaries[v.OrderId] = v;
        public void UpsertDashboard(UserDashboardView v) => _dashboards[v.UserId]  = v;

        public OrderSummaryView  GetSummary(string orderId)   => _summaries.TryGetValue(orderId, out var v) ? v : null;
        public UserDashboardView GetDashboard(string userId)  => _dashboards.TryGetValue(userId, out var v) ? v : null;

        public void Clear()
        {
            _summaries.Clear();
            _dashboards.Clear();
        }
    }

    // =========================================================================
    // Projection Engine
    // =========================================================================
    // Subscribes to write-side events and updates both read models.
    // One event fan-outs to N projections — each read model is independently updated.
    //
    // Async mode: projection runs on a background thread with a configurable delay,
    // simulating real message-queue latency (Kafka, SQS) and demonstrating the
    // eventual consistency window between write commit and read model update.
    public class ProjectionEngine
    {
        private readonly WriteStore _writeStore;
        private readonly ReadStore  _readStore;
        private readonly int        _projectionDelayMs; // 0 = synchronous

        public ProjectionEngine(WriteStore writeStore, ReadStore readStore, int projectionDelayMs = 0)
        {
            _writeStore       = writeStore;
            _readStore        = readStore;
            _projectionDelayMs = projectionDelayMs;
        }

        public void OnOrderPlaced(OrderPlacedEvent ev)
        {
            void Project()
            {
                if (_projectionDelayMs > 0) Thread.Sleep(_projectionDelayMs);

                Order order = _writeStore.Get(ev.OrderId);
                if (order == null) return;

                // Projection 1: OrderSummaryView — flat per-order view
                _readStore.UpsertSummary(new OrderSummaryView
                {
                    OrderId      = ev.OrderId,
                    UserId       = ev.UserId,
                    Total        = ev.Total,
                    ItemCount    = ev.ItemCount,
                    ProductNames = string.Join(", ", order.Items.Select(i => i.ProductName)),
                    Status       = ev.Status,
                    PlacedAt     = ev.PlacedAt
                });

                // Projection 2: UserDashboardView — per-user aggregation
                UserDashboardView dash = _readStore.GetDashboard(ev.UserId)
                    ?? new UserDashboardView { UserId = ev.UserId };
                dash.TotalOrders++;
                dash.LifetimeSpend += ev.Total;
                dash.LastOrderId    = ev.OrderId;
                dash.LastOrderAt    = ev.PlacedAt;
                _readStore.UpsertDashboard(dash);

                Console.WriteLine($"    [Projection] OrderSummaryView + UserDashboardView updated for order {ev.OrderId}");
            }

            if (_projectionDelayMs > 0)
                new Thread(Project) { IsBackground = true }.Start(); // async fan-out
            else
                Project(); // synchronous
        }

        public void OnOrderStatusUpdated(OrderStatusUpdatedEvent ev)
        {
            void Project()
            {
                if (_projectionDelayMs > 0) Thread.Sleep(_projectionDelayMs);

                OrderSummaryView summary = _readStore.GetSummary(ev.OrderId);
                if (summary != null)
                {
                    summary.Status = ev.NewStatus;
                    _readStore.UpsertSummary(summary);
                    Console.WriteLine($"    [Projection] OrderSummaryView status updated: {ev.OldStatus} → {ev.NewStatus}");
                }
            }

            if (_projectionDelayMs > 0)
                new Thread(Project) { IsBackground = true }.Start();
            else
                Project();
        }

        // Rebuild: replay all orders from write store to recreate read models.
        // Used after a read model schema change or to recover from a corrupt read DB.
        public void RebuildFromWriteStore()
        {
            _readStore.Clear();
            foreach (Order order in _writeStore.All)
            {
                OnOrderPlaced(new OrderPlacedEvent
                {
                    OrderId   = order.Id,
                    UserId    = order.UserId,
                    Total     = order.Total,
                    ItemCount = order.Items.Count,
                    PlacedAt  = order.CreatedAt,
                    Status    = order.Status
                });
                Console.WriteLine($"    [Rebuild] Replayed order {order.Id}");
            }
        }
    }

    // =========================================================================
    // Command Handler — write side
    // =========================================================================
    // Validates the command, applies business rules, writes to WriteStore,
    // then emits events for the ProjectionEngine.
    // Returns nothing — commands are fire-and-update, not fire-and-fetch.
    public class CommandHandler
    {
        private readonly WriteStore       _writeStore;
        private readonly ProjectionEngine _projection;

        public CommandHandler(WriteStore writeStore, ProjectionEngine projection)
        {
            _writeStore = writeStore;
            _projection = projection;
        }

        public void Handle(PlaceOrderCommand cmd)
        {
            if (cmd.Items == null || cmd.Items.Count == 0)
                throw new ArgumentException("Order must contain at least one item.");

            var order = new Order
            {
                Id        = cmd.OrderId,
                UserId    = cmd.UserId,
                Items     = cmd.Items,
                Status    = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            _writeStore.Save(order); // write to normalized store

            // Emit event — projection updates read models independently.
            // Write side does not know about read model schema.
            _projection.OnOrderPlaced(new OrderPlacedEvent
            {
                OrderId   = order.Id,
                UserId    = order.UserId,
                Total     = order.Total,
                ItemCount = order.Items.Count,
                PlacedAt  = order.CreatedAt,
                Status    = order.Status
            });
        }

        public void Handle(UpdateOrderStatusCommand cmd)
        {
            Order order = _writeStore.Get(cmd.OrderId)
                ?? throw new KeyNotFoundException($"Order {cmd.OrderId} not found.");

            OrderStatus old = order.Status;
            order.Status = cmd.NewStatus;
            _writeStore.Save(order);

            _projection.OnOrderStatusUpdated(new OrderStatusUpdatedEvent
            {
                OrderId   = cmd.OrderId,
                UserId    = order.UserId,
                OldStatus = old,
                NewStatus = cmd.NewStatus,
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    // =========================================================================
    // Query Handler — read side
    // =========================================================================
    // Reads only from the ReadStore — never touches the WriteStore.
    // No joins, no aggregations at query time — all precomputed by projections.
    public class QueryHandler
    {
        private readonly ReadStore _readStore;
        public QueryHandler(ReadStore readStore) => _readStore = readStore;

        public OrderSummaryView  GetOrderSummary(string orderId) => _readStore.GetSummary(orderId);
        public UserDashboardView GetUserDashboard(string userId) => _readStore.GetDashboard(userId);
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public class Program
    {
        public static void Main()
        {
            var writeStore  = new WriteStore();
            var readStore   = new ReadStore();
            var projection  = new ProjectionEngine(writeStore, readStore, projectionDelayMs: 0);
            var commands    = new CommandHandler(writeStore, projection);
            var queries     = new QueryHandler(readStore);

            // =================================================================
            // Scenario 1 — Place an order: write model ≠ read model
            // Command writes a normalized Order aggregate to WriteStore.
            // Projection immediately fans out to two read models.
            // Query returns the pre-aggregated OrderSummaryView — zero joins.
            // Demonstrates: same data stored differently for write vs read.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Place order — write normalized, read flat        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            commands.Handle(new PlaceOrderCommand
            {
                OrderId = "order-201",
                UserId  = "user-A",
                Items   = new List<OrderItem>
                {
                    new OrderItem { ProductName = "Keyboard", Quantity = 1, UnitPrice = 79.99m },
                    new OrderItem { ProductName = "Mouse",    Quantity = 2, UnitPrice = 29.99m }
                }
            });

            Console.WriteLine("\n  Write model (normalized):");
            Order w1 = writeStore.Get("order-201");
            foreach (OrderItem item in w1.Items)
                Console.WriteLine($"    {item.ProductName} x{item.Quantity} @ ${item.UnitPrice}");
            Console.WriteLine($"    Total (computed): ${w1.Total:F2}");

            Console.WriteLine("\n  Read model (denormalized — pre-aggregated, no joins):");
            Console.WriteLine($"    {queries.GetOrderSummary("order-201")}");

            // =================================================================
            // Scenario 2 — Multiple read models from one write event
            // A second order for the same user triggers both read model projections.
            // OrderSummaryView is updated for the new order.
            // UserDashboardView accumulates the lifetime spend across both orders.
            // One write event → two read models shaped differently.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Two orders — one write event → two read models   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            commands.Handle(new PlaceOrderCommand
            {
                OrderId = "order-202",
                UserId  = "user-A",
                Items   = new List<OrderItem>
                {
                    new OrderItem { ProductName = "Monitor", Quantity = 1, UnitPrice = 249.99m }
                }
            });

            Console.WriteLine("\n  OrderSummaryView for order-202:");
            Console.WriteLine($"    {queries.GetOrderSummary("order-202")}");
            Console.WriteLine("\n  UserDashboardView for user-A (aggregates both orders):");
            Console.WriteLine($"    {queries.GetUserDashboard("user-A")}");

            // =================================================================
            // Scenario 3 — Status update: command fires, projection updates read model
            // UpdateOrderStatusCommand mutates the write-side Order aggregate.
            // Projection updates only the Status field in OrderSummaryView.
            // Query sees the new status without touching the write store.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Status update — write changes, read model synced  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            Console.WriteLine($"\n  Before: {queries.GetOrderSummary("order-201")}");

            commands.Handle(new UpdateOrderStatusCommand
            {
                OrderId   = "order-201",
                NewStatus = OrderStatus.Shipped
            });

            Console.WriteLine($"  After:  {queries.GetOrderSummary("order-201")}");

            // =================================================================
            // Scenario 4 — Eventual consistency: async projection delay
            // A new ProjectionEngine with 150ms delay simulates real message-queue
            // latency (Kafka/SQS). The query immediately after the command returns
            // null — the read model is not yet updated (stale window).
            // After the projection completes, the query returns the full view.
            // This is the core trade-off: fast writes + eventual reads.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Eventual consistency — stale read during lag      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var asyncReadStore  = new ReadStore();
            var asyncProjection = new ProjectionEngine(writeStore, asyncReadStore, projectionDelayMs: 150);
            var asyncCommands   = new CommandHandler(writeStore, asyncProjection);
            var asyncQueries    = new QueryHandler(asyncReadStore);

            asyncCommands.Handle(new PlaceOrderCommand
            {
                OrderId = "order-203",
                UserId  = "user-B",
                Items   = new List<OrderItem>
                {
                    new OrderItem { ProductName = "Webcam", Quantity = 1, UnitPrice = 59.99m }
                }
            });

            // Query immediately — projection not done yet (stale window)
            OrderSummaryView immediate = asyncQueries.GetOrderSummary("order-203");
            Console.WriteLine($"\n  Query immediately after command : {(immediate == null ? "null (read model not yet updated)" : immediate.ToString())}");
            Console.WriteLine("  Waiting 200ms for async projection to complete...");
            Thread.Sleep(200);

            OrderSummaryView afterLag = asyncQueries.GetOrderSummary("order-203");
            Console.WriteLine($"  Query after projection lag      : {afterLag}");

            // =================================================================
            // Scenario 5 — Read model rebuild from write store
            // Simulate a scenario where the read store is corrupted or wiped
            // (e.g. Redis flushed, schema migration). The projection engine
            // replays all orders from the write store to reconstruct both read
            // models from scratch. This is only possible because the write store
            // remains the source of truth — read models are always derived.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 5: Read model rebuild — replay from write store      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            Console.WriteLine($"\n  Read store before wipe — order-201: {queries.GetOrderSummary("order-201")}");

            readStore.Clear();
            Console.WriteLine("  Read store wiped (simulating Redis flush / schema migration).");
            Console.WriteLine($"  Query after wipe — order-201: {(queries.GetOrderSummary("order-201") == null ? "null (read model gone)" : "")}");

            Console.WriteLine("\n  Rebuilding read models by replaying write store...");
            projection.RebuildFromWriteStore();

            Console.WriteLine($"\n  After rebuild — order-201: {queries.GetOrderSummary("order-201")}");
            Console.WriteLine($"  After rebuild — user-A dashboard: {queries.GetUserDashboard("user-A")}");
            Console.WriteLine("\n  Read models fully restored from write store ✓");
        }
    }

} // namespace DataPatterns
