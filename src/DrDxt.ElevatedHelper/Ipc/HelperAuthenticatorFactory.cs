using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperAuthenticatorFactory : construit l'authentificateur d'appelant du helper élevé à partir
// du chemin d'image de l'App de confiance fourni (--app-path / DRDXT_HELPER_APP_PATH).
//
// L'identité d'image chemin + SHA-256 est le défaut du lancement à la demande. L'App passe le
// chemin de son exe au helper au lancement ; le helper calcule l'empreinte de ce chemin au
// démarrage et branche DrDxtAppCallerAuthenticator.WithImageIdentity (chemin et SHA-256,
// comparaison en temps constant). Cela ferme le trou de l'exe remplacé au même chemin, sans
// signature de code. Vaut pour le handshake (à la demande) et pour les outils de smoke test
// historiques par variable d'environnement (qui passent aussi --app-path) : ils ont la barrière
// la plus forte sans effort, puisque leur exe est sur le disque et que son empreinte se calcule.
//
// Jamais d'ouverture par défaut : si le chemin manque, ou si l'empreinte de son image est
// incalculable au démarrage (absente, illisible, verrouillée), aucune identité chemin + empreinte
// n'est possible : on se rabat sur le refus par défaut (tout appelant refusé), jamais sur le
// chemin seul, jamais sur tout autoriser.
//
// Limite connue tant que l'App n'est pas signée : remplacer l'empreinte d'image au démarrage par
// une vraie vérification Authenticode de l'image de l'appelant (WinVerifyTrust
// WINTRUST_ACTION_GENERIC_VERIFY_V2 + épinglage de notre certificat d'éditeur) une fois DrDxt.App
// signé. L'empreinte d'image tient lieu d'identité au build de dev non signé ; la forme de la
// fabrique ne changera pas, seule la façon de calculer et comparer l'identité attendue.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
internal static class HelperAuthenticatorFactory
{
    /// <summary>Construit l'authentificateur d'appelant pour le chemin d'App de confiance donné.
    /// Rend un authentificateur chemin + SHA-256 si l'empreinte de l'image se calcule au démarrage ;
    /// sinon un authentificateur en refus par défaut (fail-closed). <paramref name="hashFile"/>
    /// calcule l'empreinte attendue depuis le chemin (le vrai calcul sur fichier par défaut) ;
    /// <paramref name="imageIdentity"/> est le résolveur dont se sert l'authentificateur produit
    /// pour identifier le pair qui se connecte (le vrai résolveur Win32 chemin + empreinte par défaut).</summary>
    public static ICallerAuthenticator Build(
        string? appPath,
        Func<string, byte[]?>? hashFile = null,
        ICallerImageIdentity? imageIdentity = null)
    {
        var member = TryBuildMember(appPath, hashFile ?? Win32CallerImageIdentity.TryHashFile, imageIdentity);
        // Aucun chemin à empreinte calculable → refus par défaut (jamais le chemin seul, jamais tout autoriser).
        return member ?? new DenyByDefaultCallerAuthenticator();
    }

    /// <summary>Les chemins demandes (argument ou variable d'environnement) qui peuvent servir de
    /// confiance. Ils arrivent de quiconque lance le helper : installe sous Program Files, un programme du compte
    /// pourrait le lancer avec --app-path=&lt;lui-meme&gt;, l'UAC affichant le nom de DrDXT. Installe, seuls les
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

    /// <summary>Construit un authentificateur multi-appelant à partir d'un ensemble de chemins
    /// d'image de confiance (par exemple l'App interactive et l'hôte réconciliateur). Chaque chemin
    /// dont l'empreinte se calcule au démarrage devient un membre chemin + SHA-256 ; le pair est
    /// autorisé si et seulement s'il correspond à l'un d'eux (<see cref="CompositeCallerAuthenticator"/>).
    /// Mêmes règles fail-closed que <see cref="Build"/>, appliquées par membre : un chemin null, vide ou
    /// à empreinte incalculable est sauté, et si aucun chemin ne donne de membre, le résultat est le
    /// refus par défaut (jamais le chemin seul, jamais tout autoriser). Tous les membres partagent le
    /// même résolveur du pair (<paramref name="imageIdentity"/>) ; il ne résout que le (chemin,
    /// empreinte) du pair : l'identité attendue diffère par membre, les appelants restent donc distingués.</summary>
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

    // Construit un authentificateur chemin + SHA-256 pour un chemin de confiance, ou null si le
    // chemin est vide ou si l'empreinte de son image est incalculable au démarrage (l'appelant saute
    // ou refuse). Ne rend jamais un authentificateur par chemin seul : sans empreinte, pas d'identité.
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
            // Le calcul d'empreinte de l'image fournie a levé (verrou, E/S, accès) → identité
            // improuvable → sauter ce chemin (l'appelant refuse par défaut si c'était le seul).
            // Jamais de repli sur le chemin seul.
            return null;
        }

        if (expectedHash is null || expectedHash.Length == 0)
            return null;

        return DrDxtAppCallerAuthenticator.WithImageIdentity(appPath, expectedHash, imageIdentity);
    }
}
