using System.IO;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// ElevatedClientFactory : construit l'IElevatedClient côté App.
//
// Deux chemins :
//   1. Par défaut : lancement à la demande et handshake. L'App trouve
//      DrDxt.ElevatedHelper.exe à côté d'elle et, à la première opération privilégiée,
//      le lance élevé (une seule invite UAC via HelperLauncher), puis lui parle par le
//      named pipe protégé par ACL, en négociant à la connexion une clé éphémère par
//      session (HandshakingElevatedClient). Aucun développeur ne partage de clé à l'avance.
//   2. Forcé (ancien smoke test) : si DRDXT_HELPER_SESSION_KEY (base64, >= 32 octets)
//      est posée, on rend à la place le TypedNamedPipeElevatedClient éprouvé, à clé par
//      variable d'environnement, pour que les outils m2smoke/r2smoke existants marchent
//      sans changement (ils lancent le helper en mode ancien et partagent la clé à l'avance).
//
// Quand rien ne peut donner de canal (exe du helper absent, etc.), on rend un client qui
// échoue proprement et refuse chaque opération (Result.Fail) : l'App ne lève ni ne plante
// jamais sur un helper absent ; une application échoue simplement proprement.
//
// Note : le lancement UAC à la demande est l'étape de dev et de test. L'installeur
// enregistre ensuite le même exe du helper comme service Windows (une seule invite UAC à
// l'installation) sur le même pipe et le même handshake : seul le mécanisme de lancement
// change (un IHelperProcessLauncher qui démarre le service), pas ce canal.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class ElevatedClientFactory
{
    public const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";

    /// <summary>Nom de fichier du helper élevé, copié à côté de l'exe de l'App à la compilation.
    /// Source de verite unique, partagee avec la garde qui authentifie le serveur du tube
    /// cote client : le fichier qu'on lance et le fichier qu'on accepte d'ecouter doivent
    /// etre nommes au meme endroit, sinon les deux derivent.</summary>
    public const string HelperExeName = PipeServerIdentity.HelperExeName;

    /// <summary>Les valeurs HKCU s'écrivent par l'App (jeton de l'utilisateur), le reste par le helper.</summary>
    public static IElevatedClient Create() => new ClientSessionUtilisateur(new ClientDesPoses(CreerVersLeHelper()));

    private static IElevatedClient CreerVersLeHelper()
    {
        // Chemin forcé : une clé partagée à l'avance garde les smoke tests existants en état de marche.
        var key = ReadSessionKey();
        if (key is not null)
        {
            var legacyTransport = new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName);
            return new TypedNamedPipeElevatedClient(key, legacyTransport);
        }

        // Chemin par défaut : lancement à la demande et handshake. On cherche le helper à côté de nous.
        var appExe = TryGetAppExePath();
        var helperExe = TryGetHelperExePath();
        if (appExe is null || helperExe is null)
        {
            // Pas de helper → client qui échoue proprement (chaque opération refusée, pas de plantage).
            return new UnavailableElevatedClient();
        }

        var pipeName = PipeSecurityFactory.DefaultPipeName;
        var spec = new HelperLaunchSpec(helperExe, appExe, pipeName);

        // La réutilisation et la présence du helper se détectent par une tentative complète de
        // connexion et de handshake (pas de sonde de connexion nue à part, qui entrerait en course
        // avec le pipe à instance unique).
        var launcher = new HelperLauncher(
            new RunAsProcessLauncher(),
            new PipeHandshakeAttempt(pipeName));

        // Le client n'accepte de parler qu'au helper qu'on vient de lancer : meme chemin des
        // deux cotes. Un squatteur non privilegie qui aurait pris le nom du tube avant lui est
        // refuse avant le handshake, donc avant d'apprendre ce que DrDXT voulait appliquer.
        var inner = new HandshakingElevatedClient(pipeName, helperExe);

        // Le lancement UAC attend la première opération privilégiée ; on réutilise le helper s'il tourne déjà.
        return new LaunchOnDemandElevatedClient(
            ct => launcher.EnsureRunningAsync(spec, ct), inner);
    }

    /// <summary>Un client qui ne lance jamais le helper : il le reutilise s'il sert deja son
    /// tube (meme garde d'authentification du serveur), sinon il echoue vite (HELPER_UNREACHABLE). Pour les
    /// gestes de fond, comme le rafraichissement de l'habilitation du manifeste : une invite UAC sans geste
    /// de l'utilisateur n'est jamais acceptable.</summary>
    /// <summary>Le dernier essai des acquits refusés, à la fermeture : sans lancer le helper, aucune invite à ce moment-là.</summary>
    public static Task<string?> DernierEssaiDesAcquitsAsync() => new ClientDesPoses(CreerSansLancerVersLeHelper()).DernierEssaiAsync();

    public static IElevatedClient CreateSansLancer() => new ClientSessionUtilisateur(new ClientDesPoses(CreerSansLancerVersLeHelper()));

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

    /// <summary>La cle d'environnement vient de HKCU\Environment, ecrivable par tout programme du
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

    // L'exe de l'App en cours (l'image appelante de confiance que le helper autorise).
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

    // L'exe du helper posé à côté de celui de l'App (copié par ProjectReference, voir le csproj).
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

    /// <summary>Client qui échoue proprement, utilisé quand aucun canal vers le helper n'est
    /// possible : il refuse chaque opération, pour qu'une application échoue proprement au lieu de planter.</summary>
    private sealed class UnavailableElevatedClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.IoFailure,
                "Elevated helper is not available (could not locate the helper executable)."));
    }
}
