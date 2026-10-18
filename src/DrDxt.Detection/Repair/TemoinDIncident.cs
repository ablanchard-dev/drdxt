using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Le témoin d'incident : quand la machine plante, ce que Windows a noté juste avant, relevé sans que le joueur lance
/// quoi que ce soit. Le service de DrDXT l'écrit (code compilé, aucun script, dossier aux droits fixés par DrDXT) ; le
/// docteur le montre au lancement suivant. Ici : ce qui déclenche un témoin, la fenêtre relue, la lecture des journaux
/// et le constat. Lecture seule.
/// </summary>
public static class TemoinDIncident
{
    /// <summary>Un événement relevé dans la fenêtre : le message n'est pas gardé (il est traduit et peut nommer des
    /// chemins) ; le fournisseur et le numéro suffisent à le retrouver.</summary>
    public sealed record Ligne(DateTimeOffset Quand, string Journal, string Fournisseur, int Id, int Niveau);

    public sealed record Temoin(DateTimeOffset Incident, string Genre, DateTimeOffset Debut, DateTimeOffset Fin, IReadOnlyList<Ligne> Lignes);

    /// <summary>Fenêtres relues et intervalle minimal entre deux témoins. **Non chiffrés** : assez pour voir ce qui
    /// précède un plantage, assez peu pour qu'une rafale d'erreurs n'écrive pas un fichier par seconde.</summary>
    public static readonly TimeSpan AvantIncident = TimeSpan.FromMinutes(10), AvantDemarrage = TimeSpan.FromMinutes(60),
        Intervalle = TimeSpan.FromMinutes(2);

    public const int Gardes = 20, PlafondLignes = 300;
    public const string Dossier = "temoins";

    /// <summary>Ce qui déclenche un témoin dans le journal Système : arrêt brutal (Kernel-Power 41), erreur matérielle
    /// fatale (WHEA-Logger 18), pilote graphique relancé (Display 4101), mémoire virtuelle épuisée
    /// (Resource-Exhaustion-Detector 2004). Les mêmes fournisseurs et numéros que <see cref="PannesDuJournal"/>.</summary>
    public static readonly (string Fournisseur, int Id, string Genre)[] Declencheurs =
    [
        ("Microsoft-Windows-Kernel-Power", 41, "arret brutal"),
        ("Microsoft-Windows-WHEA-Logger", 18, "erreur materielle fatale"),
        ("Display", 4101, "pilote graphique relance"),
        ("Microsoft-Windows-Resource-Exhaustion-Detector", 2004, "memoire virtuelle epuisee"),
    ];

    /// <summary>La requête des déclencheurs du journal Système depuis <paramref name="depuis"/>.</summary>
    public static string Requete(DateTimeOffset? depuis = null)
        => "*[System[(" + string.Join(" or ", Declencheurs.Select(d => $"(Provider[@Name='{d.Fournisseur}'] and EventID={d.Id})")) + ")"
           + (depuis is { } d0 ? $" and TimeCreated[@SystemTime>='{d0.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}']" : "") + "]]";

    public static string? Genre(string? fournisseur, int id)
        => Declencheurs.FirstOrDefault(d => d.Fournisseur == fournisseur && d.Id == id).Genre;

    /// <summary>Pur : la fenêtre relue. Un arrêt brutal est noté au démarrage qui le suit : ce qui l'a précédé est
    /// dans l'heure d'avant cette note. Tout autre incident : les dix minutes d'avant.</summary>
    public static (DateTimeOffset Debut, DateTimeOffset Fin) Fenetre(string genre, DateTimeOffset incident)
        => (incident - (genre == Declencheurs[0].Genre ? AvantDemarrage : AvantIncident), incident);

    /// <summary>Pur : au plus un témoin toutes les deux minutes.</summary>
    public static bool AEcrire(DateTimeOffset incident, DateTimeOffset? dernier)
        => dernier is not { } d || incident - d >= Intervalle || d - incident >= Intervalle;

    /// <summary>Les erreurs et avertissements des journaux Système et Application dans la fenêtre (niveaux 1 à 3).</summary>
    public static IReadOnlyList<Ligne> LireFenetre(DateTimeOffset debut, DateTimeOffset fin)
    {
        var lignes = new List<Ligne>();
        foreach (var journal in new[] { "System", "Application" })
        {
            try
            {
                var requete = new EventLogQuery(journal, PathType.LogName,
                    "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime>='"
                    + debut.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "' and @SystemTime<='"
                    + fin.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "']]]");
                using var lecteur = new EventLogReader(requete);
                for (var e = lecteur.ReadEvent(); e is not null && lignes.Count < PlafondLignes; e = lecteur.ReadEvent())
                    using (e)
                        if (e.TimeCreated is { } t)
                            lignes.Add(new Ligne(new DateTimeOffset(t), journal, e.ProviderName ?? "?", e.Id, e.Level ?? 0));
            }
            catch (EventLogException) { /* un journal illisible : le témoin le dit par son absence de lignes */ }
            catch (UnauthorizedAccessException) { }
        }
        return lignes.OrderBy(l => l.Quand).ToList();
    }

    /// <summary>Les témoins écrits, du plus récent au plus ancien ; null si le dossier n'a pas pu être lu.</summary>
    public static IReadOnlyList<Temoin>? LireTous(string dossier)
    {
        try
        {
            if (!Directory.Exists(dossier)) return [];
            return Directory.EnumerateFiles(dossier, "temoin-*.json")
                .Select(f => { try { return JsonSerializer.Deserialize<Temoin>(File.ReadAllText(f)); } catch (Exception) { return null; } })
                .OfType<Temoin>().OrderByDescending(t => t.Incident).ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : le constat du docteur, pour le dernier témoin des trente derniers jours.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Temoin>? temoins, DateTimeOffset maintenant)
    {
        if (temoins?.FirstOrDefault(t => maintenant - t.Incident <= TimeSpan.FromDays(30)) is not { } t) yield break;
        var quand = t.Incident.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue);
        var minutes = (int)Math.Round((t.Fin - t.Debut).TotalMinutes);
        var erreurs = t.Lignes.Where(l => l.Niveau is 1 or 2).ToList();
        var parSource = t.Lignes.GroupBy(l => $"{l.Fournisseur} {l.Id}").OrderByDescending(g => g.Count()).Take(5)
            .Select(g => $"{g.Key} ({g.Count()}x)");
        yield return new RepairFinding("STABILITE", "temoin d'incident", Docteur.T("TI.Valeur", quand), null, RepairStatus.Observed,
            (t.Lignes.Count == 0
                ? Docteur.T("TI.Vide", quand, Docteur.T("TI.Genre." + t.Genre), minutes)
                : Docteur.T("TI.Releve", quand, Docteur.T("TI.Genre." + t.Genre), minutes, t.Lignes.Count, erreurs.Count,
                    string.Join(", ", parSource)))
            + (temoins!.Count(x => maintenant - x.Incident <= TimeSpan.FromDays(30)) is var n and > 1 ? Docteur.T("TI.Autres", n - 1) : ""));
    }
}
