using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DrDxt.App.ViewModels;
using DrDxt.App.Views;
using DrDxt.Infrastructure.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App;

// 'Application' est qualifié en System.Windows.Application : la référence au projet
// DrDxt.Application amène aussi un espace de noms 'DrDxt.Application', qui entrerait
// sinon en collision avec le type de base WPF ci-dessous.
/// <summary>
/// Le point d'entrée de DrDXT. Au lancement, il pose les filets qui écrivent tout plantage dans crash.log, ouvre
/// l'écran de scan et rallume ce que le joueur avait laissé allumé (protection du ping, relevé des parties,
/// programmes calmés). Lancé avec « --releve », il ne fait que le relevé des parties, sans fenêtre.
/// À la fermeture, il rend les réglages temporaires encore posés.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    // L'écran ouvert. ScanViewModel est transitoire : le redemander au conteneur en construit un neuf, qui ne sait
    // rien de l'essai expert ni de la recherche de sous-tension en cours.
    private ScanViewModel? _ecranOuvert;
    private InstanceUnique? _instance;

    private void MontrerLaFenetre()
    {
        if (MainWindow is not { } fenetre) return;
        if (fenetre.WindowState == WindowState.Minimized) fenetre.WindowState = WindowState.Normal;
        fenetre.Show();
        fenetre.Activate();
    }

#if DEBUG
    // « scan-auto » : le scan part tout seul, pour capturer l'écran de résultat sur la vraie machine.
    // Rien de ceci n'existe dans la compilation livrée.
    private const string ScanAutoArg = "scan-auto";
#endif

    /// <summary>
    /// Le fichier ou le produit ecrit ce qui l'a tue. A cote des donnees de l'app, pas dans
    /// %TEMP% : un client qu'on guide au telephone doit pouvoir le trouver, et il doit survivre
    /// a un nettoyage de disque.
    /// </summary>
    private static string CrashLogPath => DrDxt.Core.DossierDonnees.Chemin("crash.log");

    private DrDxt.App.Platform.DossierDeLAppEleve? _dossierTenu;

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        // « machine=amd-radeon-synthetique » rejoue cette machine enregistrée (avant la
        // construction des services, qui la lisent).
        // Les données vont dans un dossier jetable : un rejeu n'écrit jamais dans celles du joueur.
        if (e.Args.FirstOrDefault(a => a.StartsWith("machine=", StringComparison.OrdinalIgnoreCase)) is { } machine)
            DrDxt.App.Platform.MachineDeTest.Activer(machine["machine=".Length..], Environment.GetEnvironmentVariable,
                Environment.SetEnvironmentVariable);
#endif
        base.OnStartup(e);
        // Le docteur parle la langue de Windows, comme le reste de l'App (sans cette ligne, le français).
#if DEBUG
        // « --langue=en-US » : l'interface et les nombres dans une autre langue, pour les captures du README ; inconnue, ignorée.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--langue=", StringComparison.OrdinalIgnoreCase)) is { } langue)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(langue["--langue=".Length..]);
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = culture;
            }
            catch (CultureNotFoundException) { }
        }
