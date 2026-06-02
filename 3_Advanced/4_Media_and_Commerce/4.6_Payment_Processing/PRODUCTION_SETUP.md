# Payment Processing — Production Setup

What each class in this project would be replaced with or enhanced by in a real production system (modelled on Stripe, Adyen, and PayPal).

---

## `Models/Enums.cs` — PaymentStatus + FraudDecision enums

**Problem in production:** The state machine is right, but a bare enum doesn't enforce *legal transitions* — nothing stops code from flipping `Settled → Authorized`. Real money flows need the transition graph itself to be enforced and audited.

**Production replacement: An enforced state machine + an audit trail**

```
- A transition table defines every legal edge (Authorized→Captured ok;
  Settled→Authorized rejected). Illegal transitions throw, not corrupt state.
- Every transition is appended to a payment_events audit log (who, when, why,
  prev→next) — required for disputes, compliance, and debugging.
- More states appear: Disputed, ChargebackReceived, Reversed, Expired (auth holds
  expire after ~7 days if not captured).
```

---

## `Models/ChargeRequest.cs` — API contract with an idempotency key

**Problem in production:** The shape is correct (mandatory idempotency key), but a production API needs versioning, validation, and to never carry raw card data.

**Production replacement: A versioned, validated API; card data via tokens only**

```
- API versioning (Stripe pins a version per account) so contract changes don't
  break existing integrations.
- The request carries a CARD TOKEN, never a PAN — collected client-side via a
  hosted field / SDK (Stripe Elements) so raw card data never touches your servers.
- Strict validation + a stable error taxonomy (card_declined, insufficient_funds,
  expired_card...) so merchants can react programmatically.
```

---

## `Models/Payment.cs` — Durable record with a Version field

**Problem in production:** Right model (optimistic-lock `Version`, separate captured/refunded). The gap is that money amounts and multi-currency need care, and the row needs strong consistency.

**Production replacement: A strongly-consistent row, integer minor units, currency-aware**

```
- Store amounts as integer minor units (cents) — NEVER floats (the demo does
  this correctly). Float rounding on money is a classic catastrophic bug.
- Currency is first-class: amount + ISO-4217 code together; cross-currency needs
  FX rate capture at auth time.
- Lives in a strongly-consistent SQL store (Postgres/Spanner) with the Version
  column driving optimistic concurrency (the demo's design, now durable).
```

---

## `Models/LedgerEntry.cs` — One half of a double-entry record

**Problem in production:** The append-only double-entry design is exactly right and is the heart of a correct money system. The gap is durability, immutability guarantees, and richer dimensions.

**Production replacement: An immutable, strongly-consistent ledger row**

```
- Append-only in Postgres/Spanner with immutability enforced (no UPDATE/DELETE
  grants; corrections are new offsetting entries — the demo's rule).
- Richer dimensions: currency, entry timestamp vs effective date, a transaction
  group ID linking the entries of one operation, and account-type metadata.
- Often a purpose-built ledger system (TigerBeetle, or an internal double-entry
  service) optimized for high-throughput financial transactions with the
  balanced invariant enforced atomically.
```

---

## `Models/IdempotencyEntry.cs` — Cached result with a TTL

**Problem in production:** Correct design (composite key, 24h TTL). The subtlety production adds: handling *concurrent* requests with the same key, and storing the full response.

**Production replacement: Idempotency records with in-flight locking**

```
- On first sight of a key: insert a "processing" row (a unique-constraint insert
  acts as a lock). A concurrent duplicate sees "processing" and waits/retries
  rather than starting a second charge — closes the race the demo doesn't model.
- Store the FULL serialized response + status code, so a replay returns a
  byte-identical result.
- 24h TTL in Redis (hot) with a durable DB record behind it.
```

---

## `Models/FraudContext.cs` — Bundle of fraud signals

**Problem in production:** Right idea (decouple fraud inputs), but the signals are static fields. Production assembles them live from many real-time and historical sources.

**Production replacement: A real-time feature pipeline**

```
- Velocity counters from Redis (failed attempts per card/IP/device in a window).
- Device fingerprinting + IP reputation from a third party (Sift, fraud vendors).
- Historical features from a feature store (customer avg order, account age,
  chargeback history).
- AVS/CVV from the network's pre-auth check.
- 3-D Secure (SCA) challenge results in regulated markets.
```

