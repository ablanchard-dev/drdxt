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
//   2. Forcé (outils de banc, build DEBUG hors Program Files seulement) : si DRDXT_HELPER_SESSION_KEY (base64, >= 32 octets)
//      est posée, on rend à la place le TypedNamedPipeElevatedClient éprouvé, à clé par
//      variable d'environnement, pour que les outils m2smoke/r2smoke existants marchent
//      sans changement (ils lancent le helper en mode ancien et partagent la clé à l'avance).
//
// Quand rien ne peut donner de canal (exe du helper absent, etc.), on rend un client qui
// échoue proprement et refuse chaque opération (Result.Fail) : l'App ne lève ni ne plante
// jamais sur un helper absent ; une application échoue simplement proprement.
//
// Le helper n'est pas un service : il est lancé à la demande, à chaque session. Le service de
// démarrage (DrDxt.ReconcilerHost) est un autre exécutable, qui exécute ses ops dans son propre
// processus (InProcessElevatedClient) sans passer par ce canal.
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
    public static IElevatedClient Create()
        => Create(CompteDuJoueur.Standard(), DrDxt.ElevatedHelper.ModeDesinstallation.MemeCompteQueLaSession());

    /// <param name="compteStandard">Compte hors des Administrateurs : ce qui passe par le helper échoue tout de suite, avec sa
    /// raison, sans invite ; les valeurs du compte (HKCU) s'écrivent encore par l'App.</param>
    /// <param name="memeCompteQueLaSession">Faux quand l'App tourne sous un autre compte que celui de la session
    /// (« Exécuter en tant qu'administrateur » depuis un compte standard) : son HKCU est la ruche de cet autre compte, les
    /// valeurs du compte y sont refusées plutôt qu'écrites et relues là en disant « posé ».</param>
    internal static IElevatedClient Create(bool compteStandard, bool memeCompteQueLaSession = true)
        => DrDxt.ElevatedHelper.ModeDesinstallation.GarderLaRucheDuJoueur(
            Monter(compteStandard ? new CompteStandardClient() : CreerVersLeHelper(), JournalDuCompte()), memeCompteQueLaSession);

    /// <summary>Le journal d'écriture anticipée des valeurs de la session (HKCU), que l'App écrit elle-même : dans le dossier du
    /// compte, rejoué par l'App à son lancement (RejeuDuJournalDuCompte).</summary>
    public static DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee JournalDuCompte()
        => DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee.DuCompte(DossierDonnees.Chemin("jea-session.json"));

    /// <summary>Le montage de chaque client de l'App : les valeurs de la session dans l'App, sous le journal du compte ; le reste
    /// au helper, sous le marquage des poses. Les tests le montent avec un faux registre.</summary>
    internal static IElevatedClient Monter(IElevatedClient versLeHelper, DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee journalDuCompte,
        DrDxt.ElevatedHelper.Execution.IRegistryActions? registre = null, Func<PrivilegedOp, Func<Result<string?>>?>? lecteur = null,
        Func<DrDxt.ElevatedHelper.Journal.AppelantDuJournal?>? moi = null)
        => new ClientSessionUtilisateur(new ClientDesPoses(versLeHelper), registre, journalDuCompte, lecteur, moi);

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

    /// <summary>Le dernier essai des acquits refusés, à la fermeture : sans lancer le helper, aucune invite à ce moment-là.</summary>
    public static async Task<string?> DernierEssaiDesAcquitsAsync()
    {
        // Ceux du journal du compte d'abord : locaux, ils ne demandent pas le helper.
        var locaux = ClientSessionUtilisateur.ReprendreLesAcquitsLocaux();
        var helper = await new ClientDesPoses(CreerSansLancerVersLeHelper()).DernierEssaiAsync().ConfigureAwait(false);
        return helper ?? (locaux ? null : Docteur.T("JEA.AcquitRefuse"));
    }

    /// <summary>Un client qui ne lance jamais le helper : il le reutilise s'il sert deja son
    /// tube (meme garde d'authentification du serveur), sinon il echoue vite (HELPER_UNREACHABLE). Pour les
    /// gestes de fond, comme le rafraichissement de l'habilitation du manifeste : une invite UAC sans geste
    /// de l'utilisateur n'est jamais acceptable.</summary>
    public static IElevatedClient CreateSansLancer() => Monter(CreerSansLancerVersLeHelper(), JournalDuCompte());

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
    /// Program Files, l'App l'ignore et passe toujours par la poignee de main ; un build de developpement lance hors
    /// installation la garde. L'App livree l'ignore partout (<see cref="LireCleDeSession"/>).</summary>
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
        => LireCleDeSession(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable);

    /// <summary>La clé de session des outils de banc, ou null. Le helper livré l'ignore (mode legacy des builds de
    /// développement seulement) ; l'App livrée aussi, où qu'elle soit installée : /DIR= ou un dossier copié la mettrait
    /// hors de Program Files, et la variable ferait prendre le client qui ne vérifie pas le serveur du tube.</summary>
    internal static byte[]? LireCleDeSession(string? processus, string programFiles, Func<string, string?> environnement)
    {
#if !DEBUG
        _ = (processus, programFiles, environnement);
        return null;
#else
        if (!CleDEnvironnementPermise(processus, programFiles))
            return null;
        var b64 = environnement(SessionKeyEnv);
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
#endif
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

    /// <summary>Sur un compte standard, le helper ne servirait pas l'App du joueur : chaque opération échoue tout de suite.</summary>
    private sealed class CompteStandardClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.CompteStandard,
                "This Windows account is not an administrator: the elevated helper would run as another account."));
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
