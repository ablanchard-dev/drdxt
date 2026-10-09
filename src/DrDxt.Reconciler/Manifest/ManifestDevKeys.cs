using System;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// ───────────────────────────────────────────────────────────────────────────────
//  EMBEDDED PUBLIC MATERIAL ONLY — NO SECRET MAY EVER LIVE HERE, IN ANY CONFIG.
// ───────────────────────────────────────────────────────────────────────────────
//
// This file is the client/service ROOT OF TRUST for the reconciler MANIFEST (the
// persistence moat). It holds ONLY material that is safe to ship in a binary that
// anyone can decompile:
//   * the 32-byte raw Ed25519 manifest-verify PUBLIC key — it can ONLY VERIFY a
//     signed desired-state manifest, never mint one. The App + reconciler host build
//     their verifier from this so a self-contained team build VERIFIES real manifests.
//
// NO PRIVATE KEY BYTES LIVE HERE (M5 review finding #3). A usable manifest-signing
// PRIVATE key is FORBIDDEN in any shipping code path — and unlike a #if DEBUG fence,
// this file carries NO seed bytes in EITHER Debug or Release. The dev/test PRIVATE seed
// that the unit tests use to sign fixture manifests lives ONLY in the test assemblies
// (DrDxt.TestSupport.ManifestDevTestKeys, tests/Shared) — so a shipping DrDxt.Reconciler.dll
// contains zero usable private-key bytes.
//
// PRODUCTION (11/09/2026 — plus de signataire serveur, la regle est « rien hors du PC ») : la
// vraie cle de signature est PROPRE A CHAQUE MACHINE (MachineManifestKey), creee par le helper
// eleve au premier CommitManifestOp sous %ProgramData%\DrDxt (Administrateurs + SYSTEM
// seulement), et `manifest-public.key` en est derivee. Cette cle de dev n'est jamais acceptee
// ailleurs que dans le repli embarque sous #if DEBUG : un fichier provisionne qui la contient est
// refuse (EstLaCleDeDev), et une cle de machine qui la deriverait aussi. A RELEASE build with no
// provisioned key FAILS CLOSED (a dev-key-signed or unsigned manifest is rejected).
// ───────────────────────────────────────────────────────────────────────────────
public static class ManifestDevKeys
{
    /// <summary>The 32-byte raw Ed25519 PUBLIC key. SAFE to commit — it can only
    /// verify, never sign. The App + reconciler host build their verifier from this so
    /// they VERIFY real manifests in a self-contained DEBUG/team build. A RELEASE build
    /// does NOT trust it — it requires a provisioned production public key (see the
    /// header) and otherwise fails closed. Its matching dev/test PRIVATE seed is
    /// TEST-ONLY and lives in DrDxt.TestSupport.ManifestDevTestKeys (tests/Shared),
    /// NEVER in this shipping assembly.</summary>
    public static byte[] PublicKey =>
    [
        0x97, 0xE0, 0xAC, 0x22, 0x73, 0xD9, 0xEF, 0xC2,
        0x9E, 0x7F, 0x2A, 0x11, 0x3F, 0x4D, 0x19, 0xE9,
        0x15, 0xC0, 0x80, 0x4B, 0x4C, 0x41, 0xE3, 0x4C,
        0xAE, 0x35, 0x81, 0x4C, 0xC4, 0xFE, 0xAE, 0x28,
    ];

    /// <summary>11/09/2026 — cette cle publique est-elle la cle de DEV ? Une cle de machine, un fichier
    /// de cle publique provisionne ou une cle derivee par le helper ne doivent jamais l'etre : sa graine
    /// privee vit dans les tests, donc quiconque a le depot signerait un manifeste accepte.</summary>
    public static bool EstLaCleDeDev(ReadOnlySpan<byte> clePublique) => clePublique.SequenceEqual(PublicKey);

    /// <summary>Builds the verify-only <see cref="IManifestVerifier"/> from the embedded
    /// PUBLIC key. Never fails (the embedded key is well-formed by construction); wrapped
    /// in a Result so a future provisioned-key swap keeps the same fail-clean contract.
    /// DEBUG/test convenience only — a RELEASE build verifies with a PROVISIONED key and
    /// never falls back to this (see HostServiceCollectionExtensions / AppManifestPublicKey).</summary>
    public static Result<IManifestVerifier> CreateVerifier()
    {
        try
        {
            return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(PublicKey));
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"Embedded manifest public key could not be imported: {ex.Message}");
        }
    }
}
