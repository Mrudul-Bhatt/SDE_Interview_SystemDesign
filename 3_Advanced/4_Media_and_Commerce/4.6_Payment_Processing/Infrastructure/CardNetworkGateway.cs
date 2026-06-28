// CardNetworkGateway — our boundary to the card networks (Visa / Mastercard / issuing banks).
//
// THE BIG IDEA:
// This is the one component that actually talks to the outside money rails. Authorize asks the
// issuing bank to place a hold on the card and returns an authRef; Capture and Refund later quote
// that authRef to act on the same hold. In real life each call is a 1-3 second network round-trip
// — the slowest leg of the whole flow, which is why fraud scoring (cheap, local) runs first.
//
// WHY authRef IS THE THREAD: the bank identifies the hold, not us. We store authRef on the Payment
// and pass it back for Capture/Refund so every action targets the correct authorization.
//
// SIMULATED HERE: any PAN in the constructor's declinedPans set fails Authorize; Capture and
// Refund always succeed. A real gateway must also handle "auth expired", "card cancelled", and
// "insufficient funds" at each stage — capture can fail even after a successful authorize.
//
// HOW IT BEHAVES AT RUNTIME (constructed with "4111111111111111" as the declined PAN):
//
//   Call                                   | Returns
//   ---------------------------------------|----------------------------------
//   Authorize(goodPan, 10000)  [Scen 1]    | (true,  "AUTH_1A2B", null)
//   Authorize(declinedPan, 3000) [Scen 4]  | (false, null, "CARD_DECLINED")
//   Capture("AUTH_1A2B", 10000)            | (true,  null)
//   Refund("AUTH_1A2B", 5000)  [Scen 5]    | (true,  "REF_9C8D", null)

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

public class CardNetworkGateway
{
    // PANs the simulated bank will decline. In reality the bank decides; we never know in advance.
    private readonly HashSet<string> _declinedPans;

    public CardNetworkGateway(params string[] declinedPans) => _declinedPans = [.. declinedPans];

    // Ask the issuing bank to hold funds. Returns an authRef the later Capture/Refund must quote.
    // The slow remote leg of the flow — runs only after fraud Allow and a valid token.
    public (bool success, string authRef, string error) Authorize(string pan, long amountCents)
    {
        if (_declinedPans.Contains(pan))
            return (false, null, "CARD_DECLINED");
        var authRef = "AUTH_" + Convert.ToHexString(RandomBytes(4));
        return (true, authRef, null);
    }

    // Convert a hold into an actual charge. Always succeeds here; real systems can fail with
    // auth-expired etc., which is why PaymentService checks the result before writing the ledger.
    public (bool success, string error) Capture(string authRef, long amountCents) => (true, null);

    // Return money for a captured charge. Returns a refundRef for the merchant's records.
    public (bool success, string refundRef, string error) Refund(string authRef, long amountCents) =>
        (true, "REF_" + Convert.ToHexString(RandomBytes(4)), null);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n]; RandomNumberGenerator.Fill(b); return b;
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // WHAT _declinedPans HOLDS AT RUNTIME (snapshot for the whole demo):
    //
    //   _declinedPans (set of PANs the simulated bank rejects) = {
    //
    //      "4111111111111111"   // wired in Program: new CardNetworkGateway("4111111111111111")
    //   }
    //
    // This set is fixed at construction and never changes. It's ONLY a test seam — it lets the
    // demo force a decline (Scenario 4 charges the card behind declinedToken, whose PAN is in
    // this set, so Authorize returns CARD_DECLINED). Every other PAN authorizes successfully.
    //
    // In reality there is no such list — the issuing bank decides accept/decline per request and
    // we only learn the outcome from its response. Capture and Refund hold no state at all; they
    // just echo success in this simulation.
    // ──────────────────────────────────────────────────────────────────────────────────
}
