using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HelperLauncher — obtains a RUNNING elevated helper ON DEMAND.
//
// What this is (and the migration path — read before changing):
//   This is the DEV/test stepping-stone for elevation. When the App needs to
//   apply a privileged tweak it asks the launcher to ensure the helper is up;
//   the launcher spawns DrDxt.ElevatedHelper.exe ELEVATED via an interactive UAC
//   prompt (ProcessStartInfo { UseShellExecute=true, Verb="runas" } — see
//   RunAsProcessLauncher). The SPEC's end-state (05-M5 §1 invariant
//   "Élévation minimale: seul l'installeur élève — 1 UAC") is a PERMANENT helper
//   registered as a Windows service at install time (one UAC, ever).
//
//   This class is deliberately structured so that migration is a SEAM SWAP, not a
//   rewrite: the launch mechanism is IHelperProcessLauncher and "is it up?" is a
//   real connect+handshake ATTEMPT (IHelperHandshakeAttempt). The installer-era
//   code registers/starts the same helper exe as a service (a different
//   IHelperProcessLauncher that does `sc start` / ServiceController.Start instead of
//   `runas`), and the same pipe + handshake negotiate the session key. The channel
//   does not change — only how the helper process gets started.
//
// FIX 1 — no separate bare-connect liveness probe (it raced the single-instance
//   pipe). Liveness/reuse is detected by a COMPLETE connect+handshake attempt (a
//   clean, fully-serviced connection — never a truncated, probe-consumed slot that
//   forces the server to re-create its FIRST_PIPE_INSTANCE pipe mid-race). The
//   post-launch startup latency is absorbed by a bounded RETRY of that same
//   attempt (connect failures retried up to a deadline). So there is exactly ONE
//   accept per attempt; a launched helper that never serves in time fails clean as
//   HELPER_UNREACHABLE rather than spinning or racing.
//
// DECISIONS:
//   * already running (a handshake attempt SUCCEEDS first try) -> REUSE, never
//     spawn a second elevated process.
//   * a live pipe that REJECTS the handshake (auth/protocol mismatch) -> Fail
//     ELEVATION_DENIED, never launch a 2nd elevated process onto it, never spin.
//   * not running (connect fails) -> launch, then retry the attempt until the
//     helper serves or the deadline is hit (HELPER_UNREACHABLE).
//   * UAC cancelled -> Result.Fail(ELEVATION_DECLINED) — clean, never a throw/crash.
//
// Fail-clean: every path returns a Result; the App treats Fail by falling back to
// the UnavailableElevatedClient (an apply fails clean rather than crashing).
// -----------------------------------------------------------------------

/// <summary>Inputs needed to start the elevated helper: the helper exe, the
/// trusted App image path (so the helper authorizes only us), and the pipe name.</summary>
public readonly record struct HelperLaunchSpec(string HelperExePath, string AppImagePath, string PipeName);

/// <summary>Seam: starts the elevated helper process. Production = interactive
/// `runas` UAC; the installer era swaps in a service-start launcher.</summary>
public interface IHelperProcessLauncher
{
    /// <summary>Starts the helper. Result.Ok if the process was launched (the host
    /// may still be initializing); ELEVATION_DECLINED if the user cancelled UAC.</summary>
    Result Launch(HelperLaunchSpec spec);
}

/// <summary>Outcome of a single COMPLETE connect+handshake attempt against the
/// helper pipe. This replaces the old bare-connect liveness probe (FIX 1).</summary>
public enum HelperAttemptOutcome
{
    /// <summary>Connected AND the handshake completed (helper is up and authorized
    /// us). The negotiated session is discarded — this is only a liveness/reuse
    /// signal; the real op runs its own handshake.</summary>
    Connected,

    /// <summary>Could not connect (pipe absent / connect timeout). The helper is not
    /// serving yet → retryable while it starts up.</summary>
    ConnectFailed,

    /// <summary>Connected but the handshake was REJECTED (caller-auth denied or a
    /// protocol error). A live but hostile/mismatched peer — TERMINAL, not retryable.</summary>
    HandshakeRejected,
}

