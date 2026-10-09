namespace DrDxt.TestSupport;

// -----------------------------------------------------------------------
// TEST-only fixture: the dev/test PRIVATE seed paired with
// DrDxt.Reconciler.Manifest.ManifestDevKeys.PublicKey.
//
// Why this lives in tests/ and not in src/ (M5 review finding #3):
// a usable manifest-signing PRIVATE key must never ship in a production binary — anyone
// who decompiles DrDxt.Reconciler.dll and gets this seed could forge a desired-state
// manifest the boot reconciler reapplies. The 32-byte PUBLIC key stays in src/
// (ManifestDevKeys.PublicKey — safe, it can only verify); its matching PRIVATE seed lives
// Here, compiled only into the test assemblies (in every config, incl. Release), so the
// hermetic tests can sign a fixture manifest the embedded public key then verifies, with
// ZERO private-key bytes in any shipping DLL.
//
// This file is LINKED into the reconciler / host / app test projects (a single source of
// truth — no per-project copies of the seed to drift).
// -----------------------------------------------------------------------
internal static class ManifestDevTestKeys
{
    /// <summary>The 32-byte raw Ed25519 PRIVATE seed matched to
    /// <c>ManifestDevKeys.PublicKey</c>. TEST-only — used by tests to sign fixture
    /// manifests the embedded public key verifies. Never present in a shipping binary.</summary>
    public static byte[] DevPrivateSeed =>
    [
        0x31, 0x5D, 0xED, 0x95, 0x60, 0xB6, 0xA4, 0x94,
        0x1D, 0x5D, 0x11, 0xD4, 0x7A, 0x97, 0xE3, 0x14,
        0x73, 0xA0, 0x94, 0x17, 0xAE, 0x1E, 0x4C, 0xA0,
        0xD8, 0x9D, 0x85, 0x3E, 0x67, 0xB2, 0x6E, 0x1A,
    ];
}
