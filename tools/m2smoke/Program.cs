using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// m2smoke — LIVE proof of the SAFE apply->revert pipeline.
//
// Against the RUNNING elevated helper, over the signed + allowlisted typed IPC:
//   1. read the current active power plan (Win32PowerPlanReader)
//   2. EnsureRestorePoint  -> CreateRestorePointOp (through the helper)
//   3. apply SetActivePowerPlanOp(High Performance)
//   4. read back & assert the active plan CHANGED
//   5. revert to the ORIGINAL plan (SetActivePowerPlanOp)
//   6. read back & assert the active plan is RESTORED
//
// Each step prints PASS/FAIL. Exit code 0 = all PASS. Nothing here is unit-tested
// — this is the human-run boundary proof. See README.md for launch instructions.
// -----------------------------------------------------------------------

const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";

Console.WriteLine("=== DrDXT m2smoke — SAFE apply->revert live proof ===\n");

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
var powerReader = new Win32PowerPlanReader();

var allPass = true;

// --- Step 1: read current plan ---
var originalRead = powerReader.GetActivePowerPlan();
if (!originalRead.IsOk)
{
    Fail($"Step 1 read active power plan: {originalRead.Error!.Value.Code} — {originalRead.Error.Value.Message}");
    return 1;
}
var original = originalRead.Value;
Pass($"Step 1 read active power plan = {Describe(original)}");

// If we're already on High Performance, target Balanced instead so we can prove a change.
var target = original == KnownPowerPlans.HighPerformance
    ? KnownPowerPlans.Balanced
    : KnownPowerPlans.HighPerformance;

// --- Step 2: EnsureRestorePoint via CreateRestorePointOp ---
var rp = await client.ExecuteAsync(new CreateRestorePointOp("DrDXT m2smoke — before power-plan apply"), default);
if (rp.IsOk)
    Pass("Step 2 CreateRestorePointOp (restore point created)");
else
{
    // A restore-point failure is reported but does not abort the live proof of the
    // apply/revert wiring (System Restore may be off on a test box). Flag it.
    Warn($"Step 2 CreateRestorePointOp: {rp.Error!.Value.Code} — {rp.Error.Value.Message} (continuing; check System Restore is ON)");
}

// --- Step 3: apply SetActivePowerPlanOp(target) ---
var apply = await client.ExecuteAsync(new SetActivePowerPlanOp(target), default);
if (!apply.IsOk)
{
    Fail($"Step 3 apply SetActivePowerPlanOp({Describe(target)}): {apply.Error!.Value.Code} — {apply.Error.Value.Message}");
    return 1;
}
Pass($"Step 3 apply SetActivePowerPlanOp({Describe(target)})");

// --- Step 4: read back & assert changed ---
var afterApply = powerReader.GetActivePowerPlan();
if (afterApply.IsOk && afterApply.Value == target)
    Pass($"Step 4 active plan changed to {Describe(target)}");
else
{
    Fail($"Step 4 expected {Describe(target)} but read {(afterApply.IsOk ? Describe(afterApply.Value) : afterApply.Error!.Value.Code)}");
    allPass = false;
}

// --- Step 5: revert to original ---
var revert = await client.ExecuteAsync(new SetActivePowerPlanOp(original), default);
if (revert.IsOk)
    Pass($"Step 5 revert SetActivePowerPlanOp({Describe(original)})");
else
{
    Fail($"Step 5 revert: {revert.Error!.Value.Code} — {revert.Error.Value.Message}");
    allPass = false;
}

// --- Step 6: read back & assert restored ---
var afterRevert = powerReader.GetActivePowerPlan();
if (afterRevert.IsOk && afterRevert.Value == original)
    Pass($"Step 6 active plan restored to {Describe(original)}");
else
{
    Fail($"Step 6 expected {Describe(original)} but read {(afterRevert.IsOk ? Describe(afterRevert.Value) : afterRevert.Error!.Value.Code)}");
    allPass = false;
}

