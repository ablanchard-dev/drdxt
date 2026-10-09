using System.Security.Cryptography;
using System.Text;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HMAC-SHA256 over the canonical request bytes.
//
// SECURITY (master plan §9.14):
//   * This is transport integrity only — it proves the request was not
//     altered in flight and that the peer holds the shared session key.
//     It is not authorization. Authorization is a separate, fail-closed seam
//     (ICallerAuthenticator) that decides whether the *caller* is allowed to
//     run privileged ops at all. Never collapse the two.
//   * Verification uses a constant-time compare
//     (CryptographicOperations.FixedTimeEquals) to avoid timing oracles.
//   * The session key is INJECTED (no secret baked into the binary). M0 tests
//     pass a test key; M2 provisions a real per-session key.
// -----------------------------------------------------------------------
public static class RequestHmac
{
    // ASCII unit separator (0x1F). Used as an unambiguous field delimiter so
    // distinct (kind, nonce) pairs can never canonicalize to the same bytes.
    private const string FieldSeparator = "\u001F"; // ASCII Unit Separator (0x1F) - explicit escape so it survives copy/paste and editor cleanups

    /// <summary>
    /// Canonical, unambiguous byte serialization of everything the HMAC covers
    /// (every request field EXCEPT the HMAC itself). Explicit field separators
    /// prevent concatenation ambiguity, e.g. (Kind=1, Nonce="2x") must not
    /// canonicalize the same as (Kind=12, Nonce="x"). The "v1" version tag lets
    /// the canonical form evolve safely if the covered field set grows in M2.
    /// </summary>
    public static byte[] CanonicalBytes(OpKind kind, string nonce)
    {
        var canonical = $"v1{FieldSeparator}{(int)kind}{FieldSeparator}{nonce}";
        return Encoding.UTF8.GetBytes(canonical);
    }

    /// <summary>Computes the base64 HMAC-SHA256 over the canonical bytes.</summary>
    public static string Compute(ReadOnlySpan<byte> key, OpKind kind, string nonce)
    {
        var data = CanonicalBytes(kind, nonce);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var written = HMACSHA256.HashData(key, data, mac);
        return Convert.ToBase64String(mac[..written]);
    }

    /// <summary>
    /// Verifies the supplied base64 HMAC against a freshly computed one in
    /// constant time. Returns false fail-closed for any malformed input
    /// (bad base64, wrong length) — never throws to the caller.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> key, OpKind kind, string nonce, string hmacBase64)
    {
        // Missing/empty HMAC → fail-closed. Guard before decoding: JSON can set
        // this to null despite the non-nullable signature, and Convert.FromBase64String(null)
        // throws ArgumentNullException (not FormatException). The guard keeps the
        // "never throws to the caller" invariant true for every input.
        if (string.IsNullOrEmpty(hmacBase64))
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
        var data = CanonicalBytes(kind, nonce);
        HMACSHA256.HashData(key, data, expected);

        // FixedTimeEquals is itself length-safe and constant-time.
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
