using System.Globalization;

namespace DrDxt.Core;

/// <summary>Les heures d'activité de Windows Update telles que lues (0 à 23, fin exclue : 15 et 8 = de 15 h à 8 h).
/// <paramref name="Automatique"/> : <c>true</c> quand Windows les règle tout seul d'après l'activité, <c>false</c> en manuel,
/// <c>null</c> quand la valeur est absente (non lue, jamais « manuel »).</summary>
public sealed record HeuresActivesLues(int Debut, int Fin, bool? Automatique);

/// <summary>Une partie relevée réduite à ce qui compte ici : quand elle a commencé et fini.</summary>
public sealed record PlageDeJeu(DateTimeOffset Debut, DateTimeOffset Fin);

/// <summary>
/// 06/10/2026 (chantier L-WU) : Windows Update a redémarré le PC du mainteneur à 10:15 pour deux pilotes. Microsoft
/// (Learn, « Manage device restarts after updates ») : « Automatic restarts after an update occur outside of the active
/// hours », 18 h au plus depuis l'heure de début. Les heures de jeu sont connues par le relevé des parties : ce type dit si
/// les parties tombent hors des heures d'activité, et quelle plage les couvrirait. Source et mesure :
/// Microsoft Learn, « Manage device restarts after updates » et « Policy CSP - Update » (ActiveHoursMaxRange).
/// </summary>
public static class HeuresActives
{
    /// <summary>Microsoft Learn (Policy CSP Update, ActiveHoursMaxRange) : 18 h au plus depuis le début.</summary>
    public const int DureeMax = 18;

    public const string Cle = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    public const string NomDebut = "ActiveHoursStart";
    public const string NomFin = "ActiveHoursEnd";
    public const string NomAutomatique = "SmartActiveHoursState";

    /// <summary>Le processus de Windows Update qui demande ses redémarrages (événement 1074, relevé le 06/10).</summary>
    public const string ProcessusWindowsUpdate = "MoUsoCoreWorker";

    /// <summary>Lecture brute ⇒ heures, ou <c>null</c> quand une heure manque ou sort de 0 à 23.</summary>
    public static HeuresActivesLues? Depuis(object? debut, object? fin, object? automatique)
        => debut is int d and >= 0 and <= 23 && fin is int f and >= 0 and <= 23
            ? new HeuresActivesLues(d, f, automatique is int a ? a == 1 : null)
            : null;

    /// <summary>07/10/2026 (revue F-18) : les heures protégées aujourd'hui que la plage proposée ne protège plus. La
    /// plage proposée glisse d'un bloc : ce qu'elle perd est contigu, d'où un seul intervalle.</summary>
    public static string Perdues(HeuresActivesLues actuelles, (int Debut, int Fin) proposee)
    {
        static HashSet<int> Heures(int d, int f) => Enumerable.Range(0, Duree(d, f)).Select(k => (d + k) % 24).ToHashSet();
        var gardees = Heures(proposee.Debut, proposee.Fin);
        var perdues = Enumerable.Range(0, Duree(actuelles.Debut, actuelles.Fin)).Select(k => (actuelles.Debut + k) % 24)
            .Where(h => !gardees.Contains(h)).ToList();
        return perdues.Count == 0 ? ""
            : $" En échange, Windows pourra redémarrer de {Texte(perdues[0], (perdues[^1] + 1) % 24)}, aujourd'hui protégé.";
    }

    /// <summary>Durée de la plage, de 1 à 24 (début égal à fin : la journée entière, que Windows ne propose pas).</summary>
    public static int Duree(int debut, int fin) => (fin - debut + 24) % 24 is var n && n == 0 ? 24 : n;

    public static bool Couvre(int debut, int fin, int heure) => (heure - debut + 24) % 24 < Duree(debut, fin);

    /// <summary>Les heures (locales) que la partie touche, de l'heure de son début à celle de sa fin.</summary>
    public static IEnumerable<int> HeuresTouchees(PlageDeJeu p, TimeZoneInfo fuseau)
    {
        var debut = TimeZoneInfo.ConvertTime(p.Debut, fuseau);
        var fin = TimeZoneInfo.ConvertTime(p.Fin, fuseau);
        if (fin < debut) yield break;
        // Une partie de plus d'une journée touche toutes les heures : borné à 24.
        var heures = Math.Min(24, (int)(new DateTime(fin.Year, fin.Month, fin.Day, fin.Hour, 0, 0)
            - new DateTime(debut.Year, debut.Month, debut.Day, debut.Hour, 0, 0)).TotalHours + 1);
        for (var i = 0; i < heures; i++) yield return (debut.Hour + i) % 24;
    }

    public static IReadOnlyList<PlageDeJeu> Hors(HeuresActivesLues h, IReadOnlyList<PlageDeJeu> parties, TimeZoneInfo fuseau)
        => parties.Where(p => HeuresTouchees(p, fuseau).Any(x => !Couvre(h.Debut, h.Fin, x))).ToList();

