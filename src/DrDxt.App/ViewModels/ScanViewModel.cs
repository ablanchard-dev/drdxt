using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.App.ViewModels;

public sealed partial class ScanViewModel : ObservableObject
{
    private const string Dash = "—";

    // Restore-point gate (§16): a blocked apply surfaces an explicit, localized message —
    // never a silent skip. The key chosen depends on WHY apply is blocked.
    private const string KeyRestoreBlocked = "Apply.RestoreRequired.Blocked";
    private const string KeyApplyFailed = "Apply.Failed";
    private const string KeyNothingToApply = "Apply.NothingApplicable";
    private const string KeySousGarde = "Apply.SousGarde";
    private const string KeyKeptOriginNotSigned = "Optimize.KeptOriginNotSigned";

    // 23/09/2026 : DrDXT est gratuit (GPL-3.0). L'ancien gate payant de l'application prouvée a été retiré ;
    // rien ne mène plus à un achat.

    // The persistence moat (§9.12): after a validated apply the App commits a signed
    // desired-state manifest so the reconciler can re-assert it at boot. A commit failure
    // is NON-FATAL (the tweaks are already applied + measured) — it surfaces this signal,
    // not a blocking error.
    private const string KeyMoatUnarmed = "Apply.MoatUnarmed";
    // 11/09/2026 — le manifeste signe illisible : refus nomme, puis reprise explicite.
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
    /// scan isole ne voit pas -- chaque mesure prise seule parait normale.</summary>
    private readonly DrDxt.Measurement.PerformanceJournalStore? _performanceJournal;
    private readonly IScanScorer _scorer;
    private readonly IStorageDetector _storage;
    private readonly IBiosAdvisor _biosAdvisor;
    // L'audit de sante (pure lecture). Une COUTURE, pas le service concret : `RepairAuditService`
    // lit le registre, le journal d'evenements et `bcdedit` en dur, donc l'injecter tel quel
    // rendrait tout test de cet ecran dependant de la machine qui l'execute. Meme idiome que
    // `_machineId` / `_writeReport`. null => pas de section, et le scan ne perd rien.
    private readonly Func<IReadOnlyList<RepairFinding>>? _repairAudit;
    private readonly IGuideGenerator _guideGenerator;
    private readonly ISafetyManager _safety;
    private readonly ITweakEngine _engine;
    private readonly IScanProfileConsumer _profileConsumer;
    private readonly IGpuTuningApplyStep _gpuTuningApplyStep;
    private readonly INavigationService _navigation;
    private readonly Func<DashboardViewModel> _dashboardFactory;
    private readonly IDesiredStateStore _desiredStateStore;
    private readonly AppliedTweakManifestMapper _manifestMapper;
    private readonly Func<string, string> _localize;
    // Generic game detection (no per-game list): finds the running game to measure in-game.
    private readonly DrDxt.App.Platform.IGameLocator _gameLocator;
    // L0.2 — la boucle fermee du BIOS : poser le marqueur avant le voyage, relire au retour.
    // Optionnelle : null = la couture n'est pas la, le bouton « ouvrir le BIOS » ne s'affiche
    // pas (CanOpenFirmwareDirectly reste faux) et aucun verdict n'est prononce. C'est un
    // manque VISIBLE, pas un succes silencieux.
    private readonly DrDxt.App.Platform.IFirmwareTrip? _firmwareTrip;
    // D7 / gate G6 — de quoi fabriquer le rapport que la personne colle dans son ticket : la
    // version du produit et l'ecriture du fichier. Optionnelles : sans elles le bouton disparait
    // plutot que de ne rien faire.
    private readonly Func<string, string, string>? _writeReport;
    private readonly string _appVersion;

    // Captured from the last successful scan so Apply runs the profile-driven plan + passes
    // the measured baseline to the engine for the re-bench / auto-revert verdict.
    private ScanProfileConsumption? _consumption;
    private BenchmarkResult? _scannedBaseline;
    // The game PID measured this scan (0 = no game running → config-only). Reused by Apply's re-bench.
    private int _gamePid;
    private readonly DrDxt.App.Platform.ILecteurDeVoisins _voisins;
    // Lot 1 « preuve du besoin » : le dernier tri de sante, le meme que l'ecran, pour que le rapport
    // exporte ce que le client a vu. null = audit non lance.
    private RepairTriage? _derniereSante;
    // Le conseiller BIOS a-t-il REPONDU a ce scan ? PopulateBiosFindings rend null pour « rien a
    // conseiller » comme pour une panne : seul ce drapeau les separe (DejaReglee le lit).
    private bool _biosLu;
    // L8 b — le goulot mesure par le scan : le diagnostic de l'Optimiser part de lui.
    private BottleneckVerdict? _goulotScan;
    // L8 b — un descripteur devient un candidat mesurable (journalise avant d'ecrire), et ce que la
    // stabilite interdit. null => aucun levier a chaud ne part en session / stabilite non lue.
    private readonly Func<CandidateDescriptor, IAutopilotCandidate?>? _creerCandidat;
    private readonly Func<(InstabiliteGpuObservee? Gpu, IReadOnlyList<ArretClasse>? Arrets)>? _lireStabilite;
    private readonly Func<string, bool>? _lancerInstalleur;
    // Phase 18 — ce que les threads du jeu ont consomme pendant la mesure du scan. null = pas observe.
    private readonly Func<int, TimeSpan, DrDxt.Detection.Cpu.ProcessCpuSample?>? _observerThreads;
    private GameThreadPressure _pressionThreads = GameThreadPressure.Unknown;
    // Phase 18 — les pertes a l'interface reseau pendant la meme fenetre de jeu. null = pas mesure.
    private readonly Func<TimeSpan, RepairFinding?>? _mesurerPertesReseau;
    private RepairFinding? _constatPertesReseau;
    // 24/09/2026 — le ping quand la machine envoie, pendant la meme fenetre de jeu. null = pas mesure.
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

    // Phase 18 — les DPC/ISR des pilotes pendant la meme fenetre de jeu. null = pas mesure.
    private readonly Func<TimeSpan, RepairFinding?>? _mesurerDpc;
    private RepairFinding? _constatDpc;
    // Chaque candidat juge laisse une ligne : le corpus qui tranchera un jour « le p95 seul suffit-il ».
    private readonly DrDxt.Application.Autopilot.JournalDesCandidats? _journalCandidats;

    private readonly DrDxt.Bios.NvramBiosSettingsReader? _biosNvram;
    // 24/09/2026 — ce que le scan a LU dans le BIOS, affiché dans son domaine (vu absent sur la photo).
    private IReadOnlyList<BiosSettingReading>? _reglagesBiosLus;

