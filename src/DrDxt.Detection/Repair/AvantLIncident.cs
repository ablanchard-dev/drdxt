using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>Un fait daté : un incident ou un changement. <paramref name="AuJour"/> : seul le jour est connu (logiciels).</summary>
public sealed record FaitDate(DateTimeOffset Quand, string Libelle, bool AuJour = false);

/// <summary>
/// Chaque incident
/// (arrêt brutal, écran bleu, blocage graphique) est rapproché de ce qui a été installé dans les jours qui le précèdent :
/// pilotes, à la seconde, lus dans le journal d'installation de Windows (<c>%windir%\INF\setupapi.dev*.log</c>, qui garde
/// des mois d'historique) ; logiciels, au jour, lus dans les clés de désinstallation. Un rapprochement de dates, jamais
/// une cause affirmée. Lecture seule.
/// </summary>
public static class AvantLIncident
{
    // Trois jours, choix non chiffré (assez pour « installé hier soir, planté ce matin », assez court pour ne pas
    // tout rapprocher de tout) ; à revoir si un vrai cas tombe juste au-delà.
    public static readonly TimeSpan Fenetre = TimeSpan.FromDays(3);
    // La même panne dans les deux semaines avant l'installation dit une série déjà en cours ; un incident isolé
    // des mois plus tôt n'efface plus le rapprochement. Choix non chiffré, comme la fenêtre.
    private static readonly TimeSpan SerieEnCours = TimeSpan.FromDays(14);
    private const int IncidentsMontres = 5;
    private static readonly TimeSpan Recent = TimeSpan.FromDays(30);

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<FaitDate> incidents, IReadOnlyList<FaitDate> changements, DateTimeOffset maintenant)
    {
        // La date d'un logiciel est réécrite à chaque mise à jour automatique
        // (relevé le 02/10 sur une machine réelle : Edge, Chrome et Brave). Elle ne déclenche donc rien : seuls les pilotes, datés à la seconde, rapprochent ;
        // les logiciels de la même période sont dits en contexte.
        var rapproches = incidents.OrderByDescending(i => i.Quand)
            .Select(i => (i, avant: changements.Where(c => !c.AuJour && Precede(c, i)).OrderBy(c => c.Quand).ToList()))
            .Where(x => x.avant.Count > 0)
            // Une panne déjà présente dans les deux semaines avant la première de ces installations ne vient pas
            // d'elles : rien n'est rapproché, sinon une série d'arrêts serait attribuée à une installation plus tardive.
            .Where(x => !incidents.Any(y => y.Libelle == x.i.Libelle && y.Quand < x.avant[0].Quand
                                            && x.avant[0].Quand - y.Quand <= SerieEnCours))
            .Take(IncidentsMontres);
        foreach (var (i, avant) in rapproches)
        {
            var logiciels = changements.Where(c => c.AuJour && Precede(c, i)).OrderBy(c => c.Quand).ToList();
            var recent = maintenant - i.Quand <= Recent;
            // La liste « Mesuré » ne montre que la valeur : l'installation la plus proche y entre, pas seulement l'incident.
            var proche = avant[^1];
            yield return new RepairFinding("STABILITE", "incident après une installation",
                Docteur.T("AI.Valeur", i.Libelle, i.Quand.ToLocalTime(), proche.Libelle, Ecart(proche, i))
                + (avant.Count > 1 ? Docteur.T("AI.EtAutres", avant.Count - 1) : ""), null,
                recent ? RepairStatus.Suspicious : RepairStatus.Observed,
                Docteur.T("AI.Debut", Majuscule(i.Libelle), i.Quand.ToLocalTime(), Fenetre.TotalDays,
                    string.Join(Docteur.T("AI.Separateur"), avant.Select(c => $"{c.Libelle}, {Ecart(c, i)}")))
                + (logiciels.Count == 0 ? ""
                    : Docteur.T("AI.Logiciels", string.Join(Docteur.T("AI.Separateur"), logiciels.Select(c => $"{c.Libelle}, {Ecart(c, i)}"))))
                // Sans contredire le constat de la version du pilote.
                + Docteur.T("AI.Rapprochement"),
                recent ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    // Un logiciel daté au jour compte depuis minuit : le rapprochement n'est dit que s'il tient pour toute heure du jour.
    private static bool Precede(FaitDate c, FaitDate i) => c.Quand <= i.Quand && i.Quand - c.Quand <= Fenetre;

    private static string Ecart(FaitDate c, FaitDate i)
    {
        if (c.AuJour)
            return c.Quand.Date == i.Quand.ToLocalTime().Date
                ? Docteur.T("AI.MemeJour")
                : Docteur.T("AI.LeJour", c.Quand);
        var h = (i.Quand - c.Quand).TotalHours;
        var delai = h < 1 ? Docteur.T("AI.MoinsDUneHeure") : h < 48 ? Docteur.T("AI.Heures", h) : Docteur.T("AI.Jours", h / 24);
        return Docteur.T("AI.Installe", c.Quand.ToLocalTime(), delai);
    }

    private static string Majuscule(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("fr-FR")) + s[1..];

    // ── Lecture ─────────────────────────────────────────────────────────────────────────

    private static readonly Regex Section = new(@"^>>>  Section start (\d{4}/\d\d/\d\d \d\d:\d\d:\d\d)", RegexOptions.Compiled);
    private static readonly Regex Fournisseur = new(@"^\s*inf:\s+Provider:\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^\s*inf:\s+Driver Version:\s+[\d/]+,(\S+)", RegexOptions.Compiled);

    /// <summary>Les paquets de pilote tiers importés, à leur première apparition (format relevé le 03/10/2026 sur la
    /// machine : « Section start », puis « inf: Provider » et « inf: Driver Version » ; les pilotes Microsoft sont laissés).</summary>
    internal static IReadOnlyList<FaitDate> PilotesInstalles(IEnumerable<string> lignes)
    {
        var premiers = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? quand = null; string? fournisseur = null;
        foreach (var l in lignes)
        {
            if (Section.Match(l) is { Success: true } s)
            {
                quand = DateTime.TryParseExact(s.Groups[1].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var d) ? new DateTimeOffset(d) : null;
                fournisseur = null;
            }
            else if (Fournisseur.Match(l) is { Success: true } f) fournisseur = f.Groups[1].Value.Trim();
            else if (Version.Match(l) is { Success: true } v && fournisseur is not null && quand is { } q
                     && !fournisseur.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                var cle = $"{fournisseur} {v.Groups[1].Value}";
                if (!premiers.TryGetValue(cle, out var deja) || q < deja) premiers[cle] = q;
            }
        }
        return premiers.Select(p => new FaitDate(p.Value, "pilote " + Lisible(p.Key))).ToList();
    }

    /// <summary>« NVIDIA 32.0.16.1656 » se lit aussi « 616.56 », le numéro qu'affiche NVIDIA.</summary>
    private static string Lisible(string pilote)
    {
        var morceaux = pilote.Split(' ');
        var version = morceaux[^1];
        return pilote.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) && version.StartsWith("3", StringComparison.Ordinal)
               && DrDxt.Detection.Gpu.VersionDuPilote.VersionNvidia(version) is { } nv && version.Count(c => c == '.') == 3
            ? $"{pilote} ({nv})" : pilote;
    }

    public static IReadOnlyList<FaitDate> LirePilotes()
    {
        try
        {
            var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            return PilotesInstalles(Directory.EnumerateFiles(inf, "setupapi.dev*.log").SelectMany(f =>
            {
                try { return File.ReadLines(f).ToList(); } catch { return []; }
            }));
        }
        catch { return []; }
    }

    /// <summary>« PhysX 9.26.0703 9.26.0703 », la version était déjà dans le nom affiché.</summary>
    internal static string LibelleLogiciel(string nom, string version) =>
        version.Length == 0 || nom.Contains(version, StringComparison.OrdinalIgnoreCase) ? Docteur.T("AI.Logiciel", nom) : Docteur.T("AI.LogicielVersion", nom, version);

    public static IReadOnlyList<FaitDate> LireLogiciels()
    {
        try
        {
            return InventaireMachine.LogicielsInstalles()
                .Where(e => e.Date is not null)
                .Select(e => DateTime.TryParseExact(e.Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
                    ? new FaitDate(new DateTimeOffset(d), LibelleLogiciel(e.Nom, e.Version), AuJour: true) : null)
                .OfType<FaitDate>().ToList();
        }
        catch { return []; }
    }
}
