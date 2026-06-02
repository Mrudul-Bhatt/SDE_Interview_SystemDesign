# Payment Processing — Beginner Summary

## What is this project?

A **Payment Processing** system (like Stripe, Adyen, or PayPal) sits between a merchant's checkout button and the global banking network. When a customer clicks "Pay $100," this system has to: check it isn't fraud, ask the customer's bank to approve it, move the money, take a small fee, tell the merchant it worked, and keep a perfect accounting record — all without ever charging someone twice or losing a cent.

Think of it like the cashier at a very paranoid, very meticulous bank branch. Before accepting your money, the cashier: confirms you're not a known scammer, phones your bank to confirm the funds exist, writes *every single movement* into a tamper-proof ledger in matching pairs, gives you a receipt, and at the end of the night cross-checks their drawer against the bank's official record down to the penny.

The reason this is hard isn't the happy path — it's that **money is involved, so every failure mode matters.** A dropped network request must never become a double charge. A crash mid-transaction must never lose money. The books must *always* balance.

---

## The Big Challenges

1. **Never charge twice (idempotency).** The customer's phone loses signal right after they tap Pay. The app retries. Without protection, that's two $100 charges for one purchase.
2. **Never lose or invent money (the ledger).** Every cent that moves must be recorded, and the total money in must always equal the total money out. This has to be auditable years later.
3. **Never let two operations corrupt each other (concurrency).** Two capture requests arriving at once must not both succeed and over-charge the authorization.
4. **Stop fraud before the money moves.** Stolen cards, bots, and laundering must be caught *before* contacting the bank.
5. **Protect card numbers (PCI compliance).** Raw card numbers are radioactive. A leak is catastrophic — legally and financially.
6. **Tell the merchant reliably, even if their server is down (webhooks).** And prove the message really came from you.

Every file in this project solves one of these problems.

---

## The Money Lifecycle — The One Big Idea

A payment is a **state machine**. Money doesn't move in one step — it moves through stages, and each stage is a deliberate, recorded transition:

```
   AUTHORIZE          CAPTURE            SETTLE
┌─────────────┐   ┌─────────────┐   ┌─────────────┐
│ bank places │   │ actually    │   │ money wired │
│ a HOLD on   │──▶│ take the    │──▶│ to merchant │
│ the funds   │   │ money + fee │   │ bank account│
└─────────────┘   └─────────────┘   └─────────────┘
  Authorized        Captured           Settled

      ↓ (any time after capture)
   REFUND → PartiallyRefunded / Refunded   (offsetting entries, never erasure)

   (or, early on:)  Failed / Blocked / Cancelled
```

**Why three steps instead of one?** Authorization is reversible (a hold expires harmlessly); capture is when money truly changes hands. Separating them lets a merchant authorize at checkout but capture only when the item ships — and cancel for free if it doesn't. This is the foundation of how all card payments work.

---

## Two Concepts You Must Understand

**Idempotency** — Every charge request carries a client-chosen `IdempotencyKey` (like `"order-1001"`). The server remembers the result for that key for 24 hours. If the same key arrives again, it returns the *cached* result instead of charging again. This is the single most important safety mechanism in payments: it makes "retry on failure" safe.

**Double-Entry Ledger** — Every money movement is recorded as matching **debits and credits** that always sum to zero. Money is never created or destroyed, only moved between accounts. Entries are **append-only and immutable** — to undo something, you post an *offsetting* entry, never edit or delete. If total debits ever ≠ total credits, you have a bug, and you've caught it before money is lost. This is 700-year-old accounting, and it's still how every financial system on earth works.

---

## The Files — What Each One Does

### The Models (the data shapes)

**`Models/Enums.cs`** — The `PaymentStatus` state machine (Pending → Authorized → Captured → Settled, plus Failed/Blocked/Cancelled and Refunded/PartiallyRefunded) and the `FraudDecision` (Allow / Review / Block).

**`Models/ChargeRequest.cs`** — The API contract. `IdempotencyKey` is mandatory — it's how the client says "this is the same request I just tried." `ChargeResult.WasIdempotent` tells the caller whether they got a fresh charge or a cached replay.

