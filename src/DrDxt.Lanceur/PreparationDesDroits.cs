using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DrDxt.Lanceur;

/// <summary>
/// Prépare un dossier que SYSTEM croit (Program Files\DrDXT, ProgramData\DrDxt), en natif, dans le lanceur élevé : aucun
/// processus enfant, donc rien ne lit l'environnement du compte, et aucune stratégie de langage ne peut l'empêcher. Lit
/// propriétaire, règles et points d'analyse sans jamais suivre un lien ; un lien ou un dossier qui n'est pas fiable est
/// renommé de côté (en mode « donnees », un fichier non fiable reste en place), et le dossier neuf naît avec son descripteur, sans appartenir au compte un instant. Modes « app », « donnees », « verifier ».
/// Codes : 0 fiable/préparé, 1 non fiable, 2 erreur ; chaque objet écarté est écrit « ecarte &lt;chemin&gt; ».
/// </summary>
internal static class PreparationDesDroits
{
    private static readonly SecurityIdentifier[] Confiance =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    private const FileSystemRights Ecriture =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const string DroitsDuProprietaire = "S-1-3-4";

    private static FileSystemSecurity Securite(string chemin, FileAttributes attr)
        => attr.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(chemin).GetAccessControl() : new FileInfo(chemin).GetAccessControl();

    /// <summary>Un objet fiable : ni point d'analyse, DACL présente, propriétaire de confiance, aucune règle d'écriture pour
    /// un autre. Une DACL nulle (NO_ACCESS_CONTROL) autorise tout le monde : refusée.</summary>
    private static bool Objet(string chemin)
    {
        var attr = File.GetAttributes(chemin);
        if (attr.HasFlag(FileAttributes.ReparsePoint)) return false;
        var s = Securite(chemin, attr);
        if (s.GetSecurityDescriptorSddlForm(AccessControlSections.Access).Contains("NO_ACCESS_CONTROL", StringComparison.Ordinal)) return false;
        if (s.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier proprio || !Confiance.Contains(proprio)) return false;
        foreach (FileSystemAccessRule r in s.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & Ecriture) == 0) continue;
            if (r.IdentityReference is SecurityIdentifier sid && Confiance.Contains(sid)) continue;
            return false;
        }
        return true;
    }

    /// <summary>L'objet et, pour un dossier, tout ce qu'il contient : sans jamais suivre un lien.</summary>
    private static bool Sur(string chemin)
    {
        if (!Objet(chemin)) return false;
        if (!File.GetAttributes(chemin).HasFlag(FileAttributes.Directory)) return true;
        foreach (var e in Directory.GetFileSystemEntries(chemin))
            if (!Sur(e)) return false;
        return true;
    }

    private static bool Racine(string chemin)
        => Objet(chemin) && new DirectoryInfo(chemin).GetAccessControl().AreAccessRulesProtected;

    private static bool DroitsDuProprietairePresents(string chemin)
    {
        foreach (FileSystemAccessRule r in new DirectoryInfo(chemin).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (r.IdentityReference is SecurityIdentifier sid && sid.Value == DroitsDuProprietaire) return true;
        return false;
    }

    private static bool Existe(string chemin)
    {
        try { _ = File.GetAttributes(chemin); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void Ecarter(string chemin, string vers)
    {
        if (File.GetAttributes(chemin).HasFlag(FileAttributes.Directory)) Directory.Move(chemin, vers);
        else File.Move(chemin, vers);
    }

    public static int Preparer(string mode, string racine, string sddl, Action<string> ecrire)
    {
        try
        {
            if (mode == "verifier")
                return Racine(racine) && Sur(racine) && DroitsDuProprietairePresents(racine) ? 0 : 1;

            var horodatage = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            // La racine elle-même : fiable pour « donnees » dès qu'elle est une racine (ses enfants sont triés ensuite) ;
            // pour « app », fiable seulement si tout l'arbre l'est et porte DROITS DU PROPRIÉTAIRE. Sinon, de côté.
            if (Existe(racine) && !(Racine(racine) && (mode == "donnees" || (DroitsDuProprietairePresents(racine) && Sur(racine)))))
            {
                var vers = racine + ".ecarte-" + horodatage;
                Ecarter(racine, vers);
                ecrire("ecarte " + vers);
            }
            if (!Directory.Exists(racine))
            {
                var s = new DirectorySecurity();
                s.SetSecurityDescriptorSddlForm(sddl);
                new DirectoryInfo(racine).Create(s);
            }
            if (!Racine(racine)) return 1;
            if (mode == "donnees")
                foreach (var e in Directory.GetFileSystemEntries(racine))
                {
                    var nom = Path.GetFileName(e);
                    var attr = File.GetAttributes(e);
                    if (nom.StartsWith("ecarte-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!attr.HasFlag(FileAttributes.ReparsePoint) && !(attr.HasFlag(FileAttributes.Directory) && !Sur(e))) continue;
                    var vers = Path.Combine(racine, "ecarte-" + horodatage + "-" + nom);
                    Ecarter(e, vers);
                    ecrire("ecarte " + vers);
                }
            return 0;
        }
        catch (Exception ex)
        {
            ecrire(ex.Message);
            return 2;
        }
    }
}
