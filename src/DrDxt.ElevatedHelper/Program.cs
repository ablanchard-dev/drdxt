using System.Runtime.Versioning;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;

[assembly: SupportedOSPlatform("windows")]
// 25/09/2026 : processus eleve. Une DLL cherchee par son seul nom passe par le dossier courant
// et le PATH, dont plusieurs dossiers sont modifiables par l'utilisateur : une DLL posee la
// s'executerait en administrateur. Tout ce que ce processus charge vit dans System32.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// Elevated helper host (M5 — handshake-fronted typed gateway; R1 legacy mode kept).
//
// Runs the privileged typed gateway (real executor + OpAllowlist + a real
// process-identity authenticator) on the ACL'd named pipe accept-loop, ELEVATED.
// Fail-closed throughout.
//
// TWO MODES (both authenticate the caller; both deny-by-default):
//   * HANDSHAKE mode (DEFAULT — M5, §9.13): NO pre-shared key. Each connection
//     authenticates the caller and then NEGOTIATES an ephemeral per-session key
//     over the ACL'd pipe (HandshakeAcceptingServer). This is what the App's
//     ElevatedClientFactory + HandshakingElevatedClient use.
//   * LEGACY env-var mode (R1 dev-grade, kept for the m2smoke/r2smoke tools): when
//     --session-key=<b64> / env DRDXT_HELPER_SESSION_KEY is provisioned, runs the
//     TypedPrivilegedServer accept loop keyed by that pre-shared HMAC key so the
//     existing smoke tools keep working unchanged.
//
// PROVISIONING:
//   * Trusted caller image path: --app-path=<full path> or env
//     DRDXT_HELPER_APP_PATH. The authenticator authorizes only this exe by PATH
//     AND SHA-256 (the hash is computed from this path at startup — FIX 2; closes
//     the swapped-exe-at-same-path gap, no code signing needed). No path / an
//     unhashable path → DenyByDefault (every caller denied) so the helper never
//     fails open.
//   * Optional pipe name: --pipe=<name> (defaults to the standard name).
//   * Optional --session-key=<b64> selects the LEGACY pre-shared mode.
//
// NOTE (05-M5 §1 end-state): the on-demand UAC launch is the dev stepping-stone;
// the installer later registers this same exe as a Windows service over the same
// pipe + handshake (one UAC at install). The accept loop below does not change.
// -----------------------------------------------------------------------
internal static class Program
{
    private const string SessionKeyArg = "--session-key=";
    private const string AppPathArg = "--app-path=";
    private const string ReconcilerPathArg = "--reconciler-path=";
    private const string PipeArg = "--pipe=";
    private const string ServiceArg = "--service";
    private const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";
    private const string AppPathEnv = "DRDXT_HELPER_APP_PATH";
    private const string ReconcilerPathEnv = "DRDXT_HELPER_RECONCILER_PATH";

