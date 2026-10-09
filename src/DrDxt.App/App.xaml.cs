using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DrDxt.App.Navigation;
using DrDxt.App.ViewModels;
using DrDxt.App.Views;
using DrDxt.Infrastructure.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App;

// 'Application' is qualified to System.Windows.Application: the DrDxt.Application
// project reference also brings a 'DrDxt.Application' namespace into scope, which
// otherwise collides with the WPF base type below.
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

#if DEBUG
    // QA-only, DEBUG builds only (compiled out of Release so it can never ship): launch
    // with this arg to render the dashboard with sample data for a screenshot.
    private const string DashboardPreviewArg = "dashboard-preview";

    // Meme affordance pour l'ecran d'activation (L2 / gate G1). Elle existe pour une raison
    // apprise a ses depens : un UserControl ne construit ses ressources qu'au moment ou il est
    // AFFICHE. Une ressource au mauvais nom passe la compilation, passe les tests, et tue
    // l'application au premier clic du client. Cet argument permet de voir l'ecran sans avoir a
    // simuler un scan complet et un achat.

    // Captures de l'écran de résultat sur la vraie machine : le scan part tout seul, et
    // « apercu-complet » affiche la version débloquée. Affichage seulement : Optimiser relit la
    // licence en direct, et rien de ceci n'existe dans la compilation livrée.
    private const string ScanAutoArg = "scan-auto";
#endif

    /// <summary>
    /// Le fichier ou le produit ecrit ce qui l'a tue. A cote des donnees de l'app, pas dans
    /// %TEMP% : un client qu'on guide au telephone doit pouvoir le trouver, et il doit survivre
    /// a un nettoyage de disque.
    /// </summary>
    private static string CrashLogPath => DrDxt.Core.DossierDonnees.Chemin("crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        // chantier B14 (S8) : « machine=amd-radeon-synthetique » rejoue cette machine enregistrée (avant la
        // construction des services, qui la lisent).
        // Les données vont dans un dossier jetable : un rejeu n'écrit jamais dans celles du joueur.
        if (e.Args.FirstOrDefault(a => a.StartsWith("machine=", StringComparison.OrdinalIgnoreCase)) is { } machine)
            DrDxt.App.Platform.MachineDeTest.Activer(machine["machine=".Length..], Environment.GetEnvironmentVariable,
                Environment.SetEnvironmentVariable);
#endif
        base.OnStartup(e);

        // ── Le filet, et pourquoi il existe ────────────────────────────────────────────────
        //
        // Sans lui, une exception non geree tue le processus sans un mot : pas de fenetre, pas
        // de message, l'icone disparait. Le client ne sait meme pas qu'il y a eu une erreur, et
        // le SAV n'a rien a lire.
        //
        // Ce n'est pas une precaution theorique. Le 22/06, une couleur XAML invalide a fait
        // planter cette app en boucle au demarrage, et la seule trace etait un code .NET dans
        // l'observateur d'evenements. Rebelote le 02/09 en refaisant l'ecran de scan : build
        // vert, tests verts, application morte a la seconde -- une ressource au mauvais nom.
        // **Deux fois le meme silence.**
        //
        // Un produit vendu ne peut pas se comporter comme ca : le premier client qui le vit
        // n'a rien a raconter au support, et le support n'a rien a lui demander.
        DispatcherUnhandledException += (_, args) =>
        {
            // Handled = true avant d'afficher quoi que ce soit : si le rapport lui-meme
            // echouait, on ne veut pas remplacer un crash par un autre.
            args.Handled = true;
            ReportFatal(args.Exception);
        };

        // Le second filet couvre ce que le premier ne voit pas : une exception levee hors du
        // fil d'interface (une tache de fond, un timer). Elle ne peut pas etre annulee -- le
        // processus s'arrete de toute facon -- mais elle sera ecrite.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception);

        // 06/10/2026 (J1b) : « --releve » = lancé à l'ouverture de session, sans fenêtre, pour le seul relevé des parties.
        // Le helper n'y est jamais lancé : aucune demande d'élévation ne surgit au démarrage de Windows.
        var releveDeFond = e.Args.Contains(DrDxt.App.Platform.ReleveAuDemarrage.Argument, StringComparer.OrdinalIgnoreCase);
        var collection = new ServiceCollection().AddDrDxtApp();
        if (releveDeFond)
            collection.AddSingleton<DrDxt.Core.IElevatedClient>(_ => DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer());
        _services = collection.BuildServiceProvider();
        if (releveDeFond)
        {
            ReleverEnFond(_services);
            return;
        }

#if DEBUG
        // 03/10/2026 (D4.3) : « vitrine-sphere » ouvre une fenêtre qui ne montre que la sphère de marque, rendue par la
        // carte graphique : le chrome liquide est un shader ps_3_0 que RenderTargetBitmap (rendu logiciel) ne dessine pas,
        // donc l'icône Windows se photographie sur cette fenêtre-ci.
        if (e.Args.Any(a => string.Equals(a, "vitrine-sphere", StringComparison.OrdinalIgnoreCase)))
        {
            Effets.MoteurDAnimation.ForcePourCapture = true;
            var vitrine = new Window
            {
                Title = "DrDXT vitrine", Width = 300, Height = 300, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x06, 0x07, 0x0A)), WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new Control { Template = (ControlTemplate)FindResource("SphereDeMarque"), Margin = new Thickness(22) },
            };
            Effets.MoteurDAnimation.Brancher(vitrine);
            vitrine.Show();
            return;
        }
        if (e.Args.Any(a => string.Equals(a, DashboardPreviewArg, StringComparison.OrdinalIgnoreCase)))
        {
            ShowDashboardPreview(e.Args);
            return;
        }