    public ScanViewModel(
        IHardwareDetector detector,
        ISettingsStateReader settings,
        IBenchmarkRunner benchmark,
        IScanScorer scorer,
        IStorageDetector storage,
        ISafetyManager safety,
        ITweakEngine engine,
        IScanProfileConsumer profileConsumer,
        IGpuTuningApplyStep gpuTuningApplyStep,
        INavigationService navigation,
        Func<DashboardViewModel> dashboardFactory,
        IDesiredStateStore desiredStateStore,
        AppliedTweakManifestMapper manifestMapper,
        IBiosAdvisor biosAdvisor,
        IGuideGenerator guideGenerator,
        Func<string, string> localize,
        // Optional/defaulted so existing (test) constructions stay source-compatible; DI passes the
        // real Win32 locator. Null → NullGameLocator → no game found → honest config-only scan.
        DrDxt.App.Platform.IGameLocator? gameLocator = null,
        // Optionnel/en queue, comme gameLocator, pour que toutes les constructions existantes
        // (tests compris) restent valides. La DI passe la vraie couture.
        DrDxt.App.Platform.IFirmwareTrip? firmwareTrip = null,
        // (nom de fichier, contenu) -> chemin ecrit. Une couture : les tests n'ecrivent rien sur
        // le disque, et le VM ne connait pas l'arborescence de Windows.
        Func<string, string, string>? writeReport = null,
        string? appVersion = null,
        // LM — le banc memoire. Optionnels/en queue comme les precedents, pour que toutes les
        // constructions existantes restent valides. null => le scan ne mesure pas et n'affirme
        // rien de mesure sur la memoire ; la DI passe le vrai service.
        DrDxt.Measurement.Memory.MemoryBaselineService? memoryBaseline = null,
        Func<string>? machineId = null,
        DrDxt.Measurement.PerformanceJournalStore? performanceJournal = null,
        // L'audit de sante. Optionnel/en queue comme les precedents : toutes les constructions
        // existantes restent valides. null => aucune section d'audit, et surtout aucune phrase
        // affirmant que la machine va bien — un audit absent ne se raconte pas comme un audit vide.
        Func<IReadOnlyList<RepairFinding>>? repairAudit = null,
        DrDxt.Measurement.Cpu.CpuBaselineService? cpuBaseline = null,
        Func<IReadOnlyList<RepairFinding>, DrDxt.Infrastructure.Repair.JournalDeSante?>? journalSante = null,
        // L'ATTRIBUTION reseau. Optionnelle/en queue comme les precedentes : toutes les
        // constructions existantes restent valides. null => le scan ne dit rien du reseau, ce qui
        // est exactement ce qu'il faisait avant. Un faux compteur suffit a la tester d'ici.
        Func<CancellationToken, Task<string?>>? attributionReseau = null,
        // L8 b — le cerveau de l'Optimiser. Optionnels/en queue comme les precedents.
        Func<CandidateDescriptor, IAutopilotCandidate?>? creerCandidat = null,
        Func<(InstabiliteGpuObservee? Gpu, IReadOnlyList<ArretClasse>? Arrets)>? lireStabilite = null,
        // Phase 18 — lance l'installeur de mise a jour. null = ShellExecute (UAC par l'installeur).
        Func<string, bool>? lancerInstalleur = null,
        // Phase 18 — observe les threads du jeu (GetThreadTimes, sans elevation). null = pas observe.
        Func<int, TimeSpan, DrDxt.Detection.Cpu.ProcessCpuSample?>? observerThreads = null,
        // Phase 18 — mesure les pertes a l'interface pendant une fenetre. null = pas mesure.
        Func<TimeSpan, RepairFinding?>? mesurerPertesReseau = null,
        // Phase 18 — mesure les DPC/ISR des pilotes pendant une fenetre. null = pas mesure.
        Func<TimeSpan, RepairFinding?>? mesurerDpc = null,
        // Le reservoir des candidats juges par l'Optimiser. null = rien n'est consigne.
        DrDxt.Application.Autopilot.JournalDesCandidats? journalCandidats = null,
        // 11/09/2026 — ce qui tourne A cote du jeu pendant la mesure (demande produit : un scan a
        // affiche 164 FPS pendant qu'il regardait DrDXT, jeu en arriere-plan). null = on ne regarde
        // pas, et la liste reste vide — jamais « rien ne tournait ».
        DrDxt.App.Platform.ILecteurDeVoisins? voisins = null,
        // 16/09/2026 — demande produit : relancer l'appli elevee pour lire ce qui exige
        // l'administrateur. null = droits inconnus ou relance indisponible : aucun bouton.
        bool? estEleve = null,
        Func<bool>? relancerEnAdministrateur = null,
        // 24/09/2026 — lit les reglages BIOS dans la NVRAM (cartes de la matrice). null = on garde
        // l'inference, rien n'est affirme comme « lu dans le BIOS ».
        DrDxt.Bios.NvramBiosSettingsReader? biosNvram = null,
        Func<TimeSpan, DrDxt.Measurement.Network.VerdictBufferbloat?>? mesurerBufferbloat = null,
        // Le helper eleve qui posera le plafond. null = mode indisponible.
        IElevatedClient? helper = null,
        // 24/09/2026 — le journal des leviers gardes et le lecteur qui les relit. null = on ne remarque rien.
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null,
        Func<IReadOnlyList<RepairFinding>>? lireReglagesJeu = null,
        // 24/09/2026 — le lien PCIe de la carte graphique (NVML). null ou lecture ratée = « non lu », comme avant.
        Func<CarteDeLaMachine.LienPcie?>? lirePcie = null,
        // 03/10/2026 (P3.1c) — la route vers le serveur de la partie, pendant la même fenêtre. null = on ne regarde pas.
        Func<int, TimeSpan, Task<RepairFinding?>>? mesurerRoute = null,
        // 03/10/2026 (P3.3) — le plus bas de la mémoire libre pendant la fenêtre. null = on ne regarde pas.
        Func<TimeSpan, Task<ulong?>>? mesurerMemoireLibre = null,
        // 03/10/2026 (tour G) — la dernière version publiée du pilote, comparée à celle installée. null = on ne regarde pas.
        Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? verifierPilote = null,
        // 07/10/2026 (T1) — écrit la machine lue (inventaire, réglages, santé) au format des fixtures, anonymisée, et rend
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
        _gpuTuningApplyStep = gpuTuningApplyStep;
        _navigation = navigation;
        _dashboardFactory = dashboardFactory;
        _desiredStateStore = desiredStateStore;
        _manifestMapper = manifestMapper;
        _localize = localize;
        _gameLocator = gameLocator ?? new DrDxt.App.Platform.NullGameLocator();
        _firmwareTrip = firmwareTrip;
        _writeReport = writeReport;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "(version inconnue)" : appVersion;

        Features = new ObservableCollection<FeatureItem>
        {
            new("Diagnostic", _localize("UI.Feature.Diagnostic.Title"), _localize("UI.Feature.Diagnostic.Body")),
            new("Gaming", _localize("UI.Feature.Gaming.Title"), _localize("UI.Feature.Gaming.Body")),
            new("Security", _localize("UI.Feature.Security.Title"), _localize("UI.Feature.Security.Body")),
            new("Report", _localize("UI.Feature.Report.Title"), _localize("UI.Feature.Report.Body")),
        };

        ChoixDeScene = new ChoixScene[]
        {
            new(SceneRepresentativity.Unknown, _localize("Scan.Scene.Unknown")),
            new(SceneRepresentativity.Gameplay, _localize("Scan.Scene.Gameplay")),
            new(SceneRepresentativity.Entrainement, _localize("Scan.Scene.Training")),
            new(SceneRepresentativity.MenuLobby, _localize("Scan.Scene.Menu")),
            // 23/09/2026 — le premier scan ne se fait pas en jeu. Sans cette case, la seule reponse
            // honnete disponible etait « je ne sais pas », alors que la personne sait tres bien.
            new(SceneRepresentativity.HorsJeu, _localize("Scan.Scene.HorsJeu")),
        };
        _sceneDeclaree = ChoixDeScene[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanApplyGpuTuning))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGpuTuningCommand))]
    private ScanState _state = ScanState.Idle;

    /// <summary>Set when an apply is BLOCKED (restore-point gate, nothing applicable,
    /// engine failure). A localized, explicit message — never a silent skip (§16).</summary>
    [ObservableProperty]
    private string? _applyBlockedMessage;

    /// <summary>L8 b — ce que l'Optimiser a juge, pose et mesure, en clair. null = rien lance.</summary>
    [ObservableProperty]
    private string? _resultatOptimisation;

    /// <summary>06/10/2026 (plan 3.4) : un souci est en cours, le point que l'Optimiser prendra le contiendra ; dit avant
    /// le clic, avec le point sain que le docteur a trouvé. null = pas de souci en cours, ou points illisibles.</summary>
    [ObservableProperty]
    private string? _avertissementPoint;

    /// <summary>06/10/2026 (chantier B6) : le bilan santé, huit lignes fixes ; avant le scan, toutes non lues.</summary>
    [ObservableProperty]
    private IReadOnlyList<LigneBilan> _bilan = BilanSante.Construire([]);

    /// <summary>Une reponse a « pendant la mesure, tu joues : », et son libelle localise.</summary>
    public sealed record ChoixScene(SceneRepresentativity Valeur, string Libelle);

    /// <summary>11/09/2026 — ce que le joueur dit jouer pendant la mesure en jeu. Par defaut « je ne
    /// sais pas » (`Unknown`), jamais un defaut rassurant : DrDXT ne sait pas reconnaitre un lobby, et
    /// le silence ne vaut jamais « partie reelle ».
    /// NON memorise par jeu, expres : une scene se declare pour cette mesure. Reprendre la reponse
    /// d'hier ferait passer une ancienne partie pour celle d'aujourd'hui — le defaut rassurant refuse
    /// ci-dessus, venu par un autre chemin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelSceneRetenue))]
    private ChoixScene _sceneDeclaree;

    /// <summary>29/09/2026 : avant Optimiser, le choix déjà fait avant le scan se dit en une ligne ; le
    /// sélecteur ne revient que sur « Changer ».</summary>
    public string LabelSceneRetenue => SceneDeclaree.Valeur == SceneRepresentativity.Unknown
        ? _localize("Scan.Scene.PasChoisie")
        : $"{_localize("Scan.Scene.Retenue")} {SceneDeclaree.Libelle}";
    public string LabelSceneChanger => _localize("Scan.Scene.Changer");

    private static readonly System.Text.RegularExpressions.Regex Borne = new(
        @"(core offset|memory offset|power limit) clamped (-?\d+)→(-?\d+)(?:%| MHz) \(GpuSafeBounds\)");

    /// <summary>08/10/2026 : la note d'un réglage proposé vient du moteur, en anglais (ScanProfileConsumer) ; elle est
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
    public string LabelVoirVoisins => _localize("Scan.Voisins.Voir");
    public string LabelVoirLesAutres => _localize("Carte.VoirLesAutres");

    // 29/09/2026, quatrième critique : le verbe et la durée sur chaque capsule Agir.
    public string LabelCapsuleTestMemoire => _localize("Agir.Capsule.TestMemoire");
    public string LabelCapsuleRemesureCpu => _localize("Agir.Capsule.RemesureCpu");
    public string LabelCapsuleRemesureMemoire => _localize("Agir.Capsule.RemesureMemoire");
    public string LabelCapsuleBcd => _localize("Agir.Capsule.Bcd");
    public string LabelCapsuleReparation => _localize("Agir.Capsule.Reparation");

    /// <summary>29/09/2026 : Windows a coupé ses animations (réglage d'optimisation courant) et DrDXT, qui le
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

    /// <summary>L8 a — ce que le revert au demarrage a fait d'une session interrompue. Sa propre
    /// propriete : un clic sur Optimiser ou Scanner ne doit pas l'effacer avant qu'il soit lu.</summary>
    [ObservableProperty]
    private string? _messageDemarrage;

    /// <summary>Set (non-fatally) when the apply SUCCEEDED but committing the signed
    /// desired-state manifest failed — the optimizations are applied + measured, but the
    /// reconciler cannot re-assert them at boot yet ("moat not armed", §9.12). A localized
    /// diagnostic, not a blocking error: apply still succeeded and the dashboard shows.</summary>
    [ObservableProperty]
    private string? _moatUnarmedMessage;

    /// <summary>11/09/2026 — un commit du manifeste a ete refuse parce que le manifeste signe existant ne se
    /// relit pas (ou que la cle de la machine est inutilisable) : rien n'a ete ecrit par-dessus. Le message
    /// le dit, avec la raison du helper, et <see cref="PeutRepartirDuManifeste"/> ouvre la reprise.</summary>
    [ObservableProperty]
    private string? _manifesteIllisibleMessage;

    [ObservableProperty]
    private bool _peutRepartirDuManifeste;

    public string LabelManifestReset => _localize(KeyManifestReset);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    private int _score;

    /// <summary>
    /// How many scored levers were actually READABLE on this machine. The scorer has always
    /// computed it (an Unknown lever deducts nothing, by doctrine — penalising something we did
    /// not read would fabricate a deficit), but nothing ever displayed it, so a machine we could
    /// barely read still announced a confident "100 — in excellent health". Null = the scorer
    /// reported none, which is treated as INCOMPLETE, never as "everything was read".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    [NotifyPropertyChangedFor(nameof(CoverageText))]
    [NotifyPropertyChangedFor(nameof(IsCoverageComplete))]
    // 17/09/2026 : sans cette ligne, « lecture impossible » calculé avant la couverture restait à l'écran.
    [NotifyPropertyChangedFor(nameof(RegisterText))]
    private ScanCoverage? _coverage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _gatingNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelResultHeadlineLead))]
    private string? _degradedNote;

    [ObservableProperty]
    private string _avgFpsDisplay = Dash;

    [ObservableProperty]
    private string _p1LowDisplay = Dash;

    [ObservableProperty]
    private string _marginDisplay = Dash;

    // Derived narrative — set during scan, all already localized.
    [ObservableProperty]
    private string _subtitleText = string.Empty;

    // 03/10/2026 (D10.1, revue n° 4) : l'en-tête « À faire d'abord » reprend ce compte ; calculé une seule fois, avant
    // que la santé soit chargée, il affichait « les 1 points » sous « 13 points à corriger ». Il suit maintenant le résumé.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelAFaire))]
    private string _problemSummary = string.Empty;

    [ObservableProperty]
    private string _assistantSummary = string.Empty;

    public bool IsScanning => State == ScanState.Scanning;
    public bool CanScan => State != ScanState.Scanning;

    /// <summary>Optimiser est offert des qu'un scan a produit des resultats (Done / AlreadyOptimal /
    /// Degraded). L8 b : il juge d'abord ce qu'il y a a tenter, et le dit quand il n'y a rien.</summary>
    public bool CanApply =>
        State is ScanState.Done or ScanState.AlreadyOptimal or ScanState.Degraded;

    /// <summary>Ce que l'infobulle du bouton « Appliquer » dit, selon qu'il peut agir ou non.
    /// <para>
    /// 17/09/2026 — SIX commandes de l'application peuvent etre grisees et aucune
    /// n'expliquait son inertie. Celle-ci est l'action principale du produit : grisee
    /// avant tout scan, elle laissait la personne devant un bouton mort, sans savoir
    /// s'il est casse ou s'il attend un geste d'elle.
    /// </para></summary>
    public string ApplyHintKey => CanApply ? "scan.apply.ready" : "scan.apply.needs_scan";

    /// <summary>Le texte deja traduit. `ScanView` ne declare pas le convertisseur `Loc`
    /// (il n'existe que dans `DashboardView`) : lier une CLE ici aurait leve au rendu, et
    /// un test qui se contente de relire le XAML ne l'aurait jamais vu. Cette vue localise
    /// par ses proprietes, comme `LabelOptimize` juste a cote.</summary>
    public string ApplyHint => _localize(ApplyHintKey);

    /// <summary>M6 — the dedicated GPU-tuning apply is opt-in/explicit: offered only once a
    /// scan produced results AND the per-chip profile recommended an APPLICABLE GPU step for
    /// this machine. An unsupported GPU (unknown / iGPU / laptop / handheld) → no GPU plan →
    /// the action stays disabled (never silently applies an OC on scan).</summary>
    public bool CanApplyGpuTuning => CanApply && _consumption is { HasGpuPlan: true };

    /// <summary>True when the scanned machine has an applicable GPU-tuning recommendation
    /// (drives the opt-in GPU action's visibility — UI binding pending).</summary>
    public bool HasGpuTuningRecommendation => _consumption is { HasGpuPlan: true };

    /// <summary>cahier §16 — false when the recommended GPU-tuning lines are not yet
    /// hardware-validated (Tested=false): the UI marks the opt-in GPU action "non vérifié".</summary>
    public bool GpuTuningVerified =>
        _consumption is { HasGpuPlan: true } c
        && c.Items.Where(i => c.GpuPlan.TweakIds.Contains(i.TweakId)).All(i => i.IsVerified);

    /// <summary>
    /// Le verdict en une expression — trois ETATS, fonction de deux ENTIERS, jamais d'une somme.
    ///
    /// Mesure du 05/09/2026 : ce verdict derivait du score /100. Doubler toutes les penalites
    /// du grand livre — sans changer un seul levier, un seul etat, un seul constat — faisait
    /// passer la ligne que le client lit de « UI.Verdict.Good » a « UI.Verdict.Fair ». Le score
    /// avait ete retire de la vitrine le 02/09 ; il continuait de decider ce que le client lisait.
    /// Le retirer d'ici est ce qui rend le test d'invariance vert.
    ///
    /// Les trois etats, dans l'ordre de priorite :
    ///   K > 0            -> la lecture est incomplete. Aucun verdict de sante n'est permis :
    ///                       ce qu'on n'a pas lu peut etre exactement le probleme.
    ///   K = 0 et N = 0   -> rien a corriger dans ce qui a ete lu. La nuance est la promesse.
    ///   K = 0 et N > 0   -> N reglages a corriger. Aucun plancher : douze mauvais leviers
    ///                       affichent douze, ce que le /100 (plancher 47) ne pouvait pas dire.
    /// </summary>
    public string VerdictText =>
        !IsCoverageComplete ? _localize("UI.Verdict.Partial")
        : ProvenProblemCount == 0 && !SanteACorriger ? _localize("UI.Verdict.Clean")
        // 25/09/2026 : vu sur la photo, « réglages à corriger » au-dessus de « 0 leviers mal réglés » : les
        // points venaient de la santé (mises à jour, incidents, failles), pas des réglages. Le titre le dit.
        : ProvenProblemCount == 0 ? _localize("UI.Verdict.SanteACorriger")
        : _localize("UI.Verdict.Problems");

    /// <summary>
    /// 17/09/2026 (D22) : l'audit de santé a trouvé au moins un état suspect ou nuisible. Le titre et
    /// le résumé ne regardaient que le scanner : « rien à corriger » s'affichait au-dessus de
    /// « Windows et stabilité : à corriger ». Un état seulement à surveiller (information) ne compte pas.
    /// </summary>
    private bool SanteACorriger => RepairFindings.Any(r => r.Severity > Severity.Info);

    /// <summary>True only when every applicable lever was read. Absent coverage counts as false.</summary>
    public bool IsCoverageComplete => Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 };

    /// <summary>"known/applicable" — the read count behind the score. Em dash when unavailable.</summary>
    public string CoverageText => Coverage is { } c
        ? $"{c.KnownLevers}/{c.ApplicableLevers}"
        : "—";

    // ═══ LE REGISTRE — ce qui remplace le score /100 (D2, lot L1, gate G3) ═══════════════════
    //
    // Pourquoi le score partait : ses six leviers portent des magnitudes qui sont, c'est ecrit
    // dans le code, des « estimations d'expert calibrees ». Penalite maximale 53, donc
    // **plancher mathematique a 47/100** : le chiffre ne pouvait litteralement pas dire « cette
    // machine est mal reglee ». Et il contredisait la liste de mythes du produit, qui refuse
    // tout levier non mesurable.
    //
    // Ce qui le remplace ne promet rien et ne note rien : il compte. « 3 problemes prouves ·
    // 12 leviers lus sur 14 ». Les deux nombres sont des faits de lecture, pas des jugements.

    /// <summary>
    /// Le nombre de problemes de configuration CONSTATES — les leviers lus et trouves
    /// sous-optimaux, comptes dans le grand livre.
    ///
    /// « Prouve » porte sur l'EXISTENCE du probleme, jamais sur son ampleur : on a lu que XMP est
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
    // Le verdict depend desormais du compte, plus du score : sans cette ligne il resterait fige
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
                // 03/10/2026 (D8.4, revue n° 3) : « 0 à changer » sous « 13 points à corriger » se lisait comme un
                // démenti ; à zéro, la phrase le dit en mots, et la ligne précise qu'elle parle de ce que DrDXT pose lui-même.
                _localize(ProvenProblemCount == 0 ? "UI.Register.None" : ProvenProblemCount == 1 ? "UI.Register.Problem" : "UI.Register.Problems"),
                ProvenProblemCount);

            // Sans couverture, on n'affiche pas le compte seul. Mesure du 05/09 : « 3 problemes
            // prouves » sans savoir combien de leviers ont ete lus laisse croire que la machine a
            // ete examinee. Un scan qui n'a pas pu mesurer sa propre couverture n'a rien examine
            // de facon affirmable, et il doit le dire.
            if (Coverage is not { } c)
                return _localize("UI.Register.NoCoverage");

            // 17/09/2026 : tout a été lu et rien du catalogue ne s'applique à cette machine. Ce n'est
            // pas « lecture impossible » : c'est une lecture complète au résultat vide.
            if (c.ApplicableLevers == 0)
                return _localize("UI.Register.NothingApplies");

            var read = string.Format(
                CultureInfo.CurrentCulture,
                _localize("UI.Register.LeversRead"), c.KnownLevers, c.ApplicableLevers);

            // 29/09/2026, quatrième critique : d'abord de quoi on parle (les réglages que DrDXT sait poser), puis
            // combien sont à changer ; lu dans l'autre sens, il contredisait « 12 points à corriger » au-dessus.
            return $"{read}, {problems}";
        }
    }

    // UI label properties — bound via localize so zero hardcoded strings in XAML.
    public string LabelHeaderTagline => _localize("UI.Header.Tagline");
    public string LabelAppName => _localize("UI.App.Name");
    // ── 11/09/2026 — ce qui tournait a cote, et la marge ──────────────────────────────────────
    //
    // Un scan a affiche « 164 FPS » pendant qu'on regardait l'ecran de DrDXT, le jeu en
    // arriere-plan. Le meme soir, en partie reelle avec nos compilations sur son processeur : 79,5.
    // Un FPS moyen seul est trompeur. L'ecran montre donc, a cote du chiffre, sa MARGE D'ERREUR (que
    // BenchmarkResult calcule deja) et la liste courte de ce qui consommait en meme temps.

    /// <summary>Ce qui tournait a cote du jeu pendant la mesure, une ligne lisible par voisin. Liste
    /// Vide = on n'a rien vu au-dessus du seuil, ou on n'a pas su regarder. <see cref="VoisinsConnus"/>
    /// dit lequel des deux, pour que l'ecran ne fasse jamais passer « on ne sait pas » pour « rien ».</summary>
    public ObservableCollection<string> VoisinsDuJeu { get; } = new();

    /// <summary>Vrai des qu'au moins un voisin notable a ete vu. Faux = rien a montrer.</summary>
    [ObservableProperty]
    private bool _voisinsConnus;

    /// <summary>La phrase d'en-tete de la liste, ou la phrase qui dit qu'on n'a rien vu de notable.</summary>
    [ObservableProperty]
    private string? _voisinsResume;

    public string LabelVoisins => _localize("Scan.Voisins.Header");
    public string LabelVoisinsTitre => _localize("Scan.Voisins.Titre");
    public string LabelConsigneScene => _localize("Scan.Scene.Consigne");

    /// <summary>Le FPS moyen avec sa marge, dans le meme champ : « 148 ± 3,2 ». Jamais un chiffre
    /// seul — c'est la demande produit, et c'est la seule forme honnete d'une mesure.</summary>
    [ObservableProperty]
    private string _avgFpsAvecMarge = Dash;

    /// <summary>27/09/2026 : le 1 % low s'affichait seul, alors qu'il varie plus d'une passe à l'autre que la
    /// moyenne et qu'il a sa propre marge depuis le 26/09 (`P1LowMarginOfError`). Même règle que le FPS moyen.</summary>
    [ObservableProperty]
    private string _p1LowAvecMarge = Dash;

    private void PeuplerLesVoisins(IReadOnlyList<DrDxt.App.Platform.VoisinDuJeu>? vus)
    {
        VoisinsDuJeu.Clear();
        // 03/10/2026 (D8.6, revue n° 3) : « pas pu lire » a sa propre phrase, au lieu d'une parenthèse sous « rien vu ».
        if (vus is null)
        {
            VoisinsConnus = false;
            VoisinsResume = _localize("Scan.Voisins.NonLus");
            return;
        }
        foreach (var v in vus)
            VoisinsDuJeu.Add(v.GpuPourcent is { } gpu
                ? string.Format(CultureInfo.CurrentUICulture, _localize("Scan.Voisins.LigneGpu"), v.Nom,
                    v.ProcesseurPourcent, gpu)
                : string.Format(CultureInfo.CurrentUICulture, _localize("Scan.Voisins.Ligne"), v.Nom,
                    v.ProcesseurPourcent));

        VoisinsConnus = VoisinsDuJeu.Count > 0;
        // 17/09/2026 : sans jeu lu, la liste ne dit pas « à côté du jeu ».
        var avecJeu = _gamePid != 0;
        VoisinsResume = VoisinsConnus
            ? _localize(avecJeu ? "Scan.Voisins.Header" : "Scan.Voisins.HeaderSansJeu")
            : _localize(avecJeu ? "Scan.Voisins.Aucun" : "Scan.Voisins.AucunSansJeu");
    }

    public string LabelAvgFps => _localize("UI.Label.AvgFps");
    public string LabelP1Low => _localize("UI.Label.P1Low");
    public string LabelMargin => _localize("UI.Label.Margin");
    public string LabelScanButton => _localize("Scan.Button.Start");
    public string LabelFindings => _localize("Scan.Findings.Header");
    public string LabelRecommendationsHeader => _localize("Scan.Recommendations.Header");

    /// <summary>16/09/2026 : zero levier recommande se DIT, au lieu d'un compteur a 0 et d'une liste vide.</summary>
    public bool AucuneRecommandation => Recommendations.Count == 0;
    private void NotifierAucuneRecommandation(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(AucuneRecommandation));
    public string LabelAucuneRecommandation => _localize("Scan.Recommendations.None");
    public string LabelScanning => _localize("Scan.State.Scanning");
    // Idle headline + hero copy.
    public string LabelIdleHeadlineLead => _localize("UI.Idle.HeadlineLead");
    public string LabelIdleHeadlineAccent => _localize("UI.Idle.HeadlineAccent");
    public string LabelProductPromise => _localize("Product.Promise");
    public string LabelIdleHeroTitle => _localize("UI.Idle.HeroTitle");
    public string LabelIdleHeroBody => _localize("UI.Idle.HeroBody");
    public string LabelTrustLocal => _localize("UI.Trust.Local");
    public string LabelTrustData => _localize("UI.Trust.Data");

    // Results headline ("Votre PC est <verdict>."). When the in-game perf was not measured
    // (degraded — no game running), the lead scopes the verdict to config ("Côté config, votre
    // PC est …") so the green "en bonne santé" never overclaims a measured in-game result — same
    // honesty rule as the config-only sub-scores. The subtitle then sells the unmeasured gap.
    public string LabelResultHeadlineLead =>
        _localize(DegradedNote is not null ? "UI.Result.HeadlineLeadConfig" : "UI.Result.HeadlineLead");
    public string LabelScanDone => _localize("UI.Result.ScanDone");

    // Hero — results side.
    public string LabelCoverageCaption => _localize("UI.Coverage.Caption");
    /// <summary>Sous-titre du registre — dit ce que les deux nombres SONT : des lectures.</summary>
    public string LabelRegisterCaption => _localize("UI.Register.Caption");
    public string LabelOptimize => _localize("UI.Cta.Optimize");
    public string LabelRapportDescription => _localize("Rapport.Description");
    public string LabelRescan => _localize("UI.Cta.Rescan");

    // Assistant panel.
    public string LabelAssistantTitle => _localize("UI.Assistant.Title");
    public string LabelAssistantOnline => _localize("UI.Assistant.Online");
    public string LabelAssistantIdleLead => _localize("UI.Assistant.IdleLead");
    public string LabelAssistantWillExamine => _localize("UI.Assistant.WillExamine");
    public string LabelViewReport => _localize("UI.Assistant.ViewReport");

    // Footer.
    public string LabelFooterVersion => DashboardViewModel.FooterVersion(_localize);
    // Phase 18 — ce que la verification de mise a jour a CONSTATE au demarrage. Tant qu'elle n'a pas
    // rendu : « jamais verifiees », jamais une coche.
    private string? _etatMisesAJour;
    public string LabelFooterUpdates => _etatMisesAJour ?? _localize("UI.Footer.Updates");

    private DrDxt.App.Updates.ResultatMiseAJour? _miseAJour;

    public string LabelInstallUpdate => _localize("UI.Footer.Install");

    /// <summary>Le bouton n'existe que pour une version App VERIFIEE dont on connait chemin,
    /// empreinte et version.</summary>
    public bool CanInstallUpdate => _miseAJour?.Installable == true;

    /// <summary>06/10/2026 (chantier B10) : la version signée et vérifiée qui attend le clic ; null sinon.</summary>
    public string? VersionProposee => _miseAJour is { Installable: true } m ? m.Version : null;

    public void AfficherMiseAJour(DrDxt.App.Updates.ResultatMiseAJour resultat)
    {
        _miseAJour = resultat;
        // B10 : une version vérifiée est nommée ; l'ancienne phrase disait « une nouvelle version » sans dire laquelle.
        _etatMisesAJour = VersionProposee is { } v
            ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, _localize("UI.Footer.Updates.NewVersionNamed"), v)
            : _localize(DrDxt.App.Updates.VerificationMiseAJour.CleResx(resultat.Etat));
        OnPropertyChanged(nameof(LabelFooterUpdates));
        OnPropertyChanged(nameof(VersionProposee));
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Lance l'installeur signe verifie (empreinte RE-VERIFIEE avant lancement). L'UAC et la
    /// fermeture de l'App appartiennent a l'installeur.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private void InstallUpdate()
    {
        if (_miseAJour is not { Installable: true } m) return;
        var lance = DrDxt.App.Updates.InstallationMiseAJour.Lancer(
            m.CheminInstalleur!, m.Sha256!, m.Version!, _lancerInstalleur);
        _etatMisesAJour = _localize(lance.IsOk ? "UI.Footer.Updates.InstallerLaunched" : "UI.Footer.Updates.InstallerRefused");
        // Revue (F-33) : un installeur refusé (fichier changé depuis la vérification) n'est plus proposé.
        if (!lance.IsOk)
        {
            _miseAJour = m with { Etat = DrDxt.App.Updates.EtatMiseAJour.Refusee };
            OnPropertyChanged(nameof(VersionProposee));
            OnPropertyChanged(nameof(CanInstallUpdate));
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(LabelFooterUpdates));
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<ComponentItem> Components { get; } = new();
    public ObservableCollection<FeatureItem> Features { get; }

    /// <summary>M6 — the per-chip profile's surfaced recommendations for the scanned
    /// machine: each carries its honest GainEvidence (claim + source) and an
    /// <see cref="ProfileRecommendationItem.IsVerified"/> flag. Filled on a successful
    /// scan; a not-yet-validated (Tested=false) line is marked "non vérifié" (§16).</summary>
    public ObservableCollection<ProfileRecommendationItem> Recommendations { get; } = new();

    /// <summary>
    /// 17/09/2026 — la carte de la machine : les mêmes constats, mesures et recommandations que les
    /// listes ci-dessous, rangés par domaine (processeur, carte graphique, mémoire, latence, réseau,
    /// stockage, Windows, BIOS). Rien n'y est ajouté ; un domaine où rien n'a été lu le dit.
    /// </summary>
    public ObservableCollection<DomaineMachine> Domaines { get; } = new();

    [ObservableProperty]
    private DomaineMachine? _domaineSelectionne;

    public bool ADesDomaines => Domaines.Count > 0;

    /// <summary>08/10/2026 (critique UI, défaut 6) : l'orbite montrait cinq composants sans état ni clic, et taisait trois
    /// domaines. Chaque satellite porte maintenant l'état de son domaine et l'ouvre ; les domaines sans composant
    /// (latence, réseau, BIOS) sont listés sous l'orbite. Les cinq tentacules de chrome restent : l'effet en dessine cinq.</summary>
    public IReadOnlyList<SatelliteDomaine> Satellites =>
        Components.Select(c => new SatelliteDomaine(c,
            Domaines.FirstOrDefault(d => d.Cle == CarteDeLaMachine.DomaineDuComposant(c.IconKey)))).ToList();

    public IReadOnlyList<DomaineMachine> DomainesSansComposant
    {
        get
        {
            var portes = Components.Select(c => CarteDeLaMachine.DomaineDuComposant(c.IconKey)).ToHashSet(StringComparer.Ordinal);
            return Domaines.Where(d => !portes.Contains(d.Cle)).ToList();
        }
    }

    public bool ADesDomainesSansComposant => DomainesSansComposant.Count > 0;

    private void AnnoncerSatellites()
    {
        OnPropertyChanged(nameof(Satellites));
        OnPropertyChanged(nameof(DomainesSansComposant));
        OnPropertyChanged(nameof(ADesDomainesSansComposant));
    }

    /// <summary>08/10/2026 (défaut 10) : le mot d'état d'un interrupteur, dans le gabarit « Bascule ».</summary>
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

    /// <summary>29/09/2026 : sous le verdict, ce qui est à corriger, le plus grave d'abord ; un clic ouvre son domaine.</summary>
    public ObservableCollection<PointAFaire> PointsAFaire { get; } = new();

    public string LabelAFaire => EnTeteAFaire(_localize, CompteCommeLaCarte().ACorriger);

    /// <summary>03/10/2026 (D8.4, revue n° 3) : le grand nombre de points à corriger ne disait pas où les trouver ; la
    /// liste n'en montre que les premiers. Son en-tête rappelle le total et où sont les autres.</summary>
    internal static string EnTeteAFaire(Func<string, string> localiser, int total) => total switch
    {
        <= 0 => localiser("Carte.AFaire"),
        1 => localiser("Carte.AFaire.Total.Un"),
        _ => string.Format(CultureInfo.CurrentCulture, localiser("Carte.AFaire.Total"), total),
    };
    public string LabelPourquoi => _localize("Carte.Pourquoi");
    public string LabelCopierCommande => _localize("Carte.CopierCommande");

    /// <summary>29/09/2026 (B21) : le presse-papiers de Windows ; une couture pour les tests.</summary>
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

    // UI1 (08/10/2026, vu sur capture) : la liste « Mesuré » prend le même titre et le même accord que les constats.
    internal static string LigneVerifiee(RepairFinding v) =>
        !string.IsNullOrWhiteSpace(v.ValeurCourante)
            ? $"{RepairFindingViewModel.TitreAffiche(v)} : {RepairFindingViewModel.ValeurAffichee(v)}"
            : RepairFindingViewModel.TitreAffiche(v);

    internal static string MentionDePreuve(bool profilTeste, bool verifieIci, Func<string, string> localiser)
        => profilTeste ? string.Empty : verifieIci ? localiser("Levier.VerifieIci") : localiser("Carte.Propose.NonValide");

    private static string Majuscule(string phrase) =>
        phrase.Length == 0 ? phrase : char.ToUpper(phrase[0], CultureInfo.CurrentCulture) + phrase[1..];

    private void ConstruireCarte()
    {
        var entrees = new List<EntreeCarte>();

        foreach (var f in Findings)
        {
            var (titre, suite) = CarteDeLaMachine.TitreEtSuite(f.Title);
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDuConstat(f.Category), f.Severity,
                titre, suite, string.Empty, string.Empty, NonLu: false));
        }

        // UI1 (08/10/2026, vu sur capture) : un réglage défait depuis portait son identifiant (« Latency.vrrFlipModelOn »)
        // pour titre ; il porte le nom que DrDXT lui donne partout ailleurs.
        foreach (var r in RepairFindings)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLAudit(r.DomaineCle, r.NomCle), r.Severity,
                TitreDuConstat(r, _localize),
                Majuscule(r.Explication), r.ValeurCourante, r.ActionLabel, NonLu: false));

        foreach (var r in RepairBlindSpots)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLAudit(r.DomaineCle, r.NomCle), r.Severity,
                r.Nom, Majuscule(r.Explication), r.ValeurCourante, string.Empty, NonLu: true));

        foreach (var b in BiosFindings)
            entrees.Add(new EntreeCarte("bios", b.Severity, b.Title,
                b.Rationale, b.GainLabel, b.ActionLabel, NonLu: false));

        // 29/09/2026, photo (latence) : le titre en gras était tout l'argumentaire, sur trois lignes. Le titre est
        // le nom du réglage, celui qu'annonce Optimiser ; l'argumentaire passe dessous. Un id sans nom garde l'ancien.
        // Vu sur capture (08/10/2026, après ADM1) : un réglage que « Vérifier DrDXT » a posé, relu et rendu sur CE PC le dit,
        // au lieu de « pas encore prouvé sur ce matériel ». Le profil entier, lui, reste non testé (une seule preuve).
        var verifiesIci = _lastScan is { } scanVerifie ? LeviersVerifiesIci?.Invoke(scanVerifie.Hw) : null;
        foreach (var r in Recommendations)
            entrees.Add(new EntreeCarte(CarteDeLaMachine.DomaineDeLaRecommandation(r.Intent), Severity.Info,
                TweakDisplayKeys.TitleKey(r.TweakId) is var cle && cle != r.TweakId ? _localize(cle) : r.Claim,
                cle != r.TweakId ? $"{r.Claim} {r.Source}" : r.Source,
                MentionDePreuve(r.IsVerified, verifiesIci?.Contains(r.TweakId) == true, _localize),
                TraduireNote(r.AdjustmentNote, _localize), NonLu: false, Propose: true));

        var latence = new List<string>();
        if (AvgFpsDisplay != Dash) latence.Add($"{LabelAvgFps} : {AvgFpsAvecMarge}");
        if (P1LowDisplay != Dash) latence.Add($"{LabelP1Low} : {P1LowAvecMarge}");
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
            // SEC3 (08/10/2026) : ce que le service a réaffirmé seul après que Windows l'a remis.
            ["windows"] = CarteDeLaMachine.LignesDuService(LireEtatDuService?.Invoke(), _localize),
            ["bios"] = CarteDeLaMachine.LignesBios(_reglagesBiosLus, _lastScan?.Hw.Memory.FirstOrDefault()?.ConfiguredMhz ?? 0)
                .Concat(_lienPcie is { } lien ? [CarteDeLaMachine.LignePcie(lien)] : Array.Empty<string>()).ToList(),
        };

        // L'audit et les recommandations sont maintenant connus : le titre, la phrase d'assistant et
        // le résumé se recalculent pour ne jamais contredire la carte.
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(RegisterText));
        ProblemSummary = ResumeDesProblemes();
        AssistantSummary = ResumeDuRapport();

        Domaines.Clear();
        // Une observation (« Observed ») n'est pas un état conforme : elle porte sa valeur, qui la
        // distingue d'un constat du même nom (vu le 24/09 : un incident noyau isolé listé « conforme »
        // à côté du même incident répété, affiché à regarder).
        // 25/09/2026 : rangés par famille d'audit, avec son libellé en sous-titre quand un domaine en
        // mêle plusieurs ; ce qui peut faire planter la machine d'abord.
        var titres = new HashSet<string>();
        var verifies = (_derniereSante?.Verifies ?? [])
            .GroupBy(v => CarteDeLaMachine.DomaineDeLAudit(v.Domaine, v.Nom))
            .ToDictionary(g => g.Key, g =>
            {
                var familles = g.GroupBy(v => v.Domaine)
                    .OrderBy(f => OrdreDesFamilles.IndexOf(f.Key) is var i and >= 0 ? i : OrdreDesFamilles.Count)
                    .ThenBy(f => RepairLibelles.Domaine(f.Key), StringComparer.CurrentCulture)
                    .ToList();
                var lignes = new List<string>();
                // Un sous-titre par ligne n'est que du bruit (vu sur photo, page Carte graphique :
                // 3 sous-titres pour 3 états). Le rangement ne sert qu'une liste longue.
                // Le seuil de 6 vient des photos du 25/09, pas d'une source.
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

        // 24/09/2026 : l'explication sourcée de chaque état lu, en info-bulle sur sa ligne.
        var explications = (_derniereSante?.Verifies ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.Explication))
            .GroupBy(LigneVerifiee)
            .ToDictionary(g => g.Key, g => Majuscule(g.First().Explication!));

        // ROAD1 (08/10/2026) : ce que DrDXT fait sur chaque page, levier par levier, sur la machine lue.
        var leviersParPage = CatalogueDesLeviers?.Invoke() is { } catalogue && _lastScan is { } scanLu
            ? CarteDeLaMachine.LeviersParPage(DrDxt.Application.Tweaks.CouvertureDesLeviers.Calculer(catalogue, scanLu.Hw, scanLu.State,
                LeviersPoses.Select(l => l.Id).ToHashSet(StringComparer.Ordinal), LeviersVerifiesIci?.Invoke(scanLu.Hw)), _localize)
            : null;
        foreach (var d in CarteDeLaMachine.Construire(entrees, mesures, Components, _localize, verifies, explications, titres,
                     _dernierDeficitCpu is { Demontrable: true } or { BrideDesLeDepart: true } && CpuSustainedLabel is not null ? new HashSet<string> { "processeur" } : null))
            Domaines.Add(leviersParPage?.GetValueOrDefault(d.Cle) is { } leviers ? d with { Leviers = leviers } : d);
        DomaineSelectionne = CarteDeLaMachine.PremierAOuvrir(Domaines);
        PointsAFaire.Clear();
        foreach (var p in CarteDeLaMachine.AFaire(Domaines)) PointsAFaire.Add(p);
        OnPropertyChanged(nameof(ADesDomaines));
        AnnoncerSatellites();
    }

    /// <summary>cahier §16 — the localized "non vérifié" tag for a not-yet-validated
    /// profile recommendation (an item whose <see cref="ProfileRecommendationItem.IsVerified"/>
    /// is false). The view shows it next to those lines.</summary>
    public string LabelUnverified => _localize("UI.Profile.Unverified");

    /// <summary>M3 — the BIOS Doctor's firmware findings for the scanned machine, surfaced in
    /// the scan results. GUIDANCE-only (never in the apply batch — we never write the firmware),
    /// ordered fix-misconfig-first: actionable GUIDE findings (highest severity first, so
    /// XMP/EXPO off — the single biggest real gain — leads) ahead of informational/not-readable
    /// ones. Filled on a successful scan; empty when the machine is well-tuned, the BIOS is
    /// locked (laptop/handheld), or the advisor faults (best-effort, never breaks the scan).</summary>
    public ObservableCollection<BiosFindingViewModel> BiosFindings { get; } = new();

