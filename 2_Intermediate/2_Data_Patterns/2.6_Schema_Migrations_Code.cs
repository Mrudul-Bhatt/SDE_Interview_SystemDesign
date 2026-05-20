// Q6. Implement a Schema Migration Engine
//
// Simulate versioned migration files, a migration runner that applies them
// exactly once, checksum tamper detection, the Expand-Contract pattern for
// zero-downtime column rename, batched backfill, and forward-only rollback.
//
// Core mechanism
// ───────────────
// Each migration has a version number, description, and Up() logic.
// A schema_migrations tracking table records which versions were applied.
// The runner applies only pending (unapplied) migrations in order.
// Checksums detect accidental edits to previously applied migrations.
//
// Key patterns demonstrated
// ──────────────────────────
// Expand-Contract  → 3-phase zero-downtime column rename (V004/V005/V006)
// Batched backfill → UPDATE in small batches with sleep between each
// Dual-write repo  → application layer writes to BOTH columns during Phase 1–2
// Forward rollback → "undo" is a new V007 migration, not a destructive rollback
//
// Complexity: Migrate O(migrations), Backfill O(rows/batchSize * batchSize)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DataPatterns
{
    // =========================================================================
    // In-memory database simulation
    // =========================================================================

    public class ColumnDef
    {
        public string Name;
        public string Type;
        public bool   IsNullable;

        public override string ToString() =>
            $"{Name} {Type}{(IsNullable ? "" : " NOT NULL")}";
    }

    // Simulates one DB table: schema (columns + indexes) + data rows.
    // Rows are Dictionary<columnName, value> to allow dynamic schema changes.
    public class SimulatedTable
    {
        public string Name;
        public Dictionary<string, ColumnDef>         Schema  = new Dictionary<string, ColumnDef>();
        public List<Dictionary<string, object>>       Rows    = new List<Dictionary<string, object>>();
        public List<string>                           Indexes = new List<string>();

        public void AddColumn(string name, string type, bool nullable = true)
        {
            Schema[name] = new ColumnDef { Name = name, Type = type, IsNullable = nullable };
            // Backfill existing rows with null for the new column
            foreach (var row in Rows) row[name] = null;
        }

        public bool HasColumn(string name) => Schema.ContainsKey(name);

        public void DropColumn(string name)
        {
            Schema.Remove(name);
            foreach (var row in Rows) row.Remove(name);
        }

        public void AddIndex(string indexName, string column) =>
            Indexes.Add($"{indexName} ON {Name}({column})");

        public void PrintSchema()
        {
            Console.WriteLine($"  Table: {Name}");
            foreach (ColumnDef col in Schema.Values)
                Console.WriteLine($"    {col}");
            foreach (string idx in Indexes)
                Console.WriteLine($"    INDEX {idx}");
        }
    }

    // Wraps all tables + the schema_migrations tracking table.
    public class SimulatedDatabase
    {
        public Dictionary<string, SimulatedTable> Tables
            = new Dictionary<string, SimulatedTable>();

        // Mirrors the real schema_migrations table that Flyway/Liquibase manage.
        public List<MigrationRecord> AppliedMigrations = new List<MigrationRecord>();

        public SimulatedTable GetOrCreate(string tableName)
        {
            if (!Tables.ContainsKey(tableName))
                Tables[tableName] = new SimulatedTable { Name = tableName };
            return Tables[tableName];
        }

        public SimulatedTable Get(string tableName) =>
            Tables.TryGetValue(tableName, out SimulatedTable t) ? t : null;
    }

    // =========================================================================
    // Migration infrastructure
    // =========================================================================

    public class MigrationRecord
    {
        public int      Version;
        public string   Description;
        public string   Checksum;    // SHA256 of the migration script text
        public DateTime AppliedAt;
    }

    public interface IMigration
    {
        int    Version     { get; }
        string Description { get; }
        string Script      { get; } // canonical text used for checksum
        void   Up(SimulatedDatabase db);
    }

    // =========================================================================
    // Migration runner
    // =========================================================================
    // Applies pending migrations in order. Validates checksums of already-applied
    // migrations to detect accidental edits. Never applies a migration twice.
    public class MigrationRunner
    {
        private readonly SimulatedDatabase _db;
        private readonly List<IMigration>  _migrations;

        public MigrationRunner(SimulatedDatabase db, List<IMigration> migrations)
        {
            _db         = db;
            _migrations = migrations.OrderBy(m => m.Version).ToList();
        }

        // Returns (applied count, errors).
        public (int applied, List<string> errors) Migrate()
        {
            var errors  = new List<string>();
            int applied = 0;

            foreach (IMigration migration in _migrations)
            {
                MigrationRecord existing = _db.AppliedMigrations
                    .FirstOrDefault(r => r.Version == migration.Version);

                if (existing != null)
                {
                    // Already applied — validate checksum to detect tampering.
                    // In production: Flyway throws with "checksum mismatch" error.
                    string currentChecksum = ComputeChecksum(migration.Script);
                    if (existing.Checksum != currentChecksum)
                    {
                        errors.Add($"CHECKSUM MISMATCH on V{migration.Version:D3} " +
                                   $"({migration.Description}) — migration was edited after being applied!");
                    }
                    // Already applied and checksum valid — skip.
                    continue;
                }

                // Pending — apply it.
                Console.WriteLine($"  Applying V{migration.Version:D3} — {migration.Description}...");
                try
                {
                    migration.Up(_db);
                    _db.AppliedMigrations.Add(new MigrationRecord
                    {
                        Version     = migration.Version,
                        Description = migration.Description,
                        Checksum    = ComputeChecksum(migration.Script),
                        AppliedAt   = DateTime.UtcNow
                    });
                    applied++;
                    Console.WriteLine($"    ✓ V{migration.Version:D3} applied");
                }
                catch (Exception ex)
                {
                    errors.Add($"V{migration.Version:D3} FAILED: {ex.Message}");
                    break; // stop on first failure — do not skip ahead
                }
            }
            return (applied, errors);
        }

        public void PrintStatus()
        {
            Console.WriteLine("  schema_migrations table:");
            Console.WriteLine($"  {"Ver",-6} {"Description",-40} {"AppliedAt",-22} {"Checksum",10}");
            Console.WriteLine($"  {new string('─', 82)}");
            foreach (MigrationRecord r in _db.AppliedMigrations)
            {
                Console.WriteLine($"  V{r.Version:D3}   {r.Description,-40} " +
                                  $"{r.AppliedAt:yyyy-MM-dd HH:mm:ss}   {r.Checksum[..8]}...");
            }
        }

        private static string ComputeChecksum(string script)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(script));
            return Convert.ToHexString(hash).ToLower();
        }
    }

    // =========================================================================
    // Concrete migrations — V001 through V006 + V007 (forward rollback)
    // =========================================================================

    // V001 — Create users table.
    // Additive: safe to deploy before new code (old code ignores new tables).
    public class V001_CreateUsersTable : IMigration
    {
        public int    Version     => 1;
        public string Description => "create_users_table";
        public string Script      => "CREATE TABLE users (id INT NOT NULL, email VARCHAR(255) NOT NULL, created_at TIMESTAMPTZ NOT NULL);";

        public void Up(SimulatedDatabase db)
        {
            SimulatedTable t = db.GetOrCreate("users");
            t.AddColumn("id",         "INT",          nullable: false);
            t.AddColumn("email",      "VARCHAR(255)",  nullable: false);
            t.AddColumn("created_at", "TIMESTAMPTZ",   nullable: false);

            // Seed rows so backfill scenarios have data to work with.
            for (int i = 1; i <= 12; i++)
                t.Rows.Add(new Dictionary<string, object>
                {
                    ["id"]         = i,
                    ["email"]      = $"user{i}@example.com",
                    ["created_at"] = DateTime.UtcNow.AddDays(-i)
                });
        }
    }

    // V002 — Add index on users.email.
    // CONCURRENTLY: does not block reads/writes during index build.
    public class V002_AddEmailIndex : IMigration
    {
        public int    Version     => 2;
        public string Description => "add_email_index_concurrently";
        public string Script      => "CREATE INDEX CONCURRENTLY idx_users_email ON users(email);";

        public void Up(SimulatedDatabase db) =>
            db.Get("users").AddIndex("CONCURRENTLY idx_users_email", "email");
    }

    // V003 — Create orders table.
    // Additive: new table, fully backward-compatible.
    public class V003_CreateOrdersTable : IMigration
    {
        public int    Version     => 3;
        public string Description => "create_orders_table";
        public string Script      => "CREATE TABLE orders (id INT NOT NULL, user_id INT NOT NULL, total DECIMAL NOT NULL);";

        public void Up(SimulatedDatabase db)
        {
            SimulatedTable t = db.GetOrCreate("orders");
            t.AddColumn("id",      "INT",     nullable: false);
            t.AddColumn("user_id", "INT",     nullable: false);
            t.AddColumn("total",   "DECIMAL", nullable: false);
        }
    }

    // ── Expand-Contract for renaming  email → email_address ──────────────────

    // V004 — EXPAND phase: add new column email_address (nullable).
    // Old code still reads/writes email — continues to work.
    // New application code (v2) is deployed AFTER this migration:
    //   READ:  email_address ?? email  (fall back to old column)
    //   WRITE: both email AND email_address
    public class V004_ExpandAddEmailAddress : IMigration
    {
        public int    Version     => 4;
        public string Description => "expand_add_email_address_column";
        public string Script      => "ALTER TABLE users ADD COLUMN email_address VARCHAR(255);";

        public void Up(SimulatedDatabase db) =>
            db.Get("users").AddColumn("email_address", "VARCHAR(255)", nullable: true);
    }

    // V005 — BACKFILL phase: copy email → email_address in small batches.
    // Done as a migration but the batching pattern is the key interview concept.
    // In production this runs as a background job, not a single UPDATE.
    public class V005_BackfillEmailAddress : IMigration
    {
        public int    Version     => 5;
        public string Description => "backfill_email_address_from_email";
        public string Script      => "UPDATE users SET email_address = email WHERE email_address IS NULL LIMIT 5; -- repeat";

        private const int BatchSize   = 5;  // small batch: short lock window
        private const int SleepMs     = 10; // yield to application writes between batches

        public void Up(SimulatedDatabase db)
        {
            SimulatedTable users = db.Get("users");
            int total    = 0;
            int batchNum = 0;

            while (true)
            {
                // Each batch = one small UPDATE (short-lived row lock).
                List<Dictionary<string, object>> batch = users.Rows
                    .Where(r => r["email_address"] == null)
                    .Take(BatchSize)
                    .ToList();

                if (batch.Count == 0) break;

                foreach (var row in batch)
                    row["email_address"] = row["email"]; // copy

                total += batch.Count;
                batchNum++;
                Console.WriteLine($"      Batch {batchNum}: copied {batch.Count} rows " +
                                  $"(total {total}/{users.Rows.Count}) — sleeping {SleepMs}ms...");
                Thread.Sleep(SleepMs); // yield; prevents replication lag
            }

            Console.WriteLine($"      Backfill complete: {total} rows updated in {batchNum} batches");
        }
    }

    // V006 — CONTRACT phase: drop the old email column.
    // Deployed AFTER application v3 (which no longer references email).
    // Deployment order for destructive changes:
    //   1. Deploy v3 code  → stop reading/writing email
    //   2. Run V006        → drop email safely
    public class V006_ContractDropEmail : IMigration
    {
        public int    Version     => 6;
        public string Description => "contract_drop_old_email_column";
        public string Script      => "ALTER TABLE users DROP COLUMN email;";

        public void Up(SimulatedDatabase db) =>
            db.Get("users").DropColumn("email");
    }

    // V007 — Forward-only rollback: re-add email as a copy of email_address.
    // "Undo" is never a destructive rollback of V006 — it is a NEW migration.
    // This means the rollback path is tested, versioned, and safe.
    public class V007_ForwardRollbackRestoreEmail : IMigration
    {
        public int    Version     => 7;
        public string Description => "forward_rollback_restore_email_column";
        public string Script      => "ALTER TABLE users ADD COLUMN email VARCHAR(255); UPDATE users SET email = email_address;";

        public void Up(SimulatedDatabase db)
        {
            SimulatedTable users = db.Get("users");
            users.AddColumn("email", "VARCHAR(255)", nullable: true);
            foreach (var row in users.Rows)
                row["email"] = row["email_address"];
        }
    }

    // =========================================================================
    // Dual-write repository (application layer during Expand phase)
    // =========================================================================
    // During Phase 1 (after V004, before V006), the app writes to BOTH columns
    // so that old code reading email and new code reading email_address both work.
    // This class lives in the application layer, not in the migration runner.
    public class UserRepository
    {
        public enum AppVersion { V1_OldOnly, V2_DualWrite, V3_NewOnly }

        private readonly SimulatedTable _users;
        private readonly AppVersion     _version;

        public UserRepository(SimulatedTable users, AppVersion version)
        {
            _users   = users;
            _version = version;
        }

        public void UpdateEmail(int userId, string newEmail)
        {
            Dictionary<string, object> row =
                _users.Rows.FirstOrDefault(r => (int)r["id"] == userId);
            if (row == null) return;

            switch (_version)
            {
                case AppVersion.V1_OldOnly:
                    // Old code: only knows about email
                    row["email"] = newEmail;
                    Console.WriteLine($"    [v1] Wrote email only for user {userId}");
                    break;

                case AppVersion.V2_DualWrite:
                    // Expand phase: write to both so reads from either column succeed
                    row["email"]         = newEmail;
                    row["email_address"] = newEmail;
                    Console.WriteLine($"    [v2] Dual-wrote email + email_address for user {userId}");
                    break;

                case AppVersion.V3_NewOnly:
                    // Contract phase: only write to the new column
                    row["email_address"] = newEmail;
                    Console.WriteLine($"    [v3] Wrote email_address only for user {userId}");
                    break;
            }
        }

        public string ReadEmail(int userId)
        {
            Dictionary<string, object> row =
                _users.Rows.FirstOrDefault(r => (int)r["id"] == userId);
            if (row == null) return null;

            return _version switch
            {
                AppVersion.V1_OldOnly  => row["email"]?.ToString(),
                // Fall back to email if email_address not yet backfilled (stale row)
                AppVersion.V2_DualWrite => (row.ContainsKey("email_address") && row["email_address"] != null
                                            ? row["email_address"]
                                            : row["email"])?.ToString(),
                AppVersion.V3_NewOnly  => row["email_address"]?.ToString(),
                _                      => null
            };
        }
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public class Program
    {
        public static void Main()
        {
            // =================================================================
            // Scenario 1 — Sequential migration application and version tracking
            // V001, V002, V003 applied in order. Each recorded in schema_migrations.
            // Running the runner again is a no-op — already-applied are skipped.
            // Demonstrates: versioned files, applied-once guarantee, idempotency.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Sequential migrations — versioning and tracking  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            var db     = new SimulatedDatabase();
            var runner = new MigrationRunner(db, new List<IMigration>
            {
                new V001_CreateUsersTable(),
                new V002_AddEmailIndex(),
                new V003_CreateOrdersTable()
            });

            var (applied, errors) = runner.Migrate();
            Console.WriteLine($"\n  Applied: {applied} migration(s)");

            Console.WriteLine("\n  Current schema:");
            db.Get("users").PrintSchema();

            Console.WriteLine("\n  Re-running runner (all already applied — no-op):");
            var (applied2, _) = runner.Migrate();
            Console.WriteLine($"  Applied: {applied2}  ← 0 (idempotent)");

            Console.WriteLine();
            runner.PrintStatus();

            // =================================================================
            // Scenario 2 — Checksum tamper detection
            // Simulate an edit to an already-applied migration (e.g. someone
            // modified V001 on disk). The runner detects the checksum mismatch
            // and raises an error rather than silently re-applying a changed script.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Checksum mismatch — tampered migration detected   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            // TamperedV001 has the same version but different script text.
            var tamperedRunner = new MigrationRunner(db, new List<IMigration>
            {
                new TamperedV001(), // same version=1, different script
                new V002_AddEmailIndex(),
                new V003_CreateOrdersTable()
            });

            var (_, errors2) = tamperedRunner.Migrate();
            Console.WriteLine($"\n  Errors detected: {errors2.Count}");
            foreach (string err in errors2)
                Console.WriteLine($"  ✗ {err}");

            // =================================================================
            // Scenario 3 — Expand-Contract: zero-downtime column rename
            // Three migration phases + dual-write application code.
            //
            // Phase 1 (V004): add email_address nullable. App v2 dual-writes.
            // Phase 2 (V005): backfill email → email_address in small batches.
            // Phase 3 (V006): drop old email. App v3 writes email_address only.
            //
            // At every point, either the old or new code can read its column.
            // No downtime, no full table lock during the rename.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Expand-Contract — zero-downtime column rename     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            // ── Phase 1: EXPAND — add email_address column ────────────────────
            Console.WriteLine("\n  ── Phase 1: EXPAND (add email_address, keep email) ──");
            new MigrationRunner(db, new List<IMigration> { new V004_ExpandAddEmailAddress() }).Migrate();
            db.Get("users").PrintSchema();

            // App v2 deployed: dual-write keeps both columns current.
            var repoV2 = new UserRepository(db.Get("users"), UserRepository.AppVersion.V2_DualWrite);
            repoV2.UpdateEmail(1, "updated_user1@example.com");
            Console.WriteLine($"    v2 reads email for user 1:  {repoV2.ReadEmail(1)}");

            // Old v1 code still in service — reads email fine.
            var repoV1 = new UserRepository(db.Get("users"), UserRepository.AppVersion.V1_OldOnly);
            Console.WriteLine($"    v1 reads email for user 1:  {repoV1.ReadEmail(1)}  ← old code still works");

            // ── Phase 2: BACKFILL — copy email → email_address ────────────────
            Console.WriteLine("\n  ── Phase 2: BACKFILL (copy data in batches) ──");
            new MigrationRunner(db, new List<IMigration>
            {
                new V004_ExpandAddEmailAddress(), // already applied — skipped
                new V005_BackfillEmailAddress()
            }).Migrate();

            // Verify a few rows
            Console.WriteLine($"\n    User 5 email:         {db.Get("users").Rows[4]["email"]}");
            Console.WriteLine($"    User 5 email_address: {db.Get("users").Rows[4]["email_address"]}  ← backfilled");

            // ── Phase 3: CONTRACT — drop old email column ─────────────────────
            Console.WriteLine("\n  ── Phase 3: CONTRACT (deploy v3 code, then drop email) ──");

            // App v3 deployed first — stops referencing email column.
            var repoV3 = new UserRepository(db.Get("users"), UserRepository.AppVersion.V3_NewOnly);
            repoV3.UpdateEmail(2, "v3_user2@example.com");

            // Now safe to drop the column — no code references it.
            new MigrationRunner(db, new List<IMigration>
            {
                new V004_ExpandAddEmailAddress(),
                new V005_BackfillEmailAddress(),
                new V006_ContractDropEmail()
            }).Migrate();

            Console.WriteLine("\n  Schema after CONTRACT:");
            db.Get("users").PrintSchema();
            Console.WriteLine($"\n  v3 reads email_address for user 2: {repoV3.ReadEmail(2)}  ← renamed column works");

            // =================================================================
            // Scenario 4 — Forward-only rollback: undo via a new migration
            // If the team decides to revert the rename, they do NOT roll back V006.
            // Instead they write V007 that re-adds the email column.
            // This keeps the migration history linear, tested, and safe.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Forward-only rollback — undo is a new migration   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            Console.WriteLine($"\n  Before V007 — has 'email' column: {db.Get("users").HasColumn("email")}");

            new MigrationRunner(db, new List<IMigration>
            {
                new V004_ExpandAddEmailAddress(),
                new V005_BackfillEmailAddress(),
                new V006_ContractDropEmail(),
                new V007_ForwardRollbackRestoreEmail()  // the "rollback" is just another migration
            }).Migrate();

            Console.WriteLine($"  After  V007 — has 'email' column: {db.Get("users").HasColumn("email")}  ← restored");
            Console.WriteLine($"  User 3 email restored: {db.Get("users").Rows[2]["email"]}");

            Console.WriteLine("\n  Final schema_migrations table:");
            // All 7 migrations recorded — linear history, no gaps, no edits.
            new MigrationRunner(db, new List<IMigration>
            {
                new V001_CreateUsersTable(),       new V002_AddEmailIndex(),
                new V003_CreateOrdersTable(),      new V004_ExpandAddEmailAddress(),
                new V005_BackfillEmailAddress(),   new V006_ContractDropEmail(),
                new V007_ForwardRollbackRestoreEmail()
            }).PrintStatus();
        }
    }

    // Helper: tampered version of V001 used in Scenario 2
    internal class TamperedV001 : IMigration
    {
        public int    Version     => 1;
        public string Description => "create_users_table";
        // Different script text → different checksum → tamper detected
        public string Script      => "CREATE TABLE users (id INT NOT NULL, email TEXT NOT NULL);";
        public void   Up(SimulatedDatabase db) { }
    }

} // namespace DataPatterns
