using System.Security.Cryptography;
using System.Text;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HMAC-SHA256 over the canonical bytes of a TYPED privileged request.
//
// SECURITY (master plan §9.14): Transport integrity only — it proves the op
// JSON + nonce were not altered in flight and that the peer holds the session
// key. It is not authorization (that is ICallerAuthenticator) and not the
// allowlist (that is OpAllowlist). Verification is constant-time
// (FixedTimeEquals) and never throws on malformed input (fail-closed).
// The key is INJECTED; M2 may keep an ephemeral per-session HMAC key (the
// crypto hardening to asymmetric is M5), but the fail-clean path is tested now.
// -----------------------------------------------------------------------
public static class TypedRequestHmac
{
    // ASCII unit separator (0x1F) — unambiguous field delimiter so distinct
    // (opJson, nonce) pairs can never canonicalize to the same bytes. Explicit
    //  escape so it survives copy/paste and editor cleanups.
    private const string FieldSeparator = "";

    public static byte[] CanonicalBytes(string opJson, string nonce)
    {
        var canonical = $"typed-v1{FieldSeparator}{opJson}{FieldSeparator}{nonce}";
        return Encoding.UTF8.GetBytes(canonical);
    }

    public static string Compute(ReadOnlySpan<byte> key, string opJson, string nonce)
    {
        var data = CanonicalBytes(opJson, nonce);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var written = HMACSHA256.HashData(key, data, mac);
        return Convert.ToBase64String(mac[..written]);
    }

    public static bool Verify(ReadOnlySpan<byte> key, string opJson, string nonce, string? hmacBase64)
    {
        // Guard before decoding: a null/empty HMAC must fail-close, never throw.
        if (string.IsNullOrEmpty(hmacBase64))
            return false;
        if (opJson is null || nonce is null)
            return false;

        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(hmacBase64);
        }
        catch (FormatException)
        {
            return false; // malformed base64 → fail-closed
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var data = CanonicalBytes(opJson, nonce);
        HMACSHA256.HashData(key, data, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
