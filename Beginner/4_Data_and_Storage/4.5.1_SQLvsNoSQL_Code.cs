// ─── 4.5  SQL vs NoSQL — Core Tradeoffs, Simulated in C# ───────────────────
//
// Scenario throughout: AXS ticketing platform
//   Tracks users, their music preferences, and concert seat bookings.
//
// Three demos, each exposing a real architectural pain point:
//
//   Demo 1 — Cross-document query
//             NoSQL forces an O(n) scan of every document.
//             SQL maintains a composite index so the same query is O(result).
//             Feel why SQL exists the moment you need "find users by city AND genre."
//
//   Demo 2 — Concurrent seat booking (ACID vs no-ACID)
//             Without a transaction, two users both get a booking confirmation
//             for the same seat. With a transaction, exactly one wins.
//             Feel why ACID matters the moment money is involved.
//
//   Demo 3 — Schema evolution
//             Adding a new field in SQL requires a migration that rewrites
//             every row and stalls writes. In NoSQL you just include the field
//             in new documents — old documents are unaffected.
//             Feel why NoSQL exists the moment requirements change fast.
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// =============================================================================
// PROGRAM — entry point, runs all three demos in sequence
// =============================================================================

class Program
{
    static void Main()
    {
        Demo1_CrossDocumentQuery.Run();
        Demo2_ConcurrentBooking.Run();
        Demo3_SchemaEvolution.Run();
        PrintSummary();
    }

    static void PrintSummary()
    {
        Separator("SUMMARY — The decision framework");

        Console.WriteLine("Choose SQL when:");
        Console.WriteLine("  - Queries join multiple entities (users + bookings + events)");
        Console.WriteLine("  - Money or inventory is involved — ACID prevents double-booking");
        Console.WriteLine("  - Schema is stable — enforcement catches bugs at insert time");
        Console.WriteLine("  AXS uses: PostgreSQL for events, seats, bookings, payments");
        Console.WriteLine();
        Console.WriteLine("Choose NoSQL when:");
        Console.WriteLine("  - Every read is a single-key fetch (user profile, session lookup)");
        Console.WriteLine("  - Data shape varies per record (product catalog: laptop vs t-shirt)");
        Console.WriteLine("  - Schema changes rapidly — new fields ship without migrations");
        Console.WriteLine("  - Write volume exceeds one SQL node (Cassandra: 1M writes/sec)");
        Console.WriteLine("  AXS uses: Redis (seat locks, sessions) + Elasticsearch (event search)");
        Console.WriteLine();
        Console.WriteLine("Real systems use both — SQL is the transactional source of truth,");
        Console.WriteLine("NoSQL handles caching, search, analytics, and anywhere eventual");
        Console.WriteLine("consistency is acceptable.");
        Console.WriteLine();
    }

    // Shared display helpers used by all demos
    public static void Separator(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(new string('─', 68));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('─', 68));
        Console.ResetColor();
    }

    public static void Label(string text)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

// =============================================================================
// DEMO 1 — Cross-document query: NoSQL O(n) scan vs SQL indexed JOIN
// =============================================================================
//
// Pain point: "Find all users in Mumbai who prefer rock music."
//   NoSQL  → must scan every document (no cross-document index).
//   SQL    → composite index on (city, genre) jumps straight to results.
//
// Both stores are seeded with 100,000 users so the time difference is visible.

static class Demo1_CrossDocumentQuery
{
    // ── NoSQL: document store ─────────────────────────────────────────────────
    //
    // Accepts any shape — no schema, no enforcement. Flexibility is the point.
    // The cost surfaces the moment you ask a cross-document question:
    // there is no index that covers city + genre, so every document is opened.

    class DocumentStore
    {
        readonly List<Dictionary<string, object>> _docs = new List<Dictionary<string, object>>();

        public void Insert(Dictionary<string, object> doc) => _docs.Add(doc);

