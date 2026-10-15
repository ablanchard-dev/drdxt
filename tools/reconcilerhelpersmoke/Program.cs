using System.Diagnostics;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// reconcilerhelpersmoke — LIVE proof of the reconciler↔helper CHANNEL.
//
// Proves the three pieces of this increment end to end, with NO pre-shared key:
//   1. SERVICE-MODE accept loop: the helper is launched ELEVATED in --service mode
//      (the long-lived, multi-connection loop) and serves connection after connection.
//   2. MULTI-CALLER auth: the helper is told to trust TWO images — a DrDxt.App path
//      (--app-path) AND a reconciler path (--reconciler-path). Here THIS smoke exe is
//      provisioned as the trusted reconciler image, so it can connect AS the reconciler.
//   3. CONNECT-ONLY reconciler client: HostElevatedClientFactory.CreateConnectOnly()
//      connects to the already-running helper service (NO runas launch) and round-trips
//      a harmless allowlisted op (Ping) over the negotiated, signed, allowlisted IPC.
//
// Each step prints PASS/FAIL; exit 0 = all PASS. This is the human-run boundary proof
// (the real `runas` spawn + UAC is the untested unit edge). Do NOT run in CI — it
// elevates. See README.md.
// -----------------------------------------------------------------------

Console.WriteLine("=== DrDXT reconcilerhelpersmoke — service-mode + multi-caller + connect-only ===\n");
PrintHowToRun();

// --- Step 0: locate the helper exe next to this tool ---
var baseDir = AppContext.BaseDirectory;
var helperExe = Path.Combine(baseDir, "DrDxt.ElevatedHelper.exe");
var thisExe = Environment.ProcessPath;

if (string.IsNullOrWhiteSpace(thisExe))
{
    Fail("Could not resolve this process path (needed as the trusted reconciler image).");
    return 3;
}
if (!File.Exists(helperExe))
{
    Fail($"Helper exe not found next to this tool: {helperExe}");
    Console.WriteLine("   Build this tool so the ProjectReference copies the helper, e.g.:");
    Console.WriteLine("     dotnet build tools/reconcilerhelpersmoke/reconcilerhelpersmoke.csproj -c Debug");
    return 3;
}
Pass($"Step 0 located helper: {helperExe}");

// A dedicated pipe for the smoke so it never collides with a real helper/App session.
var pipeName = "DrDXT.ReconSmoke." + Guid.NewGuid().ToString("N");

// We provision a placeholder App path (the helper hashes whatever exists; here we just
// reuse the helper exe as a stand-in trusted App image — its hash resolves) AND THIS
// exe as the trusted reconciler image. Only the reconciler slot needs to match us.
var appImagePath = helperExe;            // stand-in trusted "App" image (hashes fine)
var reconcilerImagePath = thisExe!;      // THIS exe = the trusted reconciler image

// --- Step 1: launch the helper ELEVATED in --service mode (interactive UAC) ---
Console.WriteLine("\n  Launching the elevated helper in SERVICE mode — APPROVE the UAC prompt...");
var launched = LaunchHelperServiceElevated(helperExe, appImagePath, reconcilerImagePath, pipeName);
if (!launched.IsOk)
{
    if (launched.Error!.Value.Code == HelperLauncher.ElevationDeclined)
        Fail("Step 1 launch: UAC was declined (ELEVATION_DECLINED). Re-run and approve the prompt.");
    else
        Fail($"Step 1 launch: {launched.Error.Value.Code} — {launched.Error.Value.Message}");
    return 1;
}
Pass("Step 1 helper launched ELEVATED in --service mode (UAC approved)");

// --- Step 2: connect AS the reconciler identity (connect-only) + Ping ---
// The service loop needs a moment to create its FirstPipeInstance pipe; the connect-only
// client's bounded connect-retry absorbs that startup latency.
var reconClient = HostElevatedClientFactory.CreateConnectOnly(pipeName, connectTimeoutMs: 10000);

var allPass = true;
var ping = await reconClient.ExecuteAsync(new PingOp(), default);
if (ping.IsOk)
    Pass("Step 2 reconciler connect-only handshake + Ping round-tripped OK (multi-caller auth allowed us)");
else
{
    Fail($"Step 2 reconciler Ping: {ping.Error!.Value.Code} — {ping.Error.Value.Message}");
    allPass = false;
}

// --- Step 3: a SECOND op proves the SERVICE LOOP serves many sequential connections ---
var ping2 = await reconClient.ExecuteAsync(new PingOp(), default);
if (ping2.IsOk)
    Pass("Step 3 second Ping round-tripped OK (service loop re-armed + served the next connection)");
else
{
    Fail($"Step 3 second Ping: {ping2.Error!.Value.Code} — {ping2.Error.Value.Message}");
    allPass = false;
}

Console.WriteLine();
Console.WriteLine(allPass ? "=== OVERALL: PASS ===" : "=== OVERALL: FAIL ===");
Console.WriteLine("(The elevated helper keeps running; end the process to stop the service loop.)");
return allPass ? 0 : 1;

// -----------------------------------------------------------------------
// Launches the helper ELEVATED in --service mode with the multi-caller paths. This is a
// SMOKE-ONLY launcher (the production launcher seam adds only --app-path/--pipe); here we
// also pass --reconciler-path and --service so the live channel can be exercised. Uses
// ShellExecute Verb=runas (UAC). A typed argument LIST (never a concatenated command
// string) → no injection surface. The session key is NEGOTIATED over the pipe.
// -----------------------------------------------------------------------
static Result LaunchHelperServiceElevated(
    string helperExe, string appImagePath, string reconcilerImagePath, string pipeName)
{
    var psi = new ProcessStartInfo
    {
        FileName = helperExe,
        UseShellExecute = true, // REQUIRED for Verb="runas" (UAC).
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = Path.GetDirectoryName(helperExe) ?? Environment.CurrentDirectory,
    };
    psi.ArgumentList.Add("--service");
    psi.ArgumentList.Add($"--app-path={appImagePath}");
    psi.ArgumentList.Add($"--reconciler-path={reconcilerImagePath}");
    psi.ArgumentList.Add($"--pipe={pipeName}");

    try
    {
        using var proc = Process.Start(psi);
        return proc is null
            ? Result.Fail(ErrorCodes.IoFailure, "Failed to start the elevated helper process.")
            : Result.Ok();
    }
    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
    {
        return Result.Fail(HelperLauncher.ElevationDeclined, "Elevation was declined by the user.");
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
    {
        return Result.Fail(ErrorCodes.IoFailure, "Could not launch the elevated helper.");
    }
}

// --- helpers ---
static void Pass(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  [PASS] " + m); Console.ForegroundColor = c; }
static void Fail(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  [FAIL] " + m); Console.ForegroundColor = c; }

static void PrintHowToRun()
{
    Console.WriteLine("How to run this LIVE proof (PowerShell):");
    Console.WriteLine("  1) Build it (copies the helper next to it):");
    Console.WriteLine("       dotnet build tools/reconcilerhelpersmoke/reconcilerhelpersmoke.csproj -c Debug");
    Console.WriteLine("  2) Run it (it raises ONE UAC prompt to launch the elevated helper service):");
    Console.WriteLine(@"       tools\reconcilerhelpersmoke\bin\Debug\net8.0-windows\reconcilerhelpersmoke.exe");
    Console.WriteLine("  NOTE: no session key is pre-shared — the key is negotiated over the pipe");
    Console.WriteLine("        AFTER the helper authenticates THIS exe as the trusted reconciler image.");
    Console.WriteLine();
}