#endif

#if DEBUG
        // « hors-ecran » : la fenêtre s'ouvre hors de tout moniteur, sans prendre le focus ni la
        // barre des tâches, pour être photographiée par son handle pendant que l'utilisateur
        // travaille. Le 23/09, une capture de l'écran a pris ce qui était devant la fenêtre.
        var horsEcran = e.Args.Any(a => string.Equals(a, "hors-ecran", StringComparison.OrdinalIgnoreCase));
        if (horsEcran) Effets.MoteurDAnimation.ForcePourCapture = true;
#endif
        var window = new MainWindow();
#if DEBUG
        if (horsEcran) PreparerHorsEcran(window, e.Args);
#endif

        // M5 — the shell hosts whatever the navigation service points at, mapping each
        // target view-model onto its UserControl. Scan ⇄ dashboard switching flows through
        // INavigationService so the view-models never touch WPF.
        var navigation = _services.GetRequiredService<INavigationService>();
        navigation.CurrentViewModelChanged += (_, target) => window.Content = ViewFor(target);

        var scan = _services.GetRequiredService<ScanViewModel>();
        window.Content = new ScanView { DataContext = scan };
        window.Show();

        // Fill the idle "will examine" component list off the UI thread (fire-and-forget).
        _ = scan.LoadIdleComponentsAsync();

        // 19/09/2026 — les 4,5 s d'initialisation de perflib se paient une fois par processus.
        // Elles se payaient pendant le SCAN ; elles se paient maintenant ici, pendant que la
        // fenetre s'ouvre et que personne n'attend rien. Voir AmorceDesCompteurs.
        _ = DrDxt.App.Platform.AmorceDesCompteurs.AuLancementAsync();

        // 14/09/2026 — la V1 relit au lancement les reglages que DrDXT a poses, et dit ce qui a bouge.
        _ = DrDxt.App.Platform.VerificationDesReglages.AuLancementAsync(_services, scan);

        // L8 a — une session tuee a peut-etre laisse un levier d'essai pose : le rendre, et le dire.
        _ = RendreSessionInterrompueAsync(_services, scan);

        // 25/09/2026 — la protection du ping que le joueur avait allumee se rallume ici.
        scan.MemoirePlafond = DrDxt.Application.Network.PlafondEnvoiMemorise.ParDefaut();
        // 28/09/2026 : service de demarrage en marche = c'est lui qui porte la protection du ping (un seul poseur).
        scan.ServiceDemarrageActif = () => new DrDxt.App.Platform.Win32ServiceStateReader()
            .GetServiceState("DrDxtReconciler") is { IsOk: true, Value: DrDxt.Application.Tweaks.Hardware.ServiceState.Running };
        // Et son plafond suit le ping pendant la partie (releve passif, le meme que le scan).
        var collecteur = new DrDxt.Measurement.Network.CollecteurEnvoi(
            new DrDxt.Measurement.Network.SystemPingProbe(),
            DrDxt.Detection.Network.AttributionReseauService.ReferenceInternetParDefaut);
        scan.CollecteurFenetre = (duree, ct) => collecteur.CollecterAsync(duree, TimeSpan.FromMilliseconds(250), ct);
        scan.JournalPlafond = _services.GetService(typeof(DrDxt.Core.IDiagnosticsLog)) as DrDxt.Core.IDiagnosticsLog;
        scan.HelperSansLancer = DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer();
        scan.ServicePorteUnRetenu = () => DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut().Lire() is not null;
        _ = scan.RallumerPlafondEnvoiAsync();
        // 27/09/2026 : et la veille « Surveiller mes parties », si le joueur l'avait laissee allumee.
        scan.MemoireVeille = DrDxt.Application.Network.VeillePingMemorisee.ParDefaut();
        scan.RallumerVeillePing();
        // 03/10/2026 (P3.2b) : le choix « programmes gourmands après le jeu », gardé ; rien d'abaissé ne survit à DrDXT.
        scan.MemoireCalmer = new DrDxt.Application.Network.VeillePingMemorisee(
            DrDxt.Core.FichierJsonAtomique.CheminLocal("calmer-voisins.json"));
        // 07/10/2026 (L-VOISINS) : les programmes que le joueur exclut du calmeur, lus avant de le rallumer.
        scan.FichierExclus = new DrDxt.Core.FichierJsonAtomique(DrDxt.Core.FichierJsonAtomique.CheminLocal("voisins-exclus.json"), "VOISINS_EXCLUS");
        scan.ChargerExclus();
        scan.RallumerCalmerVoisins();
        Exit += (_, _) => scan.RendreLesPriorites();
        // 29/09/2026 (A3) : les parties relevées, et le relevé relancé si le joueur l'avait laissé allumé.
        scan.MemoireParties = DrDxt.Application.Parties.PartiesMemorisees.ParDefaut();
        // 04/10/2026 : avant ChargerParties, sinon la carte se construit sans les changements (vu sur la version installée).
        scan.JournalChangements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut();
        BrancherReglagesSuivis(scan, _services);
        scan.MesurerAvantPartie = ct => DrDxt.App.Platform.MesureAvantPartie.MesurerAsync(
            new DrDxt.App.Platform.VoisinsWin32(), ct);
        scan.LireIncidents = () =>
        {
            var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Since(DateTimeOffset.Now.AddDays(-30));
            return (arrets.IsOk ? arrets.Value! : [],
                (DrDxt.Detection.Repair.ErreursMaterielles.Lire(30) ?? []).Where(e => e.Gravite is 0 or 1 or 2).Select(e => e.Quand).ToList());
        };
        scan.LireChangementsDepuisLeScan = () => DrDxt.Detection.Repair.InventaireMachine.Apercu(
            DrDxt.Core.DossierDonnees.Chemin("inventaire-machine.json"), DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil)
            .Select(c => c.Explication).ToList();
        scan.LireMisesAJourDeLaMachine = () =>
            [.. DrDxt.Detection.Repair.AvantLIncident.LirePilotes(), .. DrDxt.Detection.Repair.MisesAJourWindows.Lire(),
             .. DrDxt.Detection.Repair.InstallationDeWindows.Fait()];
        // Revue F-40 : lues dès l'ouverture, pour que le premier affichage borne déjà les bilans.
        _ = scan.MisesAJourAsync();
        scan.LevierParId = id => _services!.GetRequiredService<DrDxt.Core.ITweakCatalog>().Get(id) is { IsOk: true } t ? t.Value : null;
        // Revue F-23 : jamais d'invite UAC sans geste ; le retrait automatique ne part que si le helper tourne déjà.
        scan.HelperJoignableSansUac = async () =>
        {
            try { return (await DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer().ExecuteAsync(new DrDxt.Core.PingOp(), CancellationToken.None)).IsOk; }
            catch (Exception) { return false; }
        };
        scan.RetirerDuManifeste = id => DrDxt.App.Platform.RetraitDeLevier.ApresAnnulationAsync(
            _services!.GetRequiredService<DrDxt.Core.IDesiredStateStore>(),
            new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut()), id);
        scan.InscrireAuDemarrage = DrDxt.App.Platform.ReleveAuDemarrage.Regler;
        scan.PrendreVerrouReleve = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.VerrouDuReleve.CheminParDefaut());
        scan.ChargerParties();
        scan.ChargerLeviersPoses();
        // 07/10/2026 (J5b) : les bascules se relèvent aussi sans scan ; le retrait part d'ici, l'écran ouvert.
        _ = scan.ReleverReglagesHorsScanAsync();
        // 30/09/2026 (G3) : une preuve du repos de la carte commencée avant un redémarrage se conclut au lancement ;
        // (G4) le réglage gardé reste ensuite jugé par les parties relevées.
        scan.LevierReposCarte = new DrDxt.Application.Tweaks.Latency.ReposRenduALaCarteTweak(
            (DrDxt.Application.Tweaks.Hardware.INvApi)_services.GetService(typeof(DrDxt.Application.Tweaks.Hardware.INvApi))!,
            (DrDxt.Core.IElevatedClient)_services.GetService(typeof(DrDxt.Core.IElevatedClient))!);
        scan.ReglagesDeLaCarte = DrDxt.Application.Tweaks.ReglagesRetenus.ParDefaut();
        scan.SondeCarte = new DrDxt.Detection.NvmlHardwareProbe();
        scan.AttenteReposCarte = DrDxt.Application.Tweaks.PreuvesEnAttente.ParDefaut();
        _ = scan.ConclureReposCarteAsync(scan.LevierReposCarte, scan.SondeCarte, scan.AttenteReposCarte, t => Task.Delay(t));

        // SEC3 (08/10/2026) : la page Windows dit ce que le service a réaffirmé seul (lecture seule de son état).
        scan.LireEtatDuService = () =>
            new DrDxt.Reconciler.JsonReconcilerStateStore(DrDxt.Reconciler.JsonReconcilerStateStore.DefaultPath())
                .LoadAsync(CancellationToken.None).Result is { IsOk: true } lu ? lu.Value : null;

        // ROAD1 (08/10/2026) : la liste « ce que DrDXT fait ici » de chaque page lit le vrai catalogue.
        if (_services.GetService<DrDxt.Core.ITweakCatalog>() is { } catalogueLeviers)
            scan.CatalogueDesLeviers = catalogueLeviers.All;
        scan.LeviersVerifiesIci = hw => DrDxt.Application.Tweaks.LeviersVerifies.Lire(DrDxt.Core.DossierDonnees.Chemin("leviers-verifies.json"),
            DrDxt.Application.Tweaks.LeviersVerifies.Empreinte(hw));
        // GARDE2 (09/10/2026) : la garde d'incident lit aussi les vidages à chaud du pilote graphique.
        scan.LireVidagesCarte = () => DrDxt.Detection.Hardware.StabiliteLue.VidagesCarte(new DrDxt.Detection.Hardware.WerLiveKernelReportLog());

        // GPU-OC (08/10/2026) : le mode Expert de la carte (page Graphique). Même journal que l'autopilote : le démarrage
        // suivant rend la carte si DrDXT est tué ; l'écran refuse de poser pendant une partie.
        if (_services.GetServices<DrDxt.Core.ITweak>().OfType<DrDxt.Application.Tweaks.Gpu.GpuUndervoltTweak>().FirstOrDefault() is { } levierCarte
            && _services.GetService<DrDxt.Application.Tweaks.Gpu.IGpuTuningTargetSource>() is DrDxt.Application.Tweaks.Gpu.GpuTuningTargetSource cibles
            && _services.GetService<DrDxt.Application.Autopilot.IRevertEnAttente>() is { } journalCarte)
        {
            var expert = new DrDxt.Application.Tweaks.Gpu.ReglageCarteExpert(levierCarte, cibles, journalCarte);
            scan.PoserCarteExpert = async mhz =>
            {
                var r = await expert.PoserAsync(mhz);
                return r.IsOk
                    ? DrDxt.Core.Result<DrDxt.Application.Tweaks.Gpu.IEssaiCarte>.Ok(r.Value!)
                    : DrDxt.Core.Result<DrDxt.Application.Tweaks.Gpu.IEssaiCarte>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
            };
            // GPU-OC (08/10/2026) : le ventilateur NVIDIA, même journal, son propre bail ; le helper ne pose que vers le haut.
            if (_services.GetService<DrDxt.Core.IElevatedClient>() is { } eleve)
            {
                var ventilateur = new DrDxt.Application.Tweaks.Gpu.VentilateurExpert(eleve, journalCarte);
                scan.PoserVentilateurExpert = pct => ventilateur.PoserAsync(pct);
            }
        }

        // 27/09/2026 : une référence mémoire ou processeur prise pendant une partie se remesure seule quand le
        // poste est au repos (RemesureAuRepos). Rien ne se passe tant qu'aucune n'est soupçonnée. Les commandes
        // appelées disent elles-mêmes leurs échecs sur la carte ; une exception ici ne doit pas tuer la minuterie.
        scan.LirePoste = DrDxt.App.Platform.EtatDuPosteWindows.LireAsync;
        scan.CorruptionSansSource = DrDxt.App.Platform.CorruptionDuMagasin.SurCetteMachine;
        var remesureAuRepos = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        remesureAuRepos.Tick += async (_, _) =>
        {
            try { await scan.TenterRemesureAuReposAsync(DateTimeOffset.Now, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        };
        remesureAuRepos.Start();

        // 03/10/2026 (D6.2) : relancée en administrateur par l'analyse, l'appli reprend la scène choisie et analyse.
        if (e.Args.Any(a => string.Equals(a, "--analyser", StringComparison.OrdinalIgnoreCase)))
        {
            var sceneArg = e.Args.FirstOrDefault(a => a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase));
            if (sceneArg is not null && int.TryParse(sceneArg["--scene=".Length..], out var indice)
                && indice >= 0 && indice < scan.ChoixDeScene.Count)
                scan.SceneDeclaree = scan.ChoixDeScene[indice];
            window.ContentRendered += (_, _) => scan.ScanCommand.Execute(null);
        }

#if DEBUG
        if (e.Args.Any(a => string.Equals(a, ScanAutoArg, StringComparison.OrdinalIgnoreCase)))
        {
            // « xmp-off » : le conseiller BIOS voit un XMP coupé, pour photographier le guide que
            // la machine de reference (XMP actif) n'affiche jamais. Le registre et le reste du scan lisent
            // la vraie machine.
            ScanViewModel.SimulerXmpOffPourLeBios =
                e.Args.Any(a => string.Equals(a, "xmp-off", StringComparison.OrdinalIgnoreCase));
            window.ContentRendered += (_, _) => scan.ScanCommand.Execute(null);
            VisiterLeResultat(window, scan);
            SondeDeGel(scan);
        }
        // 08/10/2026 (ADM2, « tu es admin ») : « expert-auto » prouve le mode Expert sur la vraie carte par le chemin du
        // produit (App, helper, bail) : ventilateur fixé à 60 % puis rendu, décalage d'horloge +15 MHz puis rendu, chaque
        // étape relue (NVML pour le ventilateur, NVAPI pour l'horloge) et écrite dans %TEMP%\drdxt-expert.txt.
        if (e.Args.Any(a => string.Equals(a, "expert-auto", StringComparison.OrdinalIgnoreCase)))
        {
            window.ContentRendered += async (_, _) =>
            {
                var journal = new System.Text.StringBuilder();
                var nvml = new DrDxt.GpuNative.Nvml.NvmlNative();
                string Ventilo() => nvml.TryLireVentilateurs(0, out var v)
                    ? string.Join(" ; ", v.Select(f => $"ventilateur {f.Index} : {f.VitessePct} %, cible {f.CiblePct} %, {(f.Automatique ? "automatique" : "manuel")}"))
                    : "ventilateur illisible";
                var horloge = _services?.GetService<DrDxt.Application.Tweaks.Hardware.INvApiGpuTuning>();
                string Decalage() => horloge?.GetCoreClockOffsetMhz(0) is { IsOk: true } d ? $"décalage cœur {d.Value} MHz" : "décalage illisible";
                try
                {
                    journal.AppendLine($"avant          : {Ventilo()} ; {Decalage()}");
                    if (scan.PoserVentilateurExpert is { } ventilateur)
                    {
                        var r = await ventilateur(60);
                        journal.AppendLine($"ventilo posé   : {(r.IsOk ? "oui" : "non, " + r.Error?.Message)} ; {Ventilo()}");
                        if (r.IsOk)
                        {
                            await Task.Delay(3000);
                            journal.AppendLine($"après 3 s      : {Ventilo()}");
                            var rendu = await r.Value!.RendreAsync();
                            await r.Value.DisposeAsync();
                            journal.AppendLine($"ventilo rendu  : {(rendu.IsOk ? "oui" : "non, " + rendu.Error?.Message)} ; {Ventilo()}");
                        }
                    }
                    if (scan.PoserCarteExpert is { } carte)
                    {
                        var r = await carte(15);
                        journal.AppendLine($"horloge posée  : {(r.IsOk ? "oui" : "non, " + r.Error?.Message)} ; {Decalage()}");
                        if (r.IsOk)
                        {
                            var rendu = await r.Value!.RendreAsync();
                            await r.Value.DisposeAsync();
                            journal.AppendLine($"horloge rendue : {(rendu.IsOk ? "oui" : "non, " + rendu.Error?.Message)} ; {Decalage()}");
                        }
                    }
                    journal.AppendLine($"fin            : {Ventilo()} ; {Decalage()}");
                }
                catch (Exception ex) { journal.AppendLine("erreur : " + ex.Message); }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "drdxt-expert.txt"), journal.ToString());
                Current.Shutdown();
            };
        }
        // 08/10/2026 (GPU-OC, plantage simulé) : « expert-crash » fixe le ventilateur à 60 % puis tue l'App sans rien rendre ;
        // le bail du helper doit rendre l'automatique seul (relevé de l'extérieur par NVML). Avec « horloge » : décalage +15 MHz
        // au lieu du ventilateur, rendu par le même bail (relevé de l'extérieur par NVAPI).
        if (e.Args.Any(a => string.Equals(a, "expert-crash", StringComparison.OrdinalIgnoreCase)))
        {
            var horloge = e.Args.Any(a => string.Equals(a, "horloge", StringComparison.OrdinalIgnoreCase));
            window.ContentRendered += async (_, _) =>
            {
                if (horloge
                        ? scan.PoserCarteExpert is { } carte && (await carte(15)).IsOk
                        : scan.PoserVentilateurExpert is { } ventilateur && (await ventilateur(60)).IsOk)
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "drdxt-expert-crash.txt"), DateTimeOffset.Now.ToString("HH:mm:ss"));
                Environment.FailFast("plantage simulé (expert-crash)");
            };
        }
        // 08/10/2026 (ADM1, « tu es admin ») : « verifier-auto » lance « Vérifier DrDXT sur mon PC » par le même chemin que le
        // bouton, écrit le bilan dans %TEMP%\drdxt-verification.txt et ferme l'App. Compilation de mise au point seulement.
        if (e.Args.Any(a => string.Equals(a, "verifier-auto", StringComparison.OrdinalIgnoreCase)))
        {
            window.ContentRendered += async (_, _) =>
            {
                await scan.ConfirmerVerificationCommand.ExecuteAsync(null);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "drdxt-verification.txt"),
                    scan.VerificationBilan + Environment.NewLine + scan.VerificationChemin);
                Current.Shutdown();
            };
        }
