using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostPublicKeyProvider — loads the verify-only PUBLIC key for the manifest.
//
// The reconciler host VERIFIES manifests and never signs them: it is given the 32-byte raw
// Ed25519 PUBLIC key only. The private key does live on this machine (11/09/2026), but only
// the elevated helper reads it — never this service (see SignedDesiredStateStore, key custody).
// This reads the public key from a file under the admin-write manifest root.
//
// FAIL-CLOSED: an absent or malformed key returns Fail. The host then has NO verifier,
// so it cannot verify any manifest and (correctly) does not reconcile — it never falls
// back to an unverified read. A missing/empty PRIVATE key is the norm and never wired
// here (this provider only ever produces a verifier).
//
// 11/09/2026 — the key is the MACHINE's own: the elevated helper derives it from the
// machine's signing key (MachineManifestKey) and writes it next to the manifest at the first
// CommitManifestOp. This loader is the consumption side; it does not embed a key.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostPublicKeyProvider
{
    private const string FileName = "manifest-public.key";
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>The default public-key path under the admin-write manifest root
    /// (sibling of the manifest + sidecar).</summary>
    public static string DefaultPath()
        => Path.Combine(ProgramDataManifestStorage.DefaultRoot(), FileName);

    /// <summary>Loads the 32-byte raw Ed25519 public key. Absent / wrong length /
    /// not-admin-trusted-at-rest → Fail (fail-closed: no key ⇒ no verifier ⇒ the host
    /// refuses to reconcile).</summary>
    /// <param name="enforceTrust">When true (production), the file is trusted only if its
    /// owner is Administrators/SYSTEM and the root is not non-admin-writable
    /// (<see cref="TrustedManifestRoot.EnsureTrustedFile"/>) — a forged key dropped into a
    /// user-owned root is rejected (BLOCKER #1a). Tests pass false to round-trip a temp
    /// file whose owner is the (non-admin) test user.</param>
    public static Result<byte[]> Load(string path, bool enforceTrust = true)
    {
        try
        {
            if (!File.Exists(path))
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    $"Manifest public key not provisioned at '{path}'.");

            // At-rest integrity: a PRESENT-but-untrusted key file (user-owned / in a
            // user-writable root) is an attack, not a missing key — fail closed hard.
            if (enforceTrust)
            {
                var trust = TrustedManifestRoot.EnsureTrustedFile(path);
                if (!trust.IsOk)
                    return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest public key is not admin-trusted at rest: {trust.Error!.Value.Message}");
            }

            var key = File.ReadAllBytes(path);
            if (key.Length != Ed25519PublicKeyLength)
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    $"Manifest public key has wrong length ({key.Length}, expected {Ed25519PublicKeyLength}).");

            // 11/09/2026 — un fichier de cle qui contient la cle de DEV est refuse, dans toutes les
            // configurations : sa graine privee vit dans les tests du depot.
            if (ManifestDevKeys.EstLaCleDeDev(key))
                return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                    "The provisioned manifest public key is the development key; it is never trusted.");

            return Result<byte[]>.Ok(key);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCodes.SignatureInvalid,
                $"Manifest public key read failed: {ex.Message}");
        }
    }

    /// <summary>Builds a verify-only <see cref="IManifestVerifier"/> from the provisioned
    /// public key, or Fail if no valid (and admin-trusted-at-rest) key is present. Never
    /// produces a signer.</summary>
    public static Result<IManifestVerifier> LoadVerifier(string path, bool enforceTrust = true)
    {
        var key = Load(path, enforceTrust);
        if (!key.IsOk)
            return Result<IManifestVerifier>.Fail(key.Error!.Value.Code, key.Error.Value.Message);

        try
        {
            return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(key.Value!));
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"Manifest public key could not be imported: {ex.Message}");
        }
    }
}
