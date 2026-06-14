// ServerDocument — authoritative document state and the immutable operation log.
//
// THE BIG IDEA:
// Think of ServerDocument like a bank account ledger. The current balance (_text)
// is just the sum of all transactions replayed in order — you could throw it away
// and recompute it from scratch by replaying every entry in the ledger. The ledger
// itself (_opLog) is the real source of truth: immutable, append-only, never
// modified after the fact.
//
// This split is what makes reconnection reliable. When Bob's laptop goes offline
// at version 5 and reconnects at version 9, the server doesn't need to diff anything
// — it just hands Bob ops 6–9 via GetOpsSince(5). Bob runs each one through
// OTEngine.Transform against his own pending ops and arrives at the same document
// as every other client, regardless of which server he reconnects to.
//
// WHY OP LOG IS THE SOURCE OF TRUTH (not the text buffer):
// _text is a fast-access derived cache. If a server crashes and restarts, it
// reconstructs _text by replaying _opLog from version 0. The log entries are also
// exactly what CollabServer sends to reconnecting clients — "here are all ops since
// your last known version; transform your pending work against them."
// In production: _opLog lives in Cassandra (partition key = documentId, clustering
// key = version ASC for cheap range reads). _text is snapshotted to Redis or S3
// periodically so server restarts don't replay millions of ops.
//
// WHY APPLY INCREMENTS VERSION EVEN FOR NOOPS:
// A NoOp still represents "this client's operation was received and processed at
// this point in the sequence." Version is a Lamport clock, not a character-change
// counter. Giving NoOps a slot preserves causal ordering — GetOpsSince returns a
// gapless integer sequence, and clients can trust that every version between their
// local counter and the server version has exactly one log entry.
//
// WHY APPLY RETURNS THE NEW VERSION (not void):
// CollabServer stamps this version number into the Ack it sends back to the
// submitting client. The client uses the acked version to advance its own local
// version counter and know precisely "my op is now server version N."

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AdvancedDesigns
{
    public class ServerDocument
    {
        // The in-memory text buffer: a derived cache of applying every op in _opLog
        // in order. Rebuilt on server restart by replaying the log. StringBuilder
        // is used instead of string because Insert/Remove at arbitrary positions
        // mutate in place rather than allocating a new string for every keystroke.
        //
        // ── RUNTIME SNAPSHOT (after Alice and Bob each type one character) ──
        //   Initial text: "Hi"
        //   Op 1 (alice): Insert(pos=2, '!')  → _text = "Hi!"
        //   Op 2 (bob):   Insert(pos=0, 'W')  → _text = "WHi!"
        //
        //   _text.ToString() = "WHi!"   ← what all clients converge to
        //
        //   If server restarts: replay op1 then op2 on "" + "Hi" → same "WHi!" ✓
        private readonly StringBuilder _text;

        // Monotonically increasing Lamport clock. Advanced by every Apply() call
        // regardless of whether the op is a NoOp (see header for why).
        // Clients piggyback their local version on every op submission so CollabServer
        // can look up which server ops arrived after them via GetOpsSince.
        //
        // ── RUNTIME SNAPSHOT (same scenario) ──
        //   After op1: _version = 1
        //   After op2: _version = 2
        //   After a NoOp from carol: _version = 3   ← NoOp still increments
        private int _version;

        // The append-only ledger: every op ever applied to this document, in order.
        // Tuple fields: version = the server clock value when this op was applied,
        //               op      = the (possibly transformed) TextOp that was applied,
        //               clientId = who submitted it (for auditing and tiebreaking).
        //
        // ── RUNTIME SNAPSHOT (same scenario + carol's NoOp) ──
        //   _opLog = [
        //       (1, Insert(pos=2,'!'),  "alice"),
        //       (2, Insert(pos=0,'W'),  "bob"  ),
        //       (3, NoOp,               "carol" )   ← carol's delete was already done by bob
        //   ]
        //
        //   GetOpsSince(0) → [(1,Insert), (2,Insert), (3,NoOp)]  ← brand-new client
        //   GetOpsSince(1) → [(2,Insert), (3,NoOp)]              ← alice catching up
        //   GetOpsSince(2) → [(3,NoOp)]                          ← bob catching up
        //   GetOpsSince(3) → []                                  ← fully synced
        private readonly List<(int version, TextOp op, string clientId)> _opLog = [];

        // Exposes the current text snapshot for read-only access by CollabServer
        // (e.g., to send to a freshly connecting client as their initial state).
        public string Text => _text.ToString();

        // Exposes the current version for CollabServer to include in Acks and to
        // tell new clients "start your local counter here."
        public int Version => _version;

        public ServerDocument(string initialText = "")
        {
            _text = new StringBuilder(initialText);
        }

        // Applies op to the text buffer and records it in the log.
        // Called by CollabServer AFTER OTEngine has already transformed the op
        // against all concurrent server ops — so by the time we get here, op.Position
        // is already correct for the current _text. Returns the new version so
        // CollabServer can send "your op landed at version N" back to the client.
        //
        // NoOps skip the buffer mutation but still advance the version and enter
        // the log — the client that submitted the NoOp needs an Ack with a version
        // number so it can release its pending-op queue and advance its local clock.
        public int Apply(TextOp op, string clientId)
        {
            if (!op.IsNoOp) ApplyToBuffer(op);
            _version++;
            _opLog.Add((_version, op, clientId));
            return _version;
        }

        // Returns all ops applied strictly after afterVersion, in ascending version
        // order. Called by CollabServer in two scenarios:
        //   1. Client reconnects: "I'm at v=5, what did I miss?" → GetOpsSince(5)
        //   2. Before applying a new op: "what server ops arrived since this client's
        //      base version?" → CollabServer transforms the op against these before Apply.
        //
        // Strict greater-than (not >=): a client at version 5 already has op 5
        // applied locally — they need ops 6, 7, 8 …, not op 5 again.
        public List<(int version, TextOp op)> GetOpsSince(int afterVersion) =>
            _opLog.Where(e => e.version > afterVersion)
                  .Select(e => (e.version, e.op))
                  .ToList();

        // Mutates _text in place. Only called from Apply after the IsNoOp guard,
        // so every op here is either an Insert or a Delete.
        //
        // Math.Min clamp on Insert: valid OT should never produce a position beyond
        // the string length, but a defensive clamp is cheaper than an IndexOutOfRange
        // exception in the rare edge case of extreme concurrency or a buggy client.
        //
        // Delete guard (op.Position < _text.Length): same reasoning — a well-formed
        // transformed Delete should always be in range, but we prefer a silent no-op
        // over a crash if something slips through.
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
}
