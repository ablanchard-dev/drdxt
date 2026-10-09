using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Detection.Wmi;
using DrDxt.Infrastructure;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using DrDxt.Reconciler.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostServiceCollectionExtensions — the production DI graph for the reconciler host.
//
// Wires the full verify-only reconcile pipeline:
//   * AddDrDxtCore (Serilog + IDiagnosticsLog + ILocalizer),
//   * AddDrDxtReconcilerAdapters (prod probes + reapply mapper/client deps),
//   * the pure policy / mapper / catalog,
//   * the sidecar state store (JsonReconcilerStateStore) under %ProgramData%\DrDxt,
//   * the SIGNED manifest store in VERIFY-only mode (PUBLIC key only, never a signer),
//   * the boot-outcome store + boot-loop guard + the real DesiredStateReconciler,
//   * a LoggingReconcilerNotifier,
//   * the SERVICE-context IElevatedClient = the IN-PROCESS executor (moat Option B),
//   * the host's own tested classes (tick / single-flight / triggers / boot marker),
//   * the Worker as the hosted service.
//
// MOAT WIRING (decision M4/M5, Option B — see M4-M5-moat-wiring-decision.md): the host
// runs as LocalSystem and reapplies the validated tweaks IN-PROCESS at boot (a boot
// service cannot raise an interactive UAC, and the reconcilable surfaces — HKLM /
// power-plan / service — require SYSTEM). It opens NO privileged IPC endpoint; the only
// input that can drive a write is the Ed25519-signature-verified manifest, and every op
// is bounded by the same closed leaf-exact OpAllowlist the elevated helper uses. The
// low-privilege reconciler + helper-broker channel (IElevatedReapplyClient over the
// connect-only handshake) is the retained V2 hardening, not the V1 default.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostServiceCollectionExtensions
{
    /// <summary>L'ordre de confiance ci-dessus, pour une clé à ce chemin. 06/10/2026 : extrait pour que les tests le
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
        // --- Cross-cutting: logging / diagnostics / localization. ---
        services.AddDrDxtCore();

        // --- Production reconciler probes + the re-apply security boundary. ---
        // The WMI driver probe (WmiGpuDriverProbe) needs Detection's IWmiQuery; the host
        // supplies the bare read seam (it does not need the full Detection scan graph).
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));
        services.AddDrDxtReconcilerAdapters();

        // --- Pure policy / catalog (single source of truth for surfaces + locations). ---
        services.TryAddSingleton<IReconcilabilityPolicy, DefaultReconcilabilityPolicy>();
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();

        // --- Injectable UTC clock + delay seams. ---
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IDelay, SystemDelay>();

        // --- Runtime sidecar state store under the admin-write manifest root (§5). ---
        services.TryAddSingleton<IReconcilerStateStore>(
            _ => new JsonReconcilerStateStore(JsonReconcilerStateStore.DefaultPath()));

        // --- VERIFY-only manifest store: PUBLIC key only, never a signer (key custody). ---
        // The admin-write ProgramData storage (same root as the sidecar).
        services.TryAddSingleton<IManifestStorage>(_ => new ProgramDataManifestStorage());
        // The verifier's PUBLIC key, in priority order:
        //   (1) a provisioned manifest-public.key file (the production override path — a
        //       fresh release key dropped under the admin-write root), else
        //   (2) DEBUG/test only — the EMBEDDED V1 public key (ManifestDevKeys) so a
        //       self-contained build VERIFIES real manifests signed by the App's dev
        //       signer, else a FAIL-CLOSED verifier, else
        //   (3) RELEASE — a FAIL-CLOSED verifier: a release never trusts the embedded dev
        //       key, so a dev-key-signed or unsigned manifest is rejected and nothing is
        //       reapplied unless a PROVISIONED production public key is present.
        // The host never fails open: a tampered / foreign-signed manifest always rejects
        // (the verifier holds only OUR public key). This is the manifest key-custody
        // invariant the T21 security gate enforces (no usable dev key drives the moat).
        services.TryAddSingleton<IManifestVerifier>(_ => Verificateur(HostPublicKeyProvider.DefaultPath()));
        // ONE store instance, exposed as both IDesiredStateStore (state) and
        // ISignedManifestReader (the full manifest incl. its bounded-validity ExpiresAt
        // entitlement, Finding 1) so the entitlement gate shares the exact verify path.
        services.TryAddSingleton(sp => new SignedDesiredStateStore(
            sp.GetRequiredService<IManifestStorage>(),
            signer: null, // verify-only host: a NULL signer is the norm (cannot sign).
            sp.GetRequiredService<IManifestVerifier>(),
            sp.GetRequiredService<IReconcilabilityPolicy>(),
            sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IDesiredStateStore>(sp => sp.GetRequiredService<SignedDesiredStateStore>());
        services.TryAddSingleton<ISignedManifestReader>(sp => sp.GetRequiredService<SignedDesiredStateStore>());

        // Le gate de reapplication : ce qui est arme doit se verifier (manifeste signe par la cle de
        // la machine). Fail-closed. DrDXT est gratuit depuis le 23/09/2026 : plus de licence a lire.
        services.TryAddSingleton<IEntitlementGate>(sp => new ManifestEntitlementGate(
            sp.GetRequiredService<ISignedManifestReader>()));

        // --- Boot-loop guard + boot-outcome store. ---
        services.TryAddSingleton<IBootOutcomeStore, ReconcilerStateBootOutcomeStore>();
        services.TryAddSingleton<BootLoopGuard>();

        // --- The notifier (logs via IDiagnosticsLog, i18n via ILocalizer). ---
        services.TryAddSingleton<IReconcilerNotifier, LoggingReconcilerNotifier>();

        // --- The SERVICE-context elevated client: the IN-PROCESS executor (moat wiring
        // decision M4/M5, Option B — see M4-M5-moat-wiring-decision.md). The reconciler host
        // runs as LocalSystem and reapplies validated tweaks at boot without any IPC: there is
        // NO open privileged endpoint to attack. It is driven only by the Ed25519-signature-
        // verified, admin-write-ACL'd manifest (verified upstream by SignedDesiredStateStore,
        // signature before parse, fail-closed) and bounded by the same leaf-exact OpAllowlist
        // the remote helper applies (master §9.13). The NVAPI *and* ADLX (AMD) tuning seams are
        // DISABLED here, so the boot service is structurally incapable of GPU writes on either
        // vendor — the reconciler persists only validated, durable settings (registry /
        // power-plan / service), never volatile GPU offsets. The low-priv reconciler + helper-broker channel
        // (HostElevatedClientFactory.CreateConnectOnly) is retained as the documented V2
        // hardening, not the V1 default. ---
        services.TryAddSingleton<IElevatedClient>(_ =>
        {
            var nvApiDisabled = new DisabledNvApiActions();
            var executor = new PrivilegedOpExecutor(
                new Win32RegistryActions(),
                new Win32PowerPlanActions(),
                new Win32RestorePointActions(),
                new Win32ServiceActions(),
                nvApiDisabled,  // DRS seam: the boot reconciler never tunes the GPU.
                nvApiDisabled,  // NVAPI tuning seam: same — fail-clean by construction.
                new DisabledAdlxActions(), // ADLX (AMD) tuning seam: same, fail-clean by construction.
                // 28/09/2026 : le plafond d'envoi QoS, pour la protection du ping retenue par le joueur. Seul
                // ProtectionDuPingParLeService le demande, pour le jeu retenu et pendant qu'il tourne, sous la
                // meme allowlist ; les politiques vivent en memoire (ActiveStore), un redemarrage les efface.
                qos: new Win32NetworkQosActions());
            // M3 lot 2 — the firmware-boot seam is DELIBERATELY not passed here. The boot
            // reconciler runs unattended, so a reboot it triggered would reach the user as a
            // machine that restarted into the BIOS on its own, with nothing on screen to
            // explain it. Same reasoning as the GPU seams above: this host reconciles state,
            // it never performs an act the user did not just ask for. Omitted (null) rather
            // than stubbed, so any attempt fails LOUDLY instead of silently doing nothing.
            return new InProcessElevatedClient(new OpAllowlist(), executor);
        });

        // --- The concrete reconciler (registered both as itself and via IReconciler). ---
        services.TryAddSingleton<DesiredStateReconciler>();
        services.TryAddSingleton<IReconciler>(sp => sp.GetRequiredService<DesiredStateReconciler>());

        // --- The host's own tested classes. ---
        services.TryAddSingleton<ReconcileTickService>();
        services.TryAddSingleton(sp =>
            new SingleFlightReconcile(sp.GetRequiredService<ReconcileTickService>().RunTickAsync));
        services.TryAddSingleton<FeatureUpdateDetector>();
        services.TryAddSingleton<IUpdateSignal, EventLogUpdateSignal>();
        services.TryAddSingleton<FeatureUpdateTrigger>();
        services.TryAddSingleton<BootSessionMarker>();
        services.TryAddSingleton(_ => HostTimingOptions.Default);

        // --- The hosted Worker (thin shell). ---
        services.AddHostedService<Worker>();

        // 28/09/2026 : la protection du ping sans DrDXT ouvert. Lit le reglage que le helper a ecrit sous
        // %ProgramData%\DrDxt (jamais %LOCALAPPDATA%), rien tant que le joueur n'a rien retenu.
        services.TryAddSingleton(sp => new DrDxt.Reconciler.Reseau.ProtectionDuPingParLeService(
            sp.GetRequiredService<IElevatedClient>(),
            DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut().Lire,
            JeuEnCours,
            sp.GetService<IDiagnosticsLog>()));
        services.AddHostedService(sp => new ProtectionDuPingWorker(
            sp.GetRequiredService<DrDxt.Reconciler.Reseau.ProtectionDuPingParLeService>(), ProtectionDuPingWorker.BattementParDefaut));

        return services;
    }

    /// <summary>Le jeu retenu tourne-t-il, dans n'importe quelle session ? Le service est en session 0 : il voit
    /// les processus de toutes les sessions par leur nom.</summary>
    internal static bool JeuEnCours(string exe)
    {
        var processus = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        foreach (var p in processus) p.Dispose();
        return processus.Length > 0;
    }
}

/// <summary>A verifier that fails every check — wired only when no public key is
/// provisioned, so the host is FAIL-CLOSED (it cannot verify ⇒ it never reconciles an
/// unverified manifest). It never throws.</summary>
internal sealed class FailClosedManifestVerifier : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => false;
}
