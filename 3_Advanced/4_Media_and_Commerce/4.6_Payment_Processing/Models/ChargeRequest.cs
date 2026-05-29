// ChargeRequest / ChargeResult — the API contract for the Charge endpoint.
//
// IdempotencyKey is mandatory: it's how the client says "this is the same
// request I just tried" so the server returns the cached result instead of
// triggering a second charge.
//
// WasIdempotent in the result lets the client tell "we processed your charge
// just now" apart from "we returned the previous result we cached for this key"
// — useful for client-side UI hints but not strictly required.

public class ChargeRequest
{
    public string IdempotencyKey { get; set; }
    public string MerchantId     { get; set; }
    public string CustomerId     { get; set; }
    public string CardToken      { get; set; }
    public long   AmountCents    { get; set; }
    public string Currency       { get; set; }
    public string MerchantUrl    { get; set; }
    public FraudContext FraudCtx { get; set; }
}

public class ChargeResult
{
    public bool   Success       { get; set; }
    public string PaymentId     { get; set; }
    public PaymentStatus Status { get; set; }
    public string Error         { get; set; }
    public bool   WasIdempotent { get; set; }
}
