namespace DrDxt.Core;

/// <summary>Les grandeurs physiques qu'une passe rend, chacune jugee separement.</summary>
public enum GameMetricKind
{
    /// <summary>Le debit. <c>1000 / frametime moyen</c>, jamais la moyenne de FPS instantanes.</summary>
    Moyenne,

    /// <summary>Le 1 % low : moyenne des 1 % de frames les plus longues, exprimee en FPS.
    /// C'est ce que le joueur ressent quand ca accroche.</summary>
    UnPourCentLow,

    /// <summary>Le p95 des frametimes : la frame plus lente que 95 % des autres.
    ///
    /// Ce n'est pas « la fluidite » : les 5 % de frames les plus lentes, pas le pire centile, et un reglage peut
    /// l'ameliorer en degradant les a-coups visibles. Sur un a-coup toutes les 60 frames (08/09/2026, `tools/tailbench
    /// --formes`), le p95 rend +1,6 % quand le 1 % low rend -65,6 % (regression prouvee 13 fois sur 14). D'ou « regularite
    /// p95 amelioree » et « 1 % low non resolu » dans la meme phrase, jamais « la fluidite est prouvee ».</summary>
    P95Frametime,
}

/// <summary>
/// Une preuve par grandeur, et la ponderation vient apres.
///
/// Un score composite ne se resout pas (en Warzone, +/-72 % quand la moyenne seule resolvait a +/-4,6 %) : chaque grandeur
/// porte son ecart et sa resolution, et la ponderation, preference du joueur, n'arbitre qu'ensuite entre des preuves.
///
/// Le corpus : huit parties, une estimation empirique (92 positions glissantes ne sont pas 92 experiences, deux voisines
/// partagent 6 fenetres sur 7). Mesure le 08/09/2026, capture comme unite independante, fenetres disjointes,
/// leave-one-capture-out (`tools/tailbench --loco 10 8`) :
///
///   8 holdouts, 15 experiences independantes, **0 faux verdict**, 1 invalide.
///   La procedure choisit le p95 frametime sur 7 holdouts sur 8.
///   Ecart maximal observe sous no-op, par capture : 2,0 % a 15,0 % selon la partie.
///
/// Le p95 frametime est la meilleure grandeur de queue robuste sur ce corpus, choisie sans voir la partie evaluee. La
/// resolution varie d'un facteur ~8 d'une partie a l'autre : chaque experience annonce la sienne. Valide independamment :
/// 8 alternances de 10 s ; les 24 du protocole de production ne laissent que 3 experiences disjointes sur 2 captures.
///
/// Trois methodes rejetees par la mesure :
///   * le **p99** frametime est systematiquement surconfiant (annonce 9,2 % pour un ecart p95
///     observe de 15,3 % a 8 alternances) : il fabriquerait des verdicts ;
///   * mettre les frames en commun par etat au lieu de les agreger par fenetre empire le 1 % low
///     (110 % puis 165 % quand on ajoute des donnees), parce que le pire centile d'un grand pool
///     ne selectionne plus que quelques a-coups geants ;
///   * le **0,1 % low** comme grandeur de decision : sous un effet reel de +2 % il rend une
///     mediane de -3,2 %, il se trompe de sens plus souvent qu'il ne voit juste.
/// </summary>
public static class GameMetricSet
{
    /// <summary>Les grandeurs qu'une session mesure et juge, dans l'ordre d'affichage.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<GameMetricKind> Toutes =
        [GameMetricKind.Moyenne, GameMetricKind.UnPourCentLow, GameMetricKind.P95Frametime];

    /// <summary>Le nom stable d'une grandeur, pour les preuves et le journal.</summary>
    public static string Nom(GameMetricKind k) => k switch
    {
        GameMetricKind.Moyenne => "fps.moyen",
        GameMetricKind.UnPourCentLow => "fps.p1low",
        GameMetricKind.P95Frametime => "frametime.p95",
        _ => "inconnu",
    };

    /// <summary>Le libelle joueur.</summary>
    public static string Libelle(GameMetricKind k) => k switch
    {
        GameMetricKind.Moyenne => Docteur.T("GM.Moyenne"),
        GameMetricKind.UnPourCentLow => "1 % low",
        GameMetricKind.P95Frametime => Docteur.T("GM.P95"),
        _ => "?",
    };

    /// <summary>
    /// La valeur d'une grandeur dans le sens « plus grand = mieux », ou <c>0</c> si elle n'a pas
    /// ete mesuree.
    ///
    /// Le p95 frametime est en millisecondes et se lit a l'envers : 8 ms valent mieux que
    /// 12 ms. Il est rendu ici en FPS equivalents. Sans cette inversion, un vrai gain de fluidite
    /// porterait le signe d'une degradation et le produit annulerait exactement ce qu'il cherche.
    /// </summary>
    public static double Valeur(GameMetrics m, GameMetricKind k) => k switch
    {
        GameMetricKind.Moyenne => m.AvgFps,
        GameMetricKind.UnPourCentLow => m.P1LowFps,
        GameMetricKind.P95Frametime => m.P95FrametimeMs is > 0 ? 1000.0 / m.P95FrametimeMs.Value : 0.0,
        _ => 0.0,
    };
}

/// <summary>Ce qu'une grandeur a etabli pendant une experience : son ecart, sa resolution, son
/// verdict. Le trio est indissociable : un ecart sans sa resolution est une promesse.</summary>
public sealed record MetricEvidence(GameMetricKind Metrique, Evidence Preuve)
{
    public string Nom => GameMetricSet.Nom(Metrique);
    public string Libelle => GameMetricSet.Libelle(Metrique);
}
