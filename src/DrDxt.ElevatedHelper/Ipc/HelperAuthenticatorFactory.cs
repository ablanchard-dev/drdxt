using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperAuthenticatorFactory : l'authentificateur d'appelant du helper élevé, depuis le chemin d'image de l'App de
// confiance (--app-path / DRDXT_HELPER_APP_PATH). Le helper calcule au démarrage l'empreinte de ce chemin et branche
// DrDxtAppCallerAuthenticator.WithImageIdentity (chemin et SHA-256, comparaison en temps constant) : l'exe remplacé au
// même chemin est refusé, sans signature de code. Vaut aussi pour les outils de smoke test (qui passent --app-path).
//
// Jamais d'ouverture par défaut : chemin absent ou empreinte incalculable (absente, illisible, verrouillée) → refus de
// tout appelant, jamais le chemin seul, jamais tout autoriser.
//
// Limite connue tant que l'App n'est pas signée : remplacer l'empreinte par une vérification Authenticode de l'appelant
// (WinVerifyTrust WINTRUST_ACTION_GENERIC_VERIFY_V2 + épinglage de notre certificat d'éditeur). La forme de la fabrique
// ne changera pas, seul le calcul de l'identité attendue.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
internal static class HelperAuthenticatorFactory
{
    /// <summary>L'authentificateur chemin + SHA-256 pour le chemin d'App de confiance donné, ou le refus par défaut si
    /// l'empreinte ne se calcule pas (fail-closed). <paramref name="hashFile"/> calcule l'empreinte attendue ;
    /// <paramref name="imageIdentity"/> identifie le pair qui se connecte (par défaut, les vrais calculs Win32).</summary>
    public static ICallerAuthenticator Build(
        string? appPath,
        Func<string, byte[]?>? hashFile = null,
        ICallerImageIdentity? imageIdentity = null)
    {
        var member = TryBuildMember(appPath, hashFile ?? Win32CallerImageIdentity.TryHashFile, imageIdentity);
        // Aucun chemin à empreinte calculable → refus par défaut (jamais le chemin seul, jamais tout autoriser).
        return member ?? new DenyByDefaultCallerAuthenticator();
    }

    /// <summary>Les chemins demandes (argument ou environnement) qui peuvent servir de confiance. Ils viennent de quiconque
    /// lance le helper : un programme du compte pourrait passer --app-path=&lt;lui-meme&gt;, l'UAC affichant le nom de
    /// DrDXT. Installe, seuls les executables du dossier du helper (reserve aux administrateurs) restent ; hors
    /// installation (outils de developpement), rien ne change.</summary>
    internal static IReadOnlyList<string> CheminsDeConfiance(
        IEnumerable<string?> demandes, string dossierDuHelper, string programFiles)
    {
        var valides = demandes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        if (!EstInstalle(dossierDuHelper, programFiles) || Dossier(dossierDuHelper) is not { } dossier) return valides;
        return valides.Where(p => Dossier(Path.GetDirectoryName(p)) is { } d
                                  && (d.Equals(dossier, StringComparison.OrdinalIgnoreCase) || Sous(d, dossier)))
            .ToList();
    }

    /// <summary>Le helper tourne-t-il depuis Program Files (dossier reserve aux administrateurs) ?
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

    /// <summary>Un authentificateur multi-appelant (l'App interactive, l'hôte réconciliateur…) : chaque chemin à
    /// empreinte calculable devient un membre chemin + SHA-256, le pair passe s'il correspond à l'un
    /// (<see cref="CompositeCallerAuthenticator"/>). Fail-closed par membre comme <see cref="Build"/> : chemin vide ou
    /// empreinte incalculable sauté, aucun membre → refus par défaut. Le résolveur du pair (<paramref name="imageIdentity"/>)
    /// est partagé ; l'identité attendue diffère par membre.</summary>
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

        // Aucun chemin résolu ni à empreinte calculée → refus par défaut (jamais d'ouverture par défaut).
        return members.Count == 0
            ? new DenyByDefaultCallerAuthenticator()
            : new CompositeCallerAuthenticator(members);
    }

    // Chemin + SHA-256 pour un chemin de confiance, ou null (chemin vide, empreinte incalculable) ; jamais par chemin seul.
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
            // Empreinte qui lève (verrou, E/S, accès) : identité improuvable, chemin sauté, jamais le chemin seul.
            return null;
        }

        if (expectedHash is null || expectedHash.Length == 0)
            return null;

        return DrDxtAppCallerAuthenticator.WithImageIdentity(appPath, expectedHash, imageIdentity);
    }
}
