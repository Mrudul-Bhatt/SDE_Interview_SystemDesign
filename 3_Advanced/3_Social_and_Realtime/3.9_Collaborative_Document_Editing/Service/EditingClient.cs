// EditingClient — the per-user editor with optimistic local apply.
//
// The key trick is "optimistic UI": local edits are applied instantly so typing
// feels zero-latency. The op is queued for the server, and we track which ops
// are still pending (un-ACKed).
//
// When a remote op arrives:
//   - If it's our own op echoed back → dequeue from pending, advance version.
//   - Otherwise → transform it against each of our pending local ops, AND
//     transform each pending op against the incoming one. This double-transform
//     keeps both the local view AND the pending queue consistent so future ops
//     stay aligned with the server's evolving state.

namespace AdvancedDesigns
{
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

        // User types — apply locally and queue for server (optimistic apply).
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

        // Server broadcasts a remote op — transform against pending local ops, then apply.
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
}
