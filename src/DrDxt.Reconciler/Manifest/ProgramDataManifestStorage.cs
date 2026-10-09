using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// ProgramDataManifestStorage — the production IManifestStorage (plan T2).
//
// Defence-in-depth (master §9.12 / B1): the manifest lives under
//   %ProgramData%\DrDxt\desired-state.manifest
// with a DACL that grants write to Administrators + SYSTEM only and read to
// authenticated users. A non-admin user can read it (the reconciler runs low-priv)
// but cannot REWRITE it. This complements — never replaces — the asymmetric
// signature: the signature is the integrity authority; the ACL just raises the bar
// at rest.
//
// Writes are ATOMIC (write to a sibling .tmp, then File.Move/replace) so a crash
// mid-flush never leaves a half-written manifest (plan T6 / I2).
//
// TESTABILITY: the path + the DACL descriptor are pure static helpers (asserted in
// unit tests with no FS / no admin). The instance is constructed with an explicit
// root so tests round-trip in a temp dir; `applyAcl` is opt-out for those tests.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class ProgramDataManifestStorage : IManifestStorage
{
    private const string FileName = "desired-state.manifest";

    private readonly string _root;
    private readonly bool _applyAcl;

    public ProgramDataManifestStorage() : this(DefaultRoot(), applyAcl: true) { }

    /// <param name="root">The directory the manifest lives in.</param>
    /// <param name="applyAcl">Apply the admin-write DACL on create (false in unit
    /// tests, which run without admin in a temp dir).</param>
    public ProgramDataManifestStorage(string root, bool applyAcl)
    {
        _root = root;
        _applyAcl = applyAcl;
    }

    private string FilePath => Path.Combine(_root, FileName);

    /// <summary>%ProgramData%\DrDxt — the admin-write root.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DrDxt");

    /// <summary>The hardened admin-write root security: owner pinned to Administrators,
    /// Write for Administrators + SYSTEM, read for authenticated users; protected (does
    /// not inherit a looser parent ACL). Pinning the owner closes the "creator keeps an
    /// implicit WRITE_DAC and reopens the DACL" hole (BLOCKER #1b) — see
    /// <see cref="TrustedManifestRoot.BuildHardenedSecurity"/>.</summary>
    public static DirectorySecurity BuildAdminWriteDacl()
        => TrustedManifestRoot.BuildHardenedSecurity();

    public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope);

            if (_applyAcl)
            {
                // Production: write HARDENED — ensure/repair the admin-owned root, write
                // atomically, and PIN the manifest file's owner=Administrators + protected
                // admin-only DACL (BLOCKER #1 / F3) so the SYSTEM reconciler trusts it at
                // rest even when the writer is an admin USER (not SYSTEM). Fails LOUD if the
                // owner cannot be set (caller not elevated) — never leaves a user-owned
                // manifest the service would silently reject.
                return Task.FromResult(
                    TrustedManifestRoot.WriteTrustedFile(FilePath, Encoding.UTF8.GetBytes(json)));
            }

            // Test/no-ACL mode (temp dir the test user owns): plain atomic write.
            if (!Directory.Exists(_root))
                Directory.CreateDirectory(_root);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            // Atomic replace: File.Move(overwrite) is an atomic rename on NTFS.
            File.Move(tmp, FilePath, overwrite: true);
            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, $"Manifest write failed: {ex.Message}"));
        }
    }

    /// <summary>11/09/2026 — la reprise : le manifeste illisible est RENOMME a cote de lui, jamais efface
    /// (il reste la seule trace de ce qu'il contenait). Absent ⇒ rien a faire. Le renommage garde le
    /// descripteur de securite du fichier.</summary>
    public Result MettreDeCote() => MiseDeCote.Renommer(FilePath);

    /// <summary>11/09/2026 (lot 6e) — apres un rendu a la desinstallation. Le manifeste restait en place :
    /// une reinstallation y retrouvait des origines deja rendues, capturees avant la desinstallation
    /// precedente, et le jour ou le service les rejouerait il reecrirait un passe que l'utilisateur a
    /// quitte. Il est renomme `desired-state.manifest.rendu-<heure UTC>` — jamais efface : il reste la
    /// trace de ce que la desinstallation a lu.</summary>
    public Result MettreDeCoteApresRendu() => MiseDeCote.Renommer(FilePath, "rendu");

    public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(FilePath))
                return Task.FromResult(Result<SignedManifestEnvelope?>.Ok(null));

            // At-rest integrity (BLOCKER #1a): before trusting a PRESENT manifest, assert
            // its owner is Administrators/SYSTEM and the root is not non-admin-writable. A
            // poisoned root (e.g. a user-created %ProgramData%\DrDxt) → fail-closed (treat
            // as tampered), so nothing is reapplied. Defence-in-depth on top of the Ed25519
            // signature (the integrity authority). Skipped only in the test/no-ACL mode.
            if (_applyAcl)
            {
                var trust = TrustedManifestRoot.EnsureTrustedFile(FilePath);
                if (!trust.IsOk)
                    return Task.FromResult(Result<SignedManifestEnvelope?>.Fail(
                        ErrorCodes.ManifestTampered,
                        $"Manifest root is not admin-trusted at rest: {trust.Error!.Value.Message}"));
            }

            var json = File.ReadAllText(FilePath);
            var env = JsonSerializer.Deserialize<SignedManifestEnvelope>(json);
            // 26/09/2026 : un fichier present qui contient « null » n'est pas un fichier absent.
            return Task.FromResult(env is null
                ? Result<SignedManifestEnvelope?>.Fail(ErrorCodes.ManifestTampered, "Manifest file is present but empty.")
                : Result<SignedManifestEnvelope?>.Ok(env));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<SignedManifestEnvelope?>.Fail(ErrorCodes.IoFailure, $"Manifest read failed: {ex.Message}"));
        }
    }
}