---

## `Models/WebhookEvent.cs` — Notification with retry fields

**Problem in production:** Correct shape (attempt count, next-retry for backoff). The gap is durability and ordering across many events for one merchant.

**Production replacement: A durable, ordered event-delivery record**

```
- Persisted before any send attempt (the demo's WebhookService does this).
- Per-merchant ordering where it matters, and an event log the merchant can
  replay from their dashboard (the demo notes manual replay).
- Stored with the signing key version so signature verification survives key rotation.
```

---

## `Models/BankSettlementRecord.cs` — One bank-report row

**Problem in production:** A single CSV-style row can't represent real settlement files (fees, interchange, FX, batch grouping).

**Production replacement: A parsed, normalized settlement feed**

```
- Bank/processor files (or APIs) arrive per scheme with fees, interchange splits,
  FX rates, and batch IDs — parsed into normalized records.
- Reconciliation matches not just the principal but fees and net amounts
  (the ReconciliationJob notes this).
```

---

## `Core/FraudScorer.cs` — Rule-based 0–100 scorer

**Problem in production:** A fixed rule set is a fine baseline (and stays as a fast first pass), but real fraud uses ML and adapts continuously.

**Production replacement: Rules + an ML model + async review queue**

```
- Rules stay as a fast, explainable pre-filter (hard blocks: bad CVV, velocity).
- An ML model scores the gray zone on hundreds of features, retrained on fresh
  chargeback labels — fraud patterns shift weekly, static rules go stale.
- The "Review" decision feeds a manual review queue / step-up (3-D Secure
  challenge), not a synchronous block.
- Reasons stay INTERNAL (the demo's key point) — never tell a fraudster which
  signal tripped, or they tune around it.
```

---

## `Infrastructure/CardNetworkGateway.cs` — Visa/Mastercard boundary

**Problem in production:** The slowest, most failure-prone leg, simulated as always-succeeding. Real network calls time out, decline for dozens of reasons, and have strict reliability requirements.

**Production replacement: Resilient acquirer/processor integration**

```
- Real integration with an acquiring bank / processor over ISO 8583 or a modern API.
- Timeouts, retries with idempotency (a retried auth must NOT double-authorize),
  and circuit breakers when the network is degraded.
- Rich decline handling: insufficient_funds, do_not_honor, expired_card,
  auth_expired at capture — each mapped to a merchant-facing error.
- Smart retry / routing: retry soft declines, route across multiple acquirers
  for cost and approval-rate optimization.
```

---

## `Service/PaymentService.cs` — The charge/capture/settle/refund orchestrator

**Problem in production:** The pipeline order is correct (**idempotency → validate → fraud → authorize → persist → ledger → store idempotency → webhook**) and optimistic locking is the right concurrency control. The gaps are atomicity across steps, async webhooks, and saga-style failure recovery.

**Production replacement: A durable workflow with atomic DB transactions**

```
- The payment row + its ledger entries are written in ONE database transaction —
  so you can never have a Captured payment with no ledger entries (or vice versa).
  The demo writes them sequentially; production makes them atomic.
- Webhook enqueue happens via the transactional outbox pattern: write the event
  to an outbox table in the same transaction, a relay publishes it after commit —
  guarantees "if the charge committed, the webhook will be sent" with no dual-write race.
- Long flows (auth → capture days later → settle → possible refund/chargeback)
  run as a saga/state machine with compensation (e.g. auth succeeded but ledger
  write failed → void the auth).
- Stays optimistic-locked on Version (the demo's CONCURRENT_UPDATE → retry).
```

---

## `Storage/CardVault.cs` — The only component that sees raw PANs

**Problem in production:** The isolation concept (tokens out, PAN in) is exactly the PCI-compliance architecture. Production hardens it dramatically.

**Production replacement: A PCI-DSS Level 1 vault (or outsource it entirely)**

