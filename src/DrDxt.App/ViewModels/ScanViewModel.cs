using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Profiles;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// L'écran principal de DrDXT. Il lance le scan (matériel, réglages, BIOS, réseau, processeur, mémoire, mesure
/// en jeu, audit de santé), range ce qui a été lu sur la carte de la machine et porte les gestes du joueur
/// (Optimiser, rapport, mise à jour). Une étape en panne ne fait pas échouer le scan : elle dit ce qu'elle n'a pas lu.
/// Chaque fichier ScanViewModel.*.cs en porte une partie.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject
{
    private const string Dash = "—";

    // Garde du point de restauration : une application bloquée affiche un message explicite et
    // traduit, jamais un saut silencieux. La clé choisie dépend de la raison du blocage.
    private const string KeyRestoreBlocked = "Apply.RestoreRequired.Blocked";
    private const string KeyApplyFailed = "Apply.Failed";
    private const string KeyNothingToApply = "Apply.NothingApplicable";
    private const string KeySousGarde = "Apply.SousGarde";
    private const string KeyKeptOriginNotSigned = "Optimize.KeptOriginNotSigned";

    // DrDXT est gratuit (GPL-3.0) : rien ne mène à un achat.

    // La persistance : après une application validée, l'App enregistre un manifeste signé de
    // l'état voulu, pour que le reconciler le réimpose au démarrage. Un échec d'écriture n'est
    // pas fatal (les leviers sont déjà appliqués et mesurés) : il remonte ce signal, pas une
    // erreur bloquante.
    private const string KeyMoatUnarmed = "Apply.MoatUnarmed";
    // Le manifeste signe illisible : refus nomme, puis reprise explicite.
    private const string KeyManifestUnreadable = "Apply.ManifestUnreadable";
    private const string KeyReferenceNotSeeded = "Apply.ReferenceNotSeeded";
    private const string KeyManifestReset = "Apply.ManifestReset";
    private const string KeyManifestResetDone = "Apply.ManifestResetDone";
    private const string KeyManifestResetFailed = "Apply.ManifestResetFailed";

    private readonly IHardwareDetector _detector;
    private readonly ISettingsStateReader _settings;
    private readonly IBenchmarkRunner _benchmark;
    private readonly DrDxt.Measurement.Memory.MemoryBaselineService? _memoryBaseline;
    private readonly DrDxt.Measurement.Cpu.CpuBaselineService? _cpuBaseline;
    private readonly Func<CancellationToken, Task<string?>>? _attributionReseau;
    private readonly Func<IReadOnlyList<RepairFinding>, DrDxt.Infrastructure.Repair.JournalDeSante?>? _journalSante;
    private readonly Func<string>? _machineId;

    /// <summary>Le journal des mesures. Optionnel : le harnais AMD et la previsualisation QA n'en
    /// ont pas, et un scan doit se derouler sans lui.
    ///
    /// Sans journal, aucune derive n'est detectable entre sessions : le produit
    /// redecouvre la machine a chaque lancement, et une degradation lente est exactement ce qu'un
    /// scan isole ne voit pas : chaque mesure prise seule parait normale.</summary>
    private readonly DrDxt.Measurement.PerformanceJournalStore? _performanceJournal;
    private readonly IScanScorer _scorer;
    private readonly IStorageDetector _storage;
    private readonly IBiosAdvisor _biosAdvisor;
    // L'audit de sante (pure lecture). Une couture, pas le service concret : `RepairAuditService`
    // lit le registre, le journal d'evenements et `bcdedit` en dur, donc l'injecter tel quel
    // rendrait tout test de cet ecran dependant de la machine qui l'execute. Meme idiome que
    // `_machineId` / `_writeReport`. null => pas de section, et le scan ne perd rien.
    private readonly Func<IReadOnlyList<RepairFinding>>? _repairAudit;
    private readonly IGuideGenerator _guideGenerator;
    private readonly ISafetyManager _safety;
    /// <summary>Pour les tests : la porte du point de restauration, telle qu'Optimiser la franchit.</summary>
    internal ISafetyManager Safety => _safety;
    private readonly ITweakEngine _engine;
    private readonly IScanProfileConsumer _profileConsumer;
    private readonly IDesiredStateStore _desiredStateStore;
    private readonly AppliedTweakManifestMapper _manifestMapper;
    private readonly Func<string, string> _localize;
    // Détection générique du jeu (aucune liste par jeu) : trouve le jeu lancé pour le mesurer en partie.
    private readonly DrDxt.App.Platform.IGameLocator _gameLocator;
    // La boucle fermee du BIOS : poser le marqueur avant le voyage, relire au retour.
    // Optionnelle : null = la couture n'est pas la, le bouton « ouvrir le BIOS » ne s'affiche
    // pas (CanOpenFirmwareDirectly reste faux) et aucun verdict n'est prononce. C'est un
    // manque visible, pas un succes silencieux.
    private readonly DrDxt.App.Platform.IFirmwareTrip? _firmwareTrip;
    // De quoi fabriquer le rapport que la personne colle dans son ticket : la
    // version du produit et l'ecriture du fichier. Optionnelles : sans elles le bouton disparait
    // plutot que de ne rien faire.
    private readonly Func<string, string, string>? _writeReport;
    private readonly string _appVersion;

    // Pris au dernier scan réussi : Apply exécute le plan issu du profil et passe la base mesurée
    // au moteur, pour le verdict de remesure et de retour arrière automatique.
    private ScanProfileConsumption? _consumption;
    private BenchmarkResult? _scannedBaseline;
    // Le PID du jeu mesuré à ce scan (0 = aucun jeu lancé, donc config seule). Repris par la remesure d'Apply.
    private int _gamePid;
    private readonly DrDxt.App.Platform.ILecteurDeVoisins _voisins;
    // Le dernier tri de sante, le meme que l'ecran, pour que le rapport
    // exporte ce que le client a vu. null = audit non lance.
    private RepairTriage? _derniereSante;
    // Le conseiller BIOS a-t-il repondu a ce scan ? PopulateBiosFindings rend null pour « rien a
    // conseiller » comme pour une panne : seul ce drapeau les separe (DejaReglee le lit).
    private bool _biosLu;
    // Le goulot mesure par le scan : le diagnostic de l'Optimiser part de lui.
    private BottleneckVerdict? _goulotScan;
    // Un descripteur devient un candidat mesurable (journalise avant d'ecrire), et ce que la
    // stabilite interdit. null => aucun levier a chaud ne part en session / stabilite non lue.
    private readonly Func<CandidateDescriptor, IAutopilotCandidate?>? _creerCandidat;
    private readonly Func<(InstabiliteGpuObservee? Gpu, IReadOnlyList<ArretClasse>? Arrets)>? _lireStabilite;
    private readonly Func<string, bool>? _lancerInstalleur;
    // Ce que les threads du jeu ont consomme pendant la mesure du scan. null = pas observe.
    private readonly Func<int, TimeSpan, DrDxt.Detection.Cpu.ProcessCpuSample?>? _observerThreads;
    private GameThreadPressure _pressionThreads = GameThreadPressure.Unknown;
    // Les pertes a l'interface reseau pendant la meme fenetre de jeu. null = pas mesure.
    private readonly Func<TimeSpan, RepairFinding?>? _mesurerPertesReseau;
    private RepairFinding? _constatPertesReseau;
    // Le ping quand la machine envoie, pendant la meme fenetre de jeu. null = pas mesure.
    private readonly Func<TimeSpan, DrDxt.Measurement.Network.VerdictBufferbloat?>? _mesurerBufferbloat;
    private RepairFinding? _constatBufferbloat;
    private readonly Func<int, TimeSpan, Task<RepairFinding?>>? _mesurerRoute;
    private RepairFinding? _constatRoute;
    private readonly Func<TimeSpan, Task<ulong?>>? _mesurerMemoireLibre;
    private RepairFinding? _constatMemoireEnPartie;
    private readonly Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? _verifierPilote;
    private readonly Func<HardwareInventory, SettingsState, IReadOnlyList<RepairFinding>, string>? _exporterMachine;
    // Les constats de santé du dernier scan, tels que lus : ce que « Exporter ma machine » écrit.
    private IReadOnlyList<RepairFinding>? _constatsLus;
    private Task<RepairFinding?>? _verificationPilote;
    // Ce que le mode automatique posera : le plafond mesure et l'exe du jeu observe. 0 / null = rien
    // de mesure, et le mode refuse de demarrer : pas de plafond sans verdict.
    private long _plafondEnvoiMesure;
    private string? _exeJeuMesure;
    private readonly IElevatedClient? _helper;
    private readonly DrDxt.Application.Tweaks.JournalDesLeviersAppliques? _journalLeviers;
    private readonly DrDxt.Application.Tweaks.Hardware.IRegistryReader? _registreGains;
    private readonly Func<IReadOnlyList<RepairFinding>>? _lireReglagesJeu;
    private readonly Func<CarteDeLaMachine.LienPcie?>? _lirePcie;
    private CarteDeLaMachine.LienPcie? _lienPcie;
    private DrDxt.Application.Network.PlafondEnvoiPendantLeJeu? _modePlafond;
    private CancellationTokenSource? _battementPlafond;

    // Les DPC/ISR des pilotes pendant la meme fenetre de jeu. null = pas mesure.
    private readonly Func<TimeSpan, RepairFinding?>? _mesurerDpc;
    private RepairFinding? _constatDpc;
    // Chaque candidat juge laisse une ligne : le corpus qui tranchera un jour « le p95 seul suffit-il ».
    private readonly DrDxt.Application.Autopilot.JournalDesCandidats? _journalCandidats;

    private readonly DrDxt.Bios.NvramBiosSettingsReader? _biosNvram;
    // Ce que le scan a LU dans le BIOS, affiché dans son domaine (vu absent sur la photo).
    private IReadOnlyList<BiosSettingReading>? _reglagesBiosLus;
    private DrDxt.Bios.EcartsAUsine? _ecartsUsine;
    /// <summary>Où écrire le BIOS lu pour le docteur. L'appli le pose ; null = rien n'est écrit (les tests).</summary>
    internal FichierJsonAtomique? FichierBiosLu { get; set; }

    public ScanViewModel(
        IHardwareDetector detector,
        ISettingsStateReader settings,
        IBenchmarkRunner benchmark,
        IScanScorer scorer,
        IStorageDetector storage,
        ISafetyManager safety,
        ITweakEngine engine,
        IScanProfileConsumer profileConsumer,
        IDesiredStateStore desiredStateStore,
        AppliedTweakManifestMapper manifestMapper,
        IBiosAdvisor biosAdvisor,
        IGuideGenerator guideGenerator,
        Func<string, string> localize,
        // Optionnel avec valeur par défaut, pour que les constructions existantes (tests) compilent
        // toujours ; la DI passe le vrai localisateur Win32. Null → NullGameLocator → aucun jeu
        // trouvé → scan honnête de la config seule.
        DrDxt.App.Platform.IGameLocator? gameLocator = null,
        // Optionnel/en queue, comme gameLocator, pour que toutes les constructions existantes
        // (tests compris) restent valides. La DI passe la vraie couture.
        DrDxt.App.Platform.IFirmwareTrip? firmwareTrip = null,
        // (nom de fichier, contenu) -> chemin ecrit. Une couture : les tests n'ecrivent rien sur
        // le disque, et le VM ne connait pas l'arborescence de Windows.
        Func<string, string, string>? writeReport = null,
        string? appVersion = null,
        // Le banc memoire. Optionnels/en queue comme les precedents, pour que toutes les
        // constructions existantes restent valides. null => le scan ne mesure pas et n'affirme
        // rien de mesure sur la memoire ; la DI passe le vrai service.
        DrDxt.Measurement.Memory.MemoryBaselineService? memoryBaseline = null,
        Func<string>? machineId = null,
        DrDxt.Measurement.PerformanceJournalStore? performanceJournal = null,
        // L'audit de sante. Optionnel/en queue comme les precedents : toutes les constructions
        // existantes restent valides. null => aucune section d'audit, et surtout aucune phrase
        // affirmant que la machine va bien : un audit absent ne se raconte pas comme un audit vide.
        Func<IReadOnlyList<RepairFinding>>? repairAudit = null,
        DrDxt.Measurement.Cpu.CpuBaselineService? cpuBaseline = null,
        Func<IReadOnlyList<RepairFinding>, DrDxt.Infrastructure.Repair.JournalDeSante?>? journalSante = null,
        // L'attribution reseau. Optionnelle/en queue comme les precedentes : toutes les
        // constructions existantes restent valides. null => le scan ne dit rien du reseau, ce qui
        // est exactement ce qu'il faisait avant. Un faux compteur suffit a la tester d'ici.
        Func<CancellationToken, Task<string?>>? attributionReseau = null,
        // Le cerveau de l'Optimiser. Optionnels/en queue comme les precedents.
        Func<CandidateDescriptor, IAutopilotCandidate?>? creerCandidat = null,
        Func<(InstabiliteGpuObservee? Gpu, IReadOnlyList<ArretClasse>? Arrets)>? lireStabilite = null,
        // Lance l'installeur de mise a jour. null = ShellExecute (UAC par l'installeur).
        Func<string, bool>? lancerInstalleur = null,
        // Observe les threads du jeu (GetThreadTimes, sans elevation). null = pas observe.
        Func<int, TimeSpan, DrDxt.Detection.Cpu.ProcessCpuSample?>? observerThreads = null,
        // Mesure les pertes a l'interface pendant une fenetre. null = pas mesure.
        Func<TimeSpan, RepairFinding?>? mesurerPertesReseau = null,
        // Mesure les DPC/ISR des pilotes pendant une fenetre. null = pas mesure.
        Func<TimeSpan, RepairFinding?>? mesurerDpc = null,
        // Le reservoir des candidats juges par l'Optimiser. null = rien n'est consigne.
        DrDxt.Application.Autopilot.JournalDesCandidats? journalCandidats = null,
        // Ce qui tourne a cote du jeu pendant la mesure (demande produit : un scan a
        // affiche 164 FPS pendant qu'il regardait DrDXT, jeu en arriere-plan). null = on ne regarde
        // pas, et la liste reste vide, jamais « rien ne tournait ».
        DrDxt.App.Platform.ILecteurDeVoisins? voisins = null,
        // Demande produit : relancer l'appli elevee pour lire ce qui exige
        // l'administrateur. null = droits inconnus ou relance indisponible : aucun bouton.
        bool? estEleve = null,
        Func<bool>? relancerEnAdministrateur = null,
        // Lit les reglages BIOS dans la NVRAM (cartes de la matrice). null = on garde
        // l'inference, rien n'est affirme comme « lu dans le BIOS ».
        DrDxt.Bios.NvramBiosSettingsReader? biosNvram = null,
        Func<TimeSpan, DrDxt.Measurement.Network.VerdictBufferbloat?>? mesurerBufferbloat = null,
        // Le helper eleve qui posera le plafond. null = mode indisponible.
        IElevatedClient? helper = null,
        // Le journal des leviers gardes et le lecteur qui les relit. null = on ne remarque rien.
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null,
        Func<IReadOnlyList<RepairFinding>>? lireReglagesJeu = null,
        // Le lien PCIe de la carte graphique (NVML). null ou lecture ratée = « non lu », comme avant.
        Func<CarteDeLaMachine.LienPcie?>? lirePcie = null,
        // La route vers le serveur de la partie, pendant la même fenêtre. null = on ne regarde pas.
        Func<int, TimeSpan, Task<RepairFinding?>>? mesurerRoute = null,
        // Le plus bas de la mémoire libre pendant la fenêtre. null = on ne regarde pas.
        Func<TimeSpan, Task<ulong?>>? mesurerMemoireLibre = null,
        // La dernière version publiée du pilote, comparée à celle installée. null = on ne regarde pas.
        Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? verifierPilote = null,
        // Écrit la machine lue (inventaire, réglages, santé) au format des fixtures, anonymisée, et rend
        // le chemin. null = pas de bouton.
        Func<HardwareInventory, SettingsState, IReadOnlyList<RepairFinding>, string>? exporterMachine = null)
    {
        _verifierPilote = verifierPilote;
        _exporterMachine = exporterMachine;
        _mesurerRoute = mesurerRoute;
        _mesurerMemoireLibre = mesurerMemoireLibre;
        _lireReglagesJeu = lireReglagesJeu;
        _lirePcie = lirePcie;
        _journalLeviers = journalLeviers;
        _registreGains = registreGains;
        _mesurerBufferbloat = mesurerBufferbloat;
        _helper = helper;
        _biosNvram = biosNvram;
        _estEleve = estEleve;
        _relancerEnAdministrateur = relancerEnAdministrateur;
        _voisins = voisins ?? new DrDxt.App.Platform.AucunVoisin();
        _journalCandidats = journalCandidats;
        _observerThreads = observerThreads;
        _mesurerPertesReseau = mesurerPertesReseau;
        _mesurerDpc = mesurerDpc;
        _lancerInstalleur = lancerInstalleur;
        _creerCandidat = creerCandidat;
        _lireStabilite = lireStabilite;
        _attributionReseau = attributionReseau;
        _repairAudit = repairAudit;
        _memoryBaseline = memoryBaseline;
        _machineId = machineId;
        _cpuBaseline = cpuBaseline;
        _journalSante = journalSante;
        _performanceJournal = performanceJournal;
        _detector = detector;
        _settings = settings;
        _benchmark = benchmark;
        _scorer = scorer;
        _storage = storage;
        _biosAdvisor = biosAdvisor;
        _guideGenerator = guideGenerator;
        _safety = safety;
        _engine = engine;
        _profileConsumer = profileConsumer;
        _desiredStateStore = desiredStateStore;
        _manifestMapper = manifestMapper;
        _localize = localize;
        _gameLocator = gameLocator ?? new DrDxt.App.Platform.NullGameLocator();
        _firmwareTrip = firmwareTrip;
        _writeReport = writeReport;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? _localize("App.VersionInconnue") : appVersion;

        ChoixDeScene = new ChoixScene[]
        {
            new(SceneRepresentativity.Unknown, _localize("Scan.Scene.Unknown")),
            new(SceneRepresentativity.Gameplay, _localize("Scan.Scene.Gameplay")),
            new(SceneRepresentativity.Entrainement, _localize("Scan.Scene.Training")),
            new(SceneRepresentativity.MenuLobby, _localize("Scan.Scene.Menu")),
            // Le premier scan ne se fait pas en jeu. Sans cette case, la seule reponse
            // honnete disponible etait « je ne sais pas », alors que la personne sait tres bien.
            new(SceneRepresentativity.HorsJeu, _localize("Scan.Scene.HorsJeu")),
        };
        _sceneDeclaree = ChoixDeScene[0];
        AbonnerLaProchaineEtape();
        SuivreLesOperationsLongues();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(ApplyHintKey))]
    [NotifyPropertyChangedFor(nameof(ApplyHint))]
    [NotifyPropertyChangedFor(nameof(CanApplyGpuTuning))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGpuTuningCommand))]
    [NotifyPropertyChangedFor(nameof(TitreAccueil))]
    [NotifyPropertyChangedFor(nameof(SousTitreAccueil))]
    private ScanState _state = ScanState.Idle;

    /// <summary>Rempli quand une application est bloquée (garde du point de restauration, rien
    /// d'applicable, panne du moteur). Un message traduit et explicite, jamais un saut silencieux.</summary>
    [ObservableProperty]
    private string? _applyBlockedMessage;

    /// <summary>Ce que l'Optimiser a juge, pose et mesure, en clair. null = rien lance.</summary>
    [ObservableProperty]
    private string? _resultatOptimisation;

    /// <summary>Un souci est en cours, le point que l'Optimiser prendra le contiendra ; dit avant
    /// le clic, avec le point sain que le docteur a trouvé. null = pas de souci en cours, ou points illisibles.</summary>
    [ObservableProperty]
    private string? _avertissementPoint;

    /// <summary>Le bilan santé, huit lignes fixes ; avant le scan, toutes non lues.</summary>
    [ObservableProperty]
    private IReadOnlyList<LigneBilan> _bilan = BilanSante.Construire([]);

    /// <summary>Une reponse a « pendant la mesure, tu joues : », et son libelle localise.</summary>
    public sealed record ChoixScene(SceneRepresentativity Valeur, string Libelle)
    {
        /// <summary>Le nom que le lecteur d'écran annonce pour l'élément de liste : WPF lit ToString quand l'élément n'a pas de nom.</summary>
        public override string ToString() => Libelle;
    }

    /// <summary>Ce que le joueur dit jouer pendant la mesure en jeu. Par defaut « je ne
    /// sais pas » (`Unknown`), jamais un defaut rassurant : DrDXT ne sait pas reconnaitre un lobby, et
    /// le silence ne vaut jamais « partie reelle ».
    /// Pas memorise par jeu, expres : une scene se declare pour cette mesure. Reprendre la reponse
    /// d'hier ferait passer une ancienne partie pour celle d'aujourd'hui : c'est le defaut rassurant refuse
    /// ci-dessus, venu par un autre chemin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelSceneRetenue))]
    private ChoixScene _sceneDeclaree;

    /// <summary>Avant Optimiser, le choix déjà fait avant le scan se dit en une ligne ; le
    /// sélecteur ne revient que sur « Changer ».</summary>
    public string LabelSceneRetenue => SceneDeclaree.Valeur == SceneRepresentativity.Unknown
        ? _localize("Scan.Scene.PasChoisie")
        : $"{_localize("Scan.Scene.Retenue")} {SceneDeclaree.Libelle}";
    public string LabelSceneChanger => _localize("Scan.Scene.Changer");

    private static readonly System.Text.RegularExpressions.Regex Borne = new(
        @"(core offset|memory offset|power limit) clamped (-?\d+)→(-?\d+)(?:%| MHz) \(GpuSafeBounds\)");

    /// <summary>La note d'un réglage proposé vient du moteur, en anglais (ScanProfileConsumer) ; elle est
    /// traduite ici, phrase connue par phrase connue. Une phrase inconnue passe telle quelle plutôt que d'être tue.</summary>
    internal static string TraduireNote(string? note, Func<string, string> localiser)
    {
        if (string.IsNullOrEmpty(note)) return string.Empty;
        var t = note
            .Replace(ScanProfileConsumer.NoteReglageCarte, localiser("Carte.Note.ReglageCarte"))
            .Replace(ScanProfileConsumer.NoteNonLu, localiser("Carte.Note.NonLu"))
            .Replace(ScanProfileConsumer.NoteNonApplicable, localiser("Carte.Note.NonApplicable"))
            .Replace(ScanProfileConsumer.NoteInconnu, localiser("Carte.Note.Inconnu"));
        return Borne.Replace(t, m => string.Format(CultureInfo.CurrentCulture, localiser(m.Groups[1].Value switch
        {
            "core offset" => "Carte.Note.Borne.Coeur",
            "memory offset" => "Carte.Note.Borne.Memoire",
            _ => "Carte.Note.Borne.Puissance",
        }), m.Groups[2].Value, m.Groups[3].Value));
    }
    public string LabelAjouterPrecision => _localize("Scan.Scene.Preciser");
    public string LabelReglageCartePourquoi => _localize("Carte.Reglage.Pourquoi");
    public string LabelPlusDOptions => _localize("Resultat.PlusDOptions");
    public string LabelCadreReversible => _localize("Cadre.Reversible");
    public string LabelCadreALaMain => _localize("Cadre.ALaMain");
    public string LabelVoirVoisins => _localize("Scan.Voisins.Voir");
    public string LabelVoirLesAutres => _localize("Carte.VoirLesAutres");

    /// <summary>« Détails techniques » déplié ou non, gardé d'un lancement à l'autre comme le choix des animations.
    /// Replié par défaut ; un fichier absent ou abîmé vaut replié.</summary>
    [ObservableProperty] private bool _detailsTechniquesDeplies = LireDetailsDeplies();
    internal static DrDxt.Core.FichierJsonAtomique FichierDetailsDeplies { get; set; } =
        new(DrDxt.Core.FichierJsonAtomique.CheminLocal("details-techniques.json"), "DETAILS_TECHNIQUES");
    private static bool LireDetailsDeplies() => FichierDetailsDeplies.Read() is { IsOk: true, Value: { } v } && v.Trim() == "true";
    partial void OnDetailsTechniquesDepliesChanged(bool value) => FichierDetailsDeplies.WriteAtomic(value ? "true" : "false");

    // Le verbe et la durée sur chaque capsule Agir.
    public string LabelCapsuleTestMemoire => _localize("Agir.Capsule.TestMemoire");
    public string LabelCapsuleRemesureCpu => _localize("Agir.Capsule.RemesureCpu");
    public string LabelCapsuleRemesureMemoire => _localize("Agir.Capsule.RemesureMemoire");
    public string LabelCapsuleBcd => _localize("Agir.Capsule.Bcd");
    public string LabelCapsuleReparation => _localize("Agir.Capsule.Reparation");

    /// <summary>Windows a coupé ses animations (réglage d'optimisation courant) et DrDXT, qui le
    /// respecte, restait figé. Le joueur peut l'animer quand même ; le choix est gardé.</summary>
    public bool AnimationsFigeesParWindows => Effets.MoteurDAnimation.FigeParWindows;
    public string LabelAnimerMalgreWindows => _localize("Animations.MalgreWindows");

    [RelayCommand]
    private void AnimerMalgreWindows()
    {
        Effets.MoteurDAnimation.ChoisirAnimerMalgreWindows(true);
        OnPropertyChanged(nameof(AnimationsFigeesParWindows));
    }

    /// <summary>Ce que le joueur en dit, mot pour mot (facultatif). « partie reelle, zone calme »
    /// vaut mieux que n'importe quelle categorie : porte tel quel dans `SceneContract.Description`.</summary>
    [ObservableProperty]
    private string? _descriptionScene;

    /// <summary>Les quatre reponses, « je ne sais pas » en tete.</summary>
    public IReadOnlyList<ChoixScene> ChoixDeScene { get; }

    public string LabelSceneQuestion => _localize("Scan.Scene.Question");
    public string LabelSceneDescription => _localize("Scan.Scene.Description");

    /// <summary>Ce que le revert au demarrage a fait d'une session interrompue. Sa propre
    /// propriete : un clic sur Optimiser ou Scanner ne doit pas l'effacer avant qu'il soit lu.</summary>
    [ObservableProperty]
    private string? _messageDemarrage;

    // L'essai interrompu dont le rendu a été refusé (fréquence de la carte AMD au-delà de la bande du helper) ; null sinon.
    private DrDxt.Application.Autopilot.IRevertEnAttente? _essaiARemettreALUsine;

    /// <summary>Le constat de démarrage propose la remise à l'usine de la carte : elle ne part que sur ce clic.</summary>
    public bool RemiseUsineProposee => _essaiARemettreALUsine is not null;
    public string LabelRemiseUsine => _localize("Demarrage.RemiseUsine");

    // L'essai interrompu que le lancement n'a pas pu rendre (le helper ne tournait pas, ou un refus) ; null sinon.
    private (DrDxt.Application.Autopilot.IRevertEnAttente Journal, ITweakCatalog Catalogue)? _essaiARendre;

    /// <summary>Le constat de démarrage propose de rendre l'essai interrompu : sur ce clic, le helper peut être lancé.</summary>
    public bool RenduEssaiPropose => _essaiARendre is not null;
    public string LabelRendreEssai => _localize("Demarrage.RendreEssai");

    /// <summary>Au lancement, sans clic : l'essai d'une session interrompue est rendu si le helper tourne déjà ; sinon
    /// aucune invite Windows ne s'ouvre, le refus se dit, et le bouton le rend sur un clic.</summary>
    internal async Task RendreSessionInterrompueAsync(DrDxt.Application.Autopilot.IRevertEnAttente journal, ITweakCatalog catalogue)
    {
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        await RendreEssaiAsync(journal, catalogue).ConfigureAwait(true);
        // Ce qu'une App morte a posé dans la session sans en noter l'origine, rendu par celle-ci.
        (string? Texte, IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> Lignes) session;
        try { session = RejeuDuCompte is { } rc ? await Task.Run(rc).ConfigureAwait(true) : (null, []); }
        catch (Exception) { session = (null, []); }
        if (session.Texte is { } texteSession)
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? texteSession : MessageDemarrage + Environment.NewLine + texteSession;
        await SortirLesLeviersRendusAsync(session.Lignes).ConfigureAwait(true);
        // Ce que le helper a rendu d'une session interrompue, dit une fois, après le constat de l'App.
        string? rejeu;
        try { rejeu = RejeuLu is { } r ? await Task.Run(r.TexteNouveau).ConfigureAwait(true) : null; }
        catch (Exception) { rejeu = null; }
        if (rejeu is not null)
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? rejeu : MessageDemarrage + Environment.NewLine + rejeu;
        // Un compte standard se dit dès le lancement : sinon il ne se découvrait qu'au premier clic refusé.
        if (CompteStandard())
        {
            var compte = _localize("Demarrage.CompteStandard");
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? compte : MessageDemarrage + Environment.NewLine + compte;
        }
    }

    /// <summary>Le compte Windows du joueur est-il standard ? Une couture pour les tests.</summary>
    internal Func<bool> CompteStandard { get; set; } = DrDxt.App.Platform.CompteDuJoueur.Standard;

    /// <summary>Le rejeu du journal du compte (valeurs de la session) ; null dans les tests qui ne le portent pas.</summary>
    internal Func<Task<(string? Texte, IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> Lignes)>>? RejeuDuCompte { get; set; }

    /// <summary>Les rapports du rejeu du helper et du service ; null dans les tests qui ne les portent pas.</summary>
    internal Func<IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu>>? RapportsDuRejeu { get; set; }

    /// <summary>Un levier gardé qu'un rejeu a rendu (l'App arrêtée avant d'acquitter son origine) n'est plus gardé : il sort de
    /// la liste signée puis du journal, et c'est dit. Sans relecture branchée, rien n'est supposé.</summary>
    private async Task SortirLesLeviersRendusAsync(IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> duCompte)
    {
        if (_journalLeviers is not { } journal || VerifierLaPose is not { } relu) return;
        IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> lignes;
        try { lignes = [.. duCompte, .. (RapportsDuRejeu?.Invoke() ?? []).SelectMany(r => r.Lignes)]; }
        catch (Exception) { lignes = duCompte; }
        string sid;
        using (var moi = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = moi.User?.Value ?? string.Empty;
        var ids = DrDxt.App.Platform.LeviersRendusParLeRejeu.Ids(journal.Lire(), lignes, relu, sid);
        foreach (var id in ids)
        {
            await RetirerDuManifesteAsync(id).ConfigureAwait(true);
            journal.Retirer(id);
        }
        if (ids.Count == 0) return;
        var texte = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.RendusParLeRejeu"),
            string.Join(", ", ids.Select(id => _localize(TweakDisplayKeys.TitleKey(id)))));
        MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? texte : MessageDemarrage + Environment.NewLine + texte;
        ChargerLeviersPoses();
    }

    /// <summary>Le rapport du rejeu du journal d'écriture anticipée ; null dans les tests qui ne le portent pas.</summary>
    internal DrDxt.App.Platform.RejeuDuJournalLu? RejeuLu { get; set; }

    [RelayCommand]
    private Task RendreEssaiInterrompuAsync()
        => _essaiARendre is { } e ? RendreEssaiAsync(e.Journal, e.Catalogue) : Task.CompletedTask;

    private async Task RendreEssaiAsync(DrDxt.Application.Autopilot.IRevertEnAttente journal, ITweakCatalog catalogue)
    {
        string? phrase;
        var remiseUsine = false;
        // Hors du fil d'interface : le revert passe par le canal privilégié. Un refus de rendre la fréquence de la carte
        // AMD se dit ; la remise à l'usine attend le clic du joueur.
        try { phrase = await Task.Run(() => DrDxt.Application.Autopilot.RevertAuDemarrage.Executer(journal, catalogue, out remiseUsine,
            id => _localize(TweakDisplayKeys.TitleKey(id)), r => RaisonLisible.Dire(r, _localize), _registreGains, VerifierLaPose)).ConfigureAwait(true); }
        catch (Exception ex) { phrase = Docteur.T("App.VerificationSessionEchec", RaisonLisible.Dire(ex, _localize)); }
        if (remiseUsine) ProposerRemiseUsine(journal);
        // Pas rendu : l'essai reste au journal (le lancement suivant le retente), et le bouton le rend sur un clic.
        _essaiARendre = !remiseUsine && journal.Lire() is { IsOk: true, Value: not null } ? (journal, catalogue) : null;
        OnPropertyChanged(nameof(RenduEssaiPropose));
        if (phrase is not null) MessageDemarrage = phrase;
    }

    internal void ProposerRemiseUsine(DrDxt.Application.Autopilot.IRevertEnAttente journal)
    {
        _essaiARemettreALUsine = journal;
        OnPropertyChanged(nameof(RemiseUsineProposee));
    }

    [RelayCommand]
    private async Task RemettreLaCarteALUsineAsync()
    {
        if (_essaiARemettreALUsine is not { } journal) return;
        _essaiARemettreALUsine = null;
        OnPropertyChanged(nameof(RemiseUsineProposee));
        // Relu au clic, et repassé au même contrôle que le démarrage : ce fichier vit sous %LOCALAPPDATA%, écrivable par
        // tout programme du compte, et le helper rejoue son inverse.
        if (journal.Lire() is not { IsOk: true, Value: { Snapshot: { } snapshot } entree }
            || !DrDxt.Application.Autopilot.RevertAuDemarrage.InverseRejouable(entree.TweakId, snapshot.OriginalValueJson)
            || LevierParId?.Invoke(entree.TweakId) is not { } levier)
        {
            MessageDemarrage = _localize("Demarrage.RemiseUsineIntrouvable");
            return;
        }
        var titre = _localize(TweakDisplayKeys.TitleKey(entree.TweakId));
        var issue = await DrDxt.Application.Autopilot.AnnulationParClic.AnnulerAsync(levier, snapshot, _helper);
        if (!issue.Resultat.IsOk)
        {
            MessageDemarrage = RaisonLisible.OuIncertain(issue.Resultat,
                string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, RaisonLisible.Dire(issue.Resultat, _localize)), _localize, titre);
            _essaiARemettreALUsine = journal;
            OnPropertyChanged(nameof(RemiseUsineProposee));
            return;
        }
        // Rendu sans remise à l'usine : « annulé » seulement relu à l'origine ; encore posé, le bouton reste.
        var relu = issue.Message is null ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.NotApplied;
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            MessageDemarrage = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, _localize("Leviers.EncorePose"));
            _essaiARemettreALUsine = journal;
            OnPropertyChanged(nameof(RemiseUsineProposee));
            return;
        }
        // Rendue, l'entrée quitte le journal : le démarrage suivant ne la retente pas.
        journal.Lever();
        MessageDemarrage = issue.Message ?? string.Format(CultureInfo.CurrentCulture,
            _localize(relu == DrDxt.Application.Tweaks.EtatApplique.NotApplied ? "Leviers.Annule" : "Leviers.AnnuleNonRelu"), titre);
    }

    /// <summary>Posé quand Optimiser a réussi mais que l'écriture du manifeste signé a échoué : les réglages sont
    /// posés et mesurés, mais le service ne pourra pas les reposer au démarrage. Un message traduit, pas une erreur
    /// bloquante : le résultat s'affiche quand même.</summary>
    [ObservableProperty]
    private string? _moatUnarmedMessage;

    /// <summary>Un commit du manifeste a ete refuse parce que le manifeste signe existant ne se
    /// relit pas (ou que la cle de la machine est inutilisable) : rien n'a ete ecrit par-dessus. Le message
    /// le dit, avec la raison du helper, et <see cref="PeutRepartirDuManifeste"/> ouvre la reprise.</summary>
    [ObservableProperty]
    private string? _manifesteIllisibleMessage;

    [ObservableProperty]
    private bool _peutRepartirDuManifeste;

    public string LabelManifestReset => _localize(KeyManifestReset);

    /// <summary>
    /// Combien de leviers notés étaient vraiment lisibles sur cette machine. Le scorer le calcule
    /// (un levier Unknown ne retire rien, par principe : pénaliser ce qu'on n'a pas lu inventerait
    /// un déficit), mais rien ne l'affichait, et une machine à peine lue annonçait avec assurance
    /// « 100, en excellente santé ». Null = le scorer n'a rien rendu : c'est traité comme
    /// incomplet, jamais comme « tout a été lu ».
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    [NotifyPropertyChangedFor(nameof(CoverageText))]
    [NotifyPropertyChangedFor(nameof(IsCoverageComplete))]
    // Sans cette ligne, « lecture impossible » calculé avant la couverture restait à l'écran.
    [NotifyPropertyChangedFor(nameof(RegisterText))]
    private ScanCoverage? _coverage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _gatingNote;

    [ObservableProperty]
    private string? _degradedNote;

    [ObservableProperty]
    private string _avgFpsDisplay = Dash;

    [ObservableProperty]
    private string _p1LowDisplay = Dash;

    [ObservableProperty]
    private string _marginDisplay = Dash;

    // Récit dérivé : rempli pendant le scan, déjà traduit.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SousTitreAccueil))]
    private string _subtitleText = string.Empty;

    // L'en-tête « À faire d'abord » reprend ce compte ; calculé une seule fois, avant
    // que la santé soit chargée, il affichait « les 1 points » sous « 13 points à corriger ». Il suit maintenant le résumé.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelAFaire))]
    [NotifyPropertyChangedFor(nameof(TitreAccueil))]
    private string _problemSummary = string.Empty;

    /// <summary>Le titre de l'accueil : l'état de l'analyse, pas l'inventaire de ce qui ne va pas. Le nombre vit dans le
    /// dépliant des points (<see cref="LabelAFaire"/>), le plus grave en tête. Une analyse partielle (perf en jeu pas encore
    /// mesurée, le cas d'un premier scan) a abouti ; seul un échec garde son résumé.</summary>
    public string TitreAccueil => ScanPretAAgir ? _localize("Accueil.Titre") : ProblemSummary;

    /// <summary>Sous le titre : rien après une analyse qui a abouti (« joue une partie, puis relance » était un conseil, pas
    /// un état) ; ce qui manque, après un échec.</summary>
    public string SousTitreAccueil => ScanPretAAgir ? string.Empty : SubtitleText;

    [ObservableProperty]
    private string _assistantSummary = string.Empty;

    public bool IsScanning => State == ScanState.Scanning;
    public bool CanScan => !OperationLongueEnCours;

    /// <summary>Optimiser est offert des qu'un scan a produit des resultats (Done / AlreadyOptimal /
    /// Degraded). Il juge d'abord ce qu'il y a a tenter, et le dit quand il n'y a rien.</summary>
    public bool CanApply => ScanPretAAgir && !OperationLongueEnCours;

    /// <summary>Ce que l'infobulle du bouton « Appliquer » dit, selon qu'il peut agir ou non.
    /// <para>
    /// Six commandes de l'application peuvent etre grisees et aucune
    /// n'expliquait son inertie. Celle-ci est l'action principale du produit : grisee
    /// avant tout scan, elle laissait la personne devant un bouton mort, sans savoir
    /// s'il est casse ou s'il attend un geste d'elle.
    /// </para></summary>
    // L'info-bulle
    // n'était jamais recalculée après le scan. Elle suit l'état, et dit quand il n'y a rien à poser.
    public string ApplyHintKey => !ScanPretAAgir ? "scan.apply.needs_scan" : OperationLongueEnCours ? "UI.OperationEnCours"
        : RienAPoserDuTout ? "Apply.RienAPoserSeul" : "scan.apply.ready";

    /// <summary>Le texte deja traduit. `ScanView` ne declare pas le convertisseur `Loc`
    /// (il n'est declare que dans `MainWindow.xaml`) : lier une cle ici aurait leve au rendu, et
    /// un test qui se contente de relire le XAML ne l'aurait jamais vu. Cette vue localise
    /// par ses proprietes, comme `LabelOptimize` juste a cote.</summary>
    public string ApplyHint => _localize(ApplyHintKey);

    /// <summary>L'application du réglage GPU est explicite, sur demande : proposée seulement quand
    /// un scan a rendu des résultats et que le profil de la puce recommande une étape GPU applicable
    /// à cette machine. Un GPU non pris en charge (inconnu, iGPU, portable, console portable) → pas
    /// de plan GPU → l'action reste désactivée (jamais d'OC posé en silence au scan).</summary>
    public bool CanApplyGpuTuning => CanApply && _consumption is { HasGpuPlan: true };

    /// <summary>Vrai quand la machine scannée a une recommandation de réglage GPU applicable
    /// (pilote la visibilité de l'action GPU sur demande ; binding de l'UI à venir).</summary>
    public bool HasGpuTuningRecommendation => _consumption is { HasGpuPlan: true };

    /// <summary>Faux quand les lignes de réglage GPU recommandées ne sont pas encore validées
    /// sur du vrai matériel (Tested=false) : l'UI marque l'action GPU « non vérifié ».</summary>
    public bool GpuTuningVerified =>
        _consumption is { HasGpuPlan: true } c
        && c.Items.Where(i => c.GpuPlan.TweakIds.Contains(i.TweakId)).All(i => i.IsVerified);

    /// <summary>
    /// Le verdict en une expression : trois états, fonction de deux entiers, jamais d'une somme.
    ///
    /// Il ne dépend d'aucune somme de pénalités : doubler toutes les pénalités du registre sans changer un levier ne
    /// change pas ce que le client lit (test d'invariance).
    ///
    /// Les trois etats, dans l'ordre de priorite :
    ///   K > 0            -> la lecture est incomplete. Aucun verdict de sante n'est permis :
    ///                       ce qu'on n'a pas lu peut etre exactement le probleme.
    ///   K = 0 et N = 0   -> rien a corriger dans ce qui a ete lu. La nuance est la promesse.
    ///   K = 0 et N > 0   -> N reglages a corriger. Aucun plancher : douze mauvais leviers
    ///                       affichent douze.
    /// </summary>
    public string VerdictText =>
        !IsCoverageComplete || _santeNonLue ? _localize("UI.Verdict.Partial")
        : ProvenProblemCount == 0 && !SanteACorriger ? _localize("UI.Verdict.Clean")
        // Vu sur la photo, « réglages à corriger » au-dessus de « 0 leviers mal réglés » : les
        // points venaient de la santé (mises à jour, incidents, failles), pas des réglages. Le titre le dit.
        : ProvenProblemCount == 0 ? _localize("UI.Verdict.SanteACorriger")
        : _localize("UI.Verdict.Problems");

    /// <summary>
    /// L'audit de santé a trouvé au moins un état suspect ou nuisible. Le titre et
    /// le résumé ne regardaient que le scanner : « rien à corriger » s'affichait au-dessus de
    /// « Windows et stabilité : à corriger ». Un état seulement à surveiller (information) ne compte pas.
    /// </summary>
    private bool SanteACorriger => RepairFindings.Any(r => r.Severity > Severity.Info);

    /// <summary>Vrai seulement si chaque levier applicable a été lu. Une couverture absente compte comme faux.</summary>
    public bool IsCoverageComplete => Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 };

    /// <summary>« lus/applicables » : le compte de lecture derrière le score. Un tiret cadratin quand il manque.</summary>
    public string CoverageText => Coverage is { } c
        ? $"{c.KnownLevers}/{c.ApplicableLevers}"
        : "—";

    // ═══ Le registre ═════════════════════════════════════════════════════════════════════════════
    //
    // Il ne promet rien et ne note rien : il compte. « 3 problèmes prouvés · 12 leviers lus sur 14 ». Les deux nombres
    // sont des faits de lecture, pas des jugements ; aucune note sur 100 (ses magnitudes sont des estimations d'expert,
    // et son plancher l'empêchait de dire « cette machine est mal réglée »).

    /// <summary>
    /// Le nombre de problemes de configuration constates : les leviers lus et trouves
    /// sous-optimaux, comptes dans le grand livre.
    ///
    /// « Prouve » porte sur l'existence du probleme, jamais sur son ampleur : on a lu que XMP est
    /// desactive, c'est un fait ; combien d'images ca coute reste une estimation, et c'est
    /// precisement pour ca qu'aucun chiffre de gain n'entre dans ce compteur.
    ///
    /// Vient du grand livre et pas de <see cref="Findings"/> : la liste des constats contient
    /// aussi des lignes d'information et de guidage (« ReBAR non lisible sur NVIDIA »), et les
    /// compter comme des problemes gonflerait le registre avec ce qu'on n'a justement pas prouve.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegisterText))]
    [NotifyPropertyChangedFor(nameof(HasProvenProblems))]
    // Le verdict dépend du compte : sans cette ligne il resterait figé
    // sur l'ecran apres un second scan.
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    private int _provenProblemCount;

    /// <summary>Vrai quand le scan a constate au moins un probleme.</summary>
    public bool HasProvenProblems => ProvenProblemCount > 0;

    /// <summary>
    /// Le registre, en une ligne : « 3 problemes prouves · 12 leviers lus sur 14 ». C'est tout ce
    /// que le produit affirme au premier coup d'oeil.
    ///
    /// Sans couverture on n'affiche que le compte de problemes : « 3 problemes prouves · 12
    /// leviers lus sur 0 » serait une ligne fausse, et un tiret la rendrait illisible.
    /// </summary>
    public string RegisterText
    {
        get
        {
            var problems = string.Format(
                CultureInfo.CurrentCulture,
                // « 0 à changer » sous « 13 points à corriger » passerait pour un
                // démenti ; à zéro, la phrase le dit en mots, et la ligne précise qu'elle parle de ce que DrDXT pose lui-même.
                _localize(ProvenProblemCount == 0 ? "UI.Register.None" : ProvenProblemCount == 1 ? "UI.Register.Problem" : "UI.Register.Problems"),
                ProvenProblemCount);

            // Sans couverture, on n'affiche pas le compte seul. Mesure du 05/09 : « 3 problemes
            // prouves » sans savoir combien de leviers ont ete lus laisse croire que la machine a
            // ete examinee. Un scan qui n'a pas pu mesurer sa propre couverture n'a rien examine
            // de facon affirmable, et il doit le dire.
            if (Coverage is not { } c)
                return _localize("UI.Register.NoCoverage");

            // Tout a été lu et rien du catalogue ne s'applique à cette machine. Ce n'est
            // pas « lecture impossible » : c'est une lecture complète au résultat vide.
            if (c.ApplicableLevers == 0)
                return _localize("UI.Register.NothingApplies");

            var read = string.Format(
                CultureInfo.CurrentCulture,
                _localize("UI.Register.LeversRead"), c.KnownLevers, c.ApplicableLevers);

            // D'abord de quoi on parle (les réglages que DrDXT sait poser), puis
            // combien sont à changer ; lu dans l'autre sens, il contredisait « 12 points à corriger » au-dessus.
            return $"{read}, {problems}";
        }
    }

    // Libellés de l'UI : liés via la traduction, pour qu'aucune chaîne ne soit écrite en dur dans le XAML.
    public string LabelHeaderTagline => _localize("UI.Header.Tagline");
    public string LabelAppName => _localize("UI.App.Name");
    public string LabelAvgFps => _localize("UI.Label.AvgFps");
    public string LabelP1Low => _localize("UI.Label.P1Low");
    public string LabelMargin => _localize("UI.Label.Margin");
    public string LabelScanButton => _localize("Scan.Button.Start");
    public string LabelFindings => _localize("Scan.Findings.Header");
    public string LabelRecommendationsHeader => _localize("Scan.Recommendations.Header");

    /// <summary>Zero levier recommande se dit, au lieu d'un compteur a 0 et d'une liste vide.</summary>
    public bool AucuneRecommandation => Recommendations.Count == 0;
    private void NotifierAucuneRecommandation(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(AucuneRecommandation));
    public string LabelAucuneRecommandation => _localize("Scan.Recommendations.None");
    public string LabelScanning => _localize("Scan.State.Scanning");
    // Titre et texte du bandeau principal, à l'attente.
    public string LabelIdleHeadlineLead => _localize("UI.Idle.HeadlineLead");
    public string LabelIdleHeadlineAccent => _localize("UI.Idle.HeadlineAccent");
    public string LabelProductPromise => _localize("Product.Promise");
    public string LabelIdleHeroTitle => _localize("UI.Idle.HeroTitle");
    public string LabelIdleHeroBody => _localize("UI.Idle.HeroBody");
    public string LabelTrustLocal => _localize("UI.Trust.Local");
    public string LabelTrustData => _localize("UI.Trust.Data");

    public string LabelScanDone => _localize("UI.Result.ScanDone");

    // Bandeau principal, côté résultats.
    public string LabelCoverageCaption => _localize("UI.Coverage.Caption");
    /// <summary>Sous-titre du registre : dit ce que les deux nombres sont, des lectures.</summary>
    public string LabelRegisterCaption => _localize("UI.Register.Caption");
    public string LabelOptimize => _localize("UI.Cta.Optimize");
    public string LabelRapportDescription => _localize("Rapport.Description");
    public string LabelEssaiExplication => _localize("Essai.Explication");
    public string LabelRescan => _localize("UI.Cta.Rescan");

    // Panneau d'aide.
    public string LabelAssistantTitle => _localize("UI.Assistant.Title");
    public string LabelAssistantOnline => _localize("UI.Assistant.Online");
    public string LabelAssistantIdleLead => _localize("UI.Assistant.IdleLead");
    public string LabelAssistantWillExamine => _localize("UI.Assistant.WillExamine");
    public string LabelViewReport => _localize("UI.Assistant.ViewReport");

    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<ComponentItem> Components { get; } = new();

    /// <summary>Les recommandations du profil de la puce pour la machine scannée : chacune porte
    /// sa GainEvidence honnête (affirmation et source) et un drapeau
    /// <see cref="ProfileRecommendationItem.IsVerified"/>. Rempli à chaque scan réussi ; une ligne
    /// pas encore validée (Tested=false) est marquée « non vérifié ».</summary>
    public ObservableCollection<ProfileRecommendationItem> Recommendations { get; } = new();

    /// <summary>L'étiquette traduite « non vérifié » d'une recommandation de profil pas encore
    /// validée (un élément dont <see cref="ProfileRecommendationItem.IsVerified"/> est faux).
    /// La vue l'affiche à côté de ces lignes.</summary>
    public string LabelUnverified => _localize("UI.Profile.Unverified");

    /// <summary>Les constats firmware du docteur BIOS pour la machine scannée, affichés dans les
    /// résultats. Uniquement du guidage (jamais dans le lot appliqué : on n'écrit jamais le
    /// firmware), les erreurs de réglage d'abord : les constats à guider (sévérité la plus haute en
    /// tête, donc XMP/EXPO coupé, le plus gros gain réel, passe premier) avant ceux d'information ou
    /// illisibles. Rempli à chaque scan réussi ; vide si la machine est bien réglée, si le BIOS est
    /// verrouillé (portable, console portable) ou si le conseiller tombe en panne (au mieux, sans
    /// jamais casser le scan).</summary>
    public ObservableCollection<BiosFindingViewModel> BiosFindings { get; } = new();

