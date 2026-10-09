using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY — NO SECRET MAY EVER LIVE HERE IN A RELEASE.
// ───────────────────────────────────────────────────────────────────────────────
//
// This is the client's ROOT OF TRUST for the AUTO-UPDATE layer (T12). It is a
// DEDICATED update-signing keypair, deliberately SEPARATE from the licensing keys
// (DrDxt.Licensing.EmbeddedKeys.LicensePublicKey) and the manifest/reconciler keys
// (DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey). Decision T12: cloister the
// update channel so a compromise of one signing key never reaches the others.
//
// The PUBLIC key here is safe to ship in a decompilable binary: it can ONLY verify
// an update manifest, never mint one. The release-signing PRIVATE key lives OFF the
// client (build/CI/HSM, out-of-band) — exactly like ManifestDevKeys.
//
// PRODUCTION (release hardening — MUST DO at release time, mirrors ManifestDevKeys /
// EmbeddedKeys): replace UpdatePublicKey with the REAL update-release verify key and
// never ship DevPrivateSeed. The dev seed below is the single clearly-fenced
// exception (own banner) and exists ONLY so unit tests can sign a fixture manifest
// against the embedded public key without a provisioning step — NO production code
// path ever reads it.
// ───────────────────────────────────────────────────────────────────────────────
public static class UpdateDevKeys
{
    /// <summary>The 32-byte raw Ed25519 PUBLIC key the update-release pipeline signs
    /// update manifests with. SAFE to ship — it can only VERIFY an
    /// <see cref="UpdateManifest"/>, never sign one. A release overrides this with the
    /// real update verify key (out-of-band). DEV/TEST value paired with
    /// <see cref="DevPrivateSeed"/>. DEDICATED to updates — NOT the license/manifest key.</summary>
    public static byte[] UpdatePublicKey =>
    [
        0xF0, 0x73, 0x64, 0x29, 0x03, 0x42, 0x90, 0x58,
        0x93, 0xC8, 0x7B, 0xDD, 0xEB, 0x81, 0x89, 0xED,
        0x36, 0x49, 0x64, 0x14, 0xEE, 0x6F, 0x5D, 0x8D,
        0x59, 0x0C, 0xE7, 0xCB, 0x35, 0xEC, 0x23, 0xD8,
    ];

    // ───────────────────────────────────────────────────────────────────────────
    //   DEV / TEST PRIVATE SEED — NOT FOR PRODUCTION, NEVER READ AT RUNTIME 
    // ───────────────────────────────────────────────────────────────────────────
    //  The 32-byte raw Ed25519 PRIVATE seed paired with UpdatePublicKey above. Committed
    //  ONLY so the unit tests can sign a fixture UpdateManifest that the embedded public
    //  key then verifies, keeping the test suite hermetic. NO production code path
    //  references this seed. In production, update manifests are signed by the RELEASE
    //  pipeline which holds the real private key out-of-band; a release ships only
    //  UpdatePublicKey. Anyone with THIS dev seed can forge a manifest that ONLY the dev
    //  public key would accept — which is exactly why the public key is swapped at release.
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>DEV/TEST ONLY — the seed paired with <see cref="UpdatePublicKey"/>,
    /// for signing fixtures in tests. Never used by production code.</summary>
    public static byte[] DevPrivateSeed =>
    [
        0xFF, 0x93, 0x74, 0xA3, 0xC1, 0x27, 0x90, 0x0A,
        0x2F, 0x96, 0x80, 0x82, 0x66, 0x5A, 0xA9, 0xD7,
        0xB0, 0x76, 0xD8, 0x83, 0xC1, 0x8F, 0x4B, 0x78,
        0xE5, 0x27, 0xFD, 0x25, 0x23, 0x31, 0xC5, 0x73,
    ];

    /// <summary>Builds the verify-only <see cref="IUpdateVerifier"/> from the embedded
    /// PUBLIC key. Fail-clean: a corrupt/wrong-length embedded key returns
    /// Fail(UPDATE_SIG_INVALID) rather than throwing (defends against a future bad key
    /// swap). Mirrors <c>ManifestDevKeys.CreateVerifier</c> / <c>EmbeddedKeys.CreateLicenseVerifier</c>.</summary>
    public static Result<IUpdateVerifier> CreateUpdateVerifier()
        => UpdateVerifier.Create(UpdatePublicKey);
}