#if DEBUG
    /// <summary>Debug seulement (argument « xmp-off ») : voir le guide BIOS d'un XMP coupé.</summary>
    internal static bool SimulerXmpOffPourLeBios;
#endif

    /// <summary>True when the scan surfaced at least one BIOS finding (drives the section's
    /// visibility in ScanView.xaml).</summary>
    public bool HasBiosFindings => BiosFindings.Count > 0;

    /// <summary>Localized header for the guidance-only BIOS findings section.</summary>
    public string LabelBiosHeader => _localize("Scan.Bios.Header");

    /// <summary>M3 — the PERSONALIZED BIOS guide for the scanned machine: the "HOW" behind the
    /// findings (the exact menu path → setting → value to set, plus the mandatory recovery
    /// section and an HONEST confidence label — Verified only for the exact board, otherwise a
    /// clearly-labelled best-known fallback). GUIDANCE-only text: never a write, never in the
    /// apply batch. Built only when the generator produced at least one actionable step; null
    /// when the machine is well-tuned, the guide is recovery-only, or the generator faults
    /// (best-effort, never breaks the scan).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBiosGuide))]
    private BiosGuideViewModel? _biosGuide;

    /// <summary>True when the scan produced an actionable BIOS guide (drives the section's
    /// visibility in ScanView.xaml).</summary>
    public bool HasBiosGuide => BiosGuide is not null;

    /// <summary>Le verdict du retour de BIOS : « ton changement a-t-il pris ? », reglage par
    /// reglage. Null quand rien n'etait en attente — c'est le cas de presque toutes les
    /// ouvertures de l'app, et une section vide ne doit pas s'afficher.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBiosLoopResult))]
    private BiosLoopViewModel? _biosLoop;

    // LM — la moitie memoire de la meme section : ce que la machine mesurait avant le voyage
    // BIOS, ce qu'elle mesure apres, et si l'ecart est un resultat. null = rien a montrer.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryAb))]
    private MemoryAbViewModel? _memoryAb;

    /// <summary>Vrai quand un voyage dans le BIOS attend son verdict (pilote la visibilite).</summary>
    public bool HasBiosLoopResult => BiosLoop is not null;

    public string LabelOublierChangementBios => _localize("Bios.Retour.Oublier");

    [ObservableProperty] private string? _erreurOubliBios;

    /// <summary>26/09/2026 : renoncer a un changement qui n'a pas pris. Le marqueur est retire, la section
    /// disparait ; au prochain passage par le bouton du guide, un nouveau voyage repart de zero.</summary>
    [RelayCommand]
    private void OublierChangementBios()
    {
        if (_firmwareTrip is null || BiosLoop is null) return;
        var r = _firmwareTrip.Oublier();
        if (r.IsOk) { ErreurOubliBios = null; BiosLoop = null; MemoryAb = null; }
        else ErreurOubliBios = $"{_localize("Bios.Retour.OublierEchec")} : {r.Error?.Message}";
    }

    public bool HasMemoryAb => MemoryAb is not null;

    public string LabelBiosReturnHeader => _localize("Scan.BiosReturn.Header");

    /// <summary>
    /// Cet hote sait-il prouver qu'un changement BIOS a pris ? Faux quand la couture n'est pas
    /// cablee : le bouton « emmene-moi dans le BIOS » ne s'affiche pas et aucun verdict n'est
    /// prononce.
    ///
    /// Expose parce que le contraire est INVISIBLE. Une couture optionnelle non passee donne un
    /// ScanViewModel parfaitement construit et parfaitement muet -- c'est la forme exacte du
    /// defaut L0.2, ou tout etait enregistre et rien n'etait resolu. Un test de composition peut
    /// desormais le constater au lieu de lire le fichier de cablage.
    /// </summary>
    public bool CanProveBiosChanges => _firmwareTrip is not null;

    // ── D7 / gate G6 — le rapport de diagnostic ─────────────────────────────────────────────
    //
    // « Jamais de debogage a l'aveugle par mail. » Sans ce bouton, le premier client difficile
    // consomme une semaine : une question a la fois, avec quelqu'un qui ne sait pas ou regarder.
    //
    // Le bouton appelait `OnComingSoon` — la meme impasse que le bouton « debloquer ».

    private ScanResult? _lastScan;

    /// <summary>« Ce que ta memoire rend », relu dans le magasin (jamais remesure ici) ; null sans
    /// reference, ou si elle date d'une autre configuration. La DATE voyage avec les chiffres : une
    /// mesure enregistree se lit autrement qu'une mesure du jour.</summary>
    private string? LigneMemoire(IReadOnlyList<MemoryModule> modules)
    {
        if (_memoryBaseline is null || _machineId is null) return null;
        var reference = _memoryBaseline.MesureCourante(_machineId(), modules);
        if (reference is null) return null;
        var ligne = string.Format(CultureInfo.CurrentCulture, _localize("Scan.Memory.Measured"),
            reference.TakenUtc.ToLocalTime().DateTime,
            reference.Measurement.BandwidthGBs, reference.Measurement.BandwidthSigmaPct,
            reference.Measurement.LatencyNs, reference.Measurement.LatencySigmaPct);

        // 26/09/2026 : une référence prise avant le garde « jeu lancé » peut décrire le partage avec un jeu
        // (celle du 16/09 : 12,7 Go/s au lieu de 33,4, capture de Warzone 1 min 40 plus tard).
        var mention = MentionPriseEnJeu("Scan.Memory.PriseEnJeu", reference.TakenUtc, reference.JeuFermeVerifie);
        _memoireSuspecte = mention.Length > 0;
        return ligne + mention;
    }

    /// <summary>Une référence d'avant le garde « jeu lancé » qu'une capture de jeu du même scan accompagne dans le
    /// journal : la phrase qui le dit, sinon rien. Une référence prise sous le garde ne se soupçonne pas : un jeu
    /// lancé juste après la mesure laisserait une capture proche sans rien avoir faussé.</summary>
    private string MentionPriseEnJeu(string cle, DateTimeOffset prise, bool jeuFermeVerifie)
    {
        if (jeuFermeVerifie || _machineId is null) return "";
        IReadOnlyList<DrDxt.Core.PerformanceSample> journal;
        try { journal = _performanceJournal?.Lire() ?? []; }
        catch { journal = []; }
        return CaptureDuMemeScan(journal, prise, _machineId()) is { } capture
            ? string.Format(CultureInfo.CurrentCulture, _localize(cle),
                capture.GameId ?? "?", capture.TakenUtc.ToLocalTime().DateTime)
            : "";
    }

    /// <summary>Une capture de jeu à moins de cette durée de la référence appartient au même scan. **NON CHIFFRÉ**
    /// au-delà des deux cas de la machine de référence : 1 min 40 (mémoire, 16/09) et 5 min (processeur, 11/09).
    /// Le soupçon ne vise que les références d'avant le garde : le prix d'un faux positif est une remesure.</summary>
    internal static readonly TimeSpan FenetreDuMemeScan = TimeSpan.FromMinutes(10);

    internal static DrDxt.Core.PerformanceSample? CaptureDuMemeScan(
        IReadOnlyList<DrDxt.Core.PerformanceSample> journal, DateTimeOffset reference, string machineId)
        => journal
            .Where(s => s.MachineId == machineId && s.AvgFps > 0 && (s.TakenUtc - reference).Duration() <= FenetreDuMemeScan)
            .OrderBy(s => (s.TakenUtc - reference).Duration())
            .FirstOrDefault();

    /// <summary>Le chemin du dernier rapport ecrit, montre a l'utilisateur pour qu'il le
    /// retrouve. Vide tant qu'aucun export n'a eu lieu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportedReport))]
    private string _lastReportPath = string.Empty;

    /// <summary>Vrai quand un rapport vient d'etre ecrit.</summary>
    public bool HasExportedReport => LastReportPath.Length > 0;

    /// <summary>Vrai quand l'export est possible — sinon le bouton disparait au lieu de ne
    /// rien faire.</summary>
    public bool CanExportReport => _writeReport is not null;

    public string LabelReportWritten => _localize("Support.ReportWritten");
    public string LabelReportFailed => _localize("Support.ReportFailed");

    /// <summary>
    /// Ecrit le rapport et dit ou. L'empreinte machine y figure — c'est ce que le support demande
    /// pour retrouver une licence — mais jamais la cle : ce fichier est fait pour etre colle dans
    /// un salon public, et une cle qui s'y trouverait serait publiee par le client lui-meme.
    /// </summary>
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (_writeReport is null) return;

        try
        {
            var now = DateTimeOffset.Now;
            // Ce fichier est fait pour être envoyé : il passe par le même anonymiseur que les autres
            // sorties (chemin du profil, courriels, empreintes), même si aucune ligne n'en porte
            // aujourd'hui. Une ligne ajoutée demain ne pourra pas y glisser le nom du joueur.
            var text = new DrDxt.Infrastructure.Ops.TextScrubber().Scrub(
                DrDxt.Application.Support.DiagnosticReport.Build(_lastScan, _appVersion, now, _derniereSante, _localize,
                    processeur: _dernierDeficitCpu,
                    reseau: NetworkPathLabel is { } ligne ? new DrDxt.Measurement.Network.LigneReseau(ligne) : null,
                    memoire: MemoryMeasuredLabel is { } memoire ? new DrDxt.Measurement.Memory.LigneMemoireMesuree(memoire) : null,
                    derive: PerformanceDriftLabel is { } derive ? new DrDxt.Measurement.LigneDerive(derive) : null));

            LastReportPath = _writeReport(
                DrDxt.Application.Support.DiagnosticReport.FileNameFor(now), text);
        }
        catch
        {
            // Disque plein, droits refuses : on le DIT. Un bouton d'export qui ne fait rien
            // visiblement est pire que pas de bouton -- le client croit avoir un fichier.
            LastReportPath = string.Empty;
            ApplyBlockedMessage = _localize("Support.ReportFailed");
        }
    }

    /// <summary>Le chemin de la dernière machine exportée. Vide tant qu'aucun export n'a eu lieu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportedMachine))]
    private string _lastMachineExportPath = string.Empty;

    public bool HasExportedMachine => LastMachineExportPath.Length > 0;

    /// <summary>Vrai quand l'export de la machine est branché ; sinon le bouton disparaît.</summary>
    public bool CanExportMachine => _exporterMachine is not null;

    public string LabelExportMachine => _localize("Machine.ExportButton");
    public string LabelMachineExportWritten => _localize("Machine.ExportWritten");

    /// <summary>
    /// 07/10/2026 (T1, kit testeur) : écrit ce que le scan a lu, anonymisé, dans un fichier que le joueur envoie lui-même.
    /// Rien ne part sur le réseau. Sans scan, ou si l'écriture échoue, on le dit : pas de chemin vers un fichier absent.
    /// </summary>
    [RelayCommand]
    private async Task ExporterMaMachineAsync()
    {
        if (_exporterMachine is null) return;
        LastMachineExportPath = string.Empty;
        try
        {
            if (_materielLu is not { } hw || _reglagesLus is not { } st) throw new InvalidOperationException("aucun scan lu");
            var sante = _constatsLus ?? [];
            var exporter = _exporterMachine;
            // Hors du thread UI : l'export relit les numéros de série par WMI pour les retirer.
            LastMachineExportPath = await Task.Run(() => exporter(hw, st, sante));
        }
        catch
        {
            ApplyBlockedMessage = _localize("Machine.ExportFailed");
        }
    }

    /// <summary>Localized header for the personalized BIOS guide section.</summary>
    /// <summary>
    /// D4 / gate G7 — « la partie BIOS est OPTIONNELLE ».
    ///
    /// Sans cette phrase, un joueur qui n'ose pas entrer dans son BIOS croit qu'il rate la moitie
    /// du produit, et un joueur qui ose mais s'y perd ouvre un ticket. Un dev solo qui vend a des
    /// joueurs un produit qui les envoie dans leur BIOS ne peut pas se permettre ni l'un ni
    /// l'autre : le differenciateur mis en vitrine n'est pas le guide, c'est la PREUVE qu'un
    /// changement a pris.
    /// </summary>
    public string LabelBiosOptional => _localize("Bios.Optional.Note");

    public string LabelBiosGuideHeader => _localize("Scan.Bios.Guide.Header");

    /// <summary>Localized, reassuring header for the guide's mandatory recovery/safety ladder
    /// ("if something goes wrong") — surfaced in a calm, non-alarming panel.</summary>
    public string LabelBiosRecoveryHeader => _localize("Scan.Bios.Guide.RecoveryHeader");

    /// <summary>
    /// Fills the idle "Le scan va examiner…" list. Shows localized labels immediately with "—"
    /// placeholders, then detects hardware off the UI thread and fills real models when ready.
    /// Safe to fire-and-forget from the view after the window is shown.
    /// </summary>
    public async Task LoadIdleComponentsAsync()
    {
        PopulateComponents(hw: null);

        var hwResult = await Task.Run(async () => await _detector.DetectAsync()).ConfigureAwait(true);
        if (State == ScanState.Idle && hwResult.IsOk && hwResult.Value is { } hw)
            PopulateComponents(hw);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        // 18/09/2026. Les sondes du scan parlent a WMI, au registre, a des pilotes et a des
        // services : du code qui LEVE au lieu de refuser -- un service arrete, une cle
        // absente, un pilote qui rend n'importe quoi. Sans cette garde, l'exception remontait
        // jusqu'au filet fatal de App.xaml.cs et l'application se fermait AU MILIEU du scan,
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
            System.Diagnostics.Trace.TraceError("Scan interrompu par une exception : {0}", ex);
            ErrorMessage = _localize("Scan.Error.Inattendu");
            State = ScanState.Failed;
        }
        finally
        {
            _etapeEnCours = 0;
            // D6.1 : finie, la jauge reste pleine un instant ; interrompue, elle part tout de suite.
            if (State is ScanState.Failed) JaugeVisible = false;
            else _ = MasquerLaJaugeAsync(numero);
        }
    }

    /// <summary>29/09/2026 (B1) : les pôles du réseau déjà lus par CE scan, un bit par composant, dans l'ordre de
    /// <see cref="Components"/> (processeur, carte, mémoire, stockage, Windows), le même que celui des pôles.</summary>
    [ObservableProperty] private int _polesLus;

    /// <summary>03/10/2026 (D1.1) : l'analyse dure plus de 10 s ; un rond qui tourne sans fin fait croire à un plantage
    /// (NN/g, Progress Indicators). Elle avance par étapes nommées, dans l'ordre réel de <see cref="ScanCoeurAsync"/>.</summary>
    public const int EtapesDuScan = 8;

    /// <summary>La part de l'analyse déjà faite, de 0 à 1 ; jamais en recul pendant un scan.</summary>
    [ObservableProperty] private double _progressionDuScan;

    /// <summary>« Étape k sur 8 · nom » : ce qui tourne en ce moment, écrit sous le rond.</summary>
    [ObservableProperty] private string? _labelEtapeDuScan;

    /// <summary>03/10/2026 (D3.1) : la fin de l'étape en cours, un peu en deçà : la jauge peut y avancer seule pendant
    /// l'étape, jamais au-delà.</summary>
    [ObservableProperty] private double _plafondDuScan;

    /// <summary>03/10/2026 (D6.1) : « la jauge pas même synchro que le scanner ». Les huit étapes ne durent pas pareil
    /// (le réseau et le processeur prennent l'essentiel) ; chacune pèse son temps réel. Durées par défaut tirées d'une
    /// analyse réelle au repos de la machine de référence (03/10 : réseau et processeur 8,0 s, audit 3,2 s, réglages 2,7 s,
    /// le reste sous la seconde), remplacées par celles de la dernière analyse de cette machine.</summary>
    private static readonly double[] DureesParDefaut = [0.1, 2.7, 0.05, 8.0, 0.1, 0.6, 0.1, 3.2];
    private double[] _dureesAttendues = (double[])DureesParDefaut.Clone();
    private readonly double[] _dureesMesurees = new double[EtapesDuScan];
    private readonly System.Diagnostics.Stopwatch _chronoEtape = new();
    private int _etapeEnCours;
    private int _numeroDeScan;

    /// <summary>Lit les durées de la dernière analyse ; branché par l'appli, absent des tests (rien n'est écrit).</summary>
    internal Func<double[]?>? LireDureesDesEtapes { get; set; }
    internal Action<double[]>? EnregistrerDureesDesEtapes { get; set; }

    /// <summary>La part de la jauge qui avance par seconde pendant l'étape en cours.</summary>
    [ObservableProperty] private double _vitesseDuScan;

    /// <summary>La jauge reste à l'écran un instant après la fin, le temps qu'on la voie pleine.</summary>
    [ObservableProperty] private bool _jaugeVisible;

    /// <summary>Le début et la fin d'une étape sur la jauge, de 0 à 1, au prorata des durées.</summary>
    internal static (double Debut, double Fin) Bornes(IReadOnlyList<double> durees, int numero)
    {
        var total = durees.Sum();
        var avant = durees.Take(numero - 1).Sum();
        return (avant / total, (avant + durees[numero - 1]) / total);
    }

    private void Etape(int numero, string cle)
    {
        if (_etapeEnCours > 0) _dureesMesurees[_etapeEnCours - 1] = _chronoEtape.Elapsed.TotalSeconds;
        if (numero == 1)
        {
            var lues = LireDureesDesEtapes?.Invoke();
            _dureesAttendues = lues is { Length: EtapesDuScan } && lues.All(d => d > 0 && double.IsFinite(d)) ? lues : (double[])DureesParDefaut.Clone();
            Array.Clear(_dureesMesurees);
            JaugeVisible = true;
        }
        _etapeEnCours = numero;
        _chronoEtape.Restart();
        var (debut, fin) = Bornes(_dureesAttendues, numero);
        ProgressionDuScan = debut;
        PlafondDuScan = debut + (fin - debut) * 0.95;
        VitesseDuScan = (fin - debut) / _dureesAttendues[numero - 1];
        LabelEtapeDuScan = string.Format(CultureInfo.CurrentCulture, _localize("Scan.Etape.Format"),
            numero, EtapesDuScan, _localize(cle));
    }

    private void AllumerPole(params string[] cles)
    {
        var bits = PolesLus;
        foreach (var cle in cles)
            for (var i = 0; i < Components.Count; i++)
                if (Components[i].IconKey == cle) bits |= 1 << i;
        PolesLus = bits;
    }

    private async Task MasquerLaJaugeAsync(int numero)
    {
        await Task.Delay(1100).ConfigureAwait(true);
        if (numero == _numeroDeScan && !IsScanning) JaugeVisible = false;
    }

    private async Task ScanCoeurAsync()
    {
        ReinitialiserPourUnScan();
        PolesLus = 0;

        Etape(1, "Scan.Etape.Materiel");
        var hwResult = await _detector.DetectAsync();
        if (!hwResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Detection");
            State = ScanState.Failed;
            return;
        }

        var hw = hwResult.Value!;
        _materielLu = hw;
        PopulateComponents(hw);
        // 03/10/2026 (tour G) : la version du pilote graphique, demandée au fabricant pendant le reste de l'analyse.
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

        // 24/09/2026 — le BIOS LU remplace l'inference quand il est lisible et que Windows ne le
        // contredit pas. Sinon `st` reste tel quel : l'inference, avec ses reserves.
        if (_biosNvram is not null)
            st = DrDxt.Bios.NvramBiosSettingsReader.Appliquer(
                st, await _biosNvram.ReadAsync(hw.Board.Product, hw.Bios.Version));
        _reglagesBiosLus = st.BiosLu;
        _reglagesLus = st;

        // L'ordre des etapes ci-dessous est une contrainte, pas un hasard, et il a ete paye.
        // Le verdict de retour BIOS lit la reference d'avant le voyage. Le rafraichissement, lui,
        // juge une reference caduque des que la frequence a change -- ce qui est exactement ce
        // qu'un passage XMP off -> on provoque -- et la remplace. Rafraichir d'abord effacait donc
        // la reference d'avant, et l'A/B comparait l'apres a l'apres : « aucune difference
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
        // Revue du 04/10 (P2) : hors du bloc processeur, une panne du banc ne saute plus le relevé.
        try { ReleverReglagesSuivis(EtatsSuivis(_reglagesLus, _materielLu, LireInstantReplay?.Invoke()), Horloge()); }
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

        // M3 — surface the BIOS Doctor's firmware findings (XMP/EXPO off is the single biggest
        // real gain and lives in the BIOS, not in any software tweak). GUIDANCE-only: never
        // added to the apply batch — the user changes them in the BIOS themselves. Then build
        // the personalized guide (the exact "HOW") from the same raw findings.
#if DEBUG
        var biosFindings = PopulateBiosFindings(hw, SimulerXmpOffPourLeBios ? st with { XmpExpo = TriState.Off } : st);
#else
        var biosFindings = PopulateBiosFindings(hw, st);
#endif
        PopulateBiosGuide(hw, biosFindings, st);

        // L'audit de sante — pure lecture (registre, journal d'evenements, bcdedit, WMI). Il vit
        // ici et pas dans le banc de mesure parce que la frontiere est le cout : ces familles ne
        // demandent qu'une lecture, la memoire et le reseau exigent une mesure qui dure.
        Etape(8, "Scan.Etape.Audit");
        await PopulateRepairAuditAsync();
        // L'audit de santé lit ensemble le stockage (usure, SMART) et Windows : leurs deux pôles s'allument ensemble.
        AllumerPole("Ssd", "Windows");

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
        State = ScanState.Scanning;
        Findings.Clear();
        ProvenProblemCount = 0;
        ErrorMessage = null;
        GatingNote = null;
        DegradedNote = null;
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
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
        // NS-9 — D'OU vient l'instabilite du reseau.
        //
        // Ce bloc doit rester avant le banc CPU, juste en dessous. Le banc sature tous les
        // cœurs rapides pendant plus de quarante secondes : mesurer la gigue ICMP apres lui, ce
        // serait attribuer au reseau une instabilite que le produit vient lui-meme de fabriquer.
        // Meme garde que partout ailleurs — une panne de ce cote ne fait pas echouer le scan.
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
                // de quarante secondes. Meme motif que le compte de canaux memoire, et meme garde —
                // une panne de ce cote ne doit jamais faire echouer le SCAN.
                var jeuEnCours = _gameLocator.JeuOuvert();
                // 29/09/2026 (A2) : le réglage de puissance lu voyage avec la mesure ; changé depuis, on remesure.
                var reglage = ReglageDePuissanceLu(_reglagesBiosLus);
                var refAvant = _cpuBaseline.Reference(_machineId());
                var deficit = await Task.Run(() => _cpuBaseline.DeficitPourScan(_machineId(), jeuEnCours, reglage));
                NoterChangementBios(refAvant?.ReglageBios, _cpuBaseline.Reference(_machineId())?.ReglageBios, refAvant?.TakenUtc);

                // 26/09/2026 : la référence processeur de la machine de référence (11/09 17:46, ±7,4 %) précède de
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

    // LM — la mesure memoire. Sans elle, MemoryBench serait un instrument que le produit n'appelle
    // jamais. Elle ne tourne qu'au premier scan (decision produit) -- le service relit ensuite la
    // reference enregistree -- et sur un fil de travail, parce que parcourir 384 Mo bloquerait
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
                var canaux = await Task.Run(
                    () => _memoryBaseline.ProvenChannelsForScan(_machineId(), hw.Memory, jeuEnCours));
                st = st with { MemoryProvenChannels = canaux };

                // La mesure vient d'etre prise ou relue juste au-dessus : on relit le magasin, on
                // ne remesure pas. Rien d'enregistre (mesure rejetee, banc indisponible) => la
                // ligne n'existe pas, plutot qu'un chiffre sorti de nulle part.
                //
                // Et une reference PERIMEE ne s'affiche pas non plus : le magasin garde
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
                // SCAN entier, et le client voyait « echec du scan » pour une mesure de confort.
                // Ne pas savoir combien de canaux sont prouves laisse simplement le compte a zero
                // -- c'est-a-dire rien d'affirme, ce que le reste du produit sait deja traiter.
            }
        }
        return st;
    }

    private async Task<(BenchmarkResult? Baseline, bool Degraded)> MesurerEnJeuAsync()
    {
        // Generic in-game measurement: find the game currently running (any title, no list) and
        // measure IT. No game → PID 0 → the runner fails clean → honest config-only (degraded) scan,
        // never a fabricated number. Captured for Apply's re-bench so before/after use the same game.
        _gamePid = _gameLocator.LocateRunningGamePid() ?? 0;
        var jeuReduit = _gamePid == 0 && _gameLocator.JeuOuvert();
        if (jeuReduit) _gamePid = await AttendreLeJeuAsync() ?? 0;
        // Phase 18 — les threads du jeu, observes pendant la meme fenetre que les frames (jamais avant :
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
        // 03/10/2026 (P3.1c) : la route vers le serveur que le jeu contacte, pour dire si un relais payant servirait.
        var route = pidObserve > 0 && _mesurerRoute is { } mesurerRoute
            ? mesurerRoute(pidObserve, DureeCaptureEnJeu)
            : Task.FromResult<RepairFinding?>(null);
        var memoireLibre = pidObserve > 0 && _mesurerMemoireLibre is { } mesurerMemoire
            ? mesurerMemoire(DureeCaptureEnJeu)
            : Task.FromResult<ulong?>(null);
        var dpc = pidObserve > 0 && _mesurerDpc is { } mesurerDpc
            ? Task.Run(() => mesurerDpc(DureeCaptureEnJeu))
            : Task.FromResult<RepairFinding?>(null);
        // 11/09/2026 — ce qui tourne a cote, pendant la meme fenetre. Un scan a deja affiche 164 FPS
        // pendant qu'on regardait l'ecran de DrDXT ; le meme soir, en partie avec nos compilations,
        // 79,5. Le chiffre seul ne veut rien dire : l'ecran dira ce qu'il y avait autour.
        var voisins = Task.Run(() => _voisins.Lire(pidObserve > 0 ? pidObserve : null, CancellationToken.None));
        // 26/09/2026 : un joueur qui revient sur DrDXT avant la fin réduit le jeu en pleine capture. Les images
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
        // 04/10/2026 (plan 1.5) : « tu as quitté ton jeu » est apparu sans aucun jeu ouvert (scan du 04/10 04:50), et rien
        // ne permettait de savoir quelle fenêtre avait été prise pour un jeu. La phrase nomme maintenant le programme
        // retenu : un faux positif se voit tout de suite, pour le joueur comme pour le support.
        if (jeuQuitte)
            DegradedNote = exeObserve is { } retenu
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Scan.Degraded.JeuQuitteNomme"),
                    DureeCaptureEnJeu.TotalSeconds, System.IO.Path.GetFileNameWithoutExtension(retenu))
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _localize("Scan.Degraded.JeuQuitte"), DureeCaptureEnJeu.TotalSeconds);
        else if (degraded)
            DegradedNote = _localize(jeuReduit && _gamePid == 0
                ? "Scan.Degraded.JeuReduit"
                : CleDeCouvertureManquante(benchResult.Error!.Value.Code));

        BenchmarkResult? baseline = benchResult.IsOk && !jeuQuitte ? benchResult.Value! with { ProcessusMesure = exeObserve } : null;
        return (baseline, degraded);
    }

    private void PublierLesLibellesDeMesure(BenchmarkResult? baseline)
    {
        // LA CADENCE. Mesuree a chaque scan et lue par le seul classifieur de goulot jusqu'ici.
        //
        // La garde se REUTILISE : sous vingt echantillons un p95 se confond avec le maximum —
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
        // meme filtre que le percentile — la lecon appliquee d'entree cette fois, et non apres
        // qu'une revue l'ait signalee. Compte inconnu ⇒ silence.
        PresentStallLabel = DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline) is { } attente
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.PresentStall.Measured"), attente)
            : null;

        // LA SACCADE. Le p95 des frametimes vient de la meme liste que le compte ci-dessous, avec
        // le meme filtre (`Clean`) : la valeur et la garde ne peuvent pas porter sur deux
        // populations differentes — la faute payee deux fois dans cette boucle.
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
    }

    private void PublierLeResultat(ScanResult scanResult, bool degraded)
    {
        Score = scanResult.Score;
        Coverage = scanResult.Coverage;
        // D7 : le rapport de diagnostic decrit le dernier scan. Le garder ici evite de le
        // recalculer au moment de l'export -- un rapport qui relance un scan decrirait une autre
        // machine que celle dont le client parle.
        _lastScan = scanResult;
        // Le registre (D2) : on compte les leviers lus et trouves sous-optimaux. Le grand livre
        // est la source, parce qu'il ne contient que les leviers reellement notes -- pas les
        // lignes d'information ni les guidages.
        ProvenProblemCount = scanResult.Ledger?.Count(e => e.State == LeverState.Suboptimal) ?? 0;

        foreach (var f in scanResult.Findings)
            Findings.Add(new FindingViewModel(f, _localize));

        PopulateNarrative(degraded);
    }

    private SettingsState PeuplerLesRecommandations(HardwareInventory hw, SettingsState st, BenchmarkResult? baseline, ScanResult scanResult)
    {
        // M6 — consume the per-chip profile recommendation: compose the profile-driven
        // plan, override + clamp the GPU targets (IGpuTuningTargetSource), and surface each
        // selection's honest evidence. Captured so Apply runs this plan and re-benches
        // against the same measured baseline.
        _scannedBaseline = baseline;
        _goulotScan = scanResult.Bottleneck;
        // PHASE 18 F3 — le goulot mesure entre dans l'etat avant la selection : un levier CPU ne
        // s'arme que si le processeur limite ce jeu. Un verdict provisoire (confiance basse)
        // n'arme rien : trop peu de mesure pour justifier une ecriture sur la machine.
        st = st with
        {
            GoulotMesure = scanResult.Bottleneck is { Provisional: false } goulot ? goulot.State : null,
            // 06/10/2026 (L-WU) : les heures des parties relevées, pour les heures d'activité de Windows Update. Sans relevé
            // branché : null (non lu), jamais « aucune partie ».
            HeuresDeJeu = MemoireParties?.Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        };
        _consumption = _profileConsumer.Consume(hw, st);
        OnPropertyChanged(nameof(ApercuOptimisation));
        Recommendations.Clear();
        Recommendations.CollectionChanged -= NotifierAucuneRecommandation;
        Recommendations.CollectionChanged += NotifierAucuneRecommandation;
        // 16/09/2026 : n'est recommande que ce qui sera reellement applique. Scan reel de la machine
        // de reference : 10 lignes « optimisation recommandee », dont 5 inapplicables selon le
        // catalogue et 2 reglages GPU fermes par decision, qui ne seront jamais ecrits.
        foreach (var item in _consumption.Items.Where(i => i.InExecutedPlan
                     || (i.Applicable && i.Intent == DrDxt.Profiles.ProfileIntent.GpuTune)))
            // Stamp the non-technical category tease for the LOCKED (free) view here, where the
            // localizer lives (the consumer is pure, no i18n). Free reveals the category, never how.
            Recommendations.Add(item with
            {
                CategoryLabel = string.Format(
                    _localize("Scan.Recommendation.Tease"), _localize(RecommendationCategoryKey(item.Intent))),
            });
        return st;
    }

    /// <summary>
    /// Revue du 12/09 (M1) — « deja bien reglee » se decidait sur le score /100 (>= 90), retire de
    /// tout ce que le client lit le 05/09. Il se decide sur les faits de lecture : rien a corriger
    /// (scanner, plan, GPU, BIOS, sante) et rien de non lu. Un audit absent ou un conseiller BIOS en
    /// panne n'ont rien lu : jamais « deja reglee » sur ce qu'on n'a pas vu.
    /// </summary>
    // 14/09/2026 (C1 bis) : le scanner rend `Unknown` sans constat pour ce qu'il n'a pas pu lire
    // (XMP, rafraichissement, pagefile, MMCSS) : « aucun constat » n'est pas « tout lu ». Sa
    // couverture le dit ; une couverture absente n'a rien affirme.
    // 14/09/2026 (relecture 12/20) : `Findings.Count == 0` n'etait jamais vrai en production —
    // le vrai scorer ajoutait toujours « Finding.Latency.Available » (retire le 16/09 : il ne lisait rien). Rien a
    // corriger = aucun constat au-dessus d'Info. Ce qui n'a pas ete lu reste la clause de couverture.
    // Simplification voulue : pas de clause « aucun levier sous-optimal au registre » — chaque Suboptimal de
    // LeverCatalog porte un constat Minor/Major, la mutation l'a montree redondante (verte, 589/589).
    private bool DejaReglee(ScanResult scan) =>
        scan.Findings.All(f => f.Severity == Severity.Info)
        && scan.Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 }
        // 14/09/2026 — un plan GPU ne compte pas : sinon toute machine NVIDIA de bureau (leviers GPU applicables) ne
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