    private static async Task<int> Main(string[] args)
    {
        // 11/09/2026 — la desinstallation : un mode court, sans tube, lance par l'installeur deja eleve
        // (voir ModeDesinstallation). Il passe avant tout le reste : aucun tube n'est ouvert.
        if (args.Any(a => string.Equals(a, ModeDesinstallation.Argument, StringComparison.Ordinal)))
            return ModeDesinstallation.Executer();

        // A pre-shared key selects LEGACY mode; its absence selects HANDSHAKE mode.
        var sessionKey = ReadSessionKey(args);
        var handshakeMode = sessionKey is null;

        // --service selects the long-lived, multi-connection accept loop (the installed
        // Windows-service shape): the loop serves connection after connection over the
        // helper's lifetime via the same per-connection pipeline. Without it the helper
        // keeps the historical "serve until stopped" behaviour (functionally identical —
        // both run the same accept loop — but the flag makes the service intent explicit
        // and is the entry the installer registers).
        var serviceMode = args.Any(a => string.Equals(a, ServiceArg, StringComparison.Ordinal));

        var pipeName = ReadArg(args, PipeArg) ?? PipeSecurityFactory.DefaultPipeName;

        // Authenticator (FIX 2): authorize only the trusted caller images by PATH AND
        // SHA-256 (computed at startup). MULTI-caller: --app-path (the interactive App)
        // AND --reconciler-path (the low-priv reconciler host) are both allowed, each by
        // its own path+hash; an unknown image is denied. HelperAuthenticatorFactory never
        // fails open — no trusted, hashable path → deny-by-default (never allow-all, never
        // silently path-only). Applies to the on-demand handshake flow AND the env-var
        // legacy smoke tools (which pass --app-path).
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

        // Real executor: SAFE non-GPU ops + the SAFE NVAPI DRS surface + the RISKY
        // NVAPI clock-offset / power-limit writes + the RISKY ADLX (AMD) tuning write
        // all perform real driver actions. Only the multi-point GPU fan-curve remains
        // deferred (T6). The native calls are the smoke boundary; on an AMD / no-NVIDIA
        // box every NVAPI action fails clean ("NVIDIA NVAPI unavailable"), and on a
        // non-AMD box every ADLX action fails clean ("AMD ADLX unavailable").
        var executor = new PrivilegedOpExecutor(
            new Win32RegistryActions(),
            new Win32PowerPlanActions(),
            new Win32RestorePointActions(),
            new Win32ServiceActions(),
            new NvApiDrsActions(),
            // RISKY private NVAPI clock-offset + power-limit writes (recipe §A/§B):
            // GET-then-SET, version=sizeof, fail-clean. A struct-size mismatch is
            // rejected by the driver (IncompatibleStructVersion) before any write.
            new NvApiTuningActions(),
            // RISKY ADLX (AMD) tuning writes (T3, the AMD mirror): GET-then-SET over the
            // single verified AdlxNative boundary, default-DENY band re-check, fail-clean.
            new ADLXTuningActions(),
            // M3 lot 2 — the firmware trip (shutdown /r /fw) and its undo (/a /fw). Wired
            // Here and nowhere else: this helper only ever acts on an op the user just
            // triggered from the app, so a restart always has a visible cause. DrDXT writes
            // no firmware variable of its own — Windows owns the OsIndications bit.
            new Win32FirmwareBootActions(),
            // 11/09/2026 — le seul ecrivain du manifeste signe : la cle de la machine vit ici, jamais
            // dans l'App (integrite moyenne) ni dans le service (qui verifie).
            new ManifestCommitActions(),
            // 24/09/2026 — le plafond d'envoi QoS (ActiveStore : efface au redemarrage).
            new Win32NetworkQosActions(),
            // 24/09/2026 — retirer / reposer un element BCD de l'ensemble ferme, relu apres ecriture.
            new Win32BcdActions(),
            // 28/09/2026 : le seul ecrivain du reglage de protection du ping que porte le service de demarrage.
            plafondDuService: DrDxt.Reconciler.Reseau.PlafondEnvoiDuService.ParDefaut(),
            // 08/10/2026 (GPU-OC) : le ventilateur en mode Expert, vers le haut seulement, rendu sous bail.
            ventilateur: new NvmlVentilateurActions(),
            // 10/10/2026 (B1) : la limite de puissance du processeur, relue avant et après, par PawnIO s'il est là.
            cpu: new PawnIoCpuPowerActions());

        // Dead-man's-switch: a held GPU offset / power limit registers a lease whose
        // recorded revert (an existing typed op) fires automatically if the controller
        // stops sending heartbeats. State is in-memory only (no disk persistence). The
        // same allowlisted executor runs the auto-revert.
        var watchdog = new LeaseWatchdog(
            new SystemClock(), new OpAllowlist(), executor, new HelperDiagnosticsLog());

        // Select the accept strategy by mode. HANDSHAKE mode mints an ephemeral key
        // per connection after caller-auth (§9.13); LEGACY mode keys the proven
        // TypedPrivilegedServer with the pre-shared HMAC key (m2smoke/r2smoke compat).
        // Both expose AcceptOneAsync and run the same validated, allowlisted pipeline.
        // SEC1 (08/10/2026) : lancé à la demande, le helper s'arrête après 15 minutes sans connexion authentifiée et sans
        // bail ; une connexion refusée ne compte pas (revue de sécurité du 08/10).
        var inactivite = serviceMode ? null : new ArretSurInactivite(TimeSpan.FromMinutes(15), () => DateTimeOffset.UtcNow);

        Func<CancellationToken, Task> acceptOne;
        if (handshakeMode)
        {
            var hsServer = new HandshakeAcceptingServer(
                authenticator, new CryptoSessionKeyGenerator(), new OpAllowlist(), executor, pipeName, watchdog)
            { ApresAuthentification = pid => inactivite?.ClientAuthentifie(pid) };
            acceptOne = hsServer.AcceptOneAsync;
            Console.Out.WriteLine("DrDxt.ElevatedHelper: handshake mode (per-session key negotiated on connect).");
        }
        else
        {
            var typedServer = new TypedPrivilegedServer(
                sessionKey!, authenticator, new OpAllowlist(), executor, pipeName, watchdog);
            acceptOne = async ct =>
            {
                try { await typedServer.AcceptOneAsync(ct).ConfigureAwait(false); }
                finally { inactivite?.Activite(); }
            };
            Console.Out.WriteLine("DrDxt.ElevatedHelper: legacy pre-shared-key mode (smoke tools).");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };


        // Background monitor: tick the watchdog on a fixed cadence so EXPIRED leases
        // auto-revert. Runs alongside the accept loop; faults are swallowed so a single
        // bad tick never crashes the host.
        var monitor = RunLeaseMonitorAsync(watchdog, TimeSpan.FromSeconds(5), cts.Token, () =>
        {
            if (inactivite?.DoitSArreter(watchdog.NombreDeBaux, ProcessusVivant) != true) return;
            Console.Out.WriteLine("DrDxt.ElevatedHelper: App fermée ou 15 minutes sans connexion, et aucun bail : arrêt.");
            cts.Cancel();
        });

        Console.Out.WriteLine(
            $"DrDxt.ElevatedHelper: listening on pipe '{pipeName}' (elevated{(serviceMode ? ", service mode" : string.Empty)}).");

        // Long-lived, multi-connection accept loop (HelperServiceLoop): each iteration
        // serves exactly ONE connection via the proven per-connection pipeline above
        // (acceptOne) then re-creates the FirstPipeInstance pipe for the next caller. A
        // single bad/garbage connection is logged + tolerated; a sustained fault burst
        // backs off then fails closed. The per-connection protocol is UNTOUCHED — this
        // is purely the outer loop/hardening. The --service flag does not change the loop
        // (the installed service and the on-demand launch share this exact code path); it
        // only labels the intent + is the entry the installer registers.
        var loop = new HelperServiceLoop(
            acceptOne,
            new ConsoleServiceLoopLog(),
            backoff: ct => Task.Delay(TimeSpan.FromMilliseconds(250), ct));
        var exitCode = await loop.RunAsync(cts.Token).ConfigureAwait(false);

        // 26/09/2026 : arret normal ou rafale d'echecs (un squatteur du tube peut la provoquer), les baux actifs
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

        // Let the monitor unwind cleanly on shutdown.
        try { await monitor.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on cancel */ }

        return exitCode;
    }

