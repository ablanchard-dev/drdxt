using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// CheminProtege — un exécutable que le helper fait tourner en SYSTEM doit être hors d'atteinte du compte.
//
// 25/09/2026 (revue de sécurité, vérifié sur la machine de dev) : PresentMonService tournait en
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

    /// <summary><c>null</c> quand le chemin est protégé ; sinon la raison, en clair.</summary>
    public static string? Verifier(string executable)
    {
        try
        {
            var fichier = new FileInfo(Path.GetFullPath(executable));
            if (!fichier.Exists) return "l'exécutable n'existe pas";
            if (Probleme(fichier.GetAccessControl(), fichier.FullName) is { } f) return f;
            var dossier = fichier.Directory!;
            return Probleme(dossier.GetAccessControl(), dossier.FullName);
        }
        catch (Exception ex)
        {
            return $"ACL illisible ({ex.GetType().Name}) : pas de preuve, pas de démarrage";
        }
    }

    private static string? Probleme(FileSystemSecurity acl, string chemin)
    {
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier proprio || !Confiance.Contains(proprio))
            return $"{chemin} appartient à un compte qui peut en réécrire les droits";

        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((r.FileSystemRights & Dangereux) == 0) continue;
            if (r.IdentityReference is SecurityIdentifier sid && Confiance.Contains(sid)) continue;
            return $"{chemin} est modifiable par {Nom(r.IdentityReference)}";
        }
        return null;
    }

    private static string Nom(IdentityReference id)
    {
        try { return id.Translate(typeof(NTAccount)).Value; }
        catch { return id.Value; }
    }
}