#if DEBUG
    /// <summary>Debug seulement (argument « xmp-off ») : voir le guide BIOS d'un XMP coupé.</summary>
    internal static bool SimulerXmpOffPourLeBios;
#endif

    /// <summary>Vrai quand le scan a rendu au moins un constat BIOS (pilote la visibilité de la
    /// section dans ScanView.xaml).</summary>
    public bool HasBiosFindings => BiosFindings.Count > 0;

    /// <summary>Titre traduit de la section des constats BIOS (guidage seul).</summary>
    public string LabelBiosHeader => _localize("Scan.Bios.Header");

    /// <summary>Le guide BIOS personnalisé de la machine scannée : le « comment » derrière les
    /// constats (le chemin exact menu → réglage → valeur, plus la section de secours obligatoire et
    /// une étiquette de confiance honnête : Verified seulement pour la carte exacte, sinon un repli
    /// connu, clairement étiqueté). Du texte de guidage seul : jamais une écriture, jamais dans le
    /// lot appliqué. Construit seulement si le générateur a produit au moins une étape à faire ;
    /// null si la machine est bien réglée, si le guide n'a que le secours, ou si le générateur tombe
    /// en panne (au mieux, sans jamais casser le scan).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBiosGuide))]
    private BiosGuideViewModel? _biosGuide;

    /// <summary>Vrai quand le scan a produit un guide BIOS à suivre (pilote la visibilité de la
    /// section dans ScanView.xaml).</summary>
    public bool HasBiosGuide => BiosGuide is not null;

    /// <summary>Le verdict du retour de BIOS : « ton changement a-t-il pris ? », reglage par
    /// reglage. Null quand rien n'etait en attente : c'est le cas de presque toutes les
    /// ouvertures de l'app, et une section vide ne doit pas s'afficher.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBiosLoopResult))]
    private BiosLoopViewModel? _biosLoop;

    // La moitie memoire de la meme section : ce que la machine mesurait avant le voyage
    // BIOS, ce qu'elle mesure apres, et si l'ecart est un resultat. null = rien a montrer.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryAb))]
    private MemoryAbViewModel? _memoryAb;

    /// <summary>Vrai quand un voyage dans le BIOS attend son verdict (pilote la visibilite).</summary>
    public bool HasBiosLoopResult => BiosLoop is not null;

    public string LabelOublierChangementBios => _localize("Bios.Retour.Oublier");

    [ObservableProperty] private string? _erreurOubliBios;

    /// <summary>Renoncer a un changement qui n'a pas pris. Le marqueur est retire, la section
    /// disparait ; au prochain passage par le bouton du guide, un nouveau voyage repart de zero.</summary>
    [RelayCommand]
    private void OublierChangementBios()
    {
        if (_firmwareTrip is null || BiosLoop is null) return;
        var r = _firmwareTrip.Oublier();
        if (r.IsOk) { ErreurOubliBios = null; BiosLoop = null; MemoryAb = null; }
        else ErreurOubliBios = RaisonLisible.Echec(r, _localize("Bios.Retour.OublierEchec"), _localize);
    }

    public bool HasMemoryAb => MemoryAb is not null;

    public string LabelBiosReturnHeader => _localize("Scan.BiosReturn.Header");

    /// <summary>
    /// Cet hote sait-il prouver qu'un changement BIOS a pris ? Faux quand la couture n'est pas
    /// cablee : le bouton « emmene-moi dans le BIOS » ne s'affiche pas et aucun verdict n'est
    /// prononce.
    ///
    /// Expose parce que le contraire est invisible. Une couture optionnelle non passee donne un
    /// ScanViewModel parfaitement construit et parfaitement muet : c'est la forme exacte du
    /// defaut ou tout etait enregistre et rien n'etait resolu. Un test de composition peut
    /// desormais le constater au lieu de lire le fichier de cablage.
    /// </summary>
    public bool CanProveBiosChanges => _firmwareTrip is not null;

    /// <summary>Titre traduit de la section du guide BIOS personnalisé.</summary>
    public string LabelBiosGuideHeader => _localize("Scan.Bios.Guide.Header");

    /// <summary>Titre traduit et rassurant de l'échelle de secours obligatoire du guide (« si
    /// quelque chose tourne mal »), affiché dans un panneau calme, sans alarme.</summary>
    public string LabelBiosRecoveryHeader => _localize("Scan.Bios.Guide.RecoveryHeader");

    /// <summary>
    /// Remplit la liste d'attente « Le scan va examiner… ». Affiche tout de suite les libellés
    /// traduits avec un tiret en attente, puis détecte le matériel hors du thread UI et met les
    /// vrais modèles quand ils sont prêts. La vue peut l'appeler sans l'attendre, une fois la
    /// fenêtre affichée.
    /// </summary>
    public async Task LoadIdleComponentsAsync()
    {
        PopulateComponents(hw: null);

        // Lancée sans attente : une détection qui lève laisse les composants « — » (non lus), et l'exception est observée.
        var stockage = LireStockageAsync();
        try
        {
            var hwResult = await Task.Run(async () => await _detector.DetectAsync()).ConfigureAwait(true);
            await stockage.ConfigureAwait(true);
            if (State == ScanState.Idle) PopulateComponents(hwResult.IsOk ? hwResult.Value : null);
        }
        catch (Exception)
        {
            await stockage.ConfigureAwait(true);
            if (State == ScanState.Idle) PopulateComponents(hw: null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        // Les sondes du scan parlent a WMI, au registre, a des pilotes et a des
        // services : du code qui leve au lieu de refuser (un service arrete, une cle
        // absente, un pilote qui rend n'importe quoi). Sans cette garde, l'exception remonterait
        // jusqu'au filet fatal de App.xaml.cs et l'application se fermerait au milieu du scan,
        // c'est-a-dire au seul moment ou la personne attend quelque chose de nous.
        //
        // Un refus propre (`Result.Fail`) garde son message precis ; ceci ne rattrape que
        // l'imprevu, et le detail technique part au journal, pas a l'ecran.
        if (ConfierAUneInstanceElevee()) return;
        var numero = ++_numeroDeScan;
        try
        {
            await ScanCoeurAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Scan interrupted by an exception: {0}", ex);
            ErrorMessage = _localize("Scan.Error.Inattendu");
            State = ScanState.Failed;
        }
        finally
        {
            _etapeEnCours = 0;
            // Finie, la jauge reste pleine un instant ; interrompue, elle part tout de suite.
            if (State is ScanState.Failed) JaugeVisible = false;
            else _ = MasquerLaJaugeAsync(numero);
        }
    }

    private async Task ScanCoeurAsync()
    {
        ReinitialiserPourUnScan();
        PolesLus = 0;

        Etape(1, "Scan.Etape.Materiel");
        var stockage = LireStockageAsync();
        var hwResult = await _detector.DetectAsync();
        await stockage;
        if (!hwResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Detection");
            State = ScanState.Failed;
            return;
        }

        var hw = hwResult.Value!;
        _materielLu = hw;
        PopulateComponents(hw);
        // La version du pilote graphique, demandée au fabricant pendant le reste de l'analyse.
        _verificationPilote = _verifierPilote is { } verifier ? verifier(hw.Gpus) : null;

        Etape(2, "Scan.Etape.Reglages");
        var stResult = await _settings.ReadAsync(hw);
        if (!stResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Settings");
            State = ScanState.Failed;
            return;
        }

        var st = stResult.Value!;

        // Le BIOS LU remplace l'inference quand il est lisible et que Windows ne le
        // contredit pas. Sinon `st` reste tel quel : l'inference, avec ses reserves.
        if (_biosNvram is not null)
            st = DrDxt.Bios.NvramBiosSettingsReader.Appliquer(
                st, await _biosNvram.ReadAsync(hw.Board.Product, hw.Bios.Version));
        _reglagesBiosLus = st.BiosLu;
        // Les écarts à l'usine, lus dans le magasin d'usine du firmware ; null quand la carte n'est pas dans la table.
        _ecartsUsine = _biosNvram is null ? null : await _biosNvram.LireEcartsAsync(hw.Board.Product, hw.Bios.Version);
        _reglagesLus = st;
        try { RetirerLeviersVolatilsRendus(); }
        catch (Exception) { /* un journal non nettoyé ne fait pas échouer le scan */ }
        // Le docteur n'a pas de helper pour lire le BIOS ; il relit ce que le scan vient d'en lire. Réécrit à
        // Chaque scan, vide quand rien n'est lu : un relevé d'avant une mise à jour du BIOS ne survit pas.
        FichierBiosLu?.WriteAtomic(System.Text.Json.JsonSerializer.Serialize(
            new DrDxt.Detection.Cpu.ChaleurEnPartie.BiosLu(Horloge(), st.BiosLu ?? [])));

        // L'ordre des etapes ci-dessous est une contrainte, pas un hasard, et il a ete paye.
        // Le verdict de retour BIOS lit la reference d'avant le voyage. Le rafraichissement, lui,
        // juge une reference caduque des que la frequence a change (ce qui est exactement ce
        // qu'un passage XMP off -> on provoque) et la remplace. Rafraichir d'abord effacerait donc
        // la reference d'avant, et l'A/B comparerait l'apres a l'apres : « aucune difference
        // mesurable » annonce sur un vrai +79 %. Le verdict passe devant, et consomme la reference
        // avant que quoi que ce soit la remplace.
        // Verrouille par ScanMeasuresMemoryTests.La_reference_d_AVANT_le_BIOS_survit_au_scan_qui_la_relit.
        Etape(3, "Scan.Etape.Bios");
        var abMemoire = await PopulateBiosLoopAsync(st, hw);

        // Le reseau passe avant le banc processeur, qui sature tous les cœurs rapides pendant plus de
        // quarante secondes : mesurer la gigue apres lui attribuerait au reseau une instabilite que
        // le produit vient lui-meme de fabriquer.
        Etape(4, "Scan.Etape.ReseauProcesseur");
        await MesurerReseauEtProcesseurAsync();
        // Hors du bloc processeur, une panne du banc ne saute plus le relevé.
        try { await ReleverReglagesSuivisAsync(EtatsSuivis(_reglagesLus, _materielLu, LireInstantReplay?.Invoke(), _reglagesBiosLus), Horloge()); }
        catch (Exception) { /* un suivi raté ne fait pas échouer le scan */ }
        AllumerPole("Cpu");

        // La memoire remplace la reference : elle reste apres PopulateBiosLoopAsync, ci-dessus.
        Etape(5, "Scan.Etape.Memoire");
        st = await MesurerLaMemoireAsync(st, hw, abMemoire);
        AllumerPole("Ram");

        if (hw.Chassis is ChassisType.Laptop or ChassisType.Handheld)
            GatingNote = _localize("Scan.Gating.Note");

        Etape(6, "Scan.Etape.EnJeu");
        var (baseline, degraded) = await MesurerEnJeuAsync();
        AllumerPole("Gpu");
        ApplyBaseline(baseline);
        JournaliserLaMesure(baseline);
        PublierLesLibellesDeMesure(baseline);

        Etape(7, "Scan.Etape.Analyse");
        var scoreResult = _scorer.Score(hw, st, baseline);
        if (!scoreResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Analyse");
            State = ScanState.Failed;
            return;
        }

        var scanResult = scoreResult.Value!;
        PublierLeResultat(scanResult, degraded);
        // Le goulot mesure entre dans `st` ici, et ce `st`-la sert aussi au conseiller BIOS, plus bas.
        st = PeuplerLesRecommandations(hw, st, baseline, scanResult);

        // Affiche les constats firmware du docteur BIOS (XMP/EXPO coupé est le plus gros gain réel,
        // et il vit dans le BIOS, pas dans un levier logiciel). Guidage seul : jamais ajouté au lot
        // appliqué, le joueur les change lui-même dans le BIOS. Puis construit le guide personnalisé
        // (le « comment » exact) à partir des mêmes constats bruts.
#if DEBUG
        var biosFindings = PopulateBiosFindings(hw, SimulerXmpOffPourLeBios ? st with { XmpExpo = TriState.Off } : st);
#else
        var biosFindings = PopulateBiosFindings(hw, st);
#endif
        PopulateBiosGuide(hw, biosFindings, st);

        // L'audit de sante, pure lecture (registre, journal d'evenements, bcdedit, WMI). Il vit
        // ici et pas dans le banc de mesure parce que la frontiere est le cout : ces familles ne
        // demandent qu'une lecture, la memoire et le reseau exigent une mesure qui dure.
        Etape(8, "Scan.Etape.Audit");
        await PopulateRepairAuditAsync();
        // L'audit de santé lit ensemble le stockage (usure, SMART) et Windows : leurs deux pôles s'allument ensemble.
        AllumerPole("Ssd", "Windows", "Latency", "Network", "Firmware");

        ConstruireCarte();
        if (_etapeEnCours > 0) _dureesMesurees[_etapeEnCours - 1] = _chronoEtape.Elapsed.TotalSeconds;
        if (_dureesMesurees.All(d => d > 0)) EnregistrerDureesDesEtapes?.Invoke((double[])_dureesMesurees.Clone());
        await JugerCeQuiResteAPoserAsync().ConfigureAwait(true);
        ProgressionDuScan = 1;
        PlafondDuScan = 1;
        VitesseDuScan = 0;

        State = degraded ? ScanState.Degraded
              : DejaReglee(scanResult) ? ScanState.AlreadyOptimal
              : ScanState.Done;
    }

    private void ReinitialiserPourUnScan()
    {
        _posesDepuisLeScan.Clear();
        State = ScanState.Scanning;
        Findings.Clear();
        ProvenProblemCount = 0;
        // Le verdict du scan précédent ne reste pas au-dessus d'un scan qui échoue.
        ProblemSummary = string.Empty;
        SubtitleText = string.Empty;
        DateDuScan = null;
        ErrorMessage = null;
        GatingNote = null;
        DegradedNote = null;
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
        ResultatOptimisation = null;
        AvgFpsDisplay = Dash;
        P1LowDisplay = Dash;
        MarginDisplay = Dash;
        Recommendations.Clear();
        Domaines.Clear();
        PointsAFaire.Clear();
        DomaineSelectionne = null;
        OnPropertyChanged(nameof(ADesDomaines));
        AnnoncerSatellites();
        BiosFindings.Clear();
        RepairFindings.Clear();
        RepairSummary = null;
        BiosGuide = null;
        BiosLoop = null;
        MemoryAb = null;
        _consumption = null;
        OnPropertyChanged(nameof(ApercuOptimisation));
        _scannedBaseline = null;
        _goulotScan = null;
        _pressionThreads = GameThreadPressure.Unknown;
        _constatPertesReseau = null;
        _constatBufferbloat = null;
        _constatDpc = null;
        _constatRoute = null;
        _constatMemoireEnPartie = null;
    }

    // Le reseau, puis le processeur : une panne de l'un ou de l'autre ne fait jamais echouer le scan.
    private async Task MesurerReseauEtProcesseurAsync()
    {
        if (_cpuBaseline is not null && _machineId is not null)
        {
            try
            {
                var banc = _cpuBaseline.MesureraPourScan(_machineId(), _gameLocator.JeuOuvert(), ReglageDePuissanceLu(_reglagesBiosLus));
                Prevoir(4, DureeReseau + (banc ? DureeBancProcesseur : 0));
            }
            catch (Exception) { /* la jauge garde sa prévision */ }
        }
        // D'ou vient l'instabilite du reseau.
        //
        // Ce bloc doit rester avant le banc CPU, juste en dessous. Le banc sature tous les
        // cœurs rapides pendant plus de quarante secondes : mesurer la gigue ICMP apres lui, ce
        // serait attribuer au reseau une instabilite que le produit vient lui-meme de fabriquer.
        // Meme garde que partout ailleurs : une panne de ce cote ne fait pas echouer le scan.
        if (_attributionReseau is not null)
        {
            try
            {
                NetworkPathLabel = await _attributionReseau(CancellationToken.None);
            }
            catch
            {
                NetworkPathLabel = null;
            }
        }

        if (_cpuBaseline is not null && _machineId is not null)
        {
            try
            {
                // Hors du thread UI : la premiere mesure charge tous les cœurs rapides pendant plus
                // de quarante secondes. Meme motif que le compte de canaux memoire, et meme garde :
                // une panne de ce cote ne doit jamais faire echouer le scan.
                var jeuEnCours = _gameLocator.JeuOuvert();
                // Le réglage de puissance lu voyage avec la mesure ; changé depuis, on remesure.
                var reglage = ReglageDePuissanceLu(_reglagesBiosLus);
                var refAvant = _cpuBaseline.Reference(_machineId());
                var deficit = await Task.Run(() => _cpuBaseline.DeficitPourScan(_machineId(), jeuEnCours, reglage));
                NoterChangementBios(refAvant?.ReglageBios, _cpuBaseline.Reference(_machineId())?.ReglageBios, refAvant?.TakenUtc);

                // La référence processeur de la machine de référence (11/09 17:46, ±7,4 %) précède de
                // cinq minutes une capture de Warzone : prise pendant la partie, comme celle de la mémoire. La
                // réserve voyage avec le verdict : le rapport envoyé au support la porte comme l'écran.
                if (deficit?.Reference is { } prise)
                    deficit = deficit with { Reserve = MentionPriseEnJeu("Cpu.PriseEnJeu", prise.TakenUtc, prise.JeuFermeVerifie) };
                _dernierDeficitCpu = deficit;
                CpuSustainedLabel = PhraseProcesseur(deficit, _reglagesBiosLus);
                CpuSauteJeu = jeuEnCours && deficit is null;
                ErreurRemesureCpu = null;
            }
            catch
            {
                CpuSustainedLabel = null;
            }
        }
    }

    // La mesure memoire. Sans elle, MemoryBench serait un instrument que le produit n'appelle
    // jamais. Elle ne tourne qu'au premier scan (decision produit ; le service relit ensuite la
    // reference enregistree) et sur un fil de travail, parce que parcourir 384 Mo bloquerait
    // l'interface. Une mesure rejetee rend 0 : le scan continue sans rien affirmer.
    // Elle ne tourne pas apres un A/B refuse : l'ecran vient de dire au client « relance au repos »,
    // et le marqueur BIOS est deja consomme, donc la seule chose qui rend ce conseil applicable est
    // la reference d'avant. La rafraichir rendrait la comparaison impossible.
    private async Task<SettingsState> MesurerLaMemoireAsync(SettingsState st, HardwareInventory hw, MemoryAbOutcome abMemoire)
    {
        if (abMemoire != MemoryAbOutcome.Refusee && _memoryBaseline is not null && _machineId is not null)
        {
            try
            {
                var jeuEnCours = _gameLocator.JeuOuvert();
                Prevoir(5, _memoryBaseline.MesureraPourScan(_machineId(), hw.Memory, jeuEnCours) ? DureeBancMemoire : DureeSansBanc);
                var canaux = await Task.Run(
                    () => _memoryBaseline.ProvenChannelsForScan(_machineId(), hw.Memory, jeuEnCours));
                st = st with { MemoryProvenChannels = canaux };
                st = st with
                {
                    MemoryUnSeulCanalMesure = _memoryBaseline.MesureCourante(_machineId(), hw.Memory) is { } mesuree
                        && DrDxt.Measurement.Memory.MemoryInference.UnSeulCanalProbable(mesuree.Measurement.BandwidthMultiFilGBs, hw.Memory),
                };

                // La mesure vient d'etre prise ou relue juste au-dessus : on relit le magasin, on
                // ne remesure pas. Rien d'enregistre (mesure rejetee, banc indisponible) => la
                // ligne n'existe pas, plutot qu'un chiffre sorti de nulle part.
                //
                // Et une reference perimee ne s'affiche pas non plus : le magasin garde
                // volontairement la mesure d'avant quand la configuration memoire a change, pour
                // que l'A/B reste possible. La publier comme « ce que ta memoire rend » decrirait
                // une machine qui n'existe plus.
                MemoryMeasuredLabel = LigneMemoire(hw.Memory);
                MemoireSauteeJeu = jeuEnCours && MemoryMeasuredLabel is null;
                ErreurRemesureMemoire = null;
            }
            catch
            {
                // Ce bloc n'avait aucune garde : une panne du cote memoire faisait echouer le
                // scan entier, et le client voyait « echec du scan » pour une mesure de confort.
                // Ne pas savoir combien de canaux sont prouves laisse simplement le compte a zero,
                // c'est-a-dire rien d'affirme, ce que le reste du produit sait deja traiter.
            }
        }
        return st;
    }

    private async Task<(BenchmarkResult? Baseline, bool Degraded)> MesurerEnJeuAsync()
    {
        // Mesure générique en jeu : trouve le jeu lancé (n'importe quel titre, sans liste) et mesure
        // celui-là. Pas de jeu → PID 0 → le runner échoue proprement → scan honnête de la config seule
        // (mode dégradé), jamais un chiffre inventé. Gardé pour la remesure d'Apply, pour que l'avant et
        // l'après portent sur le même jeu.
        _gamePid = _gameLocator.LocateRunningGamePid() ?? 0;
        var jeuReduit = _gamePid == 0 && _gameLocator.JeuOuvert();
        if (jeuReduit) _gamePid = await AttendreLeJeuAsync() ?? 0;
        // Les threads du jeu, observes pendant la meme fenetre que les frames (jamais avant :
        // une consommation prise a un autre moment decrirait un autre moment du jeu).
        var pidObserve = _gamePid;
        var observation = pidObserve > 0 && _observerThreads is { } observer
            ? Task.Run(() => observer(pidObserve, DureeCaptureEnJeu))
            : Task.FromResult<DrDxt.Detection.Cpu.ProcessCpuSample?>(null);
        // Meme fenetre pour la carte reseau : les pertes d'une partie, pas celles d'un bureau au repos.
        var pertes = pidObserve > 0 && _mesurerPertesReseau is { } mesurerPertes
            ? Task.Run(() => mesurerPertes(DureeCaptureEnJeu))
            : Task.FromResult<RepairFinding?>(null);
        // Et pour les pilotes : un pic DPC sous charge est une micro-saccade, au repos il ne prouve rien.
        var bloat = pidObserve > 0 && _mesurerBufferbloat is { } mesurerBloat
            ? Task.Run(() => mesurerBloat(DureeCaptureEnJeu))
            : Task.FromResult<DrDxt.Measurement.Network.VerdictBufferbloat?>(null);
        var exeObserve = pidObserve > 0 ? NomExeDuProcessus(pidObserve) : null;
        // La route vers le serveur que le jeu contacte, pour dire si un relais payant servirait.
        var route = pidObserve > 0 && _mesurerRoute is { } mesurerRoute
            ? mesurerRoute(pidObserve, DureeCaptureEnJeu)
            : Task.FromResult<RepairFinding?>(null);
        var memoireLibre = pidObserve > 0 && _mesurerMemoireLibre is { } mesurerMemoire
            ? mesurerMemoire(DureeCaptureEnJeu)
            : Task.FromResult<ulong?>(null);
        var dpc = pidObserve > 0 && _mesurerDpc is { } mesurerDpc
            ? Task.Run(() => mesurerDpc(DureeCaptureEnJeu))
            : Task.FromResult<RepairFinding?>(null);
        // Ce qui tourne a cote, pendant la meme fenetre. Un scan a deja affiche 164 FPS
        // pendant qu'on regardait l'ecran de DrDXT ; le meme soir, en partie avec nos compilations,
        // 79,5. Le chiffre seul ne veut rien dire : l'ecran dira ce qu'il y avait autour.
        var voisins = Task.Run(() => _voisins.Lire(pidObserve > 0 ? pidObserve : null, CancellationToken.None));
        // Un joueur qui revient sur DrDXT avant la fin réduit le jeu en pleine capture. Les images
        // d'un jeu réduit ne décrivent pas sa partie : le jeu est vérifié chaque seconde, et s'il a quitté
        // l'écran, aucun chiffre n'est donné.
        var surveillance = _gamePid > 0 ? new SurveillanceDuJeu() : null;
        if (surveillance is not null) _ = SurveillerLeJeuAsync(_gamePid, surveillance, DureeCaptureEnJeu);
        var benchResult = await _benchmark.RunAsync(_gamePid, DureeCaptureEnJeu, 5, CancellationToken.None);
        var jeuQuitte = false;
        if (surveillance is not null) { surveillance.Fini = true; jeuQuitte = surveillance.Quitte; }
        DrDxt.Detection.Cpu.ProcessCpuSample? threads;
        try { threads = await observation.ConfigureAwait(true); }
        catch { threads = null; }   // une observation ratee n'est pas « aucune pression »
        _pressionThreads = threads is null
            ? GameThreadPressure.Unknown
            : PressionThreads.Classer(threads.ThreadsChauds.FirstOrDefault()?.PartDUnThreadLogique, threads.ThreadsVus);
        try { _constatPertesReseau = await pertes.ConfigureAwait(true); }
        catch { _constatPertesReseau = null; }   // une mesure ratee n'est pas « aucune perte »
        try
        {
            var verdict = await bloat.ConfigureAwait(true);
            _constatBufferbloat = verdict is null
                ? null
                : DrDxt.Measurement.Network.Bufferbloat.Constat(verdict, DureeCaptureEnJeu);
            RetenirMesureEnvoi(verdict is { Detecte: true } ? verdict.PlafondConseilleBps : 0, exeObserve);
        }
        catch { _constatBufferbloat = null; }
        try { _constatDpc = await dpc.ConfigureAwait(true); }
        catch { _constatDpc = null; }
        try { _constatRoute = await route.ConfigureAwait(true); }
        catch { _constatRoute = null; }
        try
        {
            _constatMemoireEnPartie = DrDxt.Measurement.Memory.MemoireEnPartie.Juger(
                await memoireLibre.ConfigureAwait(true), benchResult.IsOk ? benchResult.Value!.FrameTimesMs : null);
        }
        catch { _constatMemoireEnPartie = null; }
        try { PeuplerLesVoisins(await voisins.ConfigureAwait(true)); }
        catch { PeuplerLesVoisins(null); }
        var degraded = !benchResult.IsOk || jeuQuitte;
        // « tu as quitté ton jeu » peut s'afficher sans aucun jeu ouvert, quand une autre fenêtre a été prise pour le
        // jeu. La phrase nomme le programme retenu : un faux positif se voit tout de suite, pour le joueur comme pour le support.
        if (jeuQuitte)
            DegradedNote = exeObserve is { } retenu
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Scan.Degraded.JeuQuitteNomme"),
                    DureeCaptureEnJeu.TotalSeconds, System.IO.Path.GetFileNameWithoutExtension(retenu))
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _localize("Scan.Degraded.JeuQuitte"), DureeCaptureEnJeu.TotalSeconds);
        else if (degraded)
            // La réparation du service déjà refusée dans la session se dit telle quelle : la phrase générique du service
            // ferait chercher une panne alors qu'il suffit de relancer DrDXT.
            DegradedNote = benchResult.Error is { Code: ErrorCodes.PmonUnavailable } e && e.Message == Docteur.T("SH.DejaRefusee")
                ? e.Message
                : _localize(jeuReduit && _gamePid == 0
                    ? "Scan.Degraded.JeuReduit"
                    : CleDeCouvertureManquante(benchResult.Error!.Value.Code));

        BenchmarkResult? baseline = benchResult.IsOk && !jeuQuitte ? benchResult.Value! with { ProcessusMesure = exeObserve } : null;
        return (baseline, degraded);
    }

    private void PublierLesLibellesDeMesure(BenchmarkResult? baseline)
    {
        // La cadence. Mesuree a chaque scan et lue par le seul classifieur de goulot jusqu'ici.
        //
        // La garde se reutilise : sous vingt echantillons un p95 se confond avec le maximum ;
        // ce n'est pas de la prudence, c'est de l'arithmetique (1/20 = 5 %). Sans elle, une capture
        // courte publierait un « ecart de 1 187 ms », chiffre juste et vide de sens.
        //
        // Et elle compte les echantillons d'animation, pas les trames. Le p95 repose sur une
        // serie a part : une passe dont l'erreur d'animation manque ne contribue pas au percentile,
        // et le nombre de trames n'en sait rien. Compter les trames validait un p95 tire de huit
        // points en en lisant mille. Compte inconnu (mesure d'avant ce champ) ⇒ on se tait.
        CadenceLabel = DrDxt.Measurement.FrametimeStatistics.CadenceP95(baseline) is { } ecart
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.Cadence.Measured"), ecart)
            : null;

        // L'attente d'affichage. Le compte vient de sa serie a elle, publiee par le banc avec le
        // meme filtre que le percentile, des le depart. Compte inconnu ⇒ silence.
        PresentStallLabel = DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline) is { } attente
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.PresentStall.Measured"), attente)
            : null;

        // La saccade. Le p95 des frametimes vient de la meme liste que le compte ci-dessous, avec
        // le meme filtre (`Clean`) : la valeur et la garde ne peuvent pas porter sur deux
        // populations differentes.
        StutterLabel = DrDxt.Measurement.FrametimeStatistics.SaccadeP95(baseline) is { } lentes
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.Stutter.Measured"), lentes)
            : null;

        // Le plafond du gain. L'occupation GPU vient de cette mesure, jamais d'une constante ; sans
        // trames ni GPU-busy, la ligne n'existe pas plutot que d'afficher une borne inventee.
        //
        // Le rappel de scene est colle a la borne et n'en part jamais : une borne unique lue
        // sans lui deviendrait « ce que je peux gagner », c'est-a-dire une promesse.
        // Le rappel de scene ne porte aucun chiffre. Les series de reference vont de
        // +5,3 % a +16,3 %, sur une machine et un jeu. Les afficher chez quelqu'un d'autre
        // ferait passer la mesure d'un tiers pour la sienne.
        GpuCeilingLabel = DrDxt.Measurement.PlafondGpu.PhraseDuScan(baseline?.GpuBusyPercent);

        // La courbe de cette mesure et sa médiane.
        CourbeDeLaMesure = CourbeDesTempsDeTrame.Construire(baseline?.FrameTimesMs);
    }

    private void PublierLeResultat(ScanResult scanResult, bool degraded)
    {
        Coverage = scanResult.Coverage;
        // Le rapport de diagnostic decrit le dernier scan. Le garder ici evite de le
        // recalculer au moment de l'export : un rapport qui relance un scan decrirait une autre
        // machine que celle dont le client parle.
        _lastScan = scanResult;
        OnPropertyChanged(nameof(SousTensionPrerequisVisible));
        OnPropertyChanged(nameof(SousTensionPossible));
        // Le registre : on compte les leviers lus et trouves sous-optimaux. Le grand livre
        // est la source, parce qu'il ne contient que les leviers reellement notes, pas les
        // lignes d'information ni les guidages.
        ProvenProblemCount = scanResult.Ledger?.Count(e => e.State == LeverState.Suboptimal) ?? 0;

        foreach (var f in scanResult.Findings)
            Findings.Add(new FindingViewModel(f, _localize));

        PopulateNarrative(degraded);
    }

    private SettingsState PeuplerLesRecommandations(HardwareInventory hw, SettingsState st, BenchmarkResult? baseline, ScanResult scanResult)
    {
        // Consomme la recommandation du profil de la puce : compose le plan issu du profil, remplace
        // et borne les cibles GPU (IGpuTuningTargetSource), et affiche la preuve honnête de chaque
        // choix. Gardé pour qu'Apply exécute ce plan et remesure contre la même base mesurée.
        _scannedBaseline = baseline;
        _goulotScan = scanResult.Bottleneck;
        // Le goulot mesure entre dans l'etat avant la selection : un levier CPU ne
        // s'arme que si le processeur limite ce jeu. Un verdict provisoire (confiance basse)
        // n'arme rien : trop peu de mesure pour justifier une ecriture sur la machine.
        st = st with
        {
            GoulotMesure = scanResult.Bottleneck is { Provisional: false } goulot ? goulot.State : null,
            // Les heures des parties relevées, pour les heures d'activité de Windows Update. Sans relevé
            // branché : null (non lu), jamais « aucune partie ».
            HeuresDeJeu = MemoireParties?.Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        };
        _consumption = _profileConsumer.Consume(hw, st);
        OnPropertyChanged(nameof(ApercuOptimisation));
        Recommendations.Clear();
        Recommendations.CollectionChanged -= NotifierAucuneRecommandation;
        Recommendations.CollectionChanged += NotifierAucuneRecommandation;
        // N'est recommande que ce qui sera reellement applique. Scan reel de la machine
        // de reference : 10 lignes « optimisation recommandee », dont 5 inapplicables selon le
        // catalogue et 2 reglages GPU fermes par decision, qui ne seront jamais ecrits.
        foreach (var item in _consumption.Items.Where(i => i.InExecutedPlan
                     || (i.Applicable && i.Intent == DrDxt.Profiles.ProfileIntent.GpuTune)))
            Recommendations.Add(item);
        return st;
    }

    /// <summary>
    /// « Deja bien reglee » se decide sur les faits de lecture : rien a corriger
    /// (scanner, plan, GPU, BIOS, sante) et rien de non lu. Un audit absent ou un conseiller BIOS en
    /// panne n'ont rien lu : jamais « deja reglee » sur ce qu'on n'a pas vu.
    /// </summary>
    // Le scanner rend `Unknown` sans constat pour ce qu'il n'a pas pu lire
    // (XMP, rafraichissement, pagefile, MMCSS) : « aucun constat » n'est pas « tout lu ». Sa
    // couverture le dit ; une couverture absente n'a rien affirme.
    // Rien à corriger = aucun constat au-dessus d'Info, et non `Findings.Count == 0` : un constat Info
    // ne demande rien. Ce qui n'a pas ete lu reste la clause de couverture.
    // Simplification voulue : pas de clause « aucun levier sous-optimal au registre ». Chaque Suboptimal de
    // LeverCatalog porte un constat Minor/Major, la mutation l'a montree redondante (verte, 589/589).
    private bool DejaReglee(ScanResult scan) =>
        scan.Findings.All(f => f.Severity == Severity.Info)
        && scan.Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 }
        // Un plan GPU ne compte pas : sinon toute machine NVIDIA de bureau (leviers GPU applicables) ne
        // pouvait jamais etre « deja bien reglee ». Un reglage de la carte n'est pas une correction, c'est un essai mesure.
        && _consumption is { Plan.TweakIds.Count: 0 }
        && _biosLu && BiosFindings.Count == 0
        && _derniereSante is { Attention.Count: 0, NonLus.Count: 0 };

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Dash : value.Trim();

    private static string Format(string template, int n) =>
        template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, n)
            : $"{n} {template}";
}
