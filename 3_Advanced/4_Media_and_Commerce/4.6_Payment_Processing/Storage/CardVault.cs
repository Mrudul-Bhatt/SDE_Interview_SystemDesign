// CardVault — the only component that ever holds a raw card number (PAN).
//
// THE BIG IDEA:
// Raw card numbers are radioactive: any system that touches them falls under the full weight of
// PCI DSS compliance. So we isolate them HERE and nowhere else. The vault swaps a PAN for an
// opaque token ("tok_abc123") and hands that to the rest of the system. Even if the main payment
// database leaks, the attacker gets tokens — useless without the vault. This is "scope reduction":
// shrink the blast radius to one tightly-controlled component.
//
// WHY TOKENS INSTEAD OF THE PAN EVERYWHERE: Payment, ChargeRequest, logs, the ledger — all store
// the token. Only Detokenize (called once, during Authorize) ever turns it back into a PAN, and
// only inside the vault's network segment.
//
// In production: PANs are AES-256 encrypted at rest under HSM-managed keys, every access is
// audit-logged, keys rotate quarterly, and the PAN is NEVER written to a log line.
//
// HOW IT BEHAVES AT RUNTIME (Program wires up two test cards):
//
//   Call                              | Returns / effect
//   ----------------------------------|------------------------------------------
//   Tokenize("4000000000000000")      | "tok_9f3a..."; _tokenToPan[tok] = the PAN
//   IsValid("tok_9f3a...")            | true  (used by Charge step 2 to reject junk tokens)
//   Detokenize("tok_9f3a...")         | "4000000000000000"  (only during Authorize)
//   IsValid("tok_bogus")              | false

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

public class CardVault
{
    // token -> PAN. In production this is encrypted under an HSM key and never logged.
    private readonly Dictionary<string, string> _tokenToPan = [];

    // Swap a raw PAN for a random opaque token. The token carries no information about the PAN,
    // so it's safe to store and pass around outside the vault.
    public string Tokenize(string pan)
    {
        var token = "tok_" + Convert.ToHexString(RandomBytes(8)).ToLower();
        _tokenToPan[token] = pan;  // encrypted at rest in a real system
        return token;
    }

    // Reverse a token to its PAN. Called ONLY by the payment engine during authorization,
    // inside the vault's isolated segment. Returns null for an unknown token.
    public string Detokenize(string token) => _tokenToPan.TryGetValue(token, out var pan) ? pan : null;

    // Cheap existence check so Charge can reject a bad/expired token before doing real work.
    public bool IsValid(string token) => _tokenToPan.ContainsKey(token);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT _tokenToPan HOLDS AT RUNTIME (snapshot after all of Program.cs has run):
    //
    //   _tokenToPan (token -> raw PAN) = {
    //
    //      // goodToken — the valid test card used by Scenarios 1, 2, 3, 5
    //
    //      "tok_9f3a1b2c..."  ->  "4000000000000000"
    //
    //
    //      // declinedToken — the card the bank rejects, used by Scenario 4
    //
    //      "tok_7d8e9f0a..."  ->  "4111111111111111"
    //   }
    //
    // Only two cards are ever tokenized in the demo. Every other store and log holds only the
    // opaque "tok_..." values — this dictionary is the SINGLE place the raw card numbers exist
    // (encrypted under an HSM key in production). A leak of any other store reveals tokens, not PANs.
    // ──────────────────────────────────────────────────────────────────────────────────
}
