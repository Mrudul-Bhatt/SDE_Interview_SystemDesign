// ChargeRequest / ChargeResult — the API contract for the Charge endpoint.
//
// THE BIG IDEA:
// ChargeRequest is what the merchant sends to start a payment; ChargeResult is what comes back.
// IdempotencyKey is mandatory — it's how the client says "this is a retry of the same charge"
// so the server returns the cached result instead of charging the customer twice.
//
// WasIdempotent lets the client tell "we just processed this" apart from "we returned a cached
// result for a key we'd seen before" — a UI hint, not required for correctness.
//
// HOW IT BEHAVES AT RUNTIME (Scenario 1, a clean $100 charge):
//
//   Request : { IdempotencyKey="order-1001", AmountCents=10000, CardToken=goodToken,
//               FraudCtx={ AvsMatch=true, CvvMatch=true, ... } }
//   Result  : { Success=true, PaymentId="pay_X", Status=Authorized, WasIdempotent=false }

public class ChargeRequest
{
    public string IdempotencyKey { get; set; }  // mandatory; dedupes retries
    public string MerchantId { get; set; }
    public string CustomerId { get; set; }
    public string CardToken { get; set; }  // vault token, not the raw PAN
    public long AmountCents { get; set; }
    public string Currency { get; set; }
    public string MerchantUrl { get; set; }  // where to deliver the result webhook
    public FraudContext FraudCtx { get; set; }  // signals for the fraud scorer
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string PaymentId { get; set; }
    public PaymentStatus Status { get; set; }
    public string Error { get; set; }   // set when Success=false (e.g. PAYMENT_DECLINED)
    public bool WasIdempotent { get; set; }   // true => this is a cached replay, not a new charge
}
