using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>L'audit de santé à l'écran : les constats triés, les angles morts nommés et ce qui a changé depuis le scan précédent.</summary>
public sealed partial class ScanViewModel
{
    // ── Audit de sante ────────────────────────────────────────────────────────────────────

    /// <summary>Les constats qui meritent le regard du joueur, et eux seuls. Le tri appartient a
    /// <see cref="RepairTriage"/> : lui seul decide, et il est teste.</summary>
    public ObservableCollection<RepairFindingViewModel> RepairFindings { get; } = new();

    /// <summary>True quand l'audit a tourne et a quelque chose a montrer.</summary>
    public bool HasRepairFindings => RepairFindings.Count > 0;

    /// <summary>Ce que le produit n'a pas su lire, nommé un par un, comme `tools/repairaudit` le fait.
    ///
    /// « Non lu » n'est jamais « sain », et un trou de lecture doit se voir. Un compteur dans la phrase
    /// de résumé ne le montre pas : il le résume.</summary>
    public ObservableCollection<RepairFindingViewModel> RepairBlindSpots { get; } = new();

    /// <summary>Une ligne du journal en mots, jamais par les noms d'énumération des statuts
    /// (« ExpectedDefault → UserChoiceProbable »).</summary>
    private static string LigneDeChangement(RepairDelta d)
    {
        var tete = RepairLibelles.Ligne(d.Domaine, d.Nom);
        return d.Quoi switch
        {
            RepairChangement.StatutChange
                when Enum.TryParse<RepairStatus>(d.Avant, out var avant) && Enum.TryParse<RepairStatus>(d.Apres, out var apres)
                => $"{tete}{Docteur.DeuxPoints}{RepairFindingViewModel.LibelleStatut(avant)} → {RepairFindingViewModel.LibelleStatut(apres)}.",
            RepairChangement.Disparu => Docteur.T("App.ConstatDisparu", tete, d.Avant, d.Phrase),
            RepairChangement.Apparu or RepairChangement.FamilleNouvelle => $"{tete} ({d.Apres}){Docteur.DeuxPoints}{d.Phrase}",
            _ => $"{tete}{Docteur.DeuxPoints}{d.Avant} → {d.Apres}. {d.Phrase}",
        };
    }

    private readonly bool? _estEleve;
    private readonly Func<bool>? _relancerEnAdministrateur;

    /// <summary>« aucun intérêt de faire un scan sans puis un scan avec admin, faut tout faire
    /// direct ». Lancée sans droits, l'analyse demande l'élévation une seule fois, au moment de lancer : l'appli se
    /// relance en administrateur avec la scène choisie et enchaîne l'analyse complète. Refusée, l'analyse se fait
    /// quand même, sans les lectures réservées, et le dit. Branché par l'appli, absent des tests.</summary>
    internal Func<int, bool>? RelancerEleveEtAnalyser { get; set; }
    private bool _elevationRefusee;

    /// <summary>Vrai si l'analyse vient d'être confiée à l'instance élevée (celle-ci se ferme).</summary>
    private bool ConfierAUneInstanceElevee()
    {
        if (!PeutSeRelancerEleve || RelancerEleveEtAnalyser is null || _elevationRefusee) return false;
        var scene = Math.Max(0, ChoixDeScene.ToList().IndexOf(SceneDeclaree));
        if (RelancerEleveEtAnalyser(scene)) return true;
        _elevationRefusee = true;
        MessageRelanceAdministrateur = _localize("Scan.Elevation.Refused");
        return false;
    }

    /// <summary>Le bouton « lire aussi ce qui demande l'administrateur » : seulement quand l'appli
    /// tourne sans droits connus, que la relance existe, et qu'il reste des angles morts a lire.</summary>
    public bool PeutRelancerEnAdministrateur =>
        PeutSeRelancerEleve && _relancerEnAdministrateur is not null && RepairBlindSpots.Count > 0;

    /// <summary>Sans droits, et pas depuis un compte standard : là, l'invite Windows demande le mot de passe d'un
    /// administrateur et l'App relancée tourne sous ce compte. Elle lirait ses réglages à lui, et y écrirait ceux du
    /// compte en disant « posé ». L'analyse se fait alors dans cette fenêtre, sans les lectures réservées.</summary>
    private bool PeutSeRelancerEleve => _estEleve == false && !CompteStandard();

