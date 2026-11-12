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
    /// (Resource-Exhaustion-Detector 2004). Display 4101 et Resource-Exhaustion-Detector 2004 sont ceux de
    /// <see cref="PannesDuJournal"/> ; Kernel-Power 41 et WHEA-Logger 18 sont lus par les familles des arrêts et des erreurs
    /// matérielles.</summary>
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

    /// <summary>Pur : la fenêtre relue. Un arrêt brutal est noté au démarrage qui le suit, et le PC a pu rester éteint
    /// des heures entre les deux : ce qui l'a précédé est dans l'heure d'avant <paramref name="dernierSigne"/>, le dernier
    /// événement de la séance interrompue. Non trouvé : l'heure d'avant la note, et le docteur le dit. Tout autre
    /// incident est noté sur le vif : les dix minutes d'avant.</summary>
    public static (DateTimeOffset Debut, DateTimeOffset Fin) Fenetre(string genre, DateTimeOffset incident, DateTimeOffset? dernierSigne = null)
    {
        if (SeNoteApresCoup(genre) && dernierSigne is { } signe && signe < incident) return (signe - AvantDemarrage, signe);
        return (incident - (genre == Declencheurs[0].Genre ? AvantDemarrage : AvantIncident), incident);
    }

    /// <summary>Pur : ce qui arrête la machine (arrêt brutal, erreur matérielle fatale) ne peut être noté qu'au démarrage
    /// suivant. Le reste (pilote relancé, mémoire épuisée) est noté sur le vif, Windows tournant toujours.</summary>
    public static bool SeNoteApresCoup(string genre) => genre == Declencheurs[0].Genre || genre == Declencheurs[1].Genre;

    /// <summary>Pur : dans le journal Système lu de <paramref name="note"/> vers le passé, l'événement qui précède le
    /// démarrage de Windows (Kernel-General 12, le premier de chaque séance) est le dernier que la séance interrompue a
    /// écrit. Seulement si Windows a démarré dans les <see cref="AvantIncident"/> d'avant la note : au-delà, l'incident
    /// est de cette séance-ci. Null sans ce démarrage ou sans rien avant lui.</summary>
    public static DateTimeOffset? DernierSigneDeVie(IEnumerable<(DateTimeOffset Quand, string? Fournisseur, int Id)> duPlusRecentAuPlusAncien,
        DateTimeOffset note)
    {
        var demarrage = false;
        foreach (var e in duPlusRecentAuPlusAncien)
        {
            if (demarrage) return e.Quand;
            if (note - e.Quand > AvantIncident) return null;
            demarrage = e is { Fournisseur: "Microsoft-Windows-Kernel-General", Id: 12 };
        }
        return null;
    }

    /// <summary>Ce qu'un démarrage écrit avant la note d'un incident tient en quelques dizaines de lignes ; au-delà de
    /// cette borne, le démarrage n'est pas cherché plus loin.</summary>
    public const int PlafondDuDemarrage = 5000;

    /// <summary>Le dernier signe de vie avant la note d'un arrêt brutal, lu dans le journal Système ; null s'il ne se lit pas.</summary>
    public static DateTimeOffset? LireDernierSigneDeVie(DateTimeOffset note)
    {
        try
        {
            var requete = new EventLogQuery("System", PathType.LogName, "*[System[TimeCreated[@SystemTime<='"
                + note.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "']]]") { ReverseDirection = true };
            using var lecteur = new EventLogReader(requete);
            return DernierSigneDeVie(Lus(lecteur), note);
        }
        catch (EventLogException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<(DateTimeOffset, string?, int)> Lus(EventLogReader lecteur)
    {
        var n = 0;
        for (var e = lecteur.ReadEvent(); e is not null && n < PlafondDuDemarrage; e = lecteur.ReadEvent(), n++)
            using (e)
                if (e.TimeCreated is { } t) yield return (new DateTimeOffset(t), e.ProviderName, e.Id);
    }

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

    /// <summary>Les témoins écrits, du plus récent au plus ancien ; null si le dossier n'a pas pu être lu. Un fichier hors
    /// confiance (posé par un compte avant l'installation : la réparation du dossier ne descend pas) n'est pas lu.</summary>
    public static IReadOnlyList<Temoin>? LireTous(string dossier, Func<string, bool> fiable)
    {
        try
        {
            if (!Directory.Exists(dossier)) return [];
            return Directory.EnumerateFiles(dossier, "temoin-*.json").Where(fiable)
                .Select(f => { try { return JsonSerializer.Deserialize<Temoin>(File.ReadAllText(f)); } catch (Exception) { return null; } })
                .OfType<Temoin>().OrderByDescending(t => t.Incident).ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Les témoins de trop, à supprimer : les plus anciens par leur nom, au-delà des <see cref="Gardes"/>. Un fichier
    /// hors confiance n'occupe aucune place gardée (sinon des noms plus récents posés d'avance feraient supprimer chaque vrai
    /// témoin aussitôt écrit), et n'est jamais supprimé : le service ne suit pas un lien posé par un autre.</summary>
    public static IEnumerable<string> EnTrop(string dossier, Func<string, bool> fiable)
        => Directory.EnumerateFiles(dossier, "temoin-*.json").Where(fiable)
            .OrderByDescending(f => f, StringComparer.Ordinal).Skip(Gardes);

    /// <summary>Pur : le constat du docteur, pour le dernier témoin des trente derniers jours.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Temoin>? temoins, DateTimeOffset maintenant)
    {
        if (temoins?.FirstOrDefault(t => maintenant - t.Incident <= TimeSpan.FromDays(30)) is not { } t) yield break;
        // Un arrêt brutal se date de son dernier signe de vie, pas du démarrage qui l'a noté. Fenêtre finie à la note : le
        // signe n'a pas été trouvé, c'est l'heure d'avant le redémarrage qui a été relue, et la phrase le dit.
        var brutal = t.Genre == Declencheurs[0].Genre;
        var avantRedemarrage = brutal && t.Fin >= t.Incident;
        var apresCoup = SeNoteApresCoup(t.Genre) && t.Fin < t.Incident;
        var quand = (apresCoup ? t.Fin : t.Incident).ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue);
        var minutes = (int)Math.Round((t.Fin - t.Debut).TotalMinutes);
        var erreurs = t.Lignes.Where(l => l.Niveau is 1 or 2).ToList();
        var parSource = t.Lignes.GroupBy(l => $"{l.Fournisseur} {l.Id}").OrderByDescending(g => g.Count()).Take(5)
            .Select(g => $"{g.Key} ({g.Count()}x)");
        yield return new RepairFinding("STABILITE", "temoin d'incident", Docteur.T("TI.Valeur", quand), null, RepairStatus.Observed,
            (t.Lignes.Count == 0
                ? Docteur.T(avantRedemarrage ? "TI.RedemarrageVide" : "TI.Vide", quand, Docteur.T("TI.Genre." + t.Genre), minutes)
                : Docteur.T(avantRedemarrage ? "TI.RedemarrageReleve" : "TI.Releve", quand, Docteur.T("TI.Genre." + t.Genre), minutes, t.Lignes.Count, erreurs.Count,
                    string.Join(", ", parSource)))
            + (temoins!.Count(x => maintenant - x.Incident <= TimeSpan.FromDays(30)) is var n and > 1 ? Docteur.T("TI.Autres", n - 1) : ""));
    }
}
