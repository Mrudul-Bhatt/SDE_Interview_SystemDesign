using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AdvancedDesigns
{
    // ─── Operation (Insert or Delete at a position) ────────────────────────────

    public enum OpKind { Insert, Delete, NoOp }

    public class TextOp
    {
        public OpKind Kind { get; }
        public int Position { get; }
        public char Character { get; }  // for Insert
        public string ClientId { get; }
        public int ClientVersion { get; }

        public TextOp(OpKind kind, int position, char character = '\0',
            string clientId = "", int clientVersion = 0)
        {
            Kind = kind;
            Position = position;
            Character = character;
            ClientId = clientId;
            ClientVersion = clientVersion;
        }

        public static TextOp Noop() => new TextOp(OpKind.NoOp, -1);

        public bool IsNoOp => Kind == OpKind.NoOp;

        public override string ToString() =>
            Kind == OpKind.Insert ? $"Insert(pos={Position}, '{Character}')"
          : Kind == OpKind.Delete ? $"Delete(pos={Position})"
          : "NoOp";
    }

    // ─── Operational Transformation Engine ────────────────────────────────────

    public static class OTEngine
    {
        // Transform op1 assuming op2 was already applied to the document.
        // Returns op1 adjusted so it has the same intended effect after op2.
        public static TextOp Transform(TextOp op1, TextOp op2)
        {
            if (op1.IsNoOp || op2.IsNoOp) return op1;

            if (op1.Kind == OpKind.Insert && op2.Kind == OpKind.Insert)
                return TransformInsertInsert(op1, op2);

            if (op1.Kind == OpKind.Insert && op2.Kind == OpKind.Delete)
                return TransformInsertDelete(op1, op2);

            if (op1.Kind == OpKind.Delete && op2.Kind == OpKind.Insert)
                return TransformDeleteInsert(op1, op2);

            // Delete vs Delete
            return TransformDeleteDelete(op1, op2);
        }

        private static TextOp TransformInsertInsert(TextOp op1, TextOp op2)
        {
            // op2 (insert at p2) was applied first — adjust op1's position
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Insert, op1.Position + 1, op1.Character, op1.ClientId, op1.ClientVersion);

            if (op2.Position == op1.Position)
            {
                // Tie-break by clientId for deterministic ordering
                if (string.Compare(op2.ClientId, op1.ClientId, StringComparison.Ordinal) < 0)
                    return new TextOp(OpKind.Insert, op1.Position + 1, op1.Character, op1.ClientId, op1.ClientVersion);
            }

            return op1; // op2 was at or after op1's position — no shift needed
        }

        private static TextOp TransformInsertDelete(TextOp op1, TextOp op2)
        {
            // op2 (delete at p2) was applied first
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Insert, op1.Position - 1, op1.Character, op1.ClientId, op1.ClientVersion);
            return op1;
        }

        private static TextOp TransformDeleteInsert(TextOp op1, TextOp op2)
        {
            // op2 (insert at p2) was applied first
            if (op2.Position <= op1.Position)
                return new TextOp(OpKind.Delete, op1.Position + 1, clientId: op1.ClientId, clientVersion: op1.ClientVersion);
            return op1;
        }

        private static TextOp TransformDeleteDelete(TextOp op1, TextOp op2)
        {
            // op2 (delete at p2) was applied first
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Delete, op1.Position - 1, clientId: op1.ClientId, clientVersion: op1.ClientVersion);

            if (op2.Position == op1.Position)
                return TextOp.Noop(); // already deleted by op2

            return op1; // op2 was after op1 — no effect
        }
    }

    // ─── Document (server-side authoritative copy) ─────────────────────────────

    public class ServerDocument
    {
        private readonly StringBuilder _text;
        private int _version;
        private readonly List<(int version, TextOp op, string clientId)> _opLog
            = new List<(int, TextOp, string)>();

        public string Text => _text.ToString();
        public int Version => _version;

        public ServerDocument(string initialText = "")
        {
            _text = new StringBuilder(initialText);
        }

        // Apply op and record it. Returns the version after applying.
        public int Apply(TextOp op, string clientId)
        {
            if (!op.IsNoOp) ApplyToBuffer(op);
            _version++;
            _opLog.Add((_version, op, clientId));
            return _version;
        }

        // Get all ops after a given version (for client catch-up)
        public List<(int version, TextOp op)> GetOpsSince(int afterVersion) =>
            _opLog.Where(e => e.version > afterVersion)
                  .Select(e => (e.version, e.op))
                  .ToList();

        private void ApplyToBuffer(TextOp op)
        {
            if (op.Kind == OpKind.Insert)
            {
                int pos = Math.Min(op.Position, _text.Length);
                _text.Insert(pos, op.Character);
            }
            else if (op.Kind == OpKind.Delete && op.Position < _text.Length)
            {
                _text.Remove(op.Position, 1);
            }
        }
    }

    // ─── Collab Server (receives, transforms, broadcasts ops) ─────────────────

    public class CollabServer
    {
        private readonly ServerDocument _doc;
        private readonly List<Action<TextOp, int>> _broadcastTargets = new List<Action<TextOp, int>>();
        private readonly List<string> _log = new List<string>();

        public CollabServer(string initialText = "")
        {
            _doc = new ServerDocument(initialText);
        }

        public void RegisterClient(Action<TextOp, int> onBroadcast)
        {
            _broadcastTargets.Add(onBroadcast);
        }

        // Receive an op from a client at clientVersion, transform, apply, broadcast.
        public (TextOp transformedOp, int newVersion) ReceiveOp(TextOp op, string clientId)
        {
            Log($"Server v={_doc.Version}: received {op} from {clientId}");

            // Transform incoming op against all server ops since client's version
            TextOp transformed = op;
            var serverOpsSince = _doc.GetOpsSince(op.ClientVersion);
            foreach (var (_, serverOp) in serverOpsSince)
            {
                transformed = OTEngine.Transform(transformed, serverOp);
                if (transformed.IsNoOp)
                {
                    Log($"  Transformed to NoOp (already handled by concurrent op)");
                    break;
                }
                Log($"  Transform against {serverOp} → {transformed}");
            }

            int newVersion = _doc.Apply(transformed, clientId);
            Log($"  Applied → doc=\"{_doc.Text}\" v={newVersion}");

            // Broadcast to all registered clients (in real system: over WebSocket)
            foreach (var target in _broadcastTargets)
                target(transformed, newVersion);

            return (transformed, newVersion);
        }

        public string CurrentText => _doc.Text;
        public int CurrentVersion => _doc.Version;
        public IReadOnlyList<string> Log() => _log;
        private void Log(string msg) => _log.Add(msg);
    }

    // ─── Client (local document + pending op buffer) ───────────────────────────

    public class EditingClient
    {
        public string ClientId { get; }
        private readonly StringBuilder _localText;
        private int _serverVersion;
        private readonly Queue<TextOp> _pendingOps = new Queue<TextOp>();
        private readonly List<string> _log = new List<string>();

        public string LocalText => _localText.ToString();
        public int ServerVersion => _serverVersion;

        public EditingClient(string clientId, string initialText, int initialVersion = 0)
        {
            ClientId = clientId;
            _localText = new StringBuilder(initialText);
            _serverVersion = initialVersion;
        }

        // User types — apply locally and queue for server
        public TextOp LocalInsert(int position, char ch)
        {
            int safePos = Math.Min(position, _localText.Length);
            _localText.Insert(safePos, ch);
            var op = new TextOp(OpKind.Insert, safePos, ch, ClientId, _serverVersion);
            _pendingOps.Enqueue(op);
            Log($"{ClientId} local insert: '{ch}' at {safePos} → \"{_localText}\"");
            return op;
        }

        public TextOp LocalDelete(int position)
        {
            if (position >= _localText.Length) return TextOp.Noop();
            _localText.Remove(position, 1);
            var op = new TextOp(OpKind.Delete, position, clientId: ClientId, clientVersion: _serverVersion);
            _pendingOps.Enqueue(op);
            Log($"{ClientId} local delete: pos={position} → \"{_localText}\"");
            return op;
        }

        // Server broadcasts a remote op — transform against pending local ops, then apply
        public void ReceiveRemoteOp(TextOp serverOp, int newServerVersion)
        {
            if (serverOp.ClientId == ClientId)
            {
                // This is our own op coming back as ACK — just advance version
                if (_pendingOps.Count > 0) _pendingOps.Dequeue();
                _serverVersion = newServerVersion;
                Log($"{ClientId} ACK own op, server now v={newServerVersion}");
                return;
            }

            // Transform incoming op against each of our pending (unacknowledged) ops
            TextOp incoming = serverOp;
            var pendingList = _pendingOps.ToList();
            for (int i = 0; i < pendingList.Count; i++)
            {
                incoming = OTEngine.Transform(incoming, pendingList[i]);
                // Also transform the pending op against the incoming (so they stay consistent)
                pendingList[i] = OTEngine.Transform(pendingList[i], serverOp);
            }

            // Rebuild pending queue with transformed ops
            _pendingOps.Clear();
            foreach (var p in pendingList) _pendingOps.Enqueue(p);

            _serverVersion = newServerVersion;

            // Apply the (possibly transformed) remote op to local text
            if (!incoming.IsNoOp)
            {
                ApplyToLocal(incoming);
                Log($"{ClientId} received remote {serverOp} → transformed to {incoming} → \"{_localText}\"");
            }
            else
            {
                Log($"{ClientId} received remote {serverOp} → NoOp (already handled locally)");
            }
        }

        private void ApplyToLocal(TextOp op)
        {
            if (op.Kind == OpKind.Insert)
            {
                int pos = Math.Min(op.Position, _localText.Length);
                _localText.Insert(pos, op.Character);
            }
            else if (op.Kind == OpKind.Delete && op.Position < _localText.Length)
            {
                _localText.Remove(op.Position, 1);
            }
        }

        public IReadOnlyList<string> Log() => _log;
        private void Log(string msg) => _log.Add(msg);
    }

    // ─── Main Program ──────────────────────────────────────────────────────────

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
            foreach (var line in server.Log()) Console.WriteLine($"  {line}");
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
            foreach (var line in server.Log()) Console.WriteLine($"  {line}");
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
            foreach (var line in server.Log()) Console.WriteLine($"  {line}");
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
            foreach (var line in server.Log()) Console.WriteLine($"  {line}");
        }
    }
}
