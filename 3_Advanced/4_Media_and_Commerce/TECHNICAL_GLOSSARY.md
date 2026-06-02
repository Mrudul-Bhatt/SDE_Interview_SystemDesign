# Technical Glossary — Media and Commerce

Terms found across the **Video Streaming** and **Payment Processing** projects.

These two projects look unrelated but share a deep theme: **expensive, irreversible operations that must be reliable at massive scale.** Video must be transcoded once and served billions of times; money must move exactly once and be accounted for to the penny. Both lean heavily on idempotency, durable ordering, and content/record immutability.

---

## Video Encoding & Delivery — *Video Streaming*

| Term | Where | One-line meaning |
|---|---|---|
| **HLS (HTTP Live Streaming)** | HlsStore, TranscodeWorker | Video chopped into small segments + a manifest, served over plain HTTP |
| **ABR (Adaptive Bitrate)** | AbrPlayer | Player picks the highest quality the current network can sustain, per segment |
| **Rendition** | Enums, BitrateTable | One discrete quality level (360p, 480p, 720p, 1080p, 4K) |
| **Bitrate** | BitrateTable | Data-per-second of a rendition (kbps); compared against measured throughput |
| **Segment** | HlsSegment | One ~6-second slice of one rendition — the unit of download and caching |
| **Manifest (M3U8)** | TranscodeWorker.BuildManifest | The text file listing all renditions/segments; players fetch it first |
| **Transcoding** | TranscodeWorker | Converting one raw upload into many renditions of HLS segments |
| **Content-Addressed URL** | HlsSegment.Url | URL derived from (videoId, quality, index) → immutable → cache-forever-safe |
| **CDN (Content Delivery Network)** | CdnEdgeCache | Edge servers near viewers that cache segments so bytes travel a short distance |
| **Origin** | HlsStore | The authoritative source the CDN pulls from on a cache miss |
| **Cache Hit Rate** | CdnEdgeCache.HitRate | Fraction of requests served from cache (production target: 95%+) |

---

## Upload, Playback & Counting — *Video Streaming*

| Term | Where | One-line meaning |
|---|---|---|
| **Chunked Resumable Upload** | UploadService | Split a huge file into pieces so a dropped connection resumes from the gap |
| **Idempotent Chunk** | UploadSession.ReceivedChunks | Re-sending the same chunk is harmless (Set membership) |
| **Resume Point** | UploadService.GetResumePoint | The index of the first missing chunk → where the client restarts |
| **Playback Buffer** | AbrPlayer._bufferSeconds | Seconds of video downloaded ahead; draining it causes a stall |
| **Throughput** | AbrPlayer | Measured download speed used to choose the next segment's quality |
| **Emergency Drop** | AbrPlayer.ChooseQuality | Force 360p when buffer is low — continuity beats picture quality |
| **Watch Progress / Playhead** | WatchProgress, WatchHistory | A user's saved position in a video, enabling "resume where you left off" |
| **Buffered View Counting** | ViewCounter | Aggregate view heartbeats and flush periodically instead of per-view DB writes |
| **Anti-Fraud View Floor** | ViewCounter (MinPlaybackSeconds) | A "view" requires ≥30s of real playback to stop open-and-close bots |
| **Trending (view velocity)** | VideoMetaStore.Trending | Ranking by view rate + recency, not just cumulative count |

---

## Payment Lifecycle — *Payment Processing*

| Term | Where | One-line meaning |
|---|---|---|
| **Authorize** | PaymentService.Charge | Ask the issuing bank to place a reversible hold on the funds |
| **Capture** | PaymentService.Capture | Actually take the held money (and the platform fee) |
| **Settle** | PaymentService.Settle | Wire the captured money to the merchant's bank account |
| **Refund / Partial Refund** | PaymentService.Refund | Return money via offsetting entries; status → (Partially)Refunded |
| **Payment State Machine** | Enums.PaymentStatus | Pending → Authorized → Captured → Settled (+ Failed/Blocked/Refunded) |
| **Authorization Hold** | CardNetworkGateway.Authorize | The temporary fund reservation that expires harmlessly if never captured |
| **Card Network Gateway** | CardNetworkGateway | The boundary to Visa/Mastercard; the slow ~1–3s remote leg |
| **Platform Fee** | PaymentService (2.9%) | The processor's cut, split out in the ledger at capture time |

