using System.Runtime.Versioning;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.ElevatedHelper.Leases;

[assembly: SupportedOSPlatform("windows")]
// Processus eleve : une DLL cherchee par son seul nom passe par le dossier courant et le PATH, modifiables par
// l'utilisateur, et s'executerait en administrateur. Tout ce que ce processus charge vit dans System32.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace DrDxt.ElevatedHelper;

// Hôte du helper élevé : la passerelle typée privilégiée (vrai exécuteur + OpAllowlist + authentificateur par identité
// de processus) sur la boucle d'acceptation du pipe nommé protégé par ACL. Fail-closed partout. Deux modes, qui
// authentifient l'appelant et refusent par défaut :
//   * handshake (par défaut) : aucune clé d'avance ; chaque connexion authentifie l'appelant puis négocie une clé
//     éphémère (HandshakeAcceptingServer), côté App ElevatedClientFactory + HandshakingElevatedClient ;
//   * historique (dev, outils de smoke test) : --session-key=<b64> / DRDXT_HELPER_SESSION_KEY lance
//     TypedPrivilegedServer avec cette clé HMAC partagée.
// Appelant de confiance : --app-path=<chemin complet> ou DRDXT_HELPER_APP_PATH, admis par chemin et SHA-256 calculé
// au démarrage (ferme l'exe remplacé au même chemin, sans signature de code) ; pas de chemin ou empreinte
// incalculable → DenyByDefault. --pipe=<nom> facultatif (nom standard par défaut). Case cochée à l'installation, la tâche
// « DrDXT helper » lance ce même exe à l'ouverture de session (--service), sur le même pipe + handshake ; la boucle ne
// change pas.
internal static class Program
{
    private const string SessionKeyArg = "--session-key=";
    private const string AppPathArg = "--app-path=";
    private const string ReconcilerPathArg = "--reconciler-path=";
    private const string PipeArg = "--pipe=";
    private const string ServiceArg = "--service";
    internal const string ConfianceIndisponible = "redirection-trust";
    private const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";
    private const string AppPathEnv = "DRDXT_HELPER_APP_PATH";
    private const string ReconcilerPathEnv = "DRDXT_HELPER_RECONCILER_PATH";

