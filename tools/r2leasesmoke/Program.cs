using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// r2leasesmoke — LIVE proof of the LEASE WATCHDOG (dead-man's-switch).
//
// Validation "A": prove END-TO-END that a HELD GPU clock offset, applied THROUGH the
// elevated helper, is AUTO-REVERTED by the helper's background watchdog when the
// controller dies WITHOUT renewing the lease.
//
// Against the RUNNING elevated helper, over the signed + allowlisted typed IPC:
//   1. read the current core-clock offset (baseline; expect 0) for the printout
//      (read-only NvApiTuningActions.Get*, like tools/r2probe)
//   2. apply a CONSERVATIVE HELD offset of -15 MHz on GPU 0 via ApplyNvApiClockOffsetOp
//      THROUGH the helper (the helper routes it to the REAL NvApiTuningActions write)
//   3. establish a lease (EstablishLeaseOp) whose revert op = the codec-serialized
//      ApplyNvApiClockOffsetOp(gpuIndex=0, offset=0) (back to stock), leaseSeconds = 8
//   4. print what was applied + the lease id, then EXIT IMMEDIATELY — NO heartbeat,
//      NO Complete. The helper watchdog (5s monitor tick) auto-reverts to 0 within
//      ~(leaseSeconds + one tick).
//
// This process NEVER renews and NEVER completes the lease — simulating a controller
// that died. After it exits, the operator waits ~15s and runs tools/r2probe to
// confirm the offset is back to 0. Nothing here is unit-tested; this is the
// human-run boundary proof. See SMOKE.md for the exact launch/run/verify commands.
// -----------------------------------------------------------------------

const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";
const int gpu = 0;
const int heldCoreOffsetMhz = -15; // conservative underclock, well inside GpuSafeBounds
const int leaseSeconds = 8;        // short on purpose so the live wait is quick

Console.WriteLine("=== DrDXT r2leasesmoke — lease-watchdog auto-revert live proof ===\n");
PrintHowToLaunch();

// --- Build the signed-IPC client (same session key the helper was launched with) ---
var b64 = Environment.GetEnvironmentVariable(SessionKeyEnv);
if (string.IsNullOrWhiteSpace(b64))
{
    Fail($"No {SessionKeyEnv} set. Provision the SAME base64 session key for the helper and this process.");
    return 3;
}

byte[] key;
try { key = Convert.FromBase64String(b64); }
catch (FormatException) { Fail($"{SessionKeyEnv} is not valid base64."); return 3; }
if (key.Length < 32) { Fail($"{SessionKeyEnv} must decode to >= 32 bytes."); return 3; }

var transport = new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName);
IElevatedClient client = new TypedNamedPipeElevatedClient(key, transport);

// --- Step 1: read the baseline offset (read-only; no elevation needed for reads) ---
var reader = new NvApiTuningActions();
var baseline = reader.GetCoreClockOffsetMhz(gpu);
if (baseline.IsOk)
    Pass($"Step 1 baseline core-clock offset(GPU {gpu}) = {baseline.Value} MHz (expected 0)");
else
    Warn($"Step 1 could not read baseline offset: {baseline.Error?.Code} — {baseline.Error?.Message} " +
         "(continuing; the helper still applies through the IPC)");

// --- Step 2: apply a HELD offset of -15 MHz THROUGH the helper ---
var apply = await client.ExecuteAsync(new ApplyNvApiClockOffsetOp(gpu, heldCoreOffsetMhz, 0), default);
if (!apply.IsOk)
{
    Fail($"Step 2 apply ApplyNvApiClockOffsetOp(GPU {gpu}, {heldCoreOffsetMhz} MHz) THROUGH helper: " +
         $"{apply.Error!.Value.Code} — {apply.Error.Value.Message}");
    return 1;
}
Pass($"Step 2 applied HELD offset {heldCoreOffsetMhz} MHz on GPU {gpu} THROUGH the helper");

