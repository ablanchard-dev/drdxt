using DrDxt.App.Platform;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Bios;
using System.IO;
using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.Detection.Wmi;
using DrDxt.GpuNative.NvApi;
using DrDxt.Infrastructure;
using DrDxt.Measurement;
using DrDxt.Profiles;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using DrDxt.Reconciler.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.App;

public static class AppServiceCollectionExtensions
{
    /// <summary>Un fichier illisible (le jeu l'écrit pendant le scan) se dit « non lu » pour ce jeu
    /// seul ; il ne cache plus les autres.</summary>
    internal static RepairFinding ConstatUnreal(string exe, FileInfo fichier, BottleneckState? goulot = null)
    {
        var nom = DrDxt.Detection.Game.ReglagesUnreal.Nom(exe);
        try
        {
            var lu = DrDxt.Detection.Game.ReglagesUnreal.Lire(nom, fichier.FullName, DrDxt.Detection.FichierTiers.Lire(fichier.FullName), fichier.LastWriteTime);
            var constat = DrDxt.Detection.Game.ReglagesUnreal.Constat(lu, DateTime.Now, goulot);
            // Le jeu en DLSS porte son exécutable : le geste « dernier modèle DLSS » vise son profil pilote.
            return lu.Upscaling?.Contains("DLSS", StringComparison.OrdinalIgnoreCase) == true ? constat with { Cible = exe } : constat;
        }
        catch (Exception)
        {
            return new RepairFinding(DrDxt.Detection.Game.ReglagesCod.Domaine, Docteur.Nom("RU.Nom", nom), null, null, RepairStatus.Unknown,
                Docteur.T("App.ReglagesJeuIllisibles", fichier.FullName));
        }
    }

    /// <summary>Le fichier de Call of Duty illisible (le jeu l'écrit, ou il dépasse le plafond) se dit « non lu » ; il
    /// n'emporte plus les constats des jeux Unreal, lus dans le même lot.</summary>
    internal static IReadOnlyList<RepairFinding> ConstatsCod(FileInfo fichier)
    {
        try
        {
            return DrDxt.Detection.Game.ReglagesCod.Constats(
                DrDxt.Detection.Game.ReglagesCod.Lire(fichier.FullName, DrDxt.Detection.FichierTiers.Lire(fichier.FullName), fichier.LastWriteTime),
                DateTime.Now);
        }
        catch (Exception)
        {
            return [new RepairFinding(DrDxt.Detection.Game.ReglagesCod.Domaine, Docteur.Nom("RU.Nom", "Call of Duty"), null, null, RepairStatus.Unknown,
                Docteur.T("App.ReglagesJeuIllisibles", fichier.FullName))];
        }
    }

