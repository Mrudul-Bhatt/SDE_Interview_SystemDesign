// CollabServer — the traffic controller that serializes concurrent edits into a
// single consistent op sequence shared by every client.
//
// THE BIG IDEA:
// Think of CollabServer like an air traffic controller. Multiple planes (clients)
// are flying toward the same runway (the document). Each pilot planned their approach
// based on the runway layout when they departed (their clientVersion). By the time
// they arrive, other planes may have already landed and shifted the markings.
// ATC (CollabServer) adjusts each plane's approach path (transforms the op) before
// clearing it to land (Apply to ServerDocument). Then it broadcasts the updated
// layout to every other plane still in the air so nobody's navigation chart goes stale.
//
// THE RECEIVE PIPELINE (order is load-bearing — do not reorder):
//
//   1. Fetch server ops since the client's base version.
//      GetOpsSince(op.ClientVersion) returns all ops committed by OTHER clients
//      after this client last synced. These are the ops whose effect on the
//      document the incoming op doesn't yet know about.
//
//   2. Transform the incoming op against each server op in order (chained, not batched).
//      Each step adjusts Position for one committed op; the result feeds the next step.
//      Early-exit on NoOp: once a NoOp, no further transform can recover a meaningful
//      operation — the target character is already gone.
//
//   3. Apply the transformed op to ServerDocument.
//      Advances version, appends to the immutable log, mutates the text buffer.
//
//   4. Broadcast the TRANSFORMED op (not the original) to all registered clients.
//      Other clients are sitting at the current server version. The transformed op
//      has positions valid for that version. Sending the original (pre-transform)
//      op would point at wrong positions from every other client's perspective
//      and corrupt their local document.
//
// WHY SINGLE-WRITER PER DOCUMENT (sharded by doc_id in production):
// The transform chain only converges if ops are applied in one agreed-upon total
// order. If two servers each independently transformed and applied concurrent ops,
// their chains would diverge — different clients would see different strings even
// after applying the same set of ops. One server owning one document (no distributed
// locking required) is the architectural guarantee that makes OT deterministic.
//
// WHY TRANSFORM SEQUENTIALLY (not in batch):
// Each transform step changes the position that the next step must work with:
//   Transform(op, serverOp1) → position adjusted for op1's effect
//   Transform(result, serverOp2) → position adjusted for op1 AND op2
// A batch transform would need to compute the cumulative position shift of all
// prior ops simultaneously — that is just the sequential chain rewritten badly.

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class CollabServer
    {
        // The single authoritative document — one instance per document, shared by
        // no other server (see WHY SINGLE-WRITER above). Holds the text buffer,
        // the version counter, and the immutable op log.
        //
        // ── RUNTIME SNAPSHOT (doc = "Hello", after Alice and Bob each type once) ──
        //   Before any ops:    _doc.Text = "Hello",  _doc.Version = 0
        //   After Alice's op:  _doc.Text = "Hello!", _doc.Version = 1
        //   After Bob's op:    _doc.Text = "WHello!",_doc.Version = 2
        private readonly ServerDocument _doc;

        // Every registered client callback. When a new op is applied, CollabServer
        // calls every entry here with (transformedOp, newVersion) so each client
        // can update its local document and advance its local version counter.
        //
        // The Action takes TWO arguments — TextOp and int — because the client needs
        // both: the op to apply to its local text buffer AND the version number to
        // record as its new _serverVersion (so its next submission carries the right
        // ClientVersion for GetOpsSince to compute the correct catch-up window).
        //
        // In production each entry is a WebSocket write, not an in-memory callback.
        // The list is populated once at setup via RegisterClient; real systems would
        // add/remove entries dynamically as clients connect and disconnect.
        //
        // ── RUNTIME SNAPSHOT (two clients registered) ──
        //   _broadcastTargets = [
        //       λ(op, ver) → alice.ReceiveBroadcast(op, ver),
        //       λ(op, ver) → bob.ReceiveBroadcast(op, ver)
        //   ]
        //   When Bob's op is applied at v=2, BOTH lambdas fire:
        //       alice.ReceiveBroadcast(Insert(pos=0,'W'), 2)   ← alice applies it locally
        //       bob.ReceiveBroadcast(Insert(pos=0,'W'),   2)   ← bob gets his own op echoed back
        //   Bob ignores the echo (EditingClient checks for self-sent ops); alice applies it.
        private readonly List<Action<TextOp, int>> _broadcastTargets = [];

        // Append-only event log for debugging and test assertions.
        // Records every step of the receive pipeline so you can trace exactly which
        // transform ran, what position it produced, and what the document looked like
        // after each apply. In production this is structured telemetry, not a list.
        //
        // ── RUNTIME SNAPSHOT (after Alice Insert(5,'!') then Bob Insert(0,'W')) ──
        //   _log = [
        //       "Server v=0: received Insert(5,'!') from alice",
        //       "  Applied → doc=\"Hello!\" v=1",
        //       "Server v=1: received Insert(0,'W') from bob",
        //       "  Transform against Insert(5,'!') → Insert(0,'W')   ← pos 0 < 5, no shift",
        //       "  Applied → doc=\"WHello!\" v=2"
        //   ]
        private readonly List<string> _log = [];

        public CollabServer(string initialText = "")
        {
            _doc = new ServerDocument(initialText);
        }

        // Registers a client's receive callback. Called once per client at connect time.
        // The callback fires on every ReceiveOp broadcast — including ops submitted by
        // other clients. EditingClient is responsible for ignoring its own echoed ops.
        // In production: replaces the in-memory callback with a WebSocket channel write.
        public void RegisterClient(Action<TextOp, int> onBroadcast)
        {
            _broadcastTargets.Add(onBroadcast);
        }

        // Executes the full receive pipeline: transform → apply → broadcast.
        // Returns (transformedOp, newVersion) so the caller (EditingClient or a test)
        // can verify what position the op ultimately landed at and which version it became.
        //
        // ── RUNTIME SNAPSHOT — two concurrent ops on "Hello" (v=0) ──
        //
        //   CASE 1: Alice's op arrives first — no prior server ops to transform against.
        //       op = Insert(pos=5, '!', clientVersion=0)
        //       GetOpsSince(0) → []           ← empty: Alice IS the first op
        //       transform loop: skipped
        //       Apply(Insert(5,'!')) → doc="Hello!" v=1
        //       Broadcast Insert(5,'!') at v=1 to all clients
        //       Returns (Insert(5,'!'), 1)
        //
        //   CASE 2: Bob's concurrent op arrives second — must transform against Alice's.
        //       op = Insert(pos=0, 'W', clientVersion=0)   ← Bob was also at v=0
        //       GetOpsSince(0) → [(1, Insert(5,'!'))]      ← Alice's op is now in the log
        //       Transform(Insert(0,'W'), Insert(5,'!')):
        //           op2.Position=5 is NOT < op1.Position=0 → no shift → Insert(0,'W') unchanged
        //       Apply(Insert(0,'W')) → doc="WHello!" v=2
        //       Broadcast Insert(0,'W') at v=2 to all clients
        //       Returns (Insert(0,'W'), 2)
        //
        //   CASE 3: Carol deletes the same char Bob already deleted (NoOp path).
        //       op = Delete(pos=2, clientVersion=0)
        //       GetOpsSince(0) → [(1,Insert(5,'!')), (2,Insert(0,'W')), …, (N,Delete(2))]
        //       After chained transforms, position aligns to same char → Transform → NoOp
        //       Apply(NoOp) → version increments, buffer unchanged
        //       Broadcast NoOp at vN+1 → clients ignore it (no text change)
        public (TextOp transformedOp, int newVersion) ReceiveOp(TextOp op, string clientId)
        {
            Log($"Server v={_doc.Version}: received {op} from {clientId}");

            // Step 1 + 2: fetch concurrent server ops and transform against each in order.
            TextOp transformed = op;
            var serverOpsSince = _doc.GetOpsSince(op.ClientVersion);
            foreach (var (_, serverOp) in serverOpsSince)
            {
                transformed = OTEngine.Transform(transformed, serverOp);
                if (transformed.IsNoOp)
                {
                    Log($"  Transformed to NoOp (already handled by concurrent op)");
                    break; // early-exit: a NoOp can't be further corrected into a real op
                }
                Log($"  Transform against {serverOp} → {transformed}");
            }

            // Step 3: commit to the authoritative document.
            int newVersion = _doc.Apply(transformed, clientId);
            Log($"  Applied → doc=\"{_doc.Text}\" v={newVersion}");

            // Step 4: push the TRANSFORMED op to all clients so they stay in sync.
            foreach (var target in _broadcastTargets)
                target(transformed, newVersion);

            return (transformed, newVersion);
        }

        public string CurrentText => _doc.Text;
        public int CurrentVersion => _doc.Version;
        public IReadOnlyList<string> GetLog() => _log;
        private void Log(string msg) => _log.Add(msg);
    }
}