#endif

        // Phase 18 — la mise a jour, verifiee pour de vrai (sous delai borne, hors fil d'interface).
        _ = VerifierMisesAJourAsync(scan);
    }

    private static async Task VerifierMisesAJourAsync(ScanViewModel scan)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        using var client = new System.Net.Http.HttpClient();
        var etat = await Task.Run(() => DrDxt.App.Updates.VerificationMiseAJour.VerifierAsync(
            () => DrDxt.App.Updates.VerificationMiseAJour.Monter(
                DrDxt.App.Updates.VerificationMiseAJour.UrlEffective(Environment.GetEnvironmentVariable(DrDxt.App.Updates.VerificationMiseAJour.VariableUrl)), client, version),
            DrDxt.App.Updates.VerificationMiseAJour.DelaiParDefaut, CancellationToken.None));
        scan.AfficherMiseAJour(etat);
    }

    private static async Task RendreSessionInterrompueAsync(IServiceProvider services, ScanViewModel scan)
    {
        string? phrase;
        try
        {
            var journal = services.GetRequiredService<DrDxt.Application.Autopilot.IRevertEnAttente>();
            var catalogue = services.GetRequiredService<DrDxt.Core.ITweakCatalog>();
            // Hors du fil d'interface : le revert passe par le canal privilegie.
            phrase = await Task.Run(() => DrDxt.Application.Autopilot.RevertAuDemarrage.Executer(journal, catalogue));
        }
        catch (Exception ex)
        {
            phrase = $"La vérification de la dernière session a échoué ({ex.Message}) : un réglage d'essai "
                     + "est peut-être encore en place.";
        }
        if (phrase is not null) scan.MessageDemarrage = phrase;
    }

    /// <summary>
    /// Ecrit la panne, puis la MONTRE. Dans cet ordre : si l'affichage echoue a son tour (pas de
    /// bureau, session en fermeture), la trace est deja sur le disque.
    ///
    /// Total par construction — chaque moitie est isolee. Un filet de securite qui peut lui-meme
    /// lever ne protege de rien, et rate precisement le cas ou tout va deja mal.
    /// </summary>
    /// <summary>06/10/2026 (J1b) : le relevé des parties DrDXT fermé. Aucune fenêtre ; le même relevé que l'App (même code,
    /// même fichier), sous le même verrou, si bien que l'App ouverte et lui ne relèvent jamais deux fois la même partie.
    /// Le joueur l'arrête par le bouton du relevé : le choix « éteint » est relu toutes les trente secondes.</summary>
    private void ReleverEnFond(ServiceProvider services)
    {
        var memoire = DrDxt.Application.Parties.PartiesMemorisees.ParDefaut();
        if (!memoire.EstAllume())
        {
            Shutdown();
            return;
        }
        var scan = services.GetRequiredService<ScanViewModel>();
        scan.MemoireParties = memoire;
        scan.PrendreVerrouReleve = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.VerrouDuReleve.CheminParDefaut());
        // 07/10/2026 (J5b) : les bascules d'après le redémarrage se notent et se jugent ici, sans scan. LevierParId n'est pas
        // posé : rien ne se retire sans fenêtre (voir ReleverReglagesHorsScanAsync).
        scan.JournalChangements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut();
        BrancherReglagesSuivis(scan, services);
        scan.ChargerParties();
        _ = scan.ReleverReglagesHorsScanAsync();
        var relecture = new System.Windows.Threading.DispatcherTimer { Interval = DrDxt.Application.Parties.VeilleDesParties.HorsPartie };
        relecture.Tick += (_, _) => { if (!memoire.EstAllume()) Shutdown(); };
        relecture.Start();
    }

    /// <summary>07/10/2026 (J5b) : le suivi des réglages et leurs lectures sans scan, les mêmes pour l'App et le relevé de fond.</summary>
    private static void BrancherReglagesSuivis(ScanViewModel scan, IServiceProvider services)
    {
        // 07/10/2026 (J4) : l'essai en alternance, lu par l'App et par le relevé de fond (qui marque les parties ; seul l'App
        // pose ou rend le levier, quand son helper tourne déjà).
        scan.MemoireEssai = DrDxt.Application.Parties.MemoireDeLEssai.ParDefaut();
        scan.SuiviReglages = DrDxt.Application.Parties.SuiviDesReglages.ParDefaut();
        scan.LireInstantReplay = () => DrDxt.Detection.Gpu.EnregistrementNvidia.EtatInstantReplay(DrDxt.Detection.Gpu.EnregistrementNvidia.LireReglages());
        scan.LireReglagesSuivis = () => ScanViewModel.EtatsSuivisSansScan(services.GetRequiredService<DrDxt.Detection.Settings.IRegistryProvider>(),
            services.GetService<DrDxt.Detection.Settings.IHagsEffectiveReader>(), scan.LireInstantReplay());
    }

    private static void ReportFatal(Exception? ex)
    {
        var detail = ex?.ToString() ?? "Exception inconnue (aucun objet d'exception fourni).";

        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Ajout, jamais ecrasement : deux plantages de suite racontent une histoire que le
            // second seul ne raconte pas.
            // BOM UTF-8 explicite. Sans elle, .NET ecrit de l'UTF-8 nu et les lecteurs qui
            // devinent -- PowerShell 5.1 en tete, l'outil le plus probable d'un depannage --
            // rendent « levée » en « levÃ©e ». Un rapport de panne illisible est un rapport de
            // panne perdu, et c'est le SAV qui paie. Constate en verifiant ce filet.
            File.AppendAllText(path,
                $"=== {DateTimeOffset.Now:u} ==={Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // Disque plein, droits refuses : on ne peut pas ecrire. Ce n'est pas une raison de
            // priver l'utilisateur du message ci-dessous.
        }

        try
        {
            // 19/09/2026 — ce message etait ecrit en dur, en francais, pendant que la seule
            // autre boite de dialogue du produit l'etait en ANGLAIS. Quelle que soit la langue
            // du poste, l'un des deux tombait a cote. La regle — « human-readable text resolves
            // from a resx KEY via ILocalizer, never hardcoded » — etait deja ecrite ailleurs.
            //
            // Ici, le localisateur est construit a la main, pas injecte. Ce filet tourne
            // quand tout le reste a deja echoue : demander un service au conteneur, c'est
            // dependre de la chose qui vient peut-etre de casser. `ResxLocalizer` ne lit qu'un
            // jeu de ressources embarque, et sa clef manquante rend la clef — jamais une
            // exception. Un filet de securite qui peut lever ne protege de rien.
            var textes = new ResxLocalizer();
            MessageBox.Show(
                string.Format(CultureInfo.CurrentUICulture, textes.Get("Dialog.Crash.Message"), CrashLogPath),
                textes.Get("Dialog.Crash.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Aucune interface disponible : la trace disque reste, et c'est le but.
        }
    }

    // Maps a navigation target view-model onto its hosting control. Kept tiny and explicit:
    // a real navigation framework is overkill for two screens.
    private static UserControl? ViewFor(object? target) => target switch
    {
        ScanViewModel scan => new ScanView { DataContext = scan },
        DashboardViewModel dashboard => new DashboardView { DataContext = dashboard },
        _ => null,
    };

#if DEBUG
    // Une fois le résultat affiché, défile page par page et dépose un signal par page
    // (%TEMP%\drdxt-visite-N.txt, puis drdxt-visite-fin.txt) : le script de capture photographie
    // la fenêtre à chaque signal.
    /// <summary>
    /// Sonde de gel (DEBUG) — ce que l'oeil voit, chronometre.
    ///
    /// Un battement de 50 ms sur le fil d'interface. S'il revient en retard, c'est que le fil
    /// etait occupe ailleurs : le retard est la duree pendant laquelle rien ne pouvait bouger a
    /// l'ecran. On note l'etat du scan au meme instant, pour savoir a quelle phase l'attribuer.
    /// Rien n'est affiche : la sonde ecrit dans %TEMP% et ne change pas ce qu'elle mesure.
    /// </summary>
    private static void SondeDeGel(ScanViewModel scan)
    {
        var chemin = Path.Combine(Path.GetTempPath(), "drdxt-gel.txt");
        File.WriteAllText(chemin, $"battement 50 ms, demarre a {DateTime.Now:HH:mm:ss.fff}\n");
        var depart = DateTime.UtcNow;
        var attendu = TimeSpan.FromMilliseconds(50);
        var dernier = DateTime.UtcNow;
        var battement = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render) { Interval = attendu };
        battement.Tick += (_, _) =>
        {
            var maintenant = DateTime.UtcNow;
            var retard = maintenant - dernier - attendu;
            dernier = maintenant;
            if (retard.TotalMilliseconds >= 120)
                File.AppendAllText(chemin, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "t+{0,7:F0} ms : fil d'interface bloqué {1,6:F0} ms (état : {2})\n",
                    (maintenant - depart).TotalMilliseconds, retard.TotalMilliseconds, scan.State));
        };
        battement.Start();

        // Temoin : un fichier sans ligne « bloque » ne prouve rien si le scan n'a pas tourne. Chaque
        // changement d'etat du scan s'inscrit ici, sur la meme horloge (23/09/2026).
        scan.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(ScanViewModel.State))
                File.AppendAllText(chemin, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "t+{0,7:F0} ms : état du scan -> {1}\n", (DateTime.UtcNow - depart).TotalMilliseconds, scan.State));
        };
    }

    private static void VisiterLeResultat(Window window, ScanViewModel scan)
    {
        var dossier = Path.GetTempPath();
        foreach (var f in Directory.EnumerateFiles(dossier, "drdxt-visite-*.txt")) File.Delete(f);
        var commence = false;
        scan.PropertyChanged += (_, ev) =>
        {
            if (commence || ev.PropertyName != nameof(ScanViewModel.State)
                || scan.State is ScanState.Idle or ScanState.Scanning) return;
            commence = true;
            // « ping-disponible » : simule ce qu'une mesure EN JEU aurait retenu (plafond conseillé,
            // exe du jeu) pour photographier l'état « disponible » de la carte réseau, qu'un scan hors
            // jeu ne montre jamais. Même point d'entrée que le vrai scan ; rien n'est écrit.
            if (Environment.GetCommandLineArgs().Any(a => a == "ping-disponible"))
                scan.RetenirMesureEnvoi(20_000_000, "cod.exe");
            var page = 0;
            var minuterie = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            minuterie.Tick += (_, _) =>
            {
                var defileur = Descendant<System.Windows.Controls.ScrollViewer>(window);
                if (defileur is null) return;
                if (page > 0 && defileur.VerticalOffset >= defileur.ScrollableHeight - 1)
                {
                    minuterie.Stop();
                    PhotographierLesDomaines(window, scan, dossier);
                    return;
                }
                if (page > 0) defileur.ScrollToVerticalOffset(defileur.VerticalOffset + defileur.ViewportHeight - 60);
                page++;
                var numero = page;
                window.Dispatcher.BeginInvoke(() =>
                {
                    File.WriteAllText(Path.Combine(dossier, $"drdxt-visite-{numero}.txt"), numero.ToString());
                    // 03/10/2026 : la page elle-même, photographiée par l'appli (hors écran, rien n'apparaît sur l'écran).
                    PhotographierVue(window, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"page-{numero}.png"));
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            minuterie.Start();
        };
    }

    // Ouvre chaque domaine de la carte, l'amène à l'écran et dépose un signal par domaine.
    private static async void PhotographierLesDomaines(Window window, ScanViewModel scan, string dossier)
    {
        // 25/09/2026 — « remesurer-processeur » : la même commande que la carte « Remesurer ton processeur »,
        // avant les photos et le rapport, pour juger la phrase qu'elle produit sur une mesure fraîche.
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "remesurer-processeur", StringComparison.OrdinalIgnoreCase)))
            await scan.RemesurerProcesseurCommand.ExecuteAsync(null);
        var index = 0;
        var minuterie = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        minuterie.Tick += async (_, _) =>
        {
            if (index >= scan.Domaines.Count)
            {
                File.WriteAllText(Path.Combine(dossier, "drdxt-visite-fin.txt"), "fin");
                minuterie.Stop();
                // 25/09/2026 — « rapport » : le même export que le bouton, pour juger le document réel
                // (sections, anonymisation) sur un scan réel, sans personne devant l'écran.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "rapport", StringComparison.OrdinalIgnoreCase)))
                    scan.ExportReportCommand.Execute(null);
                // 08/10/2026 (CONTRIB2) : « exporter », le même fichier que « Exporter ma machine », pour le ranger en fixture.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "exporter", StringComparison.OrdinalIgnoreCase)))
                    await scan.ExporterMaMachineCommand.ExecuteAsync(null);
                // Hors écran (le seul cas où ce drapeau est levé), personne ne regarde la fenêtre :
                // rester ouvert faisait attendre le lanceur jusqu'à son délai, ~5 min par tournée.
                if (Effets.MoteurDAnimation.ForcePourCapture) Current.Shutdown();
                return;
            }
            var domaine = scan.Domaines[index++];
            scan.DomaineSelectionne = domaine;
            // 03/10/2026 : la capsule du rail glisse 280 ms jusqu'au domaine choisi ; photographier tout de suite la
            // prenait à mi-chemin, et l'audit croyait la surbrillance décalée. On attend la fin du glissement.
            var apresGlissement = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            apresGlissement.Tick += (_, _) => { apresGlissement.Stop(); Photographier(); };
            apresGlissement.Start();
            void Photographier() => window.Dispatcher.BeginInvoke(() =>
            {
                // 08/10/2026 (ROAD1) : « deplier » ouvre les listes repliées (« Ce que DrDXT fait ici », mode Expert) pour les
                // photographier ouvertes.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "deplier", StringComparison.OrdinalIgnoreCase)))
                    DeplierTout(window);
                if (DescendantNomme(window, "CarteMachine") is FrameworkElement carte)
                    carte.BringIntoView(new Rect(0, 0, carte.ActualWidth, 40));
                window.Dispatcher.BeginInvoke(() =>
                {
                    File.WriteAllText(Path.Combine(dossier, $"drdxt-visite-domaine-{domaine.Cle}.txt"), domaine.Cle);
                    // 24/09/2026 — une photo par domaine : les 40 photos du minuteur s'arretent avant la
                    // carte, et une interface se juge sur un scan reel, domaine par domaine.
                    PhotographierVue(window, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"domaine-{domaine.Cle}.png"));
                    // Et le BAS du panneau : un domaine long (windows) cache sa section « Agir » sous le pli.
                    if (DescendantNomme(window, "CarteMachine") is FrameworkElement c)
                    {
                        c.BringIntoView(new Rect(0, Math.Max(0, c.ActualHeight - 40), c.ActualWidth, 40));
                        window.Dispatcher.BeginInvoke(() =>
                        {
                            PhotographierVue(window, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"domaine-{domaine.Cle}-bas.png"));
                            // Et la carte entière : le milieu d'un domaine long (les constats de sécurité de
                            // la page Windows) ne tombait sur aucune des deux photos.
                            PhotographierElement(c, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"domaine-{domaine.Cle}-entier.png"));
                        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        minuterie.Start();
    }

    private static void PhotographierVue(Window window, string chemin)
    {
        if (window.Content is not FrameworkElement vue || vue.ActualWidth < 1) return;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var rendu = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)vue.ActualWidth, (int)vue.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendu.Render(vue);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendu));
        using var f = File.Create(chemin);
        png.Save(f);
    }

    // Un élément sur toute sa hauteur, même la partie hors de la fenêtre, sur le fond de la fenêtre :
    // la carte seule est transparente, et son texte clair serait illisible sur un PNG sans fond.
    private static void PhotographierElement(FrameworkElement element, string chemin)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return;
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var fond = Window.GetWindow(element)?.Background ?? System.Windows.Media.Brushes.Black;
        var dessin = new System.Windows.Media.DrawingVisual();
        using (var dc = dessin.RenderOpen())
        {
            var cadre = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            dc.DrawRectangle(fond, null, cadre);
            // La zone copiée est la carte elle-même, en coordonnées absolues : sans cela, le pinceau étirait
            // les limites VISUELLES du contenu sur le cadre (photo du BIOS écrasée, texte illisible).
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(element)
            {
                Viewbox = cadre,
                ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
            }, null, cadre);
        }
        var rendu = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendu.Render(dessin);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendu));
        using var f = File.Create(chemin);
        png.Save(f);
    }

    private static DependencyObject? DescendantNomme(DependencyObject racine, string nom)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(racine); i++)
        {
            var enfant = System.Windows.Media.VisualTreeHelper.GetChild(racine, i);
            if (enfant is FrameworkElement { Name: var n } && n == nom) return enfant;
            if (DescendantNomme(enfant, nom) is { } plusBas) return plusBas;
        }
        return null;
    }

    private static T? Descendant<T>(DependencyObject racine) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(racine); i++)
        {
            var enfant = System.Windows.Media.VisualTreeHelper.GetChild(racine, i);
            if (enfant is T trouve) return trouve;
            if (Descendant<T>(enfant) is { } plusBas) return plusBas;
        }
        return null;
    }

    private static void DeplierTout(DependencyObject racine)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(racine); i++)
        {
            var enfant = System.Windows.Media.VisualTreeHelper.GetChild(racine, i);
            if (enfant is System.Windows.Controls.Expander repli) repli.IsExpanded = true;
            DeplierTout(enfant);
        }
    }

    // Builds the dashboard VM, loads the representative sample report and shows it in the
    // main window. DEBUG-only — used solely by the dashboard-preview startup arg (QA screenshots).