    public string LabelRelancerEnAdministrateur => _localize("Scan.Elevation.Button");
    public string LabelRelancerEnAdministrateurAide => _localize("Scan.Elevation.Hint");

    [ObservableProperty]
    private string? _messageRelanceAdministrateur;

    /// <summary>Relance l'appli avec les droits administrateur, pour lire. Si l'accord est refuse,
    /// l'appli reste ouverte et le dit.</summary>
    [RelayCommand]
    private void RelancerEnAdministrateur()
    {
        if (_relancerEnAdministrateur is null || !PeutSeRelancerEleve) return;
        MessageRelanceAdministrateur = _relancerEnAdministrateur() ? null : _localize("Scan.Elevation.Refused");
    }

    /// <summary>True quand au moins un etat n'a pas pu etre lu.</summary>
    public bool HasRepairBlindSpots => RepairBlindSpots.Count > 0;

    /// <summary>ce que le processeur rend a un jeu, en regime soutenu, ou <c>null</c> tant que rien n'a ete mesure (jamais
    /// un zero rassurant). Un banc en rafales mentirait : sur la machine de reference, un cœur seul tient 5 292 MHz et huit
    /// cœurs en charge continue tombent a 3 698, bascule vue a la quinzieme seconde.</summary>
    [ObservableProperty]
    private string? _cpuSustainedLabel;

    /// <summary>d'ou vient l'instabilite du reseau, en une phrase, ou <c>null</c> des qu'un maillon manque (pas de
    /// passerelle lisible, une fenetre sans reponse, un verdict indecidable) : le silence est un resultat, « reseau sain »
    /// n'en est pas un.</summary>
    [ObservableProperty]
    private string? _networkPathLabel;

    /// <summary>ce qu'on peut gagner au plus en liberant du processeur, ou <c>null</c> sans GPU mesure. Un plafond, jamais
    /// une prediction : sur la machine de reference (processeur bride, jeu CPU-bound), « +25 a +30 % » semblait evident,
    /// mais le GPU occupe 86 a 95 % du temps borne le gain a +16,3 % / +5,3 %, sous deux hypotheses optimistes (travail GPU
    /// par image constant et parfaitement empile).</summary>
    [ObservableProperty]
    private string? _gpuCeilingLabel;

    /// <summary>ce que cette machine rend par rapport a sa premiere mesure, ou <c>null</c> sans rien a comparer : une
    /// degradation lente, qu'aucun scan isole ne voit. Un ecart sous la resolution se dit comme une borne, pas comme une
    /// alerte (un produit qui crie tous les mois n'est plus lu).</summary>
    [ObservableProperty]
    private string? _performanceDriftLabel;

    /// <summary>Les reglages poses par DrDXT qui ont bouge depuis, relus au lancement
    /// (<see cref="DrDxt.App.Platform.VerificationDesReglages"/>). <c>null</c> = tout tient, ou rien a relire.</summary>
    [ObservableProperty]
    private string? _reglagesModifies;

    /// <summary>ce que la memoire rend vraiment (debit et latence du banc de 30 a 40 s du premier scan), ou <c>null</c>
    /// tant que rien n'a ete mesure. Seule une mesure acceptee arrive ici (le magasin ne garde pas ce que les controles de
    /// qualite rejettent), et l'affichage relit, il ne relance aucun banc.</summary>
    [ObservableProperty]
    private string? _memoryMeasuredLabel;

    /// <summary>La cadence : ce qu'une moyenne d'images ne dira jamais. <c>null</c> quand la
    /// grandeur n'a pas ete mesuree ou quand le p95 ne merite pas son nom.
    ///
    /// L'erreur d'animation, mesuree sur la vraie partie, est la seule grandeur qui dise qu'une moyenne de 140 FPS est
    /// desagreable a jouer. Aucun seuil « bon » ou « mauvais » : la valeur et ce qu'elle represente, le joueur juge.</summary>
    [ObservableProperty]
    private string? _cadenceLabel;

    /// <summary>La courbe des temps de trame de CETTE mesure, et sa médiane ; null sans trames. Jamais un avant/après :
    /// l'App n'a qu'une passe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ACourbe), nameof(PointsDeLaCourbe), nameof(PicsDeLaCourbe), nameof(LabelPicsEcretes))]
    private CourbeDesTempsDeTrame? _courbeDeLaMesure;

