// TextOp — the atomic edit unit in our OT system.
//
// Every keystroke becomes one TextOp. The OT engine transforms ops against each
// other so all clients converge, regardless of the order edits arrive.
//
// ClientVersion is the version the client was at WHEN it generated the op — used
// by the server to know which concurrent ops to transform against.

namespace AdvancedDesigns
{
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

        // NoOp is the result of transforming an op that's been nullified by a
        // concurrent op (e.g., both clients deleted the same character).
        public static TextOp Noop() => new TextOp(OpKind.NoOp, -1);

        public bool IsNoOp => Kind == OpKind.NoOp;

        public override string ToString() =>
            Kind == OpKind.Insert ? $"Insert(pos={Position}, '{Character}')"
          : Kind == OpKind.Delete ? $"Delete(pos={Position})"
          : "NoOp";
    }
}
