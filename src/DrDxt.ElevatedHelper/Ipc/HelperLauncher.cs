using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperLauncher : obtient à la demande un helper élevé en marche, par une invite UAC interactive (RunAsProcessLauncher,
// `runas`). L'élévation minimale visée (« seul l'installeur élève, 1 UAC ») est un helper en service Windows : un autre
// IHelperProcessLauncher (`sc start`), même pipe, même handshake. Seul le démarrage change.
//
// Pas de sonde de vie à connexion nue : elle consommerait un créneau et forcerait le serveur à recréer son pipe
// FIRST_PIPE_INSTANCE en pleine course. Vie et réutilisation se lisent par une tentative complète connexion+handshake,
// reprise jusqu'à une échéance après lancement : une acceptation par tentative.
//   * déjà en marche -> réutiliser, jamais de second processus élevé ;
//   * pipe vivant qui rejette le handshake -> ELEVATION_DENIED, ni second processus ni boucle ;
//   * pas en marche -> lancer, reprendre jusqu'au service ou à l'échéance (HELPER_UNREACHABLE) ;
//   * UAC annulée -> ELEVATION_DECLINED, jamais une exception.
// Chaque chemin rend un Result ; sur un échec, l'App se rabat sur UnavailableElevatedClient.
// -----------------------------------------------------------------------

/// <summary>L'exe du helper, l'image de l'App de confiance (le helper n'autorise qu'elle) et le nom du pipe.</summary>
public readonly record struct HelperLaunchSpec(string HelperExePath, string AppImagePath, string PipeName);

/// <summary>Couture : démarre le processus du helper élevé (UAC `runas`, ou démarrage de service).</summary>
public interface IHelperProcessLauncher
{
    /// <summary>Ok si le processus est lancé (il peut encore s'initialiser) ; ELEVATION_DECLINED si l'UAC est
    /// annulée.</summary>
    Result Launch(HelperLaunchSpec spec);
}

/// <summary>Issue d'une tentative complète connexion+handshake sur le pipe du helper.</summary>
public enum HelperAttemptOutcome
{
    /// <summary>Connecté et autorisé. La session négociée est jetée : la vraie op fait son propre handshake.</summary>
    Connected,

    /// <summary>Connexion impossible (pipe absent, délai dépassé) : reprise possible pendant le démarrage.</summary>
    ConnectFailed,

    /// <summary>Connecté mais handshake rejeté (auth ou protocole) : pair hostile ou incompatible, définitif.</summary>
    HandshakeRejected,
}

/// <summary>Couture : une tentative complète connexion+handshake, classée. En production : PipeHandshakeAttempt.</summary>
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

    /// <summary>S'assure qu'un helper élevé sert le pipe : le lance au besoin (une UAC) et reprend connexion+handshake
    /// jusqu'à ce qu'il réponde.</summary>
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
                // Un pipe vivant a rejeté le handshake : ni second processus élevé, ni boucle.
                return Result.Fail(ErrorCodes.ElevationDenied,
                    "An elevated helper is serving the pipe but rejected the handshake.");
            case HelperAttemptOutcome.ConnectFailed:
            default:
                break; // pas en marche → lancement plus bas
        }

        // 2. Lancer en élevé ; une invite annulée revient en ELEVATION_DECLINED.
        var launch = _processLauncher.Launch(spec);
        if (!launch.IsOk)
            return launch;

        // 3. Le temps que l'hôte crée son pipe : échec de connexion → reprendre jusqu'à l'échéance ; handshake rejeté →
        //    définitif.
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
// RunAsProcessLauncher : le vrai lancement par UAC (ShellExecute, Verb="runas"). Le helper reçoit --app-path (l'image
// DrDxt.App de confiance, pour l'auth de l'appelant) et --pipe ; la clé de session est négociée sur le pipe
// (HandshakeServerCore/HandshakeClientCore), jamais passée par l'environnement. Win32Exception 1223 (ERROR_CANCELLED,
// « Non » à l'UAC) -> ELEVATION_DECLINED ; tout autre échec -> IO_FAILURE. Hors tests unitaires (vraie UAC) : le smoke
// test helperhandshake la pilote en réel.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class RunAsProcessLauncher : IHelperProcessLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED : « Non » à l'UAC.
    public const string Lanceur = "DrDxt.Lanceur.exe";

    /// <summary>L'invite porte le lanceur natif quand il est à côté du helper : il redémarre le helper avec un
    /// environnement reconstruit (un processus élevé hérite de HKCU\Environment, où tout programme du compte peut poser
    /// CORECLR_PROFILER_PATH). Sans lanceur (build de développement) : le helper lui-même.</summary>
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
        // Liste d'arguments typée, jamais une chaîne concaténée : aucune surface d'injection.
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
// PipeHandshakeAttempt : connexion, handshake client, session jetée aussitôt. Le serveur la sert comme une connexion
// normale et recrée son pipe FIRST_PIPE_INSTANCE à l'étape habituelle : aucun créneau à moitié ouvert.
//   * la connexion lève ou expire  -> ConnectFailed (reprendre) ;
//   * le handshake rend une clé     -> Connected (réutiliser) ;
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

            // Handshake complet, pour une connexion servie jusqu'au bout ; la clé est jetée.
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
            // Autre panne de transport sur un pipe connecté : rejet (fail-closed), sans boucle.
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
