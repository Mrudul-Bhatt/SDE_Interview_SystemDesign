// TextOp — the atomic edit unit in our OT system.
//
// THE BIG IDEA:
// Think of a TextOp like a surgical instruction handed to a document:
//   Insert(pos=3, 'x') → "slide everything from index 3 rightward and put 'x' there"
//   Delete(pos=3)       → "remove whichever character currently sits at index 3"
//
// Every keystroke the user makes becomes exactly one TextOp. The OT engine
// transforms pairs of concurrent TextOps against each other so that, no matter
// what order they arrive at the server or peer clients, every copy of the document
// ends up identical.
//
// WHY POSITION IS AN INDEX INTO THE CURRENT STRING (not an absolute character ID):
// We intentionally keep ops simple — Position is a live index into the document as
// the client saw it at ClientVersion. This is what makes transformation necessary:
// if Alice inserts at pos=5 and Bob concurrently deletes at pos=3, Bob's delete
// shifts everything left by one, so Alice's insert must be retargeted to pos=4.
// The OT transform step does exactly that adjustment before applying Alice's op.
//
// WHY ClientVersion (not a timestamp):
// Wall-clock timestamps drift between machines and can collide. ClientVersion is
// the server document version the client was on when it typed the op — an integer
// that is unambiguous and cheap to compare. The server uses it to find every op
// committed AFTER that version; those are the concurrent ops it must transform
// this new op against before applying it.

namespace AdvancedDesigns
{
    // The three states an op can be in.
    // NoOp is not produced directly by users — it emerges from the transform step
    // when two ops cancel each other out (e.g., two clients delete the same char).
    public enum OpKind { Insert, Delete, NoOp }

    public class TextOp
    {
        // ── RUNTIME SNAPSHOT — what one populated instance holds ──
        //
        //   Scenario 1 — Alice inserts 'x' at position 3 (she was on server version 5):
        //       Kind           = OpKind.Insert
        //       Position       = 3              ← insert BEFORE the char currently at index 3
        //       Character      = 'x'
        //       ClientId       = "alice"
        //       ClientVersion  = 5              ← Alice was on doc version 5 when she typed
        //       ToString()     → "Insert(pos=3, 'x')"
        //
        //   Scenario 2 — Bob deletes the character at position 1 (also on version 5):
        //       Kind           = OpKind.Delete
        //       Position       = 1              ← remove the char currently at index 1
        //       Character      = '\0'           ← unused for Delete; position alone identifies the target
        //       ClientId       = "bob"
        //       ClientVersion  = 5              ← same version as Alice → these two ops are concurrent
        //       ToString()     → "Delete(pos=1)"
        //
        //   Scenario 3 — NoOp produced by the transform engine:
        //       Kind           = OpKind.NoOp
        //       Position       = -1             ← sentinel; never used as a document index
        //       Character      = '\0'
        //       ClientId       = ""
        //       ClientVersion  = 0
        //       ToString()     → "NoOp"
        //
        //   WHAT HAPPENS WHEN ALICE'S AND BOB'S OPS ABOVE MEET AT THE SERVER:
        //   Both have ClientVersion=5, so the server sees them as concurrent.
        //   Bob's Delete(pos=1) is committed first (e.g., arrived first).
        //   Before applying Alice's Insert(pos=3), the server transforms it against
        //   Bob's committed delete: that delete shifted everything at pos≥1 one step
        //   left, so Alice's target moves from pos=3 → pos=2.
        //   Transformed op applied to document: Insert(pos=2, 'x').
        //   Both clients converge on the same final string — Bob's char removed,
        //   Alice's 'x' inserted at the position-adjusted index.

        // What kind of edit this is. Determines which transform path runs and
        // whether Character carries meaningful data.
        public OpKind Kind { get; }

        // 0-based character index into the document string as seen by the client
        // at ClientVersion.
        //   Insert: the new character is placed BEFORE the character currently at
        //           this index (or at the end if Position == document.Length).
        //   Delete: the character currently AT this index is removed.
        //   NoOp:   Position is -1 (sentinel — never used as an index).
        public int Position { get; }

        // The character to insert. Only meaningful for Insert ops.
        // Delete and NoOp leave this as '\0' (the default) — the position alone
        // identifies which character to remove; no payload is needed.
        public char Character { get; }

        // Which client authored this op. Used for tie-breaking during transform:
        // when two ops land at the exact same position, the one from the
        // lexicographically smaller ClientId is applied first, giving both sides
        // a deterministic ordering without any network round-trip.
        public string ClientId { get; }

        // The server document version this client was on when it generated the op.
        // The server compares this against its own committed-op log to find every
        // op that happened concurrently (i.e., was committed after this version)
        // and transforms this op through each of them before applying it.
        // Example: if the server is at version 7 and ClientVersion=5, the server
        // must transform this op against the two ops at versions 6 and 7.
        public int ClientVersion { get; }

        public TextOp(OpKind kind, int position, char character = '\0',
            string clientId = "", int clientVersion = 0)
        {
            Kind = kind;
            Position = position;
            Character = character;  // '\0' is fine for Delete/NoOp — never read
            ClientId = clientId;
            ClientVersion = clientVersion;
        }

        // Factory for the "do nothing" outcome of a transform.
        // Returned when two concurrent ops cancel each other — most commonly when
        // both clients deleted the very same character. Applying a NoOp to the
        // document is a safe, explicit no-op rather than a conditional branch at
        // every call site.
        public static TextOp Noop() => new TextOp(OpKind.NoOp, -1);

        // Quick guard used throughout the engine to skip application and further
        // transformation of an already-nullified op.
        public bool IsNoOp => Kind == OpKind.NoOp;

        public override string ToString() =>
            Kind == OpKind.Insert ? $"Insert(pos={Position}, '{Character}')"
          : Kind == OpKind.Delete ? $"Delete(pos={Position})"
          : "NoOp";
    }
}
