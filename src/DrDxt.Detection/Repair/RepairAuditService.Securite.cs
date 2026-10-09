using DrDxt.Core;

namespace DrDxt.Detection.Repair;

public sealed partial class RepairAuditService
{
    // 25/09/2026 : un service qui tourne en SYSTEM depuis un dossier que le compte peut modifier est une
    // élévation de privilèges classique : n'importe quel programme lancé sans droits remplace le binaire,
    // et il s'exécute en SYSTEM au démarrage suivant du service. Vue sur la machine de reference (PresentMonService, laissé
    // dans un dossier de son profil). Même règle que celle du helper (`CheminProtege`) : le fichier et son
    // dossier. Constat seulement (Suspicious) : DrDXT ne touche pas au service d'un autre logiciel.
    private Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> ServicesSysteme() => _wmi.Query("SELECT Name, DisplayName, PathName, StartName, StartMode FROM Win32_Service");

    internal static IEnumerable<RepairFinding> ServicesRemplacables(Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> services)
    {
        if (!services.IsOk)
        {
            yield return new RepairFinding("SERVICES", "services système remplaçables", null, null, RepairStatus.Unknown,
                $"la liste des services n'a pas pu être lue ({services.Error?.Message}).");
            yield break;
        }

        foreach (var s in services.Value!)
        {
            if (!string.Equals(s.GetString("StartName"), "LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = CheminDuBinaire(s.GetString("PathName"));
            // Un binaire absent n'est pas une faille ; une ACL illisible n'est pas une preuve : on se tait.
            if (exe is null || !File.Exists(exe)) continue;
            var raison = CheminProtege.Verifier(exe);
            if (raison is null || raison.StartsWith("ACL illisible", StringComparison.Ordinal)) continue;

            var nom = s.GetString("DisplayName") ?? s.GetString("Name") ?? exe;
            // 25/09/2026 : un service désactivé ne démarre pas ; le chemin reste, réactivé il s'ouvrirait.
            var debut = string.Equals(s.GetString("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)
                ? "ce service est désactivé, il ne démarre pas aujourd'hui ; réactivé, il tournerait avec les droits du système (SYSTEM)"
                : "ce service tourne avec les droits du système (SYSTEM)";
            // 29/09/2026 : « remplaçable » faisait peur sans rien dire ; le titre dit ce qui peut arriver.
            yield return new RepairFinding("SERVICES", $"service {nom} : son fichier peut être remplacé par un autre programme", exe, null,
                RepairStatus.Suspicious,
                $"{debut}, mais {SansLeChemin(raison, exe)}. Un programme lancé sans "
                + "droits administrateur pourrait remplacer ce fichier, et il s'exécuterait en SYSTEM au prochain "
                + "démarrage du service. "
                + Remede(exe, "Réinstaller le logiciel dans Program Files, ou retirer ce droit d'écriture, ferme ce chemin.")
                + " DrDXT ne touche pas à ce service.");
        }
    }

    // 25/09/2026, vu dans le rapport réel : Epic Games Updater vit déjà sous C:\Program Files, et le conseil lui
    // disait d'y réinstaller le logiciel. Là, c'est le droit d'écriture posé sur le fichier qui ouvre le chemin.
    internal static string Remede(string exe, string horsProgramFiles)
    {
        var sousProgramFiles = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Any(d => d.Length > 0 && exe.StartsWith(Path.TrimEndingDirectorySeparator(d) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        // 27/09/2026 : « le retirer » ne disait pas comment. Sous Program Files, rendu aux Administrateurs, le fichier
        // reprend les droits de son dossier (Utilisateurs en lecture) : la commande de l'installeur de DrDXT, vérifiée
        // sur ses propres fichiers. Hors Program Files, le dossier lui-même peut donner l'écriture : pas de commande.
        // DOC-QUALITE (09/10/2026) : sous ProgramData, le dossier est celui que l'installeur de l'éditeur a choisi.
        var pd = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (pd.Length > 0 && exe.StartsWith(pd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Ce dossier est choisi par l'éditeur du logiciel, pas par toi : le lui signaler est ce qui ferme ce chemin "
                   + "pour de bon. Retirer toi-même ce droit d'écriture peut empêcher ses mises à jour.";
        return sousProgramFiles
            ? "Le fichier est déjà sous Program Files : c'est ce droit d'écriture qui ouvre le chemin. Pour le retirer, "
              + $"dans une console administrateur : icacls \"{exe}\" /setowner *S-1-5-32-544 puis icacls \"{exe}\" /reset "
              + "(le fichier revient aux Administrateurs et reprend les droits de son dossier). Le signaler à l'éditeur "
              + "du logiciel évite qu'une mise à jour le rouvre."
            : horsProgramFiles;
    }

    // 25/09/2026 : le chemin non cité. « C:\Program Files\App\svc.exe » sans guillemets fait essayer à
    // Windows C:\Program.exe, puis C:\Program Files\App.exe… avant le vrai binaire. Ce n'est une faille que
    // si l'un de ces emplacements accepte un fichier : sur un Windows sain, C:\ n'accorde aux utilisateurs
    // que la création de dossiers (mesuré sur la machine de reference, deux services non cités, aucun exploitable). On se tait
    // donc tant que rien n'est posable, pour ne pas crier sur un chemin inoffensif.
    internal static IEnumerable<RepairFinding> CheminsNonCites(Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> services)
    {
        if (!services.IsOk) yield break; // déjà dit par ServicesRemplacables, même requête
        foreach (var s in services.Value!)
        {
            if (!string.Equals(s.GetString("StartName"), "LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            var posable = CandidatsNonCites(s.GetString("PathName"))
                .FirstOrDefault(c => Path.GetDirectoryName(c) is { } d && DossierAccepteUnFichier(d));
            if (posable is null) continue;
            var nom = s.GetString("DisplayName") ?? s.GetString("Name") ?? posable;
            yield return new RepairFinding("SERVICES", $"chemin de service non cité : {nom}", posable, null,
                RepairStatus.Suspicious,
                $"le chemin de ce service n'est pas entre guillemets et contient des espaces : Windows essaie d'abord "
                + $"« {posable} », et ton compte peut poser un fichier à cet endroit. Un programme sans droits "
                + "administrateur pourrait s'y installer et s'exécuter en SYSTEM au démarrage du service"
                + (string.Equals(s.GetString("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)
                    ? " (ce service est désactivé : il ne démarre pas aujourd'hui, le chemin s'ouvrirait s'il était réactivé)"
                    : "")
                + ". Mettre le "
                + "chemin du service entre guillemets ferme ce chemin. DrDXT ne touche pas à ce service.");
        }
    }

    /// <summary>Les emplacements que Windows essaie avant le vrai binaire d'un chemin non cité : chaque
    /// préfixe qui s'arrête sur une espace, suivi de « .exe ». Vide si le chemin est cité ou sans espace.</summary>
    internal static IReadOnlyList<string> CandidatsNonCites(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName) || pathName.TrimStart().StartsWith('"')) return [];
        var p = Environment.ExpandEnvironmentVariables(pathName.Trim());
        var fin = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (fin <= 0) return [];
        var binaire = p[..fin];
        var candidats = new List<string>();
        for (var i = binaire.IndexOf(' '); i > 0; i = binaire.IndexOf(' ', i + 1))
            candidats.Add(binaire[..i] + ".exe");
        return candidats;
    }

    /// <summary>Un compte hors SYSTEM, Administrateurs et TrustedInstaller peut-il créer un fichier dans ce
    /// dossier (droit WriteData, ou propriété du dossier) ? Créer un sous-dossier (AppendData) ne compte pas.
    /// Illisible : non, faute de preuve.</summary>
    internal static bool DossierAccepteUnFichier(string dossier)
    {
        try
        {
            var acl = new DirectoryInfo(dossier).GetAccessControl();
            var confiance = new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                new System.Security.Principal.SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
            };
            if (acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is System.Security.Principal.SecurityIdentifier proprio
                && !confiance.Contains(proprio))
                return true;
            foreach (System.Security.AccessControl.FileSystemAccessRule r in
                     acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                if (r.PropagationFlags.HasFlag(System.Security.AccessControl.PropagationFlags.InheritOnly)) continue;
                if ((r.FileSystemRights & System.Security.AccessControl.FileSystemRights.WriteData) == 0) continue;
                if (r.IdentityReference is System.Security.Principal.SecurityIdentifier sid && confiance.Contains(sid)) continue;
                return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    /// <summary>La raison de `CheminProtege` commence par le chemin jugé (le fichier ou son dossier), que
    /// le constat affiche déjà comme valeur : le dire deux fois allongeait l'explication (vu sur la photo
    /// du 25/09). Le fichier devient « ce fichier », son dossier « son dossier » ; autre chose reste tel quel.</summary>
    internal static string SansLeChemin(string raison, string exe)
    {
        if (raison.StartsWith(exe + " ", StringComparison.OrdinalIgnoreCase)) return "ce fichier" + raison[exe.Length..];
        var dossier = Path.GetDirectoryName(exe);
        if (dossier is not null && raison.StartsWith(dossier + " ", StringComparison.OrdinalIgnoreCase))
            return $"son dossier ({dossier})" + raison[dossier.Length..];
        return raison;
    }

    /// <summary>Le binaire d'un `PathName` de service : entre guillemets, ou jusqu'au premier « .exe »
    /// (les arguments suivent), variables d'environnement développées. <c>null</c> si rien ne ressemble à
    /// un exécutable (pilote, chemin noyau).</summary>
    internal static string? CheminDuBinaire(string? pathName)
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
}
