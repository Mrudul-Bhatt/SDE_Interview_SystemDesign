using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// ============================================================
//  Notification System — in-memory simulation
//  Covers: multi-channel fan-out, user preferences/opt-out,
//          deduplication (idempotency key), rate limiting,
//          template rendering, retry with backoff, delivery tracking
// ============================================================

namespace Infrastructure
{
    // ── Enums ─────────────────────────────────────────────────────────────────

    public enum NotificationChannel { Email, Sms, Push, InApp, Webhook }

    public enum NotificationPriority
    {
        Critical = 0,   // OTP, fraud alerts — bypasses rate limit
        High     = 1,   // order confirmed, payment failed
        Normal   = 2,   // shipping update, receipt
        Marketing = 3   // promotions, weekly digest
    }

    public enum DeliveryStatus { Queued, Processing, Sent, Failed, Skipped }

    // ── NotificationRequest ────────────────────────────────────────────────────

    // What the producer (OrderService, PaymentService, etc.) sends
    public class NotificationRequest
    {
        public string          RequestId   { get; set; }   // idempotency key provided by producer
        public int             UserId      { get; set; }
        public string          TemplateId  { get; set; }
        public string          ContextId   { get; set; }   // orderId / paymentId / etc.
        public NotificationPriority Priority { get; set; }
        public List<NotificationChannel> Channels { get; set; } // desired channels
        public Dictionary<string, string> Variables { get; set; } // template vars
    }

    // ── DeliveryRecord ─────────────────────────────────────────────────────────

    public class DeliveryRecord
    {
        public string              Id         { get; set; }
        public int                 UserId     { get; set; }
        public NotificationChannel Channel    { get; set; }
        public string              TemplateId { get; set; }
        public DeliveryStatus      Status     { get; set; }
        public string              StatusNote { get; set; }
        public DateTime            CreatedAt  { get; set; }
        public DateTime            UpdatedAt  { get; set; }
        public int                 Attempts   { get; set; }
    }

    // ── TemplateEngine ─────────────────────────────────────────────────────────

    // Renders Handlebars-style {{variable}} templates with provided variables
    public class TemplateEngine
    {
        private readonly Dictionary<string, (string Subject, string Body)> _templates
            = new Dictionary<string, (string, string)>
        {
            ["order_confirmed"] = (
                "Your order {{orderId}} is confirmed!",
                "Hi {{firstName}}, your order of {{total}} ships by {{estimatedDate}}."),
            ["payment_failed"] = (
                "Action required: payment failed for order {{orderId}}",
                "Hi {{firstName}}, we couldn't charge {{total}}. Please update your payment method."),
            ["otp"] = (
                "Your verification code",
                "Your one-time code is {{code}}. Expires in 5 minutes. Do not share."),
            ["promo"] = (
                "{{promoTitle}} — limited time offer",
                "Hi {{firstName}}, {{promoBody}}"),
        };

        public (string Subject, string Body) Render(
            string templateId, Dictionary<string, string> vars)
        {
            if (!_templates.TryGetValue(templateId, out var tpl))
                throw new ArgumentException($"Unknown template: {templateId}");

            string subject = Substitute(tpl.Subject, vars);
            string body    = Substitute(tpl.Body,    vars);
            return (subject, body);
        }

        private string Substitute(string template, Dictionary<string, string> vars)
        {
            var sb = new StringBuilder(template);
            foreach (var kv in vars)
                sb.Replace("{{" + kv.Key + "}}", kv.Value);
            return sb.ToString();
        }

        public bool HasMissingVariables(string templateId, Dictionary<string, string> vars)
        {
            if (!_templates.TryGetValue(templateId, out var tpl)) return true;
            string combined = tpl.Subject + tpl.Body;
            // find all {{placeholder}} tokens
            int i = 0;
            while ((i = combined.IndexOf("{{", i)) >= 0)
            {
                int end = combined.IndexOf("}}", i);
                if (end < 0) break;
                string key = combined.Substring(i + 2, end - i - 2);
                if (!vars.ContainsKey(key)) return true;
                i = end + 2;
            }
            return false;
        }
    }

    // ── UserPreferencesStore ───────────────────────────────────────────────────

    // Per-user, per-channel opt-in/opt-out state
    public class UserPreferencesStore
    {
        // (userId, channel) → opted in?
        private readonly Dictionary<(int, NotificationChannel), bool> _prefs
            = new Dictionary<(int, NotificationChannel), bool>();

        public void Set(int userId, NotificationChannel channel, bool optedIn)
            => _prefs[(userId, channel)] = optedIn;

