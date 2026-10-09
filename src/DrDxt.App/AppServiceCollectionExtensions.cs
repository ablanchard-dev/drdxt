using DrDxt.App.Navigation;
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
    /// <summary>
    /// Composes all DrDXT DI modules into the root service collection.
    /// Call this once at application startup before building the provider.
    /// </summary>
    /// <summary>Revue F-53 : un fichier illisible (le jeu l'écrit pendant le scan) se dit « non lu » pour ce jeu
    /// seul ; il ne cache plus les autres.</summary>
    internal static RepairFinding ConstatUnreal(string exe, FileInfo fichier)
    {
        var nom = DrDxt.Detection.Game.ReglagesUnreal.Nom(exe);
        try
        {
            return DrDxt.Detection.Game.ReglagesUnreal.Constat(DrDxt.Detection.Game.ReglagesUnreal.Lire(
                nom, fichier.FullName, File.ReadAllText(fichier.FullName), fichier.LastWriteTime), DateTime.Now);
        }
        catch (Exception)
        {
            return new RepairFinding(DrDxt.Detection.Game.ReglagesCod.Domaine, $"réglages de {nom}", null, null, RepairStatus.Unknown,
                $"le fichier de réglages du jeu ({fichier.FullName}) n'a pas pu être lu : rien n'est dit de ses réglages.");
        }
    }

    private static IReadOnlyList<RepairFinding> ReglagesDesJeuxUnreal()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Lire()
                .Select(p => p.Jeu).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(exe => (Exe: exe, Fichier: DrDxt.Detection.Game.ReglagesUnreal.Trouver(exe, local)))
                .Where(x => x.Fichier is not null)
                .Select(x => ConstatUnreal(x.Exe, x.Fichier!))
                .ToList();
        }
        catch (Exception) { return []; /* un fichier illisible ne casse pas le scan */ }
    }

    public static IServiceCollection AddDrDxtApp(this IServiceCollection services)
    {
        services.AddDrDxtCore();
        services.AddDrDxtDetection();
        // Le focus observe, pas devine. Sans cette ligne, DrDXT inferait l'alt-tab a partir de
        // la regularite des frametimes — une inference qui se trompe dans les deux sens.
        services.TryAddSingleton<IForegroundProbe, DrDxt.Detection.Win32ForegroundProbe>();
        services.AddDrDxtMeasurement();

        // M3 — the BIOS Doctor read+diff advisor (IBiosAdvisor) + guide library. The scan
        // flow consumes its findings (guidance-only, never written) so the user sees their
        // biggest real lever (XMP/EXPO off) that lives in the BIOS, not in any software tweak.
        services.AddDrDxtBios();

        // M6 — per-chip optimization profiles: registers IProfileMatcher (Core contract)
        // + the rich IChipProfileMatcher/IChipProfileCatalog. Registration only; the scan
        // flow's consumption of a recommendation (compose a TweakPlan / override the GPU
        // tuning targets from a profile's parameters) is the seam left to ScanViewModel.
        services.AddDrDxtProfiles();

        // R1/R2 — the real Windows seams + signed-IPC elevated client + the real
        // deterministic GPU bench. Registered before AddDrDxtApplication() so its
        // TryAdd* fail-clean stubs do not win. The remaining GPU tuning SDK seams
        // (INvApi*/IAdlx*) are still LEFT to the fail-clean stubs; IDisplayInfo is now real.
        RegisterRealSeams(services);

        // M2 — the P2 apply layer: ISafetyManager, ITweakEngine, every ITweak (D/E/F),
        // ITweakCatalog. AddDrDxtApplication() TryAdds fail-clean stubs only for seams
        // not already registered above.
        services.AddDrDxtApplication();

        // 08/10/2026 (T1b) : « Vérifier DrDXT sur mon PC » : la validation complète, sur les leviers et lecteurs réels.
        services.AddTransient<DrDxt.Application.Autopilot.ValidationDeLaMachine>();

        // M4 — the reconciler as a status/control surface (read drift + pause/resume).
        RegisterReconciler(services);

        // M5 — in-app scan ⇄ dashboard navigation (single shell surface).
        services.AddSingleton<INavigationService, NavigationService>();

        // M6 — the scan-flow consumer of a per-chip profile recommendation: composes the
        // profile-driven TweakPlan + clamps/overrides the GPU targets (IGpuTuningTargetSource)
        // + surfaces honest evidence. Depends on IChipProfileMatcher (AddDrDxtProfiles),
        // ITweakCatalog + IGpuTuningTargetSource (AddDrDxtApplication), all registered above.
        services.TryAddSingleton<IScanProfileConsumer, ScanProfileConsumer>();

        // L8 a — le filet d'une session tuee : le levier en cours d'essai est journalise avant
        // d'ecrire la machine, et rendu au demarrage suivant (App.OnStartup).
        services.TryAddSingleton<DrDxt.Application.Autopilot.IRevertEnAttente>(
            _ => DrDxt.Application.Autopilot.FichierRevertEnAttente.ParDefaut());

        // M6 — the dedicated, isolated GPU-tuning apply step: runs the profile's clamped GPU
        // selections as their own engine batch (separate from the safe batch), re-clamping to
        // GpuSafeBounds and reusing the engine + R2 stability/revert net. Depends on
        // ITweakEngine + IGpuTuningTargetSource (both from AddDrDxtApplication, above).
        services.TryAddSingleton<IGpuTuningApplyStep, GpuTuningApplyStep>();

        // L0.2 — la couture du voyage dans le BIOS. Elle relie trois choses deja enregistrees
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
            Func<IReadOnlyList<RepairFinding>> sante = () => [.. new DrDxt.Detection.Repair.RepairAuditService().Lire(), DrDxt.App.Platform.SanteWindows.Constat(),
                    .. DrDxt.Detection.Repair.InventaireMachine.ComparerEtGarder(
                        DrDxt.Core.DossierDonnees.Chemin("inventaire-machine.json"), DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil),
                    // 04/10/2026 (plan 3.2) : un arrêt brutal ou une erreur matérielle dans la semaine d'un changement daté.
                    .. StabiliteApresLesChangements()];
            bool? eleve = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            DrDxt.Bios.NvramBiosSettingsReader? nvram = new(DrDxt.App.Platform.VariablesFirmware.Lire);
            Func<ViewModels.CarteDeLaMachine.LienPcie?>? pcie = () =>
                new DrDxt.GpuNative.Nvml.NvmlNative().TryGetPcieLink(0, out var gen, out var larg, out var genMax, out var largMax)
                    ? new ViewModels.CarteDeLaMachine.LienPcie(gen, larg, genMax, largMax) : null;
            Func<IReadOnlyList<RepairFinding>>? reglagesJeu = () =>
            [
                .. DrDxt.Detection.Game.ReglagesCod.Trouver() is { } f
                    ? DrDxt.Detection.Game.ReglagesCod.Constats(
                        DrDxt.Detection.Game.ReglagesCod.Lire(f.FullName, File.ReadAllText(f.FullName), f.LastWriteTime),
                        DateTime.Now)
                    : Array.Empty<RepairFinding>(),
                // 07/10/2026 (P1) : les jeux Unreal Engine joués sur ce PC (relevé des parties), réglages lus.
                .. ReglagesDesJeuxUnreal(),
            ];
#if DEBUG
            // chantier B14 (S8) : « machine=<nom> » rejoue une machine enregistrée à la place de ce PC. Revue
            // de la passation (P0-2) : les droits viennent du fichier ; PCIe (NVML), NVRAM du BIOS et réglages du jeu, qui
            // liraient ce PC, sont coupés.
            if (DrDxt.App.Platform.MachineDeTest.Active is { } machine)
            {
                var doublure = new DrDxt.App.Platform.MachineDeTest.Detecteur(machine);
                (detecteur, reglages, mesure, disques, sante) = (doublure, doublure, doublure, doublure, () => machine.Sante);
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
                sp.GetRequiredService<IGpuTuningApplyStep>(),
                sp.GetRequiredService<INavigationService>(),
                sp.GetRequiredService<DashboardViewModel>,
                sp.GetRequiredService<IDesiredStateStore>(),
                sp.GetRequiredService<AppliedTweakManifestMapper>(),
                sp.GetRequiredService<IBiosAdvisor>(),
                sp.GetRequiredService<IGuideGenerator>(),
                key => localizer.Get(key),
                // Generic game detection (no per-game list) so the free scan measures the real game
                // currently running; no game → honest config-only. Stateless Win32 boundary.
                new DrDxt.App.Platform.Win32GameLocator(),
                // L0.2 — la boucle fermee du BIOS. Sans cette ligne, IBiosLoopVerifier et
                // IPendingBiosChangeStore restaient enregistres et jamais resolus : le produit
                // ne prouvait a personne qu'un changement BIOS avait pris.
                sp.GetRequiredService<DrDxt.App.Platform.IFirmwareTrip>(),
                // D7 / gate G6 — de quoi ecrire le rapport de diagnostic que la personne colle
                // dans son ticket.
                WriteSupportReport,
                typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(),
                // LM — le banc memoire, et l'identite de la machine qui rend une reference
                // comparable a elle seule. Sans ces deux lignes le banc existerait sans etre
                // appele, et le scan continuerait d'avouer qu'il ne sait pas lire la topologie.
                new DrDxt.Measurement.Memory.MemoryBaselineService(
                    new DrDxt.Measurement.Memory.RealMemoryBench(),
                    new DrDxt.Measurement.Memory.MemoryBaselineStore(
                        new DrDxt.Measurement.Memory.FileSystemMemoryBaselineFile()),
                    () => DateTimeOffset.UtcNow),
                () => sp.GetRequiredService<IBiosMachineId>().Current,
                // Le journal des mesures. Sans cette ligne, un seul fichier survivrait a une
                // session — la reference memoire — et le produit redecouvrirait la machine a chaque
                // lancement. Une degradation lente est precisement ce qu'un scan isole ne voit pas :
                // prise seule, chaque mesure parait normale. Le scan depose la sienne, le scan
                // suivant peut conclure.
                new DrDxt.Measurement.PerformanceJournalStore(
                    new DrDxt.Measurement.FileSystemPerformanceJournalFile()),
                // La ligne qui manquait. Le 09/09/2026, onze types de lecture — services
                // Windows, MMCSS, BCD, type de vidage, incidents noyau — etaient references par
                // `tools/repairaudit` et par personne d'autre : prouves, testes, et invisibles de
                // quiconque ouvre le produit. Un outil demontre qu'un lecteur fonctionne ; il ne
                // met rien entre les mains de quelqu'un. Sans cette ligne, la section d'audit
                // reste vide et le travail de deux jours ne sort toujours pas.
                // 25/09/2026 : plus l'etat du magasin de composants (DISM /CheckHealth, par le helper).
                // 03/10/2026 (tour H1) : et ce qui a été installé, retiré ou mis à jour depuis le scan précédent.
                sante,
                // Ce que le processeur rend a un jeu. Mesure la premiere fois, relue ensuite : plus
                // de quarante secondes de charge complete a chaque scan chaufferaient la machine du
                // client pour un chiffre qui ne bouge pas.
                //
                // La liste des processeurs vient de la TOPOLOGIE, jamais d'une formule : c'est
                // ici, dans la couche qui reference `DrDxt.Detection`, qu'elle peut etre lue. Un
                // processeur logique par cœur physique de la classe la plus rapide, groupe 0
                // seulement — hors du groupe 0 le masque d'affinite change de sens.
                new DrDxt.Measurement.Cpu.CpuBaselineService(
                    new DrDxt.Measurement.Cpu.RealCpuSustainedBench(() =>
                    {
                        // Type ecrit plutot que `var` : ce que l'App manipule ici s'appelle
                        // `LogicalProcessor`, et le cliquet « construit mais jamais appele » compte
                        // par NOM. Le 11/09/2026, quand `ThreadResidencyTracer` a quitte `src/`
                        // pour son outil, ce `var` a fait passer `LogicalProcessor` pour orphelin
                        // alors que cette ligne le lit — un faux positif que le test annonce dans
                        // son en-tete. Le nommer dit la verite au comptage.
                        IReadOnlyList<DrDxt.Detection.Cpu.LogicalProcessor> carte =
                            new DrDxt.Detection.Cpu.CpuTopology().Lire();
                        return DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(carte);
                    },
                    // 25/09/2026 : la puissance du paquet pendant la charge soutenue, pour dire si la
                    // baisse vient d'une limite de puissance du BIOS.
                    () => DrDxt.Detection.Cpu.PuissancePaquetReader.LireWatts(new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                    // 03/10/2026 (M2) : la fréquence des cœurs du banc, pour voir une limite atteinte dès la première seconde.
                    new DrDxt.Detection.Cpu.CpuFrequencyReader().FrequenceDe),
                    new DrDxt.Measurement.Cpu.CpuBaselineStore(
                        new DrDxt.Measurement.Cpu.FileSystemCpuBaselineFile())),
                // Le pilier maintenir. Le magasin, le comparateur et l'avertissement de conditions
                // existaient depuis le 08/09 ; seul `tools/repairaudit` les appelait, et le produit
                // prenait donc une photo de la machine puis l'oubliait. Une degradation lente —
                // celle qu'un scan isole ne voit jamais — lui echappait par construction.
                //
                // Les CONDITIONS voyagent avec la photo : sans elles, le journal a deja annonce
                // « le multi-canal n'est plus prouve » alors qu'une compilation tournait pendant la
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
                    // 16/09/2026 : l'empreinte du module qui lit. Une compilation deterministe rend le
                    // meme identifiant pour le meme code : il ne change que si le lecteur a change.
                    empreinteLecteur: typeof(DrDxt.Detection.Repair.RepairAuditService).Assembly
                        .ManifestModule.ModuleVersionId.ToString("N")[..12],
                    eleve: new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
                    // 17/09/2026 : l'appli ne se compare qu'à ses propres photos (pas à celles de l'outil).
                    lecteur: "appli").EnregistrerEtComparer(constats),
                // NS-9 — D'OU vient l'instabilite du reseau. L'instrument de latence et
                // l'analyseur de chemin etaient ecrits, testes, et appeles par `tools/netprobe`
                // seulement : le produit lisait les reglages de la carte et ne mesurait rien du
                // reseau — le domaine qu'un joueur competitif ressent en premier.
                //
                // La passerelle vient de la carte physique, jamais de « la premiere passerelle
                // trouvee » : un tunnel VPN en publie une aussi, et la mesurer repondrait sur le
                // tunnel au lieu du lien local.
                ct => new DrDxt.Detection.Network.AttributionReseauService(
                    new DrDxt.Detection.Network.NetworkAdapterReader(
                        new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                    new DrDxt.Measurement.Network.NetworkLatencyMeter(
                        new DrDxt.Measurement.Network.SystemPingProbe())).LigneAsync(ct),
                // L8 b — le cerveau de l'optimiser. Un descripteur devient un candidat mesurable,
                // journalise avant d'ecrire (pending-revert) et relu par AppliedStateVerifier.
                d => sp.GetRequiredService<ITweakCatalog>().Get(d.Id) is { IsOk: true, Value: { } levier }
                    ? new DrDxt.Application.Autopilot.TweakCandidate(levier, d,
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IRegistryReader>(),
                        sp.GetRequiredService<DrDxt.Application.Autopilot.IRevertEnAttente>(),
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.INvApi>(),
                        sp.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IPowerPlanReader>(),
                        // Un keep inscrit son origine au manifeste signe : la desinstallation la rend.
                        sp.GetRequiredService<IDesiredStateStore>())
                    : null,
                // Ce que la stabilite interdit : les memes lectures que tools/candidatprobe.
                () => (DrDxt.Detection.Hardware.StabiliteLue.InstabiliteGpu(
                           new DrDxt.Detection.Hardware.WerLiveKernelReportLog()),
                       // 08/10/2026 (D1) : un arrêt tombé dans une partie interrompue est dit « sous charge ».
                       DrDxt.Detection.Hardware.StabiliteLue.ArretsBrutaux(
                           new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog(), DateTimeOffset.UtcNow) is { } arrets
                           ? ClasseurDArrets.MarquerSousCharge(arrets, DrDxt.Application.Parties.PartiesMemorisees.ParDefaut().Interrompues())
                           : null),
                // Phase 18 — les threads du jeu pendant la mesure du scan (espace utilisateur, sans elevation,
                // aucune injection : compatible anti-triche).
                observerThreads: (pid, fenetre) => new DrDxt.Detection.Cpu.ThreadCpuReader().Observer(pid, fenetre),
                // Phase 18 — les pertes a l'interface pendant la partie. Une seule carte physique
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
                // Phase 18 — les DPC/ISR des pilotes pendant la partie (session ETW noyau privee, arretee
                // dans tous les cas ; sans administrateur le constat sort « non lu »).
                mesurerDpc: fenetre => DrDxt.Detection.Latency.LatenceDpc.Classer(
                    new DrDxt.Detection.Latency.DpcLatencyTracer().Observer(fenetre)),
                // Le reservoir des candidats juges, a cote du journal de performance. Sans cette
                // ligne, la provenance d'un keep (p95 seul, 1 % low seul, les deux) vivait le temps
                // d'une session et le corpus qui doit trancher ne commencait jamais.
                journalCandidats: new DrDxt.Application.Autopilot.JournalDesCandidats(
                    DrDxt.Application.Autopilot.JournalDesCandidats.CheminParDefaut(),
                    () => sp.GetRequiredService<IBiosMachineId>().Current),
                // 11/09/2026 — ce qui tourne A cote du jeu pendant la mesure (OBS, navigateur,
                // compilations). Lecture seule, la meme source que le Gestionnaire des taches.
                voisins: new DrDxt.App.Platform.VoisinsWin32(),
                // 16/09/2026 — relancer l'appli elevee pour lire ce qui exige l'administrateur.
                // Refus de l'UAC (erreur 1223) = false : l'appli reste ouverte et le dit.
                estEleve: eleve,
                relancerEnAdministrateur: () =>
                {
                    if (Environment.ProcessPath is not { } exe) return false;
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                        });
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return false;
                    }
                    System.Windows.Application.Current?.Shutdown();
                    return true;
                },
                biosNvram: nvram,
                // 24/09/2026 — le bufferbloat montant, sur le trafic reel du joueur : rien n'est envoye
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
                // 03/10/2026 (P3.1c) — la route vers le serveur de la partie : écoute des envois UDP du jeu (lecture),
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
                // 03/10/2026 (tour G) — la version du pilote graphique, demandée au fabricant (seule requête vers un serveur
                // hors des pings : NVIDIA, pour la dernière version publiée).
                verifierPilote: gpus => DrDxt.Detection.Gpu.VersionDuPilote.VerifierAsync(gpus, new DrDxt.Detection.Gpu.SourceNvidia(),
                    new DrDxt.Detection.Gpu.SourceAmd(), DrDxt.Detection.Gpu.VersionDuPilote.RadeonSoftwareVersion),
                // 03/10/2026 (P3.3) — la mémoire libre, relevée toutes les demi-secondes pendant la fenêtre : le plus bas.
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
                // 07/10/2026 (T1) — « Exporter ma machine » : le format des fixtures, anonymisé, dans Documents.
                exporterMachine: (hw, st, constats) =>
                {
                    var maintenant = DateTimeOffset.Now;
                    var nom = $"machine-exportee-{maintenant:yyyy-MM-dd-HHmmss}";
                    // Trois nombres : une version à quatre a la forme d'une adresse IP, et l'anonymisation la retirerait.
                    var note = $"exportée par DrDXT {typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3)} le "
                        + $"{maintenant:dd/MM/yyyy} ; santé lue sur la machine, pas rejouée";
                    return EcrireMachineExportee(nom + ".json", DrDxt.Detection.Repair.MachineExportee.Json(nom, note, eleve, hw, st,
                        constats, DrDxt.Detection.Repair.MachineExportee.ValeursDeCePc()));
                })
            {
                FichierBcdRendu = DrDxt.Core.DossierDonnees.Chemin("bcd-rendu.json"),
            };
            // 08/10/2026 (T1b) : « Vérifier DrDXT sur mon PC » : la validation complète par les leviers et le canal élevé du
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
                    .ValiderAsync(journal, progres, JeuOuvert).ConfigureAwait(false);
                string? chemin = null;
                try { chemin = EcrireRapportDeVerification(r); }
                catch (Exception) { /* le bilan reste à l'écran */ }
                // ROAD1 (08/10/2026) : la liste « ce que DrDXT fait ici » dit « vérifié ici » pour ces leviers.
                try { DrDxt.Application.Tweaks.LeviersVerifies.Enregistrer(DrDxt.Core.DossierDonnees.Chemin("leviers-verifies.json"), r.Empreinte, r.EssayesIds, r.ValidesIds); }
                catch (Exception) { /* la liste ne le dira pas ; le rapport, si */ }
                return new VerificationRendue(null, r.NonRemis, chemin, r.Valides, r.NonTouches, r.Refuses, r.ArretPar);
            };
            // 03/10/2026 (D6.2) : l'analyse lancée sans droits se relance en administrateur et enchaîne, une seule fois.
            vm.RelancerEleveEtAnalyser = scene =>
            {
                if (Environment.ProcessPath is not { } exe) return false;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--analyser --scene={scene}")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return false;
                }
                System.Windows.Application.Current?.Shutdown();
                return true;
            };
            // 03/10/2026 (D6.1) : les durées réelles des étapes, gardées sur la machine pour que la jauge suive l'analyse.
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

        // M2 G1 / M5 — the before/after dashboard VM. Load(ApplyReport) fills it; the
        // live constructor wires the revert (ISafetyManager) + back-to-scan (nav) paths.
        services.AddTransient<DashboardViewModel>(sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            return new DashboardViewModel(
                sp.GetRequiredService<ISafetyManager>(),
                sp.GetRequiredService<INavigationService>(),
                sp.GetRequiredService<ScanViewModel>,
                key => localizer.Get(key))
            {
                // 04/10/2026 (plan 6.1) : « Annuler » sort aussi le levier du manifeste du service et du journal des leviers.
                ApresAnnulation = id => DrDxt.App.Platform.RetraitDeLevier.ApresAnnulationAsync(
                    sp.GetRequiredService<DrDxt.Core.IDesiredStateStore>(),
                    new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut()), id),
            };
        });

        return services;
    }

    // R1 — real Windows-backed seams (read-only) + the signed-IPC elevated client.
    // These run as the non-elevated app; the privileged WRITES still go through the
    // elevated helper via IElevatedClient (typed, allowlisted ops only).
    private static void RegisterRealSeams(IServiceCollection services)
    {
        // Privileged frontier: the real signed-IPC client (fail-clean if no helper).
        services.AddSingleton<IElevatedClient>(_ => ElevatedClientFactory.Create());

        // Safety seams.
        services.AddSingleton<IRestorePointService, WmiRestorePointService>();
        services.AddSingleton<IConsentPrompt, MessageBoxConsentPrompt>();

        // Read seams (latency / fix-it capture the real original state for revert).
        services.AddSingleton<IRegistryReader, Win32RegistryReader>();
        // The display seam stops being a stub. Read-only user32 enumeration; on a machine with
        // no window station it fails clean, so callers skip rather than conclude "no display".
        // L8 suite — NVAPI passe EXPLICITEMENT : sans lui G-Sync reste illisible et le levier de cadence meurt.
        services.AddSingleton<IDisplayInfo>(sp =>
            new DrDxt.Detection.Display.Win32DisplayInfo(sp.GetRequiredService<INvApiNative>(),
                sp.GetService<DrDxt.Detection.Gpu.Adlx.IAdlxNative>()));
        // L-ECRAN : le mode de l'écran se pose côté utilisateur (user32), et le joueur le garde ou il revient.
        services.AddSingleton<IModeEcran, DrDxt.Detection.Display.Win32ModeEcran>();
        services.AddSingleton<IConfirmationEcran, FenetreConfirmationEcran>();
        services.AddSingleton<IPowerPlanReader, Win32PowerPlanReader>();
        services.AddSingleton<IServiceStateReader, Win32ServiceStateReader>();

        // R2 — the real deterministic GPU bench. The Vortice D3D11 compute runner
        // backs the DeterministicGpuLoadProbe (replacing StubGpuLoadProbe). The
        // IStabilityTester stays GpuStabilityTester (AddDrDxtApplication TryAdds it),
        // now driven by a real load — its fail-safe Inconclusive→revert is preserved.
        // Read/COMPUTE only: no NVAPI, no GPU-tuning write, no elevation.
        services.AddSingleton<IGpuComputeRunner, VorticeGpuComputeRunner>();
        // Explicit single-arg ctor → the Default workload spec (avoids DI picking the
        // greedy ctor and failing on the unregistered GpuWorkloadSpec).
        services.AddSingleton<IGpuLoadProbe>(sp =>
            new DeterministicGpuLoadProbe(sp.GetRequiredService<IGpuComputeRunner>()));

        // T5 — the real ADLX read seam: AdlxGpuTuningReader over the AdlxNative P/Invoke
        // boundary, REPLACING the fail-clean StubAdlxGpuTuning (TryAdd in AddDrDxtApplication;
        // last-registration-wins, mirroring the IGpuLoadProbe replacement just above). The
        // reader was live-validated on a real AMD RX 9070 XT (76d225d) before being wired in.
        // On AMD this makes IAdlxGpuTuning.IsAvailable true → GpuUndervolt/GpuPowerLimit
        // .ApplyAmdAsync fires. On a non-AMD machine AdlxNative's IsAvailable lazily probes
        // amdadlx64.dll and every path is catch-guarded (DllNotFoundException → false), so the
        // ctor is side-effect-free and the AMD apply path degrades clean (no crash, no write).
        // Read-only: the privileged write still goes through the elevated helper (T2/T3).
        // A1 (06/10/2026) : une seule instance ADLX par processus, celle du lecteur partagé quand la DLL est là.
        services.AddSingleton<IAdlxNative>(_ => (IAdlxNative?)DrDxt.Detection.Gpu.Adlx.AdlxReglages.Partage ?? new AdlxNative());
        services.AddSingleton<IAdlxGpuTuning>(sp =>
            new AdlxGpuTuningReader(sp.GetRequiredService<IAdlxNative>()));
        // A1 — la couture de latence AMD (Anti-Lag, limite FRTC / Chill) lue par ADLX, seulement si le pilote AMD a
        // posé amdadlx64.dll ; sinon StubAdlx (TryAdd dans AddDrDxtApplication) reste. Lecture seule : l'écriture
        // d'Anti-Lag n'existe pas côté helper (ADLXTuningActions la refuse).
        if (DrDxt.Detection.Gpu.Adlx.AdlxReglages.DllPresente())
            services.AddSingleton<IAdlx>(_ => new DrDxt.Detection.Gpu.Adlx.AdlxLatencyReader(DrDxt.Detection.Gpu.Adlx.AdlxReglages.Lire));

        // N4 — the real NVAPI read seam (NVIDIA mirror of the ADLX block above):
        // NvApiGpuTuningReader over the NvApiNative P/Invoke boundary, REPLACING the
        // fail-clean StubNvApiGpuTuning (TryAdd in AddDrDxtApplication; last-registration-wins).
        // The NVAPI write recipe was live-validated on an RTX 4060 (11b6b41: core offset
        // 0→-15→revert, power 100→90→revert); the reads are non-elevated (NvAPI_Initialize +
        // GET need no elevation). On NVIDIA this makes INvApiGpuTuning.IsAvailable true →
        // GpuUndervolt/GpuPowerLimit.ApplyNvidiaAsync fires. On a non-NVIDIA machine
        // NvApiNative lazily probes nvapi64.dll and every path is catch-guarded (absent DLL →
        // false), so the ctor is side-effect-free and the NVIDIA apply path degrades clean
        // (no crash, no write). Read-only: the privileged write still goes through the
        // elevated helper (NvApiTuningActions over the same NvApiNative boundary).
        services.AddSingleton<INvApiNative>(_ => new NvApiNative());
        services.AddSingleton<INvApiGpuTuning>(sp =>
            new NvApiGpuTuningReader(sp.GetRequiredService<INvApiNative>()));

        // N5 — the real INvApi latency read seam over the same NvApiNative DRS boundary
        // (TryGetDrsGlobalSetting, live-validated on an RTX 4060), REPLACING the fail-clean
        // StubNvApi (TryAdd in AddDrDxtApplication; last-registration-wins). This lets the
        // latency tweaks (VrrVsyncCap / RenderQueueCap / PreferMaxPerf) capture their real
        // original DRS value for a faithful revert instead of failing clean. Reads are
        // non-elevated; the write still goes through the elevated helper (DRS actions).
        services.AddSingleton<INvApi>(sp =>
            new NvApiLatencyReader(sp.GetRequiredService<INvApiNative>(), sp.GetRequiredService<IRegistryReader>()));

        // PHASE 18 F1 — le chemin du jeu en cours, pour que SettingsStateReader lise les reglages
        // du pilote au profil que le pilote applique a ce jeu (parametre optionnel du lecteur).
        services.AddSingleton<IGameExecutable>(
            new DrDxt.App.Platform.RunningGameExecutable(new DrDxt.App.Platform.Win32GameLocator()));
    }

    // -------------------------------------------------------------------
    // M4 — the reconciler as the App's STATUS / CONTROL surface + the COMMIT (signer)
    // side of the persistence moat.
    //
    // After a VALIDATED apply the App COMMITS the desired state so the reconciler re-asserts
    // it at boot (§9.12). 11/09/2026 — the App is NO LONGER the signer: it sends the entries
    // to the elevated helper (CommitManifestOp, through the existing authenticated
    // IElevatedClient), which validates, merges, signs with the MACHINE's own key and writes
    // the admin root. The App reads back verify-only (public key under %ProgramData%\DrDxt;
    // DEBUG-only fallback to the embedded dev key, a RELEASE build fails closed). This also
    // registers the AppliedTweakManifestMapper and the sidecar state store the pause/resume
    // kill-switches mutate (the M6 UI consumes these). See HelperDesiredStateStore.
    // -------------------------------------------------------------------
    private static void RegisterReconciler(IServiceCollection services)
    {
        // The WMI driver probe (WmiGpuDriverProbe) needs Detection's IWmiQuery; the App
        // already registers it via AddDrDxtDetection, but TryAdd it defensively so this
        // method is self-sufficient if the call order ever changes.
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));

        // Prod probes + the re-apply mapper/client (binds to the descriptor catalog).
        // IElevatedClient is the App's existing one (RegisterRealSeams) — REUSED, not
        // re-registered: the ElevatedReapplyClient resolves it from the container.
        services.AddDrDxtReconcilerAdapters();

        // Pure policy / catalog (single source of truth for surfaces + locations).
        services.TryAddSingleton<IReconcilabilityPolicy, DefaultReconcilabilityPolicy>();
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();

        // Injectable UTC clock (the reconciler clock, not the helper-lease one).
        services.TryAddSingleton<IClock, SystemClock>();

        // VERIFY-only manifest reads: same ProgramData admin-write root as the host. The
        // verifier comes from the machine's public-key file (written by the helper) or, DEBUG
        // only, the embedded V1 public key (AppManifestPublicKey) — a tampered/foreign manifest
        // still fails closed.
        services.TryAddSingleton<IManifestStorage>(_ => new ProgramDataManifestStorage());
        // 11/09/2026 — relue a chaque verification : la cle nait au premier commit du helper, pendant la session.
        services.TryAddSingleton<IManifestVerifier>(_ => new AppReloadingManifestVerifier(AppManifestPublicKey.DefaultPath()));
        // 11/09/2026 — aucun signataire dans l'App. Le manifeste est signe et ecrit par le helper eleve
        // (CommitManifestOp, cle propre a la machine) ; l'App envoie les entrees et relit en verify-only.
        // The App-side commit mapper: kept validated tweaks → reconciler desired state.
        services.TryAddSingleton<AppliedTweakManifestMapper>();
        // 11/09/2026 — la reference de derive (build, pilote vus au moment de l'apply, que la 1re passe
        // du reconciliateur lit) est posee par le HELPER, dans le meme CommitManifestOp que le manifeste
        // (MachineManifestCommit → ApplyReferenceSeedingStore). L'App ne l'ecrit plus : non elevee, elle ne
        // pouvait pas ecrire la racine admin, et l'echec etait avale (ReferenceDeDeriveTests). Un echec
        // cote helper revient en MANIFEST_REFERENCE_NOT_SEEDED, et l'ecran le dit. Sans decorateur ici,
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

        // The notifier — a thin App-side notifier journalling via the App's IDiagnosticsLog
        // (the host's LoggingReconcilerNotifier lives in the host EXE and is not referenced
        // here; this mirrors it without a bad dependency on the service project).
        services.TryAddSingleton<IReconcilerNotifier, AppReconcilerNotifier>();

        // 11/09/2026 — aucun reconciliateur cote App. Le vrai tourne dans le service eleve
        // (DrDxt.ReconcilerHost). L'App enregistrait ici DesiredStateReconciler, IReconciler,
        // IReconcilerStateStore, IBootOutcomeStore et BootLoopGuard sans qu'aucun code de l'App
        // ne les resolve jamais : enregistrement MORT. Mort n'est pas inoffensif — le jour ou
        // quelqu'un les resout par erreur, un SECOND ecrivain non eleve redevient actif sans
        // qu'aucun test ne rougisse. Ce que l'App garde du reconciliateur, c'est la lecture :
        // IDesiredStateStore (verify-only) et le mapper de commit.
    }

    /// <summary>
    /// Ecrit le rapport de diagnostic a cote du journal de panne, et rend son chemin.
    ///
    /// Le meme dossier que crash.log, et c'est voulu : un client qu'on guide au telephone
    /// apprend un endroit, pas deux. Le nom porte la date — deux rapports du meme client a deux
    /// moments racontent une histoire que le second seul ne raconte pas.
    ///
    /// BOM UTF-8 explicite, pour la meme raison que le journal de panne : sans elle, les
    /// accents ressortent en charabia chez qui l'ouvre avec un outil qui devine mal.
    /// </summary>
    private static string WriteSupportReport(string fileName, string content)
    {
        var folder = DrDxt.Core.DossierDonnees.Racine();
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// 07/10/2026 (T1) : la machine exportée, dans Documents (là où un joueur cherche un fichier qu'il doit envoyer), sans
    /// BOM comme les fixtures de tests/Shared/machines. Rend le chemin, que l'écran affiche.
    /// </summary>
    private static string EcrireMachineExportee(string nomDeFichier, string json)
        => EcrireDansDocuments(nomDeFichier, json);

    /// <summary>
    /// 08/10/2026 (T1b) : le rapport de « Vérifier DrDXT sur mon PC », bilan en tête, à côté de la machine exportée. Sans le
    /// nom du PC : il ne porte que les réglages, leurs valeurs d'origine et ce qui a été relu.
    /// </summary>
    private static string EcrireRapportDeVerification(DrDxt.Application.Autopilot.ValidationComplete.Resultat r)
    {
        var maintenant = DateTimeOffset.Now;
        var bilan = r.NonRemis.Count == 0
            ? "BILAN : tout est remis d'origine."
            : "BILAN : À VÉRIFIER À LA MAIN :" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", r.NonRemis);
        var texte = bilan + Environment.NewLine + Environment.NewLine
            + $"DrDXT {typeof(AppServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3)}, vérification du {maintenant:dd/MM/yyyy HH:mm}"
            + Environment.NewLine + Environment.NewLine + r.Texte;
        // 09/10/2026 : ce texte part en public (ticket GitHub) : les règles de la machine exportée s'y appliquent aussi.
        texte = DrDxt.Detection.Repair.MachineExportee.AnonymiserTexte(texte, DrDxt.Detection.Repair.MachineExportee.ValeursDeCePc());
        return EcrireDansDocuments($"verification-drdxt-{maintenant:yyyy-MM-dd-HHmmss}.txt", texte);
    }

    private static string EcrireDansDocuments(string nomDeFichier, string json)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0) throw new DirectoryNotFoundException("dossier Documents introuvable");
        var chemin = Path.Combine(documents, nomDeFichier);
        File.WriteAllText(chemin, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return chemin;
    }

    /// <summary>04/10/2026 (plan 3.2) : les changements datés des 30 derniers jours, rapprochés des arrêts brutaux et des
    /// erreurs matérielles de la semaine qui suit. Une lecture ratée rend une liste vide : rien n'est inventé.</summary>
    private static IReadOnlyList<DrDxt.Core.RepairFinding> StabiliteApresLesChangements()
    {
        try
        {
            var maintenant = DateTimeOffset.Now;
            var changements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut().Lire();
            // Revue adverse du 09/10/2026 (R7) : un journal illisible se dit « non lu », il ne se tait plus.
            var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Since(maintenant.AddDays(-30));
            if (!arrets.IsOk || DrDxt.Detection.Repair.ErreursMaterielles.Lire(30) is not { } whea)
                return DrDxt.Application.Parties.StabiliteApresChangement.NonLu(changements, maintenant).ToList();
            var erreurs = whea.Where(e => e.Gravite is 0 or 1 or 2).Select(e => e.Quand).ToList();
            return DrDxt.Application.Parties.StabiliteApresChangement.Juger(changements, arrets.Value!, erreurs, maintenant).ToList();
        }
        catch (Exception) { return []; }
    }
}