    private static IReadOnlyList<RepairFinding> ReglagesDesJeuxUnreal()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Le goulot dominant des parties de chaque jeu dit si baisser l'éclairage aidera ; une dernière partie trop courte
            // ne l'emporte pas sur l'historique.
            return DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire()
                .GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Exe: g.Key, Goulot: DrDxt.Application.Parties.ProfilDesJeux.GoulotDominant(g),
                    Fichier: DrDxt.Detection.Game.ReglagesUnreal.Trouver(g.Key, local)))
                .Where(x => x.Fichier is not null)
                .Select(x => ConstatUnreal(x.Exe, x.Fichier!, x.Goulot))
                .ToList();
        }
        catch (Exception) { return []; /* un fichier illisible ne casse pas le scan */ }
    }

    /// <summary>
    /// Assemble tous les modules DI de DrDXT dans la collection de services racine.
    /// À appeler une seule fois au démarrage, avant de construire le provider.
    /// </summary>
    public static IServiceCollection AddDrDxtApp(this IServiceCollection services)
    {
        services.AddDrDxtCore();
        services.AddDrDxtDetection();
        // Le focus observe, pas devine. Sans cette ligne, DrDXT inferait l'alt-tab a partir de
        // la regularite des frametimes, une inference qui se trompe dans les deux sens.
        services.TryAddSingleton<IForegroundProbe, DrDxt.Detection.Win32ForegroundProbe>();
        services.AddDrDxtMeasurement();

        // Le conseiller du docteur BIOS, qui lit et compare (IBiosAdvisor), et la bibliothèque de
        // guides. Le scan consomme ses constats (guidage seul, jamais écrits) pour que le joueur voie
        // son plus gros levier réel (XMP/EXPO coupé), qui vit dans le BIOS et non dans un levier logiciel.
        services.AddDrDxtBios();

        // Profils d'optimisation par puce : enregistre IProfileMatcher (contrat du Core) et les
        // IChipProfileMatcher/IChipProfileCatalog complets. Enregistrement seul ; l'usage d'une
        // recommandation par le scan (composer un TweakPlan, remplacer les cibles GPU à partir des
        // paramètres d'un profil) est la couture laissée à ScanViewModel.
        services.AddDrDxtProfiles();

        // Les vraies coutures Windows, le client élevé en IPC signé et le vrai banc GPU
        // déterministe. Enregistrés avant AddDrDxtApplication() pour que ses stubs TryAdd*, qui
        // échouent proprement, ne l'emportent pas. Les autres coutures SDK de réglage GPU
        // (INvApi*/IAdlx*) restent aux stubs qui échouent proprement ; IDisplayInfo est réel.
        RegisterRealSeams(services);

        // La couche d'application : ISafetyManager, ITweakEngine, chaque ITweak (D/E/F),
        // ITweakCatalog. AddDrDxtApplication() n'ajoute (TryAdd) des stubs qui échouent proprement
        // que pour les coutures pas déjà enregistrées plus haut.
        services.AddDrDxtApplication();

        // « Vérifier DrDXT sur mon PC » : la validation complète, sur les leviers et lecteurs réels.
        services.AddTransient<DrDxt.Application.Autopilot.ValidationDeLaMachine>();

        // Le reconciler comme surface d'état et de contrôle (lecture de la dérive, pause et reprise).
        RegisterReconciler(services);

        // Le consumer, côté scan, d'une recommandation de profil par puce : compose le TweakPlan issu
        // du profil, borne et remplace les cibles GPU (IGpuTuningTargetSource) et affiche une preuve
        // honnête. Dépend de IChipProfileMatcher (AddDrDxtProfiles), d'ITweakCatalog et
        // d'IGpuTuningTargetSource (AddDrDxtApplication), tous enregistrés plus haut.
        services.TryAddSingleton<IScanProfileConsumer, ScanProfileConsumer>();

        // Le filet d'une session tuee : le levier en cours d'essai est journalise avant
        // d'ecrire la machine, et rendu au demarrage suivant (App.OnStartup).
        services.TryAddSingleton<DrDxt.Application.Autopilot.IRevertEnAttente>(
            sp => DrDxt.Application.Autopilot.FichierRevertEnAttente.ParDefaut(sp.GetRequiredService<IRegistryReader>(),
                id => sp.GetRequiredService<ILocalizer>().Get(TweakDisplayKeys.TitleKey(id))));

        // La couture du voyage dans le BIOS. Elle relie trois choses deja enregistrees
        // et jusqu'ici orphelines (le store du marqueur, le verifieur, l'horloge) au canal
        // privilegie. TryAdd pour qu'un hote puisse substituer une doublure.
        services.TryAddSingleton<DrDxt.App.Platform.IFirmwareTrip>(sp =>
            new DrDxt.App.Platform.FirmwareTrip(
                sp.GetRequiredService<IPendingBiosChangeStore>(),
                sp.GetRequiredService<IBiosLoopVerifier>(),
                sp.GetRequiredService<IElevatedClient>(),
                sp.GetRequiredService<IBiosClock>(),
                sp.GetRequiredService<IBiosMachineId>()));

        services.AddTransient<ScanViewModel>(sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            IHardwareDetector detecteur = sp.GetRequiredService<IHardwareDetector>();
            ISettingsStateReader reglages = sp.GetRequiredService<ISettingsStateReader>();
            IBenchmarkRunner mesure = sp.GetRequiredService<IBenchmarkRunner>();
            IStorageDetector disques = sp.GetRequiredService<IStorageDetector>();
            Func<IReadOnlyList<RepairFinding>> sante = () =>
            {
                return [
                    // La santé NVMe d'abord : elle garde le compteur d'arrêts dangereux que la garde de stabilité de l'audit relit.
                    .. DrDxt.Detection.Hardware.SanteNvme.ComparerEtGarder(DrDxt.Core.DossierDonnees.Chemin(DrDxt.Detection.Hardware.SanteNvme.Fichier),
                        new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Since(DateTimeOffset.UtcNow.AddDays(-90)) is { IsOk: true } kp41 ? kp41.Value : null,
                        DateTimeOffset.UtcNow.AddDays(-90)),
                    .. new DrDxt.Detection.Repair.RepairAuditService().Lire(), DrDxt.App.Platform.SanteWindows.Constat(),
                    // Les états de veille du processeur, comparés au relevé de repos gardé. Après l'audit, jamais pendant :
                    // le relevé se prend au repos, et la charge du scan entrerait dans la référence (mesuré sur
                    // 28 fils : 2 à 4 points de repos en moins pendant l'audit, bien plus sur un processeur de 4 cœurs).
                    .. DrDxt.Detection.Cpu.EtatsDeVeille.ComparerEtGarder(
                        DrDxt.Core.DossierDonnees.Chemin(DrDxt.Detection.Cpu.EtatsDeVeille.Fichier)),
                    .. DrDxt.Detection.Repair.InventaireMachine.ComparerEtGarder(
                        DrDxt.Core.DossierDonnees.Chemin("inventaire-machine.json"), DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil),
                    // Les changements venus de l'extérieur (jeu mis à jour, pilote d'affichage changé), notés dans la frise
                    // avant d'être rapprochés des arrêts.
                    .. NoterLeMonde(),
                    // Un arrêt brutal ou une erreur matérielle dans la semaine d'un changement daté.
                    .. StabiliteApresLesChangements()];
            };
            bool? eleve = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            DrDxt.Bios.NvramBiosSettingsReader? nvram = new(DrDxt.App.Platform.VariablesFirmware.Lire);
            Func<ViewModels.CarteDeLaMachine.LienPcie?>? pcie = () =>
                new DrDxt.GpuNative.Nvml.NvmlNative().TryGetPcieLink(0, out var gen, out var larg, out var genMax, out var largMax)
                    ? new ViewModels.CarteDeLaMachine.LienPcie(gen, larg, genMax, largMax) : null;
            Func<IReadOnlyList<RepairFinding>>? reglagesJeu = () =>
            [
                .. DrDxt.Detection.Game.ReglagesCod.Trouver() is { } f
                    ? ConstatsCod(f)
                    // Rien trouvé, élevé, et un lien du compte sur le chemin : non lu, dit.
                    : DrDxt.Detection.Game.ReglagesCod.LienNonSuivi(eleve == true) is { } lien
                        ? [DrDxt.Detection.Game.ReglagesCod.ConstatNonSuivi(lien)]
                        : Array.Empty<RepairFinding>(),
                // Les jeux Unreal Engine joués sur ce PC (relevé des parties), réglages lus.
                .. ReglagesDesJeuxUnreal(),
            ];
            // La version du pilote graphique, demandée au fabricant (seule requête vers un serveur
            // hors des pings : NVIDIA, pour la dernière version publiée).
            Func<IReadOnlyList<GpuInfo>, Task<RepairFinding?>>? verifierPilote = gpus => DrDxt.Detection.Gpu.VersionDuPilote.VerifierAsync(gpus,
                new DrDxt.Detection.Gpu.SourceNvidia(), new DrDxt.Detection.Gpu.SourceAmd(), DrDxt.Detection.Gpu.VersionDuPilote.RadeonSoftwareVersion);
#if DEBUG
            // « machine=<nom> » rejoue une machine enregistrée à la place de ce PC.
            // Les droits viennent du fichier ; PCIe (NVML), NVRAM du BIOS et réglages du jeu, qui
            // liraient ce PC, sont coupés. La version du pilote aussi : la machine porte déjà son constat, relevé
            // le jour de l'export ; redemandé aujourd'hui, il partait sur le réseau et s'affichait en double.
            if (DrDxt.App.Platform.MachineDeTest.Active is { } machine)
            {
                verifierPilote = null;
                var doublure = new DrDxt.App.Platform.MachineDeTest.Detecteur(machine);
                (detecteur, reglages, mesure, disques, sante) = (doublure, doublure, doublure, doublure,
                    () => machine.Sante.Select(DrDxt.App.Platform.MachineDeTest.DansLaLangueDuDocteur).ToList());
                (eleve, nvram, pcie, reglagesJeu) = (machine.Eleve ?? true, null, null, null);
            }
#endif
            var vm = new ScanViewModel(
                detecteur,
                reglages,
                mesure,
                sp.GetRequiredService<IScanScorer>(),
                disques,
                sp.GetRequiredService<ISafetyManager>(),
                sp.GetRequiredService<ITweakEngine>(),
                sp.GetRequiredService<IScanProfileConsumer>(),
                sp.GetRequiredService<IDesiredStateStore>(),
                sp.GetRequiredService<AppliedTweakManifestMapper>(),
                sp.GetRequiredService<IBiosAdvisor>(),
                sp.GetRequiredService<IGuideGenerator>(),
                key => localizer.Get(key),
                // Détection générique du jeu (sans liste par jeu) : le scan gratuit mesure le vrai jeu lancé ;
                // pas de jeu → config seule, honnêtement. Frontière Win32 sans état.
                new DrDxt.App.Platform.Win32GameLocator(),
                // La boucle fermee du BIOS. Sans cette ligne, IBiosLoopVerifier et
                // IPendingBiosChangeStore restaient enregistres et jamais resolus : le produit
                // ne prouvait a personne qu'un changement BIOS avait pris.
                sp.GetRequiredService<DrDxt.App.Platform.IFirmwareTrip>(),
                // De quoi ecrire le rapport de diagnostic que la personne colle
                // dans son ticket.
                WriteSupportReport,
                // Trois nombres, comme les autres exports : quatre ont la forme d'une adresse IP, que l'anonymiseur retire.
                typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3),
                // Le banc memoire, et l'identite de la machine qui rend une reference
                // comparable a elle seule. Sans ces deux lignes le banc existerait sans etre
                // appele, et le scan continuerait d'avouer qu'il ne sait pas lire la topologie.
                new DrDxt.Measurement.Memory.MemoryBaselineService(
                    new DrDxt.Measurement.Memory.RealMemoryBench(),
                    new DrDxt.Measurement.Memory.MemoryBaselineStore(
                        new DrDxt.Measurement.Memory.FileSystemMemoryBaselineFile()),
                    () => DateTimeOffset.UtcNow),
                () => sp.GetRequiredService<IBiosMachineId>().Current,
                // Le journal des mesures. Sans cette ligne, un seul fichier survivrait a une
                // session (la reference memoire) et le produit redecouvrirait la machine a chaque
                // lancement. Une degradation lente est precisement ce qu'un scan isole ne voit pas :
                // prise seule, chaque mesure parait normale. Le scan depose la sienne, le scan
                // suivant peut conclure.
                new DrDxt.Measurement.PerformanceJournalStore(
                    new DrDxt.Measurement.FileSystemPerformanceJournalFile()),
                // L'audit de sante : services Windows, MMCSS, BCD, type de vidage, incidents noyau.
                // Sans cette ligne, ces lecteurs ne servent qu'a `tools/repairaudit` et la section
                // d'audit de l'ecran reste vide.
                // Plus l'etat du magasin de composants (DISM /CheckHealth, par le helper).
                // Et ce qui a été installé, retiré ou mis à jour depuis le scan précédent.
                sante,
                // Ce que le processeur rend a un jeu : mesure une fois (plus de 40 s de pleine charge), relue ensuite. La
                // liste des processeurs vient de la topologie (lue ici, la couche qui reference `DrDxt.Detection`) : un
                // logique par cœur physique de la classe la plus rapide, groupe 0 seulement (au-dela, l'affinite change de sens).
                new DrDxt.Measurement.Cpu.CpuBaselineService(
                    new DrDxt.Measurement.Cpu.RealCpuSustainedBench(() =>
                    {
                        // Type ecrit plutot que `var` : ce que l'App manipule ici s'appelle
                        // `LogicalProcessor`, et le cliquet « construit mais jamais appele » compte
                        // par nom : avec `var`, `LogicalProcessor` passerait pour orphelin alors que
                        // cette ligne le lit. Le nommer dit la verite au comptage.
                        IReadOnlyList<DrDxt.Detection.Cpu.LogicalProcessor> carte =
                            new DrDxt.Detection.Cpu.CpuTopology().Lire();
                        return DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(carte);
                    },
                    // La puissance du paquet pendant la charge soutenue, pour dire si la
                    // baisse vient d'une limite de puissance du BIOS.
                    () => DrDxt.Detection.Cpu.PuissancePaquetReader.LireWatts(new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                    // La fréquence des cœurs du banc, pour voir une limite atteinte dès la première seconde.
                    new DrDxt.Detection.Cpu.CpuFrequencyReader().FrequenceDe),
                    new DrDxt.Measurement.Cpu.CpuBaselineStore(
                        new DrDxt.Measurement.Cpu.FileSystemCpuBaselineFile())),
                // Le pilier maintenir : chaque photo de la machine est comparee a la precedente,
                // pour voir la degradation lente qu'un scan isole ne voit jamais.
                //
                // Les conditions voyagent avec la photo : sans elles, le journal annoncerait
                // « le multi-canal n'est plus prouve » quand une compilation tourne pendant la
                // lecture. La charge se lit sur les cœurs de la classe rapide, et « non lue » reste
                // null plutot que de valoir « machine au repos ».
                constats => new DrDxt.Infrastructure.Repair.RepairJournalService(
                    new DrDxt.Infrastructure.Repair.RepairHistoryStore(),
                    () =>
                    {
                        var coeurs = new DrDxt.Detection.Cpu.CpuFrequencyReader().LireParProcesseur()
                            .Where(c => c.UtilisationPourcent is not null)
                            .Select(c => c.UtilisationPourcent!.Value)
                            .ToList();
                        return coeurs.Count == 0
                            ? null
                            : new DrDxt.Core.ConditionsDeMesure(coeurs.Average(), null);
                    },
                    // L'empreinte du module qui lit. Une compilation deterministe rend le
                    // meme identifiant pour le meme code : il ne change que si le lecteur a change.
                    empreinteLecteur: typeof(DrDxt.Detection.Repair.RepairAuditService).Assembly
                        .ManifestModule.ModuleVersionId.ToString("N")[..12],
                    eleve: new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
                    // L'appli ne se compare qu'à ses propres photos (pas à celles de l'outil).
                    lecteur: "appli").EnregistrerEtComparer(constats),
                // D'ou vient l'instabilite du reseau : latence et chemin mesures. La passerelle vient de la carte physique,
                // jamais de « la premiere trouvee » : un tunnel VPN en publie une aussi, et on mesurerait le tunnel.
                ct => new DrDxt.Detection.Network.AttributionReseauService(
                    new DrDxt.Detection.Network.NetworkAdapterReader(
                        new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                    new DrDxt.Measurement.Network.NetworkLatencyMeter(
                        new DrDxt.Measurement.Network.SystemPingProbe())).LigneAsync(ct),
                // Le cerveau de l'optimiser. Un descripteur devient un candidat mesurable,
                // journalise avant d'ecrire (pending-revert) et relu par AppliedStateVerifier.
                d => sp.GetRequiredService<ITweakCatalog>().Get(d.Id) is { IsOk: true, Value: { } levier }
                    ? new DrDxt.Application.Autopilot.TweakCandidate(levier, d,
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IRegistryReader>(),
                        sp.GetRequiredService<DrDxt.Application.Autopilot.IRevertEnAttente>(),
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.INvApi>(),
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IPowerPlanReader>(),
                        // Un keep inscrit son origine au manifeste signe : la desinstallation la rend.
                        sp.GetRequiredService<IDesiredStateStore>(),
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IServiceStateReader>(),
                        // La carte et le processeur se relisent par ce qui les lit vraiment (NVAPI, ADLX, 0x610).
                        sp.GetRequiredService<DrDxt.Application.Autopilot.ValidationDeLaMachine>().Carte)
                    : null,
                // Ce que la stabilite interdit : les memes lectures que tools/candidatprobe.
                () => (DrDxt.Detection.Hardware.StabiliteLue.InstabiliteGpu(
                           new DrDxt.Detection.Hardware.WerLiveKernelReportLog()),
                       // Un arrêt tombé dans une partie interrompue est dit « sous charge ».
                       DrDxt.Detection.Hardware.StabiliteLue.ArretsBrutaux(
                           new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog(), DateTimeOffset.UtcNow) is { } arrets
                           ? ClasseurDArrets.MarquerSousCharge(arrets, DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Interrompues())
                           : null),
                // Les threads du jeu pendant la mesure du scan (espace utilisateur, sans elevation,
                // aucune injection : compatible anti-triche).
                observerThreads: (pid, fenetre) => new DrDxt.Detection.Cpu.ThreadCpuReader().Observer(pid, fenetre),
                // Les pertes a l'interface pendant la partie. Une seule carte physique
                // connectee, sinon on ne devine pas laquelle porte le jeu.
                mesurerPertesReseau: fenetre =>
                {
                    var cartes = new DrDxt.Detection.Network.NetworkAdapterReader(
                        new DrDxt.Detection.Wmi.ManagementWmiQuery()).Read();
                    if (!cartes.IsOk || cartes.Value is not { Count: 1 } une) return null;
                    return DrDxt.Measurement.Network.PertesInterface.IdDeLaCarte(une[0].Name) is { } id
                        ? DrDxt.Measurement.Network.PertesInterface.Mesurer(
                            new DrDxt.Measurement.Network.SystemNicCounterSource(), id, fenetre)
                        : null;
                },
                // Les DPC/ISR des pilotes pendant la partie (session ETW noyau privee, arretee
                // dans tous les cas ; sans administrateur le constat sort « non lu »).
                mesurerDpc: fenetre => DrDxt.Detection.Latency.LatenceDpc.Classer(
                    new DrDxt.Detection.Latency.DpcLatencyTracer().Observer(fenetre)),
                // Le reservoir des candidats juges, a cote du journal de performance. Sans cette
                // ligne, la provenance d'un keep (p95 seul, 1 % low seul, les deux) vivait le temps
                // d'une session et le corpus qui doit trancher ne commencait jamais.
                journalCandidats: new DrDxt.Application.Autopilot.JournalDesCandidats(
                    DrDxt.Application.Autopilot.JournalDesCandidats.CheminParDefaut(),
                    () => sp.GetRequiredService<IBiosMachineId>().Current),
                // Ce qui tourne a cote du jeu pendant la mesure (OBS, navigateur,
                // compilations). Lecture seule, la meme source que le Gestionnaire des taches.
                voisins: new DrDxt.App.Platform.VoisinsWin32(),
                // Relancer l'appli elevee pour lire ce qui exige l'administrateur.
                // Refus de l'UAC (erreur 1223) = false : l'appli reste ouverte et le dit.
                estEleve: eleve,
                relancerEnAdministrateur: () =>
                {
                    // Par le lanceur natif, environnement reconstruit (RelanceElevee).
                    if (!DrDxt.App.Platform.RelanceElevee.Demarrer()) return false;
                    System.Windows.Application.Current?.Shutdown();
                    return true;
                },
                biosNvram: nvram,
                // Le bufferbloat montant, sur le trafic reel du joueur : rien n'est envoye
                // hors le ping que le scan fait deja vers la meme reference.
                mesurerBufferbloat: fenetre => DrDxt.Measurement.Network.Bufferbloat.Juger(
                    new DrDxt.Measurement.Network.CollecteurEnvoi(
                            new DrDxt.Measurement.Network.SystemPingProbe(),
                            DrDxt.Detection.Network.AttributionReseauService.ReferenceInternetParDefaut)
                        .CollecterAsync(fenetre, TimeSpan.FromMilliseconds(250), CancellationToken.None)
                        .GetAwaiter().GetResult()),
                // Le plafond automatique passe par le helper (UAC a la demande : le joueur vient de
                // l'allumer).
                helper: sp.GetRequiredService<IElevatedClient>(),
                journalLeviers: new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(
                    DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut()),
                registreGains: sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IRegistryReader>(),
                lirePcie: pcie,
                lireReglagesJeu: reglagesJeu,
                // La route vers le serveur de la partie : écoute des envois UDP du jeu (lecture),
                // puis pings vers ce serveur et vers la box, rien d'autre.
                mesurerRoute: (pid, fenetre) =>
                {
                    var cartes = new DrDxt.Detection.Network.NetworkAdapterReader(new DrDxt.Detection.Wmi.ManagementWmiQuery());
                    var compteur = new DrDxt.Measurement.Network.NetworkLatencyMeter(new DrDxt.Measurement.Network.SystemPingProbe());
                    var attribution = new DrDxt.Detection.Network.AttributionReseauService(cartes, compteur);
                    return new DrDxt.Detection.Network.DiagnosticDeRouteService(
                        (p, d) => new DrDxt.Detection.Network.EcouteEnvoisUdp().Ecouter(d, p), compteur, attribution.Passerelle)
                        .MesurerAsync(pid, fenetre);
                },
                verifierPilote: verifierPilote,
                // La mémoire libre, relevée toutes les demi-secondes pendant la fenêtre : le plus bas.
                mesurerMemoireLibre: async fenetre =>
                {
                    ulong? plusBas = null;
                    var fin = DateTime.UtcNow + fenetre;
                    while (DateTime.UtcNow < fin)
                    {
                        if (DrDxt.Measurement.CommitPressureReader.LibrePhysique() is { } libre)
                            plusBas = plusBas is { } b ? Math.Min(b, libre) : libre;
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                    return plusBas;
                },
                // « Exporter ma machine » : le format des fixtures, anonymisé, dans Documents.
                exporterMachine: (hw, st, constats) =>
                {
                    var maintenant = DateTimeOffset.Now;
                    // Un nom de fichier en calendrier grégorien, quel que soit le format régional (ar-SA l'écrirait en 1448).
                    var nom = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"machine-exportee-{maintenant:yyyy-MM-dd-HHmmss}");
                    // Trois nombres : une version à quatre a la forme d'une adresse IP, et l'anonymisation la retirerait.
                    var note = Docteur.T("App.NoteExport", typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3), maintenant);
                    return EcrireMachineExportee(nom + ".json", DrDxt.Detection.Repair.MachineExportee.Json(nom, note, eleve, hw, st,
                        constats, DrDxt.Detection.Repair.MachineExportee.ValeursDeCePc()));
                })
            {
                GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(DrDxt.Core.DossierDonnees.Chemin("bcd-rendu.json")),
                // Chaque magasin cru pour sa seule ruche : celui du profil ne peut pas faire proposer une coupure de la machine.
                LireCoupures = () => [.. DrDxt.ElevatedHelper.Execution.TraceDesCoupures.DeLaSession().Lire(),
                    .. DrDxt.ElevatedHelper.Execution.TraceDesCoupures.DeLaMachine().Lire()],
            };
            // L'essai en alternance ne part que sur une pose que la machine confirme.
            vm.VerifierLaPose = sp.GetRequiredService<DrDxt.Application.Autopilot.ValidationDeLaMachine>().Verdict;
            // « Vérifier DrDXT sur mon PC » : la validation complète par les leviers et le canal élevé du
            // produit, dans le journal de remise du produit (la reprise au démarrage rend un essai interrompu).
            vm.VerifierLaMachine = async progres =>
            {
                var jeux = sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IGameExecutable>();
                bool JeuOuvert() => jeux.RunningGameExecutablePath() is not null;
                if (JeuOuvert()) return new VerificationRendue("Verifier.JeuOuvert", [], null);
                var journal = sp.GetRequiredService<DrDxt.Application.Autopilot.IRevertEnAttente>();
                if (journal.Lire() is not { IsOk: true, Value: null })
                    return new VerificationRendue("Verifier.EssaiEnCours", [], null);
                var r = await sp.GetRequiredService<DrDxt.Application.Autopilot.ValidationDeLaMachine>()
                    .ValiderAsync(journal, progres, JeuOuvert, () => vm.ArretVerificationDemande).ConfigureAwait(false);
                string? chemin = null;
                try { chemin = EcrireRapportDeVerification(r); }
                catch (Exception) { /* le bilan reste à l'écran */ }
                // La liste « ce que DrDXT fait ici » dit « vérifié ici » pour ces leviers.
                try { DrDxt.Application.Tweaks.LeviersVerifies.Enregistrer(DrDxt.Core.DossierDonnees.Chemin("leviers-verifies.json"), r.Empreinte, r.EssayesIds, r.ValidesIds); }
                catch (Exception) { /* la liste ne le dira pas ; le rapport, si */ }
                return new VerificationRendue(null, r.NonRemis, chemin, r.Valides, r.NonTouches, r.Refuses, r.ArretPar) { ValidesIds = r.ValidesIds };
            };
            // L'analyse lancée sans droits se relance en administrateur et enchaîne, une seule fois.
#if DEBUG
            // Jamais depuis un rejeu : la relance perd « machine= » et « hors-ecran », et une seconde App visible
            // analyserait ce PC. Le bouton « relancer en administrateur » reste dessiné, il ne dépend pas de ce délégué.
            if (DrDxt.App.Platform.MachineDeTest.Active is null)
#endif
            vm.RelancerEleveEtAnalyser = scene =>
            {
                // Par le lanceur natif, environnement reconstruit (RelanceElevee).
                if (!DrDxt.App.Platform.RelanceElevee.Demarrer("--analyser", $"--scene={scene}")) return false;
                System.Windows.Application.Current?.Shutdown();
                return true;
            };
            // Les durées réelles des étapes, gardées sur la machine pour que la jauge suive l'analyse.
            var durees = new DrDxt.Core.FichierJsonAtomique(DrDxt.Core.FichierJsonAtomique.CheminLocal("duree-etapes.json"), "DUREE_ETAPES");
            vm.LireDureesDesEtapes = () =>
            {
                var lu = durees.Read();
                if (!lu.IsOk || string.IsNullOrWhiteSpace(lu.Value)) return null;
                var morceaux = lu.Value.Split(';');
                var valeurs = new double[morceaux.Length];
                for (var n = 0; n < morceaux.Length; n++)
                    if (!double.TryParse(morceaux[n], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out valeurs[n])) return null;
                return valeurs;
            };
            vm.EnregistrerDureesDesEtapes = d => durees.WriteAtomic(string.Join(";", d.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
            return vm;
        });

        return services;
    }

    // Les vraies coutures adossées à Windows (lecture seule) et le client élevé en IPC signé.
    // Elles tournent dans l'app non élevée ; les écritures privilégiées passent toujours par le
    // helper élevé via IElevatedClient (opérations typées et sur liste blanche seulement).
    private static void RegisterRealSeams(IServiceCollection services)
    {
        // Frontière privilégiée : le vrai client en IPC signé (échoue proprement sans helper).
        services.AddSingleton<IElevatedClient>(_ => ElevatedClientFactory.Create());

        // Coutures de sécurité.
        services.AddSingleton<IRestorePointService, WmiRestorePointService>();
        services.AddSingleton<IConsentPrompt, MessageBoxConsentPrompt>();

        // Coutures de lecture (latence et réparations relèvent le vrai état d'origine pour le retour arrière).
        services.AddSingleton<IRegistryReader, Win32RegistryReader>();
        // La couture d'affichage n'est plus un stub. Énumération user32 en lecture seule ; sur une
        // machine sans window station elle échoue proprement, et l'appelant passe son tour au lieu de
        // conclure « aucun écran ».
        // NVAPI est passé explicitement : sans lui G-Sync reste illisible et le levier de cadence meurt.
        // Gardée un court instant : un scan relit les écrans à chaque levier qui en dépend (40 à 57 ms la lecture).
        services.AddSingleton<IDisplayInfo>(sp => new DrDxt.Detection.Display.EcransEnCache(
            new DrDxt.Detection.Display.Win32DisplayInfo(sp.GetRequiredService<INvApiNative>(),
                sp.GetService<DrDxt.Detection.Gpu.Adlx.IAdlxNative>())));
        // Le mode de l'écran se pose côté utilisateur (user32), et le joueur le garde ou il revient.
        services.AddSingleton<IModeEcran, DrDxt.Detection.Display.Win32ModeEcran>();
        services.AddSingleton<ISourceDAlimentation, DrDxt.Detection.Hardware.Win32SourceDAlimentation>();
        services.AddSingleton<IConfirmationEcran, FenetreConfirmationEcran>();
        services.AddSingleton<IPowerPlanReader, Win32PowerPlanReader>();
        // La limite de puissance du processeur par PawnIO (lecture ; l'écriture passe par le helper).
        services.AddSingleton<DrDxt.Application.Tweaks.Hardware.ICpuPowerLimitReader, DrDxt.Detection.Cpu.PawnIoCpuPowerLimitReader>();
        services.AddSingleton<IServiceStateReader, Win32ServiceStateReader>();

        // Le vrai banc GPU déterministe. Le runner compute Vortice D3D11 porte le
        // DeterministicGpuLoadProbe (à la place de StubGpuLoadProbe). L'IStabilityTester reste
        // GpuStabilityTester (ajouté par TryAdd dans AddDrDxtApplication), mené par une vraie charge ;
        // sa sécurité Inconclusive→retour arrière est gardée.
        // Lecture et calcul seulement : pas de NVAPI, pas d'écriture de réglage GPU, pas d'élévation.
        services.AddSingleton<IGpuComputeRunner, VorticeGpuComputeRunner>();
        // Constructeur explicite à un argument → la charge Default (évite que la DI choisisse le
        // constructeur le plus gourmand et échoue sur GpuWorkloadSpec, non enregistré).
        services.AddSingleton<IGpuLoadProbe>(sp =>
            new DeterministicGpuLoadProbe(sp.GetRequiredService<IGpuComputeRunner>()));

        // La lecture ADLX (AdlxGpuTuningReader sur AdlxNative) remplace le StubAdlxGpuTuning (TryAdd dans
        // AddDrDxtApplication ; le dernier enregistrement gagne). Essayee sur une seule RX 9070 XT, sans rapport archive.
        // Sans amdadlx64.dll, IsAvailable rend faux sans effet de bord ni plantage. Lecture seule : l'ecriture passe par le
        // helper eleve. Une seule instance ADLX par processus, celle du lecteur partage quand la DLL est la.
        services.AddSingleton<IAdlxNative>(_ => (IAdlxNative?)DrDxt.Detection.Gpu.Adlx.AdlxReglages.Partage ?? new AdlxNative());
        services.AddSingleton<IAdlxGpuTuning>(sp =>
            new AdlxGpuTuningReader(sp.GetRequiredService<IAdlxNative>()));
        // La couture de latence AMD (Anti-Lag, limite FRTC / Chill) lue par ADLX, seulement si le pilote AMD a
        // posé amdadlx64.dll ; sinon StubAdlx (TryAdd dans AddDrDxtApplication) reste. Lecture seule : l'écriture
        // d'Anti-Lag passe par le helper élevé (ADLXTuningActions, AdlxTuningKind.AntiLag).
        if (DrDxt.Detection.Gpu.Adlx.AdlxReglages.DllPresente())
            services.AddSingleton<IAdlx>(_ => new DrDxt.Detection.Gpu.Adlx.AdlxLatencyReader(DrDxt.Detection.Gpu.Adlx.AdlxReglages.Lire));

        // Le pendant NVIDIA : NvApiGpuTuningReader sur NvApiNative remplace le StubNvApiGpuTuning. La recette d'ecriture
        // est validee sur une RTX 4060 (cœur 0→-15→retour, puissance 100→90→retour) ; les lectures ne demandent pas
        // d'elevation. Sans nvapi64.dll, IsAvailable rend faux sans effet de bord. L'ecriture passe par le helper eleve
        // (NvApiTuningActions, meme frontiere NvApiNative).
        services.AddSingleton<INvApiNative>(_ => new NvApiNative());
        services.AddSingleton<INvApiGpuTuning>(sp =>
            new NvApiGpuTuningReader(sp.GetRequiredService<INvApiNative>()));

        // La vraie couture de lecture de latence INvApi, sur la même frontière DRS de NvApiNative
        // (TryGetDrsGlobalSetting, validé en vrai sur une RTX 4060), à la place du StubNvApi qui
        // échoue proprement (TryAdd dans AddDrDxtApplication ; le dernier enregistrement gagne).
        // Les leviers de latence (VrrVsyncCap / RenderQueueCap / PreferMaxPerf) relèvent ainsi leur
        // vraie valeur DRS d'origine, pour un retour arrière fidèle, au lieu d'échouer proprement.
        // Les lectures ne sont pas élevées ; l'écriture passe toujours par le helper élevé (actions DRS).
        services.AddSingleton<INvApi>(sp =>
            new NvApiLatencyReader(sp.GetRequiredService<INvApiNative>(), sp.GetRequiredService<IRegistryReader>()));

        // Le chemin du jeu en cours, pour que SettingsStateReader lise les reglages
        // du pilote au profil que le pilote applique a ce jeu (parametre optionnel du lecteur).
        services.AddSingleton<IGameExecutable>(
            new DrDxt.App.Platform.RunningGameExecutable(new DrDxt.App.Platform.Win32GameLocator()));
    }

    // -------------------------------------------------------------------
    // Le reconciler et la persistance signée de l'état voulu, qu'il réimpose au démarrage. L'App ne signe pas : elle
    // envoie les entrées au helper élevé (CommitManifestOp), qui valide, fusionne, signe avec la clé de la machine et
    // écrit la racine admin. L'App relit en vérification seule (clé publique sous %ProgramData%\DrDxt ; clé de dev
    // embarquée en DEBUG seulement, refusée en RELEASE). Voir HelperDesiredStateStore.
    // -------------------------------------------------------------------
    private static void RegisterReconciler(IServiceCollection services)
    {
        // La sonde de pilote WMI (WmiGpuDriverProbe) a besoin de l'IWmiQuery de Detection ; l'App
        // l'enregistre déjà via AddDrDxtDetection, mais on l'ajoute par TryAdd, par prudence, pour que
        // cette méthode se suffise si l'ordre d'appel change.
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));

        // Les sondes de prod et le mapper/client de réapplication (liés au catalogue de descripteurs).
        // IElevatedClient est celui de l'App (RegisterRealSeams), réutilisé et non réenregistré :
        // l'ElevatedReapplyClient le résout depuis le conteneur.
        services.AddDrDxtReconcilerAdapters();

        // Politique et catalogue purs (source unique pour les surfaces et les emplacements).
        services.TryAddSingleton<IReconcilabilityPolicy, DefaultReconcilabilityPolicy>();
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();

        // Horloge UTC injectable (celle du reconciler, pas celle du bail du helper).
        services.TryAddSingleton<IClock, SystemClock>();

        // Lectures du manifeste en vérification seule : même racine ProgramData en écriture admin
        // que l'hôte. Le vérificateur vient du fichier de clé publique de la machine (écrit par le
        // helper) ou, en DEBUG seulement, de la clé publique de dev embarquée (AppManifestPublicKey). Un
        // manifeste altéré ou étranger est toujours refusé.
        services.TryAddSingleton<IManifestStorage>(_ => new ProgramDataManifestStorage());
        // Relue a chaque verification : la cle nait au premier commit du helper, pendant la session.
        services.TryAddSingleton<IManifestVerifier>(_ => new AppReloadingManifestVerifier(AppManifestPublicKey.DefaultPath()));
        // Aucun signataire dans l'App. Le manifeste est signe et ecrit par le helper eleve
        // (CommitManifestOp, cle propre a la machine) ; l'App envoie les entrees et relit en verify-only.
        // Le mapper d'enregistrement côté App : leviers validés gardés → état voulu du reconciler.
        services.TryAddSingleton<AppliedTweakManifestMapper>();
        // La reference de derive (build, pilote vus au moment de l'apply, que la 1re passe
        // du reconciliateur lit) est posee par le helper, dans le meme CommitManifestOp que le manifeste
        // (MachineManifestCommit → ApplyReferenceSeedingStore) : l'App, non elevee, ne peut pas ecrire la
        // racine admin (ReferenceDeDeriveTests). Un echec cote helper revient en MANIFEST_REFERENCE_NOT_SEEDED, et l'ecran le dit. Sans decorateur ici,
        // RafraichirAsync est celui de HelperDesiredStateStore : jamais d'UAC.
        services.TryAddSingleton<IDesiredStateStore>(sp => new HelperDesiredStateStore(
            sp.GetRequiredService<IElevatedClient>(),          // commit : le helper (UAC a la demande).
            DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer(), // rafraichissement : jamais d'UAC.
            new SignedDesiredStateStore(
                sp.GetRequiredService<IManifestStorage>(),
                signer: null, // verify-only : l'App ne tient aucune cle privee.
                sp.GetRequiredService<IManifestVerifier>(),
                sp.GetRequiredService<IReconcilabilityPolicy>(),
                sp.GetRequiredService<IClock>())));

        // Le notifier : un notifier léger côté App qui journalise via l'IDiagnosticsLog de l'App
        // (le LoggingReconcilerNotifier de l'hôte vit dans l'EXE de l'hôte et n'est pas référencé
        // ici ; celui-ci l'imite sans dépendance malvenue au projet du service).
        services.TryAddSingleton<IReconcilerNotifier, AppReconcilerNotifier>();

        // Aucun reconciliateur cote App : le vrai tourne dans le service eleve (DrDxt.ReconcilerHost).
        // DesiredStateReconciler, IReconcilerStateStore, IBootOutcomeStore et BootLoopGuard ne sont pas
        // enregistres ici : resolus par erreur, ils feraient de l'App un second ecrivain non eleve. Ce que
        // l'App garde du reconciliateur, c'est la lecture : IDesiredStateStore (verify-only) et le mapper de commit.
    }

    /// <summary>
    /// Ecrit le rapport de diagnostic a cote du journal de panne (un seul endroit a retenir), sous un nom date pour que
    /// deux rapports se comparent, et rend son chemin. BOM UTF-8 explicite : sans elle, les accents ressortent en
    /// charabia dans un outil qui devine mal l'encodage.
    /// </summary>
    private static string WriteSupportReport(string fileName, string content)
    {
        var path = Path.Combine(DrDxt.Core.DossierDonnees.Racine(), fileName);
        // Dossier du joueur : jamais à travers un nom posé d'avance en lien (l'App peut tourner élevée).
        var octets = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(content)).ToArray();
        var ecrit = new DrDxt.Core.FichierJsonAtomique(path, DrDxt.Core.ErrorCodes.IoFailure).WriteAtomic(octets);
        if (!ecrit.IsOk) throw new IOException(ecrit.Error!.Value.Message);
        return path;
    }

    /// <summary>
    /// La machine exportée, dans Documents (là où un joueur cherche un fichier qu'il doit envoyer), sans
    /// BOM comme les fixtures de tests/Shared/machines. Rend le chemin, que l'écran affiche.
    /// </summary>
    private static string EcrireMachineExportee(string nomDeFichier, string json)
        => EcrireDansDocuments(nomDeFichier, json);

    /// <summary>
    /// Le rapport de « Vérifier DrDXT sur mon PC », bilan en tête, à côté de la machine exportée. Sans le
    /// nom du PC : il ne porte que les réglages, leurs valeurs d'origine et ce qui a été relu.
    /// </summary>
    private static string EcrireRapportDeVerification(DrDxt.Application.Autopilot.ValidationComplete.Resultat r)
    {
        var maintenant = DateTimeOffset.Now;
        var bilan = r.NonRemis.Count == 0
            ? Docteur.T("App.Bilan.ToutRemis")
            : Docteur.T("App.Bilan.AVerifier") + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", r.NonRemis);
        var texte = bilan + Environment.NewLine + Environment.NewLine
            + Docteur.T("App.Bilan.Entete", typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3), maintenant)
            + Environment.NewLine + Environment.NewLine + r.Texte;
        // Ce texte part en public (ticket GitHub) : les règles de la machine exportée s'y appliquent aussi.
        texte = DrDxt.Detection.Repair.MachineExportee.AnonymiserTexte(texte, DrDxt.Detection.Repair.MachineExportee.ValeursDeCePc());
        return EcrireDansDocuments(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"verification-drdxt-{maintenant:yyyy-MM-dd-HHmmss}.txt"), texte);
    }

    // L'élévation réelle du processus, pas celle d'une machine rejouée : élevée, l'écriture hors du dossier tenu est gardée.
    private static string EcrireDansDocuments(string nomDeFichier, string json)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0) throw new DirectoryNotFoundException("dossier Documents introuvable");
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var eleve = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return DrDxt.App.Platform.EcritureDansDocuments.Ecrire(documents, nomDeFichier, json, eleve);
    }

    /// <summary>Les jeux mis à jour et les pilotes d'affichage changés depuis le scan précédent, notés dans le journal des
    /// changements (la frise les montre) à l'heure du scan, le scan précédent comme borne basse. Ne rend aucun constat :
    /// la frise est leur seul affichage. Une lecture ratée ne note rien ; un journal qui refuse, non plus, et l'écart
    /// revient au scan suivant.</summary>
    private static IReadOnlyList<DrDxt.Core.RepairFinding> NoterLeMonde()
    {
        try
        {
            var maintenant = DateTimeOffset.Now;
            var journal = DrDxt.Application.Parties.JournalDesChangements.ParDefaut();
            DrDxt.Detection.Hardware.MondeDeLaMachine.ComparerEtGarder(
                DrDxt.Core.DossierDonnees.Chemin(DrDxt.Detection.Hardware.MondeDeLaMachine.Fichier), maintenant,
                (ecarts, depuis) => ecarts.Select(e => journal.Ajouter(new DrDxt.Application.Parties.Changement(maintenant, e.Libelle, depuis, e.Cle)))
                    .ToList().TrueForAll(r => r.IsOk));
        }
        catch (Exception) { /* un monde non relu ne fait pas échouer la santé */ }
        return [];
    }

    /// <summary>Les changements datés des 30 derniers jours, rapprochés des arrêts brutaux et des
    /// erreurs matérielles de la semaine qui suit. Une lecture ratée rend une liste vide : rien n'est inventé.</summary>
    private static IReadOnlyList<DrDxt.Core.RepairFinding> StabiliteApresLesChangements()
    {
        try
        {
            var maintenant = DateTimeOffset.Now;
            var changements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut().Lire();
            // Un journal illisible se dit « non lu », il ne se tait plus.
            var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Since(maintenant.AddDays(-30));
            if (!arrets.IsOk || DrDxt.Detection.Repair.ErreursMaterielles.Lire(30) is not { } whea)
                return DrDxt.Application.Parties.StabiliteApresChangement.NonLu(changements, maintenant).ToList();
            var erreurs = whea.Where(e => e.Gravite is 0 or 1 or 2).Select(e => e.Quand).ToList();
            return DrDxt.Application.Parties.StabiliteApresChangement.Juger(changements, arrets.Value!, erreurs, maintenant).ToList();
        }
        catch (Exception) { return []; }
    }
}
