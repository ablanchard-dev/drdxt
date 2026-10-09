using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// M4 — the sealed DesiredState manifest + the seams that protect it.
//
// AUTHORITATIVE security model (master plan §9.12 / B1, plan 04 §2):
//   * Integrity = an ASYMMETRIC signature over the canonical bytes. The app
//     commits with the PRIVATE key; the reconciler service holds only the
//     PUBLIC key and VERIFIES before it would ever re-apply anything.
//   * DPAPI / symmetric MAC are explicitly REFUSED for manifest integrity — the
//     machine owner can re-derive a symmetric key and forge a "valid" manifest.
//     DPAPI stays reserved for the license file (§9.5), never the manifest.
//   * Defence-in-depth = the file lives under a ProgramData admin-write-only ACL
//     (IManifestStorage). The ACL complements the signature; it never replaces it.
//
// Every seam returns a Result so an unavailable FS / crypto path fails CLEAN
// (never throws across the boundary).
// -----------------------------------------------------------------------

/// <summary>The on-disk, versioned envelope wrapping a committed
/// <see cref="DesiredState"/>. The body holds only validated, committed tweaks
/// (plan §2 / B4: a tweak is in here only if its stability test passed and its
/// measured benefit beat the margin). The envelope is what gets signed.
///
/// 11/09/2026 — la licence est sortie d'ici (revue). Ce manifeste portait un champ
/// <c>ExpiresAt</c> : la duree du droit de reappliquer, signee par la cle de la machine. Erreur
/// de categorie : cette cle protege contre un programme non eleve, jamais contre le proprietaire
/// ADMINISTRATEUR de la machine, qui peut la lire sous %ProgramData%\DrDxt et resigner ce qu'il
/// veut — « licence valable dix ans » compris. Le droit de reappliquer se lit desormais dans le
/// fichier signe par l'EDITEUR (DrDxt.Licensing.LicenceDeLaMachine, verifie par LicenseTrust) :
/// une signature que la machine ne sait pas produire. Ce manifeste ne dit plus que ce que DrDXT a
/// pose, ce qui est exactement ce que la cle de la machine sait garantir.
///
/// Un manifeste ecrit avant ce lot porte encore un <c>expiresAt</c> : il est simplement IGNORE
/// a la relecture (champ inconnu), sa signature reste valide et ses origines restent rendues a la
/// desinstallation.</summary>
public sealed record DesiredStateManifest(
    int SchemaVersion,
    DesiredState State,
    DateTimeOffset CommittedAt)
{
    /// <summary>The single schema version this build reads/writes. A manifest with
    /// any other version is refused fail-clean (no guessing — plan T5).</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Reads the FULL signed, verified manifest — pour savoir s'il y a quelque chose
/// d'ARME (la licence, elle, ne se lit plus ici : LicenceDeLaMachine). Implemented by
/// <c>SignedDesiredStateStore</c> alongside <see cref="IDesiredStateStore"/> and sharing
/// the exact same verify-before-parse path (the Ed25519 signature is the integrity
/// authority). Returns Ok(null) when no manifest exists yet (first install); a tamper /
/// schema / IO error fails clean (fail-closed).</summary>
public interface ISignedManifestReader
{
    Task<Result<DesiredStateManifest?>> LoadManifestAsync();
}

/// <summary>The detached signature + the canonical bytes it was computed over.
/// Persisted alongside the manifest body so the verifier checks the exact bytes.</summary>
public sealed record SignedManifestEnvelope(
    string CanonicalJson,
    string SignatureBase64);

// -----------------------------------------------------------------------
// ISignatureScheme — the asymmetric sign/verify seam.
//
// The reconciler (verify side) is constructed with a verifier that holds only the
// public key. The signing capability is a SEPARATE object that holds the private
// key; it is never injected into the reconcile path. Tests use an in-memory fake;
// production uses Ed25519SignatureScheme (NSec / libsodium).
// -----------------------------------------------------------------------

/// <summary>Verifies a detached signature over a canonical byte payload using a
/// PUBLIC key only. Held by the reconciler/verifier side.</summary>
public interface IManifestVerifier
{
    /// <summary>True iff <paramref name="signature"/> is a valid signature over
    /// <paramref name="canonicalBytes"/> under the public key. Never throws — a
    /// malformed signature / wrong length returns false (fail-closed).</summary>
    bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature);
}

/// <summary>Signs a canonical byte payload with the PRIVATE key. Held only by the elevated
/// helper (MachineManifestCommit, 11/09/2026) — never by the App, never injected into the
/// reconcile/verify path. The key is the machine's own (MachineManifestKey, admin-only under
/// %ProgramData%\DrDxt); the reconciler service and the App are given only the public key.</summary>
public interface IManifestSigner
{
    /// <summary>Produces a detached signature over <paramref name="canonicalBytes"/>.
    /// Fail-clean on any crypto error.</summary>
    Result<byte[]> Sign(ReadOnlySpan<byte> canonicalBytes);
}

// -----------------------------------------------------------------------
// IManifestStorage — the path + ACL seam (defence-in-depth, plan T2).
//
// Production writes under %ProgramData%\DrDxt with an admin-write-only DACL. Unit
// tests substitute an in-memory storage so they never touch the real filesystem or
// require admin. Writes are ATOMIC (temp + replace) so a crash never leaves a
// half-written manifest (plan T6 / I2).
// -----------------------------------------------------------------------
public interface IManifestStorage
{
    /// <summary>Atomically persists the signed envelope under the admin-write root.
    /// IO failure → Fail(ErrorCodes.IoFailure).</summary>
    Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct);

    /// <summary>Reads the persisted envelope. Returns Ok(null) when no manifest
    /// exists yet (first install). IO failure → Fail(ErrorCodes.IoFailure).</summary>
    Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct);
}
