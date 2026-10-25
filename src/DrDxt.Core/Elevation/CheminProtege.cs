using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// CheminProtege : un exécutable que le helper fait tourner en SYSTEM doit être hors d'atteinte du compte.
//
// PresentMonService tournait en
// LocalSystem depuis `C:\Users\<compte>\DrDXT\tools\...\PresentMonService.exe`, où `icacls` donnait au
// compte le contrôle total. Tout processus du compte, sans droits admin, pouvait remplacer le binaire ;
// au démarrage suivant du service (le helper le lance lui-même pour mesurer), ce code tournait en SYSTEM.
//
// Règle : le fichier et son dossier n'accordent de droit d'écriture qu'à SYSTEM, Administrateurs ou
// TrustedInstaller, et leur propriétaire est l'un d'eux (un propriétaire garde le droit implicite de
// réécrire la DACL). Pour le dossier, créer un fichier compte comme écrire : une DLL posée à côté serait
// chargée par le service. Les règles « héritage seulement » (CREATOR OWNER de Program Files) ne visent
// que les enfants à venir et ne comptent pas. L'installation par défaut (Program Files) passe.
//
// Chaque dossier au-dessus compte aussi : là où le compte peut supprimer un enfant (son profil), il renomme le dossier
// protégé et en pose un autre au même chemin. Pour un ancêtre, seuls comptent supprimer, renommer et réécrire la DACL ;
// créer un dossier neuf, que C:\ accorde aux utilisateurs, ne remplace rien.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class CheminProtege
{
    // Une règle peut porter un masque générique non mappé (GENERIC_ALL, GENERIC_WRITE) : Windows le traduit en droits de
    // fichier à l'accès (Microsoft Learn, « File Security and Access Rights » : GENERIC_WRITE donne FILE_GENERIC_WRITE,
    // GENERIC_ALL le contrôle total). Il compte comme les droits qu'il donne, sinon une règle effective le cacherait.
    private const FileSystemRights GeneriqueTout = (FileSystemRights)0x10000000;
    private const FileSystemRights GeneriqueEcriture = (FileSystemRights)0x40000000;

    private const FileSystemRights Dangereux =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | FileSystemRights.WriteExtendedAttributes
        | GeneriqueTout | GeneriqueEcriture;

    private const FileSystemRights DangereuxPourUnAncetre =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | GeneriqueTout;

    private static readonly SecurityIdentifier[] Confiance =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    /// <summary>Vrai quand la raison est « ACL illisible » (pas une preuve de faille), dans n'importe quelle langue
    /// du docteur : comparer le texte français laissait passer la raison anglaise.</summary>
    public static bool EstIllisible(string raison)
    {
        foreach (var langue in new[] { Docteur.Langue, System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), System.Globalization.CultureInfo.InvariantCulture })
        {
            using var _ = Docteur.Portee(langue);
            var modele = Docteur.T("CP.Illisible");
            var debut = modele.IndexOf('{') is var i and > 0 ? modele[..i] : "";
            if (debut.Length > 0 && raison.StartsWith(debut, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary><c>null</c> quand le chemin est protégé ; sinon la raison, en clair.</summary>
    public static string? Verifier(string executable) => Verifier(executable, null);

    /// <inheritdoc cref="Verifier(string)"/>
    /// <param name="borne">Le dossier où s'arrête la remontée, exclu : un test dans un dossier temporaire du profil ne juge
    /// que ce qu'il a posé. <c>null</c> en production : chaque dossier jusqu'à la racine compte.</param>
    public static string? Verifier(string executable, string? borne)
    {
        try
        {
            var fichier = new FileInfo(Path.GetFullPath(executable));
            if (!fichier.Exists) return Docteur.T("CP.Absent");
            if (Probleme(fichier.GetAccessControl(), fichier.FullName) is { } f) return f;
            var dossier = fichier.Directory!;
            if (Probleme(dossier.GetAccessControl(), dossier.FullName) is { } d) return d;
            for (var a = dossier.Parent; a is not null && !MemeDossier(a.FullName, borne); a = a.Parent)
                if (ProblemeAncetre(a.GetAccessControl(), a.FullName) is { } p) return p;
            return null;
        }
        catch (Exception ex)
        {
            return Docteur.T("CP.Illisible", ex.GetType().Name);
        }
    }

    private static bool MemeDossier(string a, string? b)
        => b is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Le binaire d'un `PathName` ou d'un `ImagePath` de service : entre guillemets, ou jusqu'au premier « .exe »
    /// (les arguments suivent), variables d'environnement développées. <c>null</c> si rien ne ressemble à un exécutable
    /// (pilote, chemin noyau).</summary>
    public static string? BinaireDuService(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName)) return null;
        var p = Environment.ExpandEnvironmentVariables(pathName.Trim());
        if (p.StartsWith('"'))
        {
            var fin = p.IndexOf('"', 1);
            return fin > 1 ? p[1..fin] : null;
        }
        var exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? p[..(exe + 4)] : null;
    }

    /// <summary>La règle d'un dossier au-dessus de celui de l'exécutable : <c>null</c> quand le compte ne peut ni en retirer
    /// un enfant, ni le renommer, ni en réécrire la DACL.</summary>
    public static string? ProblemeAncetre(FileSystemSecurity acl, string chemin) => Probleme(acl, chemin, DangereuxPourUnAncetre);

    private static string? Probleme(FileSystemSecurity acl, string chemin, FileSystemRights dangereux = Dangereux)
    {
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier proprio || !Confiance.Contains(proprio))
            return Docteur.T("CP.Proprio", chemin);

        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((r.FileSystemRights & dangereux) == 0) continue;
            if (r.IdentityReference is SecurityIdentifier sid && Confiance.Contains(sid)) continue;
            return Docteur.T("CP.Modifiable", chemin, Nom(r.IdentityReference));
        }
        return null;
    }

    private static string Nom(IdentityReference id)
    {
        try { return id.Translate(typeof(NTAccount)).Value; }
        catch { return id.Value; }
    }
}
