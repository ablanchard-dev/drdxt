using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperLauncher : obtient à la demande un helper élevé en marche.
//
// Ce que c'est (et la voie de migration, à lire avant de modifier) :
//   C'est la marche de dev/test pour l'élévation. Quand l'App doit appliquer un levier
//   privilégié, elle demande au lanceur de s'assurer que le helper tourne ; le lanceur
//   démarre DrDxt.ElevatedHelper.exe en élevé via une invite UAC interactive
//   (ProcessStartInfo { UseShellExecute=true, Verb="runas" }, voir RunAsProcessLauncher).
//   L'état final visé (invariant « Élévation minimale : seul l'installeur élève, 1 UAC »)
//   est un helper permanent, enregistré comme service Windows à l'installation (une seule
//   UAC, pour toujours).
//
//   La classe est construite exprès pour que la migration soit un échange de couture, pas
//   une réécriture : le mécanisme de lancement est IHelperProcessLauncher, et « tourne-t-il ? »
//   est une vraie tentative connexion+handshake (IHelperHandshakeAttempt). Le code de l'ère
//   installeur enregistre et démarre le même exe comme service (un autre IHelperProcessLauncher
//   qui fait `sc start` / ServiceController.Start au lieu de `runas`), et le même pipe + handshake
//   négocient la clé de session. Le canal ne change pas : seul le démarrage du processus change.
//
// Pas de sonde de vie à connexion nue séparée (elle entrerait en course avec le pipe à instance
//   unique). La vie et la réutilisation se détectent par une tentative complète connexion+handshake
//   (une connexion propre et servie jusqu'au bout, jamais un créneau tronqué, consommé par une sonde,
//   qui forcerait le serveur à recréer son pipe FIRST_PIPE_INSTANCE en pleine course). La latence de
//   démarrage après lancement est absorbée par une reprise bornée de cette même tentative (échecs
//   de connexion repris jusqu'à une échéance). Il y a donc exactement une acceptation par tentative ;
//   un helper lancé qui ne sert pas à temps échoue proprement en HELPER_UNREACHABLE, sans tourner
//   en boucle ni entrer en course.
//
// Décisions :
//   * déjà en marche (une tentative de handshake réussit du premier coup) -> réutiliser, jamais
//     de second processus élevé.
//   * un pipe vivant qui rejette le handshake (désaccord d'auth ou de protocole) -> échec
//     ELEVATION_DENIED, jamais de 2e processus élevé lancé dessus, jamais de boucle.
//   * pas en marche (la connexion échoue) -> lancer, puis reprendre la tentative jusqu'à ce que
//     le helper serve ou que l'échéance tombe (HELPER_UNREACHABLE).
//   * UAC annulée -> Result.Fail(ELEVATION_DECLINED) : propre, jamais une exception ni un crash.
//
// Échec propre : chaque chemin rend un Result ; sur un échec, l'App se rabat sur
// UnavailableElevatedClient (une application échoue proprement au lieu de planter).
// -----------------------------------------------------------------------

/// <summary>Les entrées pour démarrer le helper élevé : l'exe du helper, le chemin de
/// l'image de l'App de confiance (le helper n'autorise qu'elle) et le nom du pipe.</summary>
public readonly record struct HelperLaunchSpec(string HelperExePath, string AppImagePath, string PipeName);

/// <summary>Couture : démarre le processus du helper élevé. En production : UAC `runas`
/// interactive ; l'ère installeur y met un lanceur par démarrage de service.</summary>
public interface IHelperProcessLauncher
{
    /// <summary>Démarre le helper. Result.Ok si le processus est lancé (l'hôte peut encore
    /// s'initialiser) ; ELEVATION_DECLINED si l'utilisateur a annulé l'UAC.</summary>
    Result Launch(HelperLaunchSpec spec);
}

/// <summary>Issue d'une seule tentative complète connexion+handshake sur le pipe du
/// helper. Remplace la sonde de vie à connexion nue.</summary>
public enum HelperAttemptOutcome
{
    /// <summary>Connecté et handshake terminé (le helper tourne et nous a autorisés).
    /// La session négociée est jetée : ce n'est qu'un signal de vie et de réutilisation ;
    /// la vraie op fait son propre handshake.</summary>
    Connected,

    /// <summary>Connexion impossible (pipe absent, délai de connexion dépassé). Le helper ne
    /// sert pas encore → on peut reprendre pendant son démarrage.</summary>
    ConnectFailed,

    /// <summary>Connecté mais handshake rejeté (auth de l'appelant refusée ou erreur de
    /// protocole). Un pair vivant mais hostile ou incompatible : définitif, pas de reprise.</summary>
    HandshakeRejected,
}

