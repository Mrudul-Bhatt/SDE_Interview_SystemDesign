// CardNetworkGateway — the boundary between us and Visa/Mastercard/etc.
//
// Authorize asks the issuing bank to place a hold on the card; the returned
// authRef is what we use later for Capture and Refund. In real life this is a
// remote call with a 1-3 second roundtrip — the slow leg of the entire flow.
//
// Simulated here: any PAN passed to the constructor's declinedPans list will
// fail Authorize. Capture and Refund always succeed in this simulation; real
// systems must handle "auth expired", "card cancelled", "insufficient funds"
// errors at each stage.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

public class CardNetworkGateway
{
    private readonly HashSet<string> _declinedPans;

    public CardNetworkGateway(params string[] declinedPans) =>
        _declinedPans = new HashSet<string>(declinedPans);

    public (bool success, string authRef, string error) Authorize(string pan, long amountCents)
    {
        if (_declinedPans.Contains(pan))
            return (false, null, "CARD_DECLINED");
        var authRef = "AUTH_" + Convert.ToHexString(RandomBytes(4));
        return (true, authRef, null);
    }

    public (bool success, string error) Capture(string authRef, long amountCents) =>
        (true, null);

    public (bool success, string refundRef, string error) Refund(string authRef, long amountCents) =>
        (true, "REF_" + Convert.ToHexString(RandomBytes(4)), null);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n]; RandomNumberGenerator.Fill(b); return b;
    }
}
