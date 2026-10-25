using System.Diagnostics;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Les couches logicielles qui s'intercalent entre une manette, une souris ou un clavier et le jeu : un appareil créé par
/// un logiciel (manette virtuelle sur un bus virtuel), ou un pilote filtre tiers posé sur l'appareil, sur un de ses
/// ancêtres USB ou sur toute sa classe. Avec une telle couche, une mesure de cadence ou de latence d'entrée mesure la
/// couche, pas l'appareil : DrDXT le dit. Lecture seule.
/// </summary>
/// <remarks>
/// Un appareil est virtuel quand, en remontant ses ancêtres (CM_Get_Parent), on atteint un nœud énuméré à la racine
/// (« ROOT\ ») avant tout contrôleur matériel (« PCI\ » ou « ACPI\ ») : c'est l'arbre qui le dit, jamais le fabricant
/// déclaré (un identifiant de vendeur se recopie). Relevé sur la machine de référence le 16/10/2026 : seize appareils
/// d'entrée, tous rattachés au contrôleur USB PCI 8086:7A60, aucun filtre de périphérique ; seuls kbdclass et mouclass
/// sur les classes clavier et souris, livrés par Microsoft. Filtres : DEVPKEY_Device_UpperFilters et LowerFilters
/// (devpkey.h), valeurs UpperFilters et LowerFilters des clés de classe.
/// </remarks>
public static class CouchesDEntree
{
    public sealed record Filtre(string Service, string? Editeur, string? Version, string Ou);

    public sealed record Appareil(string Nom, bool Virtuel, IReadOnlyList<Filtre> Filtres);

    public static readonly string[] Classes = ["HIDClass", "Mouse", "Keyboard", "XnaComposite"];