        // CRITICAL / High-priority transactional: always allowed regardless of pref
        public bool IsAllowed(int userId, NotificationChannel channel, NotificationPriority priority)
        {
            if (priority == NotificationPriority.Critical) return true;

            if (_prefs.TryGetValue((userId, channel), out bool opted))
                return opted;

            return true; // default allow if no explicit preference recorded
        }
    }

    // ── DeduplicationStore ─────────────────────────────────────────────────────

    // Redis SETNX simulation: idempotency key → expiry timestamp
    public class DeduplicationStore
    {
        private readonly Dictionary<string, DateTime> _seen
            = new Dictionary<string, DateTime>();
        private readonly TimeSpan _ttl;

        public DeduplicationStore(TimeSpan? ttl = null)
            => _ttl = ttl ?? TimeSpan.FromHours(24);

        // Returns true if this is the FIRST time we see this key (safe to send)
        public bool TryMarkNew(string key)
        {
            lock (_seen)
            {
                DateTime now = DateTime.UtcNow;
                // evict expired entries lazily
                if (_seen.TryGetValue(key, out DateTime expiry) && now < expiry)
                    return false; // already seen — duplicate

                _seen[key] = now.Add(_ttl);
                return true;
            }
        }

        // Deterministic key: hash of userId + templateId + contextId + channel
        public static string BuildKey(int userId, string templateId,
                                      string contextId, NotificationChannel channel)
            => $"{userId}:{templateId}:{contextId}:{channel}";
    }

    // ── RateLimiter ────────────────────────────────────────────────────────────

    // Token-bucket per (userId, channel) — replenishes at fixed rate
    public class RateLimiter
    {
        private class Bucket
        {
            public double   Tokens    { get; set; }
            public DateTime LastRefill { get; set; }
        }

        private readonly Dictionary<(int, NotificationChannel), Bucket> _buckets
            = new Dictionary<(int, NotificationChannel), Bucket>();
        private readonly double _maxTokens;
        private readonly double _refillPerMs; // tokens added per millisecond

        // Example: maxTokens=10, refillPeriod=1h → 10 per hour
        public RateLimiter(double maxTokens = 10, TimeSpan? refillPeriod = null)
        {
            _maxTokens   = maxTokens;
            TimeSpan period = refillPeriod ?? TimeSpan.FromHours(1);
            _refillPerMs = maxTokens / period.TotalMilliseconds;
        }

        public bool TryConsume(int userId, NotificationChannel channel,
                               NotificationPriority priority)
        {
            // CRITICAL priority always bypasses rate limiting
            if (priority == NotificationPriority.Critical) return true;

            lock (_buckets)
            {
                var key = (userId, channel);
                if (!_buckets.TryGetValue(key, out Bucket bucket))
                {
                    bucket = new Bucket { Tokens = _maxTokens, LastRefill = DateTime.UtcNow };
                    _buckets[key] = bucket;
                }

                // refill tokens based on elapsed time
                double elapsed = (DateTime.UtcNow - bucket.LastRefill).TotalMilliseconds;
                bucket.Tokens    = Math.Min(_maxTokens, bucket.Tokens + elapsed * _refillPerMs);
                bucket.LastRefill = DateTime.UtcNow;

                if (bucket.Tokens >= 1.0)
                {
                    bucket.Tokens -= 1.0;
                    return true;
                }
                return false;
            }
        }
    }

    // ── Channel Adapters ───────────────────────────────────────────────────────

    public interface IChannelAdapter
    {
        NotificationChannel Channel { get; }
        // Returns true on success; throws on transient failure
        bool Send(int userId, string subject, string body, string contextId);
    }

    public class EmailAdapter : IChannelAdapter
    {
        public NotificationChannel Channel => NotificationChannel.Email;
        public bool SimulateTransientFail { get; set; } = false;
        private int _callCount = 0;

        public bool Send(int userId, string subject, string body, string contextId)
        {
            _callCount++;
            if (SimulateTransientFail && _callCount <= 2)
                throw new Exception("SMTP timeout (transient)");

            Console.WriteLine($"    [EMAIL → user {userId}] Subject: {subject}");
            return true;
        }
    }

    public class SmsAdapter : IChannelAdapter
    {
        public NotificationChannel Channel => NotificationChannel.Sms;
        public bool SimulatePermanentFail { get; set; } = false;

        public bool Send(int userId, string subject, string body, string contextId)
        {
            if (SimulatePermanentFail)
                throw new InvalidOperationException("SMS: number not reachable (permanent)");

            Console.WriteLine($"    [SMS  → user {userId}] {body.Substring(0, Math.Min(60, body.Length))}...");
            return true;
        }
    }

