using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// La carte de la machine : les domaines, leurs satellites, les points à corriger, et sa construction après chaque scan.
public sealed partial class ScanViewModel
{
    /// <summary>
    /// La carte de la machine : les mêmes constats, mesures et recommandations que les
    /// listes ci-dessous, rangés par domaine (processeur, carte graphique, mémoire, latence, réseau,
    /// stockage, Windows, BIOS). Rien n'y est ajouté ; un domaine où rien n'a été lu le dit.
    /// </summary>
    public ObservableCollection<DomaineMachine> Domaines { get; } = new();

    [ObservableProperty]
    private DomaineMachine? _domaineSelectionne;

    public bool ADesDomaines => Domaines.Count > 0;

    /// <summary>L'orbite montrait cinq composants sans état ni clic, et taisait trois
    /// domaines. Chaque satellite porte maintenant l'état de son domaine et l'ouvre ; les domaines sans composant
    /// (latence, réseau, BIOS) sont listés sous l'orbite. Les cinq tentacules de chrome restent : l'effet en dessine cinq.</summary>
    /// <para>Les huit domaines sont huit satellites, chacun branché par sa tentacule. Plus de rangée de
    /// pastilles sous la bille.</para></summary>
    public static readonly string[] OrdreDesSatellites = ["Cpu", "Gpu", "Latency", "Ram", "Ssd", "Firmware", "Windows", "Network"];

    /// <summary>Les trois domaines sans pièce de matériel à eux (latence, réseau, BIOS) : posés au scan du matériel.</summary>
    private IReadOnlyList<ComponentItem> _composantsSansPiece = [];

    public IReadOnlyList<SatelliteDomaine> Satellites
    {
        get
        {
            var tous = Components.Concat(_composantsSansPiece).ToDictionary(c => c.IconKey, StringComparer.Ordinal);
            return OrdreDesSatellites.Where(tous.ContainsKey).Select(cle => tous[cle])
                .Select(c => new SatelliteDomaine(c, Domaines.FirstOrDefault(d => d.Cle == CarteDeLaMachine.DomaineDuComposant(c.IconKey))))
                .ToList();
        }
    }

    private void AnnoncerSatellites() { OnPropertyChanged(nameof(Satellites)); AnnoncerEtape(); }

    /// <summary>Le mot d'état d'un interrupteur, dans le gabarit « Bascule ».</summary>
    public string LabelBasculeAllume => _localize("Bascule.Allume");
    public string LabelBasculeEteint => _localize("Bascule.Eteint");

    public string LabelCarteTitre => _localize("Carte.Titre");
    public string LabelCarteMesures => _localize("Carte.Mesures");
    public string LabelCarteConstats => _localize("Carte.Constats");
    public string LabelCarteProposes => _localize("Carte.Proposes");
    public string LabelCarteNonLus => _localize("Carte.NonLus");
    public string LabelCarteVide => _localize("Carte.Vide");
    public string LabelMaintenirPourLancer => _localize("Scan.Hold.Hint");

    [RelayCommand]
    private void ChoisirDomaine(DomaineMachine? domaine)
    {
        if (domaine is not null) DomaineSelectionne = domaine;
    }

    /// <summary>Sous le verdict, ce qui est à corriger, le plus grave d'abord ; un clic ouvre son domaine.</summary>
    public ObservableCollection<PointAFaire> PointsAFaire { get; } = new();

    public string LabelAFaire => EnTeteAFaire(_localize, CompteCommeLaCarte().ACorriger);

    /// <summary>Le grand nombre de points à corriger ne dit pas où les trouver, et la
    /// liste n'en montre que les premiers. Son en-tête rappelle le total et où sont les autres.</summary>
    internal static string EnTeteAFaire(Func<string, string> localiser, int total) => total switch
    {
        <= 0 => localiser("Carte.AFaire"),
        1 => localiser("Carte.AFaire.Total.Un"),
        _ => string.Format(CultureInfo.CurrentCulture, localiser("Carte.AFaire.Total"), total),
    };
    public string LabelPourquoi => _localize("Carte.Pourquoi");
    public string LabelCommentCorriger => _localize("Carte.CommentCorriger");
    public string LabelCopierCommande => _localize("Carte.CopierCommande");