```
- Dedicated, network-isolated service; PANs encrypted with HSM-managed keys,
  AES-256 at rest, keys rotated, every access audit-logged, PAN never logged.
- Most companies OUTSOURCE this to Stripe/Adyen/Braintree precisely to keep raw
  card data out of their PCI scope — the single most expensive thing to operate
  compliantly. The vault here represents the part you'd usually NOT build.
- Network tokenization: store network tokens (from Visa/MC) instead of raw PANs
  where possible — auto-updated when a card is reissued.
```

---

## `Storage/IdempotencyStore.cs` — (merchant, key) → result cache

**Problem in production:** Right design; needs durability and the in-flight lock described under `IdempotencyEntry`.

**Production replacement: Redis + durable backing + atomic first-writer**

```
Redis (24h TTL, fast path) + Postgres (durable record for keys that age out but
might still be retried). First write uses an atomic insert/SETNX so exactly one
request becomes the "processor" and concurrent duplicates wait for its result.
```

---

## `Storage/LedgerService.cs` — Append-only double-entry ledger

**Problem in production:** The `IsBalanced` invariant is the canary that keeps the whole system honest, and append-only-with-offsets is correct accounting. Production needs strong consistency, atomic balanced writes, and scale.

**Production replacement: A strongly-consistent ledger with atomic balanced posts**

```
- A batch of entries is written atomically and rejected if debits ≠ credits —
  the balanced invariant enforced at write time, not just checked after.
- Strong consistency (Postgres/Spanner) with synchronous replication — money
  records cannot be eventually-consistent.
- Account balances derived via materialized rollups (not summing all history each
  time) so GetBalance stays fast at billions of entries.
- Often a specialized engine (TigerBeetle) for very high transaction throughput.
```

---

## `Storage/PaymentStore.cs` — Payment DB with version-checked Update

**Problem in production:** `Update ... WHERE version = expected` is exactly the right optimistic-concurrency primitive. The gap is durability and read scale.

**Production replacement: Strongly-consistent SQL with the same CAS, plus read replicas**

```
- Postgres/Spanner: UPDATE payments SET ..., version=version+1 WHERE id=? AND
  version=? — the affected-rows check is the atomic compare-and-swap (the demo's design).
- Writes go to the primary (strong consistency); high-volume reads (dashboards,
  status checks) hit read replicas.
```

---

## `Service/WebhookService.cs` — At-least-once delivery with backoff + HMAC

**Problem in production:** This is a strong design already — persist-before-send, exponential backoff, HMAC signing. Production scales the delivery and hardens verification.

**Production replacement: A durable webhook delivery service**

```
- Events from a transactional outbox → a delivery worker pool → merchant HTTPS.
- Backoff schedule (the demo's 10s→...→24h), then mark FAILED with dashboard replay.
- HMAC-SHA256 signature + a timestamp in the header to prevent replay attacks;
  publish signing secrets with versioning so rotation doesn't break verification.
- At-least-once: merchants MUST dedupe by event ID (the demo notes this).
- Per-merchant rate limiting / circuit breaking so one slow merchant endpoint
  doesn't back up the whole delivery pipeline.
```

---

## `Service/ReconciliationJob.cs` — Nightly books-vs-bank check

**Problem in production:** The three-way discrepancy model (in-ledger-only / in-bank-only / mismatch) is exactly how real reconciliation works. Production scales it and adds fees/FX.

**Production replacement: An automated multi-way reconciliation pipeline**

```
- Ingests settlement files/APIs per scheme; matches principal AND fees,
  interchange, and FX (the job notes this).
- Auto-resolves known timing differences (T+1 settlement), auto-corrects small
  rounding deltas, and ALERTS/opens a case on anything material — especially
  IN_BANK_ONLY (money you can't account for).
- Re-asserts the ledger balanced invariant (the demo ends with IsBalanced()).
- Auditors depend on this report; it's run continuously, not just nightly, at scale.
```

---

## `Program.cs` — Sequential demo scenarios

**Problem in production:** A single process running scenarios; production is many always-on services.

**Production replacement: Deployed microservices + infrastructure**

