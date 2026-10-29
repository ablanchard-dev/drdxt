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

    // ═══ Le registre ═════════════════════════════════════════════════════════════════════════════
    //
    // Il ne promet rien et ne note rien : il compte. « 3 problèmes prouvés · 12 leviers lus sur 14 ». Les deux nombres
    // sont des faits de lecture, pas des jugements ; aucune note sur 100 (ses magnitudes sont des estimations d'expert,
    // et son plancher l'empêchait de dire « cette machine est mal réglée »).

    /// <summary>
    /// Le nombre de problemes de configuration constates : les leviers lus et trouves sous-optimaux, comptes dans le
    /// grand livre. « Prouve » porte sur l'existence du probleme (XMP lu desactive), jamais sur son ampleur : aucun chiffre
    /// de gain n'y entre. Pas <see cref="Findings"/>, qui porte aussi des lignes d'information et de guidage (« ReBAR non
    /// lisible sur NVIDIA ») qu'il ne faut pas compter comme des problemes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegisterText))]
    [NotifyPropertyChangedFor(nameof(HasProvenProblems))]
    // Le verdict dépend du compte : sans cette ligne il resterait figé
    // sur l'ecran apres un second scan.
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    private int _provenProblemCount;

    // Libellés de l'UI : liés via la traduction, pour qu'aucune chaîne ne soit écrite en dur dans le XAML.
    public string LabelHeaderTagline => _localize("UI.Header.Tagline");
    public string LabelAppName => _localize("UI.App.Name");
    public string LabelAvgFps => _localize("UI.Label.AvgFps");
    public string LabelP1Low => _localize("UI.Label.P1Low");
    public string LabelMargin => _localize("UI.Label.Margin");
    public string LabelScanButton => _localize("Scan.Button.Start");
    public string LabelFindings => _localize("Scan.Findings.Header");
    public string LabelRecommendationsHeader => _localize("Scan.Recommendations.Header");

    public string LabelScanDone => _localize("UI.Result.ScanDone");

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
    /// Cet hote sait-il prouver qu'un changement BIOS a pris ? Faux quand la couture n'est pas cablee : le bouton
    /// « emmene-moi dans le BIOS » ne s'affiche pas et aucun verdict n'est prononce. Expose pour qu'un test de
    /// composition voie une couture optionnelle oubliee, qui donnerait un ScanViewModel construit mais muet.
    /// </summary>
    public bool CanProveBiosChanges => _firmwareTrip is not null;

    /// <summary>Titre traduit de la section du guide BIOS personnalisé.</summary>
    public string LabelBiosGuideHeader => _localize("Scan.Bios.Guide.Header");

    /// <summary>Titre traduit et rassurant de l'échelle de secours obligatoire du guide (« si
    /// quelque chose tourne mal »), affiché dans un panneau calme, sans alarme.</summary>
    public string LabelBiosRecoveryHeader => _localize("Scan.Bios.Guide.RecoveryHeader");

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Dash : value.Trim();

    private static string Format(string template, int n) =>
        template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, n)
            : $"{n} {template}";
}