    /// <summary>Le presse-papiers de Windows ; une couture pour les tests.</summary>
    internal Action<string> VersPressePapiers { get; set; } = texte => System.Windows.Clipboard.SetText(texte);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Copier(string? texte)
    {
        if (!string.IsNullOrEmpty(texte)) VersPressePapiers(texte);
    }

    // Le texte d'un état lu sans geste : la même chaîne sert de ligne et de clé à son explication.
    private static readonly List<string> OrdreDesFamilles =
        ["STABILITE", "WINDOWS UPDATE", "SERVICES", "TACHES", "PERIPHERIQUES", "USB", "DEFENDER", "DEMARRAGE", "BCD"];

    internal static string TitreDuConstat(RepairFindingViewModel r, Func<string, string> localiser)
        => string.Equals(r.DomaineCle, "persistance", StringComparison.OrdinalIgnoreCase) && TweakDisplayKeys.TitleKey(r.NomCle) is var cle && cle != r.NomCle ? localiser(cle) : r.Nom;

    // La liste « Mesuré » prend le même titre et le même accord que les constats.
    internal static string LigneVerifiee(RepairFinding v) =>
        !string.IsNullOrWhiteSpace(v.ValeurCourante)
            ? $"{RepairFindingViewModel.TitreAffiche(v)}{Docteur.DeuxPoints}{RepairFindingViewModel.ValeurAffichee(v)}"
            : RepairFindingViewModel.TitreAffiche(v);

    /// <summary>D'où vient la preuve d'un réglage proposé : ce PC d'abord, puis un rapport archivé sur une carte de la
    /// même famille, sinon « pas encore prouvé ».</summary>
    internal static string MentionDePreuve(bool profilTeste, bool verifieIci, bool familleValidee, Func<string, string> localiser)
        => profilTeste ? string.Empty
         : verifieIci ? localiser("Levier.VerifieIci")
         : familleValidee ? localiser("Levier.VerifieFamille")
         : localiser("Carte.Propose.NonValide");

    private static string Majuscule(string phrase) =>
        phrase.Length == 0 ? phrase : char.ToUpper(phrase[0], CultureInfo.CurrentCulture) + phrase[1..];