    public class PushAdapter : IChannelAdapter
    {
        public NotificationChannel Channel => NotificationChannel.Push;

        public bool Send(int userId, string subject, string body, string contextId)
        {
            Console.WriteLine($"    [PUSH → user {userId}] {subject}");
            return true;
        }
    }

    public class InAppAdapter : IChannelAdapter
    {
        public NotificationChannel Channel => NotificationChannel.InApp;
        private readonly List<string> _inbox = new List<string>();

        public bool Send(int userId, string subject, string body, string contextId)
        {
            _inbox.Add($"[{DateTime.UtcNow:HH:mm:ss}] {subject}");
            Console.WriteLine($"    [IN-APP → user {userId}] queued: {subject}");
            return true;
        }

        public IReadOnlyList<string> GetInbox() => _inbox;
    }

    // ── DeliveryLog ────────────────────────────────────────────────────────────

    public class DeliveryLog
    {
        private readonly List<DeliveryRecord> _records = new List<DeliveryRecord>();
        private int _seq = 0;

        public DeliveryRecord Create(int userId, NotificationChannel channel, string templateId)
        {
            var r = new DeliveryRecord
            {
                Id         = $"notif-{Interlocked.Increment(ref _seq):D4}",
                UserId     = userId,
                Channel    = channel,
                TemplateId = templateId,
                Status     = DeliveryStatus.Queued,
                CreatedAt  = DateTime.UtcNow,
                UpdatedAt  = DateTime.UtcNow,
            };
            lock (_records) _records.Add(r);
            return r;
        }

        public void Update(DeliveryRecord r, DeliveryStatus status, string note = null)
        {
            r.Status     = status;
            r.StatusNote = note;
            r.UpdatedAt  = DateTime.UtcNow;
        }

        public IReadOnlyList<DeliveryRecord> QueryByUser(int userId)
            => _records.Where(r => r.UserId == userId).ToList();

        public void Print(int userId)
        {
            Console.WriteLine($"\n  Delivery log for user {userId}:");
            Console.WriteLine($"  {"ID",-12} {"Channel",-8} {"Template",-18} {"Status",-12} {"Note"}");
            Console.WriteLine($"  {new string('─', 72)}");
            foreach (var r in QueryByUser(userId))
                Console.WriteLine($"  {r.Id,-12} {r.Channel,-8} {r.TemplateId,-18} {r.Status,-12} {r.StatusNote ?? ""}");
        }
    }

    // ── NotificationService ────────────────────────────────────────────────────

    public class NotificationService
    {
        private readonly TemplateEngine           _templates;
        private readonly UserPreferencesStore     _prefs;
        private readonly DeduplicationStore       _dedup;
        private readonly RateLimiter              _rateLimiter;
        private readonly Dictionary<NotificationChannel, IChannelAdapter> _adapters;
        private readonly DeliveryLog              _log;
        private const int MaxRetries = 3;

        public NotificationService(
            TemplateEngine           templates,
            UserPreferencesStore     prefs,
            DeduplicationStore       dedup,
            RateLimiter              rateLimiter,
            IEnumerable<IChannelAdapter> adapters,
            DeliveryLog              log)
        {
            _templates   = templates;
            _prefs       = prefs;
            _dedup       = dedup;
            _rateLimiter = rateLimiter;
            _log         = log;
            _adapters    = adapters.ToDictionary(a => a.Channel);
        }

        // Fan-out: send to all requested channels in parallel
        public void Send(NotificationRequest req)
        {
            Console.WriteLine($"\n  → Sending '{req.TemplateId}' to user {req.UserId} " +
                              $"[priority={req.Priority}] channels=[{string.Join(",", req.Channels)}]");

            // Validate template variables up front
            if (_templates.HasMissingVariables(req.TemplateId, req.Variables))
            {
                Console.WriteLine("  ✗ Rejected: template has unresolved variables");
                return;
            }

            var tasks = req.Channels.Select(channel =>
                Task.Run(() => SendToChannel(req, channel))).ToArray();
            Task.WaitAll(tasks);
        }