        // O(n) — must inspect every document regardless of result size.
        public List<Dictionary<string, object>> FindMumbaiRockFans()
        {
            var results = new List<Dictionary<string, object>>();
            foreach (var doc in _docs)
            {
                bool inMumbai = doc.TryGetValue("city", out var c) && c.Equals("Mumbai");
                bool likesRock = doc.TryGetValue("genre", out var g) && g.Equals("rock");
                if (inMumbai && likesRock)
                    results.Add(doc);
            }
            return results;
        }

        public int Count => _docs.Count;
    }

    // ── SQL: normalized tables with composite index ───────────────────────────
    //
    // Two tables: users (id, name, email, city) + preferences (user_id, genre).
    // Composite index on (city, genre) is built during every InsertPreference.
    // This is the write overhead SQL pays — so reads later are O(result size).
    //
    // Simulates:
    //   CREATE INDEX idx_city_genre ON user_preferences(city, genre)

    record User(int Id, string Name, string Email, string City);
    record Preference(int UserId, string Genre);

    class RelationalStore
    {
        readonly Dictionary<int, User> _users = new Dictionary<int, User>();
        readonly List<Preference> _preferences = new List<Preference>();

        // city → genre → [userId, userId, ...]
        readonly Dictionary<string, Dictionary<string, List<int>>> _cityGenreIndex =
            new Dictionary<string, Dictionary<string, List<int>>>();

        public void InsertUser(User user) => _users[user.Id] = user;

        public void InsertPreference(Preference pref)
        {
            _preferences.Add(pref);

            // Index update on every write — this is the SQL write overhead.
            if (!_users.TryGetValue(pref.UserId, out var user)) return;

            if (!_cityGenreIndex.ContainsKey(user.City))
                _cityGenreIndex[user.City] = new Dictionary<string, List<int>>();
            if (!_cityGenreIndex[user.City].ContainsKey(pref.Genre))
                _cityGenreIndex[user.City][pref.Genre] = new List<int>();

            _cityGenreIndex[user.City][pref.Genre].Add(pref.UserId);
        }

        // Simulates:
        //   SELECT u.* FROM users u
        //   JOIN preferences p ON u.id = p.user_id
        //   WHERE u.city = 'Mumbai' AND p.genre = 'rock'
        //
        // O(result size) — index skips every row that can't match.
        public List<User> FindByCityAndGenre(string city, string genre)
        {
            if (!_cityGenreIndex.TryGetValue(city, out var byGenre)) return new List<User>();
            if (!byGenre.TryGetValue(genre, out var ids)) return new List<User>();
            return ids.Select(id => _users[id]).ToList();
        }

        public int UserCount => _users.Count;
    }

    // ── Demo runner ───────────────────────────────────────────────────────────

