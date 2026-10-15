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
    bool Propose = false,
    // La clé du constat (« Finding.Gpu.PerfMaxGlobal »), pour reconnaître un point sans comparer
    // des textes traduits.
    string? Cle = null,
    // Le geste porté par le constat lui-même ; sans lui, le remède se repère dans la prose.
    string? RemedeDuConstat = null,
    // Ce que DrDXT sait faire lui-même de ce remède : un bouton par geste, sous « Comment corriger ».
    IReadOnlyList<GesteDuDocteur>? Gestes = null)
{
    public bool AGestes => Gestes is { Count: > 0 };
    /// <summary>L'encadré « Comment corriger » : un texte de remède, des gestes, ou les deux. Sans texte, les gestes restaient cachés.</summary>
    public bool ACommentCorriger => ARemede || AGestes;

    /// <summary>La première phrase du détail reste à l'écran ; la suite s'ouvre sous « Pourquoi ? »
    /// (un constat mémoire faisait dix lignes d'un bloc).</summary>
    public string DetailCourt => CarteDeLaMachine.TitreEtSuite(Detail).Titre;
    public string DetailSuite => CarteDeLaMachine.TitreEtSuite(SansRemede).Suite;
    public bool ADetailSuite => DetailSuite.Length > 0;

    /// <summary>La Carte graphique était « à corriger » sans aucun geste visible ; le remède était écrit,
    /// mais derrière « Pourquoi ? ». Il se lit maintenant sans clic.</summary>
    // Simplification assumee : repere par les debuts de phrase que les textes de remede emploient deja ; un nouveau texte de remede
    // qui commence autrement reste derriere « Pourquoi ? », sauf si le constat porte son remede (RemedeDuConstat).
    // Seul le constat NVIDIA « performances maximales pour tout le PC » porte encore son remède dans l'explication ; les
    // autres passent par le champ Remede.
    private static readonly string[] DebutsDeRemede = ["Pour le retirer", "To remove it"];
    private int DebutDuRemede
    {
        get
        {
            var premiere = CarteDeLaMachine.TitreEtSuite(Detail).Titre.Length;
            return DebutsDeRemede.Select(d => Detail.IndexOf(d, premiere, StringComparison.Ordinal))
                .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        }
    }
    public string Remede => RemedeDuConstat ?? (DebutDuRemede is var i and >= 0 ? Detail[i..].Trim() : string.Empty);
    public bool ARemede => Remede.Length > 0;

    /// <summary>Les commandes icacls du remède, une par ligne, pour le bouton « Copier ». DrDXT
    /// n'exécute rien sur un logiciel tiers : il copie, le joueur colle dans sa console administrateur.</summary>
    public string CommandeACopier => string.Join("\r\n",
        System.Text.RegularExpressions.Regex.Matches(Remede, "icacls \"[^\"]+\" /(?:setowner \\S+|reset)").Select(m => m.Value));
    public bool ACommande => CommandeACopier.Length > 0;

    /// <summary>Deux commandes icacls complètes noyées dans la prose faisaient quatre
    /// lignes illisibles. La prose les annonce ; elles s'affichent dessous en police de code, une par ligne, et se copient.</summary>
    public string RemedeLisible => !ACommande ? Remede : System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(Remede, @"icacls ""[^""]+"" /setowner \S+ (?:puis|then) icacls ""[^""]+"" /reset", Docteur.T("Carte.DeuxCommandes")),
        @"icacls ""[^""]+"" /(?:setowner \S+|reset)", Docteur.T("Carte.UneCommande"));
    private string SansRemede => RemedeDuConstat is null && DebutDuRemede is var i and >= 0 ? Detail[..i].TrimEnd() : Detail;

    /// <summary>Sur un réglage proposé, <see cref="Valeur"/> porte la réserve « pas encore validé »,
    /// pas une lecture. Dans la pastille en police de code des valeurs lues, elle passait pour un chiffre mesuré.</summary>
    public string ValeurLue => Propose ? string.Empty : Valeur;

    /// <summary>La pastille en police de code recopiait la phrase juste au-dessus
    /// (« Version du pilote graphique » puis la même version). Elle ne s'affiche que si elle apprend quelque chose.</summary>
    public string ValeurAAfficher => ValeurLue.Length == 0 || DejaDit(ValeurLue) ? string.Empty : ValeurLue;

    /// <summary>L'étiquette « recommandation » sur un point rouge (à corriger) se contredisait.
    /// Sur un constat à corriger, elle se tait ; les autres étiquettes d'action restent.</summary>
    public string ActionAAfficher => Gravite > Severity.Info && string.Equals(Action, "recommandation", StringComparison.Ordinal)
        ? string.Empty : Action;

    private bool DejaDit(string valeur)
    {
        static string Plat(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var v = Plat(valeur);
        // Seul ce qui est affiché compte (titre et première phrase) : comparé au texte entier, un coût dit
        // seulement derrière le « ? » cacherait la pastille.
        var dessus = Titre + " " + DetailCourt;
        // Une valeur courte (« 60 », « on ») se retrouvait dans n'importe quel mot ou nombre du dessus.
        if (v.Length >= 4 && Plat(dessus).Contains(v, StringComparison.Ordinal)) return true;
        // Une valeur dont tous les nombres sont déjà dans la phrase n'apprend rien : après « Ton pilote NVIDIA 617.14 …
        // la dernière est la 617.42 », la pastille « 617.14 installé → 617.42 disponible » répéterait la phrase.
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
    /// <summary>Comme un constat, la première phrase reste à l'écran et la suite s'ouvre sous
    /// « Pourquoi ? » (la mesure mémoire et celle du réseau faisaient quatre lignes).</summary>
    public string TexteCourt => CarteDeLaMachine.GloserLeJargon(CarteDeLaMachine.TitreEtSuite(Texte).Titre);
    /// <summary>L'explication sourcée d'une ligne lue n'était qu'en info-bulle, que personne ne découvre
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

    /// <summary>Ce que la constellation de l'en-tête dessine. Une lecture par mesure ou état lu (ni
    /// l'en-tête « N états lus », ni les sous-titres de famille) et par constat ; les constats sont les points vifs.</summary>
    public int NombreDeLectures => Mesures.Count(m => m != Entete && Titres?.Contains(m) != true) + Lignes.Count;
    public int NombreDeConstats => Lignes.Count;

    /// <summary>L'explication sourcée d'un état lu sans geste, par texte de ligne. Sans
    /// elle, l'écran ne montrait que la valeur et la phrase n'était lue nulle part.</summary>
    public IReadOnlyDictionary<string, string>? Explications { get; init; }

    /// <summary>Les sous-titres de famille glissés dans la liste des états lus (vu sur
    /// photo : 20 lignes à plat sur la page Windows, services, mises à jour et périphériques mêlés).</summary>
    public IReadOnlySet<string>? Titres { get; init; }

    /// <summary>« N états lus par l'audit » annonce toute la liste. Ni donnée (pas de puce),
    /// ni titre de famille (même rendu, la hiérarchie devenait plate sur la page Processeur).</summary>
    public string? Entete { get; init; }

    /// <summary>La phrase d'ouverture du domaine, qui dit quoi faire.</summary>
    public string Verdict { get; init; } = "";

    /// <summary>Ce que la vue affiche : la ligne, et son explication en info-bulle quand il y en a une.</summary>
    public IReadOnlyList<LigneMesure> MesuresAffichees =>
        Mesures.Select(m => new LigneMesure(m, Explications?.GetValueOrDefault(m) ?? (Cle == "bios" ? CarteDeLaMachine.ExplicationBios(m) : null),Titres?.Contains(m) == true, m == Entete)).ToList();

    /// <summary>Au-delà de huit lignes (le BIOS en affichait dix-neuf), six restent à l'écran et le
    /// reste se déplie ; jusqu'à huit, tout se lit d'un coup.</summary>
    private const int LignesAvantRepli = 8, LignesGardees = 6;
    public IReadOnlyList<LigneMesure> MesuresVisibles =>
        MesuresAffichees.Count > LignesAvantRepli ? MesuresAffichees.Take(LignesGardees).ToList() : MesuresAffichees;
    public IReadOnlyList<LigneMesure> MesuresRepliees =>
        MesuresAffichees.Count > LignesAvantRepli ? MesuresAffichees.Skip(LignesGardees).ToList() : [];
    public bool ADesMesuresRepliees => MesuresRepliees.Count > 0;

    /// <summary>Le nombre annoncé sur « Voir les autres lignes ». Un sous-titre de famille n'est pas
    /// un état lu : le compter donnait « 14 états lus », trois affichés, « 13 autres » (vu sur la page Processeur).</summary>
    public int NombreDeLignesRepliees => MesuresRepliees.Count(m => !m.EstTitre && !m.EstEntete);

    /// <summary>La page Windows empilait douze constats. Les lignes arrivent triées par gravité :
    /// au-delà de quatre, le reste se déplie. UI1 : jamais un constat à corriger ; seul ce qui est pour
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

    /// <summary>Ce que DrDXT fait sur cette page, levier par levier.</summary>
    public IReadOnlyList<LigneLevier> Leviers { get; init; } = [];
    public bool ADesLeviers => Leviers.Count > 0;
}

/// <summary>Un levier sur sa page. <see cref="Raison"/> seulement quand il n'est pas pour ce matériel.</summary>
public sealed record LigneLevier(string Nom, string Statut, string? Raison, DrDxt.Application.Tweaks.StatutDuLevier Code)
{
    public bool ARaison => Raison is not null;
}

/// <summary>
/// Range par domaine de la machine tout ce que le scan a lu (constats, recommandations, audit de santé,
/// BIOS, mesures), pour qu'un joueur qui cherche « et ma mémoire ? » trouve sans tout lire. Elle range sans
/// juger ni ajouter de ligne : l'état d'un domaine vient des gravités déjà calculées, et un domaine non lu le dit.
/// Pure, sans WPF : l'ordre, les règles d'état et le rangement se testent seuls.
/// </summary>
public static class CarteDeLaMachine
{
    // Les identifiants du service (manifeste) vers le nom du réglage à l'écran.
    private static readonly IReadOnlyDictionary<string, string> NomsDuService = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["os.hags"] = "tweak.latency.hagsOn.title",
        ["latency.renderQueueCap"] = "tweak.latency.render_queue_cap.title",
        ["latency.vrrVsyncCap"] = "tweak.latency.vrr_vsync_cap.title",
        ["fixit.sanePowerPlan"] = "tweak.fixit.power_plan.title",
        ["fixit.x3dVCacheService"] = "tweak.fixit.x3d_ccd.title",
        ["latency.reflex.lowLatencyMode"] = "tweak.latency.reflex.title",
    };

    /// <summary>Ce que le service a dû réaffirmer à son dernier passage (Windows l'avait remis, au
    /// démarrage ou après une mise à jour), réglage par réglage, avec la date. Rien de lu = rien de dit.</summary>
    public static IReadOnlyList<string> LignesDuService(DrDxt.Reconciler.ReconcilerState? etat, Func<string, string> localiser)
        => (etat?.Items ?? [])
            .Where(i => string.Equals(i.LastResultCode, "Reapplied", StringComparison.Ordinal))
            .Select(i => string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser("Service.Reaffirme"),
                NomsDuService.TryGetValue(i.TweakId, out var cle) ? localiser(cle) : i.TweakId,
                i.LastReconciledAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)))
            .ToList();

    /// <summary>Les leviers rangés par page, nommés, avec leur statut et leur raison.</summary>
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

    /// <summary>Sous le verdict, ce qui est à corriger, le plus grave d'abord puis dans l'ordre de
    /// la carte, trois au plus par défaut. Les angles morts et les réglages proposés restent dans leur domaine : ils ne
    /// sont pas des défauts lus.</summary>
    public static IReadOnlyList<PointAFaire> AFaire(IEnumerable<DomaineMachine> domaines, int max = 3) =>
        Regrouper(domaines
            .SelectMany(d => d.Lignes.Where(l => l.Gravite != Severity.Info && !l.NonLu && !l.Propose).Select(l => new PointAFaire(d, l)))
            .OrderByDescending(p => p.Entree.Gravite)
            .ThenBy(p => Ordre.ToList().IndexOf(p.Domaine.Cle)))
            .Take(max)
            .ToList();

    /// <summary>Plusieurs points « à faire d'abord » peuvent dire la même chose (« service X : son fichier peut
    /// être remplacé par un autre programme »). Dans un même domaine, les constats
    /// qui partagent la phrase après « : » deviennent une ligne qui les nomme tous ; le domaine garde chacun avec son
    /// remède. La place libérée laisse monter les points suivants.</summary>
    internal static IEnumerable<PointAFaire> Regrouper(IEnumerable<PointAFaire> points)
    {
        var sep = Docteur.DeuxPoints;
        string? Suite(string titre) => titre.IndexOf(sep, StringComparison.Ordinal) is var i and > 0 ? titre[(i + sep.Length)..] : null;
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
            var noms = string.Join(", ", memes.Select(q => q.Entree.Titre[..q.Entree.Titre.IndexOf(sep, StringComparison.Ordinal)]));
            // Les noms d'abord, puis la phrase au pluriel : « Son fichier… (3 : …) » ne dirait pas de qui.
            var pluriel = suite.StartsWith("son ", StringComparison.Ordinal) || suite.StartsWith("sa ", StringComparison.Ordinal)
                ? "leur " + suite[(suite.IndexOf(' ') + 1)..]
                : suite.StartsWith("its ", StringComparison.Ordinal) ? "their " + suite[4..] : suite;
            var titre = char.ToUpper(noms[0], System.Globalization.CultureInfo.CurrentCulture) + noms[1..] + sep + pluriel;
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

    /// <summary>Un constat du moteur n'a qu'un texte. Affiché en entier comme titre, il
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
        => Docteur.T("Carte.Pcie", l.Gen, l.Largeur, l.GenMax, l.LargeurMax)
           + (l.Largeur < l.LargeurMax ? Docteur.T("Carte.Pcie.Bride")
               : l.Gen < l.GenMax ? Docteur.T("Carte.Pcie.Repos") : string.Empty);

    /// <summary>Les réglages lus dans la NVRAM du BIOS, une ligne chacun, avec le libellé que le menu
    /// du BIOS affiche (on le retrouve tel quel en y entrant). Rien de lu : aucune ligne.</summary>
    public static IReadOnlyList<string> LignesBios(IReadOnlyList<BiosSettingReading>? lus, int mtsWindows = 0)
    {
        if (lus is null) return [];
        // Les quatre paliers de la courbe du ventilateur processeur (huit octets au BIOS) tiennent en une ligne quand ils sont
        // tous lus ; sinon chaque palier lu garde sa ligne, au lieu de disparaître sans un mot.
        var courbe = CourbeVentilateurCpu(lus);
        var lignes = lus.Where(r => courbe is null || !EstPalierDeVentilateur(r.Setting))
            .Select(r => $"{r.Setting}{Glose(r.Setting)}{Docteur.DeuxPoints}{LibelleLisible(r.Libelle)}{(r.IsDefault ? Docteur.T("Carte.Bios.Usine") : Docteur.T("Carte.Bios.Modifie"))}"
                         + DiscordanceMemoire(r, mtsWindows)).ToList();
        if (courbe is not null)
        {
            var usine = lus.Where(r => EstPalierDeVentilateur(r.Setting)).All(r => r.IsDefault);
            lignes.Add(Docteur.T("Carte.Bios.CourbeVentilateur") + Docteur.DeuxPoints + courbe
                       + (usine ? Docteur.T("Carte.Bios.Usine") : Docteur.T("Carte.Bios.Modifie")));
        }
        return lignes;
    }

    private static bool EstPalierDeVentilateur(string reglage) => reglage.StartsWith("CPU Fan1 level ", StringComparison.Ordinal);

    /// <summary>Les quatre paliers de la courbe du ventilateur processeur lus au BIOS, en une valeur
    /// (« 40-62-80-92 °C, 35-55-70-100 % »). Un palier manquant : null, rien n'est inventé.</summary>
    internal static string? CourbeVentilateurCpu(IReadOnlyList<BiosSettingReading>? lus)
    {
        if (lus is null) return null;
        var temperatures = new List<long>();
        var vitesses = new List<long>();
        for (var i = 1; i <= 4; i++)
        {
            var t = lus.FirstOrDefault(r => r.Setting == $"CPU Fan1 level {i} Temperature");
            var v = lus.FirstOrDefault(r => r.Setting == $"CPU Fan1 level {i} Fan Speed");
            if (t is null || v is null) return null;
            temperatures.Add(t.Raw);
            vitesses.Add(v.Raw);
        }
        return string.Join("-", temperatures) + " °C, " + string.Join("-", vitesses) + " %";
    }

    /// <summary>Le libellé d'une fréquence mémoire tel que le BIOS l'écrit (« 3800 G1 (38x1.00x100.00) » : le débit, le
    /// rapport du contrôleur, puis la formule ratio x multiplicateur x horloge de base) se dit « 3800 MT/s, Gear 1 ».
    /// Tout autre libellé reste tel quel.</summary>
    internal static string LibelleLisible(string libelle) =>
        System.Text.RegularExpressions.Regex.Match(libelle, @"^(\d+) G([124]) \([\d.x]+\)$") is { Success: true } m
            ? Docteur.T("Carte.Bios.Frequence", m.Groups[1].Value, m.Groups[2].Value)
            : libelle;

    /// <summary>Ce que le réglage fait, en quelques mots, après son nom exact (qui
    /// reste celui du menu du BIOS). Un nom inconnu reste nu : pas de glose devinée. L'ordre compte : le premier
    /// fragment trouvé l'emporte.</summary>
    private static readonly (string Fragment, string Glose)[] Gloses =
    [
        ("X.M.P", "Carte.Glose.Xmp"), ("XMP", "Carte.Glose.Xmp"), ("EXPO", "Carte.Glose.Xmp"),
        ("Re-Size BAR", "Carte.Glose.Rebar"), ("Resizable BAR", "Carte.Glose.Rebar"),
        ("Above 4G", "Carte.Glose.Above4G"),
        ("CSM", "Carte.Glose.Csm"),
        ("C-State", "Carte.Glose.CState"), ("C State", "Carte.Glose.CState"),
        // Fragment en minuscules : la comparaison ignore la casse, et un sigle en capitales dans une chaîne en dur est refusé.
        ("eist", "Carte.Glose.Eist"),
        ("DRAM Frequency", "Carte.Glose.Dram"),
        ("Turbo Boost", "Carte.Glose.Turbo"),
        ("CPU Cooler Tuning", "Carte.Glose.Cooler"),
        ("(PL1)", "Carte.Glose.Pl1"), ("(PL2)", "Carte.Glose.Pl2"),
        ("Fast Boot", "Carte.Glose.FastBoot"),
    ];

    private static string Glose(string reglage) =>
        Gloses.FirstOrDefault(g => reglage.Contains(g.Fragment, StringComparison.OrdinalIgnoreCase)) is { Glose: { } glose }
            ? $" ({Docteur.T(glose)})" : "";

    internal static IEnumerable<string> FragmentsGloses => Gloses.Select(g => g.Fragment);

    /// <summary>Le jargon des lignes mesurées, reformulé au premier regard, une fois
    /// par ligne (la première occurrence), comme les réglages BIOS. L'explication complète reste sous « Pourquoi ? ».</summary>
    private static readonly (string Terme, string Glose)[] Jargon =
    [
        // Le terme suit la langue du texte ; la glose, celle du docteur.
        ("gigue", "Carte.Jargon.Gigue"), ("jitter", "Carte.Jargon.Gigue"),
        ("premier saut", "Carte.Jargon.PremierSaut"), ("first hop", "Carte.Jargon.PremierSaut"),
        ("modération d'interruption", "Carte.Jargon.Moderation"), ("interrupt moderation", "Carte.Jargon.Moderation"),
        ("BUGCODE_USB3_DRIVER", "Carte.Jargon.Usb3"),
    ];

    public static string GloserLeJargon(string texte)
    {
        foreach (var (terme, cle) in Jargon)
        {
            var glose = Docteur.T(cle);
            var i = texte.IndexOf(terme, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var fin = i + terme.Length;
            // « premier saut (ta box) (192.168.1.254) » ; quand le terme a déjà sa
            // parenthèse, la glose y entre : « premier saut (ta box, 192.168.1.254) ».
            texte = texte[fin..].StartsWith(" (", StringComparison.Ordinal)
                ? texte.Insert(fin + 2, $"{glose}, ")
                : texte.Insert(fin, $" ({glose})");
        }
        return texte;
    }

    /// <summary>Le BIOS était le seul domaine sans « Pourquoi ? ». Ce que le réglage
    /// change, par fragment de nom comme la glose ; un nom inconnu n'a pas d'explication (rien de deviné).</summary>
    private static readonly (string[] Fragments, string Explication)[] ExplicationsBios =
    [
        (["X.M.P", "XMP", "EXPO"], "Carte.Bios.Xmp"),
        // Avant Re-Size BAR : sa glose (« prérequis de Re-Size BAR ») contient ce nom.
        (["Above 4G"], "Carte.Bios.Above4G"),
        (["Re-Size BAR", "Resizable BAR"], "Carte.Bios.Rebar"),
        (["CSM"], "Carte.Bios.Csm"),
        (["C-State", "C State"], "Carte.Bios.CState"),
        (["eist"], "Carte.Bios.Eist"),
        (["DRAM Frequency"], "Carte.Bios.Dram"),
        (["Turbo Boost"], "Carte.Bios.Turbo"),
        (["CPU Cooler Tuning"], "Carte.Bios.Cooler"),
        (["(PL1)"], "Carte.Bios.Pl1"),
        (["(PL2)"], "Carte.Bios.Pl2"),
        (["Fast Boot"], "Carte.Bios.FastBoot"),
    ];

    internal static string? ExplicationBios(string ligne)
    {
        var nom = ligne.Split(Docteur.DeuxPoints)[0];
        return ExplicationsBios.FirstOrDefault(e => e.Fragments.Any(f => nom.Contains(f, StringComparison.OrdinalIgnoreCase))).Explication is { } cle
            ? Docteur.T(cle) : null;
    }

    // La fréquence mémoire est lue à un emplacement de la NVRAM ; le BIOS en déclare d'autres, masquées
    // selon le processeur. Si la valeur lue ne concorde pas avec ce que Windows mesure, on le dit au lieu
    // d'afficher une fréquence peut-être lue au mauvais endroit.
    private static string DiscordanceMemoire(BiosSettingReading r, int mtsWindows)
    {
        if (r.Setting != "DRAM Frequency" || mtsWindows <= 0) return string.Empty;
        var chiffres = new string(r.Libelle.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(chiffres, out var lu) && lu != mtsWindows
            ? Docteur.T("Carte.Bios.Discordance", mtsWindows)
            : string.Empty;
    }

    /// <summary>Le domaine d'un constat de l'audit de santé, par sa clé brute (celle du journal).</summary>
    // La famille thermique porte deux composants. Rangée par sa seule clé, la
    // température du GPU tomberait sur la page Windows. Le nom départage.
    public static string DomaineDeLAudit(string cle, string? nomCle) => (cle, nomCle) switch
    {
        ("THERMIQUE", "temperature du GPU") => "graphique",
        ("THERMIQUE", "temperature du processeur") => "processeur",
        // Les ventilateurs de la carte refroidissent d'abord le processeur.
        ("THERMIQUE", "ventilateur qui s'arrête" or "ventilateur qui ne tourne plus" or "ventilateurs non lus") => "processeur",
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

    /// <summary>La phrase qui ouvre un domaine et dit quoi faire. Le plus grave
    /// d'abord (les lignes sont déjà rangées par gravité), sinon le premier point à regarder, sinon le réglage à essayer.</summary>
    private static string VerdictDuDomaine(EtatDomaine etat, IReadOnlyList<EntreeCarte> lignes, IReadOnlyList<EntreeCarte> proposes,
        Func<string, string> localiser)
    {
        // La phrase d'ouverture finit par un point, comme les autres domaines.
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

    /// <summary>Un titre repris après « ici : » donnait une majuscule après le
    /// deux-points (« ici : Écran… ») ou deux deux-points (« ici : service X : son fichier… »). Dans la phrase, le titre
    /// perd sa majuscule de tête et son deux-points intérieur devient une virgule.</summary>
    internal static string AvecPoint(string phrase) =>
        phrase.Length == 0 || ".!?…".Contains(phrase[^1]) ? phrase : phrase + ".";

    internal static string DansLaPhrase(string titre)
    {
        if (titre.Length == 0) return titre;
        var t = titre.Replace(Docteur.DeuxPoints, ", ", StringComparison.Ordinal);
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
            // Un réglage proposé se range à part. Sous « Constaté », « VRR + modèle flip
            // activés » passerait pour un état de la machine, juste après « VRR inactif ».
            var lignes = parDomaine[cle].Where(e => !e.NonLu && !e.Propose)
                .OrderByDescending(e => e.Gravite).ToList();
            var proposes = parDomaine[cle].Where(e => !e.NonLu && e.Propose).ToList();
            var nonLus = parDomaine[cle].Where(e => e.NonLu).ToList();
            // Une majuscule initiale sur chaque ligne mesurée : « réseau : mesuré… » détonnerait à côté des autres.
            var lesMesures = mesures.TryGetValue(cle, out var m)
                ? m.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => char.IsLower(x[0]) ? char.ToUpperInvariant(x[0]) + x[1..] : x).ToList()
                : [];

            // Les états que l'audit a lus et trouvés conformes : un domaine lu n'est jamais « pas lu ».
            // Nommés, pas seulement comptés : « 2 états conformes » ne dit pas lesquels.
            // Les sous-titres de famille ne sont pas des états : ils ne comptent pas.
            var nbEtats = verifies is not null && verifies.TryGetValue(cle, out var noms)
                ? noms.Count(n => titres?.Contains(n) != true) : 0;
            // L'en-tête n'est pas une donnée : avec une puce, il passerait pour un état de plus.
            // Il porte son propre rang : `Entete`.
            string? entete = null;
            if (nbEtats > 0)
            {
                entete = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    localiser(nbEtats == 1 ? "Carte.Verifies.Un" : "Carte.Verifies.Plusieurs"), nbEtats) + Docteur.DeuxPoints.TrimEnd();
                lesMesures.Add(entete);
            }
            // Un état par ligne : neuf états joints par des virgules faisaient un bloc illisible.
            if (verifies is not null && verifies.TryGetValue(cle, out var tous))
                lesMesures.AddRange(tous);

            var etat = lignes.Any(l => l.Gravite > Severity.Info) ? EtatDomaine.ACorriger
                : lignes.Count > 0 || proposes.Count > 0 ? EtatDomaine.ARegarder
                // Une mesure qui démontre un écart (le processeur tient 30 % de moins sous
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
    // À état égal, la gravité départage (un XMP coupé, Majeur, passait après un VRR
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
