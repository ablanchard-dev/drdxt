using DrDxt.ElevatedHelper.Execution.NvApi;

// R2 WRITE smoke — REQUIRES ELEVATION (SetPstates20 / SetStatus need admin).
//
// Conservative, SELF-REVERTING proof that the real NVAPI WRITES apply on this GPU:
// for each lever it reads the original, applies a small safe change, reads back to
// confirm it took, then restores the captured original and reads back to confirm
// restoration. It exercises NvApiTuningActions DIRECTLY (no App layer / no IPC) so a
// failure is unambiguous. Test values are well inside the hard rails AND GpuSafeBounds:
//   core  = -15 MHz  (a tiny underclock: safe, observable, reverts to original)
//   power = 90 %     (a mild power cap: safe, reverts to original)
// If anything looks wrong it still attempts the revert and reports a warning.

const int gpu = 0;
const int coreTestOffsetMhz = -15;
const int powerTestPercent = 90;

var t = new NvApiTuningActions();
var clean = true;

Console.WriteLine("== R2 WRITE smoke (ELEVATED; conservative, self-reverting) ==");

// ---- core-clock offset ----
var core0 = t.GetCoreClockOffsetMhz(gpu);
if (!core0.IsOk)
{
    clean = false;
    Console.WriteLine($"[core] FAILED to read original offset: {core0.Error?.Code}: {core0.Error?.Message}");
}
else
{
    Console.WriteLine($"[core] original offset = {core0.Value} MHz");
    var set = t.SetCoreClockOffset(gpu, coreTestOffsetMhz);
    if (!set.IsOk)
    {
        clean = false;
        Console.WriteLine($"[core] SET FAILED: {set.Error?.Code}: {set.Error?.Message}");
    }
    else
    {
        var rb = t.GetCoreClockOffsetMhz(gpu);
        Console.WriteLine($"[core] after set  = {(rb.IsOk ? rb.Value + " MHz" : "READ FAILED")} (expected {coreTestOffsetMhz})");
        if (!rb.IsOk || rb.Value != coreTestOffsetMhz)
            { clean = false; Console.WriteLine("[core] WARN: read-back did not match the applied value"); }
    }

    // Always attempt to restore the captured original.
    var rev = t.SetCoreClockOffset(gpu, core0.Value);
    var rb2 = t.GetCoreClockOffsetMhz(gpu);
    Console.WriteLine($"[core] reverted   = {(rb2.IsOk ? rb2.Value + " MHz" : "READ FAILED")} (expected {core0.Value}); revert {(rev.IsOk ? "OK" : "FAILED: " + rev.Error?.Message)}");
    if (!rev.IsOk || !rb2.IsOk || rb2.Value != core0.Value)
        { clean = false; Console.WriteLine("[core] WARN: NOT restored to original — check GPU state"); }
}

// ---- power limit ----
var pow0 = t.GetPowerLimitPercent(gpu);
if (!pow0.IsOk)
{
    clean = false;
    Console.WriteLine($"[power] FAILED to read original limit: {pow0.Error?.Code}: {pow0.Error?.Message}");
}
else
{
    Console.WriteLine($"[power] original limit = {pow0.Value} %");
    var set = t.SetPowerLimitPercent(gpu, powerTestPercent);
    if (!set.IsOk)
    {
        clean = false;
        Console.WriteLine($"[power] SET FAILED: {set.Error?.Code}: {set.Error?.Message}");
    }
    else
    {
        var rb = t.GetPowerLimitPercent(gpu);
        Console.WriteLine($"[power] after set   = {(rb.IsOk ? rb.Value + " %" : "READ FAILED")} (expected ~{powerTestPercent}, driver may clamp)");
    }

    var rev = t.SetPowerLimitPercent(gpu, pow0.Value);
    var rb2 = t.GetPowerLimitPercent(gpu);
    Console.WriteLine($"[power] reverted    = {(rb2.IsOk ? rb2.Value + " %" : "READ FAILED")} (expected {pow0.Value}); revert {(rev.IsOk ? "OK" : "FAILED: " + rev.Error?.Message)}");
    if (!rev.IsOk || !rb2.IsOk || rb2.Value != pow0.Value)
        { clean = false; Console.WriteLine("[power] WARN: NOT restored to original — check GPU state"); }
}

Console.WriteLine(clean
    ? "RESULT: WRITE smoke PASSED — writes applied and reverted cleanly."
    : "RESULT: WRITE smoke had warnings/failures — review above (the smoke always tries to revert).");