    /// <summary>Pur : l'appareil (en tête de chaîne) est-il créé par un logiciel ?</summary>
    public static bool Virtuel(IReadOnlyList<string> chaine)
    {
        foreach (var id in chaine.Skip(1))
        {
            if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
                return false;
            if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Un appareil du bus logiciel (SWD\, SwDeviceCreate) a pour parent la racine ou « un vrai appareil dont il étend la
        // fonction » (Microsoft Learn) : virtuel seulement si aucun parent physique (USB, Bluetooth) n'est dans son arbre.
        string[] physiques = ["USB\\", "BTH\\", "BTHENUM\\", "BTHLEDEVICE\\"];
        return chaine.Any(c => c.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))
               && !chaine.Any(c => physiques.Any(p => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    // Les pilotes de bus de Windows : concentrateurs USB et pile Bluetooth (services de la boîte Windows).
    private static readonly HashSet<string> ServicesDeBus = new(StringComparer.OrdinalIgnoreCase) { "USBHUB3", "usbhub", "BTHUSB", "BthEnum", "BTHPORT" };

    /// <summary>Pur : les nœuds qui sont l'appareil lui-même, de lui jusqu'au premier nœud de bus exclu (concentrateur
    /// USB, radio ou pile Bluetooth, contrôleur). Un filtre sur la radio Bluetooth ou un concentrateur est le pilote du bus,
    /// pas une couche entre l'appareil et le jeu (relevé le 16/10/2026 : le filtre du fabricant de la radio Bluetooth de la
    /// machine de référence, sur le nœud de la radio, n'est pas une couche de la manette qui passe par elle).</summary>
    public static IEnumerable<string> NoeudsDeLAppareil(IReadOnlyList<string> chaine, Func<string, string?> service)
        => chaine.TakeWhile(c => !(c.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
                                   || c.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
                                   || c.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
                                   || (service(c) is { } s && ServicesDeBus.Contains(s))));

    /// <summary>Pur : un filtre est tiers quand son fichier ne porte pas Microsoft comme éditeur, ou ne se lit pas.</summary>
    public static bool Tiers(Filtre f) => f.Editeur?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>Pur : vrai si une mesure d'entrée passe par une couche logicielle.</summary>
    public static bool CoucheActive(IReadOnlyList<Appareil> appareils, IReadOnlyList<Filtre> filtresDeClasse)
        => appareils.Any(a => a.Virtuel || a.Filtres.Any(Tiers)) || filtresDeClasse.Any(Tiers);

    public static RepairFinding Juger(IReadOnlyList<Appareil>? appareils, IReadOnlyList<Filtre> filtresDeClasse)
    {
        if (appareils is null)
            return new RepairFinding("PERIPHERIQUES", "couches logicielles d'entree", null, null, RepairStatus.Unknown, Docteur.T("CE.NonLu"));
        var morceaux = new List<string>();
        foreach (var a in appareils.Where(a => a.Virtuel))
            morceaux.Add(Docteur.T("CE.Virtuel", a.Nom));
        foreach (var (a, f) in appareils.SelectMany(a => a.Filtres.Where(Tiers).Select(f => (a, f))))
            morceaux.Add(Docteur.T("CE.FiltreAppareil", f.Service, f.Version ?? "?", f.Editeur ?? Docteur.T("CE.EditeurNonLu"), a.Nom, f.Ou));
        foreach (var f in filtresDeClasse.Where(Tiers))
            morceaux.Add(Docteur.T("CE.FiltreClasse", f.Service, f.Version ?? "?", f.Editeur ?? Docteur.T("CE.EditeurNonLu"), f.Ou));
        if (morceaux.Count == 0)
            return new RepairFinding("PERIPHERIQUES", "couches logicielles d'entree", Docteur.T("CE.Aucune"), null, RepairStatus.ExpectedDefault,
                Docteur.T("CE.Direct", appareils.Count));
        return new RepairFinding("PERIPHERIQUES", "couches logicielles d'entree", $"{morceaux.Count}", null, RepairStatus.Observed,
            string.Join(" ", morceaux) + Docteur.T("CE.Mesure"));
    }

    // ── Lecture ─────────────────────────────────────────────────────────────────────────

    public static (IReadOnlyList<Appareil>? Appareils, IReadOnlyList<Filtre> FiltresDeClasse) Lire() => LectureWmiBornee.Lire<(IReadOnlyList<Appareil>? Appareils, IReadOnlyList<Filtre> FiltresDeClasse)>(() =>
    {
        var filtresDeClasse = FiltresDesClasses();
        try
        {
            var appareils = new List<Appareil>();
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE " + string.Join(" OR ", Classes.Select(c => $"PNPClass = '{c}'")));
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                {
                    if (o["PNPDeviceID"] is not string id) continue;
                    var chaine = ArbreDesPeripheriques.Chaine(id);
                    // Les filtres de l'appareil et de ses nœuds propres (un filtre posé sur l'appareil USB composite agit sur
                    // chaque fonction qu'il porte), jamais ceux du bus qui le relie.
                    var filtres = NoeudsDeLAppareil(chaine, n => ArbreDesPeripheriques.Texte(n, ArbreDesPeripheriques.Service))
                        .SelectMany(c => ArbreDesPeripheriques.Liste(c, ArbreDesPeripheriques.FiltresHauts)
                            .Concat(ArbreDesPeripheriques.Liste(c, ArbreDesPeripheriques.FiltresBas)).Select(nom => Decrire(nom, c)))
                        .ToList();
                    appareils.Add(new Appareil(o["Name"] as string ?? id, Virtuel(chaine), filtres));
                }
            return (appareils, filtresDeClasse);
        }
        catch (Exception) { return (null, filtresDeClasse); }
    }, () => (null, FiltresDesClasses()));

    // Les GUID des classes d'entrée et USB (devguid.h).
    private static readonly (string Guid, string Nom)[] ClesDeClasse =
    [
        ("{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "HIDClass"), ("{4d36e96f-e325-11ce-bfc1-08002be10318}", "Mouse"),
        ("{4d36e96b-e325-11ce-bfc1-08002be10318}", "Keyboard"), ("{36fc9e60-c465-11cf-8056-444553540000}", "USB"),
    ];

    private static IReadOnlyList<Filtre> FiltresDesClasses()
    {
        var filtres = new List<Filtre>();
        foreach (var (guid, nom) in ClesDeClasse)
        {
            try
            {
                using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + guid);
                foreach (var valeur in new[] { "UpperFilters", "LowerFilters" })
                    if (cle?.GetValue(valeur) is string[] noms)
                        filtres.AddRange(noms.Where(n => n.Length > 0).Select(n => Decrire(n, nom)));
            }
            catch (Exception) { /* classe illisible : ses filtres ne sont pas listés, les autres le sont */ }
        }
        return filtres;
    }

    private static Filtre Decrire(string service, string ou)
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
            if (cle is null) return new Filtre(service, null, null, ou);
            var fichier = FichierDuService(service, cle.GetValue("ImagePath") as string);
            if (!File.Exists(fichier)) return new Filtre(service, null, null, ou);
            var info = FileVersionInfo.GetVersionInfo(fichier);
            return new Filtre(service, info.CompanyName, info.FileVersion, ou);
        }
        catch (Exception) { return new Filtre(service, null, null, ou); }
    }

    /// <summary>Pur : le fichier d'un service de pilote. Sans ImagePath, le fichier par défaut du service : un service
    /// filtre sans ImagePath n'est pas un filtre tiers d'office.</summary>
    internal static string FichierDuService(string service, string? imagePath)
        => CheminNoyau(Environment.ExpandEnvironmentVariables(imagePath ?? ""), service);

    /// <summary>Un ImagePath de pilote (« \SystemRoot\System32\drivers\x.sys », « System32\drivers\x.sys », « \??\C:\… »)
    /// ramené à un chemin de fichier ; sans ImagePath lisible, le fichier par défaut du service.</summary>
    internal static string CheminNoyau(string image, string service)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return windows + image[@"\SystemRoot".Length..];
        if (image.StartsWith(@"\??\", StringComparison.Ordinal)) return image[4..];
        if (image.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, image);
        return image.Length > 0 ? image : Path.Combine(windows, "System32", "drivers", service + ".sys");
    }
}
