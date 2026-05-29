// CardVault — the only system that ever sees raw card PANs.
//
// PCI DSS scope reduction depends on this isolation: the vault sits in its own
// network segment, encrypts PANs at rest with HSM-managed keys, and exposes
// only opaque tokens to the rest of the system. The main payment service stores
// tokens like "tok_abc123" — useless to an attacker even if the main DB leaks.
//
// In production: AES-256 at rest with quarterly key rotation, every access
// logged for audit, no debugging output ever logs the PAN.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

public class CardVault
{
    // In production: encrypted with HSM; never logged
    private readonly Dictionary<string, string> _tokenToPan = new Dictionary<string, string>();

    public string Tokenize(string pan)
    {
        var token = "tok_" + Convert.ToHexString(RandomBytes(8)).ToLower();
        _tokenToPan[token] = pan;  // stored encrypted in real system
        return token;
    }

    // Only called by payment engine during authorization
    public string Detokenize(string token) =>
        _tokenToPan.TryGetValue(token, out var pan) ? pan : null;

    public bool IsValid(string token) => _tokenToPan.ContainsKey(token);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
