using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Detection.Wmi;
using DrDxt.Infrastructure;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using DrDxt.Reconciler.Manifest;
using DrDxt.ElevatedHelper.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

// Le graphe DI de production de l'hôte réconciliateur, en vérification seule (sections ci-dessous). Moat, option B :
// l'hôte tourne en LocalSystem et réapplique en processus au démarrage (un service de démarrage ne peut pas ouvrir
// d'UAC, et HKLM / plan d'alimentation / service exigent SYSTEM). Aucun point d'entrée IPC privilégié : seul le
// manifeste Ed25519 vérifié mène une écriture, chaque op bornée par la même OpAllowlist fermée que le helper élevé.
// Le canal peu privilégié + courtier helper (IElevatedReapplyClient sur le handshake en connexion seule) est le
// durcissement V2 retenu, pas le défaut V1.
[SupportedOSPlatform("windows")]
public static class HostServiceCollectionExtensions
{
    /// <summary>
    /// La clé publique du vérificateur, pour une clé à ce chemin, par ordre de priorité :
    ///   (1) un fichier manifest-public.key fourni (la surcharge de production, déposée sous la racine en écriture admin) ;
    ///   (2) Debug / test seulement : la clé publique V1 embarquée (ManifestDevKeys), pour qu'un build autonome vérifie
    ///       les manifestes du signataire de dev de l'App, sinon fail-closed ;
    ///   (3) Release : fail-closed. Jamais la clé de dev : un manifeste signé par elle ou non signé est rejeté, et rien
    ///       n'est réappliqué sans clé publique de production.
    /// Un manifeste altéré ou signé par une autre clé est toujours rejeté : aucune clé de dev utilisable ne mène le moat.
    /// Extrait pour que les tests le jugent sans lire le vrai %ProgramData% (où DrDXT a peut-être déjà sa clé).
    /// </summary>
    public static IManifestVerifier Verificateur(string cheminCle)
    {
        var provisioned = HostPublicKeyProvider.LoadVerifier(cheminCle);
        if (provisioned.IsOk)
            return provisioned.Value!;

#if DEBUG
        var embedded = ManifestDevKeys.CreateVerifier();
        return embedded.IsOk ? embedded.Value! : new FailClosedManifestVerifier();
#else
        return new FailClosedManifestVerifier();
#endif
    }

