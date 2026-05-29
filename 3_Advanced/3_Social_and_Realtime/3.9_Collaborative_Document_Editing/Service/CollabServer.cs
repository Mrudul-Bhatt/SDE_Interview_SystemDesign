// CollabServer — receives ops from clients, transforms, applies, broadcasts.
//
// Receive pipeline (the critical path):
//   1. Look up all server ops since the client's base version.
//   2. Transform the incoming op against each one in order.
//      → If any transform produces NoOp (e.g., both clients deleted the same char),
//        we stop early — there's nothing left to apply.
//   3. Apply the transformed op to ServerDocument (assigns a new version).
//   4. Broadcast the transformed op to every registered client.
//
// In production this server would be sharded by doc_id so a single server owns
// each document — that single-writer constraint is what makes the transform chain
// deterministic.

namespace AdvancedDesigns
{
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
}