**`Models/Payment.cs`** — The durable record of one charge. Two critical fields: `Version` (for optimistic locking — every state change increments it, blocking concurrent corruption) and the separate `CapturedCents` / `RefundedCents` so partial captures and refunds compose cleanly. `RemainingRefundable = Captured − Refunded` is the invariant the refund flow guards.

**`Models/LedgerEntry.cs`** — One half of a double-entry transaction (a DEBIT or a CREDIT). Append-only, immutable — the bedrock of auditability.

**`Models/IdempotencyEntry.cs`** — A cached request result, keyed by `(MerchantId, Key)` so two merchants can both use `"order-1001"` without colliding. `ExpiresAt` enforces the 24-hour TTL.

**`Models/FraudContext.cs`** — The bundle of fraud signals (failed-attempt velocity, AVS/CVV match, country, amount-vs-average). Kept separate from `ChargeRequest` so fraud logic can evolve independently. Note most signals come from *outside* the payment flow (Redis velocity counters, the card network's pre-auth check).

**`Models/WebhookEvent.cs`** — A notification queued for the merchant, with `AttemptCount` + `NextRetry` driving exponential-backoff retries.

**`Models/BankSettlementRecord.cs`** — One row from the bank's end-of-day report, used by reconciliation.

### The Core Logic

**`Core/FraudScorer.cs`** — Rule-based scoring, 0–100:
```
CVV mismatch        +40        Velocity (5+ fails/10min)  +50
AVS mismatch        +20        Amount 10× cust. average   +25
High-risk country   +30

  score < 60  → Allow
  60–79       → Review (manual / 24h hold)
  ≥ 80        → Block
```
Crucially, the specific reasons are **never** shown to the customer — telling a fraudster "you failed CVV" just helps them tune the next attempt. Reasons are for internal logs only.

### The Infrastructure

**`Infrastructure/CardNetworkGateway.cs`** — The boundary to Visa/Mastercard. `Authorize` asks the issuing bank to place a hold (the slow ~1–3 second remote call, the slowest leg of the flow); `Capture` and `Refund` follow. Returns an `authRef` used to tie later operations to the original authorization.

### The Services (the orchestration)

**`Service/PaymentService.cs`** — The conductor. The **charge pipeline order is the whole game:**
```
1. Idempotency check FIRST   ← before any side effect; return cached result if seen
2. Validate card token
3. Fraud score → Allow/Review/Block
4. Authorize with the card network (the slow call)
5. Persist the Payment row (Authorized)
6. Write balanced ledger entries (debit customer / credit suspense)
7. Store idempotency result LAST  ← only after side effects are committed
8. Enqueue the webhook
```
`Capture`, `Settle`, and `Refund` each use **optimistic locking**: they read the current `Version`, mutate, and the store rejects the write if the version moved underneath them — so two concurrent captures can't both win. Every operation writes **balanced** ledger entries; refunds post *four* entries so the platform fee is correctly reversed too. Refunds are hard-capped at `RemainingRefundable` to prevent over-refunding.

### The Storage Layer

**`Storage/CardVault.cs`** — The *only* component that ever sees a raw card number. It hands out opaque tokens (`tok_abc123`); everything else stores the token, which is useless to an attacker. This isolation is what shrinks **PCI DSS scope** — even if the main database leaks, no card numbers escape.

**`Storage/IdempotencyStore.cs`** — The `(merchantId, key) → result` cache with a 24-hour TTL (Redis + Postgres fallback in production).

**`Storage/LedgerService.cs`** — The append-only double-entry ledger. `IsBalanced()` checks the global invariant (total debits == total credits) — the canary that catches payment bugs before money is lost. `GetBalance(account)` derives any account's balance by summing its entries.

**`Storage/PaymentStore.cs`** — The payment DB. `Update` enforces optimistic concurrency: the write only succeeds if `newVersion == expectedVersion + 1`. In production this is `UPDATE ... WHERE version = expectedVersion` — the affected-rows count is the atomic compare-and-swap.

**`Service/WebhookService.cs`** — At-least-once merchant notification. **Persists the event before any HTTP attempt** (so a crash never loses a notification). Retries on a backoff schedule (10s → 1m → 5m → 30m → 2h → 12h → 24h), giving up after 7 attempts (~3 days). Every payload is **HMAC-SHA256 signed** so merchants can verify it genuinely came from you — without it, an attacker could forge `payment.captured` events to get goods shipped for free.

**`Service/ReconciliationJob.cs`** — The nightly books-vs-bank check. Three discrepancy classes:
```
IN_LEDGER_ONLY:  we recorded it, bank hasn't reported yet  → usually timing, flag if it lingers
IN_BANK_ONLY:    bank has money we don't recognize          → ALWAYS investigate
MISMATCH:        amounts disagree                           → FX/fee drift; auto-fix small, alert big
```
It ends by re-asserting `IsBalanced()`. Auditors live or die by this report.

### `Program.cs` — The Demo

Runs 7 scenarios covering the full system:

| Scenario | What it demonstrates |
|---|---|
| 1 | Happy path — authorize → capture → settle, with the full balanced ledger shown |
| 2 | Idempotency — same key sent twice returns the same payment ID (no double charge) |
| 3 | Fraud block — stolen-card signals (bad CVV/AVS, high-risk country, velocity) → blocked |
| 4 | Declined card — the bank says no; payment ends in Failed |
| 5 | Partial refund — refund $50 + $50 of $150, then a $100 over-refund correctly rejected |
| 6 | Webhook retry — a failing merchant endpoint gets scheduled for backoff retry |
| 7 | Reconciliation — match settled payments against a bank report; flag the mystery row |

---

## The Big Picture — How It All Fits Together

```
CHARGE ($100 from Alice via merchant1):

PaymentService.Charge(req with IdempotencyKey="order-1001")
   1. IdempotencyStore.TryGet()  → not seen before, continue
   2. CardVault.IsValid(token)   → ok
   3. FraudScorer.Score()        → score 0, Allow
   4. CardVault.Detokenize() → CardNetworkGateway.Authorize()  → authRef
   5. PaymentStore.Save(status=Authorized, version=1)
   6. LedgerService.Record(  DEBIT customer $100 / CREDIT suspense $100  )  ← balanced
   7. IdempotencyStore.Store("order-1001" → paymentId)   ← LAST
   8. WebhookService.Enqueue("payment.authorized")


CAPTURE (take the money + 2.9% fee):

PaymentService.Capture(paymentId)
   → CardNetworkGateway.Capture(authRef)
   → optimistic lock: read version=1, set Captured, version=2, Update(expected=1)
   → LedgerService.Record(
        DEBIT  suspense          $100.00
        CREDIT merchant:net      $ 97.10
        CREDIT platform:revenue  $  2.90 )   ← still balanced


SETTLE (wire to merchant's bank) → Refund (offsetting entries) → ...

RETRY SAFETY (Alice's app re-sends "order-1001"):
   PaymentService.Charge()
   → IdempotencyStore.TryGet() → HIT → return cached paymentId
   → NO second charge ✓


NIGHTLY:
   WebhookService.ProcessDue()    → deliver/retry merchant notifications
   ReconciliationJob.Run(bank)    → match ledger vs bank, assert IsBalanced()
```

---

## Why This Design Is Used Everywhere

- **Idempotency keys** are the universal answer to "the network is unreliable but I can't afford a double charge" — Stripe, every payment API, and most modern REST APIs use exactly this pattern.
- **Double-entry ledgers** are non-negotiable in finance — append-only, always-balanced, correct-by-offsetting is how every bank, exchange, and payment processor keeps books that survive an audit.
- **Authorize/capture/settle separation** is how all card payments work — it's why a hotel can "hold" your card at check-in and only charge at check-out.
- **Tokenization + an isolated vault** is the standard PCI-compliance architecture — keep raw card data in one tiny, hardened box so a breach anywhere else can't expose it.
- **Optimistic locking** is the lightweight concurrency control of choice when conflicts are rare but catastrophic — read-modify-write with a version check beats heavy locks for payment throughput.
- **At-least-once webhooks with HMAC signing + backoff** is how every platform notifies merchants reliably and verifiably — delivery you can trust even when the receiver is flaky.
- **Nightly reconciliation** is the safety net every money-moving system runs — because no matter how careful the code, you must independently prove your books match reality.
- **Persist-before-side-effect ordering** (idempotency stored last, webhooks persisted before sending) is the same durability-first discipline seen across the other projects — never expose or commit a result until the money-critical record is safe.
