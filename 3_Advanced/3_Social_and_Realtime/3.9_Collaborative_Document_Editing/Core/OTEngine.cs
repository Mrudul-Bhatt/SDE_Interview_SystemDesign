// OTEngine — the heart of Operational Transformation.
//
// Transform(op1, op2) returns op1 adjusted as if op2 was already applied to the
// document. This is what makes concurrent edits converge: when two clients
// generate ops at the same version, each client transforms incoming ops against
// its local pending ops, and the server transforms each incoming op against all
// server ops that happened since the client's base version.
//
// Tie-breaking by ClientId is critical for determinism — when two concurrent
// inserts target the same position, ALL participants must agree on the order.

namespace AdvancedDesigns
{
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
                // Tie-break by clientId for deterministic ordering across all participants
                if (string.Compare(op2.ClientId, op1.ClientId, StringComparison.Ordinal) < 0)
                    return new TextOp(OpKind.Insert, op1.Position + 1, op1.Character, op1.ClientId, op1.ClientVersion);
            }

            return op1; // op2 was at or after op1's position — no shift needed
        }

        private static TextOp TransformInsertDelete(TextOp op1, TextOp op2)
        {
            // op2 (delete at p2) was applied first — a slot before us was freed
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Insert, op1.Position - 1, op1.Character, op1.ClientId, op1.ClientVersion);
            return op1;
        }

        private static TextOp TransformDeleteInsert(TextOp op1, TextOp op2)
        {
            // op2 (insert at p2) was applied first — our delete shifts right if insert was at/before us
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
                return TextOp.Noop(); // character already deleted by op2 — must not delete the next char by mistake

            return op1; // op2 was after op1 — no effect
        }
    }
}