    private void ConstruireCarte()
    {
        var entrees = new List<EntreeCarte>();

        foreach (var f in Findings)
        {
            var (titre, suite) = CarteDeLaMachine.TitreEtSuite(f.Title);
            if (f.PromesseRetiree is { } motif) suite = suite.Length == 0 ? motif : suite + " " + motif;
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDuConstat(f.Category), f.Severity,
                titre, suite, string.Empty, string.Empty, NonLu: false, Cle: f.Cle));
        }

        // Un réglage défait depuis portait son identifiant (« Latency.vrrFlipModelOn »)
        // pour titre ; il porte le nom que DrDXT lui donne partout ailleurs.
        foreach (var r in RepairFindings)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLAudit(r.DomaineCle, r.NomCle), r.Severity,
                TitreDuConstat(r, _localize),
                Majuscule(r.Explication), r.ValeurCourante, r.ActionLabel, NonLu: false, RemedeDuConstat: r.Remede,
                Gestes: GestesDuConstat(r)));

        foreach (var r in RepairBlindSpots)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLAudit(r.DomaineCle, r.NomCle), r.Severity,
                r.Nom, Majuscule(r.Explication), r.ValeurCourante, string.Empty, NonLu: true));

        foreach (var b in BiosFindings)
            entrees.Add(new EntreeCarte("bios", b.Severity, b.Title,
                b.Rationale, b.GainLabel, b.ActionLabel, NonLu: false));

        // Le titre est le nom du réglage, celui qu'annonce Optimiser ; l'argumentaire passe dessous (en titre, il
        // ferait trois lignes en gras). Un identifiant sans nom à l'écran garde l'argumentaire en titre.
        // Un réglage que « Vérifier DrDXT » a posé, relu et rendu sur ce PC le dit,
        // au lieu de « pas encore prouvé sur ce matériel ». Le profil entier, lui, reste non testé (une seule preuve).
        var verifiesIci = _lastScan is { } scanVerifie ? LeviersVerifiesIci?.Invoke(scanVerifie.Hw) : null;
        // La preuve doit venir de l'architecture d'une carte validée de ce PC : celle d'une autre architecture ne vaut pas ici.
        var architecturesValidees = _lastScan is { } scanCarte
            ? DrDxt.Application.Tweaks.Gpu.GpuTuningCoverage.For(scanCarte.Hw)
                .Where(c => c.Support == DrDxt.Application.Tweaks.Gpu.GpuTuningSupport.Validated).Select(c => c.Architecture).ToList()
            : [];
        foreach (var r in Recommendations)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLaRecommandation(r.Intent), Severity.Info,
                TweakDisplayKeys.TitleKey(r.TweakId) is var cle && cle != r.TweakId ? _localize(cle) : r.Claim,
                cle != r.TweakId ? $"{r.Claim} {r.Source}" : r.Source,
                MentionDePreuve(r.IsVerified, verifiesIci?.Contains(r.TweakId) == true,
                    architecturesValidees.Any(a => DrDxt.Application.Tweaks.Gpu.PreuvesArchivees.Prouve(a, r.TweakId)), _localize),
                TraduireNote(r.AdjustmentNote, _localize), NonLu: false, Propose: true));

        var latence = new List<string>();
        if (AvgFpsDisplay != Dash) latence.Add($"{LabelAvgFps}{Docteur.DeuxPoints}{AvgFpsAvecMarge}");
        if (P1LowDisplay != Dash) latence.Add($"{LabelP1Low}{Docteur.DeuxPoints}{P1LowAvecMarge}");
        // Les processus voisins ne sont pas une mesure du domaine : ils ont leur propre bloc.
        latence.AddRange(new[] { CadenceLabel, StutterLabel, PresentStallLabel, PerformanceDriftLabel }
            .Where(x => !string.IsNullOrWhiteSpace(x))!);

        var mesures = new Dictionary<string, IReadOnlyList<string>>
        {
            ["processeur"] = new[] { CpuSustainedLabel ?? string.Empty },
            ["graphique"] = new[] { GpuCeilingLabel ?? string.Empty },
            ["memoire"] = new[] { MemoryMeasuredLabel ?? string.Empty },
            ["latence"] = latence,
            ["reseau"] = new[] { NetworkPathLabel ?? string.Empty },
            // Ce que le service a réaffirmé seul après que Windows l'a remis.
            ["windows"] = CarteDeLaMachine.LignesDuService(LireEtatDuService?.Invoke(), _localize),
            ["bios"] = CarteDeLaMachine.LignesBios(_reglagesBiosLus, _lastScan?.Hw.Memory.FirstOrDefault()?.ConfiguredMhz ?? 0)
                .Concat(_lienPcie is { } lien ? [CarteDeLaMachine.LignePcie(lien)] : Array.Empty<string>())
                .Concat(CarteDeLaMachine.LigneEcartsUsine(_ecartsUsine) is { } ecarts ? [ecarts] : Array.Empty<string>()).ToList(),
        };

        // L'audit et les recommandations sont maintenant connus : le titre, la phrase d'assistant et
        // le résumé se recalculent pour ne jamais contredire la carte.
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(RegisterText));
        ProblemSummary = ResumeDesProblemes();
        AssistantSummary = ResumeDuRapport();

        Domaines.Clear();
        // Une observation (« Observed ») n'est pas un état conforme : elle porte sa valeur, qui la
        // distingue d'un constat du même nom (sinon un incident noyau isolé serait listé « conforme »
        // à côté du même incident répété, affiché à regarder).
        // Rangés par famille d'audit, avec son libellé en sous-titre quand un domaine en
        // mêle plusieurs ; ce qui peut faire planter la machine d'abord.
        var titres = new HashSet<string>();
        var verifies = (_derniereSante?.Verifies ?? []).Where(v => !DetailsTechniques.EnFait(v))
            .GroupBy(v => CarteDeLaMachine.DomaineDeLAudit(v.Domaine, v.Nom))
            .ToDictionary(g => g.Key, g =>
            {
                var familles = g.GroupBy(v => v.Domaine)
                    .OrderBy(f => OrdreDesFamilles.IndexOf(f.Key) is var i and >= 0 ? i : OrdreDesFamilles.Count)
                    .ThenBy(f => RepairLibelles.Domaine(f.Key), StringComparer.CurrentCulture)
                    .ToList();
                var lignes = new List<string>();
                // Un sous-titre par ligne n'est que du bruit (3 sous-titres pour 3 états).
                // Le rangement ne sert qu'une liste longue. Le seuil de 6 est un choix d'affichage, pas une source.
                var sousTitrer = familles.Count > 1 && g.Count() >= 6;
                foreach (var f in familles)
                {
                    if (sousTitrer)
                    {
                        var titre = RepairLibelles.Domaine(f.Key);
                        titres.Add(titre);
                        lignes.Add(titre);
                    }
                    lignes.AddRange(f.Select(LigneVerifiee));
                }
                return (IReadOnlyList<string>)lignes;
            });

        // L'explication sourcée de chaque état lu, en info-bulle sur sa ligne.
        // Un état lu qui porte ses étapes (le test mémoire jamais lancé) les garde ici : sans elles, elles n'apparaissaient nulle part.
        var explications = (_derniereSante?.Verifies ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.Explication) || !string.IsNullOrWhiteSpace(v.Remede))
            .GroupBy(LigneVerifiee)
            .ToDictionary(g => g.Key, g => Majuscule(g.First().Explication ?? "")
                + (string.IsNullOrWhiteSpace(g.First().Remede) ? ""
                    : $"{Environment.NewLine}{_localize("Carte.CommentCorriger")}{Docteur.DeuxPoints}{g.First().Remede}"));

        // Ce que DrDXT fait sur chaque page, levier par levier, sur la machine lue.
        var leviersParPage = CatalogueDesLeviers?.Invoke() is { } catalogue && _lastScan is { } scanLu
            ? CarteDeLaMachine.LeviersParPage(DrDxt.Application.Tweaks.CouvertureDesLeviers.Calculer(catalogue, scanLu.Hw, scanLu.State,
                LeviersPoses.Select(l => l.Id).ToHashSet(StringComparer.Ordinal), LeviersVerifiesIci?.Invoke(scanLu.Hw)), _localize)
            : null;
        // L'information d'expert lue, sans problème ni geste : repliée sous « Détails techniques », par page.
        var details = (_derniereSante?.Verifies ?? []).Where(DetailsTechniques.EnFait)
            .GroupBy(v => CarteDeLaMachine.DomaineDeLAudit(v.Domaine, v.Nom))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LigneMesure>)g
                .Select(v => new LigneMesure(LigneVerifiee(v), Majuscule(v.Explication))).ToList());
        foreach (var d in CarteDeLaMachine.Construire(entrees, mesures, Components, _localize, verifies, explications, titres,
                     _dernierDeficitCpu is { Demontrable: true } or { BrideDesLeDepart: true } && CpuSustainedLabel is not null ? new HashSet<string> { "processeur" } : null,
                     details))
            Domaines.Add(leviersParPage?.GetValueOrDefault(d.Cle) is { } leviers ? d with { Leviers = leviers } : d);
        DomaineSelectionne = CarteDeLaMachine.PremierAOuvrir(Domaines);
        PointsAFaire.Clear();
        foreach (var p in CarteDeLaMachine.AFaire(Domaines)) PointsAFaire.Add(p);
        OnPropertyChanged(nameof(ADesDomaines));
        AnnoncerSatellites();
    }
}
