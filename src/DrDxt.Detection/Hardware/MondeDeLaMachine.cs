using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Les changements venus de l'extérieur, placés dans l'historique de la machine : un jeu mis à jour (sa version
/// installée change), un pilote d'affichage changé. Ils expliquent un « mes sensations ont changé ce soir » qui ne vient
/// pas d'un réglage, et l'intégrité de la mémoire (HVCI) rallumée ou coupée.
/// Chaque changement est noté à l'heure où DrDXT le voit, avec le scan précédent comme borne basse :
/// rien n'est reconstruit après coup à partir de noms de fichiers. Lecture seule de la machine ; l'état précédent est
/// gardé dans les données de DrDXT.
/// </summary>
public static class MondeDeLaMachine
{
    public const string Fichier = "monde-de-la-machine.json";

    /// <param name="Jeux">Le dossier de chaque jeu prouvé et sa version installée.</param>
    /// <param name="Pilotes">Chaque carte d'affichage, par son identifiant d'instance PnP (deux cartes du même modèle sont
    /// deux cartes), et la version de son pilote.</param>
    /// <param name="NomsDesCartes">Le nom de chaque carte, pour le libellé ; null dans un état gardé avant cette clé.</param>
    /// <param name="Hvci">L'intégrité de la mémoire tourne (Win32_DeviceGuard) ; null = non lue, ou état gardé avant cette clé.</param>
    public sealed record Etat(DateTimeOffset Quand, Dictionary<string, string> Jeux, Dictionary<string, string> Pilotes,
        Dictionary<string, string>? NomsDesCartes = null, bool? Hvci = null);

    public sealed record Ecart(string Cle, string Libelle);

    /// <summary>Pur : ce qui a changé entre deux états. Un jeu ou une carte qui apparaît ou disparaît n'est pas une mise à
    /// jour : l'inventaire de la machine le dit déjà ; seule une version qui change compte.</summary>
    public static IReadOnlyList<Ecart> Comparer(Etat avant, Etat apres)
    {
        var ecarts = new List<Ecart>();
        foreach (var (dossier, version) in apres.Jeux)
            if (avant.Jeux.TryGetValue(dossier, out var ancienne) && ancienne != version)
                ecarts.Add(new Ecart($"monde:jeu:{Path.GetFileName(dossier)}:{version}",
                    Docteur.T("MO.Jeu", Path.GetFileName(dossier), ancienne, version)));
        foreach (var (carte, version) in apres.Pilotes)
            if (avant.Pilotes.TryGetValue(carte, out var ancienne) && ancienne != version)
            {
                var nom = apres.NomsDesCartes?.GetValueOrDefault(carte) ?? carte;
                ecarts.Add(new Ecart($"monde:pilote:{carte}:{version}", Docteur.T("MO.Pilote", nom, ancienne, version)));
            }
        // Les deux lus seulement : un état non lu n'est ni « rallumée » ni « coupée ».
        if (avant.Hvci is { } hvciAvant && apres.Hvci is { } hvciApres && hvciAvant != hvciApres)
            ecarts.Add(new Ecart($"monde:hvci:{(hvciApres ? "active" : "coupee")}:{apres.Quand:yyyyMMddHHmm}",
                Docteur.T(hvciApres ? "MO.HvciActive" : "MO.HvciCoupee")));
        return ecarts;
    }

    /// <summary>Ce qui se lit à ce scan : les jeux, et les cartes (pilote et nom, par identifiant d'instance) ; chaque
    /// partie est null si elle n'a pas pu être lue.</summary>
    public static (Dictionary<string, string>? Jeux, Dictionary<string, string>? Pilotes, Dictionary<string, string>? Noms) Lire()
    {
        var jeux = JeuxProuves.Lire()?.Where(j => j.Executable is not null && j.Version is not null)
            .GroupBy(j => j.Dossier, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Version!);
        try
        {
            Dictionary<string, string> pilotes = [], noms = [];
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    if (o["PNPDeviceID"] is string id && o["DriverVersion"] is string v)
                        (pilotes[id], noms[id]) = (v, o["Name"] as string ?? id);
            return (jeux, pilotes, noms);
        }
        catch (Exception) { return (jeux, null, null); }
    }

    /// <summary>Pur : l'état à garder. Ce qui a été lu remplace l'ancien ; ce qui ne l'a pas été garde son état d'avant
    /// tel quel, sans quoi une mise à jour à cheval sur un scan raté ne serait plus vue.</summary>
    public static Etat Garder(Etat? avant, Dictionary<string, string>? jeux, Dictionary<string, string>? pilotes,
        Dictionary<string, string>? noms, DateTimeOffset maintenant, bool? hvci = null)
        => new(maintenant, jeux ?? avant?.Jeux ?? [], pilotes ?? avant?.Pilotes ?? [],
            pilotes is null ? avant?.NomsDesCartes : noms, hvci ?? avant?.Hvci);

    /// <summary>L'intégrité de la mémoire tourne-t-elle ? Win32_DeviceGuard, SecurityServicesRunning contient 2 (comme
    /// <c>IntegriteMemoire</c>). Null si la classe ne répond pas.</summary>
    public static bool? LireHvci()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    if (o["SecurityServicesRunning"] is int[] services) return services.Contains(2);
            return null;
        }
        catch (Exception) { return null; }
    }

    public static Etat? LirePrecedent(string fichier)
    {
        // Un état sans ses listes ({}) vaut pas d'état : la comparaison suivante n'a rien d'avant.
        try { return File.Exists(fichier) && JsonSerializer.Deserialize<Etat>(File.ReadAllText(fichier)) is { Jeux: not null, Pilotes: not null } e ? e : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Compare l'état présent au précédent, rend les écarts (que l'appelant note dans la frise) puis garde le
    /// présent. Un premier relevé ne rend rien : il n'y a pas encore d'avant.</summary>
    public static (IReadOnlyList<Ecart> Ecarts, DateTimeOffset? Depuis) ComparerEtGarder(string fichier, DateTimeOffset maintenant)
    {
        var (jeux, pilotes, noms) = Lire();
        var hvci = LireHvci();
        if (jeux is null && pilotes is null && hvci is null) return ([], null);
        var avant = LirePrecedent(fichier);
        var present = Garder(avant, jeux, pilotes, noms, maintenant, hvci);
        new FichierJsonAtomique(fichier, ErrorCodes.StorageFail).WriteAtomic(JsonSerializer.Serialize(present));
        return avant is null ? ([], null) : (Comparer(avant, present), avant.Quand);
    }
}
