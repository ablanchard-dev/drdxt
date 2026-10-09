using static DrDxt.Core.TexteFrancais;
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
/// Les constats de reparation qui ne demandent qu'une lecture — enfin dans le produit.
///
/// Ce qui a motive ce fichier, mesure le 09/09/2026. Dix types construits en deux jours —
/// `MmcssReader`, `WindowsServiceFailureLog`, `BcdInterpretation`, `CrashDumpPolicy`,
/// `WerLiveKernelReportLog`, `RepairTriage`, `RepairHistory`, `BiosQualificationMatrix`,
/// `TailStatistic`, `RepairFinding` — et **zero sur dix** etait reference par `DrDxt.App` ou
/// `DrDxt.Application`. Tout vivait dans `tools/repairaudit`.
///
/// C'est le meme motif d'orphelin que ces deux jours ont corrige cinq fois, mais d'un cran
/// au-dessus : je branchais des lecteurs orphelins dans un OUTIL lui-meme orphelin. Un outil
/// prouve qu'un lecteur fonctionne ; il ne met rien entre les mains de quelqu'un.
///
/// ══ pourquoi ces familles et pas les autres ══
/// Celles-ci ne demandent qu'une lecture — registre, journal d'evenements, `bcdedit`, WMI, un
/// compteur de performance. Les autres (banc memoire, parking, pertes reseau) exigent une mesure
/// qui dure : un banc de 12 passes, une fenetre de 3 s. Les melanger imposerait ce cout a tout
/// appelant, y compris a une interface qui veut afficher un etat tout de suite. La frontiere est
/// donc le COUT, pas le domaine — et elle est dite ici plutot que subie.
///
/// Le 09/09 au soir, sept familles de plus ont franchi cette frontiere depuis l'outil : POWER,
/// CPU, display, GPU driver, pagefile, BIOS (identite et interface constructeur seulement — la
/// matrice de qualification vit dans `DrDxt.Bios`, que ce projet ne reference pas) et thermique.
///
/// ══ chaque famille est isolee ══
/// Une lecture WMI echoue par intermittence sur cette machine (la famille RAM de l'outil a deja
/// disparu d'une passe pour revenir a la suivante). Une exception dans une famille ne coute que
/// cette famille, et elle laisse un constat `Unknown` qui le dit : une famille muette est
/// indistinguable d'une famille saine, et c'est pire qu'une famille en erreur.
///
/// Ce service ne juge pas et ne repare rien : il assemble des constats que d'autres types ont
/// deja qualifies. Le tri de ce qui merite l'attention appartient a <see cref="RepairTriage"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class RepairAuditService
{
    private readonly IServiceFailureLog _services;
    private readonly ILiveKernelReportLog _noyau;
    private readonly IUnexpectedShutdownLog _arrets;
    private readonly IMemoryDiagnosticLog _testMemoire;
    private readonly IDumpCreationLog _vidages;
    /// <summary>La source WMI. Injectee comme les autres lecteurs depuis le 19/09/2026.
    ///
    /// Elle etait cablee en dur (`= new()`) : le seul lecteur du service sans couture, alors que
    /// `UsureDisques` et `BiosIdentite` l'interrogent. Consequence mesuree la veille : les tests
    /// d'audit attendaient WMI meme quand ils verifiaient une famille qui n'a rien a voir — cinq
    /// d'entre eux tenaient un plateau de 28,3 s a 0,2 s pres en execution complete, signature
    /// d'une ressource que Windows serialise.
    ///
    /// Le defaut reste `ManagementWmiQuery` : le produit ne change pas de comportement.</summary>
    private readonly IWmiQuery _wmi;
    private readonly IWindowsUpdateLog _misesAJour;
    private readonly ILecteurWindowsUpdate? _windowsUpdate;
    private readonly Func<RepairFinding>? _heuresActives;
    private readonly Func<RepairFinding>? _defender;
    private readonly Func<RepairFinding>? _tachesEnPartie;
    private readonly Func<RepairFinding>? _jeuxSurDisqueDur;
    private readonly Func<RepairFinding>? _connexionDeJeu;
    // J-MECA (09/10/2026) : PL1/PL2 du processeur Intel par ses registres (PawnIO installé), sinon non lu.
    private readonly Func<LimiteDePuissanceLue?> _limitesPuissance;
    private readonly Func<IEnumerable<RepairFinding>>? _plantagesDeJeu;
    private readonly Func<string?>? _hwinfoQuiTourne;
    private readonly Func<bool>? _hwinfoPublie;
    private readonly IBootPerformanceLog _demarrage;
    // 06/10/2026 (chantier B8c) : lu sur un compte sans droits (fixture `sans-admin`), un refus d'accès se disait
    // « ce disque ne publie pas son compteur », « aucune zone ACPI ne répond » ou un message d'exception anglais. Sans
    // droits, la cause dite est le manque de droits.
    private readonly Func<bool> _eleve;
    // A1 (06/10/2026) : SAM, Anti-Lag, limite d'images et température de la carte AMD, lus une fois par audit.
    private readonly Lazy<DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus> _amd;
    private readonly Lazy<(double? Celsius, string? Raison)> _intel;
    // Revue (F-32) : pas de promesse « pour la voir » : relancé en administrateur, DrDXT tente la lecture ; un
    // disque qui ne publie pas son compteur ne le publiera pas plus.
    internal const string SansDroits = "DrDXT tourne sans droits administrateur et cette lecture en demande : "
        + "lance-le en administrateur (clic droit > Exécuter en tant qu'administrateur) pour qu'il la tente.";

    /// <summary>Fenetre des essais de mise a jour. **NON CHIFFRÉ**, alignee sur celle des arrets
    /// brutaux : un echec qui dure se voit sur trois mois.</summary>
    public static readonly TimeSpan FenetreMisesAJour = TimeSpan.FromDays(90);

    // 06/10/2026 (plan 3b.7) : la lecture réelle de Windows Update n'est branchée qu'ici (production) ; les tests,
    // qui passent par le constructeur à doublures, ne lisent jamais le vrai Windows Update.
    public RepairAuditService()
        : this(new WindowsServiceFailureLog(), new WerLiveKernelReportLog(),
               new WindowsUnexpectedShutdownLog(), windowsUpdate: new LecteurWindowsUpdate(),
               hwinfoQuiTourne: DrDxt.Detection.Cpu.TemperatureHwinfo.VersionQuiTourne,
               hwinfoPublie: DrDxt.Detection.Cpu.TemperatureHwinfo.MemoirePubliee,
               amd: DrDxt.Detection.Gpu.Adlx.AdlxReglages.Lire, intel: DrDxt.Detection.Gpu.TemperatureCarte.LireIntel,
               heuresActives: HeuresActivesDuPc, plantagesDeJeu: PlantagesDeJeu.DuPc, defender: DefenderDuPc,
               tachesEnPartie: TachesEnPartieDuPc, jeuxSurDisqueDur: () => JeuxSurDisqueDur.Juger(DossiersDeJeux.Lire()),
               connexionDeJeu: () => ConnexionDeJeu.Juger(DrDxt.Detection.Network.CartesVersInternet.Lire(), DrDxt.Detection.Network.LecteurWifi.Lire())) { }

    /// <summary>07/10/2026 (D4) : les tâches planifiées lancées pendant une partie relevée, en lecture seule.</summary>
    internal static RepairFinding TachesEnPartieDuPc() => TachesEnPartie.Juger(DernieresExecutionsDesTaches.Lire(),
        DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList());

    /// <summary>07/10/2026 (D3) : les analyses de Defender contre les parties relevées, en lecture seule.</summary>
    internal static RepairFinding DefenderDuPc() => AnalysesDefender.Juger(
        JournalDefender.Analyses(DateTimeOffset.UtcNow.AddDays(-JoursRedemarrages)),
        DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        JournalDefender.SeulementInactif(), JoursRedemarrages);

    /// <summary>Fenêtre des redémarrages de Windows Update comptés. **NON CHIFFRÉ** : un mois de parties.</summary>
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
        Func<RepairFinding>? connexionDeJeu = null, Func<LimiteDePuissanceLue?>? limitesPuissance = null)
    {
        _connexionDeJeu = connexionDeJeu;
        _limitesPuissance = limitesPuissance ?? DrDxt.Detection.Cpu.TemperaturePawnIo.LireLimites;
        _jeuxSurDisqueDur = jeuxSurDisqueDur;
        _defender = defender;
        _tachesEnPartie = tachesEnPartie;
        // A5 (07/10/2026) : la température d'une carte Intel par IGCL ; sans lecteur (tests), rien n'est lu sur la vraie carte.
        _intel = new Lazy<(double? Celsius, string? Raison)>(() =>
        {
            try { return intel?.Invoke() ?? (null, "lecteur IGCL non branché"); }
            catch (Exception ex) { return (null, $"lecture IGCL interrompue : {ex.GetType().Name}"); }
        });
        // D2 (07/10/2026) : comme les heures d'activité, le vrai journal n'est lu qu'en production.
        _plantagesDeJeu = plantagesDeJeu;
        // A1 (06/10/2026) : sans lecteur (tests), rien n'est lu sur la vraie carte.
        _amd = new Lazy<DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus>(() =>
        {
            try { return amd?.Invoke() ?? DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu("Lecteur ADLX non branché."); }
            catch (Exception ex) { return DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu($"Lecture ADLX interrompue : {ex.GetType().Name}."); }
        });
        _heuresActives = heuresActives;
        _eleve = eleve ?? (() => new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        _hwinfoPublie = hwinfoPublie;
        _windowsUpdate = windowsUpdate;
        _hwinfoQuiTourne = hwinfoQuiTourne;
        // PAWN1 (09/10/2026) : HWiNFO s'il publie, sinon PawnIO s'il est installé ; chaque lecture nomme sa source.
        _temperatureHwinfo = temperatureHwinfo ?? DrDxt.Detection.Cpu.TemperatureProcesseur.Lire;
        _demarrage = demarrage ?? new WindowsBootPerformanceLog();
        _wmi = wmi ?? new ManagementWmiQuery();
        _misesAJour = misesAJour ?? new WindowsUpdateEventLog();
        _services = services;
        _noyau = noyau;
        _arrets = arrets ?? new WindowsUnexpectedShutdownLog();
        _testMemoire = testMemoire ?? new WindowsMemoryDiagnosticLog();
        _vidages = vidages ?? new WindowsDumpCreationLog();
    }

    /// <summary>Depuis quand on regarde les echecs de services. **NON CHIFFRÉ.** Trente jours
    /// laissent voir une recurrence sans remonter a une configuration qui n'existe plus.</summary>
    public static readonly TimeSpan FenetreEchecs = TimeSpan.FromDays(30);

    /// <summary>Depuis quand on regarde les arrets brutaux. **NON CHIFFRÉ**, et plus large que
    /// la fenetre des services a dessein : un arret brutal est rare, et trente jours pourraient
    /// n'en montrer aucun sur une machine qui en fait un par mois — c'est-a-dire une machine dont
    /// l'utilisateur, lui, sait tres bien qu'elle le fait.</summary>
    public static readonly TimeSpan FenetreArrets = TimeSpan.FromDays(90);

    /// <summary>Au-dela de ce nombre d'arrets brutaux dans la fenetre, le constat cesse d'etre une
    /// observation et demande le regard de l'utilisateur.
    ///
    /// **ce seuil est emprunte, pas derive.** C'est celui que la famille des incidents noyau
    /// utilise deja dans ce fichier pour la meme raison — « repete a l'identique, ce n'est plus du
    /// bruit ». Aucune mesure ne dit que trois est le bon nombre pour des arrets brutaux ; prendre
    /// le seuil deja en vigueur est un choix de coherence, et il est dit ici plutot que subi.</summary>
    private const int SeuilArretsRepetes = 3;

    public IReadOnlyList<RepairFinding> Lire()
    {
        var constats = new List<RepairFinding>();
        constats.AddRange(Isoler("POWER", Power));
        constats.AddRange(Isoler("CPU", Cpu));
        // 04/10/2026 (plan 2.2) : le garde de stabilité des Core de 13e et 14e génération, avant tout conseil de puissance.
        constats.AddRange(Isoler("CPU", () =>
        {
            var (nom, revision, identifiant) = DrDxt.Detection.Cpu.MicrocodeRaptorLake.Lire();
            return DrDxt.Detection.Cpu.MicrocodeRaptorLake.Juger(nom, revision, identifiant);
        }));
        // J-MECA (09/10/2026) : la limite de puissance effective, lue dans les registres quand PawnIO est là ; jamais écrite.
        constats.AddRange(Isoler("CPU", () => [LimiteDePuissance.Juger(_limitesPuissance())]));
        // 07/10/2026 (A7) : Curve Optimizer sur un Ryzen de bureau, en guide ; jamais écrit.
        constats.AddRange(Isoler("CPU", () =>
        {
            var carte = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Hardware\Description\System\Bios")?.GetValue("BaseBoardManufacturer") as string;
            return CurveOptimizerAmd.Juger(DrDxt.Detection.Cpu.MicrocodeRaptorLake.Lire().Nom, carte) is { } c ? [c] : [];
        }));
        constats.AddRange(Isoler("SERVICES", Services));
        // Une seule requete des services pour les deux familles de securite (mesure du 25/09 : 200 a 340 ms
        // chacune) ; lue a la premiere demande, le temps de ce Lire seulement.
        Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>? servicesLus = null;
        Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> ServicesLus() => servicesLus ??= ServicesSysteme();
        constats.AddRange(Isoler("SERVICES", () => ServicesRemplacables(ServicesLus())));
        constats.AddRange(Isoler("SERVICES", () => CheminsNonCites(ServicesLus())));
        constats.AddRange(Isoler("TACHES", () => TachesRemplacables(TachesLues(), CheminProtege.Verifier)));
        constats.AddRange(Isoler("MMCSS", Mmcss));
        constats.AddRange(Isoler("STABILITE", () => [CrashDumpPolicy.Juger(
            Dword(@"SYSTEM\CurrentControlSet\Control\CrashControl", "CrashDumpEnabled"))]));
        constats.AddRange(Isoler("BCD", Bcd));
        constats.AddRange(Isoler("STABILITE", IncidentsNoyau));
        constats.AddRange(Isoler("STABILITE", ArretsBrutaux));
        constats.AddRange(Isoler("STABILITE", TestMemoire));
        // 04/10/2026 (plan 3b.6) : ce qui ralentit le démarrage (mesuré par Windows) et la place libre du disque système.
        var ralentisseurs = DemarrageEtDisque.LireRalentisseurs();
        constats.AddRange(Isoler("DEMARRAGE", () => DemarrageEtDisque.JugerDemarrage(ralentisseurs)));
        // 06/10/2026 (chantier B7) : ce qui se lance avec Windows, lu seulement.
        constats.AddRange(Isoler("DEMARRAGE", () => ProgrammesAuDemarrage.Juger(ProgrammesAuDemarrage.Lire(), ralentisseurs)));
        constats.AddRange(Isoler("STOCKAGE", () =>
        {
            var systeme = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return systeme.IsReady ? DemarrageEtDisque.JugerDisqueSysteme(systeme.Name.TrimEnd('\\'), systeme.AvailableFreeSpace, systeme.TotalSize) : [];
        }));
        constats.AddRange(Isoler("STOCKAGE", () => DemarrageEtDisque.JugerCacheDeShaders(DemarrageEtDisque.LireCacheDeShaders())));
        // 04/10/2026 (plan 3.2) : les erreurs matérielles WHEA, premier signe d'une marge qui manque.
        constats.AddRange(Isoler("STABILITE", () => ErreursMaterielles.Juger(ErreursMaterielles.Lire())));
        constats.AddRange(Isoler("STABILITE", CreationDeVidage));
        if (_plantagesDeJeu is { } pj) constats.AddRange(Isoler("STABILITE", pj));
        constats.AddRange(Isoler("STABILITE", AvantLesIncidents));
        constats.AddRange(Isoler("STABILITE", PointSain));
        constats.AddRange(Isoler("DISPLAY", Display));
        // 06/10/2026 (chantier B5) : la fréquence que chaque souris USB annonce.
        constats.AddRange(Isoler("LATENCE", () => FrequenceSouris.LireEtJuger(_wmi)));
        constats.AddRange(Isoler("LATENCE", () => LimiteImagesHorsNvidia(CartesLues(), _amd.Value)));
        constats.AddRange(Isoler("LATENCE", () => AntiLagAmd(CartesLues(), _amd.Value)));
        constats.AddRange(Isoler("GPU DRIVER", GpuDriver));
        // 06/10/2026 (chantier B3) : le lien PCIe de chaque carte, et un écran branché sur la carte mère.
        constats.AddRange(Isoler("GPU", () => DrDxt.Detection.Gpu.BranchementDeLaCarte.LireLiens(_wmi)
            .SelectMany(l => DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerLien(l, Batterie()))));
        // 07/10/2026 (D6) : le jeu passe-t-il par le Wi-Fi ?
        if (_connexionDeJeu is { } cj)
            constats.AddRange(Isoler("RESEAU", () => [cj()]));
        // 07/10/2026 (D5) : les jeux posés sur un disque dur, et le lien PCIe de chaque SSD NVMe.
        if (_jeuxSurDisqueDur is { } jd)
            constats.AddRange(Isoler("STOCKAGE", () => [jd()]));
        constats.AddRange(Isoler("STOCKAGE", () => DrDxt.Detection.Gpu.BranchementDeLaCarte.LireLiensNvme(_wmi)
            .SelectMany(DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerLienSsd)));
        constats.AddRange(Isoler("GPU", () => DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerEcrans(
            new Win32DisplayInfo().GetAllDisplays() is { IsOk: true } e ? e.Value!.Select(d => d.Adaptateur).OfType<string>().ToList() : [],
            CartesLues(), Batterie())));
        // 04/10/2026 (plan 2.5) : l'intégrité de la mémoire, dite dans les deux sens (coût et protection), jamais écrite.
        constats.AddRange(Isoler("SECURITE", () =>
        {
            var r = _wmi.QueryInNamespace(@"\\.\root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            if (!r.IsOk || r.Value!.FirstOrDefault() is not { } l) return IntegriteMemoire.Juger(null, null);
            var services = l.Properties.TryGetValue("SecurityServicesRunning", out var v) && v is Array a
                ? a.Cast<object>().Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToList() : null;
            return IntegriteMemoire.Juger(l.GetInt("VirtualizationBasedSecurityStatus"), services);
        }));
        // 04/10/2026 (plan 2.1) : un logiciel qui coûte des images, lu dans ses réglages.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.EnregistrementNvidia.Juger(DrDxt.Detection.Gpu.EnregistrementNvidia.LireReglages())));
        // 03/10/2026 (tour G2) : les paquets de pilote d'affichage gardés par Windows et les cartes déconnectées restées déclarées.
        constats.AddRange(Isoler("GPU DRIVER", () => DrDxt.Detection.Gpu.MagasinDePilotes.Juger(
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-drivers /class Display"),
            // Revue R1 : sans « /connected », pnputil liste aussi les cartes absentes, et le pilote d'une carte fantôme
            // passait pour « en service ».
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-devices /class Display /connected"),
            DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil("/enum-devices /class Display /disconnected"))));
        constats.AddRange(Isoler("PAGEFILE", Pagefile));
        constats.AddRange(Isoler("BIOS", BiosIdentite));
        constats.AddRange(Isoler("THERMIQUE", ThermiqueGpu));
        constats.AddRange(Isoler("STOCKAGE", UsureDisques));
        constats.AddRange(Isoler("PERIPHERIQUES", Peripheriques));
        constats.AddRange(Isoler(SuspensionUsb.Domaine, SuspensionSelectiveUsb));
        constats.AddRange(Isoler("WINDOWS UPDATE", MisesAJour));
        // 06/10/2026 (plan 3b.7) : ce qui ATTEND dans Windows Update, pas seulement ce qui a échoué.
        if (_windowsUpdate is { } wu)
            constats.AddRange(Isoler("WINDOWS UPDATE", () =>
            {
                var lu = wu.Lire();
                return [MisesAJourEnAttente.Juger(lu.IsOk ? lu.Value : null, DateTimeOffset.Now, lu.Error?.Message),
                    // B12 : la nouvelle version proposée au clic (« Télécharger et installer »). Lecture entière ratée :
                    // le constat d'au-dessus dit déjà « non lu ».
                    .. lu.IsOk
                        ? MisesAJourEnAttente.JugerProposees(lu.Value!.Proposees,
                            Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                                "DisplayVersion", null) as string)
                        : []];
            }));
        // 06/10/2026 (chantier L-WU) : les heures d'activité contre les heures des parties relevées.
        if (_heuresActives is { } ha)
            constats.AddRange(Isoler("WINDOWS UPDATE", () => [ha()]));
        if (_defender is { } df)
            constats.AddRange(Isoler(AnalysesDefender.Domaine, () => [df()]));
        if (_tachesEnPartie is { } tp)
            constats.AddRange(Isoler("TACHES", () => [tp()]));
        constats.AddRange(Isoler("DEMARRAGE", () =>
        {
            var lus = _demarrage.Lire();
            var bios = _demarrage.LireTempsBios();
            return [
                .. lus.IsOk
                    ? DemarrageMesure.Juger(lus.Value!, DateTimeOffset.UtcNow, FenetreMisesAJour)
                    : [new RepairFinding("DEMARRAGE", "mesure de Windows", null, null, RepairStatus.Unknown,
                        _eleve() ? $"le journal des démarrages n'a pas pu être lu ({lus.Error?.Message})." : SansDroits)],
                .. bios.IsOk
                    ? DemarrageMesure.JugerBios(bios.Value!)
                    : [new RepairFinding("BIOS", "temps du BIOS", null, null, RepairStatus.Unknown,
                        $"le journal Système n'a pas pu être lu ({bios.Error?.Message}).")],
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
                    $"la lecture a échoué ({ex.GetType().Name} : {ex.Message}). Rien n'est affirmé "
                    + "sur cette famille : ce n'est pas un état sain, c'est un trou de lecture."),
            ];
        }
    }

    // ── POWER ──────────────────────────────────────────────────────────────────────────────
    // Le NOM du plan ne suffit pas : deux plans nommes « Equilibre » peuvent porter des reglages
    // differents. On lit le GUID, puis chaque sous-valeur processeur contre le defaut de sa propre
    // personnalite — c'est la que se voient les degats d'un ancien optimiseur, sans connaitre son
    // nom. Le parking de coeurs reste dans l'outil : c'est un echantillonnage de 3 s, pas une
    // lecture.
    private IEnumerable<RepairFinding> Power()
    {
        var plan = new PowerProvider().GetActiveSchemeGuid();
        var planGuid = plan.IsOk ? plan.Value?.Trim('{', '}').ToLowerInvariant() : null;

        yield return ModeAlimentation.JugerPlan(planGuid);
        // 06/10/2026 (chantier B4) : le mode d'alimentation de Windows 11, qui ne joue qu'avec Équilibré ou un plan
        // qui en dérive : jugé par la personnalité du plan (revue F-12).
        var personnaliteDuPlan = Guid.TryParse(planGuid, out var g) ? PowerSettingsReader.Personnalite(g)?.ToString() : null;
        foreach (var m in ModeAlimentation.JugerMode(personnaliteDuPlan, OverlaySecteur(), Batterie())) yield return m;

        if (planGuid is null || !Guid.TryParse(planGuid, out var personnalite)) yield break;

        foreach (var v in new PowerSettingsReader().LireProcesseur(personnalite))
        {
            var (statut, phrase) = v.EcarteDuDefaut switch
            {
                null => (RepairStatus.Unknown,
                    "valeur ou défaut non lus : « non lu » n'est pas « identique »"),
                false => (RepairStatus.ExpectedDefault, "identique au défaut de ce plan"),
                // Un ecart au defaut reste un ECART. Il ne dit pas que la valeur est mauvaise ni
                // que quelqu'un l'a posee par erreur — seulement que ce plan n'est plus celui que
                // Windows installe.
                true => (RepairStatus.NonDefault,
                    "ce plan ne porte plus la valeur que Windows y met : quelqu'un ou quelque chose "
                    + "l'a changée. Ce n'est pas encore un défaut : regarde si c'est voulu."),
            };
            // Un ecart sans geste est un ecart invisible. `RepairTriage.Trier` range dans
            // « verifies » — comptes, jamais affiches — tout ce qui n'est ni suspect ni nuisible ET
            // ne porte aucune action. Un `NonDefault` muet finissait donc dans le meme sac qu'un
            // reglage identique au defaut : le minimum processeur a 100 %, la signature des scripts
            // gaming, etait lu, compte, et jamais montre. La recommandation est la seule facon de
            // le rendre visible sans le declarer nuisible — la doctrine « different n'est pas
            // endommage » interdit `Suspicious`, elle n'a jamais impose le silence.
            // DISPLAY le faisait deja correctement ; POWER ne le faisait pas.
            yield return new RepairFinding("POWER", v.Nom,
                v.ValeurAc is { } a ? Lisible(v.Nom, a, v.Unite) : null,
                v.DefautAc is { } d ? Lisible(v.Nom, d, v.Unite) : null,
                statut, phrase,
                statut == RepairStatus.NonDefault ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    // 24/09/2026 : « Mode de boost : 2 » ne disait rien à personne. Table PERFBOOSTMODE lue sur
    // learn.microsoft.com (options-for-perf-state-engine-perfboostmode) : Microsoft documente 0 à 4, 3 = 1
    // et 4 = 2, et 2 n'est « agressif » qu'en CPPC non autonome ; en autonome il vaut « activé ».
    // 07/10/2026 (L-CPU) : powercfg en énumère 7 sur Windows 11 26200 (5 et 6 « à garanti »), non documentés :
    // ils s'affichent en chiffre nu, sans traduction inventée.
    // Une première table écrite de mémoire disait « 2 (agressif) » partout : faux sur un processeur
    // autonome.
    internal static string Lisible(string nom, uint valeur, string unite) => nom switch
    {
        "mode de boost" => valeur switch
        {
            0 => "0 (désactivé)",
            1 or 3 => $"{valeur} (activé)",
            2 or 4 => $"{valeur} (activé, agressif si Windows choisit la fréquence)",
            _ => valeur.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        "preference energie/perf" => $"{valeur} (0 = performance, 100 = économie)",
        // 29/09/2026 : « 100% » sur la page Processeur ; en français l'unité se sépare du nombre.
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

    // Une batterie présente (Win32_Battery) ; null = non lu.
    private bool? Batterie()
    {
        var r = _wmi.Query("SELECT DeviceID FROM Win32_Battery");
        return r.IsOk ? r.Value!.Count > 0 : null;
    }

    // ── CPU : la frequence reelle, pas celle que WMI raconte ───────────────────────────────
    private static IEnumerable<RepairFinding> Cpu()
    {
        var lecteur = new CpuFrequencyReader();
        var cpu = lecteur.Lire();

        // Ce qui vient d'etre corrige ici. Cette ligne rendait la moyenne de l'instance `_Total`,
        // c'est-a-dire des 28 processeurs logiques, CŒURS INACTIFS COMPRIS. Mesure du 09/09/2026
        // pendant une partie : `_Total` disait 3 622 MHz quand les huit a dix cœurs qui travaillaient
        // vraiment tournaient a 3 861. La moyenne repond a une question que personne ne pose.
        //
        // Et au REPOS, aucune des deux ne predit ce que la machine rend en charge : sur ce
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
                moyenne.ToString("0") + " MHz sur " + charges.Count
                + (charges.Count == 1 ? " processeur logique chargé" : " processeurs logiques chargés"), null,
                RepairStatus.Observed,
                "mesure des cœurs qui travaillent, pas la moyenne de tous. Ce relevé vaut pour "
                + "la charge de cet instant : une charge plus lourde ou plus longue fait tomber la "
                + "fréquence, et ce scan ne le voit pas.");
        }
        else if (cpu.FrequenceEffectiveMhz is { } mhz)
        {
            yield return new RepairFinding("CPU", "frequence effective",
                mhz.ToString("0") + " MHz"
                + (baseMhz > 0 ? " (base " + baseMhz.ToString("0") + " MHz x " + (cpu.PourcentageDeNominal ?? 0).ToString("0") + " %)" : ""),
                null, RepairStatus.Observed,
                // 24/09/2026 : cette phrase citait « 2100 MHz » et « un i7-14700F » en dur, lus sur
                // la machine de référence : faux sur toute autre machine.
                "aucun cœur n'était chargé : c'est la moyenne de tous les processeurs logiques, et "
                + "elle ne dit pas ce que la machine rend sous charge."
                + (baseMhz > 0
                    ? $" La fréquence que Windows annonce ({baseMhz:0} MHz) est la fréquence de base, pas "
                      + "celle que le processeur tient en réalité."
                    : ""));
        }
        else
        {
            yield return new RepairFinding("CPU", "frequence effective", null, null,
                RepairStatus.Unknown, "compteurs non lisibles, et surtout pas 2100 MHz par défaut");
        }

        // Le bridage porte aussi la question thermique : on ne sait pas combien il fait (voir
        // Thermique), mais on sait si un indicateur Windows est leve. Un seul constat pour les
        // deux lectures — c'est le meme masque.
        //
        // Le cas « aucun » est Observed et jamais ExpectedDefault : `Performance Limit Flags` ne
        // voit pas les limites de puissance du paquet (RAPL). Mesure sur cette machine : les
        // indicateurs valent 0 pendant que la frequence tombe de 5292 a 3698 MHz sous charge
        // soutenue. On observe une absence d'indicateur, on ne certifie pas une absence de limite.
        yield return cpu.EstBride switch
        {
            null => new RepairFinding("CPU", "bridage", null, null, RepairStatus.Unknown,
                "indicateurs de limite non lus"),
            false => new RepairFinding("CPU", "bridage", "aucun indicateur", null, RepairStatus.Observed,
                "les indicateurs Windows de limite de performance valent 0. Ce compteur ne voit pas les limites de puissance du paquet (RAPL) : il peut "
                + "valoir 0 pendant que la fréquence tombe sous charge soutenue (relevé : de 5292 à 3698 MHz). "
                + "Aucun indicateur levé n'est donc pas « rien ne bride »."),
            true => new RepairFinding("CPU", "bridage", "masque 0x" + ((long)(cpu.IndicateursDeLimite ?? 0)).ToString("X"),
                null, RepairStatus.Observed,
                "un indicateur de limite est leve. C'est une observation sur une fenêtre de temps, "
                + "jamais une cause d'un écart de FPS, et le masque ne dit pas à lui seul que la "
                + "limite est thermique : ne pas en conclure une surchauffe sans autre preuve."),
        };
    }

    private IEnumerable<RepairFinding> Services()
    {
        var echecs = _services.Depuis(DateTimeOffset.Now - FenetreEchecs);
        if (!echecs.IsOk)
        {
            yield return new RepairFinding("SERVICES", "echecs", null, null, RepairStatus.Unknown,
                echecs.Error?.Message ?? "journal illisible");
            yield break;
        }

        if (echecs.Value!.Count == 0)
        {
            yield return new RepairFinding("SERVICES", "echecs", "aucun", "aucun",
                RepairStatus.ExpectedDefault,
                $"aucun service n'a échoué sur les {FenetreEchecs.Days} derniers jours");
            yield break;
        }

        foreach (var f in echecs.Value.Take(6))
            yield return new RepairFinding("SERVICES", f.Service,
                $"{f.Occurrences}x {f.Nature}"
                + (f.EstUneRafale ? $" en {f.Etendue.TotalMinutes:0} min, le même jour" : "")
                + (f.JoursDistincts > 1 ? $" sur {f.JoursDistincts} journées" : ""),
                "aucun échec",
                // Ce qui distingue une maladie d'un accident est la REPARTITION, pas le compte :
                // `atvi-skuld` a echoue 21 fois... toutes dans la meme minute.
                f.EstRecurrent ? RepairStatus.Suspicious : RepairStatus.Observed,
                $"événement {f.EvenementId} du gestionnaire de services, "
                + (f.Occurrences > 1
                    ? F($"du {f.PlusAncien.ToLocalTime():dd/MM HH:mm} au {f.PlusRecent.ToLocalTime():dd/MM HH:mm}")
                    : F($"le {f.PlusRecent.ToLocalTime():dd/MM/yyyy HH:mm}"))
                + (f.EstUneRafale
                    ? ". Une seule rafale, pas un problème qui dure : tout tient dans la même journée, "
                      + "ce qui ressemble à un programme qui réessaie plutôt qu'à une panne qui revient."
                    : ". C'est un fait, pas une cause : rien ici ne prouve un effet sur le jeu.")
                // Revue adverse du 09/10/2026 (R8) : « à corriger » sans geste.
                + (f.EstRecurrent ? " Le geste : mettre à jour ou réinstaller le logiciel à qui appartient ce service, ou le "
                                    + "désinstaller s'il ne sert plus. DrDXT ne touche pas à ce service." : ""),
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

    /// <summary>
    /// L'usure des disques. Un SSD qui a consomme son endurance ralentit puis lache — c'est une
    /// piece qui vieillit de facon MESURABLE, la seule du PC dans ce cas.
    ///
    /// La forme de la requete a ete verifiee sur la machine avant d'etre ecrite, et la forme
    /// evidente est fausse : interroger `MSFT_StorageReliabilityCounter` directement rend la classe
    /// presente et aucune instance. Voir <see cref="StorageWearReader"/>.
    /// </summary>
    private IEnumerable<RepairFinding> MisesAJour()
    {
        var lus = _misesAJour.Depuis(DateTimeOffset.UtcNow - FenetreMisesAJour);
        return lus.IsOk
            ? MisesAJourEnEchec.Juger(lus.Value!, (int)FenetreMisesAJour.TotalDays, PaquetsInstalles.DeposeLe)
            : [new RepairFinding("WINDOWS UPDATE", "echecs", null, null, RepairStatus.Unknown,
                $"l'historique des mises à jour n'a pas pu être lu ({lus.Error?.Message}). « Non lu » n'est pas « aucun échec ».")];
    }

    /// <summary>25/09/2026 — les peripheriques que le Gestionnaire marque en erreur. Le tri par code
    /// vit dans <see cref="PeripheriquesEnErreur"/>.</summary>
    private IEnumerable<RepairFinding> Peripheriques()
    {
        var lu = _wmi.Query("SELECT Name, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        if (!lu.IsOk)
        {
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", null, null, RepairStatus.Unknown,
                $"la liste des périphériques n'a pas pu être lue ({lu.Error!.Value.Message}).");
            yield break;
        }

        var constats = lu.Value!
            // Un appareil absent (Present = false) n'est pas malade : Windows en garde seulement la trace.
            .Where(r => !string.Equals(r.GetString("Present"), "False", StringComparison.OrdinalIgnoreCase))
            .Select(r => PeripheriquesEnErreur.Juger(r.GetString("Name") ?? "périphérique sans nom",
                r.GetInt("ConfigManagerErrorCode") ?? 0))
            .OfType<RepairFinding>()
            .ToList();

        if (constats.Count == 0)
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", "aucune", "aucune", RepairStatus.ExpectedDefault,
                "aucun périphérique branché n'est en erreur dans le Gestionnaire de périphériques.");
        foreach (var c in constats) yield return c;
    }

    // 07/10/2026 (L-USB) : constat seul, voir SuspensionUsb. Le plan actif sur secteur, puis les périphériques d'entrée
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

    private IEnumerable<RepairFinding> UsureDisques()
    {
        var lu = new StorageWearReader(_wmi).Lire();
        if (!lu.IsOk)
        {
            yield return new RepairFinding("STOCKAGE", "usure", null, null, RepairStatus.Unknown,
                $"les compteurs de fiabilité n'ont pas pu être lus ({lu.Error!.Value.Message}). "
                + "« Non lu » n'est pas « disque neuf ».");
            yield break;
        }

        foreach (var d in lu.Value!)
        {
            // 06/10/2026 (chantier B6) : les erreurs non corrigées, avant l'usure.
            if (StorageWearReader.JugerErreurs(d) is { } erreurs) yield return erreurs;
            if (d.UsurePercent is not { } usure)
            {
                yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}", null, null,
                    RepairStatus.Unknown,
                    _eleve()
                        ? "ce disque ne publie pas son compteur d'usure. Tous ne le font pas, et l'absence "
                          + "de compteur n'est pas une absence d'usure."
                        : SansDroits);
                continue;
            }

            var merite = usure >= SeuilUsurePercent;
            yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}",
                ValeurDUsure(usure, d.TemperatureC), "0 %",
                merite ? RepairStatus.Suspicious : RepairStatus.Observed,
                merite
                    ? $"ce disque a consomme {usure} % de son endurance. Le seuil de "
                      + $"{SeuilUsurePercent} % est une convention constructeur, pas une mesure de "
                      + "ce dépôt : il dit quand regarder, pas quand le disque lâchera."
                    : $"endurance consommée : {usure} %. C'est une lecture, pas un pronostic : un "
                      + "compteur bas ne promet aucune durée de vie.",
                merite ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    // 29/09/2026 : « 0 %, 40 °C » sous le mot « usure » laissait le second chiffre sans nom.
    internal static string ValeurDUsure(int usure, int? temperatureC) =>
        // 04/10/2026 (plan 3.3, relu sur la machine) : une clé USB publiait « 0 °C ». Zéro n'est pas une température de
        // disque en marche : c'est une valeur non publiée, elle n'est pas dite.
        temperatureC is > 0 and { } t ? $"{usure} %, température {t} °C" : $"{usure} %";

    private IEnumerable<RepairFinding> Bcd() => JugerBcd(LireBcd(), _eleve());

    // Revue adverse du 09/10/2026 (R7) : sans droits administrateur, bcdedit refuse ; la famille se taisait, et l'écran de
    // récupération coupé disparaissait. Sortie non lue (null) : « non lu », avec sa cause.
    internal static IEnumerable<RepairFinding> JugerBcd(string? sortie, bool eleve)
    {
        if (sortie is null)
            return [new RepairFinding("BCD", "configuration de démarrage", null, null, RepairStatus.Unknown,
                "La configuration de démarrage n'a pas pu être lue." + (eleve ? "" : " " + SansDroits))];
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
            // 25/09/2026 : System32, pas le nom nu (ordre de recherche de CreateProcess, voir Win32ServiceActions).
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), "/enum {current}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var sortie = proc.StandardOutput.ReadToEnd();
            // Un refus (accès refusé) sort avec un code non nul : ce n'est pas « aucune entrée ».
            return proc.WaitForExit(5000) && proc.ExitCode == 0 ? sortie : null;
        }
        catch { return null; }
    }

    // ── Affichage ──────────────────────────────────────────────────────────────────────────
    // Une resolution non native N'est pas une erreur. Beaucoup de joueurs competitifs la choisissent
    // deliberement — pour gagner des FPS, ou pour l'etirement de l'image. Le dire autrement serait
    // accuser un choix.
    private IEnumerable<RepairFinding> Display()
    {
        var ecran = new Win32DisplayInfo().GetPrimaryGameDisplay();
        if (!ecran.IsOk || ecran.Value is null)
        {
            yield return new RepairFinding("DISPLAY", "ecran principal", null, null, RepairStatus.Unknown,
                "l'écran principal n'a pas pu être lu");
            yield break;
        }

        var e = ecran.Value;
        yield return e.MaxRefreshHz == 0
            ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", null, RepairStatus.Observed,
                "le maximum du panneau n'a pas été lu : « pas lu » n'est pas « déjà au maximum »")
            : e.RefreshHz >= e.MaxRefreshHz
                ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                    RepairStatus.ExpectedDefault, "le panneau tourne à son maximum")
                : new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                    RepairStatus.NonDefault,
                    $"le panneau peut faire {e.MaxRefreshHz} Hz et Windows le pilote a {e.RefreshHz} : "
                    + "arithmétique, pas une estimation",
                    RepairAction.Recommandation);

        // 06/10/2026 (chantier B2) : ce que l'écran annonce dans son EDID, contre ce que Windows propose.
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
            ? new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode", "absent", "absent",
                RepairStatus.ExpectedDefault,
                "la valeur n'existe pas : c'est l'état par défaut d'une installation propre. Des scripts la "
                + "posent pour le mode faible latence, mais aucune source ne montre que le pilote la lit ; le "
                + "réglage du panneau NVIDIA vit dans le profil du pilote, pas ici.")
            : new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode",
                ull switch { 0 => "Off", 1 => "On", 2 => "Ultra", _ => ull.ToString() }, "absent",
                RepairStatus.NonDefault,
                "quelqu'un ou quelque chose a pose cette valeur : elle n'existe pas sur une installation "
                + "neuve. Aucune source ne montre que le pilote la lit : le mode faible latence du panneau "
                + "NVIDIA vit dans le profil du pilote.",
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
            auto is null ? null : (auto == "True" ? "automatique (Windows)" : "manuelle"),
            "automatique (Windows)",
            auto == "True" ? RepairStatus.ExpectedDefault
                : auto is null ? RepairStatus.Unknown
                : RepairStatus.UserChoiceProbable,
            auto == "True"
                ? "Windows dimensionne lui-même : rien à réparer"
                : "un pagefile manuel est un choix courant et défendable : il ne devient un problème que "
                  + "s'il est trop petit pour le commit réel ou pour le vidage sur incident");
        if (alloue is not null)
            yield return new RepairFinding("PAGEFILE", "taille / pic",
                alloue + " Mo alloués, pic " + (pic ?? "?") + " Mo", null, RepairStatus.Observed,
                "le pic d'utilisation dit si la taille est réellement sollicitée");
        yield return FichierDEchangeSurDisqueDur.Juger(EmplacementsDuFichierDEchange(out var ssdPresent), ssdPresent);
        // BUREAU (09/10/2026) : la mémoire pleine est le défaut de fluidité d'un PC de bureau ; lue à l'instant, dite avec ses gestes.
        yield return MemoireSaturee.Juger(Hardware.PressionMemoire.Lire());
    }

    // 08/10/2026 (D5) : chaque fichier d'échange en service et le type du disque qui le porte (même relevé que les jeux).
    private static IReadOnlyList<(string Chemin, int TypeDeDisque)>? EmplacementsDuFichierDEchange(out bool? ssdPresent)
    {
        ssdPresent = null;
        try
        {
            var types = DossiersDeJeux.TypesParLettre();
            ssdPresent = SsdInterne();
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            var fichiers = new List<(string, int)>();
            foreach (var o in s.Get().Cast<System.Management.ManagementObject>())
                using (o)
                    if (o["Name"]?.ToString() is { Length: > 1 } chemin)
                        fichiers.Add((chemin, chemin[1] == ':' && types.TryGetValue(char.ToUpperInvariant(chemin[0]), out var t) ? t : 0));
            return fichiers;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Revue du 08/10/2026 (D5) : un SSD interne parmi TOUS les disques (avec ou sans lettre) ; un SSD en USB (BusType 7)
    /// ne compte pas. Aucun SSD mais un disque au type non dit : <c>null</c>, rien n'est affirmé.</summary>
    private static bool? SsdInterne()
    {
        var disques = new List<(int Type, int Bus)>();
        using (var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk"))
            foreach (var o in s.Get().Cast<System.Management.ManagementObject>())
                using (o)
                    disques.Add((Convert.ToInt32(o["MediaType"] ?? 0), Convert.ToInt32(o["BusType"] ?? 0)));
        return SsdInterneParmi(disques);
    }

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
        var carte = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", "Product");
        var fabricant = Wmi("SELECT Manufacturer FROM Win32_BaseBoard", "Manufacturer");
        var versionBios = Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion");
        yield return new RepairFinding("BIOS", "carte mere / firmware",
            (fabricant ?? "?") + " " + (carte ?? "?") + ", BIOS " + (versionBios ?? "?"), null,
            RepairStatus.Observed,
            "identité exacte : c'est elle qui indexe toute connaissance BIOS réutilisable, et le firmware "
            + "fait partie de l'empreinte matérielle du profil");

        foreach (var iface in new VendorBiosInterfaceProbe().Chercher())
            yield return new RepairFinding("BIOS", "interface constructeur",
                // « lecture et écriture » seul laissait croire que DrDXT écrit le BIOS : c'est ce que
                // le constructeur déclare, pas ce que DrDXT fait (il ne l'appelle jamais pour écrire).
                iface.Classe + (iface.SaitEcrire ? " (le constructeur y déclare l'écriture ; DrDXT ne s'en sert pas)" : " (lecture)"), null,
                RepairStatus.Observed, VendorBiosInterfaceProbe.Constat(iface));
    }

    // ── Thermique ──────────────────────────────────────────────────────────────────────────
    //
    // La temperature du processeur n'est pas lisible ici, et le chiffre qui semble la donner est
    // UN PIEGE. `MSAcpi_ThermalZoneTemperature` repond sur cette machine — `TZ00`, 27,9 °C. Un outil
    // d'opti l'affiche comme « CPU 28 °C » et personne ne bronche.
    //
    // Falsification faite le 08/09/2026 : charge sur 20 fils pendant 60 s, temoin de charge NON
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
    // 07/10/2026 (A5) : le compteur « Thermal Zone Information » (Win32_PerfFormattedData_Counters_ThermalZoneInformation)
    // n'est pas une autre source : il rend la même zone `\_TZ.TZ00`, 301 K, constante pendant que la charge passait de
    // 10 à 90 % sur cette machine. Une zone ACPI est une région que le fabricant de la carte mère découpe et dont il
    // choisit la sonde (Microsoft Learn, « ACPI-defined devices », zones thermiques et `_TMP`), pas le cœur. Lire la
    // température des cœurs demande les registres MSR, donc un pilote noyau : hors de ce produit.
    //
    // Ce qui reste vrai et utile : on ne sait pas combien il fait, mais on sait si un indicateur
    // de limite est leve — et ce constat vit dans la famille CPU, qui lit deja le masque.
    private readonly Func<DrDxt.Detection.Cpu.TemperatureHwinfo.Lecture?> _temperatureHwinfo;

    private IEnumerable<RepairFinding> ThermiqueGpu()
    {
        // 04/10/2026 (plan 1.2) : HWiNFO publie le capteur du paquet quand il tourne avec sa mémoire partagée. Lu là,
        // c'est une vraie température, nommée avec sa source ; sans lui, la phrase d'avant reste vraie.
        if (_temperatureHwinfo() is { } hw)
        {
            var c = hw.Celsius.ToString("0", CultureInfo.InvariantCulture) + " °C";
            // Revue du 04/10 (P1) : le seuil vient d'Intel (freinage à 100 °C). Un Ryzen 7000 (« CPU (Tctl/Tdie) »)
            // tourne à 95 °C par conception : pour lui, aucun jugement, la valeur seule.
            var chaudIntel = hw.Libelle is "CPU Package" or "Core Max" && hw.Celsius >= SeuilChaleurProcesseurC;
            yield return new RepairFinding("THERMIQUE", "temperature du processeur", c, null,
                chaudIntel ? RepairStatus.Suspicious : RepairStatus.Observed,
                $"lue dans HWiNFO (capteur « {hw.Libelle} »), au moment du scan : {c}."
                + (chaudIntel
                    ? $" C'est près de la limite où le processeur se freine lui-même (100 °C sur les Intel récents) : regarde le refroidissement."
                    : " Pour la juger, regarde-la pendant une partie : c'est là qu'elle monte."));
            foreach (var r in ThermiqueCarte()) yield return r;
            yield break;
        }

        var zone = WmiDans(@"\\.\root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
            "CurrentTemperature");
        yield return new RepairFinding("THERMIQUE", "temperature du processeur", null, null,
            RepairStatus.Unknown,
            // F-32 : sans droits, ni la zone ACPI ni HWiNFO n'ont répondu ; la suite (pourquoi une zone ACPI ne suffirait pas)
            // reste vraie dans les deux cas.
            (!_eleve() ? "DrDXT tourne sans droits administrateur : il n'a lu ni la zone thermique ACPI ni HWiNFO "
                         + "(relancé en administrateur, il retentera). "
            : zone is null
                ? "aucune zone thermique ACPI ne répond. "
                : "une zone thermique ACPI répond ("
                  // 19/09/2026 — lu et ecrit en invariant. Les deux employaient la culture
                  // courante : sur une machine francaise la temperature s'ecrivait « 29,9 °C »,
                  // et cette phrase part dans le rapport que le client colle dans son ticket —
                  // celui dont un test promet qu'il « se lit pareil partout ». Cote lecture, le
                  // miroir : une valeur WMI a point decimal ne se lisait pas en francais, et la
                  // chaine brute s'affichait a la place de la temperature.
                  + (double.TryParse(zone, NumberStyles.Float, CultureInfo.InvariantCulture, out var dk)
                      ? (dk / 10 - 273.15).ToString("0.0", CultureInfo.InvariantCulture) + " °C"
                      : zone)
                  // 25/09/2026 : « mesurée constante… de 4 à 86 % » était l'observation de la machine de
                  // mise au point, affichée à tous comme si elle venait de leur machine.
                  + "), mais une telle zone peut ne pas suivre le processeur : relevée constante à la "
                  + "décimale pendant que la charge passait de 4 à 86 %. ")
            + "Une zone ACPI lue comme une température du processeur serait un chiffre fabriqué. Sans pilote noyau (que "
            + "ce produit n'embarquera pas), la température des cœurs reste hors de portée. À la place, le "
            + "domaine Processeur mesure la puissance perdue sous charge longue, que la cause soit la chaleur "
            + "ou la limite de puissance. Si HWiNFO tourne avec sa mémoire partagée activée, DrDXT y lit la vraie température."
            + PourquoiHwinfoNePubliePas());

        foreach (var r in ThermiqueCarte()) yield return r;
    }

    /// <summary>06/10/2026 (plan 4.6) : HWiNFO tourne mais ne publie rien. Chez moi, c'était une version de 2012 ; la
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
        // Revue du 06/10 (P2) : une version illisible ne s'écrit pas « ? ».
        var nom = version == "?" ? "HWiNFO" : $"HWiNFO {version}";
        return publie
            ? $" {nom} publie ses capteurs, mais DrDXT n'y trouve pas la température du processeur : HWiNFO est peut-être "
              + "en pause, ou ton processeur nomme ce capteur autrement."
            : $" {nom} tourne, mais ne publie rien : dans ses réglages, coche « Shared Memory Support » (la version "
              + "gratuite peut la décocher au bout de 12 heures) ; si ta version est ancienne, mets-le à jour (hwinfo.com) : "
              + "les versions récentes publient la température.";
    }

    /// <summary>04/10/2026 (plan 1.2) : au-dessus, la carte dit « près de la limite ». Intel freine ses processeurs récents
    /// à 100 °C (TjMax) ; 95 °C laisse la marge d'un relevé ponctuel. **NON CHIFFRÉ** au-delà de cette source.</summary>
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
                // 25/09/2026 : « publie sa température (55,2 W) » collait des watts a une phrase sur la
                // temperature (relu dans le rapport reel). La puissance est dite pour ce qu'elle est.
                "lue par NVML, publiée par la carte elle-même"
                + (gpu.GpuPowerW is { } w ? F($" ; elle consommait {w:0.0} W au moment de la lecture") : "")
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

    /// <summary>A5 (07/10/2026) : la température d'une carte Intel lue par IGCL (ctlTemperatureGetState, sonde GPU) ; null si
    /// pas de carte Intel seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureIntel(IReadOnlyList<GpuInfo> cartes, (double? Celsius, string? Raison) intel)
        => IntelSeule(cartes) is not null && intel.Celsius is > 0 and < 150 and { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, "lue par IGCL, la bibliothèque de contrôle du pilote Intel, publiée par la carte elle-même.")
            : null;

    // La carte AMD dédiée vue par Windows (la plus grosse mémoire : une APU liste sa puce intégrée d'abord), quand
    // aucune carte NVIDIA n'est là.
    private static GpuInfo? RadeonSansNvidia(IReadOnlyList<GpuInfo> cartes)
        => cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ? null
            : cartes.Where(c => c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);

    /// <summary>A1 : la température d'une carte AMD lue par ADLX (IADLXGPUMetrics.GPUTemperature) ; null si pas de
    /// carte AMD seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureAdlx(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
        => RadeonSansNvidia(cartes) is not null && amd.TemperatureC is { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, "lue par ADLX, l'interface du pilote AMD, publiée par la carte elle-même.")
            : null;

    /// <summary>A1 : l'état d'Anti-Lag lu par ADLX sur une carte AMD. Rien quand il n'est pas lu : la ligne de la
    /// limite d'images dit déjà ce que DrDXT n'a pas pu lire.</summary>
    internal static IEnumerable<RepairFinding> AntiLagAmd(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (RadeonSansNvidia(cartes) is not { } carte || amd.AntiLag is not { } actif) yield break;
        yield return new RepairFinding("LATENCE", "Anti-Lag de la carte", actif ? "activé" : "désactivé", null, RepairStatus.Observed,
            $"Lu par ADLX sur ta carte ({carte.Model}). Anti-Lag réduit l'attente entre ta souris et l'image dans les jeux "
            + "qui ne gèrent pas leur propre réduction de latence ; il se règle dans le logiciel AMD, jeu par jeu ou pour tous.");
    }

    // 06/10/2026 (chantier B6 b) : DrDXT ne lit la température que des cartes NVIDIA (NVML). Sur une carte AMD ou
    // Intel, « NVML n'a rien rendu » ne disait rien au joueur ; on nomme sa carte et où la lire. La lecture AMD (ADLX) et
    // Intel (IGCL) attend une machine pour être prouvée.
    // 06/10/2026 (chantier B5 a) : DrDXT règle et lit la limite d'images (VRR, cap) par le pilote NVIDIA seulement.
    // Sur une carte AMD sans carte NVIDIA, le silence passait pour « rien à régler » : c'est un « non lu », dit. La lecture
    // par ADLX attend une carte AMD pour être prouvée (§9 F-19).
    internal static IEnumerable<RepairFinding> LimiteImagesHorsNvidia(IReadOnlyList<GpuInfo> cartes,
        DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) yield break;
        // Revue T3+T4 (F-24) : une Intel Arc reçoit le même « non lu ».
        if (cartes.Where(c => (c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                               || c.Model.Contains("Arc", StringComparison.Ordinal)) && c.VramBytes >= 2L << 30)
                .MaxBy(c => c.VramBytes) is not { } carte)
            yield break;
        var radeon = carte.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        var logiciel = radeon ? "le logiciel AMD" : "le logiciel Intel";
        const string Vrr = "Avec un écran à fréquence variable, une limite juste sous sa fréquence maximale garde le "
            + "rafraîchissement variable actif";
        // A1 (06/10/2026) : sur une Radeon, la limite FRTC / Chill lue par ADLX remplace le « non lu ».
        if (radeon && amd.LimiteImages is { } fps)
        {
            yield return new RepairFinding("LATENCE", "limite d'images de la carte", fps == 0 ? "aucune" : $"{fps} images/s", null,
                RepairStatus.Observed,
                $"Lue par ADLX sur ta carte ({carte.Model}) : "
                + (fps == 0 ? "ni Frame Rate Target Control ni Chill ne limitent les images. "
                    : amd.ChillActif == true && amd.ChillMaxFps == fps ? "Chill plafonne les images à ce maximum. "
                    : "Frame Rate Target Control plafonne les images à cette valeur. ")
                + $"{Vrr} ; elle se règle dans {logiciel} ou dans le jeu.");
            yield break;
        }
        yield return new RepairFinding("LATENCE", "limite d'images de la carte", null, null, RepairStatus.Unknown,
            (radeon
                ? $"DrDXT n'a pas pu lire la limite d'images de ta carte ({carte.Model}) par le pilote AMD"
                  + (amd.Raison is { } r ? $" ({r.TrimEnd('.')})" : "") + ". "
                : $"DrDXT ne lit pas encore la limite d'images de ta carte ({carte.Model}) : il ne la règle que sur les cartes NVIDIA. ")
            + $"{Vrr} ; elle se règle dans {logiciel} ou dans le jeu.");
    }

    internal static string TemperatureCarteNonLue(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd,
        string? intel = null)
    {
        // Revue T3+T4 (F-23) : APU + Radeon dédiée, Windows rend la puce intégrée d'abord : la plus grosse mémoire.
        var autre = cartes.Where(c => !c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);
        return cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) || autre is null
            ? "NVML n'a pas rendu de température, et surtout pas zéro"
            // A1 (06/10/2026) : une Radeon passe par ADLX ; la raison de l'échec est dite.
            : autre.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                ? $"DrDXT n'a pas pu lire la température de ta carte ({autre.Model}) par le pilote AMD"
                  + (amd.Raison is { } r ? $" ({r.TrimEnd('.')})" : "") + ". "
                  + "Le logiciel AMD ou HWiNFO la montre."
            // A5 (07/10/2026) : une carte Intel passe par IGCL ; la raison de l'échec est dite.
            : autre.Model.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                ? $"DrDXT n'a pas pu lire la température de ta carte ({autre.Model}) par le pilote Intel"
                  + (intel is { } i ? $" ({i.TrimEnd('.')})" : "") + ". "
                  + "Le logiciel Intel Graphics ou HWiNFO la montre."
            : $"DrDXT ne lit pas la température de ta carte ({autre.Model}) : seules les cartes NVIDIA, AMD et Intel la lui "
              + "donnent, par le pilote. Le logiciel de ta carte ou HWiNFO la montre.";
    }

    // ── LECTEURS ───────────────────────────────────────────────────────────────────────────

    private string? Wmi(string requete, string propriete) => WmiDans(@"\\.\root\cimv2", requete, propriete);

    /// <summary>La premiere ligne d'une requete WMI, ou <c>null</c> quand rien ne repond — jamais
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
