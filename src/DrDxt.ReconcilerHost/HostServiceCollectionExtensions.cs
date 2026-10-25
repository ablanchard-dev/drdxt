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

// -----------------------------------------------------------------------
// HostServiceCollectionExtensions : le graphe DI de production de l'hôte réconciliateur.
//
// Branche tout le pipeline de réconciliation en vérification seule :
//   * AddDrDxtCore (Serilog + IDiagnosticsLog + ILocalizer),
//   * AddDrDxtReconcilerAdapters (sondes de production + dépendances du mappeur / client de réapplication),
//   * la politique / le mappeur / le catalogue purs,
//   * le store d'état du sidecar (JsonReconcilerStateStore) sous %ProgramData%\DrDxt,
//   * le store du manifeste signé en vérification seule (clé publique seule, jamais de signataire),
//   * le store d'issue des démarrages + la garde anti-boucle + le vrai DesiredStateReconciler,
//   * un LoggingReconcilerNotifier,
//   * l'IElevatedClient du contexte service = l'exécuteur en processus (moat option B),
//   * les classes testées de l'hôte (tour / vol unique / déclencheurs / marqueur de démarrage),
//   * le Worker comme service hébergé.
//
// Branchement du moat (option B) : l'hôte tourne en
// LocalSystem et réapplique les leviers validés en processus au démarrage (un service de démarrage
// ne peut pas ouvrir d'UAC interactive, et les surfaces réconciliables, HKLM / plan
// d'alimentation / service, exigent SYSTEM). Il n'ouvre aucun point d'entrée IPC privilégié ; la
// seule entrée qui puisse mener une écriture est le manifeste vérifié par signature Ed25519, et
// chaque op est bornée par la même OpAllowlist fermée, à la feuille près, que le helper élevé. Le
// canal réconciliateur peu privilégié + courtier helper (IElevatedReapplyClient sur le handshake
// en connexion seule) est le durcissement V2 retenu, pas le défaut V1.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostServiceCollectionExtensions
{
    /// <summary>L'ordre de confiance ci-dessus, pour une clé à ce chemin. Extrait pour que les tests le
    /// jugent sans lire le vrai %ProgramData% (une machine où DrDXT a déjà signé un manifeste y a sa clé).</summary>
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
        // La sonde WMI du pilote (WmiGpuDriverProbe) a besoin de l'IWmiQuery de Detection ; l'hôte fournit
        // la couture de lecture nue (il n'a pas besoin de tout le graphe d'analyse de Detection).
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
        // La clé publique du vérificateur, par ordre de priorité :
        //   (1) un fichier manifest-public.key fourni (le chemin de surcharge de production : une clé
        //       neuve déposée sous la racine en écriture admin), sinon
        //   (2) Debug / test seulement : la clé publique V1 embarquée (ManifestDevKeys), pour qu'un build
        //       autonome vérifie de vrais manifestes signés par le signataire de dev de l'App, sinon un
        //       vérificateur fail-closed, sinon
        //   (3) Release : un vérificateur fail-closed. Un build Release ne fait jamais confiance à la clé
        //       de dev embarquée : un manifeste signé par la clé de dev ou non signé est rejeté et rien
        //       n'est réappliqué sans clé publique de production fournie.
        // L'hôte ne s'ouvre jamais par défaut : un manifeste altéré ou signé par une autre clé est
        // toujours rejeté (le vérificateur ne tient que notre clé publique). C'est l'invariant de garde de
        // la clé du manifeste qu'impose la barrière de sécurité (aucune clé de dev utilisable ne mène le moat).
        services.TryAddSingleton<IManifestVerifier>(_ => new VerificateurRelu(HostPublicKeyProvider.DefaultPath(), Verificateur));
        // Une seule instance de store, exposée à la fois comme IDesiredStateStore (état) et
        // ISignedManifestReader (tout le manifeste vérifié), pour que la barrière de réapplication
        // partage exactement le même chemin de vérification.
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

        // --- Le client élevé du contexte service : l'exécuteur en processus (branchement du moat,
        // option B). L'hôte réconciliateur tourne en LocalSystem et
        // réapplique au démarrage des leviers validés sans aucun IPC : aucun point d'entrée privilégié
        // ouvert à attaquer. Il n'est mené que par le manifeste vérifié par signature Ed25519, dont l'ACL
        // réserve l'écriture aux admins (vérifié en amont par SignedDesiredStateStore, signature avant
        // analyse, fail-closed), et borné par la même OpAllowlist à la feuille près que le helper distant.
        // Les coutures de réglage NVAPI *et* ADLX (AMD) sont désactivées ici : le service de démarrage est,
        // par construction, incapable d'écrire le GPU chez l'un comme l'autre constructeur ; le
        // réconciliateur ne fait persister que des réglages validés et durables (registre / plan
        // d'alimentation / service), jamais de décalages GPU volatils. Le canal réconciliateur peu
        // privilégié + courtier helper (HostElevatedClientFactory.CreateConnectOnly) est gardé comme
        // durcissement V2 documenté, pas comme défaut V1. ---
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
                // Le plafond d'envoi QoS, pour la protection du ping retenue par le joueur. Seul
                // ProtectionDuPingParLeService le demande, pour le jeu retenu et pendant qu'il tourne, sous la
                // meme allowlist ; les politiques vivent en memoire (ActiveStore), un redemarrage les efface.
                qos: new Win32NetworkQosActions());
            // La couture de démarrage firmware n'est exprès pas passée ici. Le réconciliateur de démarrage
            // tourne sans surveillance : un redémarrage qu'il déclencherait arriverait chez l'utilisateur
            // comme une machine repartie seule dans le BIOS, sans rien à l'écran pour l'expliquer. Même
            // raisonnement que pour les coutures GPU ci-dessus : cet hôte réconcilie un état, il n'accomplit
            // jamais un geste que l'utilisateur vient de ne pas demander. Omise (null) plutôt que simulée :
            // toute tentative échoue bruyamment au lieu de ne rien faire en silence.
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

/// <summary>Un vérificateur qui fait échouer chaque contrôle : branché seulement quand aucune clé
/// publique n'est fournie, pour que l'hôte soit fail-closed (il ne peut pas vérifier ⇒ il ne
/// réconcilie jamais un manifeste non vérifié). Ne lève jamais.</summary>
/// <summary>
/// Le vérificateur du service, qui relit la clé quand une vérification échoue. Le service démarre à l'installation, avant
/// que le helper ait créé la clé au premier commit ; une reprise du manifeste en crée une neuve. Lue une seule fois au
/// démarrage, la clé refusait tout manifeste jusqu'au redémarrage du PC. La relecture passe par le même chargeur, avec les
/// mêmes contrôles de confiance du fichier ; un manifeste que la clé relue ne vérifie pas reste refusé.
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

internal sealed class FailClosedManifestVerifier : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => false;
}
