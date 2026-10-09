using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperAuthenticatorFactory — binds the elevated helper's caller authenticator
// from the provisioned trusted App image path (--app-path / DRDXT_HELPER_APP_PATH).
//
// FIX 2 — path + SHA-256 image identity is the DEFAULT for the on-demand launch
// flow. The App passes its own exe path to the helper at launch; the helper hashes
// That path at startup and binds DrDxtAppCallerAuthenticator.WithImageIdentity
// (path AND SHA-256, constant-time compare). This closes the swapped-exe-at-the-
// same-path gap with NO code signing. It applies to both the handshake (on-demand)
// flow and the env-var legacy smoke tools (which also pass --app-path) — they get
// the stronger gate for free, since their exe is on disk and hashable.
//
// Never fail open: if the path is absent, or its image cannot be hashed at startup
// (missing/unreadable/locked), we cannot establish a path+hash identity, so we fall
// back to deny-by-default (every caller denied) — never to path-only, never to
// allow-all.
//
// Known limit, until the App is code-signed: replace the startup image-hash with real Authenticode
// verification of the caller image (WinVerifyTrust WINTRUST_ACTION_GENERIC_VERIFY_V2
// + pin OUR publisher cert, 07-SECURITY §2.4/§3.2) once DrDxt.App is code-signed.
// The image-hash is the UNSIGNED dev-build stand-in; the factory shape does not
// change when that lands — only how the expected identity is computed/compared.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
internal static class HelperAuthenticatorFactory
{
    /// <summary>Builds the caller authenticator for the given trusted App path.
    /// Returns a path+SHA-256 authenticator when the path's image hashes at startup;
    /// otherwise a deny-by-default authenticator (fail-closed). <paramref name="hashFile"/>
    /// computes the expected hash from the path (defaults to the real file hasher);
    /// <paramref name="imageIdentity"/> is the resolver the produced authenticator uses
    /// to identify the CONNECTING peer (defaults to the real Win32 path+hash resolver).</summary>
    public static ICallerAuthenticator Build(
        string? appPath,
        Func<string, byte[]?>? hashFile = null,
        ICallerImageIdentity? imageIdentity = null)
    {
        var member = TryBuildMember(appPath, hashFile ?? Win32CallerImageIdentity.TryHashFile, imageIdentity);
        // No hashable path → deny-by-default (never path-only, never allow-all).
        return member ?? new DenyByDefaultCallerAuthenticator();
    }

    /// <summary>26/09/2026 — les chemins demandes (argument ou variable d'environnement) qui peuvent servir de
    /// confiance. Ils arrivent de quiconque lance le helper : installe sous Program Files, un programme du compte
    /// pouvait le lancer avec --app-path=&lt;lui-meme&gt;, l'UAC affichant le nom de DrDXT. Installe, seuls les
    /// executables du dossier du helper (reserve aux administrateurs) restent ; hors installation (outils de
    /// developpement), rien ne change.</summary>
    internal static IReadOnlyList<string> CheminsDeConfiance(
        IEnumerable<string?> demandes, string dossierDuHelper, string programFiles)
    {
        var valides = demandes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        if (!EstInstalle(dossierDuHelper, programFiles) || Dossier(dossierDuHelper) is not { } dossier) return valides;
        return valides.Where(p => Dossier(Path.GetDirectoryName(p)) is { } d
                                  && (d.Equals(dossier, StringComparison.OrdinalIgnoreCase) || Sous(d, dossier)))
            .ToList();
    }

    /// <summary>26/09/2026 — le helper tourne-t-il depuis Program Files (dossier reserve aux administrateurs) ?
    /// Installe, il ne croit que son dossier (<see cref="CheminsDeConfiance"/>) et ignore la cle de session
    /// d'environnement ; hors installation, les outils de developpement gardent ces deux entrees.</summary>
    internal static bool EstInstalle(string dossierDuHelper, string programFiles)
        => Dossier(dossierDuHelper) is { } dossier && Dossier(programFiles) is { } pf && Sous(dossier, pf);

    private static string? Dossier(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(chemin)); }
        catch { return null; }
    }

    private static bool Sous(string enfant, string parent)
        => enfant.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a MULTI-caller authenticator from a SET of trusted image paths
    /// (e.g. the interactive App AND the reconciler host). Each path whose image hashes
    /// at startup becomes a path+SHA-256 member; the peer is authorized iff it matches
    /// ONE of them (<see cref="CompositeCallerAuthenticator"/>). Same fail-closed rules
    /// as <see cref="Build"/>, applied per member: a null/blank/unhashable path is
    /// SKIPPED, and if NO path yields a member the result is deny-by-default (never
    /// path-only, never allow-all). All members share the same connecting-peer resolver
    /// (<paramref name="imageIdentity"/>); it only resolves the peer's (path,hash) — the
    /// per-member EXPECTED identity differs, so distinct callers are still distinguished.</summary>
    public static ICallerAuthenticator BuildMulti(
        IEnumerable<string> appPaths,
        Func<string, byte[]?>? hashFile = null,
        ICallerImageIdentity? imageIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        var hasher = hashFile ?? Win32CallerImageIdentity.TryHashFile;

        var members = new List<ICallerAuthenticator>();
        foreach (var path in appPaths)
        {
            var member = TryBuildMember(path, hasher, imageIdentity);
            if (member is not null)
                members.Add(member);
        }

        // No path resolved/hashed → deny-by-default (never fail open).
        return members.Count == 0
            ? new DenyByDefaultCallerAuthenticator()
            : new CompositeCallerAuthenticator(members);
    }

    // Builds ONE path+SHA-256 authenticator for a trusted path, or null when the path
    // is blank or its image cannot be hashed at startup (so the caller skips/denies).
    // Never returns a path-only authenticator — no hash means no identity.
    private static ICallerAuthenticator? TryBuildMember(
        string? appPath, Func<string, byte[]?> hasher, ICallerImageIdentity? imageIdentity)
    {
        if (string.IsNullOrWhiteSpace(appPath))
            return null;

        byte[]? expectedHash;
        try
        {
            expectedHash = hasher(appPath);
        }
        catch
        {
            // Hashing the provisioned image threw (locked/IO/access) → cannot prove
            // identity → skip this path (the caller denies-by-default if it was the
            // only one). Never fall back to path-only.
            return null;
        }

        if (expectedHash is null || expectedHash.Length == 0)
            return null;

        return DrDxtAppCallerAuthenticator.WithImageIdentity(appPath, expectedHash, imageIdentity);
    }
}