    public static void Run()
    {
        Program.Separator("DEMO 1 — Cross-document query: NoSQL O(n) scan vs SQL indexed JOIN");

        var nosqlStore = new DocumentStore();
        var sqlStore = new RelationalStore();
        var rng = new Random(42);

        string[] cities = { "Mumbai", "Delhi", "Bangalore", "Chennai", "Hyderabad" };
        string[] genres = { "rock", "pop", "classical", "jazz", "electronic" };

        int totalUsers = 100_000;
        Console.WriteLine($"Seeding {totalUsers:N0} users into both stores...");

        for (int i = 0; i < totalUsers; i++)
        {
            string city = cities[rng.Next(cities.Length)];
            string genre = genres[rng.Next(genres.Length)];

            nosqlStore.Insert(new Dictionary<string, object>
            {
                ["id"] = i,
                ["name"] = $"User{i}",
                ["email"] = $"user{i}@axs.com",
                ["city"] = city,
                ["genre"] = genre
            });

            sqlStore.InsertUser(new User(i, $"User{i}", $"user{i}@axs.com", city));
            sqlStore.InsertPreference(new Preference(i, genre));
        }

        Console.WriteLine("Done.\n");
        Program.Label("Query: \"Find all users in Mumbai who prefer rock\" (drives event recommendations)");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        var nosqlResult = nosqlStore.FindMumbaiRockFans();
        sw.Stop();

        Console.WriteLine($"  NoSQL — full document scan:");
        Console.WriteLine($"    Scanned : {nosqlStore.Count:N0} documents (every single one)");
        Console.WriteLine($"    Found   : {nosqlResult.Count:N0} matching users");
        Console.WriteLine($"    Time    : {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"    Why slow: no index — must open and inspect each document");
        Console.WriteLine();

        sw.Restart();
        var sqlResult = sqlStore.FindByCityAndGenre("Mumbai", "rock");
        sw.Stop();

        Console.WriteLine($"  SQL — composite index lookup (city + genre):");
        Console.WriteLine($"    Scanned : 0 non-matching rows — index skipped them");
        Console.WriteLine($"    Found   : {sqlResult.Count:N0} matching users");
        Console.WriteLine($"    Time    : {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"    Why fast: index maps (Mumbai, rock) directly to matching IDs");
        Console.WriteLine();
        Console.WriteLine($"  Tradeoff: SQL paid index-maintenance cost on every insert.");
        Console.WriteLine($"  Payoff  : every future query on (city, genre) is O(result size).");
    }
}

// =============================================================================
// DEMO 2 — Concurrent booking: no-ACID double-booking vs ACID exactness
// =============================================================================
//
// Pain point: 50 users simultaneously click "Book Seat A5" during a flash sale.
//   No-ACID → multiple users receive a booking confirmation for the same seat.
//   ACID    → exactly one user wins, all others receive a clean "sold out."

static class Demo2_ConcurrentBooking
{
    // ── Naive booking: no atomicity between read and write ────────────────────
    //
    // Represents any system where "check availability" and "create booking" are
    // two separate, non-atomic operations — a common pattern in document stores.
    //
    // Race condition:
    //   Thread A: reads seat → free  →  [gap]  →  writes booked  →  confirmed
    //   Thread B: reads seat → free  →  [gap]  →  writes booked  →  confirmed
    //   Both return true. Both users get a confirmation email. One seat. Two owners.
    //
    // Thread.Sleep simulates real-world latency in the gap (payment API, fraud check).
    // In production this gap can be seconds, making the race nearly guaranteed.

    class NaiveBookingStore
    {
        volatile bool _seatTaken = false;
        readonly List<string> _confirmed = new List<string>();
        readonly object _listLock = new object(); // guards list only, not the seat check

        public bool TryBook(string userId)
        {
            if (_seatTaken) return false;

            // THE GAP: other threads read false here before anyone writes true.
            Thread.Sleep(5); // simulates: payment validation, fraud check, network call

            _seatTaken = true;                          // not atomic with the read above
            lock (_listLock) _confirmed.Add(userId);   // every thread that passed the read lands here
            return true;
        }

        public IReadOnlyList<string> Confirmed => _confirmed;
    }

    // ── ACID booking: atomic read + write inside a transaction lock ───────────
    //
    // Simulates:
    //   BEGIN;
    //   SELECT status FROM seats WHERE id = 'A5' FOR UPDATE;  ← row lock acquired
    //   UPDATE seats SET status = 'booked' WHERE id = 'A5';
    //   COMMIT;
    //
    // The lock makes read + write one indivisible unit. No other thread can
    // interleave between them. Exactly one thread wins; all others get a clean
    // rejection with no partial or corrupted state.

    class AcidBookingStore
    {
        bool _seatTaken = false;
        readonly object _txLock = new object(); // simulates FOR UPDATE row lock
        readonly List<string> _confirmed = new List<string>();
        public int Rejected = 0;

        public bool TryBook(string userId)
        {
            lock (_txLock) // BEGIN TRANSACTION — only one thread enters at a time
            {
                if (_seatTaken)
                {
                    Rejected++;
                    return false; // ROLLBACK — clean rejection
                }

                Thread.Sleep(1); // payment validation still happens, but atomically inside the lock

                _seatTaken = true;
                _confirmed.Add(userId);
                return true; // COMMIT
            }
        }

        public IReadOnlyList<string> Confirmed => _confirmed;
    }

    // ── Demo runner ───────────────────────────────────────────────────────────

    public static void Run()
    {
        Program.Separator("DEMO 2 — Concurrent booking: no-ACID double-booking vs ACID exactness");

        int threadCount = 50;
        Console.WriteLine($"Scenario: Taylor Swift flash sale. {threadCount} users click \"Book Seat A5\" simultaneously.");
        Console.WriteLine();

        RunNaive(threadCount);
        RunAcid(threadCount);
    }

    static void RunNaive(int threadCount)
    {
        Program.Label("WITHOUT transactions (read and write are two separate, non-atomic operations):");

        var store = new NaiveBookingStore();
        var tasks = Enumerable.Range(1, threadCount)
                              .Select(i => Task.Run(() => store.TryBook($"User-{i}")))
                              .ToArray();
        Task.WaitAll(tasks);

        Console.WriteLine($"  Confirmed bookings for Seat A5: {store.Confirmed.Count}");

        if (store.Confirmed.Count > 1)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  !! DOUBLE BOOKING — {store.Confirmed.Count} users received a confirmation:");
            Console.ResetColor();
            foreach (var u in store.Confirmed)
                Console.WriteLine($"     {u}  <- sent booking email, charged card");
            Console.WriteLine($"  The seat map shows one owner. The rest are ghost bookings.");
        }
        else
        {
            Console.WriteLine("  (Race did not fire this run — race conditions are non-deterministic.)");
        }

        Console.WriteLine();
    }

    static void RunAcid(int threadCount)
    {
        Program.Label("WITH transactions (FOR UPDATE lock — read and write are one atomic unit):");

        var store = new AcidBookingStore();
        var tasks = Enumerable.Range(1, threadCount)
                              .Select(i => Task.Run(() => store.TryBook($"User-{i}")))
                              .ToArray();
        Task.WaitAll(tasks);

        Console.WriteLine($"  Confirmed bookings for Seat A5: {store.Confirmed.Count}");
        Console.WriteLine($"  Cleanly rejected               : {store.Rejected}");

        if (store.Confirmed.Count == 1)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  OK {store.Confirmed[0]} has the seat. {store.Rejected} others got a clean \"sold out\" response.");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("  Why it works: the lock prevents any thread from reading the seat");
        Console.WriteLine("  state until the previous transaction has fully committed or rolled back.");
        Console.WriteLine("  There is no gap between \"check\" and \"book\" — they are one operation.");
    }
}

