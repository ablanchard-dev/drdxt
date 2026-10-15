using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>Sécurité : les services dont le fichier pourrait être remplacé par un autre programme, et les chemins non cités.</summary>
public sealed partial class RepairAuditService
{
    // Un service qui tourne en SYSTEM depuis un dossier que le compte peut modifier est une
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
                Docteur.T("SE.Services.NonLu", services.Error?.Message));
            yield break;
        }

        foreach (var s in services.Value!)
        {
            if (!string.Equals(s.GetString("StartName"), "LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = CheminDuBinaire(s.GetString("PathName"));
            // Un binaire absent n'est pas une faille ; une ACL illisible n'est pas une preuve : on se tait.
            if (exe is null || !File.Exists(exe)) continue;
            var raison = CheminProtege.Verifier(exe);
            if (raison is null || CheminProtege.EstIllisible(raison)) continue;

            var nom = s.GetString("DisplayName") ?? s.GetString("Name") ?? exe;
            // Un service désactivé ne démarre pas ; le chemin reste, réactivé il s'ouvrirait.
            var debut = string.Equals(s.GetString("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)
                ? Docteur.T("SE.Desactive")
                : Docteur.T("SE.Tourne");
            // « remplaçable » faisait peur sans rien dire ; le titre dit ce qui peut arriver.
            yield return new RepairFinding("SERVICES", Docteur.Nom("SE.Remplacable.Nom", nom), exe, null,
                RepairStatus.Suspicious,
                Docteur.T("SE.Remplacable", debut, SansLeChemin(raison, exe)) + Docteur.T("SE.PasTouche"),
                Remede: Remede(exe, Docteur.T("SE.Reinstaller")));
        }
    }

    // Un service déjà sous C:\Program Files (Epic Games Updater, par exemple) ne gagne rien à y être réinstallé :
    // là, c'est le droit d'écriture posé sur le fichier qui ouvre le chemin.
    internal static string Remede(string exe, string horsProgramFiles)
    {
        var sousProgramFiles = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Any(d => d.Length > 0 && exe.StartsWith(Path.TrimEndingDirectorySeparator(d) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        // « le retirer » ne dit pas comment. Sous Program Files, rendu aux Administrateurs, le fichier
        // reprend les droits de son dossier (Utilisateurs en lecture) : la commande de l'installeur de DrDXT, vérifiée
        // sur ses propres fichiers. Hors Program Files, le dossier lui-même peut donner l'écriture : pas de commande.
        // Sous ProgramData, le dossier est celui que l'installeur de l'éditeur a choisi.
        var pd = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (pd.Length > 0 && exe.StartsWith(pd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Docteur.T("SE.Editeur");
        return sousProgramFiles
            ? Docteur.T("SE.ProgramFiles", exe)
            : horsProgramFiles;
    }

    // Le chemin non cité. « C:\Program Files\App\svc.exe » sans guillemets fait essayer à
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
            yield return new RepairFinding("SERVICES", Docteur.Nom("SE.NonCite.Nom", nom), posable, null,
                RepairStatus.Suspicious,
                Docteur.T("SE.NonCite", posable)
                + (string.Equals(s.GetString("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)
                    ? Docteur.T("SE.NonCite.Desactive")
                    : "")
                + Docteur.T("SE.NonCite.Fin"),
                // La clé lue sous son nom de service, pas son nom affiché : c'est là que Windows lit le chemin.
                Remede: Docteur.T("SE.NonCite.Remede", s.GetString("Name") ?? nom));
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
    /// le constat affiche déjà comme valeur : le dire deux fois allongerait l'explication. Le fichier devient « ce fichier », son dossier « son dossier » ; autre chose reste tel quel.</summary>
    internal static string SansLeChemin(string raison, string exe)
    {
        if (raison.StartsWith(exe + " ", StringComparison.OrdinalIgnoreCase)) return Docteur.T("CP.CeFichier") + raison[exe.Length..];
        var dossier = Path.GetDirectoryName(exe);
        if (dossier is not null && raison.StartsWith(dossier + " ", StringComparison.OrdinalIgnoreCase))
            return Docteur.T("CP.SonDossier", dossier) + raison[dossier.Length..];
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