        private void SendToChannel(NotificationRequest req, NotificationChannel channel)
        {
            var record = _log.Create(req.UserId, channel, req.TemplateId);

            // ① Preference check
            if (!_prefs.IsAllowed(req.UserId, channel, req.Priority))
            {
                _log.Update(record, DeliveryStatus.Skipped, "user opted out");
                Console.WriteLine($"    [{channel}] SKIPPED — user {req.UserId} opted out");
                return;
            }

            // ② Deduplication
            string dedupKey = DeduplicationStore.BuildKey(
                req.UserId, req.TemplateId, req.ContextId, channel);
            if (!_dedup.TryMarkNew(dedupKey))
            {
                _log.Update(record, DeliveryStatus.Skipped, "duplicate (idempotency key seen)");
                Console.WriteLine($"    [{channel}] SKIPPED — duplicate notification");
                return;
            }

            // ③ Rate limiting
            if (!_rateLimiter.TryConsume(req.UserId, channel, req.Priority))
            {
                _log.Update(record, DeliveryStatus.Failed, "rate limit exceeded");
                Console.WriteLine($"    [{channel}] DROPPED — rate limit exceeded for user {req.UserId}");
                return;
            }

            // ④ Render template
            var (subject, body) = _templates.Render(req.TemplateId, req.Variables);

            // ⑤ Dispatch with retry + exponential backoff
            if (!_adapters.TryGetValue(channel, out IChannelAdapter adapter))
            {
                _log.Update(record, DeliveryStatus.Failed, "no adapter registered");
                return;
            }

            _log.Update(record, DeliveryStatus.Processing);
            int[] backoffMs = { 0, 100, 500 }; // attempt 1: immediate; 2: 100ms; 3: 500ms

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 1) Thread.Sleep(backoffMs[Math.Min(attempt - 1, backoffMs.Length - 1)]);
                record.Attempts = attempt;

