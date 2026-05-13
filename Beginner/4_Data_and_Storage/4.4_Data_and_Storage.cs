// Q1. Implement Indexed vs Non-Indexed Table Lookup
// Show the performance difference between a full table scan (no index) and a B-Tree
// index lookup.  A SortedDictionary in C# is a balanced BST — the same structure
// real databases use for B-Tree indexes.

// Q2. Implement a Write-Ahead Log (WAL) with Commit and Rollback
// Simulate ACID Atomicity and Durability.  All changes are written to a log first.
// On commit, staged changes are applied.  On rollback (or crash), the log is replayed
// in reverse to undo any partial changes — exactly how PostgreSQL/MySQL InnoDB work.

// Q3. Implement an In-Memory Message Queue with Dead Letter Queue (DLQ)
// Producers push messages; consumers process them.  Failed messages are retried up to
// N times, then moved to a Dead Letter Queue for manual inspection and replay.

// Q4. Implement an In-Memory Document Store
// Model a MongoDB-like store where documents are schema-free dictionaries.
// Supports insert, find-by-field, flexible update, and delete.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq; // Enumerable.Select used in DocumentStore.PrintAll

// ---------------------------------------------------------------------------
// Q1 — Table without an index (full table scan)
// ---------------------------------------------------------------------------
public class TableWithoutIndex
{
    // All rows in insertion order — the only way to find one is to read every element.
    // This mirrors a database heap file: no auxiliary structure, O(n) to locate a row.
    private readonly List<Dictionary<string, object>> _rows = new();

    // O(1) insert — just appends to the list, no extra bookkeeping required.
    public void Insert(Dictionary<string, object> row) => _rows.Add(row);

    // O(n) lookup — must examine every row to find matches.
    // At 500k rows this is ~500k comparisons; at 10M rows it is 10M comparisons.
    public List<Dictionary<string, object>> FindByEmail(string email)
    {
        var results = new List<Dictionary<string, object>>();
        foreach (var row in _rows)
            // TryGetValue avoids KeyNotFoundException when a row lacks the "email" column.
            if (row.TryGetValue("email", out var val) && val.Equals(email))
                results.Add(row);
        return results;
    }

    public int RowCount => _rows.Count;
}

// ---------------------------------------------------------------------------
// Q1 — Table with a B-Tree index on the email column
// ---------------------------------------------------------------------------
public class TableWithIndex
{
    // Primary storage: row data in insertion order (identical to the unindexed table).
    private readonly List<Dictionary<string, object>> _rows = new();

    // Secondary index: email value -> list of row positions in _rows.
    // SortedDictionary is a Red-Black BST (balanced), giving O(log n) lookup —
    // the same complexity as a real database B-Tree index.
    // WHY List<int> not just int: multiple rows can share the same email value
    // (e.g. before a UNIQUE constraint is enforced).
    private readonly SortedDictionary<string, List<int>> _emailIndex = new();

    // O(log n) insert — appends the row then updates the BST index.
    // Trade-off: writes pay a small extra cost now so reads are fast later.
    public void Insert(Dictionary<string, object> row)
    {
        // rowId = length before append = 0-based position of the new row.
        int rowId = _rows.Count;
        _rows.Add(row);

        // Update the email index.  'is string emailStr' both null-checks and casts.
        if (row.TryGetValue("email", out var email) && email is string emailStr)
        {
            if (!_emailIndex.ContainsKey(emailStr))
                _emailIndex[emailStr] = new List<int>();

            // Store the position so FindByEmail can jump straight to it.
            _emailIndex[emailStr].Add(rowId);
        }
    }

    // O(log n) lookup — one BST traversal (log₂ 500k ≈ 19 steps) instead of 500k comparisons.
    public List<Dictionary<string, object>> FindByEmail(string email)
    {
        // No index entry means zero matching rows — return immediately without scanning.
        if (!_emailIndex.TryGetValue(email, out var rowIds))
            return new List<Dictionary<string, object>>();

        var results = new List<Dictionary<string, object>>();
        // rowIds are direct positions in _rows — O(1) random access per match.
        foreach (var id in rowIds)
            results.Add(_rows[id]);
        return results;
    }

    public int RowCount => _rows.Count;
}

// ---------------------------------------------------------------------------
// Q2 — Write-Ahead Log (WAL) database
// ---------------------------------------------------------------------------
public class WALDatabase
{
    // The durable, committed view of the database.
    // In a real DB this lives on disk; here it is an in-memory dictionary.
    private readonly Dictionary<string, decimal> _committed = new();

