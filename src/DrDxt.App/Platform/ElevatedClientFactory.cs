using System.IO;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// ElevatedClientFactory — builds the App-side IElevatedClient.
//
// TWO PATHS:
//   1. DEFAULT (M5, §9.13): launch-on-demand + handshake. The App locates
//      DrDxt.ElevatedHelper.exe next to itself, and on the first privileged op
//      launches it ELEVATED (one interactive UAC via HelperLauncher), then talks
//      to it over the ACL'd named pipe, NEGOTIATING an ephemeral per-session key
//      on connect (HandshakingElevatedClient). No dev pre-shares a key.
//   2. OVERRIDE (legacy R1 smoke): if DRDXT_HELPER_SESSION_KEY (base64, >=32 bytes)
//      is set, the proven env-var-keyed TypedNamedPipeElevatedClient is returned
//      instead, so the existing m2smoke/r2smoke tools keep working unchanged
//      (they launch the helper in legacy mode and pre-share the key).
//
// When nothing can yield a channel (helper exe missing, etc.) a fail-clean client
// is returned that refuses every op (Result.Fail) — the App never throws/crashes
// on a missing helper; an apply simply fails clean.
//
// NOTE (05-M5 §1 end-state): the on-demand UAC launch is the dev/test stepping-
// stone. The installer later registers the same helper exe as a Windows service
// (one UAC at install) over the same pipe + handshake — only the launch mechanism
// changes (a service-start IHelperProcessLauncher), not this channel.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class ElevatedClientFactory
{
    public const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";

    /// <summary>File name of the elevated helper, copied next to the App exe at build.
    /// Source de verite unique, partagee avec la garde qui AUTHENTIFIE le serveur du tube
    /// cote client : le fichier qu'on lance et le fichier qu'on accepte d'ecouter doivent
    /// etre nommes au meme endroit, sinon les deux derivent.</summary>
    public const string HelperExeName = PipeServerIdentity.HelperExeName;

    /// <summary>SEC2 (08/10/2026) : les valeurs HKCU s'écrivent par l'App (jeton de l'utilisateur), le reste par le helper.</summary>
    public static IElevatedClient Create() => new ClientSessionUtilisateur(CreerVersLeHelper());

    private static IElevatedClient CreerVersLeHelper()
    {
        // OVERRIDE path: a pre-shared key keeps the existing smoke tools working.
        var key = ReadSessionKey();
        if (key is not null)
        {
            var legacyTransport = new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName);
            return new TypedNamedPipeElevatedClient(key, legacyTransport);
        }

        // DEFAULT path: launch-on-demand + handshake. Locate the helper next to us.
        var appExe = TryGetAppExePath();
        var helperExe = TryGetHelperExePath();
        if (appExe is null || helperExe is null)
        {
            // No helper available → fail-clean client (every op refused, no crash).
            return new UnavailableElevatedClient();
        }

        var pipeName = PipeSecurityFactory.DefaultPipeName;
        var spec = new HelperLaunchSpec(helperExe, appExe, pipeName);

        // FIX 1: reuse/liveness is detected by a COMPLETE connect+handshake attempt
        // (no separate bare-connect probe that raced the single-instance pipe).
        var launcher = new HelperLauncher(
            new RunAsProcessLauncher(),
            new PipeHandshakeAttempt(pipeName));

        // Le client n'accepte de parler qu'au helper qu'on vient de LANCER : meme chemin des
        // deux cotes. Un squatteur non privilegie qui aurait pris le nom du tube avant lui est
        // refuse avant le handshake, donc avant d'apprendre ce que DrDXT voulait appliquer.
        var inner = new HandshakingElevatedClient(pipeName, helperExe);

        // Defer the UAC launch to the first privileged op; reuse if already running.
        return new LaunchOnDemandElevatedClient(
            ct => launcher.EnsureRunningAsync(spec, ct), inner);
    }

    /// <summary>11/09/2026 — un client qui ne lance jamais le helper : il le reutilise s'il sert deja son
    /// tube (meme garde d'authentification du serveur), sinon il echoue vite (HELPER_UNREACHABLE). Pour les
    /// gestes de fond, comme le rafraichissement de l'habilitation du manifeste : une invite UAC sans geste
    /// de l'utilisateur n'est jamais acceptable.</summary>
    public static IElevatedClient CreateSansLancer() => new ClientSessionUtilisateur(CreerSansLancerVersLeHelper());

    private static IElevatedClient CreerSansLancerVersLeHelper()
    {
        var key = ReadSessionKey();
        if (key is not null)
            return new TypedNamedPipeElevatedClient(key, new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName));

        var helperExe = TryGetHelperExePath();
        return helperExe is null
            ? new UnavailableElevatedClient()
            : new HandshakingElevatedClient(PipeSecurityFactory.DefaultPipeName, helperExe, connectTimeoutMs: 500);
    }

    /// <summary>26/09/2026 — la cle d'environnement vient de HKCU\Environment, ecrivable par tout programme du
    /// compte, et elle selectionne le client legacy, qui ne verifie pas le serveur du tube. Installee sous
    /// Program Files, l'App l'ignore et passe toujours par la poignee de main ; les outils de developpement
    /// (lances hors installation) la gardent.</summary>
    internal static bool CleDEnvironnementPermise(string? processus, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(processus) || string.IsNullOrWhiteSpace(programFiles)) return true;
        try
        {
            var dossier = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar;
            return !Path.GetFullPath(processus).StartsWith(dossier, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static byte[]? ReadSessionKey()
    {
        if (!CleDEnvironnementPermise(Environment.ProcessPath,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
            return null;
        var b64 = Environment.GetEnvironmentVariable(SessionKeyEnv);
        if (string.IsNullOrWhiteSpace(b64))
            return null;
        try
        {
            var key = Convert.FromBase64String(b64);
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // The running App's own exe (the trusted caller image the helper authorizes).
    private static string? TryGetAppExePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    // The helper exe sitting next to the App exe (ProjectReference copy — see csproj).
    private static string? TryGetHelperExePath()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir))
                return null;
            var candidate = Path.Combine(baseDir, HelperExeName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fail-clean client used when no helper channel can be obtained:
    /// refuses every op so an apply fails clean rather than crashing.</summary>
    private sealed class UnavailableElevatedClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.IoFailure,
                "Elevated helper is not available (could not locate the helper executable)."));
    }
}
