using System.Diagnostics;
using System.Runtime.Versioning;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Bios;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Display;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>
/// L'audit de santé de la machine, pour l'écran et le rapport du joueur : il assemble les constats qui ne
/// demandent qu'une lecture (registre, journal d'événements, `bcdedit`, WMI, compteurs de performance).
/// Il ne juge pas et ne répare rien : le tri de ce qui mérite l'attention appartient à <see cref="RepairTriage"/>.
/// </summary>
/// <remarks>
/// La frontière est le coût, pas le domaine. Les mesures qui durent (banc mémoire de 12 passes, parking,
/// pertes réseau sur 3 s) restent ailleurs : les mélanger imposerait ce coût à tout appelant, y compris à
/// une interface qui veut afficher un état tout de suite.
///
/// Le BIOS n'y figure que par son identité et son interface constructeur : la matrice de qualification vit
/// dans `DrDxt.Bios`, que ce projet ne référence pas.
///
/// Chaque famille est isolée. Une lecture WMI peut échouer par intermittence ; une exception ne coûte que sa
/// famille, et laisse un constat `Unknown` qui le dit : une famille muette passerait pour une famille saine.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class RepairAuditService
{
    private readonly IServiceFailureLog _services;
    private readonly ILiveKernelReportLog _noyau;
    private readonly IUnexpectedShutdownLog _journalDesArrets;
    // Le journal des arrêts du scan en cours, lu une fois pour toutes les familles (ArretsDuScan) ; refait à chaque Lire.
    private IUnexpectedShutdownLog _arrets;
    private readonly IMemoryDiagnosticLog _testMemoire;
    private readonly IDumpCreationLog _journalDesVidages;
    // Le journal des vidages du scan en cours, lu une fois par fenêtre (VidagesDuScan) ; refait à chaque Lire.
    private IDumpCreationLog _vidages;
    // L'instant du scan : les familles qui partagent une fenêtre la comptent depuis lui, et lisent donc une fois.
    private DateTimeOffset _instantDuScan = DateTimeOffset.UtcNow;
    // Les rapports d'écran bleu (WER 1001, journal Système) : la source, et sa lecture du scan en cours.
    private readonly Func<DateTimeOffset, IReadOnlyList<DateTimeOffset>?> _journalDesRapports;
    private Func<DateTimeOffset, IReadOnlyList<DateTimeOffset>?> _rapports;
    // Les erreurs matérielles WHEA (journal Système), par nombre de jours : la source, et sa lecture du scan en cours.
    private readonly Func<int, IReadOnlyList<ErreursMaterielles.Enregistrement>?> _journalWhea;
    private Func<int, IReadOnlyList<ErreursMaterielles.Enregistrement>?> _whea;
    /// <summary>La source WMI, injectee comme les autres lecteurs : sans couture, chaque test d'audit attendrait le vrai
    /// WMI, que Windows serialise, meme pour une famille qui ne le lit pas. Le defaut est `ManagementWmiQuery`. Quatre
    /// lectures ouvrent encore WMI en direct (le pilote graphique pour le radar et pour le plancher du jeu, le fichier
    /// d'echange, le SSD interne) et ne se doublent pas en test.</summary>
    private readonly IWmiQuery _wmi;
    private readonly IWindowsUpdateLog _misesAJour;
    private readonly ILecteurWindowsUpdate? _windowsUpdate;
    private readonly Func<RepairFinding>? _heuresActives;
    private readonly Func<RepairFinding>? _defender;
    private readonly Func<RepairFinding>? _tachesEnPartie;
    private readonly Func<RepairFinding>? _jeuxSurDisqueDur;
    private readonly Func<RepairFinding>? _connexionDeJeu;
    // Les octets envoyés par programme pendant une fenêtre du scan : mesurés en production seulement, en fond.
    private readonly Func<IReadOnlyDictionary<int, long>?>? _envoisParProgramme;
    // Le type de NAT, demandé à la box par UPnP : en production seulement, en fond.
    private readonly Func<DrDxt.Detection.Network.TypeDeNat.Nat>? _typeDeNat;
    // L'accès contrôlé aux dossiers et les exclusions de Defender contre les dossiers de jeux : lus en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _preferencesDefender;
    // La version de pilote que chaque jeu installé exige dans son propre fichier : lue en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _piloteExige;
    // Les lectures de pages dures sur deux secondes : mesurées en production seulement (deux secondes de plus au scan).
    private readonly Func<double?>? _lecturesDures;
    // Les dossiers de jeux prouvés par l'exécutable du jeu, et la version installée : lu en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _jeuxProuves;
    // Le radar des régressions connues contre les mises à jour installées, les pilotes et le processeur : lu en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _radar;
    // Les traces d'autres outils de réglage dans le registre, datées : lues en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _traces;
    // Les périphériques d'entrée, leur rôle déclaré et leur liaison : lu en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _peripheriquesDEntree;
    // Les couches logicielles entre les périphériques d'entrée et le jeu (manette virtuelle, filtre tiers) : lu en production seulement.
    private readonly Func<IEnumerable<RepairFinding>>? _couchesDEntree;
    // Les interruptions et les DPC par cœur sur une seconde : mesurés en production seulement, pendant le reste de l'audit.
    private readonly Func<IReadOnlyList<DrDxt.Detection.Latency.RecensementParCoeur.Coeur>?>? _recensementParCoeur;
    // PL1/PL2 du processeur Intel par ses registres (PawnIO installé), sinon non lu.
    private readonly Func<LimiteDePuissanceLue?> _limitesPuissance;
    private readonly Func<IEnumerable<RepairFinding>>? _plantagesDeJeu;
    private readonly Func<string?>? _hwinfoQuiTourne;
    private readonly Func<bool>? _hwinfoPublie;
    private readonly IBootPerformanceLog _demarrage;
    // Sans droits (fixture `sans-admin`), un refus d'accès passerait pour « ce disque ne publie pas son compteur »,
    // « aucune zone ACPI ne répond » ou un message d'exception anglais. Sans droits, la cause dite est le manque de droits.
    private readonly Func<bool> _eleve;
    // SAM, Anti-Lag, limite d'images et température de la carte AMD, lus une fois par audit.
    private readonly Lazy<DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus> _amd;
    private readonly Lazy<(double? Celsius, string? Raison)> _intel;
    // Pas de promesse « pour la voir » : relancé en administrateur, DrDXT tente la lecture ; un
    // disque qui ne publie pas son compteur ne le publiera pas plus.
    internal static string SansDroits => Docteur.T("RA.SansDroits");

    /// <summary>Fenetre des essais de mise a jour. **Non chiffré**, alignee sur celle des arrets
    /// brutaux : un echec qui dure se voit sur trois mois.</summary>
    public static readonly TimeSpan FenetreMisesAJour = TimeSpan.FromDays(90);

    // La lecture réelle de Windows Update n'est branchée qu'ici (production) ; les tests,
    // qui passent par le constructeur à doublures, ne lisent jamais le vrai Windows Update.
    public RepairAuditService()
        : this(new WindowsServiceFailureLog(), new WerLiveKernelReportLog(),
               new WindowsUnexpectedShutdownLog(), windowsUpdate: new LecteurWindowsUpdate(),
               hwinfoQuiTourne: DrDxt.Detection.Cpu.TemperatureHwinfo.VersionQuiTourne,
               hwinfoPublie: DrDxt.Detection.Cpu.TemperatureHwinfo.MemoirePubliee,
               amd: DrDxt.Detection.Gpu.Adlx.AdlxReglages.Lire, intel: DrDxt.Detection.Gpu.TemperatureCarte.LireIntel,
               heuresActives: HeuresActivesDuPc, plantagesDeJeu: () => [.. PlantagesDeJeu.DuPc(), .. RapportsDePlantageCod.DuPc()], defender: DefenderDuPc,
               tachesEnPartie: TachesEnPartieDuPc, jeuxSurDisqueDur: () => JeuxSurDisqueDur.Juger(DossiersDeJeux.Lire()),
               connexionDeJeu: () => ConnexionDeJeu.Juger(DrDxt.Detection.Network.CartesVersInternet.Lire(), DrDxt.Detection.Network.LecteurWifi.Lire()),
               preferencesDefender: PreferencesDefenderDuPc, piloteExige: PiloteExigeDuPc,
               // Deux secondes qui ne dépendent pas du repos : mesurées pendant les autres lectures de l'audit.
               lecturesDures: EnFond(() => PressionMemoire.LecturesDuresParSeconde(TimeSpan.FromSeconds(2))),
               recensementParCoeur: EnFond(() => DrDxt.Detection.Latency.RecensementParCoeur.Mesurer(TimeSpan.FromSeconds(1))),
               envoisParProgramme: EnFond(() => new DrDxt.Detection.Network.EcouteEnvoisUdp().OctetsParProcessus(DrDxt.Detection.Network.EnvoisParProgramme.Fenetre)),
               typeDeNat: EnFond(() => DrDxt.Detection.Network.TypeDeNat.LireAsync().GetAwaiter().GetResult()),
               couchesDEntree: () => { var (appareils, classes) = Hardware.CouchesDEntree.Lire(); return [Hardware.CouchesDEntree.Juger(appareils, classes)]; },
               peripheriquesDEntree: () => { var a = Hardware.PeripheriquesDEntree.Lire(); return [Hardware.PeripheriquesDEntree.Juger(a), .. Hardware.FrequenceSouris.JugerManettes(a), Hardware.ReveilDesPeripheriques.LireEtJuger(a)]; },
               jeuxProuves: () => [Hardware.JeuxProuves.Juger(Hardware.JeuxProuves.Lire())], radar: RadarDuPc, traces: TracesDuPc) { }

    /// <summary>Le vrai, en lecture seule : les clés de la liste fermée et leur date, l'installation de Windows, le dernier
    /// démarrage (GetTickCount64 : le temps écoulé depuis), et les clés où DrDXT tient un réglage gardé.</summary>
    internal static IEnumerable<RepairFinding> TracesDuPc()
    {
        var gardes = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut())
            .Lire().Select(s => s.TweakId);
        var tenues = new HashSet<string>(DrDxt.Application.Autopilot.RevertAuDemarrage.ClesMachineDesLeviers(gardes), StringComparer.OrdinalIgnoreCase);
        return TracesDOptimiseurs.Juger(TracesDuRegistre.Lire(), InstallationDeWindows.Lire(),
            DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64), tenues);
    }

    /// <summary>Le vrai, en lecture seule : les KB installés (correctifs listés par WMI et historique), chaque carte et la version de son
    /// pilote, le nom du processeur, contre les tables en service.</summary>
    internal static IEnumerable<RepairFinding> RadarDuPc()
    {
        var kb = MisesAJourWindows.KbInstalles();
        var cartes = LectureWmiBornee.Lire<List<(string, string?)>?>(() =>
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
                List<(string, string?)> lues = [];
                using var rangees = s.Get();
                foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                    using (o)
                        if (o["Name"] is string nom) lues.Add((nom, o["DriverVersion"] as string));
                return lues;
            }
            catch (Exception) { return null; }
        }, () => null);
        string? processeur;
        using (var cle = Registry.LocalMachine.OpenSubKey(@"Hardware\Description\System\CentralProcessor\0"))
            processeur = cle?.GetValue("ProcessorNameString") as string;
        return RadarDesRegressions.Juger(DonneesEnService.Courantes, kb, cartes, processeur?.Trim());
    }

    /// <summary>Lance la mesure tout de suite, en fond, et rend sa lecture : la valeur est attendue quand on la demande.
    /// Seulement pour une mesure que la charge de l'audit ne fausse pas (mesuré le 16/10/2026 : 0 à 1 lecture dure par
    /// seconde seule comme pendant l'audit). Les états de veille, qui se lisent au repos, restent après l'audit.</summary>
    internal static Func<T> EnFond<T>(Func<T> mesure)
    {
        // Son propre thread : une mesure qui bloque deux secondes n'occupe pas le pool, et ne l'attend pas quand il est saturé.
        var enCours = Task.Factory.StartNew(mesure, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return () => enCours.GetAwaiter().GetResult();
    }

    /// <summary>Le vrai, en lecture seule : les fichiers de versions des jeux, et chaque carte avec sa version en service.</summary>
    internal static IEnumerable<RepairFinding> PiloteExigeDuPc()
    {
        var fichiers = DrDxt.Detection.Gpu.PiloteExigeParLeJeu.Trouver(DossiersDeJeux.Lire() ?? [])
            .Select(f => (f, DrDxt.Detection.Gpu.PiloteExigeParLeJeu.Contenu(f))).ToList();
        if (fichiers.Count == 0) return [];
        var cartes = LectureWmiBornee.Lire(() =>
        {
            var lues = new List<DrDxt.Detection.Gpu.PiloteExigeParLeJeu.Carte>();
            using (var s = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
            using (var rangees = s.Get())
                foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                    using (o)
                        if (o["Name"]?.ToString() is { } nom)
                            lues.Add(new(nom, o["DriverVersion"]?.ToString(), DrDxt.Detection.Gpu.VersionDuPilote.RadeonSoftwareVersion(nom)));
            return lues;
        }, () => throw new TimeoutException("WMI did not answer in time."));
        return DrDxt.Detection.Gpu.PiloteExigeParLeJeu.Juger(fichiers, cartes);
    }

    /// <summary>Le vrai, en lecture seule : MSFT_MpPreference, le chiffrement des disques des jeux, et les dossiers des lanceurs.</summary>
    internal static IEnumerable<RepairFinding> PreferencesDefenderDuPc()
    {
        var lu = PreferencesDefender.Lire();
        var jeux = DossiersDeJeux.Lire()?.Select(d => d.Chemin).ToList();
        var lettres = (jeux ?? []).Where(c => c.Length > 1 && c[1] == ':').Select(c => c[..1]).ToList();
        return [PreferencesDefender.JugerAccesControle(lu, jeux), .. PreferencesDefender.JugerExclusions(lu, jeux),
            .. ChiffrementDuDisque.Juger(ChiffrementDuDisque.Lire(), lettres)];
    }

    /// <summary>Les tâches planifiées lancées pendant une partie relevée, en lecture seule.</summary>
    internal static RepairFinding TachesEnPartieDuPc() => TachesEnPartie.Juger(DernieresExecutionsDesTaches.Lire(),
        DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList());

    /// <summary>Les analyses de Defender contre les parties relevées, en lecture seule.</summary>
    internal static RepairFinding DefenderDuPc() => AnalysesDefender.Juger(
        JournalDefender.Analyses(DateTimeOffset.UtcNow.AddDays(-JoursRedemarrages)),
        DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        JournalDefender.SeulementInactif(), JoursRedemarrages);

    /// <summary>Fenêtre des redémarrages de Windows Update comptés. **Non chiffré** : un mois de parties.</summary>
    public const int JoursRedemarrages = 30;

    /// <summary>Le vrai, en lecture seule : le registre (sans droits), le relevé des parties, le journal Système.</summary>
    internal static RepairFinding HeuresActivesDuPc() => HeuresActives.Juger(
        new RegistryProvider().GetHeuresActives(),
        DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        RedemarragesWindowsUpdate.Depuis(DateTimeOffset.UtcNow.AddDays(-JoursRedemarrages)),
        JoursRedemarrages, TimeZoneInfo.Local);

    public RepairAuditService(IServiceFailureLog services, ILiveKernelReportLog noyau,
        IUnexpectedShutdownLog? arrets = null, IMemoryDiagnosticLog? testMemoire = null,
        IDumpCreationLog? vidages = null, IWmiQuery? wmi = null, IWindowsUpdateLog? misesAJour = null,
        IBootPerformanceLog? demarrage = null, Func<DrDxt.Detection.Cpu.TemperatureHwinfo.Lecture?>? temperatureHwinfo = null,
        ILecteurWindowsUpdate? windowsUpdate = null, Func<string?>? hwinfoQuiTourne = null, Func<bool>? hwinfoPublie = null,
        Func<bool>? eleve = null, Func<DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus>? amd = null,
        Func<RepairFinding>? heuresActives = null, Func<IEnumerable<RepairFinding>>? plantagesDeJeu = null,
        Func<(double? Celsius, string? Raison)>? intel = null, Func<RepairFinding>? defender = null,
        Func<RepairFinding>? tachesEnPartie = null, Func<RepairFinding>? jeuxSurDisqueDur = null,
        Func<RepairFinding>? connexionDeJeu = null, Func<LimiteDePuissanceLue?>? limitesPuissance = null,
        Func<IEnumerable<RepairFinding>>? preferencesDefender = null, Func<IEnumerable<RepairFinding>>? piloteExige = null,
        Func<double?>? lecturesDures = null,
        Func<IReadOnlyList<DrDxt.Detection.Latency.RecensementParCoeur.Coeur>?>? recensementParCoeur = null,
        Func<IEnumerable<RepairFinding>>? couchesDEntree = null,
        Func<IEnumerable<RepairFinding>>? peripheriquesDEntree = null,
        Func<IEnumerable<RepairFinding>>? jeuxProuves = null,
        Func<IReadOnlyDictionary<int, long>?>? envoisParProgramme = null,
        Func<DrDxt.Detection.Network.TypeDeNat.Nat>? typeDeNat = null,
        Func<IEnumerable<RepairFinding>>? radar = null,
        Func<IEnumerable<RepairFinding>>? traces = null,
        Func<DateTimeOffset, IReadOnlyList<DateTimeOffset>?>? rapportsEcransBleus = null,
        Func<int, IReadOnlyList<ErreursMaterielles.Enregistrement>?>? erreursMaterielles = null)
    {
        _journalWhea = erreursMaterielles ?? (jours => ErreursMaterielles.Lire(jours));
        _whea = _journalWhea;
        _journalDesRapports = rapportsEcransBleus ?? EcransBleus.LireRapports;
        _rapports = _journalDesRapports;
        _typeDeNat = typeDeNat;
        _envoisParProgramme = envoisParProgramme;
        _radar = radar;
        _traces = traces;
        _lecturesDures = lecturesDures;
        _jeuxProuves = jeuxProuves;
        _peripheriquesDEntree = peripheriquesDEntree;
        _couchesDEntree = couchesDEntree;
        _recensementParCoeur = recensementParCoeur;
        _piloteExige = piloteExige;
        _preferencesDefender = preferencesDefender;
        _connexionDeJeu = connexionDeJeu;
        _limitesPuissance = limitesPuissance ?? DrDxt.Detection.Cpu.TemperaturePawnIo.LireLimites;
        _jeuxSurDisqueDur = jeuxSurDisqueDur;
        _defender = defender;
        _tachesEnPartie = tachesEnPartie;
        // La température d'une carte Intel par IGCL ; sans lecteur (tests), rien n'est lu sur la vraie carte.
        _intel = new Lazy<(double? Celsius, string? Raison)>(() =>
        {
            try { return intel?.Invoke() ?? (null, Docteur.T("RA.Lecteur.Igcl")); }
            catch (Exception ex) { return (null, Docteur.T("RA.Lecteur.IgclInterrompu", ex.GetType().Name)); }
        });
        // Comme les heures d'activité, le vrai journal n'est lu qu'en production.
        _plantagesDeJeu = plantagesDeJeu;
        // Sans lecteur (tests), rien n'est lu sur la vraie carte.
        _amd = new Lazy<DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus>(() =>
        {
            try { return amd?.Invoke() ?? DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu(Docteur.T("RA.Lecteur.Adlx")); }
            catch (Exception ex) { return DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu(Docteur.T("RA.Lecteur.AdlxInterrompu", ex.GetType().Name)); }
        });
        _heuresActives = heuresActives;
        _eleve = eleve ?? (() => new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        _hwinfoPublie = hwinfoPublie;
        _windowsUpdate = windowsUpdate;
        _hwinfoQuiTourne = hwinfoQuiTourne;
        // HWiNFO s'il publie, sinon PawnIO s'il est installé ; chaque lecture nomme sa source.
        _temperatureHwinfo = temperatureHwinfo ?? DrDxt.Detection.Cpu.TemperatureProcesseur.Lire;
        _demarrage = demarrage ?? new WindowsBootPerformanceLog();
        _wmi = wmi ?? new ManagementWmiQuery();
        _misesAJour = misesAJour ?? new WindowsUpdateEventLog();
        _services = services;
        _noyau = noyau;
        _journalDesArrets = arrets ?? new WindowsUnexpectedShutdownLog();
        _arrets = _journalDesArrets;
        _testMemoire = testMemoire ?? new WindowsMemoryDiagnosticLog();
        _journalDesVidages = vidages ?? new WindowsDumpCreationLog();
        _vidages = _journalDesVidages;
    }

    /// <summary>Depuis quand on regarde les echecs de services. **Non chiffré.** Trente jours
    /// laissent voir une recurrence sans remonter a une configuration qui n'existe plus.</summary>
    public static readonly TimeSpan FenetreEchecs = TimeSpan.FromDays(30);

    /// <summary>Depuis quand on regarde les arrets brutaux. **Non chiffré**, et plus large que
    /// la fenetre des services a dessein : un arret brutal est rare, et trente jours pourraient
    /// n'en montrer aucun sur une machine qui en fait un par mois, c'est-a-dire une machine dont
    /// l'utilisateur, lui, sait tres bien qu'elle le fait.</summary>
    public static readonly TimeSpan FenetreArrets = TimeSpan.FromDays(90);

    /// <summary>Au-dela de ce nombre d'arrets brutaux dans la fenetre, le constat cesse d'etre une
    /// observation et demande le regard de l'utilisateur.
    ///
    /// **ce seuil est emprunte, pas derive.** C'est celui que la famille des incidents noyau
    /// utilise deja dans ce fichier pour la meme raison : « repete a l'identique, ce n'est plus du
    /// bruit ». Aucune mesure ne dit que trois est le bon nombre pour des arrets brutaux ; prendre
    /// le seuil deja en vigueur est un choix de coherence, et il est dit ici plutot que subi.</summary>
    private const int SeuilArretsRepetes = 3;

    public IReadOnlyList<RepairFinding> Lire()
    {
        _fixesDuScan = new();
        _instantDuScan = DateTimeOffset.UtcNow;
        _vidages = new VidagesDuScan(_journalDesVidages);
        _rapports = new LectureDuScan<DateTimeOffset>(_journalDesRapports, d => d, _instantDuScan - FenetreArrets).Depuis;
        // La source compte en jours : elle lit un jour entier de plus que la fenêtre, et la lecture garde la fenêtre exacte.
        var whea = new LectureDuScan<ErreursMaterielles.Enregistrement>(
            depuis => _journalWhea((int)Math.Ceiling((DateTimeOffset.UtcNow - depuis).TotalDays)), e => e.Quand, _instantDuScan - FenetreArrets);
        _whea = jours => whea.Depuis(DateTimeOffset.UtcNow.AddDays(-jours));
        // Une minute de marge : chaque famille calcule sa fenêtre un peu plus tard, donc plus courte.
        _arrets = new ArretsDuScan(_journalDesArrets, DateTimeOffset.UtcNow - FenetreArrets - TimeSpan.FromMinutes(1));
        var constats = new List<RepairFinding>();
        constats.AddRange(Isoler("POWER", Power));
        // La frequence se lit seule, au calme : elle compte les cœurs charges a plus de 60 %, et lue pendant que les autres
        // familles interrogent WMI et les journaux, elle mesurait l'audit lui-meme (cœurs en turbo, bridage fausse). Deux
        // secondes de plus au scan, pour une mesure de la machine.
        constats.AddRange(Isoler("CPU", Cpu));
        // Les L3 et les processeurs qu'ils servent : sur un X3D à deux CCD, le bloc à cache empilé.
        constats.AddRange(Isoler("CPU", () => CachesL3.Juger(CachesL3.Lire())));
        // Ce que coûte une lecture de l'horloge haute résolution, et si elle recule d'un cœur à l'autre (une seconde environ).
        constats.AddRange(Isoler("CPU", () => [DrDxt.Detection.Cpu.HorlogeQpc.Juger(DrDxt.Detection.Cpu.HorlogeQpc.Mesurer())]));
        // Le garde de stabilité des Core de 13e et 14e génération, avant tout conseil de puissance.
        constats.AddRange(Isoler("CPU", () =>
        {
            var (nom, revision, identifiant) = DrDxt.Detection.Cpu.MicrocodeRaptorLake.Lire();
            return DrDxt.Detection.Cpu.MicrocodeRaptorLake.Juger(nom, revision, identifiant);
        }));
        // La génération AMD par CPUID : le portail des lectures propres à AMD.
        constats.AddRange(Isoler("CPU", () =>
        {
            var (fabricant, signature) = DrDxt.Detection.Cpu.Cpuid.Lire();
            return GenerationAmd.Juger(fabricant, signature);
        }));
        // La limite de puissance effective, lue dans les registres quand PawnIO est là ; jamais écrite.
        constats.AddRange(Isoler("CPU", () => [LimiteDePuissance.Juger(_limitesPuissance(), portable: Batterie() == true)]));
        // La chaleur relevée en partie, reliée à la limite de puissance lue (plafond conseillé).
        constats.AddRange(Isoler("CPU", () => DrDxt.Detection.Cpu.ChaleurEnPartie.Juger(
            DrDxt.Detection.Cpu.ChaleurEnPartie.Lire(DossierDonnees.Chemin("parties.json")), _limitesPuissance(),
            // Le BIOS lu au dernier scan, pour nommer la valeur de la limite de courant au lieu d'un conseil générique.
            DrDxt.Detection.Cpu.ChaleurEnPartie.LireBios(DossierDonnees.Chemin(ChaleurEnPartie.FichierBios), DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur),
            DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur, intel: DrDxt.Detection.Cpu.TemperaturePawnIo.ProcesseurIntel(),
            portable: Batterie() == true)));
        // Un ventilateur de la carte qui s'arrête en partie, ou qui ne tourne plus d'une partie à l'autre.
        constats.AddRange(Isoler("THERMIQUE", () => VentilateursEnPartie.Juger(
            DrDxt.Detection.Cpu.ChaleurEnPartie.Lire(DossierDonnees.Chemin("parties.json"))
                .Select(p => new VentilateursEnPartie.PartieVue(p.Jeu, p.Fin, p.Ventilateurs ?? [])).ToList())));
        // La carte freinée par la chaleur en partie, dite par son pilote.
        constats.AddRange(Isoler("GPU", () => DrDxt.Detection.Gpu.CarteChaudeEnPartie.Juger(
            DrDxt.Detection.Gpu.CarteChaudeEnPartie.Lire(DossierDonnees.Chemin("parties.json")))));
        // Les fils de rendu de Call of Duty, lus dans son fichier (jamais écrits), contre les cœurs performants.
        constats.AddRange(Isoler("CPU", FilsDeRenduCallOfDuty));
        // Curve Optimizer sur un Ryzen de bureau, en guide ; jamais écrit.
        constats.AddRange(Isoler("CPU", () =>
        {
            var carte = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Hardware\Description\System\Bios")?.GetValue("BaseBoardManufacturer") as string;
            // Le garde de stabilité : jamais de baisse de tension conseillée sur une machine qui s'arrête, ou non lue.
            var maintenant = DateTimeOffset.UtcNow;
            var (arrets, autres) = IncidentsDuGarde(maintenant);
            // Le Curve Optimizer lu dans le BIOS nommé au dernier scan ; le témoin SMU n'est pas lu (module PawnIO AMD absent).
            var bios = DrDxt.Detection.Cpu.ChaleurEnPartie.LireBios(DossierDonnees.Chemin(ChaleurEnPartie.FichierBios),
                DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur)?.Reglages;
            var lu = LectureCurveOptimizer.Croiser(LectureCurveOptimizer.DepuisBios(bios), echelleSmu: null, margesParCoeur: null);
            return CurveOptimizerAmd.Juger(DrDxt.Detection.Cpu.MicrocodeRaptorLake.Lire().Nom, carte, arrets, maintenant, autres, lu) is { } c ? [c] : [];
        }));
        constats.AddRange(Isoler("SERVICES", Services));
        // Une seule requete des services pour les deux familles de securite (mesure du 25/09 : 200 a 340 ms
        // chacune) ; lue a la premiere demande, le temps de ce Lire seulement.
        Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>? servicesLus = null;
        Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> ServicesLus() => servicesLus ??= ServicesSysteme();
        constats.AddRange(Isoler("SERVICES", () => ServicesRemplacables(ServicesLus())));
        constats.AddRange(Isoler("STREAM", () => DrDxt.Detection.Diffusion.ObsStudio.Juger(Directory.Exists(DrDxt.Detection.Diffusion.ObsStudio.Dossier),
            DrDxt.Detection.Diffusion.ObsStudio.LireReglages(DrDxt.Detection.Diffusion.ObsStudio.Dossier),
            DrDxt.Detection.Diffusion.ObsStudio.LireSessions(DrDxt.Detection.Diffusion.ObsStudio.Dossier, DateTime.Now))));
        constats.AddRange(Isoler("GPU DRIVER", () => [DrDxt.Detection.Gpu.PiloteRTCore.Juger(InventaireMachine.ServicesDuRegistre(), DrDxt.Detection.Gpu.PiloteRTCore.Charge)]));
        constats.AddRange(Isoler("SERVICES", () => CheminsNonCites(ServicesLus())));
        constats.AddRange(Isoler("TACHES", () => TachesRemplacables(TachesLues(), CheminProtege.Verifier)));
        constats.AddRange(Isoler("MMCSS", Mmcss));
        constats.AddRange(Isoler("STABILITE", () => [CrashDumpPolicy.Juger(
            Dword(@"SYSTEM\CurrentControlSet\Control\CrashControl", "CrashDumpEnabled"))]));
        constats.AddRange(Isoler("BCD", Bcd));
        constats.AddRange(Isoler("STABILITE", IncidentsNoyau));
        constats.AddRange(Isoler("STABILITE", ArretsBrutaux));
        constats.AddRange(Isoler("STABILITE", TestMemoire));
        // Ce qui ralentit le démarrage (mesuré par Windows) et la place libre du disque système.
        // La mesure du dernier démarrage est lue une fois : elle nomme ses programmes lents, que le récapitulatif ne
        // renomme pas, puis elle est jugée plus bas.
        var ralentisseurs = DemarrageEtDisque.LireRalentisseurs();
        // Lue hors d'Isoler : une lecture qui lève devient un échec de lecture, que les familles du démarrage disent,
        // au lieu de faire tomber tout l'audit.
        Result<IReadOnlyList<MesureDemarrage>> mesuresDemarrage;
        try { mesuresDemarrage = _demarrage.Lire(); }
        catch (Exception ex) { mesuresDemarrage = Result<IReadOnlyList<MesureDemarrage>>.Fail(ErrorCodes.DetectionFailed, ex.Message); }
        constats.AddRange(Isoler("DEMARRAGE", () => DemarrageEtDisque.JugerDemarrage(ralentisseurs, dejaNommes: mesuresDemarrage.IsOk
            ? DemarrageMesure.Nommes(mesuresDemarrage.Value!, DateTimeOffset.UtcNow, FenetreMisesAJour) : null)));
        // Ce qui se lance avec Windows, lu seulement.
        constats.AddRange(Isoler("DEMARRAGE", () => ProgrammesAuDemarrage.Juger(ProgrammesAuDemarrage.Lire(), ralentisseurs)));
        constats.AddRange(Isoler("STOCKAGE", () =>
        {
            var systeme = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return systeme.IsReady ? DemarrageEtDisque.JugerDisqueSysteme(systeme.Name.TrimEnd('\\'), systeme.AvailableFreeSpace, systeme.TotalSize) : [];
        }));
        constats.AddRange(Isoler("STOCKAGE", () => DemarrageEtDisque.JugerCacheDeShaders(DemarrageEtDisque.LireCacheDeShaders())));
        // Les erreurs matérielles WHEA, premier signe d'une marge qui manque.
        constats.AddRange(Isoler("STABILITE", () =>
        {
            var whea = _whea(90);
            // Le cœur ou le lien qui a relevé chaque erreur, à côté du compte.
            return [.. ErreursMaterielles.Juger(whea), .. ErreursMaterielles.Attribuer(whea)];
        }));
        constats.AddRange(Isoler("STABILITE", CreationDeVidage));
        constats.AddRange(Isoler("STABILITE", EcransBleusCroises));
        // Les autres pannes notées par Windows (carte relancée, disque, NTFS, pilote, mémoire épuisée), et jusqu'où le
        // journal remonte : un journal de trois jours ne prouve pas « aucune panne » sur la fenêtre.
        constats.AddRange(Isoler("STABILITE", () =>
        {
            var maintenant = DateTimeOffset.Now;
            var depuis = maintenant - CandidateSelector.FenetreInstabilite;
            return PannesDuJournal.Juger(PannesDuJournal.Lire(depuis), PannesDuJournal.DebutDuJournal(), depuis, maintenant);
        }));
        if (_plantagesDeJeu is { } pj) constats.AddRange(Isoler("STABILITE", pj));
        constats.AddRange(Isoler("STABILITE", AvantLesIncidents));
        // Le dernier témoin d'incident écrit par le service : ce que Windows a noté juste avant.
        constats.AddRange(Isoler("STABILITE", () => TemoinDIncident.Juger(TemoinDIncident.LireTous(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDxt", TemoinDIncident.Dossier),
            f => CheminProtege.Verifier(f) is null), DateTimeOffset.Now)));
        constats.AddRange(Isoler("STABILITE", PointSain));
        constats.AddRange(Isoler("DISPLAY", Display));
        // Les superpositions matérielles coupées par un ancien réglage (OverlayTestMode).
        constats.AddRange(Isoler("DISPLAY", () => { var (lu, v) = DrDxt.Detection.Display.SuperpositionsMaterielles.Lire(); return [DrDxt.Detection.Display.SuperpositionsMaterielles.Juger(lu, v)]; }));
        // Lus une fois pour la topologie et la couleur ; une lecture qui lève vaut « non lus », comme dans Isoler.
        IReadOnlyList<DrDxt.Detection.Display.CheminsDAffichage.Chemin>? chemins = null;
        try { chemins = DrDxt.Detection.Display.CheminsDAffichage.Lire(); } catch (Exception) { /* chemins null : non lus */ }
        constats.AddRange(Isoler("DISPLAY", () => [DrDxt.Detection.Display.EcransDupliques.Juger(chemins)]));
        constats.AddRange(Isoler("DISPLAY", () => DrDxt.Detection.Display.CouleurDeSortie.Juger(DrDxt.Detection.Display.CouleurDeSortie.Lire(chemins))));
        // La fréquence que chaque souris USB annonce.
        constats.AddRange(Isoler("LATENCE", () => FrequenceSouris.LireEtJuger(_wmi)));
        // Le cœur que les pilotes occupent le plus.
        if (_recensementParCoeur is { } rc)
            constats.AddRange(Isoler("LATENCE", () => [DrDxt.Detection.Latency.RecensementParCoeur.Juger(rc())]));
        constats.AddRange(Isoler("LATENCE", () => LimiteImagesHorsNvidia(CartesLues(), _amd.Value)));
        constats.AddRange(Isoler("LATENCE", () => AntiLagAmd(CartesLues(), _amd.Value)));
        constats.AddRange(Isoler("GPU DRIVER", GpuDriver));
        // Le pilote graphique 32 bits de chaque vraie carte (un lanceur 32 bits reste noir sans lui).
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.PiloteGraphique32Bits.Juger(DrDxt.Detection.Gpu.PiloteGraphique32Bits.Lire(_wmi), File.Exists)));
        // Le lien PCIe de chaque carte, et un écran branché sur la carte mère.
        // Le Resizable BAR prouvé par la plage que Windows a allouée à chaque carte.
        constats.AddRange(Isoler("GPU", () => DrDxt.Detection.Gpu.FenetreReBar.LireEtJuger(_wmi)));
        constats.AddRange(Isoler("GPU", () => DrDxt.Detection.Gpu.BranchementDeLaCarte.LireLiens(_wmi)
            .SelectMany(l => DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerLien(l, Batterie()))));
        // Le jeu passe-t-il par le Wi-Fi ?
        if (_connexionDeJeu is { } cj)
            constats.AddRange(Isoler("RESEAU", () => [cj()]));
        if (_typeDeNat is { } nat)
            constats.AddRange(Isoler("RESEAU", () => [DrDxt.Detection.Network.TypeDeNat.Juger(nat())]));
        // Qui occupe la montée pendant le scan.
        if (_envoisParProgramme is { } ep)
            constats.AddRange(Isoler("RESEAU", () => [DrDxt.Detection.Network.EnvoisParProgramme.Juger(ep(), DrDxt.Detection.Network.EnvoisParProgramme.Fenetre,
                DrDxt.Detection.Network.EnvoisParProgramme.NomDuProcessus)]));
        // La vitesse ou le duplex forcés de chaque carte filaire, contre le lien négocié.
        constats.AddRange(Isoler("RESEAU", () => DrDxt.Detection.Network.VitesseDeLaCarte.Juger(DrDxt.Detection.Network.VitesseDeLaCarte.Lire(_wmi))));
        // Les jeux posés sur un disque dur, et le lien PCIe de chaque SSD NVMe.
        if (_jeuxSurDisqueDur is { } jd)
            constats.AddRange(Isoler("STOCKAGE", () => [jd()]));
        constats.AddRange(Isoler("STOCKAGE", () => DrDxt.Detection.Gpu.BranchementDeLaCarte.LireLiensNvme(_wmi)
            .SelectMany(DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerLienSsd)));
        constats.AddRange(Isoler("GPU", () =>
        {
            var ecrans = new Win32DisplayInfo().GetAllDisplays() is { IsOk: true } e ? e.Value! : [];
            return DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerEcrans(ecrans.Select(d => d.Adaptateur).OfType<string>().ToList(),
                CartesLues(), Batterie(), ecrans.FirstOrDefault(d => d.IsPrimaryGameDisplay)?.Adaptateur);
        }));
        // Les protections contre les failles d'exécution spéculative, en information ; jamais un levier.
        constats.AddRange(Isoler("SECURITE", () => [AttenuationsProcesseur.Juger(AttenuationsProcesseur.Lire())]));
        // L'intégrité de la mémoire, dite dans les deux sens (coût et protection), jamais écrite.
        constats.AddRange(Isoler("SECURITE", () =>
        {
            var r = _wmi.QueryInNamespace(@"\\.\root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            if (!r.IsOk || r.Value!.FirstOrDefault() is not { } l) return IntegriteMemoire.Juger(null, null);
            var services = l.Properties.TryGetValue("SecurityServicesRunning", out var v) && v is Array a
                ? a.Cast<object>().Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToList() : null;
            return IntegriteMemoire.Juger(l.GetInt("VirtualizationBasedSecurityStatus"), services);
        }));
        // Un logiciel qui coûte des images, lu dans ses réglages.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.EnregistrementNvidia.Juger(DrDxt.Detection.Gpu.EnregistrementNvidia.LireReglages())));
        // La surcouche DLSS et « Ultra », des choix du joueur nommés sur carte NVIDIA, jamais posés.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.OptionsNvidiaDuJoueur.Juger(CartesLues())));
        // Les paquets de pilote d'affichage gardés par Windows et les cartes déconnectées restées déclarées.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.MagasinDePilotes.Juger(
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-drivers /class Display"),
            // Sans « /connected », pnputil liste aussi les cartes absentes, et le pilote d'une carte fantôme
            // passait pour « en service ».
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-devices /class Display /connected"),
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-devices /class Display /disconnected"),
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-devices /class Display /connected /drivers"))));
        // Une stratégie qui interdit l'installation d'un pilote, rapportée à la carte qu'elle vise vraiment.
        constats.AddRange(Isoler("GPU DRIVER", () => InstallationDePilote.Juger(InstallationDePilote.Lire(), CartesEtIdentifiants())));
        constats.AddRange(Isoler("PAGEFILE", Pagefile));
        constats.AddRange(Isoler("BIOS", BiosIdentite));
        // Les saccades du fTPM d'AMD : la génération par CPUID, le fabricant du TPM, la date du BIOS ; jamais une méthode du firmware.
        constats.AddRange(Isoler("BIOS", () =>
        {
            var (fabricant, signature) = DrDxt.Detection.Cpu.Cpuid.Lire();
            var generation = signature is { } s ? GenerationAmd.Reconnaitre(fabricant, s) : null;
            if (generation is null) return [];
            var tpm = _wmi.QueryInNamespace(@"\\.\root\CIMV2\Security\MicrosoftTpm", "SELECT ManufacturerId FROM Win32_Tpm");
            // Au jour seul : convertie en heure locale, une date sans heure recule d'un jour à l'ouest de Greenwich.
            var dateBios = InventaireDesPilotes.DateDmtf(WmiFixe(RequeteBios, "ReleaseDate"));
            return FtpmAmd.Juger(generation, tpm.IsOk ? (uint?)tpm.Value!.FirstOrDefault()?.GetInt("ManufacturerId") : null, tpm.IsOk, dateBios);
        }));
        // Les lectures AMD profondes d'un Ryzen : timings appliqués et réglé contre appliqué (registres UMC en SMN), table PM
        // (SMU), BIOS AMD nommé. Module de lecture AMD absent : une seule ligne le dit ; installé, chaque constat a sa ligne.
        constats.AddRange(Isoler("CPU", () =>
        {
            var (fabricant, signature) = DrDxt.Detection.Cpu.Cpuid.Lire();
            var generation = signature is { } s ? GenerationAmd.Reconnaitre(fabricant, s) : null;
            // Les lectures finies, PawnIO est refermé : un pilote de lecture de registres resté chargé pendant les parties est ce
            // que des anti-triches refusent (même règle que le service pour Intel).
            try { return LecturesAmdProfondes.Juger(generation, DrDxt.GpuNative.PawnIo.PawnIoAmd.Installe(), () => LecturesAmdDetaillees(generation!)).ToList(); }
            finally { DrDxt.GpuNative.PawnIo.PawnIoAmd.FermerTout(); }
        }));
        constats.AddRange(Isoler("THERMIQUE", ThermiqueGpu));
        constats.AddRange(Isoler("STOCKAGE", UsureDisques));
        if (_jeuxProuves is { } jeuxProuvesLu)
            constats.AddRange(Isoler("STOCKAGE", jeuxProuvesLu));
        // Un firmware de disque que son fabricant a corrigé depuis (table de bulletins datés).
        constats.AddRange(Isoler("STOCKAGE", () =>
        {
            var r = _wmi.QueryInNamespace(@"\\.\root\Microsoft\Windows\Storage", "SELECT FriendlyName, FirmwareVersion FROM MSFT_PhysicalDisk");
            return DefautsConnus.Juger(r.IsOk ? r.Value!.Select(l => (l.GetString("FriendlyName") ?? "", l.GetString("FirmwareVersion"))).ToList() : null);
        }));
        // L'usure de la batterie d'un portable ; rien sur un PC fixe.
        constats.AddRange(Isoler("POWER", () => DrDxt.Detection.Hardware.UsureBatterie.LireEtJuger(_wmi)));
        constats.AddRange(Isoler("PERIPHERIQUES", Peripheriques));
        if (_peripheriquesDEntree is { } peripheriquesDEntreeLu)
            constats.AddRange(Isoler("PERIPHERIQUES", peripheriquesDEntreeLu));
        if (_couchesDEntree is { } couchesDEntreeLu)
            constats.AddRange(Isoler("PERIPHERIQUES", couchesDEntreeLu));
        constats.AddRange(Isoler("PERIPHERIQUES", () => InventaireDesPilotes.Juger(PilotesLus(), DateTime.Now)));
        constats.AddRange(Isoler(SuspensionUsb.Domaine, SuspensionSelectiveUsb));
        constats.AddRange(Isoler("WINDOWS UPDATE", MisesAJour));
        // Ce qui attend dans Windows Update, pas seulement ce qui a échoué.
        if (_windowsUpdate is { } wu)
            constats.AddRange(Isoler("WINDOWS UPDATE", () =>
            {
                var lu = wu.Lire();
                return [MisesAJourEnAttente.Juger(lu.IsOk ? lu.Value : null, DateTimeOffset.Now, lu.Error?.Message),
                    // La nouvelle version proposée au clic (« Télécharger et installer »). Lecture entière ratée :
                    // le constat d'au-dessus dit déjà « non lu ».
                    .. lu.IsOk
                        ? MisesAJourEnAttente.JugerProposees(lu.Value!.Proposees,
                            Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                                "DisplayVersion", null) as string)
                        : []];
            }));
        if (_radar is { } rd)
            constats.AddRange(Isoler(RadarDesRegressions.Domaine, rd));
        if (_traces is { } tr)
            constats.AddRange(Isoler(TracesDOptimiseurs.Domaine, tr));
        // Les heures d'activité contre les heures des parties relevées.
        if (_heuresActives is { } ha)
            constats.AddRange(Isoler("WINDOWS UPDATE", () => [ha()]));
        if (_piloteExige is { } pe)
            constats.AddRange(Isoler("GPU DRIVER", pe));
        if (_preferencesDefender is { } pd)
            constats.AddRange(Isoler("SECURITE", pd));
        if (_defender is { } df)
            constats.AddRange(Isoler(AnalysesDefender.Domaine, () => [df()]));
        if (_tachesEnPartie is { } tp)
            constats.AddRange(Isoler("TACHES", () => [tp()]));
        constats.AddRange(Isoler("DEMARRAGE", () =>
        {
            var lus = mesuresDemarrage;
            var bios = _demarrage.LireTempsBios();
            return [
                .. lus.IsOk
                    ? DemarrageMesure.Juger(lus.Value!, DateTimeOffset.UtcNow, FenetreMisesAJour)
                    : [new RepairFinding("DEMARRAGE", "mesure de Windows", null, null, RepairStatus.Unknown,
                        _eleve() ? Docteur.T("RA.JournalDemarragesNonLu", lus.Error?.Message) : SansDroits)],
                .. bios.IsOk
                    ? DemarrageMesure.JugerBios(bios.Value!)
                    : [new RepairFinding("BIOS", "temps du BIOS", null, null, RepairStatus.Unknown,
                        Docteur.T("RA.JournalSystemeNonLu", bios.Error?.Message))],
            ];
        }));
        return constats;
    }

    /// <summary>Execute une famille jusqu'au bout et, si elle leve, la remplace par un constat qui
    /// nomme l'echec. Les constats deja produits avant l'exception sont abandonnes : une famille a
    /// moitie lue rendrait un bilan partiel qui passerait pour complet.</summary>
    private static IReadOnlyList<RepairFinding> Isoler(string domaine, Func<IEnumerable<RepairFinding>> famille)
    {
        try { return famille().ToList(); }
        catch (Exception ex)
        {
            return
            [
                new RepairFinding(domaine, "lecture de la famille", null, null, RepairStatus.Unknown,
                    Docteur.T("RA.FamilleEchec", ex.GetType().Name, ex.Message)),
            ];
        }
    }

    // ── POWER ──────────────────────────────────────────────────────────────────────────────
    // Le nom du plan ne suffit pas : deux plans nommes « Equilibre » peuvent porter des reglages
    // differents. On lit le GUID, puis chaque sous-valeur processeur contre le defaut de sa propre
    // personnalite : c'est la que se voient les degats d'un ancien optimiseur, sans connaitre son
    // nom. Le parking de coeurs reste dans l'outil : c'est un echantillonnage de 3 s, pas une
    // lecture.
    private IEnumerable<RepairFinding> Power()
    {
        var plan = new PowerProvider().GetActiveSchemeGuid();
        var planGuid = plan.IsOk ? plan.Value?.Trim('{', '}').ToLowerInvariant() : null;

        yield return ModeAlimentation.JugerPlan(planGuid);
        // Le mode d'alimentation de Windows 11, qui ne joue qu'avec Équilibré ou un plan
        // qui en dérive : jugé par la personnalité du plan.
        var personnaliteDuPlan = Guid.TryParse(planGuid, out var g) ? PowerSettingsReader.Personnalite(g)?.ToString() : null;
        foreach (var m in ModeAlimentation.JugerMode(personnaliteDuPlan, OverlaySecteur(), Batterie())) yield return m;

        if (planGuid is null || !Guid.TryParse(planGuid, out var planActif)) yield break;

        // Les valeurs se lisent dans le plan actif, leur défaut dans sa personnalité : un plan personnalisé a les deux différents.
        var (personnalite, planLu) = CleDeLecture(planActif, PowerSettingsReader.Personnalite);
        var portable = Batterie() == true;
        foreach (var v in new PowerSettingsReader().LireProcesseur(personnalite, planLu))
        {
            var (statut, phrase) = JugerReglageProcesseur(v, portable);
            // Un ecart sans geste est un ecart invisible. `RepairTriage.Trier` range dans
            // « verifies » (comptes, jamais affiches) tout ce qui n'est ni suspect ni nuisible et
            // ne porte aucune action. Un `NonDefault` muet finirait donc dans le meme sac qu'un
            // reglage identique au defaut : le minimum processeur a 100 %, la signature des scripts
            // gaming, serait lu, compte, et jamais montre. La recommandation est la seule facon de
            // le rendre visible sans le declarer nuisible : la doctrine « different n'est pas
            // endommage » interdit `Suspicious`, elle n'impose pas le silence.
            // DISPLAY suit la meme regle.
            yield return new RepairFinding("POWER", v.Nom,
                v.ValeurAc is { } a ? Lisible(v.Nom, a, v.Unite) : null,
                v.DefautAc is { } d ? Lisible(v.Nom, d, v.Unite) : null,
                statut, phrase,
                statut is RepairStatus.NonDefault or RepairStatus.Suspicious ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    /// <summary>Pur : la personnalité dont se lisent les défauts, et le plan dont se lisent les valeurs. Personnalité non lue :
    /// le plan lui-même (un plan Windows est sa propre personnalité ; sinon la lecture du défaut échoue et se dit non lue).</summary>
    internal static (Guid Personnalite, Guid Plan) CleDeLecture(Guid planActif, Func<Guid, Guid?> personnaliteDe)
        => (personnaliteDe(planActif) ?? planActif, planActif);

    /// <summary>Pur : le jugement d'un réglage processeur contre le défaut de sa personnalité. Un écart reste un écart, il ne dit
    /// pas que la valeur est mauvaise. Un frein (plafond de fréquence, refroidissement passif) n'est « à corriger » que s'il
    /// s'écarte de ce défaut, sur un PC fixe : égal au défaut, c'est le réglage de Windows ; défaut non lu, rien ne prouve un
    /// écart ; sur un portable, souvent voulu par le constructeur. Le constat le dit au lieu de l'accuser.</summary>
    internal static (RepairStatus Statut, string Phrase) JugerReglageProcesseur(PowerSettingValue v, bool portable)
    {
        var (statut, phrase) = v.EcarteDuDefaut switch
        {
            null => (RepairStatus.Unknown, Docteur.T("RA.Power.NonLu")),
            false => (RepairStatus.ExpectedDefault, Docteur.T("RA.Power.Identique")),
            true => (RepairStatus.NonDefault, Docteur.T("RA.Power.Ecart")),
        };
        if (PowerSettingsReader.Frein(v) != true || v.EcarteDuDefaut == false) return (statut, phrase);
        return (portable || v.EcarteDuDefaut is null ? RepairStatus.NonDefault : RepairStatus.Suspicious,
            Docteur.T("RA.Power.Frein") + (portable ? Docteur.T("RA.Power.FreinPortable") : ""));
    }

    // « Mode de boost : 2 » ne dit rien à personne. Table PERFBOOSTMODE lue sur
    // learn.microsoft.com (options-for-perf-state-engine-perfboostmode) : Microsoft documente 0 à 4, 3 = 1
    // et 4 = 2, et 2 n'est « agressif » qu'en CPPC non autonome ; en autonome il vaut « activé ».
    // Powercfg en énumère 7 sur Windows 11 26200 (5 et 6 « à garanti »), non documentés :
    // ils s'affichent en chiffre nu, sans traduction inventée.
    // « 2 (agressif) » partout serait faux sur un processeur autonome.
    internal static string Lisible(string nom, uint valeur, string unite) => nom switch
    {
        "mode de boost" => valeur switch
        {
            0 => Docteur.T("RA.Boost.Desactive"),
            1 or 3 => Docteur.T("RA.Boost.Active", valeur),
            2 or 4 => Docteur.T("RA.Boost.Agressif", valeur),
            _ => valeur.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        "preference energie/perf" => Docteur.T("RA.Epp", valeur),
        "politique de refroidissement" => Docteur.T(valeur == 0 ? "RA.Refroidissement.Passif" : "RA.Refroidissement.Actif"),
        // 0 MHz n'est pas une fréquence : c'est « aucun plafond ».
        "frequence maximale" or "frequence maximale (coeurs efficaces)" when valeur == 0 => Docteur.T("RA.Freq.SansPlafond"),
        // « 100% » sur la page Processeur ; en français l'unité se sépare du nombre.
        _ => string.IsNullOrEmpty(unite) ? valeur.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{valeur} {unite}",
    };

    // Le mode d'alimentation sur secteur (Windows 11), sans accolades ; null = absent ou non lu.
    private static string? OverlaySecteur()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            return (cle?.GetValue("ActiveOverlayAcPowerScheme") as string)?.Trim('{', '}').ToLowerInvariant();
        }
        catch (Exception) { return null; }
    }

    private IEnumerable<RepairFinding> FilsDeRenduCallOfDuty()
    {
        // Le dossier du fichier le plus récent parmi les emplacements connus : la version courante écrit sous
        // %LOCALAPPDATA%, Documents ne garde qu'une ancienne version (voir ReglagesCod.Trouver).
        if (DrDxt.Detection.Game.ReglagesCod.Trouver()?.DirectoryName is not { } dossier) return [];
        var lu = DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Lire(dossier);
        if (lu is null) return [];
        // Les cœurs de la classe la plus rapide, lus comme le banc processeur ; l'estimation par les comptes WMI en secours.
        var rapides = CoeursRapidesLus();
        if (rapides > 0) return DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Juger(lu, rapides);
        var cpu = _wmi.Query("SELECT Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var ligne = cpu.IsOk ? cpu.Value!.FirstOrDefault() : null;
        var performants = ligne is null ? 0
            : DrDxt.Detection.Jeu.ConfigurationCallOfDuty.CoeursPerformants(ligne.GetString("Manufacturer") ?? "",
                ligne.GetInt("NumberOfCores") ?? 0, ligne.GetInt("NumberOfLogicalProcessors") ?? 0);
        return DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Juger(lu, performants);
    }

    private static int CoeursRapidesLus()
    {
        try { return DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(new DrDxt.Detection.Cpu.CpuTopology().Lire()).Count; }
        catch (Exception) { return 0; }
    }

    // Les cartes vues par Windows, avec la mémoire qu'il leur prête (AdapterRAM plafonne à 4 Go : assez pour séparer une
    // puce intégrée d'une carte dédiée).
    private IReadOnlyList<GpuInfo> CartesLues()
    {
        var r = _wmi.Query("SELECT Name, AdapterRAM FROM Win32_VideoController");
        return r.IsOk
            ? r.Value!.Where(l => l.GetString("Name") is not null)
                .Select(l => new GpuInfo(l.GetString("Name")!, "", Convert.ToInt64(l.Properties.GetValueOrDefault("AdapterRAM") ?? 0L,
                    CultureInfo.InvariantCulture), "", TriState.Unknown)).ToList()
            : [];
    }

    // Les quatre constats AMD détaillés, quand le module de lecture AMD est installé. Les lecteurs du module (canaux UMC,
    // table PM) restent à brancher le jour où il l'est : d'ici là, leurs entrées sont null et chaque constat dit « non lu ».
    private IEnumerable<RepairFinding> LecturesAmdDetaillees(GenerationAmd.Ligne generation)
    {
        var barrettes = _wmi.QueryInNamespace(@"\\.\root\cimv2", "SELECT SMBIOSMemoryType, Speed FROM Win32_PhysicalMemory");
        var types = barrettes.IsOk ? barrettes.Value!.Select(b => b.GetInt("SMBIOSMemoryType") ?? 0).ToList() : [];
        var vitesseKit = barrettes.IsOk ? barrettes.Value!.Select(b => b.GetInt("Speed") ?? 0).DefaultIfEmpty(0).Min() : 0;
        // Le réglé contre l'appliqué : le BIOS nommé au dernier scan, contre les timings du contrôleur.
        var bios = DrDxt.Detection.Cpu.ChaleurEnPartie.LireBios(DossierDonnees.Chemin(ChaleurEnPartie.FichierBios),
            DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur)?.Reglages;
        return [.. TimingsMemoireAmd.Juger(generation, types, canaux: null),
            .. RegleContreApplique.Juger(generation, bios, appliques: null, vitesseKit),
            .. TablePmAmd.Juger(generation, bridage: null),
            .. TablesBiosAmd.Juger(generation, WmiFixe(RequeteCarte, "Product"),
                WmiFixe(RequeteBios, "SMBIOSBIOSVersion"))];
    }

    // Les pilotes installés (Win32_PnPSignedDriver) ; null = non lu. Une date ou une signature illisible reste null.
    private IReadOnlyList<InventaireDesPilotes.Pilote>? PilotesLus()
    {
        var r = _wmi.Query("SELECT DeviceClass, DeviceName, DriverProviderName, DriverDate, IsSigned, InfName FROM Win32_PnPSignedDriver");
        if (!r.IsOk) return null;
        return r.Value!.Select(l => new InventaireDesPilotes.Pilote(l.GetString("DeviceClass") ?? "?", l.GetString("DeviceName") ?? "?",
            l.GetString("DriverProviderName"), InventaireDesPilotes.DateDmtf(l.GetString("DriverDate")),
            l.Properties.GetValueOrDefault("IsSigned") is bool b ? b : null, l.GetString("InfName"))).ToList();
    }

    // Chaque carte d'affichage avec ses identifiants matériels et compatibles ; null = non lu.
    private IReadOnlyList<InstallationDePilote.Carte>? CartesEtIdentifiants()
    {
        var r = _wmi.Query("SELECT Name, PNPDeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
        if (!r.IsOk) return null;
        static IEnumerable<string> Ids(object? v) => v is Array a ? a.Cast<object?>().Select(x => x?.ToString()).OfType<string>() : [];
        return r.Value!.Select(l => new InstallationDePilote.Carte(l.GetString("Name") ?? "?",
            [.. Ids(l.Properties.GetValueOrDefault("HardwareID")), .. Ids(l.Properties.GetValueOrDefault("CompatibleID"))],
            l.GetString("PNPDeviceID"))).ToList();
    }

    private bool? Batterie() => Batterie(_wmi);

    // Une batterie de portable (Win32_Battery) ; null = non lu. Un onduleur branché en USB se déclare aussi comme une
    // batterie : sur un boîtier de bureau (SMBIOS), ce n'est pas un portable.
    internal static bool? Batterie(IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT DeviceID FROM Win32_Battery");
        if (!r.IsOk) return null;
        if (r.Value!.Count == 0) return false;
        return new ChassisDetector(wmi).Detect() is not { IsOk: true, Value: ChassisType.Desktop };
    }

    // ── CPU : la frequence reelle, pas celle que WMI raconte ───────────────────────────────
    private static IEnumerable<RepairFinding> Cpu()
    {
        var lecteur = new CpuFrequencyReader();
        var cpu = lecteur.Lire();

        // Les cœurs chargés, pas l'instance `_Total` : elle fait la moyenne de tous les processeurs logiques,
        // cœurs inactifs compris. Mesuré le 09/09/2026 pendant une partie : `_Total` a 3 622 MHz quand
        // les huit a dix cœurs qui travaillaient tournaient a 3 861. La moyenne repond a une question que personne ne pose.
        //
        // Et au repos, aucune des deux ne predit ce que la machine rend en charge : sur ce
        // processeur un cœur seul tient 5 292 MHz et huit cœurs sous charge continue tombent a
        // 3 698. La phrase le dit plutot que de laisser croire qu'un scan mesure un jeu.
        var charges = lecteur.LireParProcesseur()
            .Where(c => c.UtilisationPourcent >= 60 && c.PourcentageDeNominal is not null)
            .ToList();
        var baseMhz = cpu.FrequenceNominaleMhz ?? 0;

        if (charges.Count > 0 && baseMhz > 0)
        {
            var moyenne = charges.Average(c => c.PourcentageDeNominal!.Value) * baseMhz / 100.0;
            yield return new RepairFinding("CPU", "frequence effective",
                Docteur.T(charges.Count == 1 ? "RA.Freq.Chargee" : "RA.Freq.Chargees", moyenne.ToString("0"), charges.Count), null,
                RepairStatus.Observed, Docteur.T("RA.Freq.CoeursQuiTravaillent"));
        }
        else if (cpu.FrequenceEffectiveMhz is { } mhz)
        {
            yield return new RepairFinding("CPU", "frequence effective",
                mhz.ToString("0") + " MHz"
                + (baseMhz > 0 ? Docteur.T("RA.Freq.Base", baseMhz.ToString("0"), (cpu.PourcentageDeNominal ?? 0).ToString("0")) : ""),
                null, RepairStatus.Observed,
                // Cette phrase citait « 2100 MHz » et « un i7-14700F » en dur, lus sur
                // la machine de référence : faux sur toute autre machine.
                Docteur.T("RA.Freq.AucunCharge")
                + (baseMhz > 0 ? Docteur.T("RA.Freq.Annoncee", baseMhz.ToString("0")) : ""));
        }
        else
        {
            yield return new RepairFinding("CPU", "frequence effective", null, null,
                RepairStatus.Unknown, Docteur.T("RA.Freq.NonLue"));
        }

        // Le bridage porte aussi la question thermique : on ne sait pas combien il fait (voir
        // Thermique), mais on sait si un indicateur Windows est leve. Un seul constat pour les
        // deux lectures : c'est le meme masque.
        //
        // Le cas « aucun » est Observed et jamais ExpectedDefault : `Performance Limit Flags` ne
        // voit pas les limites de puissance du paquet (RAPL). Mesure sur cette machine : les
        // indicateurs valent 0 pendant que la frequence tombe de 5292 a 3698 MHz sous charge
        // soutenue. On observe une absence d'indicateur, on ne certifie pas une absence de limite.
        yield return cpu.EstBride switch
        {
            null => new RepairFinding("CPU", "bridage", null, null, RepairStatus.Unknown, Docteur.T("RA.Bridage.NonLu")),
            false => new RepairFinding("CPU", "bridage", Docteur.T("RA.Bridage.Aucun"), null, RepairStatus.Observed,
                Docteur.T("RA.Bridage.AucunExplication")),
            true => new RepairFinding("CPU", "bridage", Docteur.T("RA.Bridage.Masque", ((long)(cpu.IndicateursDeLimite ?? 0)).ToString("X")),
                null, RepairStatus.Observed, Docteur.T("RA.Bridage.Leve")),
        };
    }

    private IEnumerable<RepairFinding> Services()
    {
        var echecs = _services.Depuis(DateTimeOffset.Now - FenetreEchecs);
        if (!echecs.IsOk)
        {
            yield return new RepairFinding("SERVICES", "echecs", null, null, RepairStatus.Unknown,
                echecs.Error?.Message ?? Docteur.T("RA.Services.Illisible"));
            yield break;
        }

        if (echecs.Value!.Count == 0)
        {
            yield return new RepairFinding("SERVICES", "echecs", Docteur.T("RA.Services.Aucun"), Docteur.T("RA.Services.Aucun"),
                RepairStatus.ExpectedDefault, Docteur.T("RA.Services.AucunEchec", FenetreEchecs.Days));
            yield break;
        }

        foreach (var f in echecs.Value.Take(6))
            yield return new RepairFinding("SERVICES", f.Service,
                $"{f.Occurrences}x {f.Nature}"
                + (f.EstUneRafale ? Docteur.T("RA.Services.Rafale", f.Etendue.TotalMinutes) : "")
                + (f.JoursDistincts > 1 ? Docteur.T("RA.Services.Journees", f.JoursDistincts) : ""),
                Docteur.T("RA.Services.AucunEchecAttendu"),
                // Ce qui distingue une maladie d'un accident est la repartition, pas le compte :
                // `atvi-skuld` a echoue 21 fois... toutes dans la meme minute.
                f.EstRecurrent ? RepairStatus.Suspicious : RepairStatus.Observed,
                Docteur.T("RA.Services.Evenement", f.EvenementId)
                + (f.Occurrences > 1
                    ? Docteur.T("RA.Services.DuAu", f.PlusAncien.ToLocalTime(), f.PlusRecent.ToLocalTime())
                    : Docteur.T("RA.Services.Le", f.PlusRecent.ToLocalTime()))
                + Docteur.T(f.EstUneRafale ? "RA.Services.UneRafale" : "RA.Services.UnFait")
                + (f.Tronque ? Docteur.T("RA.Services.Tronque") : "")
                // « à corriger » sans geste.
                + (f.EstRecurrent ? Docteur.T("RA.Services.Geste") : ""),
                f.EstRecurrent ? RepairAction.Recommandation : RepairAction.Aucune);
    }

    private static IEnumerable<RepairFinding> Mmcss()
    {
        foreach (var m in new MmcssReader().Lire())
        {
            var (statut, action) = m.Effet switch
            {
                MmcssEffet.NonLu => (RepairStatus.Unknown, RepairAction.Aucune),
                // Un placebo n'est pas un degat : il ne casse rien, il ment.
                MmcssEffet.SansEffet => (RepairStatus.Observed, RepairAction.Aucune),
                MmcssEffet.Desactive => (RepairStatus.Suspicious, RepairAction.Recommandation),
                // `Observed` et non `ExpectedDefault` : la page Microsoft ne publie aucun defaut
                // pour ces reglages. Savoir ce qu'une valeur fait ne dit rien de son origine.
                _ => (RepairStatus.Observed, RepairAction.Aucune),
            };

            yield return new RepairFinding("MMCSS", m.Nom, m.Valeur, null, statut,
                Action: action,
                Explication: m.Phrase + "  [source : " + MmcssReader.Source + "]");
        }
    }

    /// <summary>Au-dela de cette part d'endurance consommee, l'usure merite le regard.
    ///
    /// **seuil emprunte, pas mesure, et il manque une source.** Quatre-vingts pour cent est la
    /// convention que les constructeurs emploient pour dire « prevois le remplacement » ; aucune
    /// mesure de ce depot ne l'etaye. Il est ecrit ici plutot que noye dans une condition, et il
    /// devra etre remplace par une source ou par une mesure.</summary>
    private const int SeuilUsurePercent = 80;

    private IEnumerable<RepairFinding> MisesAJour()
    {
        var lus = _misesAJour.Depuis(DateTimeOffset.UtcNow - FenetreMisesAJour);
        return lus.IsOk
            ? MisesAJourEnEchec.Juger(lus.Value!, (int)FenetreMisesAJour.TotalDays, PaquetsInstalles.DeposeLe)
            : [new RepairFinding("WINDOWS UPDATE", "echecs", null, null, RepairStatus.Unknown,
                Docteur.T("RA.MAJ.NonLu", lus.Error?.Message))];
    }

    /// <summary>Les peripheriques que le Gestionnaire marque en erreur. Le tri par code
    /// vit dans <see cref="PeripheriquesEnErreur"/>.</summary>
    private IEnumerable<RepairFinding> Peripheriques()
    {
        var lu = _wmi.Query("SELECT Name, PNPDeviceID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        if (!lu.IsOk)
        {
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Periph.NonLu", lu.Error!.Value.Message));
            yield break;
        }

        var constats = lu.Value!
            // Un appareil absent (Present = false) n'est pas malade : Windows en garde seulement la trace.
            .Where(r => !string.Equals(r.GetString("Present"), "False", StringComparison.OrdinalIgnoreCase))
            .Select(r => PeripheriquesEnErreur.Juger(r.GetString("Name") ?? Docteur.T("RA.Periph.SansNom"),
                r.GetInt("ConfigManagerErrorCode") ?? 0, r.GetString("PNPDeviceID")))
            .OfType<RepairFinding>()
            .ToList();

        if (constats.Count == 0)
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", Docteur.T("RA.Periph.Aucune"), Docteur.T("RA.Periph.Aucune"),
                RepairStatus.ExpectedDefault, Docteur.T("RA.Periph.AucunEnErreur"));
        foreach (var c in constats) yield return c;
    }

    // Constat seul, voir SuspensionUsb. Le plan actif sur secteur, puis les périphériques d'entrée
    // USB branchés (pilote HidUsb) dont le constructeur a activé la suspension. Rien n'est écrit.
    private IEnumerable<RepairFinding> SuspensionSelectiveUsb()
    {
        var plan = new PowerProvider().GetActiveSchemeGuid();
        if (plan.IsOk && Guid.TryParse(plan.Value, out var g)) yield return SuspensionUsb.JugerPlan(PowerSettingsReader.LireSuspensionUsb(g));

        var lu = _wmi.Query("SELECT PNPDeviceID, Present FROM Win32_PnPEntity WHERE Service = 'HidUsb'");
        if (!lu.IsOk) { yield return SuspensionUsb.JugerEntrees(null, 0); yield break; }
        var branches = lu.Value!
            .Where(r => !string.Equals(r.GetString("Present"), "False", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetString("PNPDeviceID"))
            .OfType<string>()
            .ToList();
        yield return SuspensionUsb.JugerEntrees(branches.Count, branches.Count(id => SuspensionUsb.Activee(ValeurDeParametre(id))));
    }

    private static object? ValeurDeParametre(string pnpDeviceId)
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters");
            return cle?.GetValue("SelectiveSuspendEnabled");
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// L'usure des disques. Un SSD qui a consomme son endurance ralentit puis lache : c'est une
    /// piece qui vieillit de facon mesurable, la seule du PC dans ce cas.
    ///
    /// La forme de la requete a ete verifiee sur la machine avant d'etre ecrite, et la forme
    /// evidente est fausse : interroger `MSFT_StorageReliabilityCounter` directement rend la classe
    /// presente et aucune instance. Voir <see cref="StorageWearReader"/>.
    /// </summary>
    private IEnumerable<RepairFinding> UsureDisques()
    {
        var lu = new StorageWearReader(_wmi).Lire();
        if (!lu.IsOk)
        {
            yield return new RepairFinding("STOCKAGE", "usure", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Usure.NonLu", lu.Error!.Value.Message));
            yield break;
        }

        foreach (var d in lu.Value!)
        {
            // Les erreurs non corrigées, avant l'usure.
            if (StorageWearReader.JugerErreurs(d) is { } erreurs) yield return erreurs;
            if (StorageWearReader.JugerSante(d) is { } sante) yield return sante;
            if (d.UsurePercent is not { } usure)
            {
                yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}", null, null,
                    RepairStatus.Unknown,
                    _eleve()
                        ? Docteur.T("RA.Usure.PasDeCompteur")
                        : SansDroits);
                continue;
            }

            var merite = usure >= SeuilUsurePercent;
            yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}",
                ValeurDUsure(usure, d.TemperatureC), Docteur.T("RA.Usure.ZeroPourcent"),
                StatutDUsure(usure),
                merite
                    ? Docteur.T("RA.Usure.Merite", usure, SeuilUsurePercent)
                    : Docteur.T("RA.Usure.Lecture", usure),
                merite ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    /// <summary>Le seuil d'usure est une convention du constructeur : il dit quand regarder, il ne prouve aucun tort.</summary>
    public static RepairStatus StatutDUsure(int usure) => usure >= SeuilUsurePercent ? RepairStatus.NonDefault : RepairStatus.Observed;

    // La température est nommée à côté de l'usure : « 0 %, 40 °C » sous le mot « usure » laisserait le second chiffre sans nom.
    internal static string ValeurDUsure(int usure, int? temperatureC) =>
        // Une clé USB publiait « 0 °C ». Zéro n'est pas une température de
        // disque en marche : c'est une valeur non publiée, elle n'est pas dite.
        temperatureC is > 0 and { } t ? Docteur.T("RA.Usure.AvecTemperature", usure, t) : Docteur.T("RA.Usure.Seule", usure);

    private IEnumerable<RepairFinding> Bcd() => JugerBcd(LireBcd(), _eleve());

    // Sans droits administrateur, bcdedit refuse ; la famille se taisait, et l'écran de
    // récupération coupé disparaissait. Sortie non lue (null) : « non lu », avec sa cause.
    internal static IEnumerable<RepairFinding> JugerBcd(string? sortie, bool eleve)
    {
        if (sortie is null)
            return [new RepairFinding("BCD", "configuration de démarrage", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Bcd.NonLue") + (eleve ? "" : " " + SansDroits))];
        string[] interessantes =
        [
            "bootstatuspolicy", "hypervisorlaunchtype", "disabledynamictick",
            "useplatformclock", "useplatformtick", "tscsyncpolicy", "increaseuserva",
        ];
        return sortie.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(m => m.Length >= 2 && interessantes.Contains(m[0].ToLowerInvariant()))
            .Select(m => BcdInterpretation.Juger(m[0].ToLowerInvariant(), string.Join(' ', m[1..])))
            .ToList();
    }

    /// <summary>Les entrees BCD que le produit sait interpreter, plus celles qui portent une
    /// signature connue d'optimiseur. On ne liste pas tout : un audit qui rend quarante lignes
    /// de BCD sans savoir les juger fabrique de l'inquietude, pas de l'information.</summary>
    private static string? LireBcd()
    {
        try
        {
            // System32, pas le nom nu (ordre de recherche de CreateProcess, voir Win32ServiceActions).
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), "/enum {current}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            var r = DrDxt.Core.ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(5));
            // Un refus (accès refusé) sort avec un code non nul : ce n'est pas « aucune entrée ».
            return r.Fini && r.Code == 0 ? r.Sortie : null;
        }
        catch { return null; }
    }

    // ── Affichage ──────────────────────────────────────────────────────────────────────────
    // Une resolution non native n'est pas une erreur. Beaucoup de joueurs competitifs la choisissent
    // deliberement, pour gagner des FPS ou pour l'etirement de l'image. Le dire autrement serait
    // accuser un choix.
    /// <summary>Pur : la fréquence de l'écran de jeu contre son maximum. Une fréquence non lue (0 : Win32 rend la valeur par
    /// défaut du matériel) se dit non lue : comparée au maximum, elle donnait « tourne à 0 Hz » avec une recommandation.</summary>
    internal static RepairFinding FrequenceDeLEcran(DrDxt.Application.Tweaks.Hardware.DisplayInfo e)
        => e.RefreshHz <= 0
            ? new RepairFinding("DISPLAY", "frequence", null, e.MaxRefreshHz > 0 ? $"{e.MaxRefreshHz} Hz" : null, RepairStatus.Unknown,
                Docteur.T("RA.Ecran.FrequenceNonLue"))
            : e.MaxRefreshHz == 0
                ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", null, RepairStatus.Observed,
                    Docteur.T("RA.Ecran.MaxNonLu"))
                : e.RefreshHz >= e.MaxRefreshHz
                    ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                        RepairStatus.ExpectedDefault, Docteur.T("RA.Ecran.AuMax"))
                    : new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                        RepairStatus.NonDefault,
                        Docteur.T("RA.Ecran.Bride", e.MaxRefreshHz, e.RefreshHz),
                        RepairAction.Recommandation);

    private IEnumerable<RepairFinding> Display()
    {
        var ecran = new Win32DisplayInfo().GetPrimaryGameDisplay();
        if (!ecran.IsOk || ecran.Value is null)
        {
            yield return new RepairFinding("DISPLAY", "ecran principal", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Ecran.NonLu"));
            yield break;
        }

        var e = ecran.Value;
        yield return FrequenceDeLEcran(e);

        // Ce que l'écran annonce dans son EDID, contre ce que Windows propose.
        // Comparé à la résolution native : c'est elle que le câble transporte, même quand le joueur joue en étiré (1728×1080
        // relevé le 06/10 sur l'écran principal de la machine de référence, que l'EDID ne détaille pas).
        if (e.DeviceName is { } nom
            && EcranBride.Comparer(EdidDuMoniteur.Lire(e.NomInterface), (l, h) => Win32DisplayInfo.MaxRefreshAt(nom, l, h),
                Sortie(EdidDuMoniteur.Instance(e.NomInterface)), Win32DisplayInfo.Duplique(nom)) is { } bride)
            yield return bride;
    }

    // Le branchement du moniteur (HDMI, DisplayPort…), lu dans WmiMonitorConnectionParams ; null = non lu.
    private uint? Sortie(string? instance)
    {
        if (instance is null) return null;
        var r = _wmi.QueryInNamespace(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
        if (!r.IsOk) return null;
        var ligne = r.Value!.FirstOrDefault(l => l.GetString("InstanceName")?.StartsWith(instance, StringComparison.OrdinalIgnoreCase) == true);
        return ligne?.Properties.TryGetValue("VideoOutputTechnology", out var v) == true && v is not null
            ? Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    // ── Pilote GPU ─────────────────────────────────────────────────────────────────────────
    private static IEnumerable<RepairFinding> GpuDriver()
    {
        var ull = Dword(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode");
        yield return ull is null
            ? new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode", Docteur.T("RA.Nv.Absent"), Docteur.T("RA.Nv.Absent"),
                RepairStatus.ExpectedDefault, Docteur.T("RA.Nv.AbsentExplication"))
            : new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode",
                ull switch { 0 => "Off", 1 => "On", 2 => "Ultra", _ => ull.ToString() }, Docteur.T("RA.Nv.Absent"),
                RepairStatus.NonDefault, Docteur.T("RA.Nv.PoseeExplication"),
                // Meme motif que POWER : sans geste, ce constat n'atteindrait jamais l'ecran.
                RepairAction.Recommandation);
    }

    // ── PAGEFILE ───────────────────────────────────────────────────────────────────────────
    // Un pagefile manuel n'est pas automatiquement mauvais : c'est un choix courant et defendable.
    // Ce qui compte est la coherence entre ce qui est configure et ce dont la machine a besoin.
    private IEnumerable<RepairFinding> Pagefile()
    {
        var auto = Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem", "AutomaticManagedPagefile");
        var alloue = Wmi("SELECT AllocatedBaseSize FROM Win32_PageFileUsage", "AllocatedBaseSize");
        var pic = Wmi("SELECT PeakUsage FROM Win32_PageFileUsage", "PeakUsage");
        yield return new RepairFinding("PAGEFILE", "gestion",
            auto is null ? null : (auto == "True" ? Docteur.T("RA.Pf.Auto") : Docteur.T("RA.Pf.Manuelle")),
            Docteur.T("RA.Pf.Auto"),
            auto == "True" ? RepairStatus.ExpectedDefault
                : auto is null ? RepairStatus.Unknown
                : RepairStatus.UserChoiceProbable,
            auto == "True"
                ? Docteur.T("RA.Pf.AutoExplication")
                : Docteur.T("RA.Pf.ManuelleExplication"));
        if (alloue is not null)
            yield return new RepairFinding("PAGEFILE", "taille / pic",
                Docteur.T("RA.Pf.Taille", alloue, pic ?? "?"), null, RepairStatus.Observed,
                Docteur.T("RA.Pf.TailleExplication"));
        yield return FichierDEchangeSurDisqueDur.Juger(EmplacementsDuFichierDEchange(out var ssdPresent), ssdPresent);
        // La mémoire pleine est le défaut de fluidité d'un PC de bureau ; lue à l'instant, dite avec ses gestes.
        yield return MemoireSaturee.Juger(Hardware.PressionMemoire.Lire(), _lecturesDures?.Invoke());
    }

    // Chaque fichier d'échange en service et le type du disque qui le porte (même relevé que les jeux).
    private static IReadOnlyList<(string Chemin, int TypeDeDisque)>? EmplacementsDuFichierDEchange(out bool? ssdPresent)
    {
        ssdPresent = null;
        try
        {
            var types = DossiersDeJeux.TypesParLettre();
            ssdPresent = SsdInterne();
            return LectureWmiBornee.Lire(() =>
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
                var fichiers = new List<(string, int)>();
                using var rangees = s.Get();
                foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                    using (o)
                        if (o["Name"]?.ToString() is { Length: > 1 } chemin)
                            fichiers.Add((chemin, chemin[1] == ':' && types.TryGetValue(char.ToUpperInvariant(chemin[0]), out var t) ? t : 0));
                return fichiers;
            }, () => throw new TimeoutException("WMI did not answer in time."));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Un SSD interne parmi tous les disques (avec ou sans lettre) ; un SSD en USB (BusType 7)
    /// ne compte pas. Aucun SSD mais un disque au type non dit : <c>null</c>, rien n'est affirmé.</summary>
    private static bool? SsdInterne() => LectureWmiBornee.Lire<bool?>(() =>
    {
        var disques = new List<(int Type, int Bus)>();
        using (var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk"))
        using (var rangees = s.Get())
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    disques.Add((Convert.ToInt32(o["MediaType"] ?? 0), Convert.ToInt32(o["BusType"] ?? 0)));
        return SsdInterneParmi(disques);
    }, () => throw new TimeoutException("WMI did not answer in time."));

    internal static bool? SsdInterneParmi(IReadOnlyList<(int Type, int Bus)> disques)
    {
        const int Usb = 7;
        if (disques.Any(d => d.Type == JeuxSurDisqueDur.Ssd && d.Bus != Usb)) return true;
        return disques.Any(d => d.Type is not (JeuxSurDisqueDur.Ssd or JeuxSurDisqueDur.DisqueDur)) ? null : false;
    }

    // ── BIOS : identite et interface constructeur, en lecture pure ─────────────────────────
    // La matrice de qualification (`BiosQualificationMatrix`) reste dans l'outil : elle vit dans
    // `DrDxt.Bios`, que ce projet ne reference pas, et l'ajouter ici pour une lecture creerait la
    // dependance a l'envers.
    private IEnumerable<RepairFinding> BiosIdentite()
    {
        var carte = WmiFixe(RequeteCarte, "Product");
        var fabricant = WmiFixe(RequeteCarte, "Manufacturer");
        var versionBios = WmiFixe(RequeteBios, "SMBIOSBIOSVersion");
        yield return new RepairFinding("BIOS", "carte mere / firmware",
            (fabricant ?? "?") + " " + (carte ?? "?") + ", BIOS " + (versionBios ?? "?"), null,
            RepairStatus.Observed,
            Docteur.T("RA.Bios.Identite"));

        foreach (var iface in new VendorBiosInterfaceProbe().Chercher())
            yield return new RepairFinding("BIOS", "interface constructeur",
                // « lecture et écriture » seul laissait croire que DrDXT écrit le BIOS : c'est ce que
                // le constructeur déclare, pas ce que DrDXT fait (il ne l'appelle jamais pour écrire).
                iface.Classe + Docteur.T(iface.SaitEcrire ? "RA.Bios.InterfaceEcrit" : "RA.Bios.InterfaceLecture"), null,
                RepairStatus.Observed, VendorBiosInterfaceProbe.Constat(iface));
    }

    // ── Thermique ──────────────────────────────────────────────────────────────────────────
    //
    // La temperature du processeur ne se lit pas par WMI, et le chiffre qui semble la donner est
    // un piege. `MSAcpi_ThermalZoneTemperature` repond sur cette machine : `TZ00`, 27,9 °C. Un outil
    // d'opti l'affiche comme « CPU 28 °C » et personne ne bronche.
    //
    // Mesuré le 08/09/2026 (falsification) : charge sur 20 fils pendant 60 s, temoin de charge non
    // localise (`Win32_PerfFormattedData_PerfOS_Processor`).
    //
    //   repos       zone 27,9 °C    CPU  4 %
    //   charge 15 s zone 27,9 °C    CPU 66 %
    //   charge 30 s zone 27,9 °C    CPU 86 %
    //   charge 45 s zone 27,9 °C    CPU 84 %
    //   apres       zone 27,9 °C    CPU  1 %
    //
    // **Constante a la decimale pendant que le processeur passe de 4 a 86 %.** Cette zone ne suit pas
    // le processeur. La lire et l'appeler « temperature CPU » serait un chiffre fabrique.
    //
    // Le compteur « Thermal Zone Information » (Win32_PerfFormattedData_Counters_ThermalZoneInformation)
    // n'est pas une autre source : il rend la même zone `\_TZ.TZ00`, 301 K, constante pendant que la charge passait de
    // 10 à 90 % sur cette machine. Une zone ACPI est une région que le fabricant de la carte mère découpe et dont il
    // choisit la sonde (Microsoft Learn, « ACPI-defined devices », zones thermiques et `_TMP`), pas le cœur. La
    // température des cœurs se lit dans HWiNFO quand il publie, ou dans les registres MSR par PawnIO (ThermiqueGpu).
    //
    // Sans ces deux sources, on ne sait pas combien il fait, mais on sait si un indicateur
    // de limite est leve, et ce constat vit dans la famille CPU, qui lit deja le masque.
    private readonly Func<DrDxt.Detection.Cpu.TemperatureHwinfo.Lecture?> _temperatureHwinfo;

    private IEnumerable<RepairFinding> ThermiqueGpu()
    {
        // HWiNFO publie le capteur du paquet quand il tourne avec sa mémoire partagée. Lu là,
        // c'est une vraie température, nommée avec sa source ; sans lui, la phrase d'avant reste vraie.
        if (_temperatureHwinfo() is { } hw)
        {
            var c = hw.Celsius.ToString("0", CultureInfo.InvariantCulture) + " °C";
            // Le seuil vient d'Intel (freinage à 100 °C). Un Ryzen 7000 (« CPU (Tctl/Tdie) »)
            // tourne à 95 °C par conception : pour lui, aucun jugement, la valeur seule.
            // La lecture par les
            // registres est Intel seulement, elle se juge comme le paquet ; et elle ne vient pas de HWiNFO.
            var parRegistres = hw.Libelle == DrDxt.Detection.Cpu.TemperaturePawnIo.Libelle;
            var chaudIntel = (parRegistres || hw.Libelle is "CPU Package" or "Core Max") && hw.Celsius >= SeuilChaleurProcesseurC;
            var aLaLimite = chaudIntel && hw.Celsius >= 100;
            yield return new RepairFinding("THERMIQUE", "temperature du processeur", c, null,
                chaudIntel ? RepairStatus.Suspicious : RepairStatus.Observed,
                (parRegistres ? Docteur.T("RA.Temp.Registres") : Docteur.T("RA.Temp.Hwinfo", hw.Libelle))
                + Docteur.T("RA.Temp.AuScan", c)
                + Docteur.T(aLaLimite ? "RA.Temp.Limite" : chaudIntel ? "RA.Temp.PresLimite" : "RA.Temp.EnPartie"));
            foreach (var r in ThermiqueCarte()) yield return r;
            yield break;
        }

        var zone = WmiDans(@"\\.\root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
            "CurrentTemperature");
        yield return new RepairFinding("THERMIQUE", "temperature du processeur", null, null,
            RepairStatus.Unknown,
            // Sans droits, ni la zone ACPI ni HWiNFO n'ont répondu ; la suite (pourquoi une zone ACPI ne suffirait pas)
            // reste vraie dans les deux cas.
            (!_eleve() ? Docteur.T("RA.Temp.SansDroits")
            : zone is null
                ? Docteur.T("RA.Temp.AucuneZone")
                : Docteur.T("RA.Temp.Zone",
                  // Lu et ecrit en invariant. En culture courante, une machine francaise ecrirait
                  // « 29,9 °C » dans le rapport que le client colle dans son ticket, qu'un test promet
                  // identique partout ; et une valeur WMI a point decimal ne se lirait pas en francais,
                  // la chaine brute s'afficherait a la place de la temperature.
                  double.TryParse(zone, NumberStyles.Float, CultureInfo.InvariantCulture, out var dk)
                      ? (dk / 10 - 273.15).ToString("0.0", CultureInfo.InvariantCulture) + " °C"
                      : zone))
            + Docteur.T("RA.Temp.ZoneFabriquee")
            + PourquoiHwinfoNePubliePas());

        foreach (var r in ThermiqueCarte()) yield return r;
    }

    /// <summary>HWiNFO tourne mais ne publie rien. Chez moi, c'était une version de 2012 ; la
    /// case « Shared Memory Support » non cochée, ou décochée au bout de 12 heures dans la version gratuite (forum
    /// HWiNFO, depuis la v7.00 ; aucune source officielle), en sont les autres causes. Dites comme causes possibles,
    /// jamais détectées. HWiNFO absent, ou ce service sans lecteur (tests) : rien.</summary>
    private string PourquoiHwinfoNePubliePas()
    {
        string? version;
        bool publie;
        try { version = _hwinfoQuiTourne?.Invoke(); publie = _hwinfoPublie?.Invoke() ?? false; }
        catch (Exception) { return ""; }
        if (version is null) return "";
        // Une version illisible ne s'écrit pas « ? ».
        var nom = version == "?" ? "HWiNFO" : $"HWiNFO {version}";
        return publie
            ? Docteur.T("RA.Hwinfo.PublieSansTemp", nom)
            : Docteur.T("RA.Hwinfo.NePubliePas", nom);
    }

    /// <summary>Au-dessus, la carte dit « près de la limite ». Intel freine ses processeurs récents
    /// à 100 °C (TjMax) ; 95 °C laisse la marge d'un relevé ponctuel. **Non chiffré** au-delà de cette source.</summary>
    internal const double SeuilChaleurProcesseurC = 95;

    private IEnumerable<RepairFinding> ThermiqueCarte()
    {

        var gpu = new NvmlHardwareProbe().Lire();
        if (gpu.GpuTemperatureC is null && TemperatureAdlx(CartesLues(), _amd.Value) is { } amd)
        {
            yield return amd;
            yield break;
        }
        if (gpu.GpuTemperatureC is null && TemperatureIntel(CartesLues(), _intel.Value) is { } intel)
        {
            yield return intel;
            yield break;
        }
        yield return gpu.GpuTemperatureC is { } tGpu
            ? new RepairFinding("THERMIQUE", "temperature du GPU", tGpu.ToString("0") + " °C", null,
                RepairStatus.Observed,
                // « publie sa température (55,2 W) » collait des watts a une phrase sur la
                // temperature (relu dans le rapport reel). La puissance est dite pour ce qu'elle est.
                Docteur.T("RA.Gpu.Nvml")
                + (gpu.GpuPowerW is { } w ? Docteur.T("RA.Gpu.Consommait", w) : "")
                + ".")
            : new RepairFinding("THERMIQUE", "temperature du GPU", null, null, RepairStatus.Unknown,
                TemperatureCarteNonLue(CartesLues(), _amd.Value, _intel.Value.Raison));
    }

    // La carte Intel vue par Windows (la plus grosse mémoire : une Arc passe devant la puce intégrée), quand ni NVIDIA ni
    // AMD n'est là : celles-là ont leur propre bibliothèque, lue d'abord.
    private static GpuInfo? IntelSeule(IReadOnlyList<GpuInfo> cartes)
        => cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                           || c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) ? null
            : cartes.Where(c => c.Model.Contains("Intel", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);

    /// <summary>La température d'une carte Intel lue par IGCL (ctlTemperatureGetState, sonde GPU) ; null si
    /// pas de carte Intel seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureIntel(IReadOnlyList<GpuInfo> cartes, (double? Celsius, string? Raison) intel)
        => IntelSeule(cartes) is not null && intel.Celsius is > 0 and < 150 and { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, Docteur.T("RA.Gpu.Igcl"))
            : null;

    // La carte AMD dédiée vue par Windows (la plus grosse mémoire : une APU liste sa puce intégrée d'abord), quand
    // aucune carte NVIDIA n'est là.
    private static GpuInfo? RadeonSansNvidia(IReadOnlyList<GpuInfo> cartes)
        => cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ? null
            : cartes.Where(c => c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);

    /// <summary>La température d'une carte AMD lue par ADLX (IADLXGPUMetrics.GPUTemperature) ; null si pas de
    /// carte AMD seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureAdlx(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
        => RadeonSansNvidia(cartes) is not null && amd.TemperatureC is { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, Docteur.T("RA.Gpu.Adlx"))
            : null;

    /// <summary>L'état d'Anti-Lag lu par ADLX sur une carte AMD. Rien quand il n'est pas lu : la ligne de la
    /// limite d'images dit déjà ce que DrDXT n'a pas pu lire.</summary>
    internal static IEnumerable<RepairFinding> AntiLagAmd(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (RadeonSansNvidia(cartes) is not { } carte || amd.AntiLag is not { } actif) yield break;
        yield return new RepairFinding("LATENCE", "Anti-Lag de la carte", Docteur.T(actif ? "RA.AntiLag.Active" : "RA.AntiLag.Desactive"), null,
            RepairStatus.Observed, Docteur.T("RA.AntiLag.Explication", carte.Model));
    }

    // La limite d'images (VRR, cap) se règle par le pilote NVIDIA, et se lit par ADLX sur une Radeon. Sur une carte AMD
    // ou Intel sans carte NVIDIA, le silence passerait pour « rien à régler » : sans lecture, c'est un « non lu », dit.
    internal static IEnumerable<RepairFinding> LimiteImagesHorsNvidia(IReadOnlyList<GpuInfo> cartes,
        DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) yield break;
        // Une Intel Arc reçoit le même « non lu ».
        if (cartes.Where(c => (c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                               || c.Model.Contains("Arc", StringComparison.Ordinal)) && c.VramBytes >= 2L << 30)
                .MaxBy(c => c.VramBytes) is not { } carte)
            yield break;
        var radeon = carte.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        var vrr = Docteur.T("RA.Vrr", Docteur.T(radeon ? "RA.Logiciel.Amd" : "RA.Logiciel.Intel"));
        // Sur une Radeon, la limite FRTC / Chill lue par ADLX remplace le « non lu ».
        if (radeon && amd.LimiteImages is { } fps)
        {
            yield return new RepairFinding("LATENCE", "limite d'images de la carte", fps == 0 ? Docteur.T("RA.Fps.Aucune") : Docteur.T("RA.Fps.Valeur", fps), null,
                RepairStatus.Observed,
                Docteur.T("RA.Fps.LueAdlx", carte.Model)
                + Docteur.T(fps == 0 ? "RA.Fps.AucuneLimite" : amd.ChillActif == true && amd.ChillMaxFps == fps ? "RA.Fps.Chill" : "RA.Fps.Frtc")
                + vrr);
            yield break;
        }
        yield return new RepairFinding("LATENCE", "limite d'images de la carte", null, null, RepairStatus.Unknown,
            (radeon
                ? Docteur.T("RA.Fps.NonLueAmd", carte.Model)
                  + (amd.Raison is { } r ? Docteur.T("RA.Raison", r.TrimEnd('.')) : "") + ". "
                : Docteur.T("RA.Fps.PasNvidia", carte.Model))
            + vrr);
    }

    internal static string TemperatureCarteNonLue(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd,
        string? intel = null)
    {
        // APU + Radeon dédiée, Windows rend la puce intégrée d'abord : la plus grosse mémoire.
        var autre = cartes.Where(c => !c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);
        return cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) || autre is null
            ? Docteur.T("RA.TempGpu.Nvml")
            // Une Radeon passe par ADLX ; la raison de l'échec est dite.
            : autre.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                ? Docteur.T("RA.TempGpu.NonLueAmd", autre.Model)
                  + (amd.Raison is { } r ? Docteur.T("RA.Raison", r.TrimEnd('.')) : "") + ". "
                  + Docteur.T("RA.TempGpu.LogicielAmd")
            // Une carte Intel passe par IGCL ; la raison de l'échec est dite.
            : autre.Model.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                ? Docteur.T("RA.TempGpu.NonLueIntel", autre.Model)
                  + (intel is { } i ? Docteur.T("RA.Raison", i.TrimEnd('.')) : "") + ". "
                  + Docteur.T("RA.TempGpu.LogicielIntel")
            : Docteur.T("RA.TempGpu.Autre", autre.Model);
    }

    // ── LECTEURS ───────────────────────────────────────────────────────────────────────────

    private string? Wmi(string requete, string propriete) => WmiDans(@"\\.\root\cimv2", requete, propriete);

    // Le BIOS et la carte mère ne changent pas pendant un scan : une requête chacun, gardée le temps du scan, sert toutes
    // les familles qui les lisent (jusqu'à trois sur un Ryzen). Refaite à chaque Lire.
    private const string RequeteBios = "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS";
    private const string RequeteCarte = "SELECT Manufacturer, Product FROM Win32_BaseBoard";
    private System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<WmiRow>?>> _fixesDuScan = new();

    private string? WmiFixe(string requete, string propriete) => _fixesDuScan.GetOrAdd(requete, r => new Lazy<IReadOnlyList<WmiRow>?>(() =>
    {
        var lignes = _wmi.QueryInNamespace(@"\\.\root\cimv2", r);
        return lignes.IsOk ? lignes.Value : null;
    })).Value?.FirstOrDefault()?.GetString(propriete);

    /// <summary>La premiere ligne d'une requete WMI, ou <c>null</c> quand rien ne repond, jamais
    /// une valeur fabriquee. `root\WMI` porte les zones thermiques ACPI, `root\cimv2` le reste.</summary>
    private string? WmiDans(string espaceDeNoms, string requete, string propriete)
    {
        var lignes = _wmi.QueryInNamespace(espaceDeNoms, requete);
        return lignes.IsOk ? lignes.Value!.FirstOrDefault()?.GetString(propriete) : null;
    }

    private static int? Dword(string chemin, string nom)
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(chemin);
            return cle?.GetValue(nom) is int v ? v : null;
        }
        catch { return null; }
    }
}
