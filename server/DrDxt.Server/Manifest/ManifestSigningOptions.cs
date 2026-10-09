namespace DrDxt.Server.Manifest;

// Bound from IConfiguration (env vars / appsettings). The committed appsettings holds an EMPTY
// placeholder; the real 32-byte raw Ed25519 PRIVATE seed (base64) arrives via the environment
// variable ManifestSigning__Ed25519PrivateSeedBase64 and is NEVER committed.
//
// This is the SERVER-SIDE CUSTODY of the manifest-signing private key: the reconciler moat
// reapplies a SIGNED desired-state manifest, and the signature is its integrity root. The
// PRIVATE key lives ONLY here (from env); the client/service binaries hold ONLY the matching
// PUBLIC key (DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey is the dev placeholder; a
// release provisions the production public key out-of-band) and can therefore only VERIFY.
public sealed class ManifestSigningOptions
{
    public const string SectionName = "ManifestSigning";

    // 32-byte raw Ed25519 PRIVATE seed, base64-encoded. SECRET — env only, server-side only.
    // Empty/malformed ⇒ the signer is fail-closed (it refuses to sign; never throws at startup).
    public string Ed25519PrivateSeedBase64 { get; set; } = string.Empty;
}
