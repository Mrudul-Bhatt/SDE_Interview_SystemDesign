// Q3. Implement JWT-Based Stateless Authentication
//
// Encode a user's identity and claims into a signed token. The server validates
// the signature on every request — no database lookup, no session store.
// Any server replica can validate any token issued by any other replica.
//
// Why JWT Makes Services Stateless
// ──────────────────────────────────
// Stateful (session-based):
//   Login → server stores { user_id:1, role:"admin" } in Redis
//   Every request → server looks up session in Redis
//   Problem: all servers must share Redis; Redis becomes a single point of failure
//
// Stateless (JWT):
//   Login → server signs { user_id:1, role:"admin" } → returns token to client
//   Every request → server validates signature, reads claims directly from token
//   No database, no Redis, no shared state — any server can verify independently
//
// JWT Trade-offs
// ───────────────
// Advantages:
//   ✓ No server-side session store needed
//   ✓ Any server validates any token — horizontal scaling is trivial
//   ✓ Works across domains (mobile app, browser, microservices)
//
// Disadvantages:
//   ✗ Cannot revoke a token before its expiry time
//     → User changes password → old tokens remain valid until exp
//     → Fix: short expiry (15 min) + refresh token pattern
//     → Or: maintain a token denylist in Redis (partially stateful again)
//
//   ✗ Token payload travels on every request — grows with number of claims
//
//   ✗ Secret key must be distributed to every server securely
//     → Use asymmetric RS256: private key signs, public key verifies
//     → Services only need the public key — private key stays with the auth server
//
// Complexity: Encode O(claims), Decode O(claims) — HMAC is O(token length)

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ArchitectureFundamentals
{
    // -------------------------------------------------------------------------
    // JwtService
    // -------------------------------------------------------------------------
    public class JwtService
    {
        private readonly string _secret;

        public JwtService(string secret)
        {
            _secret = secret;
        }

        // Build a token: header.payload.signature (all Base64Url-encoded)
        public string Encode(Dictionary<string, object> claims, TimeSpan expiry)
        {
            // Standard registered claims — iat and exp are Unix timestamps (seconds
            // since 1970-01-01 UTC). Integer timestamps are language-agnostic: any
            // client or service written in any language can parse them without a date
            // library, and they fit in a 32-bit integer until year 2038 (64-bit beyond).
            claims["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            claims["exp"] = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
            // DateTimeOffset.UtcNow instead of DateTime.Now: UtcNow is not affected by
            // the server's local timezone or DST transitions. Tokens from servers in
            // different timezones would produce different exp values with DateTime.Now.

            string header = Base64UrlEncode(JsonSerializer.Serialize(
                new { alg = "HS256", typ = "JWT" }));
            string payload = Base64UrlEncode(JsonSerializer.Serialize(claims));

            // Signature covers header + payload — if either is tampered the
            // signature won't match and the token is rejected in Decode.
            string signature = Sign($"{header}.{payload}");
            return $"{header}.{payload}.{signature}";
        }

        // Verify signature and expiry, then return the decoded claims.
        // Throws UnauthorizedAccessException if the token is tampered or expired.
        public Dictionary<string, JsonElement> Decode(string token)
        {
            string[] parts = token.Split('.');

            // JWT is always exactly 3 dot-separated segments: header.payload.sig.
            // A different count means the string was truncated or isn't a JWT at all.
            if (parts.Length != 3)
                throw new ArgumentException("Invalid token format — expected 3 segments");

            // ── 1. Signature verification ─────────────────────────────────────
            string expectedSig = Sign($"{parts[0]}.{parts[1]}");

            // CryptographicOperations.FixedTimeEquals instead of ==:
            // A plain string comparison returns early on the first mismatched byte.
            // An attacker can measure how long the comparison takes to guess the
            // signature one byte at a time (timing attack). FixedTimeEquals always
            // takes the same time regardless of where the strings diverge.
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSig),
                Encoding.UTF8.GetBytes(parts[2])))
            {
                throw new UnauthorizedAccessException("Invalid signature — token tampered");
            }

            // ── 2. Payload decode ─────────────────────────────────────────────
            string json = Base64UrlDecode(parts[1]);
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

            // ── 3. Expiry check ───────────────────────────────────────────────
            // Check AFTER verifying the signature: we never trust untrusted data
            // (the exp value in the payload) before proving the token is authentic.
            long exp = claims["exp"].GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
                throw new UnauthorizedAccessException("Token expired");

            return claims;
        }

        private string Sign(string data)
        {
            // HMACSHA256 is a symmetric algorithm: the same secret key both signs and
            // verifies. Simple and fast, but every server that validates tokens must
            // hold the secret. For microservices, RS256 (asymmetric) is safer: only
            // the auth server holds the private key; all other services use the public key.
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(string input) =>
            Base64UrlEncode(Encoding.UTF8.GetBytes(input));

        private static string Base64UrlEncode(byte[] input) =>
            // Standard Base64 uses '+', '/', '=' which are special characters in URLs
            // and HTTP headers. Base64Url replaces them so the token is safe to put
            // in an Authorization header or URL query parameter without percent-encoding.
            Convert.ToBase64String(input)
                .TrimEnd('=')        // padding is redundant in JWT — length is known
                .Replace('+', '-')   // URL-safe substitution
                .Replace('/', '_');  // URL-safe substitution

        private static string Base64UrlDecode(string input)
        {
            // Reverse the URL-safe substitutions before calling FromBase64String.
            string padded = input.Replace('-', '+').Replace('_', '/');

            // Base64 encodes every 3 bytes as 4 characters. The length of a valid
            // Base64 string must be a multiple of 4. We stripped padding in Encode
            // so we must restore it here — otherwise FromBase64String throws.
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------
    public class Program
    {
        public static void Main()
        {
            // Minimum 32-char secret for HMACSHA256 — shorter keys reduce entropy.
            // In production, load this from a secrets manager (AWS Secrets Manager,
            // Azure Key Vault, HashiCorp Vault) — never hard-code in source.
            var jwt = new JwtService(secret: "super-secret-key-min-32-chars!!");

            // =================================================================
            // Scenario 1 — User logs in; token is issued and verified
            // Shows the full encode → decode → claims-read flow that happens
            // on every authenticated request in a JWT-based system.
            // =================================================================
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 1: Login → issue token → verify on request ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            Console.WriteLine("\n  [AUTH SERVER] User logs in:");
            var claims = new Dictionary<string, object>
            {
                ["user_id"] = 42,
                ["email"] = "alice@example.com",
                ["role"] = "admin"
            };
            string token = jwt.Encode(claims, expiry: TimeSpan.FromHours(1));

            // Print only the first 60 chars — a real token is ~200 chars and not
            // human-readable (it's Base64). Showing it truncated proves it's opaque.
            Console.WriteLine($"  Token issued: {token[..Math.Min(60, token.Length)]}...");

            Console.WriteLine("\n  [API SERVER] Request arrives — validating token (no DB lookup):");
            var decoded = jwt.Decode(token);
            Console.WriteLine($"    user_id: {decoded["user_id"]}");
            Console.WriteLine($"    email:   {decoded["email"]}");
            Console.WriteLine($"    role:    {decoded["role"]}");
            Console.WriteLine("  → Any replica can verify this — no Redis, no session DB");

            // =================================================================
            // Scenario 2 — Tampered token is rejected (signature check)
            // An attacker intercepts the token and modifies the last 5 chars
            // of the signature. The HMAC won't match → access denied.
            // CryptographicOperations.FixedTimeEquals prevents the timing attack
            // that would let the attacker guess the signature byte by byte.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 2: Tampered token is rejected               ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            string tampered = token[..^5] + "XXXXX"; // corrupt the last 5 signature chars
            Console.WriteLine("\n  Attacker modifies token signature...");
            try
            {
                jwt.Decode(tampered);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"  Rejected: {ex.Message}");
            }

            // =================================================================
            // Scenario 3 — Expired token is rejected (exp claim check)
            // Token issued with 1ms TTL; after a 10ms sleep it is past expiry.
            // The expiry check runs AFTER signature verification — we never trust
            // an unsigned payload to tell us it isn't expired.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 3: Expired token is rejected                ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            string expired = jwt.Encode(
                new Dictionary<string, object> { ["user_id"] = 99 },
                expiry: TimeSpan.FromMilliseconds(1));

            Console.WriteLine("\n  Token issued with 1ms TTL. Waiting 10ms...");
            Thread.Sleep(10);

            try
            {
                jwt.Decode(expired);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"  Rejected: {ex.Message}");
            }

            // =================================================================
            // Scenario 4 — Refresh token pattern (conceptual)
            // Short-lived access tokens + long-lived refresh tokens solve the
            // revocation problem without making the system fully stateful.
            // =================================================================
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Scenario 4: Refresh token pattern (conceptual)       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            Console.WriteLine(@"
  Problem: JWTs can't be revoked before they expire.
  If a user's access token (1hr) is stolen, attacker has 1hr of valid access.

  Solution — issue two tokens at login:

    Access token  → short TTL (15 min), stateless JWT
    Refresh token → long TTL (7 days), stored in DB / Redis

  Flow:
    Login           → server issues access_token (15min) + refresh_token (7d)
    API request     → client sends access_token; server verifies signature only
    Token expires   → client sends refresh_token to /auth/refresh
    /auth/refresh   → server checks refresh_token in DB, issues new access_token
    User logs out   → server deletes refresh_token from DB (now revocable)
    Password change → server deletes ALL refresh tokens for that user

  Trade-off:
    Refresh token lookup IS a DB read — but it happens only every 15 minutes,
    not on every single API request.  The hot path stays fully stateless.");
        }
    }

} // namespace ArchitectureFundamentals
