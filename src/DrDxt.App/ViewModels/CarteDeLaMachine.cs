using DrDxt.Core;
using DrDxt.Profiles;

namespace DrDxt.App.ViewModels;

/// <summary>L'état d'un domaine, tel que la carte peut l'affirmer à partir de ce qui a été lu.</summary>
public enum EtatDomaine
{
    /// <summary>Au moins un constat mineur ou majeur.</summary>
    ACorriger,

    /// <summary>Des informations ou des recommandations, rien de mal réglé.</summary>
    ARegarder,

    /// <summary>Lu ou mesuré, et rien n'est ressorti. Ce n'est pas « sain » : c'est ce qui a été vu.</summary>
    RienDeSignale,

    /// <summary>Rien n'a pu être lu ni mesuré. Jamais confondu avec « rien de signalé ».</summary>
    NonLu,
}

/// <summary>
/// Une ligne de la carte : un constat, une recommandation ou un angle mort, déjà traduit.
/// <paramref name="Propose"/> : un réglage que DrDXT propose, pas un état lu sur la machine.
/// </summary>
public sealed record EntreeCarte(
    string Domaine,
    Severity Gravite,
    string Titre,
    string Detail,
    string Valeur,
    string Action,
    bool NonLu,
    bool Propose = false)
{
    /// <summary>29/09/2026 : la première phrase du détail reste à l'écran ; la suite s'ouvre sous « Pourquoi ? »
    /// (un constat mémoire faisait dix lignes d'un bloc).</summary>
    public string DetailCourt => CarteDeLaMachine.TitreEtSuite(Detail).Titre;
    public string DetailSuite => CarteDeLaMachine.TitreEtSuite(SansRemede).Suite;
    public bool ADetailSuite => DetailSuite.Length > 0;

    /// <summary>29/09/2026 (B18) : la Carte graphique était « à corriger » sans aucun geste visible ; le remède était écrit,
    /// mais derrière « Pourquoi ? ». Il se lit maintenant sans clic.</summary>
    // Simplification assumee : repere par les debuts de phrase que les textes de remede emploient deja ; un nouveau texte de remede
    // qui commence autrement reste derriere « Pourquoi ? ». Un champ Remede dans les constats si la liste s'allonge.
    private static readonly string[] DebutsDeRemede =
        ["Pour le retirer", "Pour le corriger", "Pour la corriger", "Réinstaller le logiciel", "Déplacer le programme", "Le fichier est déjà sous Program Files"];
    private int DebutDuRemede
    {
        get
        {
            var premiere = CarteDeLaMachine.TitreEtSuite(Detail).Titre.Length;
            return DebutsDeRemede.Select(d => Detail.IndexOf(d, premiere, StringComparison.Ordinal))
                .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        }
    }
    public string Remede => DebutDuRemede is var i and >= 0 ? Detail[i..].Trim() : string.Empty;
    public bool ARemede => Remede.Length > 0;

    /// <summary>29/09/2026 (B21) : les commandes icacls du remède, une par ligne, pour le bouton « Copier ». DrDXT
    /// n'exécute rien sur un logiciel tiers : il copie, le joueur colle dans sa console administrateur.</summary>
    public string CommandeACopier => string.Join("\r\n",
        System.Text.RegularExpressions.Regex.Matches(Remede, "icacls \"[^\"]+\" /(?:setowner \\S+|reset)").Select(m => m.Value));
    public bool ACommande => CommandeACopier.Length > 0;

    /// <summary>03/10/2026 (D10.5, revue n° 4) : deux commandes icacls complètes noyées dans la prose faisaient quatre
    /// lignes illisibles. La prose les annonce ; elles s'affichent dessous en police de code, une par ligne, et se copient.</summary>
    public string RemedeLisible => !ACommande ? Remede : System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(Remede, @"icacls ""[^""]+"" /setowner \S+ puis icacls ""[^""]+"" /reset", "les deux commandes ci-dessous"),
        @"icacls ""[^""]+"" /(?:setowner \S+|reset)", "la commande ci-dessous");
    private string SansRemede => DebutDuRemede is var i and >= 0 ? Detail[..i].TrimEnd() : Detail;

    /// <summary>29/09/2026 : sur un réglage proposé, <see cref="Valeur"/> porte la réserve « pas encore validé »,
    /// pas une lecture. Dans la pastille en police de code des valeurs lues, elle passait pour un chiffre mesuré.</summary>
    public string ValeurLue => Propose ? string.Empty : Valeur;

    /// <summary>08/10/2026 (critique UI, pastille mono) : la pastille en police de code recopiait la phrase juste au-dessus
    /// (« Version du pilote graphique » puis la même version). Elle ne s'affiche que si elle apprend quelque chose.</summary>
    public string ValeurAAfficher => ValeurLue.Length == 0 || DejaDit(ValeurLue) ? string.Empty : ValeurLue;

    /// <summary>08/10/2026 (critique UI) : l'étiquette « recommandation » sur un point rouge (à corriger) se contredisait.
    /// Sur un constat à corriger, elle se tait ; les autres étiquettes d'action restent.</summary>
    public string ActionAAfficher => Gravite > Severity.Info && string.Equals(Action, "recommandation", StringComparison.Ordinal)
        ? string.Empty : Action;

    private bool DejaDit(string valeur)
    {
        static string Plat(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var v = Plat(valeur);
        // Vu sur capture (08/10/2026) : comparé au texte entier, un coût dit seulement derrière le « ? » cachait la pastille ;
        // seul ce qui est affiché compte (titre et première phrase).
        var dessus = Titre + " " + DetailCourt;
        // 08/10/2026 (revue) : une valeur courte (« 60 », « on ») se retrouvait dans n'importe quel mot ou nombre du dessus.
        if (v.Length >= 4 && Plat(dessus).Contains(v, StringComparison.Ordinal)) return true;
        // Vu sur capture le 08/10 : « Ton pilote NVIDIA 617.14 … la dernière est la 617.42 » puis la pastille
        // « 617.14 installé → 617.42 disponible ». Une valeur dont tous les nombres sont déjà dans la phrase n'apprend rien.
        // Nombres entiers comparés entiers : « 60 Hz » n'est pas dit par « 1600 ».
        static List<string> Nombres(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\d+(?:[.,]\d+)*").Select(m => m.Value).ToList();
        var nombres = Nombres(valeur);
        var ditsDessus = Nombres(dessus).ToHashSet(StringComparer.Ordinal);
        return nombres.Count > 0 && nombres.All(ditsDessus.Contains);
    }
    public string ReserveDeValidation => Propose ? Valeur : string.Empty;
}

/// <summary>Un point de la liste « À faire d'abord » : le constat et le domaine qu'un clic ouvre.</summary>
public sealed record PointAFaire(DomaineMachine Domaine, EntreeCarte Entree);

/// <summary>Une ligne mesurée, et l'explication qui l'accompagne quand il y en a une.</summary>
public sealed record LigneMesure(string Texte, string? Explication, bool EstTitre = false, bool EstEntete = false)
{
    /// <summary>29/09/2026 : comme un constat, la première phrase reste à l'écran et la suite s'ouvre sous
    /// « Pourquoi ? » (la mesure mémoire et celle du réseau faisaient quatre lignes).</summary>
    public string TexteCourt => CarteDeLaMachine.GloserLeJargon(CarteDeLaMachine.TitreEtSuite(Texte).Titre);
    /// <summary>29/09/2026 : l'explication sourcée d'une ligne lue n'était qu'en info-bulle, que personne ne découvre
    /// (vu sur la page Latence : « Mode DLSS du jeu : Balanced », sans ce que le mode Performance retire). Elle
    /// s'ouvre sous « Pourquoi ? », après la suite du texte s'il y en a une.</summary>
    public string Suite => string.Join(" ", new[] { CarteDeLaMachine.TitreEtSuite(Texte).Suite, Explication ?? "" }
        .Where(s => s.Length > 0));
    public bool ASuite => Suite.Length > 0;
}

/// <summary>Un domaine de la machine et tout ce que le scan en sait.</summary>
public sealed record DomaineMachine(
    string Cle,
    string Nom,
    string IconKey,
    EtatDomaine Etat,
    string EtatLibelle,
    string Composant,
    IReadOnlyList<string> Mesures,
    IReadOnlyList<EntreeCarte> Lignes,
    IReadOnlyList<EntreeCarte> NonLus,
    IReadOnlyList<EntreeCarte> Proposes)
{
    public bool AUnComposant => !string.IsNullOrWhiteSpace(Composant);
    public bool ADesMesures => Mesures.Count > 0;

    /// <summary>03/10/2026 (D4.6) : ce que la constellation de l'en-tête dessine. Une lecture par mesure ou état lu (ni
    /// l'en-tête « N états lus », ni les sous-titres de famille) et par constat ; les constats sont les points vifs.</summary>
    public int NombreDeLectures => Mesures.Count(m => m != Entete && Titres?.Contains(m) != true) + Lignes.Count;
    public int NombreDeConstats => Lignes.Count;

    /// <summary>24/09/2026 : l'explication sourcée d'un état lu sans geste, par texte de ligne. Sans
    /// elle, l'écran ne montrait que la valeur et la phrase n'était lue nulle part.</summary>
    public IReadOnlyDictionary<string, string>? Explications { get; init; }

    /// <summary>25/09/2026 : les sous-titres de famille glissés dans la liste des états lus (vu sur
    /// photo : 20 lignes à plat sur la page Windows, services, mises à jour et périphériques mêlés).</summary>
    public IReadOnlySet<string>? Titres { get; init; }

    /// <summary>27/09/2026 : « N états lus par l'audit » annonce toute la liste. Ni donnée (pas de puce),
    /// ni titre de famille (même rendu, la hiérarchie devenait plate sur la page Processeur).</summary>
    public string? Entete { get; init; }

    /// <summary>29/09/2026 (B9) : la phrase d'ouverture du domaine, qui dit quoi faire.</summary>
    public string Verdict { get; init; } = "";

    /// <summary>Ce que la vue affiche : la ligne, et son explication en info-bulle quand il y en a une.</summary>
    public IReadOnlyList<LigneMesure> MesuresAffichees =>
        Mesures.Select(m => new LigneMesure(m, Explications?.GetValueOrDefault(m) ?? (Cle == "bios" ? CarteDeLaMachine.ExplicationBios(m) : null),Titres?.Contains(m) == true, m == Entete)).ToList();

    /// <summary>29/09/2026 : au-delà de huit lignes (le BIOS en affichait dix-neuf), six restent à l'écran et le
    /// reste se déplie ; jusqu'à huit, tout se lit d'un coup.</summary>
    private const int LignesAvantRepli = 8, LignesGardees = 6;
    public IReadOnlyList<LigneMesure> MesuresVisibles =>
        MesuresAffichees.Count > LignesAvantRepli ? MesuresAffichees.Take(LignesGardees).ToList() : MesuresAffichees;
    public IReadOnlyList<LigneMesure> MesuresRepliees =>
        MesuresAffichees.Count > LignesAvantRepli ? MesuresAffichees.Skip(LignesGardees).ToList() : [];
    public bool ADesMesuresRepliees => MesuresRepliees.Count > 0;

    /// <summary>29/09/2026 : le nombre annoncé sur « Voir les autres lignes ». Un sous-titre de famille n'est pas
    /// un état lu : le compter donnait « 14 états lus », trois affichés, « 13 autres » (vu sur la page Processeur).</summary>
    public int NombreDeLignesRepliees => MesuresRepliees.Count(m => !m.EstTitre && !m.EstEntete);

    /// <summary>29/09/2026 (B13) : la page Windows empilait douze constats. Les lignes arrivent triées par gravité :
    /// au-delà de quatre, le reste se déplie. UI1 (08/10/2026) : jamais un constat à corriger ; seul ce qui est pour
    /// information se replie (les trois plus graves restent au moins).</summary>
    internal const int ConstatsAvantRepli = 4, ConstatsGardes = 3;
    private int NombreVisibles => Lignes.Count > ConstatsAvantRepli
        ? Math.Max(ConstatsGardes, Lignes.Count(l => l.Gravite > Severity.Info)) : Lignes.Count;
    public IReadOnlyList<EntreeCarte> LignesVisibles => Lignes.Take(NombreVisibles).ToList();
    public IReadOnlyList<EntreeCarte> LignesRepliees => Lignes.Skip(NombreVisibles).ToList();
    public bool ADesLignesRepliees => LignesRepliees.Count > 0;

    public bool ADesLignes => Lignes.Count > 0;
    public bool ADesNonLus => NonLus.Count > 0;
    public bool ADesProposes => Proposes.Count > 0;
    public bool EstVide => !ADesMesures && !ADesLignes && !ADesNonLus && !ADesProposes;

    /// <summary>ROAD1 (08/10/2026) : ce que DrDXT fait sur cette page, levier par levier.</summary>
    public IReadOnlyList<LigneLevier> Leviers { get; init; } = [];
    public bool ADesLeviers => Leviers.Count > 0;
}

/// <summary>
/// La carte de la machine : ranger, jamais juger.
///
/// L'écran de résultat empilait quatre listes (constats du scanner, recommandations, audit de
/// santé, BIOS) et une dizaine de phrases de mesure, chacune dans son ordre de fabrication. Un
/// joueur qui cherche « et ma mémoire ? » devait tout lire. La carte range ces mêmes lignes par
/// domaine, sans en ajouter une : l'état d'un domaine se déduit des gravités déjà calculées, et un
/// domaine où rien n'a été lu le dit au lieu de passer pour sain.
///
/// Pur, aucune dépendance à WPF : l'ordre, les règles d'état et le rangement se testent seuls.
/// </summary>
/// <summary>ROAD1 : un levier sur sa page. <see cref="Raison"/> seulement quand il n'est pas pour ce matériel.</summary>
public sealed record LigneLevier(string Nom, string Statut, string? Raison, DrDxt.Application.Tweaks.StatutDuLevier Code)
{
    public bool ARaison => Raison is not null;
}

public static class CarteDeLaMachine
{
    // SEC3 (08/10/2026) : les identifiants du service (manifeste) vers le nom du réglage à l'écran.
    private static readonly IReadOnlyDictionary<string, string> NomsDuService = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["os.hags"] = "tweak.latency.hagsOn.title",
        ["latency.renderQueueCap"] = "tweak.latency.render_queue_cap.title",
        ["latency.vrrVsyncCap"] = "tweak.latency.vrr_vsync_cap.title",
        ["fixit.sanePowerPlan"] = "tweak.fixit.power_plan.title",
        ["fixit.x3dVCacheService"] = "tweak.fixit.x3d_ccd.title",
        ["latency.reflex.lowLatencyMode"] = "tweak.latency.reflex.title",
    };

    /// <summary>SEC3 (08/10/2026) : ce que le service a dû réaffirmer à son dernier passage (Windows l'avait remis, au
    /// démarrage ou après une mise à jour), réglage par réglage, avec la date. Rien de lu = rien de dit.</summary>
    public static IReadOnlyList<string> LignesDuService(DrDxt.Reconciler.ReconcilerState? etat, Func<string, string> localiser)
        => (etat?.Items ?? [])
            .Where(i => string.Equals(i.LastResultCode, "Reapplied", StringComparison.Ordinal))
            .Select(i => string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser("Service.Reaffirme"),
                NomsDuService.TryGetValue(i.TweakId, out var cle) ? localiser(cle) : i.TweakId,
                i.LastReconciledAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)))
            .ToList();

    /// <summary>ROAD1 (08/10/2026) : les leviers rangés par page, nommés, avec leur statut et leur raison.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<LigneLevier>> LeviersParPage(
        IEnumerable<DrDxt.Application.Tweaks.LevierSurCeMateriel> leviers, Func<string, string> localiser)
        => leviers.GroupBy(l => DrDxt.Application.Tweaks.CouvertureDesLeviers.Domaine(l.Id))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LigneLevier>)g
                .OrderBy(l => l.Statut)
                .Select(l => new LigneLevier(localiser(TweakDisplayKeys.TitleKey(l.Id)),
                    localiser($"Levier.Statut.{l.Statut}") + (l.VerifieIci ? " · " + localiser("Levier.VerifieIci") : ""),
                    l.RaisonCle is { } r ? localiser(r) : null, l.Statut))
                .ToList());

    /// <summary>L'ordre d'affichage : du plus lié aux images au plus lointain.</summary>
    public static readonly IReadOnlyList<string> Ordre =
        ["processeur", "graphique", "memoire", "latence", "reseau", "stockage", "windows", "bios"];

    private static readonly IReadOnlyDictionary<string, string> Icones = new Dictionary<string, string>
    {
        ["processeur"] = "Cpu",
        ["graphique"] = "Gpu",
        ["memoire"] = "Ram",
        ["latence"] = "Latency",
        ["reseau"] = "Network",
        ["stockage"] = "Ssd",
        ["windows"] = "Windows",
        ["bios"] = "Firmware",
    };

    /// <summary>29/09/2026 : sous le verdict, ce qui est à corriger, le plus grave d'abord puis dans l'ordre de
    /// la carte, cinq au plus. Les angles morts et les réglages proposés restent dans leur domaine : ils ne
    /// sont pas des défauts lus.</summary>
    public static IReadOnlyList<PointAFaire> AFaire(IEnumerable<DomaineMachine> domaines, int max = 5) =>
        Regrouper(domaines
            .SelectMany(d => d.Lignes.Where(l => l.Gravite != Severity.Info && !l.NonLu && !l.Propose).Select(l => new PointAFaire(d, l)))
            .OrderByDescending(p => p.Entree.Gravite)
            .ThenBy(p => Ordre.ToList().IndexOf(p.Domaine.Cle)))
            .Take(max)
            .ToList();

    /// <summary>03/10/2026 (boucle 2D2P, D5.6, revue) : trois des cinq points « à faire d'abord » disaient la même
    /// chose (« service X : son fichier peut être remplacé par un autre programme »). Dans un même domaine, les constats
    /// qui partagent la phrase après « : » deviennent une ligne qui les nomme tous ; le domaine garde chacun avec son
    /// remède. La place libérée laisse monter les points suivants.</summary>
    internal static IEnumerable<PointAFaire> Regrouper(IEnumerable<PointAFaire> points)
    {
        static string? Suite(string titre) => titre.IndexOf(" : ", StringComparison.Ordinal) is var i and > 0 ? titre[(i + 3)..] : null;
        var liste = points.ToList();
        var vus = new HashSet<(string, string)>();
        foreach (var p in liste)
        {
            var suite = Suite(p.Entree.Titre);
            if (suite is null) { yield return p; continue; }
            var cle = (p.Domaine.Cle, suite);
            if (!vus.Add(cle)) continue;
            var memes = liste.Where(q => q.Domaine.Cle == p.Domaine.Cle && Suite(q.Entree.Titre) == suite).ToList();
            if (memes.Count == 1) { yield return p; continue; }
            var noms = string.Join(", ", memes.Select(q => q.Entree.Titre[..q.Entree.Titre.IndexOf(" : ", StringComparison.Ordinal)]));
            // 03/10/2026 (D8.3, revue n° 3) : « Son fichier… (3 : …) » ne disait pas de qui ; les noms d'abord, au pluriel.
            var pluriel = suite.StartsWith("son ", StringComparison.Ordinal) || suite.StartsWith("sa ", StringComparison.Ordinal)
                ? "leur " + suite[(suite.IndexOf(' ') + 1)..] : suite;
            var titre = char.ToUpper(noms[0], System.Globalization.CultureInfo.CurrentCulture) + noms[1..] + " : " + pluriel;
            yield return p with { Entree = p.Entree with { Titre = titre } };
        }
    }

    /// <summary>Le domaine d'un constat du scanner, par sa catégorie.</summary>
    public static string DomaineDuConstat(string categorie) => categorie switch
    {
        "Scheduling" or "Cpu" or "Power" => "processeur",
        "Gpu" or "Display" => "graphique",
        "Memory" => "memoire",
        "Latency" => "latence",
        "Network" => "reseau",
        "Storage" => "stockage",
        // Simplification voulue : une catégorie nouvelle tombe dans Windows plutôt que de disparaître ; le test
        // de rangement la fera voir quand elle apparaîtra.
        _ => "windows",
    };

    /// <summary>24/09/2026 : un constat du moteur n'a qu'un texte. Affiché en entier comme titre, il
    /// faisait neuf lignes en gras (vu sur la carte graphique). La première phrase devient le titre,
    /// le reste l'explication ; un texte d'une phrase reste un titre seul.</summary>
    public static (string Titre, string Suite) TitreEtSuite(string texte)
    {
        var fin = texte.IndexOf(". ", StringComparison.Ordinal);
        return fin < 0 ? (texte, string.Empty) : (texte[..(fin + 1)], texte[(fin + 2)..].TrimStart());
    }

    /// <summary>Le lien PCIe de la carte graphique tel que NVML le rapporte.</summary>
    public sealed record LienPcie(uint Gen, uint Largeur, uint GenMax, uint LargeurMax);

    /// <summary>Une largeur sous le maximum de la carte est un vrai bridage (slot, rallonge) ; une génération
    /// plus basse au repos est l'économie d'énergie normale d'une carte NVIDIA, pas un défaut.</summary>
    public static string LignePcie(LienPcie l)
        => $"Lien PCIe de la carte graphique : Gen{l.Gen} x{l.Largeur} (maximum de la carte : Gen{l.GenMax} x{l.LargeurMax})"
           + (l.Largeur < l.LargeurMax
               ? " : moins de lignes que la carte n'en a, vérifie le slot et une éventuelle rallonge"
               : l.Gen < l.GenMax ? " : génération plus basse au repos, c'est l'économie d'énergie de la carte" : string.Empty);

    /// <summary>Les réglages lus dans la NVRAM du BIOS, une ligne chacun, avec le libellé que le menu
    /// du BIOS affiche (on le retrouve tel quel en y entrant). Rien de lu : aucune ligne.</summary>
    public static IReadOnlyList<string> LignesBios(IReadOnlyList<BiosSettingReading>? lus, int mtsWindows = 0) =>
        lus is null ? [] : lus.Select(r => $"{r.Setting}{Glose(r.Setting)} : {r.Libelle}{(r.IsDefault ? " (réglage d'usine)" : " (modifié)")}"
                                           + DiscordanceMemoire(r, mtsWindows)).ToList();

    /// <summary>29/09/2026 (B12, critique B10 P1) : ce que le réglage fait, en quelques mots, après son nom exact (qui
    /// reste celui du menu du BIOS). Un nom inconnu reste nu : pas de glose devinée. L'ordre compte : le premier
    /// fragment trouvé l'emporte.</summary>
    private static readonly (string Fragment, string Glose)[] Gloses =
    [
        ("X.M.P", "profil de vitesse de la RAM"), ("XMP", "profil de vitesse de la RAM"), ("EXPO", "profil de vitesse de la RAM"),
        ("Re-Size BAR", "la carte graphique voit toute sa mémoire d'un coup"), ("Resizable BAR", "la carte graphique voit toute sa mémoire d'un coup"),
        ("Above 4G", "prérequis de Re-Size BAR"),
        ("CSM", "démarrage compatible anciens systèmes"),
        ("C-State", "mise en veille des cœurs au repos"), ("C State", "mise en veille des cœurs au repos"),
        // Fragment en minuscules : la comparaison ignore la casse, et un sigle en capitales dans une chaîne en dur est refusé.
        ("eist", "baisse de fréquence au repos"),
        ("DRAM Frequency", "vitesse de la RAM"),
        ("Turbo Boost", "montée en fréquence automatique du processeur"),
        ("CPU Cooler Tuning", "limites de puissance du processeur"),
        ("(PL1)", "puissance tenue en charge longue"), ("(PL2)", "puissance permise en pointe courte"),
        ("Fast Boot", "démarrage raccourci"),
    ];

    private static string Glose(string reglage) =>
        Gloses.FirstOrDefault(g => reglage.Contains(g.Fragment, StringComparison.OrdinalIgnoreCase)) is { Glose: { } glose }
            ? $" ({glose})" : "";

    internal static IEnumerable<string> FragmentsGloses => Gloses.Select(g => g.Fragment);

    /// <summary>29/09/2026 (B23, critique B10 P1) : le jargon des lignes mesurées, reformulé au premier regard, une fois
    /// par ligne (la première occurrence), comme les réglages BIOS. L'explication complète reste sous « Pourquoi ? ».</summary>
    private static readonly (string Terme, string Glose)[] Jargon =
    [
        ("gigue", "variation du délai"),
        ("premier saut", "ta box"),
        ("modération d'interruption", "regroupement des signaux de la carte réseau"),
        ("BUGCODE_USB3_DRIVER", "plantage du pilote USB 3"),
    ];

    public static string GloserLeJargon(string texte)
    {
        foreach (var (terme, glose) in Jargon)
        {
            var i = texte.IndexOf(terme, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var fin = i + terme.Length;
            // 03/10/2026 (D12.1, revue n° 6) : « premier saut (ta box) (192.168.1.254) » ; quand le terme a déjà sa
            // parenthèse, la glose y entre : « premier saut (ta box, 192.168.1.254) ».
            texte = texte[fin..].StartsWith(" (", StringComparison.Ordinal)
                ? texte.Insert(fin + 2, $"{glose}, ")
                : texte.Insert(fin, $" ({glose})");
        }
        return texte;
    }

    /// <summary>29/09/2026 (B20, critique B10 P1) : le BIOS était le seul domaine sans « Pourquoi ? ». Ce que le réglage
    /// change, par fragment de nom comme la glose ; un nom inconnu n'a pas d'explication (rien de deviné).</summary>
    private static readonly (string[] Fragments, string Explication)[] ExplicationsBios =
    [
        (["X.M.P", "XMP", "EXPO"], "Sans profil, la RAM tourne à la vitesse standard JEDEC, souvent sous celle écrite sur le kit. "
            + "Le profil applique la vitesse et les délais testés par le fabricant du kit : c'est un overclock validé par lui, pas une garantie de stabilité sur ta carte."),
        // Avant Re-Size BAR : sa glose (« prérequis de Re-Size BAR ») contient ce nom.
        (["Above 4G"], "Permet de placer la mémoire des cartes au-delà des 4 premiers Go d'adresses. Re-Size BAR ne fonctionne pas sans lui."),
        (["Re-Size BAR", "Resizable BAR"], "Le processeur accède à toute la mémoire de la carte graphique d'un coup, au lieu de fenêtres de 256 Mo. "
            + "NVIDIA ne s'en sert que dans les jeux où son pilote l'a validé ; il demande Above 4G Decoding activé et CSM désactivé."),
        (["CSM"], "Le mode de démarrage des anciens systèmes. Windows 11 démarre en UEFI, sans lui ; Re-Size BAR exige qu'il soit désactivé."),
        (["C-State", "C State"], "Les états de veille des cœurs quand ils n'ont rien à faire : ils baissent la consommation au repos. "
            + "En jeu, les cœurs occupés n'y entrent pas ; « Auto » est le réglage d'usine."),
        (["eist"], "La baisse de fréquence et de tension au repos d'Intel. Dès que la charge arrive, le processeur remonte."),
        (["DRAM Frequency"], "La vitesse de la RAM telle que le BIOS la règle. Si elle ne concorde pas avec ce que Windows mesure, la ligne le dit."),
        (["Turbo Boost"], "Laisse le processeur monter au-dessus de sa fréquence de base tant que la puissance et la température le permettent. "
            + "Le couper coûte des images."),
        (["CPU Cooler Tuning"], "Chez MSI, ce menu choisit les limites de puissance du processeur (PL1, PL2) selon le refroidissement. "
            + "La page Processeur dit si cette limite bride tes images."),
        (["(PL1)"], "La puissance que le processeur peut tenir sans limite de durée : c'est elle qui compte dans une longue partie."),
        (["(PL2)"], "La puissance permise pendant une pointe courte, avant que le processeur redescende à PL1."),
        (["Fast Boot"], "Saute des vérifications au démarrage pour gagner quelques secondes. Sans effet en jeu."),
    ];

    internal static string? ExplicationBios(string ligne)
    {
        var nom = ligne.Split(" : ")[0];
        return ExplicationsBios.FirstOrDefault(e => e.Fragments.Any(f => nom.Contains(f, StringComparison.OrdinalIgnoreCase))).Explication;
    }

    // La fréquence mémoire est lue à un emplacement de la NVRAM ; le BIOS en déclare d'autres, masquées
    // selon le processeur. Si la valeur lue ne concorde pas avec ce que Windows mesure, on le dit au lieu
    // d'afficher une fréquence peut-être lue au mauvais endroit.
    private static string DiscordanceMemoire(BiosSettingReading r, int mtsWindows)
    {
        if (r.Setting != "DRAM Frequency" || mtsWindows <= 0) return string.Empty;
        var chiffres = new string(r.Libelle.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(chiffres, out var lu) && lu != mtsWindows
            ? $" (Windows mesure {mtsWindows} MT/s : lecture non concordante, à vérifier dans le BIOS)"
            : string.Empty;
    }

    /// <summary>Le domaine d'un constat de l'audit de santé, par sa clé brute (celle du journal).</summary>
    // 25/09/2026 : la famille thermique porte deux composants. Rangée par sa seule clé, la
    // température du GPU s'affichait sur la page Windows (vu sur photo). Le nom départage.
    public static string DomaineDeLAudit(string cle, string? nomCle) => (cle, nomCle) switch
    {
        ("THERMIQUE", "temperature du GPU") => "graphique",
        ("THERMIQUE", "temperature du processeur") => "processeur",
        _ => DomaineDeLAudit(cle),
    };

    public static string DomaineDeLAudit(string cle) => cle switch
    {
        "CPU" or "POWER" or "MMCSS" => "processeur",
        "GPU" or "GPU DRIVER" or "DISPLAY" => "graphique",
        "RAM" or "PAGEFILE" => "memoire",
        "LATENCE" => "latence",
        "RESEAU" => "reseau",
        "STOCKAGE" => "stockage",
        "BIOS" => "bios",
        _ => "windows",
    };

    /// <summary>Le domaine d'un réglage recommandé, par son intention.</summary>
    public static string DomaineDeLaRecommandation(ProfileIntent intention) => intention switch
    {
        ProfileIntent.Latency => "latence",
        ProfileIntent.GpuTune => "graphique",
        _ => "processeur",
    };

    /// <summary>29/09/2026 (boucle « vivant et prouvé », B9) : la phrase qui ouvre un domaine et dit quoi faire. Le plus grave
    /// d'abord (les lignes sont déjà rangées par gravité), sinon le premier point à regarder, sinon le réglage à essayer.</summary>
    private static string VerdictDuDomaine(EtatDomaine etat, IReadOnlyList<EntreeCarte> lignes, IReadOnlyList<EntreeCarte> proposes,
        Func<string, string> localiser)
    {
        // 03/10/2026 (D10.6, revue n° 4) : la phrase d'ouverture finit par un point, comme les autres domaines.
        string F(string cle, string titre) => AvecPoint(string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser(cle), DansLaPhrase(titre)));
        if (lignes.FirstOrDefault(l => l.Gravite > Severity.Info) is { } grave) return F("Carte.Verdict.ACorriger", grave.Titre);
        if (lignes.FirstOrDefault() is { } aRegarder) return F("Carte.Verdict.ARegarder", aRegarder.Titre);
        if (proposes.FirstOrDefault() is { } essai) return F("Carte.Verdict.AEssayer", essai.Titre);
        return localiser(etat switch
        {
            EtatDomaine.ARegarder => "Carte.Verdict.Mesure",
            EtatDomaine.RienDeSignale => "Carte.Verdict.Rien",
            _ => "Carte.Verdict.NonLu",
        });
    }

    private static readonly HashSet<string> NomsPropres = new(StringComparer.Ordinal)
        { "Windows", "Intel", "Microsoft", "Xbox", "Defender", "Discord", "Steam", "Chrome", "Edge", "Wi-Fi", "Ethernet", "Bluetooth", "Realtek" };

    /// <summary>03/10/2026 (D8.3, revue n° 3) : un titre repris après « ici : » donnait une majuscule après le
    /// deux-points (« ici : Écran… ») ou deux deux-points (« ici : service X : son fichier… »). Dans la phrase, le titre
    /// perd sa majuscule de tête et son deux-points intérieur devient une virgule.</summary>
    internal static string AvecPoint(string phrase) =>
        phrase.Length == 0 || ".!?…".Contains(phrase[^1]) ? phrase : phrase + ".";

    internal static string DansLaPhrase(string titre)
    {
        if (titre.Length == 0) return titre;
        var t = titre.Replace(" : ", ", ", StringComparison.Ordinal);
        var premier = t.Split(' ', 2)[0];
        // Limite assumée : liste courte de noms propres ; un nouveau nom propre en tête de titre perdrait sa majuscule ici.
        var baisser = t.Length > 1 && char.IsUpper(t[0]) && char.IsLower(t[1]) && !NomsPropres.Contains(premier);
        return baisser ? char.ToLower(t[0], System.Globalization.CultureInfo.CurrentCulture) + t[1..] : t;
    }

    public static IReadOnlyList<DomaineMachine> Construire(
        IEnumerable<EntreeCarte> entrees,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mesures,
        IEnumerable<ComponentItem> composants,
        Func<string, string> localiser,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? verifies = null,
        IReadOnlyDictionary<string, string>? explications = null,
        IReadOnlySet<string>? titres = null,
        IReadOnlySet<string>? mesureDemontree = null)
    {
        var parDomaine = entrees.ToLookup(e => e.Domaine);
        var composantsParDomaine = composants
            .GroupBy(c => DomaineDuComposant(c.IconKey))
            .ToDictionary(g => g.Key, g => g.First().Value);

        return Ordre.Select(cle =>
        {
            // 23/09/2026 : un réglage proposé se range à part. Sous « Constaté », « VRR + modèle flip
            // activés » se lisait comme un état de la machine, juste après « VRR inactif ».
            var lignes = parDomaine[cle].Where(e => !e.NonLu && !e.Propose)
                .OrderByDescending(e => e.Gravite).ToList();
            var proposes = parDomaine[cle].Where(e => !e.NonLu && e.Propose).ToList();
            var nonLus = parDomaine[cle].Where(e => e.NonLu).ToList();
            // Une majuscule initiale sur chaque ligne mesurée : « réseau : mesuré… » détonnait à côté
            // des autres (vu sur photo le 24/09/2026).
            var lesMesures = mesures.TryGetValue(cle, out var m)
                ? m.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => char.IsLower(x[0]) ? char.ToUpperInvariant(x[0]) + x[1..] : x).ToList()
                : [];

            // Les états que l'audit a lus et trouvés conformes : un domaine lu n'est jamais « pas lu ».
            // 24/09/2026 : nommés, plus seulement comptés (« 2 états conformes » ne disait pas lesquels).
            // Les sous-titres de famille ne sont pas des états : ils ne comptent pas.
            var nbEtats = verifies is not null && verifies.TryGetValue(cle, out var noms)
                ? noms.Count(n => titres?.Contains(n) != true) : 0;
            // 27/09/2026 : l'en-tête n'est pas une donnée ; avec une puce, il se lisait au même rang que les
            // états qu'il annonce (vu sur photo, carte graphique). Il porte son propre rang : `Entete`.
            string? entete = null;
            if (nbEtats > 0)
            {
                entete = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    localiser(nbEtats == 1 ? "Carte.Verifies.Un" : "Carte.Verifies.Plusieurs"), nbEtats) + " :";
                lesMesures.Add(entete);
            }
            // Un état par ligne : neuf états joints par des virgules faisaient un bloc illisible.
            if (verifies is not null && verifies.TryGetValue(cle, out var tous))
                lesMesures.AddRange(tous);

            var etat = lignes.Any(l => l.Gravite > Severity.Info) ? EtatDomaine.ACorriger
                : lignes.Count > 0 || proposes.Count > 0 ? EtatDomaine.ARegarder
                // 25/09/2026 : une mesure qui démontre un écart (le processeur tient 30 % de moins sous
                // charge longue, au-delà de trois fois sa précision) ne laisse pas dire « rien de signalé ».
                : mesureDemontree?.Contains(cle) == true && lesMesures.Count > 0 ? EtatDomaine.ARegarder
                // Connaître le nom du composant n'est pas l'avoir examiné : seule une mesure fait
                // « rien de signalé ».
                : lesMesures.Count > 0 ? EtatDomaine.RienDeSignale
                : EtatDomaine.NonLu;

            return new DomaineMachine(
                cle,
                localiser($"Carte.Domaine.{cle}"),
                Icones[cle],
                etat,
                localiser($"Carte.Etat.{etat}"),
                composantsParDomaine.GetValueOrDefault(cle, string.Empty),
                lesMesures,
                lignes,
                nonLus,
                proposes) { Explications = explications, Titres = titres, Entete = entete, Verdict = VerdictDuDomaine(etat, lignes, proposes, localiser) };
        }).ToList();
    }

    /// <summary>Le domaine à ouvrir quand le résultat arrive : le plus grave, sinon le premier.</summary>
    // 23/09/2026 : à état égal, la gravité départage (un XMP coupé, Majeur, passait après un VRR
    // inactif, Mineur, parce que les deux étaient « À corriger »).
    public static DomaineMachine? PremierAOuvrir(IReadOnlyList<DomaineMachine> carte) =>
        carte.OrderBy(d => d.Etat)
             .ThenByDescending(d => d.Lignes.Count == 0 ? Severity.Info : d.Lignes.Max(l => l.Gravite))
             .FirstOrDefault();

    internal static string DomaineDuComposant(string iconKey) => iconKey switch
    {
        "Cpu" => "processeur",
        "Gpu" => "graphique",
        "Ram" => "memoire",
        "Ssd" => "stockage",
        "Latency" => "latence",
        "Network" => "reseau",
        "Firmware" => "bios",
        _ => "windows",
    };
}