// =============================================================================
// DEMO 3 — Schema evolution: SQL migration cost vs NoSQL zero-cost addition
// =============================================================================
//
// Pain point: a new feature requires adding two fields to an existing table.
//   SQL    → ALTER TABLE rewrites every row; holds an exclusive lock on large tables.
//   NoSQL  → just include the new fields in new documents; old documents untouched.

static class Demo3_SchemaEvolution
{
    // ── SQL table: rigid schema, migration required ───────────────────────────
    //
    // Every row has identical columns. New columns need ALTER TABLE:
    //   - Rewrites every existing row to add the new column slot.
    //   - Holds ACCESS EXCLUSIVE lock (blocks all reads + writes) on older Postgres.
    //   - On 50M rows: ~8 minutes, needs a maintenance window.
    //
    // Modern tools (pg_repack, gh-ost) reduce impact, but the constraint
    // remains: schema must be agreed on before data is written.

    class SqlUserTable
    {
        record UserRow(int Id, string Name, string Email,
                       bool? VipAccess = null, string LoungeTier = null);

        readonly List<UserRow> _rows = new List<UserRow>();
        bool _migrated = false;

        public void RunMigration()
        {
            Console.WriteLine("  [SQL] ALTER TABLE users ADD COLUMN vip_lounge_access BOOLEAN;");
            Console.WriteLine("  [SQL] ALTER TABLE users ADD COLUMN lounge_tier VARCHAR(20);");
            Console.WriteLine("  [SQL] Rewriting all existing rows to add the new column slot...");
            Console.WriteLine("  [SQL] On a 50M-row table: ~8 min of reduced write throughput.");
            _migrated = true;
        }