---

## Money & Accounting — *Payment Processing*

| Term | Where | One-line meaning |
|---|---|---|
| **Double-Entry Ledger** | LedgerService | Every movement recorded as matching debits + credits that sum to zero |
| **Debit / Credit** | LedgerEntry | The two halves of every transaction; money moves between accounts |
| **Append-Only / Immutable Ledger** | LedgerService | Entries are never edited or deleted; undo = a new offsetting entry |
| **Balanced Invariant** | LedgerService.IsBalanced | Total debits must equal total credits — the canary for money bugs |
| **Suspense Account** | PaymentService (ledger) | A temporary holding account between authorize and capture |
| **Reconciliation** | ReconciliationJob | Nightly check that our ledger matches the bank's settlement report |
| **Settlement Record** | BankSettlementRecord | One row of the bank's end-of-day report, matched by reference ID |

---

## Security & Fraud — *Payment Processing*

| Term | Where | One-line meaning |
|---|---|---|
| **Tokenization** | CardVault.Tokenize | Replace a raw card number with an opaque token (`tok_…`) |
| **Card Vault** | CardVault | The only component that ever sees raw PANs; everything else holds tokens |
| **PAN (Primary Account Number)** | CardVault | The raw card number — radioactive data kept isolated |
| **PCI DSS Scope Reduction** | CardVault (comment) | Isolating card data so a breach elsewhere can't leak it |
| **Fraud Scoring** | FraudScorer | Rule-based 0–100 risk score → Allow / Review / Block |
| **AVS / CVV Match** | FraudContext | Address / security-code verification signals from the card network |
| **Velocity Check** | FraudScorer | Flag too many failed attempts on a card in a short window |
| **HMAC Signing** | WebhookService.Sign | Cryptographic signature proving a webhook genuinely came from us |

---

## Reliability & Concurrency (shared themes)

| Term | Where | One-line meaning |
|---|---|---|
| **Idempotency Key** | IdempotencyStore, ChargeRequest | A client-chosen key so a retried request returns the cached result, not a 2nd charge |
| **Optimistic Locking (OCC)** | PaymentStore.Update, Payment.Version | Version-checked writes so concurrent updates can't corrupt each other |
| **Compare-and-Swap (CAS)** | PaymentStore (comment) | `UPDATE … WHERE version = expected` — the atomic primitive behind OCC |
| **At-Least-Once Delivery** | WebhookService, UploadService | Retry until acknowledged; receivers dedupe the rare duplicate |
| **Exponential Backoff** | WebhookService | Retry delays that grow (10s→1m→5m→…) to ride out transient failures |
| **Persist-before-Side-Effect** | PaymentService, WebhookService | Save the durable record before the irreversible action / network call |
| **Atomic Operation** | UploadService, vault (`RandomNumberGenerator`) | A single uninterruptible step — e.g. collision-free ID generation |

---

## Production Infrastructure (mentioned in comments)

| Term | Where | One-line meaning |
|---|---|---|
| **CDN** | CdnEdgeCache | Edge cache network (CloudFront, Akamai) serving video globally |
| **S3 / Glacier** | RawVideoStore | Object storage for originals; Glacier = cheap cold storage for re-transcode |
| **FFmpeg / GPU Farm** | TranscodeWorker (comment) | The real transcoding toolchain running on GPU fleets |
| **Cassandra** | VideoMetaStore, WatchHistory, Payment DBs | Wide-column DB for metadata and time-ordered history |
| **Elasticsearch** | VideoMetaStore.Search (comment) | Search index for video discovery (vs the toy linear scan here) |
| **Kafka** | UploadService, ViewCounter (comments) | Durable queue for transcode jobs and view heartbeats |
| **Redis** | IdempotencyStore (comment) | Fast store for the idempotency-key cache (with a Postgres fallback) |
| **Postgres** | LedgerService, IdempotencyStore (comments) | Strongly-consistent SQL DB for the ledger and durable idempotency records |
| **HSM (Hardware Security Module)** | CardVault (comment) | Tamper-resistant hardware that manages the keys encrypting card data |
