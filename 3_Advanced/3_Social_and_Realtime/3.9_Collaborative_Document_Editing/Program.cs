// Program — entry point for all Collaborative Document Editing demo scenarios.

namespace AdvancedDesigns
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Collaborative Document Editing (OT) Demo ===\n");

            Scenario1_SequentialEdits();
            Scenario2_ConcurrentInsertInsert();
            Scenario3_ConcurrentInsertDelete();
            Scenario4_ConcurrentDeleteSameChar();
            Scenario5_ThreeClientConvergence();
        }

        // ── Scenario 1: Sequential Edits (No Conflict) ────────────────────────

        static void Scenario1_SequentialEdits()
        {
            Console.WriteLine("─── Scenario 1: Sequential Edits (no conflict) ───");

            var server = new CollabServer("Hello");
            var alice = new EditingClient("alice", "Hello");
            var bob   = new EditingClient("bob",   "Hello");

            // Wire up broadcast
            server.RegisterClient((op, v) => alice.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => bob.ReceiveRemoteOp(op, v));

            // Alice types '!' at end — version 0
            var op1 = alice.LocalInsert(5, '!');
            server.ReceiveOp(op1, "alice");

            Console.WriteLine($"Alice appends '!': server=\"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local: \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local: \"{bob.LocalText}\"");

            // Bob (now at v=1) types ' ' at pos 6 — after the '!'
            var op2 = bob.LocalInsert(6, ' ');
            server.ReceiveOp(op2, "bob");

            Console.WriteLine($"\nBob appends ' ': server=\"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local: \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local: \"{bob.LocalText}\"");
            Console.WriteLine($"  All converged: {alice.LocalText == bob.LocalText && bob.LocalText == server.CurrentText}");
            Console.WriteLine();
        }

        // ── Scenario 2: Concurrent Insert/Insert ─────────────────────────────

        static void Scenario2_ConcurrentInsertInsert()
        {
            Console.WriteLine("─── Scenario 2: Concurrent Insert/Insert ───");
            Console.WriteLine("Doc = \"Hello\". Alice inserts 'X' at pos 5. Bob inserts 'Z' at pos 2. Simultaneous.\n");

            var server = new CollabServer("Hello");
            var alice  = new EditingClient("alice", "Hello");
            var bob    = new EditingClient("bob",   "Hello");

            server.RegisterClient((op, v) => alice.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => bob.ReceiveRemoteOp(op, v));

            // Both at version 0 — concurrent
            var aliceOp = alice.LocalInsert(5, 'X'); // "HelloX" locally
            var bobOp   = bob.LocalInsert(2, 'Z');   // "HeZllo" locally

            Console.WriteLine($"Alice local after insert: \"{alice.LocalText}\"");
            Console.WriteLine($"Bob   local after insert: \"{bob.LocalText}\"");

            // Server receives Alice first
            Console.WriteLine("\nServer receives Alice's op first:");
            server.ReceiveOp(aliceOp, "alice");

            // Server receives Bob — must transform
            Console.WriteLine("\nServer receives Bob's op (concurrent at v=0):");
            server.ReceiveOp(bobOp, "bob");

            Console.WriteLine($"\nFinal state:");
            Console.WriteLine($"  Server:       \"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local:  \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local:  \"{bob.LocalText}\"");
            Console.WriteLine($"  All converged: {alice.LocalText == server.CurrentText && bob.LocalText == server.CurrentText}");

            Console.WriteLine("\nServer transform log:");
            foreach (var line in server.GetLog()) Console.WriteLine($"  {line}");
            Console.WriteLine();
        }

        // ── Scenario 3: Concurrent Insert/Delete ─────────────────────────────

        static void Scenario3_ConcurrentInsertDelete()
        {
            Console.WriteLine("─── Scenario 3: Concurrent Insert/Delete ───");
            Console.WriteLine("Doc = \"Hello\". Alice inserts 'X' at pos 5. Bob deletes 'l' at pos 2. Simultaneous.\n");

            var server = new CollabServer("Hello");
            var alice  = new EditingClient("alice", "Hello");
            var bob    = new EditingClient("bob",   "Hello");

            server.RegisterClient((op, v) => alice.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => bob.ReceiveRemoteOp(op, v));

            var aliceOp = alice.LocalInsert(5, 'X'); // "HelloX"
            var bobOp   = bob.LocalDelete(2);         // "Helo"

            Console.WriteLine($"Alice local: \"{alice.LocalText}\"");
            Console.WriteLine($"Bob   local: \"{bob.LocalText}\"");

            Console.WriteLine("\nServer receives Alice then Bob:");
            server.ReceiveOp(aliceOp, "alice");
            server.ReceiveOp(bobOp, "bob");

            Console.WriteLine($"\nFinal state:");
            Console.WriteLine($"  Server:       \"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local:  \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local:  \"{bob.LocalText}\"");
            Console.WriteLine($"  All converged: {alice.LocalText == server.CurrentText && bob.LocalText == server.CurrentText}");

            Console.WriteLine("\nServer transform log:");
            foreach (var line in server.GetLog()) Console.WriteLine($"  {line}");
            Console.WriteLine();
        }

        // ── Scenario 4: Both Delete the Same Character ────────────────────────

        static void Scenario4_ConcurrentDeleteSameChar()
        {
            Console.WriteLine("─── Scenario 4: Concurrent Delete of Same Character (NoOp resolution) ───");
            Console.WriteLine("Doc = \"Hello\". Alice deletes pos 2 ('l'). Bob also deletes pos 2 ('l'). Simultaneous.\n");

            var server = new CollabServer("Hello");
            var alice  = new EditingClient("alice", "Hello");
            var bob    = new EditingClient("bob",   "Hello");

            server.RegisterClient((op, v) => alice.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => bob.ReceiveRemoteOp(op, v));

            var aliceOp = alice.LocalDelete(2); // "Helo"
            var bobOp   = bob.LocalDelete(2);   // "Helo"

            Console.WriteLine($"Alice local: \"{alice.LocalText}\"");
            Console.WriteLine($"Bob   local: \"{bob.LocalText}\"");

            Console.WriteLine("\nServer receives Alice then Bob:");
            server.ReceiveOp(aliceOp, "alice");
            server.ReceiveOp(bobOp, "bob");   // Bob's delete transforms to NoOp

            Console.WriteLine($"\nFinal state (character deleted exactly once):");
            Console.WriteLine($"  Server:       \"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local:  \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local:  \"{bob.LocalText}\"");
            Console.WriteLine($"  All converged: {alice.LocalText == server.CurrentText && bob.LocalText == server.CurrentText}");

            Console.WriteLine("\nServer transform log:");
            foreach (var line in server.GetLog()) Console.WriteLine($"  {line}");
            Console.WriteLine();
        }

        // ── Scenario 5: Three-Client Convergence ─────────────────────────────

        static void Scenario5_ThreeClientConvergence()
        {
            Console.WriteLine("─── Scenario 5: Three-Client Convergence ───");
            Console.WriteLine("Doc = \"abc\". Alice inserts 'X' at 0, Bob inserts 'Y' at 1, Carol deletes 'b' at 1. All concurrent.\n");

            var server = new CollabServer("abc");
            var alice  = new EditingClient("alice", "abc");
            var bob    = new EditingClient("bob",   "abc");
            var carol  = new EditingClient("carol", "abc");

            server.RegisterClient((op, v) => alice.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => bob.ReceiveRemoteOp(op, v));
            server.RegisterClient((op, v) => carol.ReceiveRemoteOp(op, v));

            // All at version 0 — fully concurrent
            var aliceOp = alice.LocalInsert(0, 'X'); // "Xabc"
            var bobOp   = bob.LocalInsert(1, 'Y');   // "aYbc"
            var carolOp = carol.LocalDelete(1);       // "ac"

            Console.WriteLine($"Alice local: \"{alice.LocalText}\"");
            Console.WriteLine($"Bob   local: \"{bob.LocalText}\"");
            Console.WriteLine($"Carol local: \"{carol.LocalText}\"");

            Console.WriteLine("\nServer receives: Alice → Bob → Carol:");
            server.ReceiveOp(aliceOp, "alice");
            server.ReceiveOp(bobOp,   "bob");
            server.ReceiveOp(carolOp, "carol");

            Console.WriteLine($"\nFinal state:");
            Console.WriteLine($"  Server:       \"{server.CurrentText}\"");
            Console.WriteLine($"  Alice local:  \"{alice.LocalText}\"");
            Console.WriteLine($"  Bob   local:  \"{bob.LocalText}\"");
            Console.WriteLine($"  Carol local:  \"{carol.LocalText}\"");
            bool converged = alice.LocalText == server.CurrentText
                          && bob.LocalText   == server.CurrentText
                          && carol.LocalText == server.CurrentText;
            Console.WriteLine($"  All 3 clients converged: {converged}");

            Console.WriteLine("\nServer transform log:");
            foreach (var line in server.GetLog()) Console.WriteLine($"  {line}");
        }
    }
}
