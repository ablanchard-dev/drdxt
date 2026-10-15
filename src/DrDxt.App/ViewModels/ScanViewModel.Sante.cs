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
        if (_estEleve != false || RelancerEleveEtAnalyser is null || _elevationRefusee) return false;
        var scene = Math.Max(0, ChoixDeScene.ToList().IndexOf(SceneDeclaree));
        if (RelancerEleveEtAnalyser(scene)) return true;
        _elevationRefusee = true;
        MessageRelanceAdministrateur = _localize("Scan.Elevation.Refused");
        return false;
    }

    /// <summary>Le bouton « lire aussi ce qui demande l'administrateur » : seulement quand l'appli
    /// tourne sans droits connus, que la relance existe, et qu'il reste des angles morts a lire.</summary>
    public bool PeutRelancerEnAdministrateur =>
        _estEleve == false && _relancerEnAdministrateur is not null && RepairBlindSpots.Count > 0;

    public string LabelRelancerEnAdministrateur => _localize("Scan.Elevation.Button");
    public string LabelRelancerEnAdministrateurAide => _localize("Scan.Elevation.Hint");

    [ObservableProperty]
    private string? _messageRelanceAdministrateur;

    /// <summary>Relance l'appli avec les droits administrateur, pour lire. Si l'accord est refuse,
    /// l'appli reste ouverte et le dit.</summary>
    [RelayCommand]
    private void RelancerEnAdministrateur()
    {
        if (_relancerEnAdministrateur is null) return;
        MessageRelanceAdministrateur = _relancerEnAdministrateur() ? null : _localize("Scan.Elevation.Refused");
    }

    /// <summary>True quand au moins un etat n'a pas pu etre lu.</summary>
    public bool HasRepairBlindSpots => RepairBlindSpots.Count > 0;

    /// <summary>ce que le processeur rend a un jeu, en une phrase, ou <c>null</c> tant que rien
    /// n'a ete mesure.
    ///
    /// Un banc en rafales rend le regime que le joueur ne connait jamais : sur la machine de
    /// reference, un cœur seul tient 5 292 MHz et huit cœurs sous charge continue tombent a 3 698,
    /// la bascule se voyant a la quinzieme seconde. Cette ligne dit le regime soutenu, celui d'un
    /// jeu qui tourne des heures.
    ///
    /// Elle ne dit rien quand la mesure n'a pas pu se faire. « Pas mesure » ne se remplace pas
    /// par un zero rassurant.</summary>
    [ObservableProperty]
    private string? _cpuSustainedLabel;

    /// <summary>d'ou vient l'instabilite du reseau, en une phrase, ou <c>null</c> quand rien n'a
    /// pu etre attribue.
    ///
    /// Le produit lisait la carte et ne mesurait rien : l'instrument de latence et l'analyseur
    /// de chemin existaient, testes, appeles par un seul outil en ligne de commande. C'est le
    /// domaine qu'un joueur competitif ressent en premier.
    ///
    /// Elle disparait des qu'un maillon manque (pas de passerelle lisible, une fenetre sans
    /// reponse, un verdict indecidable). Le silence est un resultat ; « reseau sain » n'en est
    /// pas un.</summary>
    [ObservableProperty]
    private string? _networkPathLabel;

    /// <summary>ce qu'on peut gagner au plus en liberant du processeur, ou <c>null</c> quand le
    /// scan n'a mesure aucun GPU.
    ///
    /// La phrase qui evite le plus de temps perdu. Sur la machine de reference, le processeur
    /// est bride en regime soutenu et le jeu est CPU-bound : la conclusion evidente etait « lever
    /// la limite rend +25 a +30 % d'images ». Le meme jeu de mesures l'interdit : le GPU est
    /// occupe 86 a 95 % du temps, donc le plafond reel va de **+16,3 % a +5,3 %**.
    ///
    /// C'est un plafond, jamais une prediction : « pas plus que X », et le gain reel sera plus
    /// petit. La borne suppose que le travail GPU par image ne change pas et qu'il s'empile
    /// parfaitement, deux hypotheses optimistes.</summary>
    [ObservableProperty]
    private string? _gpuCeilingLabel;

    /// <summary>ce que cette machine rend par rapport a sa premiere mesure, ou <c>null</c> quand
    /// il n'y a rien a comparer.
    ///
    /// Le juge existait, teste, et n'etait appele que par des tests : le produit ecrivait un
    /// journal de performance que personne ne relisait. Une degradation lente est precisement ce
    /// qu'aucun scan isole ne voit : prise seule, chaque mesure parait normale.
    ///
    /// Un ecart sous la resolution n'est pas une alerte : il se dit comme une borne. Un produit
    /// qui crie a la degradation tous les mois n'est plus lu.</summary>
    [ObservableProperty]
    private string? _performanceDriftLabel;

    /// <summary>Les reglages poses par DrDXT qui ont bouge depuis, relus au lancement
    /// (<see cref="DrDxt.App.Platform.VerificationDesReglages"/>). <c>null</c> = tout tient, ou rien a relire.</summary>
    [ObservableProperty]
    private string? _reglagesModifies;

    /// <summary>ce que la memoire de cette machine rend vraiment, ou <c>null</c> tant que rien
    /// n'a ete mesure.
    ///
    /// Le scan payait trente a quarante secondes de banc au premier lancement, enregistrait le
    /// resultat, et aucune couche visible ne lisait le debit ni la latence : ils ne servaient qu'a
    /// deduire un nombre de canaux. Les deux chiffres qu'un joueur reconnaitrait etaient mesures
    /// puis jetes.
    ///
    /// Seule une mesure acceptee peut arriver ici : le magasin ne conserve pas ce que les
    /// controles de qualite ont rejete. Et l'affichage ne declenche aucun banc : il relit.</summary>
    [ObservableProperty]
    private string? _memoryMeasuredLabel;

    /// <summary>La cadence : ce qu'une moyenne d'images ne dira jamais. <c>null</c> quand la
    /// grandeur n'a pas ete mesuree ou quand le p95 ne merite pas son nom.
    ///
    /// Le depot ecrit lui-meme que l'erreur d'animation est **la seule grandeur qui sache dire
    /// qu'une moyenne de 140 FPS est desagreable a jouer**. Elle est mesuree a chaque scan, sur la
    /// vraie partie du joueur, et elle ne sortait nulle part : seul le classifieur de goulot la
    /// lisait, pour en tirer un etat. Le chiffre, lui, n'atteignait personne.
    ///
    /// Aucun seuil : ni « bon », ni « mauvais ». Le produit dit la valeur et ce qu'elle
    /// represente ; decider ce qui est acceptable appartient au joueur.</summary>
    [ObservableProperty]
    private string? _cadenceLabel;

    /// <summary>La saccade : les 5 % de trames les plus lentes, en millisecondes. <c>null</c> sans
    /// mesure exploitable.
    ///
    /// Renversement mesure dans ce depot : l'ecran montre le **1 % low**, dont il est ecrit
    /// qu'il resout mal (±132 % en session live, jusqu'a 43,7 % d'ecart sous no-op), et il cachait
    /// le **p95 des frametimes** : meme famille, mais assis sur 5 % des trames au lieu de 1 %,
    /// donc pas dicte par une poignee d'a-coups. Il etait mesure a chaque scan et lu par personne.
    ///
    /// Les deux restent necessaires : sur un a-coup toutes les 60 trames, le p95 rend +1,6 %
    /// quand le 1 % low prouve −65,6 %. Cette ligne ajoute, elle ne remplace pas.</summary>
    [ObservableProperty]
    private string? _stutterLabel;

    /// <summary>L'attente d'affichage : le temps passe dans `Present()` une fois la trame prete.
    /// <c>null</c> sans mesure exploitable.
    ///
    /// C'est la seule des grandeurs mesurees sur laquelle un joueur peut agir seul : un cap
    /// d'images, un vsync ou le compositeur qui retient la trame se changent dans les reglages du
    /// jeu. Elle etait mesuree a chaque scan et n'atteignait que le classifieur de goulot.
    ///
    /// Le produit nomme les trois causes possibles et n'en designe aucune : un cap volontaire
    /// est un choix, pas un defaut.</summary>
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