    // Writes the accept-loop diagnostics to the console (the helper's stderr/stdout).
    private sealed class ConsoleServiceLoopLog : IServiceLoopLog
    {
        public void Fault(int consecutive, int max, string detail) =>
            Console.Error.WriteLine(
                $"DrDxt.ElevatedHelper: accept error ({consecutive}/{max}): {detail}.");

        public void FailedClosed() =>
            Console.Error.WriteLine(
                "DrDxt.ElevatedHelper: too many consecutive accept faults; stopping (fail-closed).");
    }

    // Ticks the watchdog on a fixed cadence until cancelled. A tick never throws
    // (the watchdog swallows revert failures), but the loop is still defensively
    // guarded so a fault can never crash the elevated host.
    private static async Task RunLeaseMonitorAsync(LeaseWatchdog watchdog, TimeSpan period, CancellationToken ct,
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
                    apresChaqueTour?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // A bad tick must never crash the host; the next tick retries.
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
        // 26/09/2026 : installe, la variable d'environnement (HKCU\Environment, ecrivable par le compte) ne choisit
        // plus le mode legacy ; seul un argument de lancement le peut encore.
        var installe = HelperAuthenticatorFactory.EstInstalle(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
#if DEBUG
        var b64 = ReadArg(args, SessionKeyArg) ?? (installe ? null : Environment.GetEnvironmentVariable(SessionKeyEnv));
#else
        // Revue de sécurité du 08/10 : le mode legacy des outils de banc n'est pas une surface du produit livré.
        string? b64 = null;
        _ = installe;
#endif
        if (string.IsNullOrWhiteSpace(b64))
            return null;
        try
        {
            var key = Convert.FromBase64String(b64);
            // A too-short key is not a valid session key — refuse rather than weaken.
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

    // SEC1 (autocontrôle du 08/10/2026) : le client authentifié vit-il encore ? Un processus introuvable est parti.
    private static bool ProcessusVivant(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }
}