```
API gateway        → auth, rate limiting, API versioning
Payment service    → charge/capture/settle/refund orchestration (saga)
Vault              → PCI-isolated tokenization (often outsourced)
Fraud service      → rules + ML + review queue
Ledger service     → strongly-consistent double-entry
Webhook service    → outbox-driven delivery
Reconciliation     → settlement matching pipeline
Stores             → Postgres (payments/ledger), Redis (idempotency/velocity)
```

The seven demo scenarios map to real concerns: happy path, idempotency, fraud block, decline handling, partial refund, webhook retry, and reconciliation.

---

## Cross-cutting concerns not modelled in this project

### 1. Compliance & regulation

PCI DSS (card data), SCA/3-D Secure and PSD2 (Europe), KYC/AML (know-your-customer, anti-money-laundering), and per-region licensing. These shape the whole architecture — e.g. SCA mandates a step-up challenge flow the demo's fraud "Review" only hints at.

### 2. Disputes & chargebacks

A customer can dispute a charge weeks later. The system must handle the chargeback lifecycle (inquiry → dispute → evidence submission → win/loss), post the corresponding ledger reversals, and track dispute rates (schemes fine merchants above thresholds). This is a major subsystem the demo's lifecycle doesn't include.

### 3. Observability & money-safety alarms

```
payment_authorization_rate            approval % (a drop signals an outage/issue)
ledger_balanced (boolean alarm)       MUST always be true — page immediately if not
idempotency_hit_rate                  retry behavior health
webhook_delivery_success_ratio        merchant notification health
reconciliation_unmatched_total        money you can't account for → investigate
fraud_block_rate / chargeback_rate    fraud-system tuning signals
```

The non-negotiable alarm: **the ledger must always balance.** If `IsBalanced()` ever goes false in production, that's a sev-1 page.

### 4. Security

mTLS between services, secrets in a vault/HSM, least-privilege access to card data and the ledger, full audit logging of every money-affecting action, and tamper-evident logs for forensics.

### 5. Exactly-once money movement

The combination of idempotency keys (no double charge), optimistic locking (no double capture), the transactional outbox (no lost/duplicate webhook), and network-call idempotency (no double authorize) together give end-to-end exactly-once semantics for money — the property the whole design exists to guarantee.

### 6. Multi-currency & payouts

FX rate capture, settlement in the merchant's currency, payout scheduling (when and how merchants actually receive funds), and balance management across currencies.

---

## The Full Production Picture

```
CHARGE:
  API gateway (auth, rate limit, versioning)
  → Payment service:
       1. idempotency: atomic first-writer insert (concurrent dup waits)
       2. validate card token (vault — often outsourced)
       3. fraud: rules → ML → Allow / Review(step-up) / Block
       4. authorize via acquirer (timeouts, retries-with-idempotency, circuit breaker)
       5. ONE DB TRANSACTION: write Payment(Authorized) + balanced ledger entries
                              + webhook event to the outbox
       6. commit → outbox relay publishes "payment.authorized"
       → store full idempotent response


CAPTURE / SETTLE / REFUND:
  optimistic-locked (version CAS) state transitions, each in an atomic
  transaction with its balanced ledger entries + outbox event
  (refund posts the 4-entry fee-reversing set from the demo)


WEBHOOKS:
  outbox relay → delivery workers → merchant HTTPS (HMAC-signed, timestamped)
  → backoff retries → FAILED + dashboard replay; merchants dedupe by event ID


Background / always-on:
  Reconciliation     → match ledger vs bank (principal + fees + FX), assert balanced
  Chargeback handler → dispute lifecycle + ledger reversals
  Fraud retraining   → ML on fresh chargeback labels
  Payout scheduler   → move settled funds to merchants

Observability (always-on):
  ledger_balanced alarm   → sev-1 if ever false
  authorization rate, reconciliation unmatched, webhook success, chargeback rate
  full audit log of every money-affecting action
```

The core logic (idempotency keys for safe retries, the authorize→capture→settle state machine, append-only balanced double-entry accounting, optimistic locking against double-spend, persist-before-side-effect ordering, HMAC-signed at-least-once webhooks, and nightly reconciliation) carries forward unchanged — only the infrastructure changes from in-process simulation to a real distributed system with atomic transactions, a transactional outbox, ML fraud, an HSM-backed vault, chargeback handling, regulatory compliance, and money-safety observability.