    // The WAL log: each entry records operation, key, old value, and new value.
    // WHY store oldVal: rollback replays in reverse and restores oldVal,
    // undoing every change without ever touching _committed.
    private readonly List<(string Op, string Key, decimal OldVal, decimal NewVal)> _log = new();

    // Enforces single active transaction — two concurrent transactions would corrupt the log.
    private bool _inTransaction = false;

    // Staging area: changes live here until Commit().
    // If we crash or call Rollback(), _staged is simply discarded — _committed is untouched.
    private readonly Dictionary<string, decimal> _staged = new();

    // Opens a new transaction.  Clears both log and staging so prior state cannot leak in.
    public void BeginTransaction()
    {
        if (_inTransaction) throw new InvalidOperationException("Transaction already in progress");
        _inTransaction = true;
        _staged.Clear();
        _log.Clear();
        Console.WriteLine("[WAL] BEGIN TRANSACTION");
    }

    // Records a SET in the WAL log BEFORE staging the change.
    // "Write-Ahead" = log entry written first — if we crash between log and stage,
    // the log lets recovery UNDO the partial operation.
    public void Set(string key, decimal value)
    {
        if (!_inTransaction) throw new InvalidOperationException("No active transaction");

        // Capture the pre-change value so Rollback knows what to restore.
        decimal oldVal = _committed.GetValueOrDefault(key, 0);

        // 1. Log first (would be flushed to disk in a real WAL).
        _log.Add(("SET", key, oldVal, value));

        // 2. Stage — _committed is untouched until Commit().
        _staged[key] = value;
        Console.WriteLine($"[WAL] LOG: SET {key} = {value}  (was {oldVal})");
    }

    // Atomically copies all staged changes into _committed.
    // After this point changes are visible to readers and survive a restart.
    public void Commit()
    {
        if (!_inTransaction) throw new InvalidOperationException("No active transaction");

        foreach (var (key, value) in _staged)
            _committed[key] = value;

        _log.Clear();
        _staged.Clear();
        _inTransaction = false;
        Console.WriteLine("[WAL] COMMIT — changes applied to committed state");
    }

    // Discards staged changes by traversing the log in reverse.
    // _committed is never modified, so the DB is left in its pre-transaction state.
    public void Rollback()
    {
        if (!_inTransaction) throw new InvalidOperationException("No active transaction");

        Console.WriteLine("[WAL] ROLLBACK — undoing changes:");
        // Tail-to-head traversal: each undo reverses exactly one forward operation.
        for (int i = _log.Count - 1; i >= 0; i--)
        {
            var (op, key, oldVal, _) = _log[i];
            Console.WriteLine($"  Undo {op} {key}: restoring to {oldVal}");
        }

        // _staged thrown away — _committed was never touched, already correct.
        _log.Clear();
        _staged.Clear();
        _inTransaction = false;
    }

    // "Read your own writes": returns the staged value inside a transaction,
    // or the last committed value outside one.
    public decimal Get(string key) =>
        _inTransaction && _staged.TryGetValue(key, out var staged)
            ? staged
            : _committed.GetValueOrDefault(key, 0);

    public void PrintState()
    {
        Console.WriteLine("  Committed state:");
        foreach (var (k, v) in _committed)
            Console.WriteLine($"    {k} = {v:C}");
    }
}

// ---------------------------------------------------------------------------
// Q3 — Message Queue with retry and Dead Letter Queue (DLQ)
// ---------------------------------------------------------------------------
public class MessageQueue<T>
{
    // Internal envelope: wraps the caller's payload with delivery metadata.
    private class QueueMessage
    {
        public T Payload { get; init; } = default!; // the actual data (e.g. "order:1001")
        public int AttemptCount { get; set; } = 0;        // how many times processing was tried
        // Short unique ID for logging — first 8 chars of a GUID is readable and collision-unlikely.
        public string MessageId { get; init; } = Guid.NewGuid().ToString()[..8];
    }

    // Main queue: messages waiting for a consumer to pick them up.
    private readonly Queue<QueueMessage> _main = new();

    // Dead Letter Queue: messages that exhausted all retry attempts.
    // Kept separate so they don't block healthy messages in the main queue.
    private readonly Queue<QueueMessage> _dlq = new();

    // Maximum delivery attempts before giving up and moving to DLQ.
    private readonly int _maxAttempts;

    // Expose counts so callers can poll without draining the queues.
    public int MainCount => _main.Count;
    public int DLQCount => _dlq.Count;

