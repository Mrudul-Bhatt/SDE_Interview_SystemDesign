// ServerDocument — the authoritative document state plus the immutable op log.
//
// The op log IS the source of truth — the text buffer is just a derived cache.
// In production this log would live in Cassandra (append-only, indexed by version),
// with periodic snapshots so we don't replay millions of ops on every load.
//
// GetOpsSince is what lets a reconnecting client catch up: "I'm at v=42; give me
// everything since then so I can apply (with transforms) and reach the current state."

namespace AdvancedDesigns
{
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

        // Get all ops after a given version (for client catch-up / server transform).
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
}
