namespace DrDxt.Core;

/// <summary>Les grandeurs physiques qu'une passe rend, chacune jugee SEPAREMENT.</summary>
public enum GameMetricKind
{
    /// <summary>Le debit. <c>1000 / frametime moyen</c> — jamais la moyenne de FPS instantanes.</summary>
    Moyenne,

    /// <summary>Le 1 % low : moyenne des 1 % de frames les plus LONGUES, exprimee en FPS.
    /// C'est ce que le joueur ressent quand ca accroche.</summary>
    UnPourCentLow,

    /// <summary>Le p95 des frametimes : la frame plus lente que 95 % des autres.
    ///
    /// **ce n'est pas « LA FLUIDITE ».** Il decrit une zone MOINS PROFONDE de la distribution
    /// que le p99 ou le 1 % low : les 5 % de frames les plus lentes, pas le pire centile. Un
    /// reglage peut parfaitement ameliorer le p95 et laisser intacts — ou degrader — les a-coups
    /// que le joueur remarque vraiment.
    ///
    /// Mesure du 08/09/2026 (`tools/tailbench --formes`) : sur une deformation qui monte la
    /// moyenne en ajoutant un gros a-coup toutes les 60 frames, le p95 rend **+1,6 %** — un
    /// leger gain — pendant que le 1 % low rend **-65,6 %** et prouve la regression 13 fois sur
    /// 14. Le p95 seul aurait laisse passer le reglage.
    ///
    /// Il est notre meilleure grandeur de queue ROBUSTE sur ce corpus, et rien de plus. Le produit
    /// doit pouvoir dire « regularite p95 amelioree » ET « 1 % low non resolu » dans la meme
    /// phrase, jamais « la fluidite est prouvee ».</summary>
    P95Frametime,
}

/// <summary>
/// Une preuve par grandeur, et la ponderation vient apres.
///
/// Le produit melangeait moyenne et 1 % low en un score composite avant de mesurer, puis
/// mesurait la resolution de ce melange. Resultat en session Warzone reelle (07/09/2026) : le
/// score annoncait +/-72 % alors que la moyenne, seule, resolvait a +/-4,6 %. Une ponderation est
/// une PREFERENCE du joueur ; ce n'est pas une permission de transformer une grandeur inconnue en
/// information.
///
/// Desormais chaque grandeur porte son ecart et sa resolution. Le score n'intervient qu'apres,
/// pour arbitrer entre des preuves qui existent deja.
///
/// ══ ce que le corpus permet de dire — et ce qu'il ne permet pas ══
/// Une premiere version de ce commentaire annoncait une « resolution VRAIE de 2,7 % ». Deux
/// choses y etaient fausses. « VRAIE » : c'est une estimation empirique sur un corpus de huit
/// parties. Et le compte etait malhonnete sans que je l'aie voulu — 92 positions glissantes ne
/// sont pas 92 experiences, deux positions voisines partageant 6 fenetres sur 7.
///
/// Refait le 08/09/2026 avec la CAPTURE comme unite independante, fenetres non chevauchantes,
/// leave-one-capture-out (`tools/tailbench --loco 10 8`) :
///
///   8 holdouts, 15 experiences independantes, **0 faux verdict**, 1 invalide.
///   La procedure choisit le p95 frametime sur 7 holdouts sur 8.
///   Ecart maximal observe sous no-op, par capture : 2,0 % a 15,0 % selon la partie.
///
/// Le p95 frametime est notre meilleure grandeur de queue ROBUSTE **sur ce corpus**, et il est
/// choisi de facon stable sans jamais voir la partie sur laquelle il est evalue.
///
/// La resolution n'est pas une constante du produit : elle varie d'un facteur ~8 d'une partie a
/// l'autre. C'est l'experience qui annonce la sienne, session par session.
///
/// Le protocole de production (24 alternances) n'est pas validable sur ce corpus : il ne laisse
/// que **3 experiences disjointes sur 2 captures**, six holdouts sur huit n'en ayant aucune. Ce
/// qui est valide independamment, c'est 8 alternances de 10 s.
///
/// Trois methodes rejetees par la mesure :
///   * le **p99** frametime est systematiquement SURCONFIANT (annonce 9,2 % pour un ecart p95
///     observe de 15,3 % a 8 alternances) — il fabriquerait des verdicts ;
///   * mettre les frames EN COMMUN par etat au lieu de les agreger par fenetre EMPIRE le 1 % low
///     (110 % puis 165 % quand on ajoute des donnees), parce que le pire centile d'un grand pool
///     ne selectionne plus que quelques a-coups geants ;
///   * le **0,1 % low** comme grandeur de decision : sous un effet reel de +2 % il rend une
///     mediane de -3,2 %, il se trompe de SENS plus souvent qu'il ne voit juste.
/// </summary>
public static class GameMetricSet
{
    /// <summary>Les grandeurs qu'une session mesure et juge, dans l'ordre d'affichage.</summary>
    public static readonly GameMetricKind[] Toutes =
        { GameMetricKind.Moyenne, GameMetricKind.UnPourCentLow, GameMetricKind.P95Frametime };

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
        GameMetricKind.Moyenne => "FPS moyens",
        GameMetricKind.UnPourCentLow => "1 % low",
        GameMetricKind.P95Frametime => "régularité p95",
        _ => "?",
    };

    /// <summary>
    /// La valeur d'une grandeur dans le sens « PLUS GRAND = MIEUX », ou <c>0</c> si elle n'a pas
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
/// verdict. Le trio est indissociable — un ecart sans sa resolution est une promesse.</summary>
public sealed record MetricEvidence(GameMetricKind Metrique, Evidence Preuve)
{
    public string Nom => GameMetricSet.Nom(Metrique);
    public string Libelle => GameMetricSet.Libelle(Metrique);
}