// Read back (best-effort) so the operator can see it actually moved.
var afterApply = reader.GetCoreClockOffsetMhz(gpu);
if (afterApply.IsOk)
    Console.WriteLine($"        read-back after apply = {afterApply.Value} MHz (expected {heldCoreOffsetMhz})");

// --- Step 3: establish a lease whose recorded revert restores stock (0 MHz) ---
// The revert is the SAME shape of typed op the held path would replay — serialized
// via the bounded codec, so the helper re-binds + re-validates it leaf-exact at expiry.
var revertOp = new ApplyNvApiClockOffsetOp(gpu, 0, 0);
var revertJson = PrivilegedOpCodec.Serialize(revertOp);
if (!revertJson.IsOk || revertJson.Value is null)
{
    Fail("Step 3 could not serialize the revert op (codec failure).");
    // Best-effort: try to undo the offset we just applied so we don't leave it held.
    await client.ExecuteAsync(revertOp, default);
    return 1;
}

var leaseId = $"r2leasesmoke:gpu{gpu}:{Guid.NewGuid():N}";
var establish = await client.ExecuteAsync(new EstablishLeaseOp(leaseId, revertJson.Value, leaseSeconds), default);
if (!establish.IsOk)
{
    Fail($"Step 3 EstablishLeaseOp: {establish.Error!.Value.Code} — {establish.Error.Value.Message}");
    // Best-effort: undo the held offset since the watchdog will NOT (no lease registered).
    Warn("No lease registered — undoing the held offset directly so the GPU is not left offset.");
    await client.ExecuteAsync(revertOp, default);
    return 1;
}
Pass($"Step 3 established lease id='{leaseId}' (revert→0 MHz, leaseSeconds={leaseSeconds})");

// --- Step 4: exit WITHOUT renew/complete — simulate the controller dying ---
Console.WriteLine();
Console.WriteLine($"  Applied: HELD core-clock offset {heldCoreOffsetMhz} MHz on GPU {gpu}, through the elevated helper.");
Console.WriteLine($"  Lease  : '{leaseId}', leaseSeconds={leaseSeconds}, revert op = back to 0 MHz.");
Console.WriteLine("  Exiting WITHOUT renew/complete — the helper watchdog should auto-revert to 0");
Console.WriteLine($"  within ~(leaseSeconds + monitor tick) = ~{leaseSeconds} + ~5s. Wait ~15s, then run");
Console.WriteLine("  tools/r2probe and confirm GetCoreClockOffsetMhz(0) is back to 0 MHz.");
Console.WriteLine();
Console.WriteLine("=== r2leasesmoke: lease armed, exiting now (NO heartbeat, NO Complete) ===");
return 0;

// --- helpers ---
static void Pass(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  [PASS] " + m); Console.ForegroundColor = c; }
static void Fail(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  [FAIL] " + m); Console.ForegroundColor = c; }
static void Warn(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  [WARN] " + m); Console.ForegroundColor = c; }

static void PrintHowToLaunch()
{
    Console.WriteLine("How to run this LIVE proof (PowerShell, as the SAME user) — full runbook in SMOKE.md:");
    Console.WriteLine("  1) Generate a session key shared by both processes:");
    Console.WriteLine("       $key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))");
    Console.WriteLine("       $env:DRDXT_HELPER_SESSION_KEY = $key");
    Console.WriteLine("  2) Launch the helper ELEVATED, trusting THIS smoke exe as the caller:");
    Console.WriteLine("       Start-Process -Verb RunAs DrDxt.ElevatedHelper.exe `");
    Console.WriteLine("         -ArgumentList \"--session-key=$key\",\"--app-path=<full path to r2leasesmoke.exe>\"");
    Console.WriteLine("  3) Run this smoke (same shell, same $env:DRDXT_HELPER_SESSION_KEY): r2leasesmoke.exe");
    Console.WriteLine("  4) Wait ~15s, then run tools/r2probe to confirm the offset is back to 0.");
    Console.WriteLine();
}