    /// <summary>
    /// La plage à poser : elle couvre toutes les heures des parties, garde le plus d'heures actuelles possible, puis est la
    /// plus courte ; 18 h au plus. <c>null</c> quand rien n'est à changer (aucune partie, ou toutes déjà couvertes) ou
    /// quand les parties s'étalent sur plus de 18 h (Windows ne permet pas de toutes les couvrir).
    /// </summary>
    public static (int Debut, int Fin)? Proposer(HeuresActivesLues h, IReadOnlyList<PlageDeJeu> parties, TimeZoneInfo fuseau)
    {
        var jeu = parties.SelectMany(p => HeuresTouchees(p, fuseau)).ToHashSet();
        if (jeu.Count == 0 || jeu.All(x => Couvre(h.Debut, h.Fin, x))) return null;

        (int Debut, int Fin)? meilleure = null;
        var (gardeesMax, dureeMin) = (-1, int.MaxValue);
        for (var debut = 0; debut < 24; debut++)
            for (var duree = 1; duree <= DureeMax; duree++)
            {
                var fin = (debut + duree) % 24;
                if (!jeu.All(x => Couvre(debut, fin, x))) continue;
                var gardees = Enumerable.Range(0, 24).Count(x => Couvre(h.Debut, h.Fin, x) && Couvre(debut, fin, x));
                if (gardees > gardeesMax || (gardees == gardeesMax && duree < dureeMin))
                    (meilleure, gardeesMax, dureeMin) = ((debut, fin), gardees, duree);
            }
        return meilleure;
    }

    /// <summary>« 15 h à 8 h ».</summary>
    public static string Texte(int debut, int fin) => $"{debut} h à {fin} h";

    /// <summary>
    /// Le constat du docteur, en lecture seule. <paramref name="heures"/> <c>null</c> = non lues ; <paramref name="redemarrages"/>
    /// <c>null</c> = journal non lu (« non lu » n'est jamais « aucun ») ; ce sont les redémarrages demandés par Windows Update
    /// dans la fenêtre <paramref name="jours"/>.
    /// </summary>
    public static RepairFinding Juger(HeuresActivesLues? heures, IReadOnlyList<PlageDeJeu> parties,
        IReadOnlyList<DateTimeOffset>? redemarrages, int jours, TimeZoneInfo fuseau)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        string Quand(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, fuseau).ToString("dd/MM 'à' HH:mm", fr);
        var pendant = redemarrages?.Where(r => parties.Any(p => p.Debut <= r && r <= p.Fin)).ToList();
        var bilanRedemarrages = redemarrages is null
            ? "Les redémarrages de Windows Update n'ont pas pu être lus."
            : redemarrages.Count == 0
                ? $"Windows Update n'a pas redémarré le PC en {jours} jours."
                : $"Windows Update a redémarré le PC {redemarrages.Count} fois en {jours} jours (dernière fois le "
                  + $"{Quand(redemarrages.Max())}), dont {pendant!.Count} pendant une partie relevée.";

        if (heures is null)
            return new RepairFinding("WINDOWS UPDATE", "heures d'activité", null, null, RepairStatus.Unknown,
                "les heures d'activité de Windows Update n'ont pas pu être lues : DrDXT ne sait pas quand Windows s'autorise "
                + $"à redémarrer. « Non lu » n'est pas « bien réglé ». {bilanRedemarrages}");

        var plage = Texte(heures.Debut, heures.Fin);
        var mode = heures.Automatique == true ? " (réglées automatiquement par Windows)" : "";
        if (parties.Count == 0)
            return new RepairFinding("WINDOWS UPDATE", "heures d'activité", plage, null,
                pendant is { Count: > 0 } ? RepairStatus.Suspicious : RepairStatus.Observed,
                $"Windows Update ne redémarre pas le PC de {plage}{mode} ; en dehors, il peut le faire. Aucune partie relevée : "
                + $"DrDXT ne peut pas encore comparer ces heures à tes heures de jeu. {bilanRedemarrages}");

        var hors = Hors(heures, parties, fuseau);
        if (hors.Count == 0)
            return new RepairFinding("WINDOWS UPDATE", "heures d'activité", plage, "couvrent tes parties",
                pendant is { Count: > 0 } ? RepairStatus.Suspicious : RepairStatus.ExpectedDefault,
                (parties.Count == 1 ? "ta partie relevée tombe" : $"tes {parties.Count} parties relevées tombent toutes")
                + $" dans tes heures d'activité ({plage}{mode}) : "
                + $"Windows Update ne redémarre pas le PC pendant ces heures-là. {bilanRedemarrages}");

        var proposee = Proposer(heures, parties, fuseau);
        var suite = heures.Automatique != false
            ? (heures.Automatique == true ? "Windows les règle tout seul" : "Windows ne dit pas s'il les règle tout seul")
              + " : pour les choisir, passe-les en manuel dans Paramètres > Windows Update > Options avancées > Heures "
              + "d'activité" + (proposee is { } r ? $" (de {Texte(r.Debut, r.Fin)} couvrirait tes parties)" : "")
              + " ; « Optimiser » pourra ensuite les régler."
            : proposee is { } p
                ? $"« Optimiser » propose de les régler de {Texte(p.Debut, p.Fin)} (18 h au plus, la limite de Windows) ; "
                  + "annulable." + Perdues(heures, p)
                : "Tes parties s'étalent sur plus de 18 h par jour, la limite de Windows : elles ne peuvent pas toutes être "
                  + "couvertes.";
        return new RepairFinding("WINDOWS UPDATE", "heures d'activité", plage,
            proposee is { } q && heures.Automatique == false ? Texte(q.Debut, q.Fin) : null,
            RepairStatus.Suspicious,
            (hors.Count == 1 ? $"1 partie relevée sur {parties.Count} tombe" : $"{hors.Count} parties relevées sur {parties.Count} tombent")
            + $" hors de tes heures d'activité ({plage}"
            + $"{mode}) : Windows Update peut redémarrer le PC pendant ces parties-là. {suite} Rien d'autre n'est touché : "
            + $"Windows Update reste allumé. {bilanRedemarrages}",
            RepairAction.Recommandation);
    }
}
