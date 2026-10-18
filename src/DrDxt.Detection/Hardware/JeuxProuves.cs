using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Un dossier n'est le dossier d'un jeu que si l'exécutable du jeu y est. Parmi les dossiers que les lanceurs déclarent
/// (<see cref="DossiersDeJeux"/>), ceux qui contiennent un exécutable de la liste fermée des jeux de DrDXT
/// (<see cref="NomExeDeJeu"/>) sont prouvés, avec la version installée ; les autres sont « non prouvés ». Lecture seule.
/// </summary>
/// <remarks>
/// La version vient du fichier « .build.info » que Battle.net pose à la racine du jeu quand il existe : un en-tête
/// « Nom!TYPE:taille » séparé par « | », puis une ligne par branche, dont la colonne « Version » et la colonne « Active ».
/// Relevé le 16/10/2026 sur la machine de référence : Call of Duty sous Program Files (x86), exécutable _retail_\cod.exe,
/// Version 3.14.2.28787707 pour la branche active. Sans ce fichier, la version de fichier de l'exécutable.
/// </remarks>
public static class JeuxProuves
{
    public sealed record Jeu(string Lanceur, string Dossier, string? Executable, string? Version);

    /// <summary>Où chercher l'exécutable sous le dossier du jeu : la racine et deux niveaux (Call of Duty le range sous
    /// _retail_). **Choix** : assez pour les jeux de la liste, sans parcourir toute une bibliothèque.</summary>
    public const int Profondeur = 2;

    /// <summary>Pur : la version de la branche active d'un .build.info ; null s'il n'a ni colonne Version ni ligne active.</summary>
    public static string? VersionBattleNet(string? contenu)
    {
        if (string.IsNullOrWhiteSpace(contenu)) return null;
        var lignes = contenu.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (lignes.Count < 2) return null;
        var colonnes = lignes[0].Split('|').Select(c => c.Split('!')[0]).ToList();
        var (version, active) = (colonnes.IndexOf("Version"), colonnes.IndexOf("Active"));
        if (version < 0) return null;
        foreach (var l in lignes.Skip(1))
        {
            var champs = l.Split('|');
            if (champs.Length <= version) continue;
            if (active >= 0 && (champs.Length <= active || champs[active] != "1")) continue;
            if (champs[version].Length > 0) return champs[version];
        }
        return null;
    }

    /// <summary>Pur : l'exécutable connu trouvé dans une liste de fichiers (chemins relatifs au dossier), par son nom exact.</summary>
    public static string? ExecutableConnu(IEnumerable<string> fichiers)
        => fichiers.FirstOrDefault(f => NomExeDeJeu.EstUnJeuConnu(Path.GetFileName(f)));

    public static IReadOnlyList<Jeu>? Lire()
    {
        if (DossiersDeJeux.Lire() is not { } dossiers) return null;
        var jeux = new List<Jeu>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = Profondeur, IgnoreInaccessible = true };
        foreach (var d in dossiers)
        {
            // Une bibliothèque Steam porte plusieurs jeux : chacun est un dossier de steamapps\common.
            var candidats = d.Lanceur == "Steam"
                ? (Directory.Exists(Path.Combine(d.Chemin, "steamapps", "common")) ? Directory.EnumerateDirectories(Path.Combine(d.Chemin, "steamapps", "common")) : [])
                : [d.Chemin];
            foreach (var c in candidats)
            {
                try
                {
                    if (!Directory.Exists(c)) continue;
                    var exe = ExecutableConnu(Directory.EnumerateFiles(c, "*.exe", options).Select(f => Path.GetRelativePath(c, f)));
                    // Dans une bibliothèque, un jeu hors de la liste n'est pas dit : seuls les dossiers des lanceurs le sont.
                    if (exe is null && d.Lanceur == "Steam") continue;
                    var info = Path.Combine(c, ".build.info");
                    var version = exe is null ? null
                        : (File.Exists(info) ? VersionBattleNet(File.ReadAllText(info)) : null)
                          ?? FileVersionInfo.GetVersionInfo(Path.Combine(c, exe)).FileVersion;
                    jeux.Add(new Jeu(d.Lanceur, c, exe, version));
                }
                catch (Exception) { jeux.Add(new Jeu(d.Lanceur, c, null, null)); }
            }
        }
        return jeux;
    }

    public static RepairFinding Juger(IReadOnlyList<Jeu>? jeux)
    {
        if (jeux is null)
            return new RepairFinding("STOCKAGE", "jeux installes", null, null, RepairStatus.Unknown, Docteur.T("JX.NonLu"));
        var prouves = jeux.Where(j => j.Executable is not null).ToList();
        var autres = jeux.Where(j => j.Executable is null).ToList();
        var texte = prouves.Count == 0 ? Docteur.T("JX.Aucun")
            : string.Join(" ", prouves.Select(j => Docteur.T("JX.Prouve", Path.GetFileName(j.Dossier), j.Lanceur, j.Executable, j.Version ?? Docteur.T("JX.VersionNonLue"))));
        if (autres.Count > 0)
            texte += Docteur.T("JX.NonProuves", string.Join(", ", autres.Select(j => Path.GetFileName(j.Dossier))));
        return new RepairFinding("STOCKAGE", "jeux installes", $"{prouves.Count}", null, RepairStatus.Observed, texte + Docteur.T("JX.Regle"));
    }
}