    private static async Task<int> Main(string[] args)
    {
        // Élevé, avant toute lecture ou écriture : plus aucune jonction posée par un compte non administrateur n'est suivie
        // (ConfianceDesRedirections). Indisponible (Windows d'avant 10 22H2), c'est dit au journal du helper.
        if (DrDxt.Core.ConfianceDesRedirections.Activer() is { IsOk: false })
            new HelperDiagnosticsLog().Change(ConfianceIndisponible, "off", "unavailable", "Windows");
        // La desinstallation : un mode court lance par l'installeur deja eleve (voir ModeDesinstallation), avant tout le
        // reste, sans tube. Ses rapports parlent la langue de Windows, comme l'App.
        DrDxt.Core.Docteur.FixerLangue(System.Globalization.CultureInfo.CurrentUICulture);
        if (args.Any(a => string.Equals(a, ModeDesinstallation.Argument, StringComparison.Ordinal)))
            return ModeDesinstallation.Executer();

        // Une clé partagée d'avance choisit le mode historique ; son absence, le mode handshake.
        var sessionKey = ReadSessionKey(args);
        var handshakeMode = sessionKey is null;

        // --service : lancé par la tâche « DrDXT helper » (case de l'installeur), pour toute la session. Il ne change que
        // l'arrêt (DoitSArreter) ; la boucle d'acceptation, l'authentification des appelants et l'exécuteur sont les mêmes.
        var serviceMode = args.Any(a => string.Equals(a, ServiceArg, StringComparison.Ordinal));

        var pipeName = ReadArg(args, PipeArg) ?? PipeSecurityFactory.DefaultPipeName;

        // Authentificateur : seules les images de confiance, chacune par chemin + SHA-256 calculé au démarrage :
        // --app-path (l'App interactive) et --reconciler-path (l'hôte réconciliateur peu privilégié) ; une image inconnue
        // est refusée. Sans chemin de confiance à empreinte calculable, refus par défaut (jamais tout autoriser, jamais le
        // chemin seul). Vaut pour le handshake et pour les outils de smoke test historiques (qui passent --app-path).
        var appPath = ReadArg(args, AppPathArg) ?? Environment.GetEnvironmentVariable(AppPathEnv);
        var reconcilerPath = ReadArg(args, ReconcilerPathArg)
            ?? Environment.GetEnvironmentVariable(ReconcilerPathEnv);
        var trustedPaths = HelperAuthenticatorFactory.CheminsDeConfiance(
            [appPath, reconcilerPath], AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var authenticator = HelperAuthenticatorFactory.BuildMulti(trustedPaths);
        if (authenticator is DenyByDefaultCallerAuthenticator)
        {
            Console.Error.WriteLine(
                "DrDxt.ElevatedHelper: no trusted, hashable caller path provisioned " +
                $"({AppPathArg.TrimEnd('=')} / {ReconcilerPathArg.TrimEnd('=')} or env); " +
                "denying all callers (fail-closed).");
        }
        else
        {
            Console.Out.WriteLine(
                "DrDxt.ElevatedHelper: authorizing caller images by path + SHA-256: " +
                string.Join(", ", trustedPaths) + ".");
        }

        // Vrai exécuteur : ops sûres hors GPU, DRS NVAPI, décalage d'horloge / limite de puissance NVAPI, réglage ADLX (AMD),
        // toutes de vraies actions pilote ; seule la courbe de ventilateur GPU multipoint reste reportée. Sans NVIDIA,
        // NVAPI échoue proprement (« NVIDIA NVAPI unavailable ») ; sans AMD, ADLX aussi (« AMD ADLX unavailable »).
        // Le pilote AMD, un seul par processus : celui qui écrit et celui qui relit avant d'écrire.
        var adlx = new DrDxt.Detection.Gpu.Adlx.AdlxNative();
        var executor = new PrivilegedOpExecutor(
            new Win32RegistryActions(Win32RegistryActions.TraceDuHelper),
            new Win32PowerPlanActions(),
            new Win32RestorePointActions(),
            new Win32ServiceActions(),
            new NvApiDrsActions(),
            // NVAPI privé, décalage d'horloge + limite de puissance (recette §A/§B) : GET puis SET, version=sizeof ; une
            // taille de struct incorrecte est rejetée par le pilote (IncompatibleStructVersion) avant toute écriture.
            new NvApiTuningActions(),
            // Le miroir AMD (ADLX) : GET puis SET par l'unique frontière AdlxNative, plage revérifiée en refus par défaut.
            new ADLXTuningActions(adlx),
            // Le voyage firmware (shutdown /r /fw) et son annulation (/a /fw), ici seulement : ce helper n'agit que sur
            // une op que l'utilisateur vient de lancer, un redémarrage a toujours une cause visible. DrDXT n'écrit
            // aucune variable firmware : Windows possède le bit OsIndications.
            new Win32FirmwareBootActions(),
            // Le seul ecrivain du manifeste signe : la cle de la machine vit ici, jamais
            // dans l'App (integrite moyenne) ni dans le service (qui verifie).
            new ManifestCommitActions(),
            // Le plafond d'envoi QoS (ActiveStore : efface au redemarrage).
            new Win32NetworkQosActions(),
            // Retirer / reposer un element BCD de l'ensemble ferme, relu apres ecriture.
            new Win32BcdActions(),
            // Le seul ecrivain du reglage de protection du ping que porte le service de demarrage.
            plafondDuService: DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut(),
            // Le seul ecrivain de la pause du maintien que lit le service.
            pauseDuMaintien: DrDxt.Reconciler.PauseDuMaintien.ParDefaut(),
            // Le ventilateur en mode Expert, rendu sous bail : NVML sur une carte NVIDIA (vers le haut seulement), ADLX sinon.
            ventilateur: VentilateurSelonLaCarte.ParDefaut(),
            // La limite de puissance du processeur, relue avant et après, par PawnIO s'il est là.
            cpu: new PawnIoCpuPowerActions(),
            // Relancer un peripherique en echec, retirer un vieux pilote d'affichage exporte, le remettre (pnputil).
            pnp: new Win32PnpActions(),
            diagnostics: new HelperDiagnosticsLog(),
            // Rendre aux administrateurs le fichier d'un service SYSTEM que le compte peut remplacer, et rendre l'origine.
            droits: DroitsDesServices.ParDefaut());

        // Homme mort : un décalage GPU / une limite de puissance tenus enregistrent un bail dont le retour (une op typée)
        // part seul si le contrôleur cesse ses battements. En mémoire seulement ; le même exécuteur sous allowlist rend.
        var watchdog = new LeaseWatchdog(
            new SystemClock(), new OpAllowlist(), executor, new HelperDiagnosticsLog());

        // Le journal d'écriture anticipée, dans la racine réparée d'abord. Le tube sert l'exécuteur décoré ; le chien de garde
        // ci-dessus garde l'intérieur (le retour d'un bail est une annulation, jamais journalisée).
        var lecture = new LectureDesCiblesWin32(adlx: adlx);
        var journal = JournalDuHelper.Ouvrir(DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot(),
            DrDxt.Reconciler.Manifest.TrustedManifestRoot.EnsureCreatedHardened);
        if (journal is null)
            Console.Error.WriteLine("DrDxt.ElevatedHelper: write-ahead journal unavailable (data root not trusted); marked poses are refused.");
        // Le rejeu, en tâche de fond : au démarrage, puis à chaque client authentifié (l'App relancée après un plantage
        // retrouve ce helper vivant). Jamais avant d'écouter : l'App qui vient de lancer le helper n'attend le tube que
        // quelques secondes. Le dernier rapport non vide est écrit pour l'App.
        var racineDesDonnees = DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot();
        var rejeu = new RejeuEnSerie(
            ct => JournalDuHelper.RejouerAsync(journal, lecture, new InProcessElevatedClient(new OpAllowlist(), executor),
                RejeuDuJournal.ProcessusVivant, RejeuDuJournal.CompteCourant, ct),
            rapport =>
            {
                Console.Out.WriteLine(rapport.EnTexte(DateTimeOffset.UtcNow));
                if (RapportDuRejeu.Ecrire(racineDesDonnees, RapportDuRejeu.Helper, rapport, DateTimeOffset.UtcNow,
                        (chemin, octets) => DrDxt.Reconciler.Manifest.TrustedManifestRoot.WriteTrustedFile(chemin, octets),
                        RapportDuRejeu.DeConfiance) is { IsOk: false } e)
                    Console.Error.WriteLine($"DrDxt.ElevatedHelper: replay report not written: {e.Error?.Message}");
            },
            TimeSpan.FromSeconds(30));
        // Le tube sert l'exécuteur décoré, après un rejeu en cours (ExecuteurApresLeRejeu).
        var servi = new ExecuteurApresLeRejeu(JournalDuHelper.Decorer(executor, journal, lecture.Lecteur), rejeu);

        // La stratégie d'acceptation selon le mode : clé éphémère par connexion après l'auth (handshake), ou clé HMAC
        // partagée au TypedPrivilegedServer (historique, m2smoke/r2smoke) ; même pipeline sous allowlist. Lancé à la
        // demande, le helper s'arrête après 15 minutes sans connexion authentifiée ni bail (un refus ne compte pas).
        var inactivite = serviceMode ? null : new ArretSurInactivite(TimeSpan.FromMinutes(15), () => DateTimeOffset.UtcNow);

        Func<CancellationToken, Task> acceptOne;
        if (handshakeMode)
        {
            var hsServer = new HandshakeAcceptingServer(
                authenticator, new CryptoSessionKeyGenerator(), new OpAllowlist(), servi, pipeName, watchdog)
            {
                ApresAuthentification = pid =>
                {
                    inactivite?.ClientAuthentifie(pid);
                    _ = rejeu.DemanderHorsJeu(RejeuEnSerie.JeuConnuLance);
                },
            };
            acceptOne = hsServer.AcceptOneAsync;
            Console.Out.WriteLine("DrDxt.ElevatedHelper: handshake mode (per-session key negotiated on connect).");
        }
        else
        {
            var typedServer = new TypedPrivilegedServer(
                sessionKey!, authenticator, new OpAllowlist(), servi, pipeName, watchdog);
            acceptOne = async ct =>
            {
                try { await typedServer.AcceptOneAsync(ct).ConfigureAwait(false); }
                finally { inactivite?.Activite(); }
            };
            Console.Out.WriteLine("DrDxt.ElevatedHelper: legacy pre-shared-key mode (smoke tools).");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };


        // Surveillance de fond : le chien de garde bat a cadence fixe et les baux expirés reviennent seuls ; un mauvais tick
        // ne fait jamais planter l'hôte.
        // La garde thermique du ventilateur : tant qu'une vitesse fixe est tenue, la température est lue à chaque passage.
        var temperature = GardeThermiqueVentilateur.Temperature(new DrDxt.GpuNative.Nvml.NvmlNative(), new DrDxt.Detection.Gpu.Adlx.AdlxNative());
        var monitor = RunLeaseMonitorAsync(watchdog, TimeSpan.FromSeconds(5), cts.Token,
            ct => watchdog.RendreSiAsync(op => GardeThermiqueVentilateur.DoitRendre(op, temperature), "fan-auto-hot-or-unread", ct), () =>
        {
            if (!DoitSArreter(inactivite, watchdog.NombreDeBaux, ProcessusVivant, InstalleurEnCours)) return;
            Console.Out.WriteLine("DrDxt.ElevatedHelper: app closed, 15 minutes without a connection or installer running, and no lease: stopping.");
            cts.Cancel();
        });

        // Lancé à la demande, souvent sur un geste de l'App qui peut tomber en pleine partie : le rejeu attend la fin du jeu,
        // la connexion suivante le reprend, et le service le fait au démarrage de Windows.
        _ = rejeu.DemanderHorsJeu(RejeuEnSerie.JeuConnuLance);

        Console.Out.WriteLine(
            $"DrDxt.ElevatedHelper: listening on pipe '{pipeName}' (elevated{(serviceMode ? ", service mode" : string.Empty)}).");

        // Boucle durable (HelperServiceLoop) : chaque tour sert une connexion (acceptOne) puis recrée le pipe
        // FirstPipeInstance. Une connexion mauvaise est journalisée et tolérée ; une rafale soutenue fait reculer puis
        // fermer (fail-closed). Même chemin pour le service installé et le lancement à la demande.
        var loop = new HelperServiceLoop(
            acceptOne,
            new ConsoleServiceLoopLog(),
            backoff: ct => Task.Delay(TimeSpan.FromMilliseconds(250), ct));
        var exitCode = await loop.RunAsync(cts.Token).ConfigureAwait(false);
        // Sortie en fail-closed (rafale d'échecs), la boucle rend la main sans que rien n'ait annulé : la surveillance, qui
        // n'attend que ce jeton, tiendrait le processus sans tube jusqu'à l'arrêt pour inactivité, ou pour toujours en service.
        cts.Cancel();

        // Arret normal ou rafale d'echecs (un squatteur du tube peut la provoquer), les baux actifs
        // meurent avec ce processus. Ils sont rendus avant de sortir, sous un delai propre (cts peut etre annule).
        try
        {
            using var rendu = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await watchdog.RendreToutAsync(rendu.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DrDxt.ElevatedHelper: leases not all released on exit: {ex.GetType().Name}.");
        }

        // Laisser la surveillance se dérouler proprement à l'arrêt.
        try { await monitor.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on cancel */ }

        return exitCode;
    }

    // Écrit les diagnostics de la boucle d'acceptation sur la console (stderr/stdout du helper).
    private sealed class ConsoleServiceLoopLog : IServiceLoopLog
    {
        public void Fault(int consecutive, int max, string detail) =>
            Console.Error.WriteLine(
                $"DrDxt.ElevatedHelper: accept error ({consecutive}/{max}): {detail}.");

        public void FailedClosed() =>
            Console.Error.WriteLine(
                "DrDxt.ElevatedHelper: too many consecutive accept faults; stopping (fail-closed).");
    }

    // Fait battre le chien de garde à cadence fixe jusqu'à l'annulation. Un tick ne lève pas (il avale les échecs de
    // retour), mais la boucle reste gardée : une panne ne fait jamais planter l'hôte élevé.
    private static async Task RunLeaseMonitorAsync(LeaseWatchdog watchdog, TimeSpan period, CancellationToken ct,
        Func<CancellationToken, Task> garde,
        Action? apresChaqueTour = null)
    {
        try
        {
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await watchdog.TickAsync(ct).ConfigureAwait(false);
                    await garde(ct).ConfigureAwait(false);
                    apresChaqueTour?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Un mauvais tick ne doit jamais faire planter l'hôte ; le tick suivant réessaie.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static byte[]? ReadSessionKey(string[] args)
    {
        // Installe, la variable d'environnement (HKCU\Environment, ecrivable par le compte) ne choisit pas le mode legacy ;
        // seul un argument de lancement le peut.
        var installe = HelperAuthenticatorFactory.EstInstalle(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
#if DEBUG
        var b64 = ReadArg(args, SessionKeyArg) ?? (installe ? null : Environment.GetEnvironmentVariable(SessionKeyEnv));
#else
        // Le mode legacy des outils de banc n'est pas une surface du produit livré.
        string? b64 = null;
        _ = installe;
#endif
        if (string.IsNullOrWhiteSpace(b64))
            return null;
        try
        {
            var key = Convert.FromBase64String(b64);
            // Une clé trop courte n'est pas une clé de session valide : refuser plutôt qu'affaiblir.
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? ReadArg(string[] args, string prefix)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = a[prefix.Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        return null;
    }

    /// <summary>Le mutex que l'installeur de DrDXT crée pendant une mise à jour ou une désinstallation (LaisserLeHelperRendreSesBaux).</summary>
    internal const string MutexDeLInstalleur = "DrDxtInstalleur";

    /// <summary>Lancé à la demande, le helper s'arrête sur inactivité (<see cref="ArretSurInactivite"/>). En --service
    /// (<paramref name="inactivite"/> nul), il tient la session : il ne s'arrête que quand l'installeur tourne, ses baux rendus,
    /// pour que ses fichiers se remplacent sans arrêt forcé. Un programme du compte qui crée ce mutex ne fait que l'arrêter.</summary>
    internal static bool DoitSArreter(ArretSurInactivite? inactivite, int bauxActifs, Func<int, bool> vivant, Func<bool> installeurEnCours)
        => inactivite is not null ? inactivite.DoitSArreter(bauxActifs, vivant) : bauxActifs == 0 && installeurEnCours();

    private static bool InstalleurEnCours()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexDeLInstalleur, out var m)) return false;
            m.Dispose();
            return true;
        }
        // Existant mais fermé à ce compte : l'installeur élevé tourne bien.
        catch (UnauthorizedAccessException) { return true; }
    }

    // Le client authentifié vit-il encore ? Un processus introuvable est parti.
    private static bool ProcessusVivant(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }
}
