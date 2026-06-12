// OTEngine — the heart of Operational Transformation.
//
// THE BIG IDEA:
// Transform(op1, op2) answers one question: "If op2 was already applied to the
// document, where does op1 need to land to achieve its original intent?"
// The answer is always a new TextOp with an adjusted Position (or a NoOp when
// op1's target was wiped out by op2).
//
// WHY THIS IS NECESSARY:
// Two clients — Alice and Bob — both read the document at version 5. Alice types
// a character; Bob deletes one. Their ops are generated against the SAME snapshot,
// so they carry positions valid for that snapshot. But when they reach the server
// at different times, one op is applied first and changes the string. The second
// op's position now points at the wrong character. OT corrects the pointer before
// application so both clients converge on the same final string.
//
// THE FOUR CASES — one for each (op1.Kind, op2.Kind) pair:
//   Insert vs Insert → did op2 shift the string left of op1's target? → shift right
//   Insert vs Delete → did op2 shrink the string left of op1's target? → shift left
//   Delete vs Insert → did op2 grow the string at or left of op1's target? → shift right
//   Delete vs Delete → did op2 delete the very same char op1 wanted? → NoOp
//
// TIE-BREAKING BY ClientId:
// When two concurrent inserts aim at exactly the same position, every participant
// (server AND all clients) must agree on who "wins" the slot. Lexicographic
// ClientId comparison is the tiebreaker — it's cheap, deterministic, and needs
// no coordination. The client with the smaller ClientId gets the lower position;
// the other shifts one step right.

using System;

namespace AdvancedDesigns
{
    public static class OTEngine
    {
        // ── RUNTIME SNAPSHOT — what Transform returns for each branch ──
        //
        //   Setup: document = "Hello"
        //          char positions:  0='H'  1='e'  2='l'  3='l'  4='o'
        //          Both ops are concurrent (same ClientVersion).
        //
        //   CASE A — InsertInsert, op2 is to the LEFT of op1:
        //       op1 = Insert(pos=4, '!', clientId="alice")   ← wants '!' before 'o'
        //       op2 = Insert(pos=1, 'i', clientId="bob")     ← wants 'i' before 'e'  (committed first)
        //       After op2: "Hiello"  — 'o' shifted from index 4 → index 5
        //       Transform(op1, op2) → Insert(pos=5, '!')     ← pos + 1
        //       Final document: "Hiell!o"  ✓
        //
        //   CASE B — InsertInsert, SAME position, tie-break by ClientId:
        //       op1 = Insert(pos=2, 'X', clientId="charlie")
        //       op2 = Insert(pos=2, 'Y', clientId="alice")   ← committed first
        //       "alice" < "charlie" → alice wins the slot; charlie shifts right
        //       Transform(op1, op2) → Insert(pos=3, 'X')
        //       Final document: "HeYXllo"  ✓  (identical result on every machine)
        //
        //   CASE C — InsertDelete, op2 (delete) is to the LEFT of op1 (insert):
        //       op1 = Insert(pos=3, '!', clientId="alice")   ← wants '!' before 2nd 'l'
        //       op2 = Delete(pos=1,      clientId="bob")     ← deletes 'e'  (committed first)
        //       After op2: "Hllo"  — 2nd 'l' shifted from index 3 → index 2
        //       Transform(op1, op2) → Insert(pos=2, '!')    ← pos - 1
        //       Final document: "Hl!lo"  ✓
        //
        //   CASE D — DeleteInsert, op2 (insert) is AT op1's (delete) position:
        //       op1 = Delete(pos=2, clientId="alice")        ← wants to remove 1st 'l'
        //       op2 = Insert(pos=2, 'Z', clientId="bob")     ← inserts 'Z' before 1st 'l' (committed first)
        //       After op2: "HeZllo"  — 1st 'l' shifted from index 2 → index 3
        //       Transform(op1, op2) → Delete(pos=3)         ← pos + 1  (≤ rule: equal counts)
        //       Final document: "HeZlo"  ✓
        //
        //   CASE E — DeleteDelete, SAME position (both targeted the same char):
        //       op1 = Delete(pos=2, clientId="alice")
        //       op2 = Delete(pos=2, clientId="bob")          ← committed first; 'l' is gone
        //       Transform(op1, op2) → NoOp                  ← char already removed; nothing to do
        //       Final document: "Helo"  ✓  (one 'l' removed, not two)