/// <summary>Seam: performs ONE complete connect+handshake attempt against the helper
/// pipe and classifies the outcome. Replaces the bare-connect liveness probe so there
/// is no truncated, probe-consumed slot. Tests fake it; production = PipeHandshakeAttempt.</summary>
public interface IHelperHandshakeAttempt
{
    Task<HelperAttemptOutcome> TryHandshakeAsync(HelperLaunchSpec spec, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
public sealed class HelperLauncher
{
    /// <summary>The user cancelled the UAC prompt — apply is declined, not broken.</summary>
    public const string ElevationDeclined = ErrorCodes.ElevationDeclined;

    private readonly IHelperProcessLauncher _processLauncher;
    private readonly IHelperHandshakeAttempt _attempt;

    public HelperLauncher(IHelperProcessLauncher processLauncher, IHelperHandshakeAttempt attempt)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
    }

    /// <summary>Ensures an elevated helper is serving the pipe, launching it (one
    /// interactive UAC) if needed and retrying the connect+handshake until it comes
    /// up. Reuse is detected by a successful handshake (FIX 1 — no bare probe).</summary>
    public async Task<Result> EnsureRunningAsync(
        HelperLaunchSpec spec,
        CancellationToken ct,
        int postLaunchWaitMs = 8000,
        int pollIntervalMs = 150)
    {
        if (string.IsNullOrWhiteSpace(spec.HelperExePath))
            return Result.Fail(ErrorCodes.IoFailure, "Helper executable path was not provided.");

        // 1. One COMPLETE handshake attempt: is a helper already serving?
        var first = await _attempt.TryHandshakeAsync(spec, ct).ConfigureAwait(false);
        switch (first)
        {
            case HelperAttemptOutcome.Connected:
                // Already up and authorizes us → reuse, never spawn a second process.
                return Result.Ok();
            case HelperAttemptOutcome.HandshakeRejected:
                // A live pipe is up but rejected the handshake. Do not launch a second
                // elevated process onto it and do not spin — fail clean.
                return Result.Fail(ErrorCodes.ElevationDenied,
                    "An elevated helper is serving the pipe but rejected the handshake.");
            case HelperAttemptOutcome.ConnectFailed:
            default:
                break; // not running → launch below
        }

        // 2. Launch elevated (interactive UAC). A cancelled prompt comes back as a
        //    clean ELEVATION_DECLINED from the process-launcher seam.
        var launch = _processLauncher.Launch(spec);
        if (!launch.IsOk)
            return launch;

        // 3. The elevated host needs a moment to create its FirstPipeInstance pipe.
        //    Retry the same complete connect+handshake until it serves or we time out.
        //    A connect failure → keep retrying (still starting). A handshake REJECTION
        //    → terminal (the helper is up but won't authorize us — never fixed by waiting).
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

            // ConnectFailed → still coming up. Stop once we hit the deadline.
            if (Environment.TickCount64 >= deadline)
                return Result.Fail(ErrorCodes.HelperUnreachable,
                    "The elevated helper was launched but did not start serving in time.");

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}

// -----------------------------------------------------------------------
// RunAsProcessLauncher — the real interactive-UAC spawn (the untested live edge).
//
// Uses ShellExecute with Verb="runas" so Windows shows the consent dialog. The
// helper is passed --app-path (the trusted DrDxt.App image, for caller-auth) and
// --pipe; the session key is not passed here — it is NEGOTIATED over the pipe by
// the handshake (HandshakeServerCore/HandshakeClientCore), replacing the old
// env-var pre-share.
//
// Win32Exception with NativeErrorCode 1223 (ERROR_CANCELLED) == the user clicked
// "No" on UAC -> ELEVATION_DECLINED (clean). Any other failure -> IO_FAILURE.
//
// NOTE: this method is the boundary that the unit tests intentionally do not
// exercise (it shows real UI / elevates). The helperhandshake smoke tool drives
// it end-to-end live.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class RunAsProcessLauncher : IHelperProcessLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — UAC "No".
    public const string Lanceur = "DrDxt.Lanceur.exe";

    /// <summary>10/10/2026 (revue de sécurité) : l'invite Windows porte le lanceur natif quand il est à côté du helper ;
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
            UseShellExecute = true, // REQUIRED for Verb="runas" (UAC): no shell-out of a command string.
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(spec.HelperExePath) ?? Environment.CurrentDirectory,
        };
        // Pass identity/config as a typed argument LIST (never a concatenated
        // command string) — no injection surface. The session key is negotiated
        // over the pipe, not passed here.
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
// PipeHandshakeAttempt — the real connect+handshake liveness/reuse probe (FIX 1).
//
// One COMPLETE attempt: connect to the named pipe, run the §9.13 client handshake,
// then immediately drop the negotiated session (this is a liveness signal only —
// the real op runs its own handshake). Unlike the retired bare-connect probe, the
// server services this as a normal, complete connection and re-creates its
// FIRST_PIPE_INSTANCE pipe in the usual accept-loop step — no truncated, half-open
// slot that races the App's real connect.
//
// Outcome mapping:
//   * connect throws/times out  -> ConnectFailed (helper not serving yet; retry).
//   * handshake returns a key    -> Connected (up and authorizes us; reuse).
//   * handshake returns Fail     -> HandshakeRejected (live pipe, denied/garbage).
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
                // Not up yet / cannot connect → retryable.
                return HelperAttemptOutcome.ConnectFailed;
            }

            // Connected → run the COMPLETE handshake so the server services a clean
            // connection (no truncated slot). The negotiated key is then discarded.
            var handshake = await HandshakeClientCore.RunAsync(client, ct).ConfigureAwait(false);
            if (handshake.IsOk && handshake.Value is not null)
            {
                sessionKey = handshake.Value;
                return HelperAttemptOutcome.Connected;
            }

            // Connected but the helper refused us (auth denied / protocol error) — terminal.
            return HelperAttemptOutcome.HandshakeRejected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any other transport fault on an already-connected pipe → treat as a
            // rejection (a live but unusable peer), fail-closed and don't spin.
            return HelperAttemptOutcome.HandshakeRejected;
        }
        finally
        {
            if (sessionKey is not null)
                Array.Clear(sessionKey); // the liveness session dies immediately
            client?.Dispose();
        }
    }
}
