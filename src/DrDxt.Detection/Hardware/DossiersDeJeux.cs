using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Les dossiers de jeux et le type du disque qui les porte, sans droits. Steam : la clé
/// HKCU\Software\Valve\Steam (SteamPath) puis les « path » de steamapps\libraryfolders.vdf ; Epic : les InstallLocation des
/// manifestes de ProgramData\Epic\EpicGamesLauncher\Data\Manifests ; Battle.net, Riot, EA app et Ubisoft Connect :
/// l'InstallLocation de leurs jeux dans la liste des programmes de Windows. Le disque : MSFT_Partition (lettre ⇒ numéro de disque)
/// puis MSFT_PhysicalDisk.MediaType. Mesure du 07/10 sur la machine de référence : Steam et Epic sur C:, un Samsung 990 PRO
/// en NVMe (MediaType 4). <c>null</c> = rien n'a pu être lu.
/// </summary>
public static class DossiersDeJeux
{
    private static readonly Regex CheminVdf = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

    /// <summary>Les chemins des bibliothèques d'un libraryfolders.vdf (antislashs doublés dans le fichier).</summary>
    internal static IReadOnlyList<string> CheminsSteam(string vdf)
        => CheminVdf.Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Les jeux des autres lanceurs, par l'éditeur que leur programme d'installation inscrit dans
    /// la liste des programmes de Windows. Relevé sur une vraie machine : « Call of Duty » et « Overwatch », Publisher
    /// « Blizzard Entertainment », InstallLocation sous Program Files (x86). Le lanceur lui-même n'est pas un jeu.</summary>
    private static readonly (string Editeur, string Lanceur, string[] Outils)[] Editeurs =
    [
        ("Blizzard Entertainment", "Battle.net", ["Battle.net"]),
        // L'antitriche de Riot et les anciens noms des lanceurs (Origin, Uplay) ne sont pas des jeux.
        ("Riot Games", "Riot", ["Riot Client", "Riot Vanguard"]),
        ("Electronic Arts", "EA app", ["EA app", "Origin"]),
        ("Ubisoft", "Ubisoft Connect", ["Ubisoft Connect", "Uplay", "Ubisoft Game Launcher"]),
    ];

    /// <summary>Les dossiers de jeux parmi les programmes installés (nom, éditeur, dossier). Pur : testable sans registre.</summary>
    internal static IReadOnlyList<(string Lanceur, string Chemin)> JeuxDesLanceurs(
        IEnumerable<(string? Nom, string? Editeur, string? Dossier)> programmes)
        => programmes
            .Where(p => p.Dossier is { Length: > 3 } && p.Editeur is not null)
            .Select(p => (p.Nom, p.Dossier, Lanceur: Editeurs.FirstOrDefault(e => p.Editeur!.StartsWith(e.Editeur, StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Lanceur.Lanceur is not null
                && !p.Lanceur.Outils.Contains(p.Nom ?? "", StringComparer.OrdinalIgnoreCase)
                && !string.Equals(p.Nom, p.Lanceur.Lanceur, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.Lanceur.Lanceur, p.Dossier!.TrimEnd('\\')))
            .ToList();

    private static IEnumerable<(string? Nom, string? Editeur, string? Dossier)> ProgrammesInstalles()
    {
        foreach (var (racine, chemin) in new[]
                 {
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var liste = racine.OpenSubKey(chemin);
            if (liste is null) continue;
            foreach (var nom in liste.GetSubKeyNames())
            {
                using var p = liste.OpenSubKey(nom);
                if (p is null) continue;
                yield return (p.GetValue("DisplayName") as string, p.GetValue("Publisher") as string, p.GetValue("InstallLocation") as string);
            }
        }
    }

    public static IReadOnlyList<DossierDeJeux>? Lire()
    {
        try
        {
            var dossiers = new List<(string Lanceur, string Chemin)>();
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam
                && File.Exists(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))
                dossiers.AddRange(CheminsSteam(File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))).Select(c => ("Steam", c)));
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(epic))
                foreach (var f in Directory.EnumerateFiles(epic, "*.item"))
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (doc.RootElement.TryGetProperty("InstallLocation", out var l) && l.GetString() is { Length: > 0 } c) dossiers.Add(("Epic", c));
                    }
                    catch (Exception) { /* un manifeste abîmé n'empêche pas les autres */ }
            try { dossiers.AddRange(JeuxDesLanceurs(ProgrammesInstalles())); }
            catch (Exception) { /* la liste des programmes illisible n'empêche pas Steam et Epic */ }
            var types = TypesParLettre();
            return dossiers.DistinctBy(d => d.Chemin, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DossierDeJeux(d.Lanceur, d.Chemin,
                    d.Chemin.Length > 1 && d.Chemin[1] == ':' && types.TryGetValue(char.ToUpperInvariant(d.Chemin[0]), out var t) ? t : 0))
                .ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Le chemin complet d'un exécutable de jeu, cherché par son nom dans les dossiers des lanceurs (bibliothèques
    /// Steam par leur steamapps\common) ; null si introuvable. Profondeur bornée : un jeu Unreal range son exécutable
    /// sous Projet\Binaries\Win64, quatre niveaux sous le dossier du jeu.</summary>
    public static string? CheminDeLExecutable(string exe)
    {
        var options = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
        foreach (var d in Lire() ?? [])
        {
            var racine = d.Lanceur == "Steam" ? Path.Combine(d.Chemin, "steamapps", "common") : d.Chemin;
            try
            {
                if (Directory.Exists(racine) && Directory.EnumerateFiles(racine, exe, options).FirstOrDefault() is { } trouve)
                    return trouve;
            }
            catch (Exception) { /* un dossier illisible n'empêche pas les autres */ }
        }
        return null;
    }

    internal static Dictionary<char, int> TypesParLettre()
    {
        var types = new Dictionary<char, int>();
        try
        {
            var disques = new Dictionary<string, int>();
            using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
            using (var rangees = s.Get())
                foreach (var o in rangees.Cast<ManagementObject>())
                    using (o)
                        if (o["DeviceId"]?.ToString() is { } id) disques[id] = Convert.ToInt32(o["MediaType"] ?? 0);
            using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
            using (var rangees = s.Get())
                foreach (var o in rangees.Cast<ManagementObject>())
                    using (o)
                        if (o["DriveLetter"] is char lettre && lettre != '\0' && disques.TryGetValue(o["DiskNumber"]?.ToString() ?? "", out var t))
                            types[char.ToUpperInvariant(lettre)] = t;
        }
        catch (Exception) { /* types non lus : 0, rien n'est affirmé */ }
        return types;
    }
}