// --- HAGS registry phase: first LIVE proof of an allowlisted HKLM registry
//     apply->revert through the elevated IPC. We assert the VALUE write+revert only
//     (net-zero); HAGS's actual effect needs a reboot, which is NOT required to prove
//     the apply mechanism, so this leaves the machine exactly as it found it. ---
const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
const string HwSchModeName = "HwSchMode";
var registry = new Win32RegistryReader();

var hagsRead = registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
if (!hagsRead.IsOk)
{
    Fail($"Step 7 read HwSchMode: {hagsRead.Error!.Value.Code} — {hagsRead.Error.Value.Message}");
    allPass = false;
}
else
{
    var hagsOriginal = hagsRead.Value; // uint? — null = absent
    Pass($"Step 7 read HwSchMode = {(hagsOriginal?.ToString() ?? "absent")}");

    // Prove a real change without committing HAGS: if On (2), target Off (1); else On (2).
    var hagsTarget = hagsOriginal == 2u ? 1u : 2u;

    var hagsApply = await client.ExecuteAsync(new SetRegistryValueOp(
        RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName,
        RegistryValueType.DWord, hagsTarget.ToString()), default);
    if (!hagsApply.IsOk)
    {
        Fail($"Step 8 apply HwSchMode={hagsTarget}: {hagsApply.Error!.Value.Code} — {hagsApply.Error.Value.Message}");
        allPass = false;
    }
    else
    {
        Pass($"Step 8 apply SetRegistryValueOp(HwSchMode={hagsTarget}) via the elevated helper");

        var hagsAfter = registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
        if (hagsAfter.IsOk && hagsAfter.Value == hagsTarget)
            Pass($"Step 9 HwSchMode changed to {hagsTarget}");
        else
        {
            Fail($"Step 9 expected {hagsTarget} but read {(hagsAfter.IsOk ? (hagsAfter.Value?.ToString() ?? "absent") : hagsAfter.Error!.Value.Code.ToString())}");
            allPass = false;
        }

        // Revert to the captured original: restore the value, or delete it if it was absent.
        var hagsRevert = hagsOriginal is uint o
            ? await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName, RegistryValueType.DWord, o.ToString()), default)
            : await client.ExecuteAsync(new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName), default);
        if (hagsRevert.IsOk)
            Pass("Step 10 revert HwSchMode to original");
        else
        {
            Fail($"Step 10 revert HwSchMode: {hagsRevert.Error!.Value.Code} — {hagsRevert.Error.Value.Message}");
            allPass = false;
        }

        var hagsRestored = registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
        if (hagsRestored.IsOk && hagsRestored.Value == hagsOriginal)
            Pass($"Step 11 HwSchMode restored to {(hagsOriginal?.ToString() ?? "absent")} (machine left as found)");
        else
        {
            Fail($"Step 11 expected {(hagsOriginal?.ToString() ?? "absent")} but read {(hagsRestored.IsOk ? (hagsRestored.Value?.ToString() ?? "absent") : hagsRestored.Error!.Value.Code.ToString())}");
            allPass = false;
        }
    }
}

// --- DRS phase: first LIVE proof of an ApplyNvApiDrsSettingOp through the elevated
//     IPC — the EXACT path PreferMaxPerf / VrrVsyncCap use. Read the current PREFERRED_PSTATE
//     via the native NVAPI boundary, apply PREFER_MAX(1) THROUGH THE HELPER (allowlisted typed
//     op), read back, then revert to the captured original — machine left as found. On a
//     non-NVIDIA box the whole phase is skipped (nvapi unavailable): reported, never failed. ---
const uint PreferredPstateId = 0x1057EB71; // PREFERRED_PSTATE_ID (verified, allowlisted)
const uint PreferMax = 1;                   // PREFERRED_PSTATE_PREFER_MAX
const string BaseProfile = "Base Profile";
var nvapi = new DrDxt.GpuNative.NvApi.NvApiNative();

