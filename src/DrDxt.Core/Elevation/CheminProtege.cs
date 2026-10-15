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
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class CheminProtege
{
    private const FileSystemRights Dangereux =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | FileSystemRights.WriteExtendedAttributes;

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
    public static string? Verifier(string executable)
    {
        try
        {
            var fichier = new FileInfo(Path.GetFullPath(executable));
            if (!fichier.Exists) return Docteur.T("CP.Absent");
            if (Probleme(fichier.GetAccessControl(), fichier.FullName) is { } f) return f;
            var dossier = fichier.Directory!;
            return Probleme(dossier.GetAccessControl(), dossier.FullName);
        }
        catch (Exception ex)
        {
            return Docteur.T("CP.Illisible", ex.GetType().Name);
        }
    }

    private static string? Probleme(FileSystemSecurity acl, string chemin)
    {
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier proprio || !Confiance.Contains(proprio))
            return Docteur.T("CP.Proprio", chemin);

        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((r.FileSystemRights & Dangereux) == 0) continue;
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