#endif
        DrDxt.Core.Docteur.FixerLangue(CultureInfo.CurrentUICulture);

        // Élevée seulement, avant toute écriture : aucune jonction posée par un compte non administrateur n'est plus suivie,
        // ni sur les parents du dossier des données, ni sous un dossier créé après la vérification qui suit (le staging d'une
        // mise à jour). Sans élévation, le compte garde ses jonctions. Indisponible, c'est dit au journal plus bas.
        var confiance = DrDxt.Core.FichierJsonAtomique.ProcessusEleve ? DrDxt.Core.ConfianceDesRedirections.Activer() : DrDxt.Core.Result.Ok();

        // Relancée en administrateur, l'App écrit sous un dossier que tout programme du compte peut modifier : avant toute
        // écriture, il est vérifié sans suivre les liens et tenu (DossierDeLAppEleve). Un lien trouvé : elle ne démarre pas.
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                _dossierTenu = DrDxt.App.Platform.DossierDeLAppEleve.Verrouiller(DrDxt.Core.DossierDonnees.Racine(), out var lien);
                if (_dossierTenu is null)
                {
                    var textes = new ResxLocalizer();
                    MessageBox.Show(string.Format(CultureInfo.CurrentUICulture, textes.Get("Dialog.DossierAvecLien.Message"), lien),
                        textes.Get("Dialog.Crash.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
            }

        // ── Le filet, et pourquoi il existe ────────────────────────────────────────────────
        //
        // Sans lui, une exception non geree tue le processus sans un mot : pas de fenetre, pas
        // de message, l'icone disparait. Le client ne sait meme pas qu'il y a eu une erreur, et
        // le SAV n'a rien a lire.
        //
        // Une couleur XAML invalide ou une ressource au mauvais nom passe la compilation et les
        // tests, puis tue l'application au demarrage ; sans filet, la seule trace serait un code
        // .NET dans l'observateur d'evenements, et le support n'aurait rien a demander.
        DispatcherUnhandledException += (_, args) =>
        {
            // Handled = true avant d'afficher quoi que ce soit : si le rapport lui-meme
            // echouait, on ne veut pas remplacer un crash par un autre.
            args.Handled = true;
            ReportFatal(args.Exception);
            // Sans fenêtre montrée (une panne avant ou pendant son ouverture), rien ne fermerait le processus : il garderait la
            // place de l'App, et chaque lancement suivant ne ferait que le réveiller. Le relevé de fond tourne exprès sans fenêtre.
            if (!e.Args.Contains(ArgumentReleve, StringComparer.OrdinalIgnoreCase) && MainWindow is not { IsVisible: true })
                Shutdown(1);
        };

        // Le second filet couvre ce que le premier ne voit pas : une exception levee hors du
        // fil d'interface (une tache de fond, un timer). Elle ne peut pas etre annulee (le
        // processus s'arrete de toute facon), mais elle sera ecrite.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception);

        // « --releve » = lancé à l'ouverture de session, sans fenêtre, pour le seul relevé des parties, par la tâche planifiée
        // de l'installeur, sans droits. Il ne lance jamais le helper (aucune invite au démarrage de Windows) et ne pose rien :
        // élevé, il écrivait dans le dossier du compte, où une jonction posée sans droits devenait une écriture en administrateur.
        var releveDeFond = e.Args.Contains(ArgumentReleve, StringComparer.OrdinalIgnoreCase);
        var collection = new ServiceCollection().AddDrDxtApp();
        if (releveDeFond)
            collection.AddSingleton<DrDxt.Core.IElevatedClient>(_ => DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer());
        _services = collection.BuildServiceProvider();
        if (!confiance.IsOk)
            (_services.GetService(typeof(DrDxt.Core.IDiagnosticsLog)) as DrDxt.Core.IDiagnosticsLog)?.Change("redirection-trust", "off", "unavailable", "Windows");
        if (releveDeFond)
        {
            ReleverEnFond(_services);
            return;
        }

#if DEBUG
        // « vitrine-sphere » ouvre une fenêtre qui ne montre que la sphère de marque, rendue par la
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

#endif

#if DEBUG
        // « hors-ecran » : la fenêtre s'ouvre hors de tout moniteur, sans prendre le focus ni la
        // barre des tâches, pour être photographiée par son handle pendant que l'utilisateur
        // travaille : une capture de l'écran prendrait ce qui est devant la fenêtre.
        var horsEcran = e.Args.Any(a => string.Equals(a, "hors-ecran", StringComparison.OrdinalIgnoreCase));
        if (horsEcran) Effets.MoteurDAnimation.ForcePourCapture = true;
#endif
        // Une seule fenêtre par session : un second lancement montre la première et s'arrête là, avant tout helper.
        var prendreLaPlace = true;
#if DEBUG
        prendreLaPlace = !horsEcran; // une capture se prend à côté d'une App ouverte
#endif
        if (prendreLaPlace)
        {
            // Relancée élevée : l'ancienne App rend sa place pendant son arrêt, celle-ci l'attend jusqu'à 5 s.
            var attente = e.Args.Contains(DrDxt.App.Platform.RelanceElevee.ApresRelance, StringComparer.Ordinal)
                ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
            _instance = InstanceUnique.Prendre(InstanceUnique.NomDeSession(), () => Dispatcher.BeginInvoke(MontrerLaFenetre), attente);
            if (_instance is null)
            {
                Shutdown();
                return;
            }
        }

        var window = new MainWindow();
#if DEBUG
        if (horsEcran) PreparerHorsEcran(window, e.Args);
#endif


        var scan = _services.GetRequiredService<ScanViewModel>();
        _ecranOuvert = scan;
        // Un réglage temporaire posé (décalage expert, palier de sous-tension) se rend avant l'arrêt de l'App : la
        // fermeture est suspendue le temps du rendu (5 s au plus), sans bloquer le fil d'interface où la recherche finit
        // son finally. OnExit, qui bloque ce fil, ne reste qu'en secours. Windows qui s'arrête ignore Cancel : le rendu lancé
        // ici ne finirait jamais, c'est alors OnExit qui rend, hors de ce fil.
        // Un acquit refusé est repris une dernière fois, borné : un réglage gardé que le helper pourrait défaire au prochain
        // démarrage se dit au joueur, puis la fenêtre se ferme quoi qu'il arrive.
        window.Closing += async (_, fermeture) =>
        {
            if (_sessionFinit || _fermetureAutorisee) return;
            // Un second clic sur la croix pendant le rendu (10 s au plus) attend sa fin : sinon il fermerait l'App au milieu, et
            // seul le bail du helper rendrait la tension ou le décalage.
            if (_fermetureLancee) { fermeture.Cancel = true; return; }
            // Optimiser ou une vérification en cours : fermer la laisserait à moitié, la fenêtre le demande.
            if (scan.AvertissementDeFermeture is { } avertissement
                && MessageBox.Show(window, avertissement, "DrDXT", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                fermeture.Cancel = true;
                return;
            }
            var rendre = scan.QuelqueChoseARendre;
            if (!rendre && !DrDxt.App.Platform.ClientDesPoses.UnAcquitAttend) return;
            _fermetureLancee = true;
            fermeture.Cancel = true;
            // Quoi qu'il lève ici (un rendu qui échoue avant même de rendre sa tâche), la fenêtre se ferme.
            try
            {
                if (rendre)
                {
                    _renduAvantFermeture = scan.RendreAvantFermetureAsync();
                    try { await _renduAvantFermeture.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* le bail ou le redémarrage rendront */ }
                }
                if (await DrDxt.App.Platform.ClientDesPoses.TexteDeFermetureAsync(DrDxt.App.Platform.ElevatedClientFactory.DernierEssaiDesAcquitsAsync,
                        () => _sessionFinit, TimeSpan.FromSeconds(5)) is { } texte)
                    MessageBox.Show(window, texte, "DrDXT", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _fermetureAutorisee = true; window.Close(); }
        };
        window.Content = new ScanView { DataContext = scan };
        window.Show();

        // Remplit la liste d'attente « va examiner » hors du thread UI, sans l'attendre.
        _ = scan.LoadIdleComponentsAsync();

        // Les 4,5 s d'initialisation de perflib se paient une fois par processus.
        // Elles se paient ici, pendant que la fenetre s'ouvre et que personne n'attend rien,
        // et non pendant le scan. Voir AmorceDesCompteurs.
        _ = DrDxt.App.Platform.AmorceDesCompteurs.AuLancementAsync();

        // L'App relit au lancement les reglages que DrDXT a poses, et dit ce qui a bouge.
        _ = DrDxt.App.Platform.VerificationDesReglages.AuLancementAsync(_services, scan);

        // Une session tuee a peut-etre laisse un levier d'essai pose : le rendre, et le dire.
        _ = RendreSessionInterrompueAsync(_services, scan);

        // La protection du ping que le joueur avait allumee se rallume ici.
        scan.MemoirePlafond = DrDxt.Application.Network.PlafondEnvoiMemorise.ParDefaut();
        // Service de demarrage en marche = c'est lui qui porte la protection du ping (un seul poseur).
        scan.ServiceDemarrageActif = () => new DrDxt.App.Platform.Win32ServiceStateReader()
            .GetServiceState("DrDxtReconciler") is { IsOk: true, Value: DrDxt.Application.Tweaks.Hardware.ServiceState.Running };
        // La pause du maintien ne se propose que si le service est installe ; son etat se relit dans le fichier que pose le helper.
        scan.ServiceInstalle = () => new DrDxt.App.Platform.Win32ServiceStateReader()
            .GetServiceState("DrDxtReconciler") is { IsOk: true, Value: not DrDxt.Application.Tweaks.Hardware.ServiceState.NotInstalled };
        scan.LirePauseDuMaintien = () => DrDxt.Reconciler.PauseDuMaintien.ParDefaut().Lire();
        // Et son plafond suit le ping pendant la partie (releve passif, le meme que le scan).
        var collecteur = new DrDxt.Measurement.Network.CollecteurEnvoi(
            new DrDxt.Measurement.Network.SystemPingProbe(),
            DrDxt.Detection.Network.AttributionReseauService.ReferenceInternetParDefaut);
        scan.CollecteurFenetre = (duree, ct) => collecteur.CollecterAsync(duree, TimeSpan.FromMilliseconds(250), ct);
        // Le test de la ligne sous charge : lancé par le seul bouton, jamais au scan ni au démarrage.
        scan.TesterLaLigne = async ct =>
        {
            // Sans redirection : une réponse 3xx ne mène jamais vers une adresse que le README ne nomme pas.
            using var client = new System.Net.Http.HttpClient(DrDxt.App.Platform.ProxyDuCompte.Transport(redirections: false))
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
            var sonde = new DrDxt.Measurement.Network.SystemPingProbe();
            // La passerelle se lit par WMI : hors du fil de l'écran, que le clic ne gèle pas la fenêtre.
            var passerelle = await Task.Run(() => new DrDxt.Detection.Network.AttributionReseauService(
                new DrDxt.Detection.Network.NetworkAdapterReader(new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                new DrDxt.Measurement.Network.NetworkLatencyMeter(sonde)).Passerelle(), ct);
            var mesure = await DrDxt.Measurement.Network.TestDeLigneSousCharge.MesurerAsync(client, sonde,
                DrDxt.Detection.Network.AttributionReseauService.ReferenceInternetParDefaut, passerelle, ct);
            return DrDxt.Measurement.Network.TestDeLigneSousCharge.Phrase(mesure,
                DrDxt.Measurement.Network.TestDeLigneSousCharge.VitesseDuLienMbps(passerelle), DateTimeOffset.Now);
        };
        scan.JournalPlafond = _services.GetService(typeof(DrDxt.Core.IDiagnosticsLog)) as DrDxt.Core.IDiagnosticsLog;
        // Le message technique d'un échec ne va qu'au journal ; l'écran dit la phrase de son code.
        if (_services.GetService(typeof(Serilog.ILogger)) is Serilog.ILogger journal)
            DrDxt.App.ViewModels.RaisonLisible.JournalParDefaut = m => journal.Warning("Échec : {Raison}", m);
        scan.HelperSansLancer = DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer();
        scan.ServicePorteUnRetenu = () => DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut().Lire() is not null;
        _ = scan.RallumerPlafondEnvoiAsync();
        // Et la veille « Surveiller mes parties », si le joueur l'avait laissee allumee.
        scan.MemoireVeille = DrDxt.Application.Network.VeillePingMemorisee.ParDefaut();
        scan.RallumerVeillePing();
        // Le choix « programmes gourmands après le jeu », gardé ; rien d'abaissé ne survit à DrDXT.
        scan.MemoireCalmer = new DrDxt.Application.Network.VeillePingMemorisee(
            DrDxt.Core.FichierJsonAtomique.CheminLocal("calmer-voisins.json"));
        // Les programmes que le joueur exclut du calmeur, lus avant de le rallumer.
        scan.FichierExclus = new DrDxt.Core.FichierJsonAtomique(DrDxt.Core.FichierJsonAtomique.CheminLocal("voisins-exclus.json"), "VOISINS_EXCLUS");
        scan.ChargerExclus();
        scan.RallumerCalmerVoisins();
        Exit += (_, _) => scan.RendreLesPriorites();
        // Les parties relevées, et le relevé relancé si le joueur l'avait laissé allumé.
        scan.MemoireParties = DrDxt.Application.Parties.PartiesMemorisees.ParDefaut();
        // Avant ChargerParties, sinon la carte se construit sans les changements.
        scan.JournalChangements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut();
        scan.FichierBiosLu = new DrDxt.Core.FichierJsonAtomique(
            DrDxt.Core.DossierDonnees.Chemin(DrDxt.Detection.Cpu.ChaleurEnPartie.FichierBios), "BIOS_LU");
        BrancherReglagesSuivis(scan, _services);
        scan.MesurerAvantPartie = ct => DrDxt.App.Platform.MesureAvantPartie.MesurerAsync(
            new DrDxt.App.Platform.VoisinsWin32(), ct);
        // Le paquet d'aide s'écrit là où le joueur le choisit, jamais ailleurs.
        scan.ChoisirCheminDuPaquet = nom =>
        {
            var dialogue = new Microsoft.Win32.SaveFileDialog { FileName = nom, DefaultExt = ".zip", Filter = "zip|*.zip" };
            return dialogue.ShowDialog() == true ? dialogue.FileName : null;
        };
        // « Quoi de neuf » : le CHANGELOG de la langue du joueur, livré dans l'App, jamais téléchargé.
        using (var changelog = typeof(App).Assembly.GetManifestResourceStream(
                   DrDxt.Application.Support.NotesDeVersion.Ressource(DrDxt.Core.Docteur.Langue)))
            scan.ChargerQuoiDeNeuf(changelog is null ? null : new StreamReader(changelog).ReadToEnd(),
                DrDxt.Core.DossierDonnees.Chemin("quoi-de-neuf-vu.json"), typeof(App).Assembly.GetName().Version?.ToString(3));
        scan.LireIncidents = () =>
        {
            var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Since(DateTimeOffset.Now.AddDays(-30));
            return (arrets.IsOk ? arrets.Value! : [],
                (DrDxt.Detection.Repair.ErreursMaterielles.Lire(30) ?? []).Where(e => e.Gravite is 0 or 1 or 2).Select(e => e.Quand).ToList());
        };
        // Le test mémoire laisse un point d'étape ; resté en place, il se juge contre les arrêts et les erreurs graves.
        var repriseTestMemoire = DrDxt.Core.DossierDonnees.Chemin(DrDxt.Measurement.Memory.RepriseDuTestMemoire.Fichier);
        scan.FichierRepriseTestMemoire = repriseTestMemoire;
        scan.JugerRepriseTestMemoire = () =>
        {
            if (DrDxt.Measurement.Memory.RepriseDuTestMemoire.Lire(repriseTestMemoire) is not { } point) return null;
            var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Detail(point.Debut.AddMinutes(-1));
            return DrDxt.Measurement.Memory.RepriseDuTestMemoire.JugerUneFois(repriseTestMemoire, point,
                arrets.IsOk ? DrDxt.Detection.Hardware.ArretBrutal.HorsBouton(arrets.Value!).Select(a => a.Quand).ToList() : null,
                DrDxt.Detection.Repair.ErreursMaterielles.Lire(Math.Max(1, (int)Math.Ceiling((DateTimeOffset.Now - point.Debut).TotalDays) + 1))
                    ?.Where(e => e.Gravite is 0 or 1).Select(e => e.Quand).ToList(),
                DrDxt.Detection.Hardware.DemarrageDuSysteme.Depuis(point.Debut));
        };
        // Cinq secondes d'écoute des entrées brutes, et les couches logicielles qui faussent la mesure.
        scan.MesureDesEntrees = ct =>
        {
            var duree = TimeSpan.FromSeconds(5);
            var (appareils, classes) = DrDxt.Detection.Hardware.CouchesDEntree.Lire();
            return DrDxt.Detection.Hardware.CadenceDesEntrees.Juger(DrDxt.Detection.Hardware.CadenceDesEntrees.Mesurer(duree, ct),
                appareils is not null && DrDxt.Detection.Hardware.CouchesDEntree.CoucheActive(appareils, classes), duree);
        };
        scan.LireChangementsDepuisLeScan = () => DrDxt.Detection.Repair.InventaireMachine.Apercu(
            DrDxt.Core.DossierDonnees.Chemin("inventaire-machine.json"), DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil)
            .Select(c => c.Explication).ToList();
        scan.LireMisesAJourDeLaMachine = () =>
            [.. DrDxt.Detection.Repair.AvantLIncident.LirePilotes(), .. DrDxt.Detection.Repair.MisesAJourWindows.Lire(),
             .. DrDxt.Detection.Repair.InstallationDeWindows.Fait()];
        // Lues dès l'ouverture, pour que le premier affichage borne déjà les bilans.
        _ = scan.MisesAJourAsync();
        scan.LevierParId = id => _services!.GetRequiredService<DrDxt.Core.ITweakCatalog>().Get(id) is { IsOk: true } t ? t.Value : null;
        scan.Alimentation = _services!.GetService<DrDxt.Application.Tweaks.Hardware.ISourceDAlimentation>();
        // La recherche de sous-tension ; coupée par un plantage, elle se relit ici (palier fautif dit).
        scan.MemoireSousTension = DrDxt.Application.Parties.MemoireSousTension.ParDefaut();
        // Jamais d'invite UAC sans geste ; le retrait automatique ne part que si le helper tourne déjà. Posé avant la reprise
        // de la sous-tension : lu encore vide, il ferait passer le retour par le client qui lance le helper (une invite).
        scan.HelperJoignableSansUac = async () =>
        {
            try { return (await DrDxt.App.Platform.ElevatedClientFactory.CreateSansLancer().ExecuteAsync(new DrDxt.Core.PingOp(), CancellationToken.None)).IsOk; }
            catch (Exception) { return false; }
        };
        _ = Task.Run(async () =>
        {
            try { await scan.ReprendreRechercheSousTensionAsync(); }
            // Rien à montrer de plus : la carte relit la recherche au prochain scan. La panne, elle, part en trace de débogage.
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Undervolt search resume failed: {0}", ex); }
        });
        scan.RetirerDuManifeste = id => DrDxt.App.Platform.RetraitDeLevier.ApresAnnulationAsync(
            _services!.GetRequiredService<DrDxt.Core.IDesiredStateStore>(),
            new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut()), id);
        scan.PrendreVerrouReleve = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.VerrouDuReleve.CheminParDefaut());
        scan.PrendreVerrouEssai = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.MemoireDeLEssai.CheminDuVerrou());
        // Lue une fois, à la première question : la mesure de la cadence ne nomme la manette qu'à qui en a une.
        // Non lu : on ne sait pas, le texte nomme la manette.
        var manette = new Lazy<bool>(() => DrDxt.Detection.Hardware.PeripheriquesDEntree.Lire() is not { } appareils
            || appareils.Any(a => a.Roles.Contains(DrDxt.Detection.Hardware.PeripheriquesDEntree.Role.Manette)));
        scan.ManetteBranchee = () => manette.Value;
        scan.ChargerParties();
        scan.ChargerLeviersPoses();
        // Les bascules se relèvent aussi sans scan ; le retrait part d'ici, l'écran ouvert.
        _ = scan.ReleverReglagesHorsScanAsync();
        // Une preuve du repos de la carte commencée avant un redémarrage se conclut au lancement ;
        // le réglage gardé reste ensuite jugé par les parties relevées.
        scan.LevierReposCarte = new DrDxt.Application.Tweaks.Latency.ReposRenduALaCarteTweak(
            (DrDxt.Application.Tweaks.Hardware.INvApi)_services.GetService(typeof(DrDxt.Application.Tweaks.Hardware.INvApi))!,
            (DrDxt.Core.IElevatedClient)_services.GetService(typeof(DrDxt.Core.IElevatedClient))!);
        scan.ReglagesDeLaCarte = DrDxt.Application.Tweaks.ReglagesRetenus.ParDefaut();
        scan.NvApiDesJeux = _services.GetService(typeof(DrDxt.Application.Tweaks.Hardware.INvApi)) as DrDxt.Application.Tweaks.Hardware.INvApi;
        scan.SondeCarte = new DrDxt.Detection.NvmlHardwareProbe();
        scan.AttenteReposCarte = DrDxt.Application.Tweaks.PreuvesEnAttente.ParDefaut();
        _ = scan.ConclureReposCarteAsync(scan.LevierReposCarte, scan.SondeCarte, scan.AttenteReposCarte, t => Task.Delay(t));

        // La page Windows dit ce que le service a réaffirmé seul (lecture seule de son état).
        scan.LireEtatDuService = () =>
            new DrDxt.Reconciler.JsonReconcilerStateStore(DrDxt.Reconciler.JsonReconcilerStateStore.DefaultPath(), lectureSeule: true)
                .LoadAsync(CancellationToken.None).Result is { IsOk: true } lu ? lu.Value : null;

        // La liste « ce que DrDXT fait ici » de chaque page lit le vrai catalogue.
        if (_services.GetService<DrDxt.Core.ITweakCatalog>() is { } catalogueLeviers)
            scan.CatalogueDesLeviers = catalogueLeviers.All;
        scan.LeviersVerifiesIci = hw => DrDxt.Application.Tweaks.LeviersVerifies.Lire(DrDxt.Core.DossierDonnees.Chemin("leviers-verifies.json"),
            DrDxt.Application.Tweaks.LeviersVerifies.Empreinte(hw));
        // La garde d'incident lit aussi les vidages à chaud du pilote graphique.
        scan.LireVidagesCarte = () => DrDxt.Detection.Hardware.StabiliteLue.VidagesCarte(new DrDxt.Detection.Hardware.WerLiveKernelReportLog());

        // Le mode Expert de la carte (page Graphique). Même journal que l'autopilote : le démarrage
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
            // Le ventilateur NVIDIA, même journal, son propre bail ; le helper ne pose que vers le haut.
            if (_services.GetService<DrDxt.Core.IElevatedClient>() is { } eleve)
            {
                var ventilateur = new DrDxt.Application.Tweaks.Gpu.VentilateurExpert(eleve, journalCarte);
                scan.PoserVentilateurExpert = pct => ventilateur.PoserAsync(pct);
            }
        }

        // Une référence mémoire ou processeur prise pendant une partie se remesure seule quand le
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

        // Relancée en administrateur par l'analyse, l'appli reprend la scène choisie et analyse.
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
        // « expert-auto » prouve le mode Expert sur la vraie carte par le chemin du
        // produit (App, helper, bail) : ventilateur fixé à 60 % puis rendu, décalage d'horloge +15 MHz puis rendu, chaque
        // étape relue (NVML pour le ventilateur, NVAPI pour l'horloge) et écrite dans %TEMP%\drdxt-expert.txt.
        if (e.Args.Any(a => string.Equals(a, "expert-auto", StringComparison.OrdinalIgnoreCase)))
        {
            window.ContentRendered += async (_, _) =>
            {
                var journal = new System.Text.StringBuilder();
                var nvml = new DrDxt.GpuNative.Nvml.NvmlNative();
                string Ventilo() => nvml.TryLireVentilateurs(0, out var v)
                    ? string.Join(" ; ", v.Select(f => $"ventilateur {f.Index} : {f.VitessePct} %, cible {(f.CiblePct is { } c ? $"{c} %" : "non lue")}, {(f.Automatique ? "automatique" : "manuel")}"))
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
        // « expert-crash » fixe le ventilateur à 60 % puis tue l'App sans rien rendre ;
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
        // « verifier-auto » lance « Vérifier DrDXT sur mon PC » par le même chemin que le
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

        // Les tables gardées (radar des régressions) avant le premier scan : un fichier relu et vérifié, rien sur le réseau.
        ChargerLesDonneesGardees();

        // La mise a jour, verifiee pour de vrai (sous delai borne, hors fil d'interface).
        _ = VerifierMisesAJourAsync(scan);
    }

    /// <summary>Le paquet de tables gardé, s'il se vérifie avec la clé de release ; sinon le précédent, sinon les tables
    /// livrées avec l'App.</summary>
    private static void ChargerLesDonneesGardees()
    {
        try
        {
            if (DrDxt.Infrastructure.Updates.DonneesSignees.Verificateur.DeProduction() is { IsOk: true, Value: { } verificateur })
                DrDxt.Core.DonneesEnService.Installer(
                    DrDxt.Infrastructure.Updates.DonneesSignees.Charger(DrDxt.Core.DossierDonnees.Racine(), verificateur));
        }
        catch (Exception) { /* les tables livrées avec l'App restent en service */ }
    }

    /// <summary>Demande au serveur de mise à jour un paquet de tables plus récent, sous le même délai que la vérification
    /// de l'App ; « off » sur la variable coupe aussi cette demande.</summary>
    private static async Task DemanderLesDonneesAsync(ScanViewModel scan, System.Net.Http.HttpClient client, string? url)
    {
        var issue = DrDxt.Infrastructure.Updates.DonneesSignees.Issue.RienDeNeuf;
        try
        {
            if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var racine) && racine.Scheme == Uri.UriSchemeHttps
                && DrDxt.Infrastructure.Updates.DonneesSignees.Verificateur.DeProduction() is { IsOk: true, Value: { } verificateur })
            {
                using var borne = new CancellationTokenSource(DrDxt.App.Updates.VerificationMiseAJour.DelaiParDefaut);
                (issue, var tables) = await DrDxt.Infrastructure.Updates.DonneesSignees.DemanderAsync(client, racine,
                    DrDxt.Core.DossierDonnees.Racine(), verificateur, DrDxt.Core.DonneesEnService.Courantes, borne.Token).ConfigureAwait(false);
                if (tables is not null) DrDxt.Core.DonneesEnService.Installer(tables);
            }
        }
        catch (Exception) { /* les tables en service restent */ }
        scan.AfficherDonnees(DrDxt.Core.DonneesEnService.Courantes, issue == DrDxt.Infrastructure.Updates.DonneesSignees.Issue.Refuse);
    }

    private static async Task VerifierMisesAJourAsync(ScanViewModel scan)
    {
        // Lancée sans attente : une panne imprévue se dit « échec », jamais une vérification qui ne finit pas.
        try { await VerifierMisesAJourSansFiletAsync(scan); }
        catch (Exception) { scan.AfficherMiseAJour(new DrDxt.App.Updates.ResultatMiseAJour(DrDxt.App.Updates.EtatMiseAJour.Echec)); }
    }

    private static async Task VerifierMisesAJourSansFiletAsync(ScanViewModel scan)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        // L'installeur d'une version déjà installée a servi : le staging part (des dizaines de Mo).
        DrDxt.App.Updates.NettoyageDuStaging.Purger(DrDxt.Infrastructure.Updates.UpdateComposition.DossierDuStagingApp(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), new Version(version));
        var url = DrDxt.App.Updates.VerificationMiseAJour.UrlEffective(Environment.GetEnvironmentVariable(DrDxt.App.Updates.VerificationMiseAJour.VariableUrl));
        // Le client vit autant que l'App : le bouton « Installer » télécharge plus tard par lui.
        var (etat, client) = await Task.Run(() => DrDxt.App.Updates.VerificationMiseAJour.VerifierAuDemarrageAsync(
            c => DrDxt.App.Updates.VerificationMiseAJour.Monter(url, c, version)));
        scan.AfficherMiseAJour(etat);
        await DemanderLesDonneesAsync(scan, client, url);
    }

    private static async Task RendreSessionInterrompueAsync(IServiceProvider services, ScanViewModel scan)
    {
        try
        {
            await scan.RendreSessionInterrompueAsync(services.GetRequiredService<DrDxt.Application.Autopilot.IRevertEnAttente>(),
                services.GetRequiredService<DrDxt.Core.ITweakCatalog>());
        }
        catch (Exception ex)
        {
            scan.MessageDemarrage = DrDxt.Core.Docteur.T("App.VerificationSessionEchec", ex.Message);
        }
    }

    /// <summary>L'argument de la tâche planifiée « DrDXT releve » (installer/DrDXT.iss).</summary>
    public const string ArgumentReleve = "--releve";

    /// <summary>Le relevé des parties DrDXT fermé. Aucune fenêtre ; le même relevé que l'App (même code,
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
        // Le relevé tourne à côté du jeu : il cède la main à tout le reste, qu'il soit lancé par le Planificateur ou par
        // l'installeur (hors du Planificateur, rien d'autre ne baisse sa priorité).
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
        catch (Exception) { /* une priorité refusée n'empêche pas le relevé */ }
        // L'ouverture de session, sans jeu : l'initialisation de perflib se paie ici plutôt qu'à la première lecture du
        // processeur, en pleine première partie.
        _ = DrDxt.App.Platform.AmorceDesCompteurs.AuLancementAsync();
        var scan = services.GetRequiredService<ScanViewModel>();
        scan.MemoireParties = memoire;
        scan.PrendreVerrouReleve = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.VerrouDuReleve.CheminParDefaut());
        scan.PrendreVerrouEssai = () => DrDxt.Application.Parties.VerrouDuReleve.Prendre(DrDxt.Application.Parties.MemoireDeLEssai.CheminDuVerrou());
        // Lue une fois, à la première question : la mesure de la cadence ne nomme la manette qu'à qui en a une.
        // Non lu : on ne sait pas, le texte nomme la manette.
        var manette = new Lazy<bool>(() => DrDxt.Detection.Hardware.PeripheriquesDEntree.Lire() is not { } appareils
            || appareils.Any(a => a.Roles.Contains(DrDxt.Detection.Hardware.PeripheriquesDEntree.Role.Manette)));
        scan.ManetteBranchee = () => manette.Value;
        // Les bascules d'après le redémarrage se notent et se jugent ici, sans scan. LevierParId n'est pas
        // posé : rien ne se retire sans fenêtre (voir ReleverReglagesHorsScanAsync).
        scan.JournalChangements = DrDxt.Application.Parties.JournalDesChangements.ParDefaut();
        BrancherReglagesSuivis(scan, services);
        // Sans HelperJoignableSansUac ni client qui lance le helper : sans fenêtre, rien ne se pose ni ne se rend (l'essai en
        // alternance avance dans l'App, sur ce qu'elle montre).
        scan.ChargerParties();
        _ = scan.ReleverReglagesHorsScanAsync();
        // Sans fenêtre, un essai qui attend l'App ouverte se dit par une notification de Windows, une fois par attente.
        scan.NotifierEssaiEnAttente = texte => DrDxt.App.Platform.NotificationDeBureau.Montrer("DrDXT", texte);
        scan.AppOuverte = () => InstanceUnique.Ouverte(InstanceUnique.NomDeSession());
        var relecture = new System.Windows.Threading.DispatcherTimer { Interval = DrDxt.Application.Parties.VeilleDesParties.HorsPartie };
        relecture.Tick += (_, _) => { if (!memoire.EstAllume()) Shutdown(); };
        relecture.Start();
    }

    /// <summary>Le suivi des réglages et leurs lectures sans scan, les mêmes pour l'App et le relevé de fond.</summary>
    private static void BrancherReglagesSuivis(ScanViewModel scan, IServiceProvider services)
    {
        // L'essai en alternance, lu par l'App et par le relevé de fond (qui marque les parties ; seul l'App
        // pose ou rend le levier, quand son helper tourne déjà).
        scan.MemoireEssai = DrDxt.Application.Parties.MemoireDeLEssai.ParDefaut();
        scan.DerniereSortieDeVeille = ScanViewModel.SortieDeVeilleEnCache;
        scan.RejeuLu = DrDxt.App.Platform.RejeuDuJournalLu.ParDefaut();
        scan.RejeuDuCompte = () => DrDxt.App.Platform.RejeuDuJournalDuCompte.RejouerAsync(DrDxt.App.Platform.ElevatedClientFactory.JournalDuCompte(),
            new DrDxt.ElevatedHelper.Execution.LectureDesCiblesWin32());
        scan.RapportsDuRejeu = () => DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Lire(
            DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot(), DrDxt.ElevatedHelper.Journal.RapportDuRejeu.DeConfiance);
        scan.SuiviReglages = DrDxt.Application.Parties.SuiviDesReglages.ParDefaut();
        scan.LireInstantReplay = () => DrDxt.Detection.Gpu.EnregistrementNvidia.EtatInstantReplay(DrDxt.Detection.Gpu.EnregistrementNvidia.LireReglages());
        scan.LireReglagesSuivis = () => ScanViewModel.EtatsSuivisSansScan(services.GetRequiredService<DrDxt.Detection.Settings.IRegistryProvider>(),
            services.GetService<DrDxt.Detection.Settings.IHagsEffectiveReader>(), scan.LireInstantReplay());
    }

    /// <summary>
    /// Ecrit la panne, puis la montre. Dans cet ordre : si l'affichage echoue a son tour (pas de
    /// bureau, session en fermeture), la trace est deja sur le disque.
    ///
    /// Total par construction : chaque moitie est isolee. Un filet de securite qui peut lui-meme
    /// lever ne protege de rien, et rate precisement le cas ou tout va deja mal.
    /// </summary>
    private static void ReportFatal(Exception? ex)
    {
        var detail = ex?.ToString() ?? "Unknown exception (no exception object supplied).";

        try
        {
            var path = CrashLogPath;
            // Ajout, jamais ecrasement : deux plantages de suite racontent une histoire que le
            // second seul ne raconte pas.
            // BOM UTF-8 explicite. Sans elle, .NET ecrit de l'UTF-8 nu et les lecteurs qui
            // devinent (PowerShell 5.1 en tete, l'outil le plus probable d'un depannage)
            // rendent « levée » en « levÃ©e ». Un rapport de panne illisible est un rapport de
            // panne perdu.
            // Dossier du joueur : jamais à travers un nom posé d'avance en lien (l'App peut tourner élevée).
            // L'App continue après une panne d'interface : sans plafond, le journal grossirait à chacune. Au-delà d'un Mo, il
            // garde sa moitié la plus récente.
            DrDxt.Core.FichierJsonAtomique.BornerSansLien(path, 1024 * 1024, "CRASH_LOG");
            DrDxt.Core.FichierJsonAtomique.AjouterSansLien(path,
                $"=== {DateTimeOffset.Now:u} ==={Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}",
                "CRASH_LOG", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // Disque plein, droits refuses : on ne peut pas ecrire. Ce n'est pas une raison de
            // priver l'utilisateur du message ci-dessous.
        }

        try
        {
            // Le message vient d'une clé resx, dans la langue du poste, comme toute boîte de dialogue.
            //
            // Ici, le localisateur est construit a la main, pas injecte. Ce filet tourne
            // quand tout le reste a deja echoue : demander un service au conteneur, c'est
            // dependre de la chose qui vient peut-etre de casser. `ResxLocalizer` ne lit qu'un
            // jeu de ressources embarque, et sa clef manquante rend la clef, jamais une
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

#if DEBUG
    /// <summary>
    /// Sonde de gel (DEBUG) : ce que l'oeil voit, chronometre.
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
        // changement d'etat du scan s'inscrit ici, sur la meme horloge.
        scan.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(ScanViewModel.State))
                File.AppendAllText(chemin, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "t+{0,7:F0} ms : état du scan -> {1}\n", (DateTime.UtcNow - depart).TotalMilliseconds, scan.State));
        };
    }

    // Une fois le résultat affiché, défile page par page et dépose un signal par page
    // (%TEMP%\drdxt-visite-N.txt, puis drdxt-visite-fin.txt) : le script de capture photographie
    // la fenêtre à chaque signal.
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
            // « ping-disponible » : simule ce qu'une mesure en jeu aurait retenu (plafond conseillé,
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
                    // La page elle-même, photographiée par l'appli (hors écran, rien n'apparaît sur l'écran).
                    PhotographierVue(window, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"page-{numero}.png"));
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            minuterie.Start();
        };
    }

    // Ouvre chaque domaine de la carte, l'amène à l'écran et dépose un signal par domaine.
    private static async void PhotographierLesDomaines(Window window, ScanViewModel scan, string dossier)
    {
        // « remesurer-processeur » : la même commande que la carte « Remesurer ton processeur »,
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
                // « rapport » : le même export que le bouton, pour juger le document réel
                // (sections, anonymisation) sur un scan réel, sans personne devant l'écran.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "rapport", StringComparison.OrdinalIgnoreCase)))
                    scan.ExportReportCommand.Execute(null);
                // « exporter », le même fichier que « Exporter ma machine », pour le ranger en fixture.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "exporter", StringComparison.OrdinalIgnoreCase)))
                    await scan.ExporterMaMachineCommand.ExecuteAsync(null);
                // Hors écran (le seul cas où ce drapeau est levé), personne ne regarde la fenêtre :
                // rester ouvert faisait attendre le lanceur jusqu'à son délai, ~5 min par tournée.
                if (Effets.MoteurDAnimation.ForcePourCapture) Current.Shutdown();
                return;
            }
            var domaine = scan.Domaines[index++];
            scan.DomaineSelectionne = domaine;
            // La capsule du rail glisse 280 ms jusqu'au domaine choisi ; photographier tout de suite la
            // prenait à mi-chemin, et l'audit croyait la surbrillance décalée. On attend la fin du glissement.
            var apresGlissement = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            apresGlissement.Tick += (_, _) => { apresGlissement.Stop(); Photographier(); };
            apresGlissement.Start();
            void Photographier() => window.Dispatcher.BeginInvoke(() =>
            {
                // « deplier » ouvre les listes repliées (« Ce que DrDXT fait ici », mode Expert) pour les
                // photographier ouvertes.
                if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "deplier", StringComparison.OrdinalIgnoreCase)))
                    DeplierTout(window);
                if (DescendantNomme(window, "CarteMachine") is FrameworkElement carte)
                    carte.BringIntoView(new Rect(0, 0, carte.ActualWidth, 40));
                window.Dispatcher.BeginInvoke(() =>
                {
                    File.WriteAllText(Path.Combine(dossier, $"drdxt-visite-domaine-{domaine.Cle}.txt"), domaine.Cle);
                    // Une photo par domaine : les 40 photos du minuteur s'arretent avant la
                    // carte, et une interface se juge sur un scan reel, domaine par domaine.
                    PhotographierVue(window, Path.Combine(Path.GetTempPath(), "drdxt-photos", $"domaine-{domaine.Cle}.png"));
                    // Et le bas du panneau : un domaine long (windows) cache sa section « Agir » sous le pli.
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
            // les limites visuelles du contenu sur le cadre (photo du BIOS écrasée, texte illisible).
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

#if DEBUG
    // Préparation d'une fenêtre de scan ouverte en « hors-ecran », pour les captures.
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
            // « tout » photographie le contenu entier de la zone qui défile, cartes sous le pli comprises.
            if (args.Any(a => string.Equals(a, "tout", StringComparison.OrdinalIgnoreCase))
                && Descendant<System.Windows.Controls.ScrollViewer>(vue)?.Content is FrameworkElement contenu && contenu.ActualHeight > 0)
                vue = contenu;
            var rendu = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)vue.ActualWidth, (int)vue.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rendu.Render(vue);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendu));
            // « dernier » réécrit une seule photo jusqu'à la fermeture (un scan dure plus de 16 s).
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