if (!nvapi.IsAvailable)
{
    Warn("Step 12 NVAPI unavailable (no NVIDIA GPU?) — DRS phase skipped");
}
else if (!nvapi.TryGetDrsGlobalSetting(PreferredPstateId, out var pstateOriginal))
{
    Warn("Step 12 could not read PREFERRED_PSTATE — DRS phase skipped");
}
else
{
    Pass($"Step 12 read PREFERRED_PSTATE = {pstateOriginal}");

    var drsApply = await client.ExecuteAsync(new ApplyNvApiDrsSettingOp(PreferredPstateId, PreferMax, BaseProfile), default);
    if (!drsApply.IsOk)
    {
        Fail($"Step 13 apply ApplyNvApiDrsSettingOp(PREFER_MAX): {drsApply.Error!.Value.Code} — {drsApply.Error.Value.Message}");
        allPass = false;
    }
    else
    {
        Pass("Step 13 apply ApplyNvApiDrsSettingOp(PREFERRED_PSTATE=PREFER_MAX) via the elevated helper");

        nvapi.TryGetDrsGlobalSetting(PreferredPstateId, out var pstateAfter);
        if (pstateAfter == PreferMax)
            Pass($"Step 14 PREFERRED_PSTATE changed to {pstateAfter}");
        else { Fail($"Step 14 expected 1 but read {pstateAfter}"); allPass = false; }

        var drsRevert = await client.ExecuteAsync(new ApplyNvApiDrsSettingOp(PreferredPstateId, pstateOriginal, BaseProfile), default);
        if (drsRevert.IsOk)
            Pass($"Step 15 revert PREFERRED_PSTATE to {pstateOriginal} via the elevated helper");
        else { Fail($"Step 15 revert: {drsRevert.Error!.Value.Code} — {drsRevert.Error.Value.Message}"); allPass = false; }

        nvapi.TryGetDrsGlobalSetting(PreferredPstateId, out var pstateRestored);
        if (pstateRestored == pstateOriginal)
            Pass($"Step 16 PREFERRED_PSTATE restored to {pstateOriginal} (machine left as found)");
        else { Fail($"Step 16 expected {pstateOriginal} but read {pstateRestored}"); allPass = false; }
    }
}

Console.WriteLine();
Console.WriteLine(allPass ? "=== OVERALL: PASS ===" : "=== OVERALL: FAIL ===");
return allPass ? 0 : 1;

// --- helpers ---
static void Pass(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  [PASS] " + m); Console.ForegroundColor = c; }
static void Fail(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  [FAIL] " + m); Console.ForegroundColor = c; }
static void Warn(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  [WARN] " + m); Console.ForegroundColor = c; }

static string Describe(Guid g) =>
    g == KnownPowerPlans.HighPerformance ? "High Performance"
    : g == KnownPowerPlans.Balanced ? "Balanced"
    : g == KnownPowerPlans.Ultimate ? "Ultimate"
    : g.ToString();

static void PrintHowToLaunch()
{
    Console.WriteLine("How to run this LIVE proof (PowerShell, as the SAME user):");
    Console.WriteLine("  1) Generate a session key shared by both processes:");
    Console.WriteLine("       $key = [Convert]::ToBase64String((1..32 | %% { Get-Random -Max 256 }))");
    Console.WriteLine("       $env:DRDXT_HELPER_SESSION_KEY = $key");
    Console.WriteLine("  2) Launch the helper ELEVATED, trusting THIS smoke exe as the caller:");
    Console.WriteLine("       Start-Process -Verb RunAs DrDxt.ElevatedHelper.exe \\");
    Console.WriteLine("         -ArgumentList \"--session-key=$key\",\"--app-path=<full path to m2smoke.exe>\"");
    Console.WriteLine("     (the helper inherits the env key too; --app-path must be THIS exe so the");
    Console.WriteLine("      caller-identity check authorizes it)");
    Console.WriteLine("  3) Run this smoke (same shell, same $env:DRDXT_HELPER_SESSION_KEY): m2smoke.exe");
    Console.WriteLine();
}
