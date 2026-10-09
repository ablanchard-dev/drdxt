using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// T12 — The update TRUST GATE. Verifies the Ed25519 signature over the CANONICAL
// update manifest using the embedded, DEDICATED update PUBLIC key
// (UpdateDevKeys.UpdatePublicKey), then — and only then — the payload hash.
//
// The cardinal rule: fail-clean, never fail-open. Every uncertainty — a corrupt
// embedded key, a wrong-length signature, an NSec exception, an unparseable hash,
// a lower version — collapses to a Fail(UPDATE_*). There is no path that proceeds on
// doubt; the caller keeps the CURRENT installed version.
//
// IMPOSED ORDER (mirrors Ed25519LicenseVerifier / the §1 invariant): VerifyManifest
// (signature) first; the caller fetches the payload only on Ok; VerifyPayload checks
// the SHA-256 against the manifest hash SECOND. We only ever trust a hash that came
// from a manifest we already proved signed.
//
// ASYMMETRY (same as Ed25519ManifestVerifier): built from the PUBLIC key alone; can
// only verify, never forge a manifest. The release pipeline signs out-of-band.
// -----------------------------------------------------------------------

/// <summary>Ed25519 update verifier — holds the 32-byte PUBLIC key only.</summary>
public sealed class UpdateVerifier : IUpdateVerifier
{
    private readonly PublicKey _publicKey;

    private UpdateVerifier(PublicKey publicKey) => _publicKey = publicKey;

    /// <summary>Builds a verifier from a 32-byte raw Ed25519 public key. Fail-clean:
    /// a corrupt / wrong-length key returns Fail(UPDATE_SIG_INVALID) instead of throwing,
    /// so a future bad key swap can never crash the gate (mirrors
    /// Ed25519LicenseVerifier.Create).</summary>
    public static Result<IUpdateVerifier> Create(ReadOnlySpan<byte> publicKey)
    {
        try
        {
            var imported = PublicKey.Import(
                SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
            return Result<IUpdateVerifier>.Ok(new UpdateVerifier(imported));
        }
        catch (Exception ex)
        {
            return Result<IUpdateVerifier>.Fail(
                UpdateErrorCodes.SignatureInvalid,
                $"Embedded update public key could not be imported: {ex.Message}");
        }
    }

    public Result VerifyManifest(UpdateManifest manifest)
    {
        if (manifest is null)
            return Result.Fail(UpdateErrorCodes.SignatureInvalid, "Update manifest is absent.");

        // Decode the detached signature. Absent / empty / non-base64 / wrong-length →
        // Refuse. Never "ok by default".
        byte[] signature;
        try
        {
            if (string.IsNullOrEmpty(manifest.Signature))
                return Result.Fail(
                    UpdateErrorCodes.SignatureInvalid, "Update manifest signature is absent.");
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature is not valid base64.");
        }

        if (signature.Length != SignatureAlgorithm.Ed25519.SignatureSize)
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature is malformed.");

        try
        {
            var canonical = CanonicalBytes(manifest);
            // NSec.Verify returns false on any bad/forged signature → fail-clean Result.
            return SignatureAlgorithm.Ed25519.Verify(_publicKey, canonical, signature)
                ? Result.Ok()
                : Result.Fail(
                    UpdateErrorCodes.SignatureInvalid, "Update manifest signature did not verify.");
        }
        catch (Exception)
        {
            // Crypto frontier wrapped — any unexpected fault is a REFUSAL, never a pass.
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature verification faulted.");
        }
    }

    public Result VerifyPayload(
        UpdateManifest manifest, byte[] payload, string installedVersion, bool allowDowngrade)
    {
        if (manifest is null)
            return Result.Fail(UpdateErrorCodes.SignatureInvalid, "Update manifest is absent.");
        if (payload is null)
            return Result.Fail(UpdateErrorCodes.HashMismatch, "Update payload is absent.");

        // No-downgrade rule, enforced before we bless a payload. A lower version (outside
        // an opted-in beta channel) is refused — never silently roll the user back.
        if (!allowDowngrade && IsDowngrade(manifest.Version, installedVersion))
            return Result.Fail(
                UpdateErrorCodes.Downgrade,
                $"Update version {manifest.Version} is older than installed {installedVersion}.");

        // The hash we compare against came from a manifest the caller already proved
        // signed (VerifyManifest must run first). Compute SHA-256 of the bytes and
        // compare to the trusted manifest hash. Mismatch → payload never executed.
        string actual;
        try
        {
            actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return Result.Fail(UpdateErrorCodes.HashMismatch, "Update payload hash could not be computed.");
        }

        var expected = (manifest.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        // Length-checked, case-normalized ordinal compare. (These are PUBLIC hex digests,
        // not a secret — no constant-time requirement, mirroring SpkiPinValidator.)
        if (expected.Length == 0 || !string.Equals(actual, expected, StringComparison.Ordinal))
            return Result.Fail(
                UpdateErrorCodes.HashMismatch,
                "Downloaded update payload SHA-256 does not match the signed manifest.");

        return Result.Ok();
    }

    // -------------------------------------------------------------------
    // Version comparison — true iff `candidate` is STRICTLY older than `installed`.
    // Dotted numeric compare (1.2.10 > 1.2.9). Any unparseable component is treated as
    // 0 so a malformed version never silently passes as "newer"; an equal version is
    // Not a downgrade (re-offering the same version is a no-op upstream, not a rollback).
    // Fail-clean: a parse fault is swallowed and treated as "not a downgrade" only when
    // both parse cleanly — a thrown path returns false (the hash/sig gate still stands).
    // -------------------------------------------------------------------
    internal static bool IsDowngrade(string candidate, string installed)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(installed);
        var len = Math.Max(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            if (ai < bi) return true;   // strictly older → downgrade
            if (ai > bi) return false;  // strictly newer → not a downgrade
        }
        return false; // equal → not a downgrade
    }

    private static int[] ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new[] { 0 };
        var parts = version.Trim().Split('.');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            nums[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return nums;
    }

    // -------------------------------------------------------------------
    // Canonical serialization of the manifest's TRUSTED fields — a DETERMINISTIC byte
    // form so the signature is computed/verified over the exact same bytes on both
    // sides. The signature field itself is not part of the signed bytes (it is the
    // signature). Mirrors Ed25519LicenseVerifier.Canonicalize:
    //   * fixed field order (channel, version, url, sha256) written explicitly;
    //   * channel as its INVARIANT enum name (stable across runtimes/cultures);
    //   * no indentation / culture-sensitive formatting; relaxed (non-over-escaping)
    //     encoder, since these bytes are only ever signed/verified, never embedded in HTML.
    // -------------------------------------------------------------------
    // SEC5 (09/10/2026) : public, pour que l'outil de release (tools/publication/signer.cs) signe EXACTEMENT les octets que
    // l'App vérifie, sans recopier la forme canonique.
    public static byte[] CanonicalBytes(UpdateManifest manifest)
        => Encoding.UTF8.GetBytes(Canonicalize(manifest));

    /// <summary>The deterministic canonical JSON string the signature is computed over.
    /// Exposed to tests (InternalsVisibleTo) so a fixture can sign the exact bytes and so
    /// the canonical form itself is pinned for stability.</summary>
    internal static string Canonicalize(UpdateManifest manifest)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("channel", manifest.Channel.ToString());
            writer.WriteString("version", manifest.Version);
            writer.WriteString("url", manifest.Url);
            writer.WriteString("sha256", manifest.Sha256);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