#endif

    /// <summary>Le rendu lancé par la fermeture de la fenêtre. Lancé, OnExit n'en relance pas un second : deux écritures de
    /// la tension en parallèle, puis des services disposés sous la première.</summary>
    private Task? _renduAvantFermeture;

    /// <summary>La fenêtre a lancé sa fermeture (rendu, dernier essai des acquits) : un second clic attend.</summary>
    private bool _fermetureLancee;

    /// <summary>Le rendu de fermeture est fini : le Close qui la termine passe.</summary>
    private bool _fermetureAutorisee;

    private bool _sessionFinit;

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _sessionFinit = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Un décalage gardé est rendu à la fermeture, borné à 5 s ; sinon le bail le rend. Secours seulement : quand la fenêtre a
        // déjà lancé le rendu, il finit sur son fil ou le bail et le redémarrage rendent.
        if (_renduAvantFermeture is null && _ecranOuvert is { } scanFin)
            try { Task.Run(scanFin.FermerCarteExpertAsync).Wait(TimeSpan.FromSeconds(5)); } catch { /* le bail rendra */ }
        // La recherche de sous-tension en cours : arrêtée et la tension d'origine rendue, bornée à 5 s ; sinon le redémarrage la rend.
        if (_renduAvantFermeture is null && _ecranOuvert is { } scanTension)
            try { Task.Run(scanTension.RendreOrigineSansLeFilAsync).Wait(TimeSpan.FromSeconds(5)); } catch { /* le redémarrage rendra */ }
        // Session Windows qui finit, ou arrêt sans la fenêtre : le dernier essai des acquits, borné, sans boîte qui retiendrait l'arrêt.
        if (!_fermetureLancee && DrDxt.App.Platform.ClientDesPoses.UnAcquitAttend)
            try { Task.Run(DrDxt.App.Platform.ElevatedClientFactory.DernierEssaiDesAcquitsAsync).Wait(TimeSpan.FromSeconds(5)); } catch { /* le rejeu le dira */ }
        _services?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