        public void Insert(int id, string name, string email,
                           bool? vipAccess = null, string tier = null)
        {
            if (!_migrated && (vipAccess != null || tier != null))
                throw new InvalidOperationException(
                    "Column 'vip_lounge_access' does not exist. Run RunMigration() first.");
            _rows.Add(new UserRow(id, name, email, vipAccess, tier));
        }

        public int VipCount => _rows.Count(r => r.VipAccess == true);
    }

    // ── NoSQL collection: flexible schema, new fields cost nothing ────────────
    //
    // Each document is a dictionary — new fields are just new keys.
    // Old documents coexist without the field; the query filters by key presence.
    // No migration, no downtime, no locking. New fields ship immediately.
    //
    // Hidden cost: no enforcement. A typo in a key name or a missing required
    // field is silently accepted. Bugs that SQL catches at INSERT time reach
    // production and corrupt query results instead.

    class NoSqlCollection
    {
        readonly List<Dictionary<string, object>> _docs = new List<Dictionary<string, object>>();

        public void Insert(Dictionary<string, object> doc) => _docs.Add(doc);

        public int VipCount =>
            _docs.Count(d => d.ContainsKey("vip_lounge_access") && (bool)d["vip_lounge_access"]);
    }

    // ── Demo runner ───────────────────────────────────────────────────────────

    public static void Run()
    {
        Program.Separator("DEMO 3 — Schema evolution: SQL migration cost vs NoSQL zero-cost addition");

        Console.WriteLine("Feature request: add VIP lounge access (two new fields) for premium users.");
        Console.WriteLine("5 users already exist in the system.");
        Console.WriteLine();

        RunSqlPath();
        RunNoSqlPath();
    }

    static void RunSqlPath()
    {
        Program.Label("SQL path:");

        var table = new SqlUserTable();
        for (int i = 1; i <= 5; i++)
            table.Insert(i, $"User{i}", $"u{i}@axs.com");

        Console.WriteLine("  5 existing users inserted into SQL table.");
        Console.WriteLine("  Now adding the VIP feature. First: schema migration...\n");

        table.RunMigration();

        table.Insert(6, "VIP-Alice", "alice@axs.com", vipAccess: true, tier: "Gold");
        table.Insert(7, "VIP-Bob", "bob@axs.com", vipAccess: true, tier: "Platinum");

        Console.WriteLine($"\n  New VIP users inserted after migration. VIP count: {table.VipCount}");
        Console.WriteLine();
    }

    static void RunNoSqlPath()
    {
        Program.Label("NoSQL path:");

        var collection = new NoSqlCollection();
        for (int i = 1; i <= 5; i++)
            collection.Insert(new Dictionary<string, object> { ["id"] = i, ["name"] = $"User{i}" });

        Console.WriteLine("  5 existing users inserted into NoSQL collection.");
        Console.WriteLine("  Now adding the VIP feature. No migration needed.\n");

        Console.WriteLine("  [NoSQL] Adding 'vip_lounge_access' field to new documents only.");
        Console.WriteLine("  [NoSQL] Old documents are untouched — no rewrite, no downtime.");

        collection.Insert(new Dictionary<string, object>
        { ["id"] = 6, ["name"] = "VIP-Alice", ["vip_lounge_access"] = true, ["lounge_tier"] = "Gold" });
        collection.Insert(new Dictionary<string, object>
        { ["id"] = 7, ["name"] = "VIP-Bob", ["vip_lounge_access"] = true, ["lounge_tier"] = "Platinum" });

        Console.WriteLine($"\n  VIP users inserted. VIP count: {collection.VipCount}  (deployed in seconds, not minutes)");
        Console.WriteLine($"  Hidden cost: old docs missing 'vip_lounge_access' are silently valid.");
        Console.WriteLine($"  A typo like 'Vip_Lounge_Access' is also silently valid. No enforcement.");
    }
}
