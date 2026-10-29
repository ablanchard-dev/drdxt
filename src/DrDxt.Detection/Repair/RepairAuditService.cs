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

    /// <summary>Depuis quand on regarde les arrets brutaux. **Non chiffré**, et plus large que la fenetre des services :
    /// un arret brutal est rare, et trente jours n'en montreraient aucun sur une machine qui en fait un par mois.</summary>
    public static readonly TimeSpan FenetreArrets = TimeSpan.FromDays(90);

    /// <summary>Au-dela de ce nombre d'arrets brutaux dans la fenetre, le constat demande le regard de l'utilisateur.
    /// **Emprunte, pas derive** : c'est le seuil des incidents noyau (« repete a l'identique, ce n'est plus du
    /// bruit ») ; aucune mesure ne dit que trois est le bon nombre pour des arrets brutaux.</summary>
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
        // Leur pendant sur Radeon : Boost, Image Sharpening, RSR et AFMF, lus par ADLX et nommés, jamais posés.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.ReglagesRadeonDuJoueur.Juger(CartesLues(), _amd.Value)));
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
        // (SMU), BIOS AMD nommé, cause matérielle des redémarrages. PawnIO absent ou génération non validée en vie : une seule
        // ligne le dit ; porte ouverte, chaque constat a sa ligne.
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
        IReadOnlyList<InventaireDesPilotes.Pilote>? pilotes = null;
        constats.AddRange(Isoler("PERIPHERIQUES", () => InventaireDesPilotes.Juger(pilotes = PilotesLus(), DateTime.Now)));
        // Le pilote chipset AMD, du même inventaire, sur un processeur AMD seulement.
        constats.AddRange(Isoler("PERIPHERIQUES", () =>
            InventaireDesPilotes.JugerChipsetAmd(pilotes, DrDxt.Detection.Cpu.Cpuid.Lire().Fabricant, DateTime.Now)));
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

    private IEnumerable<RepairFinding> MisesAJour()
    {
        var lus = _misesAJour.Depuis(DateTimeOffset.UtcNow - FenetreMisesAJour);
        return lus.IsOk
            ? MisesAJourEnEchec.Juger(lus.Value!, (int)FenetreMisesAJour.TotalDays, PaquetsInstalles.DeposeLe)
            : [new RepairFinding("WINDOWS UPDATE", "echecs", null, null, RepairStatus.Unknown,
                Docteur.T("RA.MAJ.NonLu", lus.Error?.Message))];
    }

    // ── Thermique ──────────────────────────────────────────────────────────────────────────
    //
    // La température du processeur ne se lit pas par WMI : `MSAcpi_ThermalZoneTemperature` (zone `TZ00`) est restée
    // à 27,9 °C à la décimale pendant 60 s de charge sur 20 fils, processeur de 4 à 86 % (mesuré le 08/09/2026), et le
    // compteur « Thermal Zone Information » rend la même zone. Une zone ACPI est une région dont le fabricant de la carte
    // choisit la sonde, pas le cœur (Microsoft Learn, « ACPI-defined devices », `_TMP`). La température des cœurs vient
    // de HWiNFO ou des MSR par PawnIO (ThermiqueGpu) ; sans eux, seul l'indicateur de limite est lu (famille CPU).
    private readonly Func<DrDxt.Detection.Cpu.TemperatureHwinfo.Lecture?> _temperatureHwinfo;

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