/// <summary>Couture : fait une seule tentative complète connexion+handshake sur le pipe
/// du helper et classe l'issue. Remplace la sonde à connexion nue : aucun créneau tronqué,
/// consommé par une sonde. Les tests la simulent ; en production : PipeHandshakeAttempt.</summary>
public interface IHelperHandshakeAttempt
{
    Task<HelperAttemptOutcome> TryHandshakeAsync(HelperLaunchSpec spec, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
public sealed class HelperLauncher
{
    /// <summary>L'utilisateur a annulé l'invite UAC : l'application est refusée, pas cassée.</summary>
    public const string ElevationDeclined = ErrorCodes.ElevationDeclined;

    private readonly IHelperProcessLauncher _processLauncher;
    private readonly IHelperHandshakeAttempt _attempt;

    public HelperLauncher(IHelperProcessLauncher processLauncher, IHelperHandshakeAttempt attempt)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
    }

    /// <summary>S'assure qu'un helper élevé sert le pipe : le lance au besoin (une UAC
    /// interactive) et reprend connexion+handshake jusqu'à ce qu'il réponde. La réutilisation
    /// se détecte par un handshake réussi (pas de sonde nue).</summary>
    public async Task<Result> EnsureRunningAsync(
        HelperLaunchSpec spec,
        CancellationToken ct,
        int postLaunchWaitMs = 8000,
        int pollIntervalMs = 150)
    {
        if (string.IsNullOrWhiteSpace(spec.HelperExePath))
            return Result.Fail(ErrorCodes.IoFailure, "Helper executable path was not provided.");

        // 1. Une tentative complète de handshake : un helper sert-il déjà ?
        var first = await _attempt.TryHandshakeAsync(spec, ct).ConfigureAwait(false);
        switch (first)
        {
            case HelperAttemptOutcome.Connected:
                // Déjà en marche et nous autorise → réutiliser, jamais de second processus.
                return Result.Ok();
            case HelperAttemptOutcome.HandshakeRejected:
                // Un pipe vivant a rejeté le handshake. Ne pas lancer de second processus élevé
                // dessus et ne pas boucler : échec propre.
                return Result.Fail(ErrorCodes.ElevationDenied,
                    "An elevated helper is serving the pipe but rejected the handshake.");
            case HelperAttemptOutcome.ConnectFailed:
            default:
                break; // pas en marche → lancement plus bas
        }

        // 2. Lancer en élevé (UAC interactive). Une invite annulée revient de la couture
        //    du lanceur en ELEVATION_DECLINED propre.
        var launch = _processLauncher.Launch(spec);
        if (!launch.IsOk)
            return launch;

        // 3. L'hôte élevé met un instant à créer son pipe FirstPipeInstance.
        //    Reprendre la même tentative complète connexion+handshake jusqu'à ce qu'il serve ou
        //    jusqu'à l'échéance. Échec de connexion → reprendre (il démarre encore). Handshake
        //    rejeté → définitif (le helper tourne mais ne nous autorise pas : attendre n'y change rien).
        var deadline = Environment.TickCount64 + Math.Max(0, postLaunchWaitMs);
        var interval = Math.Max(10, pollIntervalMs);
        while (true)
        {
            var outcome = await _attempt.TryHandshakeAsync(spec, ct).ConfigureAwait(false);
            if (outcome == HelperAttemptOutcome.Connected)
                return Result.Ok();
            if (outcome == HelperAttemptOutcome.HandshakeRejected)
                return Result.Fail(ErrorCodes.ElevationDenied,
                    "The launched elevated helper rejected the handshake.");

            // ConnectFailed → démarre encore. Arrêter à l'échéance.
            if (Environment.TickCount64 >= deadline)
                return Result.Fail(ErrorCodes.HelperUnreachable,
                    "The elevated helper was launched but did not start serving in time.");

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}

// -----------------------------------------------------------------------
// RunAsProcessLauncher : le vrai lancement par UAC interactive (le bord réel non testé).
//
// Utilise ShellExecute avec Verb="runas" pour que Windows affiche la boîte de consentement.
// Le helper reçoit --app-path (l'image DrDxt.App de confiance, pour l'auth de l'appelant) et
// --pipe ; la clé de session n'est pas passée ici : elle est négociée sur le pipe par le
// handshake (HandshakeServerCore/HandshakeClientCore), sans partage préalable par variable
// d'environnement.
//
// Win32Exception avec NativeErrorCode 1223 (ERROR_CANCELLED) == l'utilisateur a cliqué « Non »
// sur l'UAC -> ELEVATION_DECLINED (propre). Tout autre échec -> IO_FAILURE.
//
// Note : cette méthode est la frontière que les tests unitaires n'exercent pas exprès (elle
// affiche une vraie interface et élève). L'outil de smoke test helperhandshake la pilote de
// bout en bout, en réel.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class RunAsProcessLauncher : IHelperProcessLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED : « Non » à l'UAC.
    public const string Lanceur = "DrDxt.Lanceur.exe";

    /// <summary>L'invite Windows porte le lanceur natif quand il est à côté du helper ;
    /// il redémarre le helper avec un environnement reconstruit (un processus élevé hérite de HKCU\Environment, où tout
    /// programme du compte peut poser CORECLR_PROFILER_PATH). Sans lanceur (build de développement) : le helper lui-même.</summary>
    internal static (string Fichier, IReadOnlyList<string> Prefixe) Cible(string helperExePath)
    {
        var lanceur = Path.Combine(Path.GetDirectoryName(helperExePath) ?? "", Lanceur);
        return File.Exists(lanceur) ? (lanceur, [Path.GetFileName(helperExePath)]) : (helperExePath, []);
    }

    public Result Launch(HelperLaunchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.HelperExePath) || !File.Exists(spec.HelperExePath))
            return Result.Fail(ErrorCodes.IoFailure, "Elevated helper executable was not found.");

        var (fichier, prefixe) = Cible(spec.HelperExePath);
        var psi = new ProcessStartInfo
        {
            FileName = fichier,
            UseShellExecute = true, // Obligatoire pour Verb="runas" (UAC) : aucune chaîne de commande passée au shell.
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(spec.HelperExePath) ?? Environment.CurrentDirectory,
        };
        // Identité et config passent en liste d'arguments typée (jamais une chaîne de commande
        // concaténée) : aucune surface d'injection. La clé de session est négociée sur le pipe,
        // pas passée ici.
        foreach (var a in prefixe) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add($"--app-path={spec.AppImagePath}");
        if (!string.IsNullOrWhiteSpace(spec.PipeName))
            psi.ArgumentList.Add($"--pipe={spec.PipeName}");

        try
        {
            using var proc = Process.Start(psi);
            return proc is null
                ? Result.Fail(ErrorCodes.IoFailure, "Failed to start the elevated helper process.")
                : Result.Ok();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Result.Fail(HelperLauncher.ElevationDeclined, "Elevation was declined by the user.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Result.Fail(ErrorCodes.IoFailure, "Could not launch the elevated helper.");
        }
    }
}

// -----------------------------------------------------------------------
// PipeHandshakeAttempt : la vraie sonde de vie et de réutilisation par connexion+handshake.
//
// Une tentative complète : se connecter au pipe nommé, faire le handshake client, puis jeter
// aussitôt la session négociée (simple signal de vie : la vraie op fait son propre handshake).
// Contrairement à une sonde à connexion nue, le serveur la sert comme une connexion normale et
// complète, et recrée son pipe FIRST_PIPE_INSTANCE à l'étape habituelle de la boucle d'acceptation :
// aucun créneau tronqué, à moitié ouvert, en course avec la vraie connexion de l'App.
//
// Correspondance des issues :
//   * la connexion lève ou expire  -> ConnectFailed (le helper ne sert pas encore ; reprendre).
//   * le handshake rend une clé     -> Connected (tourne et nous autorise ; réutiliser).
//   * le handshake rend Fail        -> HandshakeRejected (pipe vivant, refus ou déchet).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class PipeHandshakeAttempt : IHelperHandshakeAttempt
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    public PipeHandshakeAttempt(string? pipeName = null, int connectTimeoutMs = 250)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public async Task<HelperAttemptOutcome> TryHandshakeAsync(HelperLaunchSpec spec, CancellationToken ct)
    {
        NamedPipeClientStream? client = null;
        byte[]? sessionKey = null;
        try
        {
            client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(_connectTimeoutMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Pas encore en marche, connexion impossible → reprise possible.
                return HelperAttemptOutcome.ConnectFailed;
            }

            // Connecté → faire le handshake complet pour que le serveur serve une connexion
            // propre (pas de créneau tronqué). La clé négociée est ensuite jetée.
            var handshake = await HandshakeClientCore.RunAsync(client, ct).ConfigureAwait(false);
            if (handshake.IsOk && handshake.Value is not null)
            {
                sessionKey = handshake.Value;
                return HelperAttemptOutcome.Connected;
            }

            // Connecté mais le helper nous a refusés (auth refusée, erreur de protocole) : définitif.
            return HelperAttemptOutcome.HandshakeRejected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Toute autre panne de transport sur un pipe déjà connecté → traitée comme un rejet
            // (pair vivant mais inutilisable) : refus au moindre doute (fail-closed), sans boucle.
            return HelperAttemptOutcome.HandshakeRejected;
        }
        finally
        {
            if (sessionKey is not null)
                Array.Clear(sessionKey); // la session de vie meurt aussitôt
            client?.Dispose();
        }
    }
}