        // Transform op1 as if op2 was already applied to the document.
        // Returns op1 with a corrected Position (or NoOp if op1's target was wiped out).
        // Early-exit for either operand being a NoOp — a NoOp transforms to itself.
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

        // op2 inserted a character and was already applied.
        // If op2's insertion landed to the LEFT of op1's target, it shifted every
        // subsequent character one slot right — so op1 must aim one slot further right.
        // If they collide at the SAME slot, ClientId decides who occupies it:
        //   smaller ClientId wins the slot → the other op shifts one slot right.
        // If op2 was to the RIGHT of op1, no characters between them moved — op1 is unchanged.
        private static TextOp TransformInsertInsert(TextOp op1, TextOp op2)
        {
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Insert, op1.Position + 1, op1.Character, op1.ClientId, op1.ClientVersion);

            if (op2.Position == op1.Position)
            {
                // Lexicographic comparison: if op2.ClientId sorts before op1.ClientId,
                // op2 is assigned the lower slot — op1 must move one to the right.
                // This rule is identical on the server and every client, so the
                // document converges to the same string everywhere without coordination.
                if (string.Compare(op2.ClientId, op1.ClientId, StringComparison.Ordinal) < 0)
                    return new TextOp(OpKind.Insert, op1.Position + 1, op1.Character, op1.ClientId, op1.ClientVersion);
            }

            // op2 was at or after op1's position — nothing between them changed.
            return op1;
        }

        // op2 deleted a character and was already applied.
        // A deletion to the LEFT of op1's insert shrinks the string by one slot,
        // pulling op1's target one position closer to the start.
        // If op2's deletion was at or to the RIGHT of op1's insert, no characters
        // before op1's target were disturbed — op1's position is still valid.
        private static TextOp TransformInsertDelete(TextOp op1, TextOp op2)
        {
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Insert, op1.Position - 1, op1.Character, op1.ClientId, op1.ClientVersion);

            // op2 deleted at or after op1's position — op1's slot is undisturbed.
            return op1;
        }

        // op2 inserted a character and was already applied.
        // Any insertion AT OR BEFORE op1's delete target pushes that target one slot
        // to the right — op1 must follow it.
        // Note the "<=" (not just "<"): if op2 inserts exactly at op1's position, the
        // new character occupies that slot and the char op1 wanted to delete shifts right.
        // If op2 inserted strictly to the RIGHT, op1's target didn't move.
        private static TextOp TransformDeleteInsert(TextOp op1, TextOp op2)
        {
            if (op2.Position <= op1.Position)
                return new TextOp(OpKind.Delete, op1.Position + 1, clientId: op1.ClientId, clientVersion: op1.ClientVersion);

            // op2 inserted after op1's target — no shift needed.
            return op1;
        }

        // Both ops want to delete a character.
        // If op2 deleted to the LEFT of op1's target, op1's target shifted one slot left.
        // If op2 deleted the SAME character op1 wanted, op1 has nothing left to do — NoOp.
        //   (Without this guard, op1 would delete the character that now sits at that
        //    index — the wrong char — producing a corrupted document.)
        // If op2 deleted to the RIGHT of op1's target, op1's target is unchanged.
        private static TextOp TransformDeleteDelete(TextOp op1, TextOp op2)
        {
            if (op2.Position < op1.Position)
                return new TextOp(OpKind.Delete, op1.Position - 1, clientId: op1.ClientId, clientVersion: op1.ClientVersion);

            if (op2.Position == op1.Position)
                return TextOp.Noop(); // target already gone — nothing to delete

            // op2 was after op1's target — no effect on op1's position.
            return op1;
        }
    }
}