    // maxAttempts — how many times to retry a failing message before routing to DLQ.
    public MessageQueue(int maxAttempts = 3)
    {
        _maxAttempts = maxAttempts;
    }

    // Producer API: wraps payload in an envelope and enqueues it.
    public void Enqueue(T payload)
    {
        var msg = new QueueMessage { Payload = payload };
        _main.Enqueue(msg);
        Console.WriteLine($"[ENQUEUE] id={msg.MessageId}  queue size={_main.Count}");
    }

    // Consumer API: dequeues one message and invokes the handler.
    // handler returns true (ACK = success) or false (NACK = failure, retry).
    // Returns false if the queue is empty (nothing to process).
    public bool Process(Func<T, bool> handler)
    {
        if (_main.Count == 0) return false;

        // Dequeue removes the message — if we crash here before ACK, the message is lost.
        // At-least-once systems solve this with visibility timeouts (SQS) or ack IDs (RabbitMQ).
        var msg = _main.Dequeue();
        msg.AttemptCount++;

        Console.Write($"[PROCESS] id={msg.MessageId}  attempt={msg.AttemptCount}/{_maxAttempts}  ");

        bool success = handler(msg.Payload);

        if (success)
        {
            // Message acknowledged — processing complete, discard the envelope.
            Console.WriteLine("-> SUCCESS (acknowledged)");
        }
        else if (msg.AttemptCount < _maxAttempts)
        {
            // NACK + retries remaining: re-enqueue at the back of the main queue.
            // "At-least-once" delivery — the consumer may see this message again.
            Console.WriteLine("-> FAILED  (requeued for retry)");
            _main.Enqueue(msg);
        }
        else
        {
            // Exhausted all retries: move to DLQ instead of discarding silently.
            // DLQ lets engineers inspect the payload, find the bug, then replay.
            Console.WriteLine("-> FAILED  (max retries exceeded -> DLQ)");
            _dlq.Enqueue(msg);
        }

        return success;
    }

    // Prints every message currently in the DLQ without consuming them.
    public void InspectDLQ()
    {
        Console.WriteLine($"\n[DLQ] {_dlq.Count} unprocessable message(s):");
        foreach (var msg in _dlq)
            Console.WriteLine($"  id={msg.MessageId}  attempts={msg.AttemptCount}  payload={msg.Payload}");
    }

    // Moves all DLQ messages back to the main queue after the underlying bug is fixed.
    // Resets AttemptCount so the message gets a full set of fresh retry opportunities.
    public int ReplayDLQ()
    {
        int count = _dlq.Count;
        while (_dlq.Count > 0)
        {
            var msg = _dlq.Dequeue();
            msg.AttemptCount = 0; // fresh start — don't carry over prior failures
            _main.Enqueue(msg);
        }
        Console.WriteLine($"[REPLAY] {count} message(s) moved from DLQ -> main queue");
        return count;
    }
}

// ---------------------------------------------------------------------------
// Q4 — In-Memory Document Store (MongoDB-like)
// ---------------------------------------------------------------------------
public class DocumentStore
{
    // Maps auto-generated _id -> document (a free-form key-value dictionary).
    // WHY Dictionary<string, Dictionary<string, object>>: documents are schema-free;
    // any field can be any type, and different documents can have different fields.
    private readonly Dictionary<string, Dictionary<string, object>> _store = new();

    // Monotonic counter for generating deterministic, human-readable document IDs.
    private int _nextId = 1;

    // Inserts a document, stamps it with an auto-generated _id, and returns that id.
    // The stored copy is a shallow clone so the caller's dictionary cannot mutate the store.
    public string Insert(Dictionary<string, object> doc)
    {
        string id = $"doc_{_nextId++}";
        doc["_id"] = id; // stamp the id into the caller's dict too (MongoDB convention)
        _store[id] = new Dictionary<string, object>(doc); // shallow copy
        Console.WriteLine($"[INSERT] id={id}");
        return id;
    }

    // O(1) lookup by primary key.
    // Returns null if the id does not exist (no KeyNotFoundException).
    public Dictionary<string, object> FindById(string id) =>
        _store.TryGetValue(id, out var doc) ? doc : null;

    // O(n) scan: walks all documents and returns those where field == value.
    // In MongoDB this maps to db.collection.find({ field: value }).
    // Add a secondary index (like Q1's SortedDictionary) to make this O(log n).
    public List<Dictionary<string, object>> FindByField(string field, object value)
    {
        var results = new List<Dictionary<string, object>>();
        foreach (var doc in _store.Values)
            if (doc.TryGetValue(field, out var val) && val.Equals(value))
                results.Add(doc);
        return results;
    }