                try
                {
                    adapter.Send(req.UserId, subject, body, req.ContextId);
                    _log.Update(record, DeliveryStatus.Sent);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    // permanent failure — no point retrying
                    _log.Update(record, DeliveryStatus.Failed, $"permanent: {ex.Message}");
                    Console.WriteLine($"    [{channel}] FAILED (permanent) — {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    // transient — retry
                    Console.WriteLine($"    [{channel}] attempt {attempt} failed (transient): {ex.Message}");
                    if (attempt == MaxRetries)
                        _log.Update(record, DeliveryStatus.Failed, $"exhausted {MaxRetries} retries");
                }
            }
        }
    }

    // ── Program ────────────────────────────────────────────────────────────────

    class NotificationProgram
    {
        static void Banner(string title)
        {
            Console.WriteLine("\n╔" + new string('═', 62) + "╗");
            Console.WriteLine("║  " + title.PadRight(60) + "║");
            Console.WriteLine("╚" + new string('═', 62) + "╝");
        }

        static void Main(string[] args)
        {
            // ── shared infrastructure ──────────────────────────────────────────
            var templates   = new TemplateEngine();
            var prefs       = new UserPreferencesStore();
            var dedup       = new DeduplicationStore(TimeSpan.FromMinutes(5));
            var rateLimiter = new RateLimiter(maxTokens: 3, refillPeriod: TimeSpan.FromHours(1));
            var deliveryLog = new DeliveryLog();

            var emailAdapter = new EmailAdapter();
            var smsAdapter   = new SmsAdapter();
            var pushAdapter  = new PushAdapter();
            var inAppAdapter = new InAppAdapter();

            var svc = new NotificationService(
                templates, prefs, dedup, rateLimiter,
                new IChannelAdapter[] { emailAdapter, smsAdapter, pushAdapter, inAppAdapter },
                deliveryLog);

            // ── set up user preferences ────────────────────────────────────────
            // user 42: email+push enabled, SMS opted out
            prefs.Set(42, NotificationChannel.Email, true);
            prefs.Set(42, NotificationChannel.Push,  true);
            prefs.Set(42, NotificationChannel.Sms,   false);
            prefs.Set(42, NotificationChannel.InApp, true);

            // user 99: all channels enabled
            prefs.Set(99, NotificationChannel.Email, true);
            prefs.Set(99, NotificationChannel.Sms,   true);
            prefs.Set(99, NotificationChannel.Push,  true);

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 1: Multi-channel fan-out — order confirmed");
            // ══════════════════════════════════════════════════════════════════
            // Order service publishes one event; notification service fans out to all channels
            svc.Send(new NotificationRequest
            {
                RequestId  = "req-001",
                UserId     = 42,
                TemplateId = "order_confirmed",
                ContextId  = "ORD-301",
                Priority   = NotificationPriority.High,
                Channels   = new List<NotificationChannel>
                    { NotificationChannel.Email, NotificationChannel.Sms,
                      NotificationChannel.Push,  NotificationChannel.InApp },
                Variables  = new Dictionary<string, string>
                {
                    ["firstName"]     = "Alice",
                    ["orderId"]       = "ORD-301",
                    ["total"]         = "$149.99",
                    ["estimatedDate"] = "Mar 20"
                }
            });

            deliveryLog.Print(42);

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 2: Deduplication — same notification sent twice");
            // ══════════════════════════════════════════════════════════════════
            // Producer retries (network hiccup) — user must NOT get duplicate email
            Console.WriteLine("\n  First send (new):");
            svc.Send(new NotificationRequest
            {
                RequestId  = "req-002",
                UserId     = 99,
                TemplateId = "payment_failed",
                ContextId  = "PAY-55",
                Priority   = NotificationPriority.High,
                Channels   = new List<NotificationChannel>
                    { NotificationChannel.Email, NotificationChannel.Push },
                Variables  = new Dictionary<string, string>
                {
                    ["firstName"] = "Bob",
                    ["orderId"]   = "ORD-302",
                    ["total"]     = "$79.00"
                }
            });

            Console.WriteLine("\n  Second send (producer retry — should be deduped):");
            svc.Send(new NotificationRequest
            {
                RequestId  = "req-002",        // same contextId → same dedup key
                UserId     = 99,
                TemplateId = "payment_failed",
                ContextId  = "PAY-55",         // same → duplicate
                Priority   = NotificationPriority.High,
                Channels   = new List<NotificationChannel>
                    { NotificationChannel.Email, NotificationChannel.Push },
                Variables  = new Dictionary<string, string>
                {
                    ["firstName"] = "Bob",
                    ["orderId"]   = "ORD-302",
                    ["total"]     = "$79.00"
                }
            });

            deliveryLog.Print(99);

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 3: Rate limiting — 4th notification exceeds 3/hour");
            // ══════════════════════════════════════════════════════════════════
            // Rate limiter set to 3 tokens/hour; 4 sends for same user+channel
            for (int i = 1; i <= 4; i++)
            {
                Console.WriteLine($"\n  Promo send #{i}:");
                svc.Send(new NotificationRequest
                {
                    RequestId  = $"promo-{i}",
                    UserId     = 42,
                    TemplateId = "promo",
                    ContextId  = $"PROMO-{i}",    // different contextId → passes dedup
                    Priority   = NotificationPriority.Marketing,
                    Channels   = new List<NotificationChannel> { NotificationChannel.Email },
                    Variables  = new Dictionary<string, string>
                    {
                        ["firstName"]  = "Alice",
                        ["promoTitle"] = $"Spring Sale #{i}",
                        ["promoBody"]  = "50% off all items this weekend!"
                    }
                });
            }

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 4: CRITICAL bypasses rate limit — OTP always goes through");
            // ══════════════════════════════════════════════════════════════════
            // Even after rate limit hit, CRITICAL notifications are never dropped
            Console.WriteLine("\n  OTP (CRITICAL) after rate limit exhausted:");
            svc.Send(new NotificationRequest
            {
                RequestId  = "otp-001",
                UserId     = 42,
                TemplateId = "otp",
                ContextId  = "OTP-SESSION-789",
                Priority   = NotificationPriority.Critical,
                Channels   = new List<NotificationChannel> { NotificationChannel.Email },
                Variables  = new Dictionary<string, string>
                {
                    ["code"] = "482931"
                }
            });

            // ══════════════════════════════════════════════════════════════════
            Banner("Scenario 5: Retry on transient failure — email adapter recovers");
            // ══════════════════════════════════════════════════════════════════
            // EmailAdapter fails first 2 attempts, succeeds on attempt 3
            emailAdapter.SimulateTransientFail = true;
            Console.WriteLine("\n  Sending order_confirmed with transient SMTP failure:");
            svc.Send(new NotificationRequest
            {
                RequestId  = "req-retry-001",
                UserId     = 99,
                TemplateId = "order_confirmed",
                ContextId  = "ORD-500",
                Priority   = NotificationPriority.High,
                Channels   = new List<NotificationChannel> { NotificationChannel.Email },
                Variables  = new Dictionary<string, string>
                {
                    ["firstName"]     = "Bob",
                    ["orderId"]       = "ORD-500",
                    ["total"]         = "$200.00",
                    ["estimatedDate"] = "Mar 25"
                }
            });
            emailAdapter.SimulateTransientFail = false;

            // Final delivery summary across all users
            Console.WriteLine("\n" + new string('═', 64));
            Console.WriteLine("  Final delivery log — all records");
            Console.WriteLine(new string('═', 64));
            deliveryLog.Print(42);
            deliveryLog.Print(99);
        }
    }
}
