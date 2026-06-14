// EditingClient — the per-user editor: applies edits instantly and stays in sync
// with the server through optimistic apply and double-transform on remote ops.
//
// THE BIG IDEA:
// Think of EditingClient like a musician improvising in a live band. When you play
// a note (LocalInsert/LocalDelete), you don't pause until the bandleader approves —
// you play it immediately and keep a mental note of what the rest of the band hasn't
// heard yet (pending ops). When another musician plays something you didn't expect
// (ReceiveRemoteOp), you adjust your next planned notes so they still harmonize
// (transform pending ops), and you also adjust where their note lands relative to
// what you've already played (transform incoming op). Everyone hears the same song.
//
// WHY OPTIMISTIC APPLY (not wait for server ACK before showing the keystroke):
// A server round-trip takes 100–300 ms. If every keystroke waited for the ACK before
// appearing on screen, typing would feel like sending telegrams. Optimistic apply
// shows the character instantly. If the server ever rejects an op (bad auth, rate
// limit) the client rolls back — but that is rare. The normal path is: type, see,
// server ACKs, move on.
//
// WHY A PENDING QUEUE (not just a count of unACKed ops):
// When a remote op arrives, the client must transform the incoming op to land
// correctly ON TOP OF the pending local ops — and must transform each pending op
// to remain valid AFTER the remote op. Both transformations require knowing the
// exact ops in the pending set (their kind and position), not just how many there are.
//
// THE DOUBLE-TRANSFORM on remote op receipt:
// Two separate adjustments must happen simultaneously:
//
//   ① incoming = Transform(incoming, pendingOp_i)
//      The remote op's position is valid for the SERVER'S text. But our local text
//      also has pendingOp_i applied on top. To land correctly in our local view,
//      the remote op must hop over each pending op.
//
//   ② pendingOp_i = Transform(pendingOp_i, ORIGINAL serverOp)
//      Each pending op will eventually be sent to the server. By then, the server
//      will have applied serverOp. So we pre-adjust each pending op against the
//      ORIGINAL serverOp now — not against the progressively-shifted `incoming`,
//      because every pending op shares the same server base (one new server op
//      arrived; each pending op adjusts for that one op, not for each other).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AdvancedDesigns
{
    public class EditingClient
    {
        // This client's stable identity. Used in two places:
        //   1. Stamped on every outgoing TextOp so OTEngine can break ties when two
        //      inserts target the same position (lexicographic ClientId comparison).
        //   2. Checked in ReceiveRemoteOp to detect own-op echoes (see that method).
        public string ClientId { get; }

        // The optimistically-updated local text buffer — may be AHEAD of the server
        // by however many pending ops are in the queue. This is what the user sees
        // on screen. StringBuilder is used for in-place Insert/Remove without
        // allocating a new string per keystroke.
        //
        // ── RUNTIME SNAPSHOT (Alice has typed two chars the server hasn't ACKed yet) ──
        //   Server state (v=0): "Hello"
        //   Alice types '!' at pos=5:  _localText = "Hello!"   pending=[Insert(5,'!')]
        //   Alice types 'W' at pos=0:  _localText = "WHello!"  pending=[Insert(5,'!'), Insert(0,'W')]
        //
        //   The server is still at "Hello" — Alice is 2 ops ahead of it locally.
        private readonly StringBuilder _localText;

        // The last server version this client knows about. Advanced in two cases:
        //   - Own-op ACK arrives: server confirmed our op landed at version N → _serverVersion = N
        //   - Remote op arrives:  server confirmed someone else's op → _serverVersion = N
        //
        // Stamped as ClientVersion on every outgoing TextOp so CollabServer can call
        // GetOpsSince(_serverVersion) and find exactly which server ops arrived
        // concurrently with this client's submission.
        //
        // ── RUNTIME SNAPSHOT ──
        //   After joining at v=0:    _serverVersion = 0
        //   After Bob's op at v=1:   _serverVersion = 1   ← updated in ReceiveRemoteOp
        //   After own ACK at v=2:    _serverVersion = 2   ← updated in own-op branch
        private int _serverVersion;

        // Ops applied locally but not yet ACKed by the server. Every local edit
        // (LocalInsert / LocalDelete) appends here. Every own-op ACK dequeues the
        // front. Each remote op arrival shifts every entry's position to remain valid
        // once the remote op is applied at the server.
        //
        // WHY QUEUE (FIFO): ops must be ACKed in the order they were submitted.
        // The server processes them sequentially; the first-submitted op is always
        // the first ACK to arrive, so we always dequeue from the front.
        //
        // ── RUNTIME SNAPSHOT (Alice has two pending ops, Bob's remote arrives) ──
        //   Before Bob's op:
        //       _pendingOps = [ Insert(5,'!'), Insert(0,'W') ]
        //                       ↑ server hasn't seen either
        //
        //   Bob's Insert(0,'W') arrives at v=1:
        //       double-transform adjusts each pending op for Bob's prepend:
        //       _pendingOps = [ Insert(6,'!'), Insert(1,'W') ]
        //                       ↑ both shifted right by 1 to account for Bob's 'W' at pos=0
        //
        //   Alice's Insert(6,'!') ACK arrives at v=2:
        //       _pendingOps = [ Insert(1,'W') ]   ← front dequeued
        private readonly Queue<TextOp> _pendingOps = new();

        // Append-only event log for debugging and test assertions.
        // In production this is structured telemetry, not a list.
        private readonly List<string> _log = [];

        public string LocalText => _localText.ToString();
        public int ServerVersion => _serverVersion;

        public EditingClient(string clientId, string initialText, int initialVersion = 0)
        {
            ClientId = clientId;
            _localText = new StringBuilder(initialText);
            _serverVersion = initialVersion;
        }

        // Applies the insert to the local buffer immediately (optimistic apply) and
        // enqueues the op for server submission. The op carries _serverVersion so
        // CollabServer knows which server state this insert was generated against.
        // Returns the op so the caller can send it to CollabServer.ReceiveOp.
        //
        // safePos clamp: same defensive guard as ServerDocument.ApplyToBuffer —
        // valid callers never exceed _localText.Length, but clamping is cheaper than
        // an IndexOutOfRange in any edge case.
        public TextOp LocalInsert(int position, char ch)
        {
            int safePos = Math.Min(position, _localText.Length);
            _localText.Insert(safePos, ch);
            var op = new TextOp(OpKind.Insert, safePos, ch, ClientId, _serverVersion);
            _pendingOps.Enqueue(op);
            Log($"{ClientId} local insert: '{ch}' at {safePos} → \"{_localText}\"");
            return op;
        }

        // Applies the delete to the local buffer immediately and enqueues for server.
        // Returns NoOp (without enqueuing) if position is out of range — a delete
        // beyond the string end is a no-op rather than an exception, consistent with
        // how OTEngine treats a delete whose target was already removed.
        public TextOp LocalDelete(int position)
        {
            if (position >= _localText.Length) return TextOp.Noop();
            _localText.Remove(position, 1);
            var op = new TextOp(OpKind.Delete, position, clientId: ClientId, clientVersion: _serverVersion);
            _pendingOps.Enqueue(op);
            Log($"{ClientId} local delete: pos={position} → \"{_localText}\"");
            return op;
        }

        // Handles a broadcast from CollabServer: either an ACK for our own op,
        // or a remote edit from another client that we must integrate.
        //
        // OWN-OP ACK PATH (serverOp.ClientId == this.ClientId):
        //   The server echoes every op back to all clients, including the submitter.
        //   For the submitter, this echo is purely an ACK — the local text already
        //   has the change applied (optimistic). We just dequeue the front of pending
        //   and advance _serverVersion. No transform, no local text change.
        //
        // REMOTE OP PATH — double-transform (see header for full explanation):
        //
        // ── RUNTIME SNAPSHOT — Alice has one pending op when Bob's op arrives ──
        //
        //   Setup:  doc="Hello" (v=0), Alice typed '!' at pos=5
        //       Alice: _localText="Hello!"  _serverVersion=0  _pendingOps=[Insert(5,'!')]
        //
        //   Bob's op arrives: serverOp=Insert(pos=0,'W') at newVersion=1
        //       (Bob prepended 'W' to "Hello"; server is now at "WHello" v=1)
        //
        //   Double-transform loop (i=0, pendingList[0]=Insert(5,'!')):
        //     ① incoming = Transform(Insert(0,'W'), Insert(5,'!'))
        //            op2.pos=5 NOT < op1.pos=0 → no shift → incoming = Insert(0,'W')
        //            (Bob's 'W' at pos=0 is unaffected by Alice's '!' at pos=5)
        //     ② pending[0] = Transform(Insert(5,'!'), serverOp=Insert(0,'W'))
        //            op2.pos=0 < op1.pos=5 → shift right → pending[0] = Insert(6,'!')
        //            (Alice's '!' must move from pos=5 to pos=6 because Bob's 'W' now occupies pos=0)
        //
        //   Apply incoming Insert(0,'W') to _localText "Hello!":
        //       _localText = "WHello!"
        //
        //   After: _serverVersion=1  _pendingOps=[Insert(6,'!')]  _localText="WHello!"
        //
        //   Alice now submits Insert(6,'!') with clientVersion=1 →
        //       server GetOpsSince(1)=[] → no transforms → Apply → "WHello!" v=2
        //   Everyone converges to "WHello!" ✓
        public void ReceiveRemoteOp(TextOp serverOp, int newServerVersion)
        {
            if (serverOp.ClientId == ClientId)
            {
                // Own-op ACK: text already applied locally, just sync version and release pending.
                if (_pendingOps.Count > 0) _pendingOps.Dequeue();
                _serverVersion = newServerVersion;
                Log($"{ClientId} ACK own op, server now v={newServerVersion}");
                return;
            }

            // Remote op: double-transform against every pending local op.
            TextOp incoming = serverOp;
            var pendingList = _pendingOps.ToList();
            for (int i = 0; i < pendingList.Count; i++)
            {
                // ① Shift the remote op to land correctly over pending[i].
                incoming = OTEngine.Transform(incoming, pendingList[i]);
                // ② Shift pending[i] to remain valid after the ORIGINAL server op.
                //    Uses serverOp (original), not incoming (already shifted), because
                //    every pending op adjusts for the same one new server op — not for
                //    each other's progressive shifts.
                pendingList[i] = OTEngine.Transform(pendingList[i], serverOp);
            }

            // Rebuild pending queue with the position-adjusted ops.
            _pendingOps.Clear();
            foreach (var p in pendingList) _pendingOps.Enqueue(p);

            _serverVersion = newServerVersion;

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

        // Mutates _localText in place. Identical defensive guards to ServerDocument.ApplyToBuffer:
        // clamp Insert position and range-check Delete so a misbehaving transform never throws.
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

        public IReadOnlyList<string> GetLog() => _log;
        private void Log(string msg) => _log.Add(msg);
    }
}