    // Schema-free update: merges the supplied fields into an existing document.
    // Unlike SQL ALTER TABLE, this adds new fields to only the targeted document —
    // other documents are untouched and don't need a migration.
    public bool Update(string id, Dictionary<string, object> fields)
    {
        if (!_store.TryGetValue(id, out var doc)) return false;
        foreach (var (key, value) in fields)
            doc[key] = value; // overwrite if key exists, add if not
        Console.WriteLine($"[UPDATE] id={id}  fields=[{string.Join(", ", fields.Keys)}]");
        return true;
    }

    // Removes the document by id.  Returns false if it did not exist.
    public bool Delete(string id)
    {
        bool removed = _store.Remove(id);
        if (removed) Console.WriteLine($"[DELETE] id={id}");
        return removed;
    }

    // Debug helper: prints every document as a flat key: value line.
    // Enumerable.Select projects each KeyValuePair to "key: value" strings.
    public void PrintAll()
    {
        Console.WriteLine($"\n[STORE] {_store.Count} document(s):");
        foreach (var (id, doc) in _store)
        {
            string fields = string.Join(", ", doc.Select(kv => $"{kv.Key}: {kv.Value}"));
            Console.WriteLine($"  {id}: {{ {fields} }}");
        }
    }
}

// ---------------------------------------------------------------------------
// Entry point — demos for all four questions
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        // ===================================================================
        // Q1 DEMO — Index vs No-Index benchmark
        // ===================================================================
        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║  Q1: Indexed vs Non-Indexed Table Lookup ║");
        Console.WriteLine("╚══════════════════════════════════════════╝\n");

        var noIndex = new TableWithoutIndex();
        var withIndex = new TableWithIndex();

        Console.WriteLine("Seeding 500,000 rows...");
        for (int i = 0; i < 500_000; i++)
        {
            var row = new Dictionary<string, object>
            {
                ["id"] = i,
                ["name"] = $"User{i}",
                ["email"] = $"user{i}@example.com"
            };
            noIndex.Insert(row);
            withIndex.Insert(row);
        }

        // Last row = worst case for a sequential scan (must read all 500k rows first).
        string target = "user499999@example.com";

        var sw = Stopwatch.StartNew();
        var r1 = noIndex.FindByEmail(target);
        sw.Stop();
        Console.WriteLine($"Without index: {sw.ElapsedMilliseconds}ms  (found {r1.Count} row)");

        sw.Restart();
        var r2 = withIndex.FindByEmail(target);
        sw.Stop();
        Console.WriteLine($"With index:    {sw.ElapsedMilliseconds}ms  (found {r2.Count} row)");

        Console.WriteLine("\n--- Complexity summary ---");
        Console.WriteLine("  Insert  : O(1) without index  |  O(log n) with index (BST update)");
        Console.WriteLine("  Lookup  : O(n) without index  |  O(log n) with index (BST traversal)");
        Console.WriteLine("  500k rows: scan ~500k comparisons  vs  index ~19 (log2 500k)");
        Console.WriteLine("  Rule of thumb: read-heavy -> add indexes; write-heavy -> fewer indexes");

        // ===================================================================
        // Q2 DEMO — Write-Ahead Log
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════════╗");
        Console.WriteLine("║  Q2: Write-Ahead Log (WAL)               ║");
        Console.WriteLine("╚══════════════════════════════════════════╝\n");

        var db = new WALDatabase();

        db.BeginTransaction();
        db.Set("alice", 1000m);
        db.Set("bob", 500m);
        db.Commit();

        Console.WriteLine("\n=== Before transfer ===");
        db.PrintState(); // alice=$1000  bob=$500

        Console.WriteLine("\n=== Transfer $300: Alice -> Bob ===");
        db.BeginTransaction();
        db.Set("alice", db.Get("alice") - 300m); // 1000 -> 700
        db.Set("bob", db.Get("bob") + 300m); // 500  -> 800
        db.Commit();
        db.PrintState(); // alice=$700  bob=$800

        Console.WriteLine("\n=== Transfer $400: Alice -> Bob (FAILS mid-way) ===");
        db.BeginTransaction();
        db.Set("alice", db.Get("alice") - 400m); // logged but not committed
        Console.WriteLine("  [CRASH] Power failure before Bob's credit — rolling back...");
        db.Rollback();

        Console.WriteLine("\n=== After rollback ===");
        db.PrintState(); // alice=$700  bob=$800 — atomicity preserved

        Console.WriteLine("\n--- How a real WAL works (PostgreSQL / MySQL InnoDB / SQLite) ---");
        Console.WriteLine("  1. BEGIN     : open transaction");
        Console.WriteLine("  2. LOG       : write each change to WAL file on disk (durable)");
        Console.WriteLine("  3. COMMIT    : flush WAL, apply to data pages, mark complete in log");
        Console.WriteLine("  Crash recovery: REDO committed txns | UNDO incomplete txns");
        Console.WriteLine("  Complexity  : Commit O(n), Rollback O(n) — n = ops in transaction");

        // ===================================================================
        // Q3 DEMO — Message Queue with DLQ
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Message Queue with Dead Letter Queue ║");
        Console.WriteLine("╚══════════════════════════════════════════╝\n");

        // maxAttempts=3 — each message gets up to 3 delivery attempts before DLQ.
        var queue = new MessageQueue<string>(maxAttempts: 3);

        queue.Enqueue("order:1001");
        queue.Enqueue("order:1002");
        queue.Enqueue("order:1003"); // this one will always fail (simulated bug)
        Console.WriteLine();

        // Process until the main queue is empty.
        // order:1001 and order:1002 succeed; order:1003 fails 3x -> DLQ.
        while (queue.MainCount > 0)
        {
            queue.Process(payload =>
            {
                if (payload == "order:1003") return false; // simulated bug
                return true;
            });
        }

        queue.InspectDLQ();

        // Simulate: engineer fixes the bug, replays DLQ.
        Console.WriteLine("\n[FIX] Bug fixed. Replaying DLQ...");
        queue.ReplayDLQ();
        queue.Process(_ => true); // now succeeds

        Console.WriteLine("\n--- Delivery semantics ---");
        Console.WriteLine("  At-least-once (this impl) : message retried until ACK; may deliver twice");
        Console.WriteLine("  Exactly-once              : requires dedup ID + idempotent consumer");
        Console.WriteLine("  Idempotent pattern        : INSERT OR IGNORE INTO processed (id); skip if 0 rows");
        Console.WriteLine("  Used by: SQS, RabbitMQ (at-least-once) / Kafka transactions (exactly-once)");

        // ===================================================================
        // Q4 DEMO — Document Store
        // ===================================================================
        Console.WriteLine("\n╔══════════════════════════════════════════╗");
        Console.WriteLine("║  Q4: In-Memory Document Store            ║");
        Console.WriteLine("╚══════════════════════════════════════════╝\n");

        var docStore = new DocumentStore();

        string id1 = docStore.Insert(new Dictionary<string, object>
        {
            ["name"] = "Alice",
            ["email"] = "alice@example.com",
            ["role"] = "admin"
        });

        string id2 = docStore.Insert(new Dictionary<string, object>
        {
            ["name"] = "Bob",
            ["email"] = "bob@example.com",
            ["role"] = "user"
        });

        string id3 = docStore.Insert(new Dictionary<string, object>
        {
            ["name"] = "Charlie",
            ["email"] = "charlie@example.com",
            ["role"] = "user"
        });

        // Schema-free update: add fields to one document without migrating others.
        // In SQL this would require ALTER TABLE + backfill (minutes on large tables).
        docStore.Update(id1, new Dictionary<string, object>
        {
            ["preferences"] = "{ theme: dark, language: en }",
            ["last_login"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
        });

        // Insert a completely different document shape — no schema violation.
        docStore.Insert(new Dictionary<string, object>
        {
            ["name"] = "ServiceAccount",
            ["api_key"] = "sk-1234",
            ["rate_limit"] = 1000
        });

        docStore.PrintAll();

        // O(n) field scan: find all documents where role == "user".
        Console.WriteLine("\n[FIND] role = 'user':");
        var users = docStore.FindByField("role", "user");
        foreach (var u in users)
            Console.WriteLine($"  {u["name"]} ({u["email"]})");

        Console.WriteLine("\n--- SQL vs Document Store trade-offs ---");
        Console.WriteLine("  SQL wins    : JOINs across tables, schema enforces data integrity, complex aggregations");
        Console.WriteLine("  Docs win    : rapidly evolving schema, rows with different shapes, nested data");
        Console.WriteLine("  SQL schema change  : ALTER TABLE users ADD COLUMN last_login DATETIME");
        Console.WriteLine("    -> may lock table for minutes; all existing rows get NULL");
        Console.WriteLine("  Doc field add      : Update(id, new { last_login = ... })");
        Console.WriteLine("    -> only that document changes; others untouched, no migration");
    }
}