#if DEBUG
    // Préparation commune de toute fenêtre ouverte en « hors-ecran » (scan et aperçu du tableau de bord).
    private static void PreparerHorsEcran(Window window, string[] args)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        // Hors écran, Windows ne compose pas la fenêtre (PrintWindow rend du noir, mesuré) :
        // l'app dessine elle-même son arbre visuel, indépendamment de l'écran et de sa veille.
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-photos");
        Directory.CreateDirectory(dossier);
        var n = 0;
        var photographe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        photographe.Tick += (_, _) =>
        {
            if (window.Content is not FrameworkElement vue || vue.ActualWidth < 1) return;
            // 04/10/2026 (audit 5.2) : « tout » photographie le contenu entier de la zone qui défile, cartes sous le pli comprises.
            if (args.Any(a => string.Equals(a, "tout", StringComparison.OrdinalIgnoreCase))
                && Descendant<System.Windows.Controls.ScrollViewer>(vue)?.Content is FrameworkElement contenu && contenu.ActualHeight > 0)
                vue = contenu;
            var rendu = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)vue.ActualWidth, (int)vue.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rendu.Render(vue);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendu));
            // 04/10/2026 (audit 5.2) : « dernier » réécrit une seule photo jusqu'à la fermeture (un scan dure plus de 16 s).
            var dernier = args.Any(a => string.Equals(a, "dernier", StringComparison.OrdinalIgnoreCase));
            using (var f = File.Create(Path.Combine(dossier, dernier ? "dernier.png" : $"f{n:D2}.png"))) png.Save(f);
            if (++n >= 40 && !dernier) photographe.Stop();
        };
        // Seulement sur demande (« photos ») : chaque photo dessine tout l'arbre sur le fil
        // d'interface, 400 à 750 ms mesurés. Laissée allumée, elle se mesurait elle-même.
        if (args.Any(a => string.Equals(a, "photos", StringComparison.OrdinalIgnoreCase)))
            window.ContentRendered += (_, _) => photographe.Start();
    }
#endif

    private void ShowDashboardPreview(string[] args)
    {
        var vm = _services!.GetRequiredService<DashboardViewModel>();
        vm.Load(DashboardSample.MeasurableGain());

        var horsEcran = args.Any(a => string.Equals(a, "hors-ecran", StringComparison.OrdinalIgnoreCase));
        // Avant la fenêtre : c'est son constructeur qui branche la règle des animations.
        if (horsEcran) Effets.MoteurDAnimation.ForcePourCapture = true;
        var window = new MainWindow();
        if (horsEcran) PreparerHorsEcran(window, args);
        window.Content = new DashboardView { DataContext = vm };
        window.Show();
    }

#endif

    protected override void OnExit(ExitEventArgs e)
    {
        // GPU-OC (revue n° 13) : un décalage gardé est rendu à la fermeture, borné à 5 s ; sinon le bail le rend.
        if (_services?.GetService<ScanViewModel>() is { } scanFin)
            try { Task.Run(scanFin.FermerCarteExpertAsync).Wait(TimeSpan.FromSeconds(5)); } catch { /* le bail rendra */ }
        _services?.Dispose();
        base.OnExit(e);
    }
}