    public bool ACourbe => CourbeDeLaMesure is not null;
    /// <summary>Les pics au-dessus de l'échelle, marqués en haut de la courbe.</summary>
    public IReadOnlyList<System.Windows.Point> PicsDeLaCourbe => CourbeDeLaMesure?.PicsEcretes ?? [];
    public string? LabelPicsEcretes => LignePics(CourbeDeLaMesure, _localize);

    /// <summary>Écrêtés, les pics se disent : leur nombre et le plus haut. Sans pic, rien.</summary>
    internal static string? LignePics(CourbeDesTempsDeTrame? courbe, Func<string, string> localiser)
        => courbe is { PicsEcretes.Count: > 0 } c
            ? Pluriels.Accorder(string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser("Scan.Courbe.Pics"),
                c.PicsEcretes.Count, c.MaxMs.ToString("0", System.Globalization.CultureInfo.CurrentCulture)))
            : null;
    public System.Windows.Media.PointCollection? PointsDeLaCourbe => CourbeDeLaMesure is { } c ? new(c.Points) : null;
    public string LabelCourbe => _localize("Scan.Courbe.Titre");
    public string LabelCourbeLegende => _localize("Scan.Courbe.Legende");

    /// <summary>La saccade : les 5 % de trames les plus lentes, en millisecondes. <c>null</c> sans
    /// mesure exploitable.
    ///
    /// A cote du 1 % low, qui resout mal (±132 % en session live, jusqu'a 43,7 % d'ecart sous no-op), le p95 des
    /// frametimes s'assoit sur 5 % des trames et ne depend pas d'une poignee d'a-coups. Il ajoute sans remplacer : sur un
    /// a-coup toutes les 60 trames, le p95 rend +1,6 % quand le 1 % low prouve −65,6 %.</summary>
    [ObservableProperty]
    private string? _stutterLabel;

    /// <summary>L'attente d'affichage : le temps passe dans `Present()` une fois la trame prete.
    /// <c>null</c> sans mesure exploitable.
    ///
    /// La seule grandeur sur laquelle le joueur agit seul (cap d'images, vsync, compositeur, dans les reglages du jeu).
    /// Les trois causes sont nommees, aucune n'est designee : un cap volontaire est un choix, pas un defaut.</summary>
    [ObservableProperty]
    private string? _presentStallLabel;

    /// <summary>Ce qui a changé depuis la dernière fois : le pilier « maintenir », côté écran.
    ///
    /// Un scan isolé ne voit jamais une dégradation lente ; seule la comparaison avec la photo
    /// précédente la montre.
    ///
    /// Les valeurs mesurees qui bougent n'y figurent pas : frequence, debit, temperature varient
    /// par nature, et un diff qui trouve toujours quelque chose est un diff qu'on cesse de lire.</summary>
    public ObservableCollection<string> RepairChanges { get; } = new();

    /// <summary>True quand quelque chose a change depuis la derniere photo, ou quand il y a un
    /// avertissement a lire.
    ///
    /// Un avertissement suffit à montrer la section : « historique illisible » et « photos prises
    /// dans deux états différents » se calculent sans qu'aucun delta ne sorte.</summary>
    public bool HasRepairChanges => RepairChanges.Count > 0 || RepairChangesWarning is not null;

    /// <summary>Ce qu'il faut lire avant les changements : les deux photos n'ont pas ete prises
    /// dans le meme etat de machine, ou l'historique n'a pas pu etre lu. <c>null</c> quand elles
    /// sont comparables.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepairChanges))]
    private string? _repairChangesWarning;

    /// <summary>En-tete localise de la section des changements.</summary>
    public string LabelRepairChanges => _localize("Scan.Repair.Changes");

    /// <summary>En-tete localise des angles morts.</summary>
    public string LabelRepairBlindSpots => _localize("Scan.Repair.BlindSpots");

    /// <summary>En-tete localise de la section d'audit.</summary>
    public string LabelRepairHeader => _localize("Scan.Repair.Header");

    /// <summary>La phrase de l'audit : les trois nombres ensemble (a regarder / verifies / non
    /// lus). Vide tant que l'audit n'a pas tourne.
    ///
    /// Elle s'affiche meme quand <see cref="HasRepairFindings"/> est faux : « rien ne demande
    /// ton attention » est un resultat, et taire le nombre d'etats non lus ferait passer un audit
    /// partiel pour un audit complet.</summary>
    [ObservableProperty]
    private string? _repairSummary;
}