    public static IServiceCollection AddDrDxtReconcilerHost(this IServiceCollection services)
    {
        // --- Transversal : journalisation / diagnostics / localisation. ---
        services.AddDrDxtCore();

        // --- Sondes de production du réconciliateur + frontière de sécurité de la réapplication. ---
        // WmiGpuDriverProbe a besoin de l'IWmiQuery de Detection : la couture de lecture nue, sans le graphe d'analyse.
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));
        services.AddDrDxtReconcilerAdapters();

        // --- Politique / catalogue purs (source unique des surfaces + emplacements). ---
        services.TryAddSingleton<IReconcilabilityPolicy, DefaultReconcilabilityPolicy>();
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();

        // --- Coutures injectables d'horloge UTC + d'attente. ---
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IDelay, SystemDelay>();

        // --- Store d'état du sidecar d'exécution sous la racine du manifeste en écriture admin. ---
        services.TryAddSingleton<IReconcilerStateStore>(
            sp => new JsonReconcilerStateStore(JsonReconcilerStateStore.DefaultPath(), journal: sp.GetService<IDiagnosticsLog>()));

        // --- Store du manifeste en vérification seule : clé publique seule, jamais de signataire (garde de la clé). ---
        // Le stockage ProgramData en écriture admin (même racine que le sidecar).
        services.TryAddSingleton<IManifestStorage>(_ => new ProgramDataManifestStorage());
        // La clé publique, dans l'ordre de confiance de Verificateur, relue si une vérification échoue.
        services.TryAddSingleton<IManifestVerifier>(_ => new VerificateurRelu(HostPublicKeyProvider.DefaultPath(), Verificateur));
        // Une seule instance, exposée comme IDesiredStateStore (état) et ISignedManifestReader (tout le manifeste) : la
        // barrière de réapplication partage le même chemin de vérification.
        services.TryAddSingleton(sp => new SignedDesiredStateStore(
            sp.GetRequiredService<IManifestStorage>(),
            signer: null, // hôte en vérification seule : un signataire null est la norme (ne peut pas signer).
            sp.GetRequiredService<IManifestVerifier>(),
            sp.GetRequiredService<IReconcilabilityPolicy>(),
            sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IDesiredStateStore>(sp => sp.GetRequiredService<SignedDesiredStateStore>());
        services.TryAddSingleton<ISignedManifestReader>(sp => sp.GetRequiredService<SignedDesiredStateStore>());

        // --- Garde anti-boucle de démarrage + store d'issue des démarrages. ---
        services.TryAddSingleton<IBootOutcomeStore, ReconcilerStateBootOutcomeStore>();
        services.TryAddSingleton<BootLoopGuard>();

        // --- Le notificateur (journalise via IDiagnosticsLog, i18n via ILocalizer). ---
        services.TryAddSingleton<IReconcilerNotifier, LoggingReconcilerNotifier>();

        // --- Le client élevé du contexte service : l'exécuteur en processus (moat, option B). Aucun IPC ; mené par le seul
        // manifeste Ed25519 dont l'ACL réserve l'écriture aux admins (SignedDesiredStateStore : signature avant analyse,
        // fail-closed), borné par la même OpAllowlist que le helper. NVAPI *et* ADLX (AMD) désactivés : ce service ne
        // peut pas écrire le GPU, il ne fait persister que du durable (registre / plan d'alimentation / service). Le
        // canal peu privilégié + courtier helper (HostElevatedClientFactory.CreateConnectOnly) reste le durcissement V2. ---
        // Le journal d'écriture anticipée, dans la racine réparée d'abord. Préenregistré par un test, il n'est jamais ouvert.
        services.TryAddSingleton(_ => new JournalDuService(DrDxt.ElevatedHelper.Journal.JournalDuHelper.Ouvrir(
            ProgramDataManifestStorage.DefaultRoot(), TrustedManifestRoot.EnsureCreatedHardened)));
        services.TryAddSingleton<LectureDesCiblesWin32>();
        services.TryAddSingleton<DrDxt.Reconciler.Desinstallation.ILectureDesCibles>(sp => sp.GetRequiredService<LectureDesCiblesWin32>());
        services.TryAddSingleton<IElevatedClient>(sp =>
        {
            var nvApiDisabled = new DisabledNvApiActions();
            var executor = new PrivilegedOpExecutor(
                new Win32RegistryActions(),
                new Win32PowerPlanActions(),
                new Win32RestorePointActions(),
                new Win32ServiceActions(),
                nvApiDisabled,  // couture DRS : le réconciliateur de démarrage ne règle jamais le GPU.
                nvApiDisabled,  // couture de réglage NVAPI : idem, échec propre par construction.
                new DisabledAdlxActions(), // couture de réglage ADLX (AMD) : idem, échec propre par construction.
                // Le plafond d'envoi QoS de la protection du ping : seul ProtectionDuPingParLeService le demande, pour
                // le jeu retenu pendant qu'il tourne ; politiques en memoire (ActiveStore), effacees au redemarrage.
                qos: new Win32NetworkQosActions());
            // Pas de couture de démarrage firmware, exprès : sans surveillance, un redémarrage arriverait comme une
            // machine repartie seule dans le BIOS. Cet hôte réconcilie un état, jamais un geste non demandé. Omise
            // (null) plutôt que simulée : une tentative échoue bruyamment.
            return new InProcessElevatedClient(new OpAllowlist(), DrDxt.ElevatedHelper.Journal.JournalDuHelper.Decorer(executor,
                sp.GetRequiredService<JournalDuService>().Journal, sp.GetRequiredService<LectureDesCiblesWin32>()));
        });

        // --- Le réconciliateur concret (enregistré pour lui-même et via IReconciler). ---
        // La pause du maintien : le service la lit, seul le helper élevé l'écrit.
        services.TryAddSingleton(_ => PauseDuMaintien.ParDefaut());
        services.TryAddSingleton<DesiredStateReconciler>();
        services.TryAddSingleton<IReconciler>(sp => sp.GetRequiredService<DesiredStateReconciler>());

        // --- Les classes testées propres à l'hôte. ---
        services.TryAddSingleton<ReconcileTickService>();
        services.TryAddSingleton(sp =>
            new SingleFlightReconcile(sp.GetRequiredService<ReconcileTickService>().RunTickAsync));
        services.TryAddSingleton<FeatureUpdateDetector>();
        services.TryAddSingleton<IUpdateSignal, EventLogUpdateSignal>();
        services.TryAddSingleton<FeatureUpdateTrigger>();
        services.TryAddSingleton<BootSessionMarker>();
        services.TryAddSingleton(_ => HostTimingOptions.Default);

        // --- Le rejeu du journal d'abord (StartAsync attendu avant le service suivant), puis le Worker (coquille mince). ---
        services.TryAddSingleton(sp => new RejeuAuDemarrage(sp.GetRequiredService<JournalDuService>(),
            sp.GetRequiredService<DrDxt.Reconciler.Desinstallation.ILectureDesCibles>(), sp.GetRequiredService<IElevatedClient>(),
            sp.GetService<IDiagnosticsLog>(),
            rapport => RapportDuRejeu.Ecrire(ProgramDataManifestStorage.DefaultRoot(), RapportDuRejeu.Service, rapport, DateTimeOffset.UtcNow,
                (chemin, octets) => TrustedManifestRoot.WriteTrustedFile(chemin, octets), RapportDuRejeu.DeConfiance), delai: sp.GetRequiredService<IDelay>()));
        services.AddHostedService(sp => sp.GetRequiredService<RejeuAuDemarrage>());
        services.TryAddSingleton(sp => new FinDuRejeu(() => sp.GetRequiredService<RejeuAuDemarrage>().Differe));
        services.AddHostedService<Worker>();

        // La protection du ping sans DrDXT ouvert. Lit le reglage que le helper a ecrit sous
        // %ProgramData%\DrDxt (jamais %LOCALAPPDATA%), rien tant que le joueur n'a rien retenu.
        services.TryAddSingleton(sp => new DrDxt.Reconciler.Reseau.ProtectionDuPingParLeService(
            sp.GetRequiredService<IElevatedClient>(),
            DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut().Lire,
            JeuEnCours,
            sp.GetService<IDiagnosticsLog>()));
        services.AddHostedService(sp => new ProtectionDuPingWorker(
            sp.GetRequiredService<DrDxt.Reconciler.Reseau.ProtectionDuPingParLeService>(), ProtectionDuPingWorker.BattementParDefaut));

        // L'état du processeur servi en lecture seule à l'App sans droits (tube sans entrée, PawnIO
        // fermé sans demande). Rien n'est lu tant que personne ne se connecte.
        services.AddHostedService<ServeurDuProcesseur>();

        // Le témoin d'incident : ce que Windows a noté juste avant un plantage, lu et gardé sans que le joueur lance rien.
        services.AddHostedService<TemoinDIncidentWorker>();

        return services;
    }

    /// <summary>Le jeu retenu tourne-t-il dans la session du joueur, celle de la console ? Le service est en session 0 et
    /// voit les processus de toutes les sessions par leur nom : un processus du même nom lancé par un autre compte, ou en
    /// session 0, allumerait le plafond de toute la machine. Il ne compte pas.</summary>
    internal static bool JeuEnCours(string exe)
    {
        var processus = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            var lus = new List<(string, int)>();
            foreach (var p in processus)
                try { lus.Add((p.ProcessName, p.SessionId)); }
                catch (Exception) { /* processus fini entre-temps : il ne compte pas */ }
            return JeuEnCours(exe, lus, SessionDuJoueur(SessionsLues(), WTSGetActiveConsoleSessionId()));
        }
        finally { foreach (var p in processus) p.Dispose(); }
    }

    /// <summary>Pur : un processus du nom du jeu, dans la session du joueur (jamais la session 0 des services). Sans
    /// session à la console (<paramref name="sessionDuJoueur"/> null), aucun.</summary>
    internal static bool JeuEnCours(string exe, IEnumerable<(string Nom, int Session)> processus, int? sessionDuJoueur)
        => sessionDuJoueur is > 0 and var session
           && processus.Any(p => p.Session == session
               && string.Equals(p.Nom, Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase));

    /// <summary>Pur : la session du joueur. La seule session active hors session 0 (la console, ou le Bureau à distance quand le
    /// joueur s'y connecte) ; sans session active unique, celle de la console ; aucune, null.</summary>
    internal static int? SessionDuJoueur(IReadOnlyList<(int Id, bool Active)> sessions, uint console)
    {
        var actives = sessions.Where(s => s.Active && s.Id > 0).Select(s => s.Id).Distinct().ToList();
        if (actives.Count == 1) return actives[0];
        return console is 0 or uint.MaxValue ? null : (int)console;
    }

    // Les sessions de la machine et leur état (WTSEnumerateSessions) ; illisibles, aucune : la console décide.
    private static IReadOnlyList<(int Id, bool Active)> SessionsLues()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var tableau, out var nombre)) return [];
        try
        {
            var taille = System.Runtime.InteropServices.Marshal.SizeOf<InfoDeSession>();
            var sessions = new List<(int, bool)>(nombre);
            for (var i = 0; i < nombre; i++)
            {
                var s = System.Runtime.InteropServices.Marshal.PtrToStructure<InfoDeSession>(tableau + i * taille);
                sessions.Add((s.Id, s.Etat == 0)); // WTSActive
            }
            return sessions;
        }
        finally { WTSFreeMemory(tableau); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InfoDeSession
    {
        public int Id;
        public IntPtr NomDeStation;
        public int Etat;
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr serveur, int reserve, int version, out IntPtr sessions, out int nombre);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memoire);

    // kernel32 (Microsoft Learn, WTSGetActiveConsoleSessionId) : 0xFFFFFFFF quand aucune session n'est à la console.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

/// <summary>
/// Le vérificateur du service, qui relit la clé quand une vérification échoue : le service démarre à l'installation, avant
/// que le helper crée la clé au premier commit, et une reprise du manifeste en crée une neuve ; lue une seule fois, elle
/// refuserait tout jusqu'au redémarrage du PC. Même chargeur, mêmes contrôles de confiance du fichier ; un manifeste que la
/// clé relue ne vérifie pas reste refusé.
/// </summary>
internal sealed class VerificateurRelu(string cheminCle, Func<string, IManifestVerifier> charger) : IManifestVerifier
{
    private IManifestVerifier _courant = charger(cheminCle);

    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
    {
        if (_courant.Verify(canonicalBytes, signature)) return true;
        var relu = charger(cheminCle);
        if (!relu.Verify(canonicalBytes, signature)) return false;
        _courant = relu;
        return true;
    }
}

/// <summary>Un vérificateur qui fait échouer chaque contrôle : branché seulement quand aucune clé publique n'est fournie,
/// pour que l'hôte soit fail-closed (il ne réconcilie jamais un manifeste non vérifié). Ne lève jamais.</summary>
internal sealed class FailClosedManifestVerifier : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => false;
}
